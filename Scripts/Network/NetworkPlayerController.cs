using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using Mirror;
using RPG.Character;
using RPG.UI;
using RPG.Combat;

namespace RPG.Network
{
    /// <summary>
    /// Input do jogador local: mouse para mover/atacar/selecionar, teclado para
    /// skills, câmera orbital com anti-oclusão.
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 2 — polish) ===
    ///
    ///   1. UIInputUtils CENTRALIZADO:
    ///      IsTypingInField removido — agora usa UIInputUtils.IsTypingInInputField
    ///      compartilhado com InventoryUI e outros sistemas. Sem duplicação.
    ///
    ///   2. OVER-UI CHECK COM HELPER:
    ///      Substituído o boilerplate de EventSystem por UIInputUtils.IsPointerOverUI.
    ///
    ///   3. CACHE DE CÂMERA RESILIENTE:
    ///      _cam agora é re-resolvido se for null em qualquer ponto (caso a câmera
    ///      principal seja destruída em respawn ou trocada por script externo).
    ///
    ///   4. CmdMoveTo COM SANITY EXTRA:
    ///      Adiciona checagem de NaN/Infinity em destination — proteção contra
    ///      clientes malformados que enviem coordenadas inválidas.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class NetworkPlayerController : NetworkBehaviour
    {
        [Header("Layers")]
        [SerializeField] private LayerMask terrainLayer;
        [SerializeField] private LayerMask targetableLayer;
        [SerializeField] private LayerMask itemLayer;
        [Tooltip("Layers que bloqueiam a câmera. Normalmente o mesmo do terrain.")]
        [SerializeField] private LayerMask cameraOcclusionLayer;

        [Header("Câmera")]
        [SerializeField] private float orbitSensitivity = 3f;
        [SerializeField] private float zoomSensitivity  = 5f;
        [SerializeField] private float cameraSmoothTime = 0.05f;
        [SerializeField] private float cameraHeight     = 1.5f;

        [Header("Movimento (Click & Hold)")]
        [Tooltip("Intervalo entre atualizações de destino enquanto o mouse está pressionado.")]
        [SerializeField] private float updateTargetInterval = 0.12f;
        [Tooltip("Intervalo entre envios de CmdMoveTo para o servidor.")]
        [SerializeField] private float cmdMoveInterval = 0.18f;
        [Tooltip("Distância mínima entre o destino atual e o novo para emitir SetDestination.")]
        [SerializeField] private float redirectThreshold = 0.4f;

        [Header("Indicador de Movimento")]
        [SerializeField] private GameObject moveIndicatorPrefab;

        [Header("Debug")]
        [SerializeField] private bool debugMovement = false;

        // ── Componentes ────────────────────────────────────────────────────
        private NavMeshAgent       _agent;
        private PlayerEntity       _playerEntity;
        private SkillSystem        _skillSystem;
        private BasicAttackSystem  _basicAttack;
        private NetworkIdentity    _identity;
        private Camera             _cam;

        // ── Estado de movimento ────────────────────────────────────────────
        private bool    _holdMoving;
        private Vector3 _lastSentDestination;
        private float   _lastTargetUpdateTime;
        private float   _lastCmdMoveTime;

        // ── Câmera ─────────────────────────────────────────────────────────
        private float   _yaw         = 45f;
        private float   _pitch       = 45f;
        private float   _distance    = 12f;
        private bool    _orbiting;
        private Vector3 _camVelocity = Vector3.zero;

        private const float PITCH_MIN      = 10f;
        private const float PITCH_MAX      = 80f;
        private const float DIST_MIN       = 3f;
        private const float DIST_MAX       = 30f;
        private const float MAX_MOVE_DIST  = 120f;
        private const float CAM_SKIN_WIDTH = 0.3f;

        // Configurações de NavMeshAgent — manter em sync com NetworkPlayer/PlayerEntity
        private const float AGENT_ACCELERATION   = 60f;
        private const float AGENT_ANGULAR_SPEED  = 720f;
        private const float AGENT_STOPPING_DIST  = 0.15f;
        private const float AGENT_MIN_SPEED     = 3f;
        private const float AGENT_MAX_SPEED     = 7f;

        private float _lastSecurityWarnTime = -999f;
        private const float SECURITY_WARN_INTERVAL = 2f;

        // ══════════════════════════════════════════════════════════════════
        // Lifecycle
        // ══════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _agent       = GetComponent<NavMeshAgent>();
            _basicAttack = GetComponent<BasicAttackSystem>();
            _identity    = GetComponent<NetworkIdentity>();
        }

        private void OnEnable()
        {
            Cursor.visible   = true;
            Cursor.lockState = CursorLockMode.None;
        }

        private void OnDisable()
        {
            _orbiting        = false;
            _holdMoving      = false;
            Cursor.visible   = true;
            Cursor.lockState = CursorLockMode.None;
        }

        public override void OnStartLocalPlayer()
        {
            _playerEntity = GetComponent<PlayerEntity>();
            _skillSystem  = GetComponent<SkillSystem>();
            _basicAttack  = GetComponent<BasicAttackSystem>();
            _cam          = Camera.main;

            if (_cam == null)
                Debug.LogWarning("[NetworkPlayerController] Camera.main não encontrada.");

            // Configuração profissional do NavMeshAgent para movimento suave
            if (_agent != null)
            {
                _agent.acceleration     = AGENT_ACCELERATION;
                _agent.angularSpeed     = AGENT_ANGULAR_SPEED;
                _agent.autoBraking      = false;
                _agent.stoppingDistance = AGENT_STOPPING_DIST;

                if (_playerEntity != null && _playerEntity.Stats != null)
                    _agent.speed = Mathf.Clamp(_playerEntity.Stats.MoveSpeed, AGENT_MIN_SPEED, AGENT_MAX_SPEED);
            }

            Cursor.visible   = true;
            Cursor.lockState = CursorLockMode.None;

            if (terrainLayer    == 0) Debug.LogWarning("[NetworkPlayerController] terrainLayer não configurado.");
            if (targetableLayer == 0) Debug.LogWarning("[NetworkPlayerController] targetableLayer não configurado.");

            UIManager.Instance?.BindLocalPlayer(_playerEntity);
        }

        private void Update()
        {
            if (!isLocalPlayer) return;

            // Re-cacheia câmera se foi destruída
            if (_cam == null) _cam = Camera.main;

            HandleMouseInput();
            HandleSkillInput();
            HandleCameraOrbit();
            HandleUIInput();
        }

        private void LateUpdate()
        {
            if (!isLocalPlayer) return;
            UpdateCameraPosition();
        }

        // ══════════════════════════════════════════════════════════════════
        // Mouse — click & hold estilo Ragnarok/PoE
        // ══════════════════════════════════════════════════════════════════

        private void HandleMouseInput()
        {
            if (_cam == null) return;

            bool overUI = UIInputUtils.IsPointerOverUI();

            // ── Click inicial (down) ───────────────────────────────────────
            if (Input.GetMouseButtonDown(0) && !overUI)
            {
                Ray ray = _cam.ScreenPointToRay(Input.mousePosition);

                if (TryPickupItem(ray))         { _holdMoving = false; return; }
                if (TryHandleMonsterClick(ray)) { _holdMoving = false; return; }
                if (TrySelectTargetable(ray))   { _holdMoving = false; return; }

                if (TryMoveToGround(ray, showIndicator: true))
                {
                    _holdMoving           = true;
                    _lastTargetUpdateTime = Time.time;
                }
            }

            // ── Hold (botão pressionado) ───────────────────────────────────
            if (_holdMoving && Input.GetMouseButton(0) && !overUI)
            {
                if (Time.time - _lastTargetUpdateTime >= updateTargetInterval)
                {
                    _lastTargetUpdateTime = Time.time;
                    Ray ray = _cam.ScreenPointToRay(Input.mousePosition);
                    TryMoveToGround(ray, showIndicator: false);
                }
            }

            if (Input.GetMouseButtonUp(0))
                _holdMoving = false;
        }

        private bool TryHandleMonsterClick(Ray ray)
        {
            if (targetableLayer == 0) return false;
            if (!Physics.Raycast(ray, out RaycastHit hit, 300f, targetableLayer)) return false;

            var monster = hit.collider.GetComponentInParent<NetworkMonsterEntity>();
            if (monster == null || monster.IsDead) return false;

            bool targetChanged = _playerEntity != null
                              && _playerEntity.CurrentTarget != (ITargetable)monster;

            if (targetChanged && _basicAttack != null && _basicAttack.IsAutoAttacking)
                _basicAttack.CancelAutoAttack();

            _skillSystem?.CancelPendingWalk();
            _playerEntity?.SetTarget(monster);
            UIManager.Instance?.UpdateTargetPanel(monster);
            _basicAttack?.TryRegisterClick(monster);

            return true;
        }

        private bool TryPickupItem(Ray ray)
        {
            if (itemLayer == 0) return false;
            if (!Physics.Raycast(ray, out RaycastHit hit, 300f, itemLayer)) return false;

            var worldItem = hit.collider.GetComponentInParent<WorldItem>();
            if (worldItem == null) return false;

            if (_identity != null)
                worldItem.CmdPickUp(_identity.netId);
            return true;
        }

        private bool TrySelectTargetable(Ray ray)
        {
            if (targetableLayer == 0) return false;
            if (!Physics.Raycast(ray, out RaycastHit hit, 300f, targetableLayer)) return false;

            var targetable = hit.collider.GetComponentInParent<ITargetable>();
            if (targetable == null || targetable.IsDead) return false;

            _skillSystem?.CancelPendingWalk();
            _basicAttack?.CancelAutoAttack();
            _playerEntity?.SetTarget(targetable);
            UIManager.Instance?.UpdateTargetPanel(targetable);
            return true;
        }

        /// <summary>
        /// Move para um ponto no chão. NÃO PARA o agent antes — apenas troca o destino,
        /// preservando velocity e fluidez.
        /// </summary>
        private bool TryMoveToGround(Ray ray, bool showIndicator)
        {
            int moveLayerMask = terrainLayer != 0
                ? (int)terrainLayer
                : ~(1 << LayerMask.NameToLayer("Targetable"));

            if (!Physics.Raycast(ray, out RaycastHit hit, 300f, moveLayerMask)) return false;

            // Cancela ações pendentes SEM TOCAR no agent (sem ResetPath/velocity=0).
            _skillSystem?.CancelPendingWalkSoft();
            _basicAttack?.CancelAutoAttackSoft();

            if (showIndicator)
            {
                _playerEntity?.ClearTarget();
                UIManager.Instance?.ClearTargetPanel();
            }

            Vector3 dest = hit.point;
            if (NavMesh.SamplePosition(dest, out NavMeshHit navHit, 3f, NavMesh.AllAreas))
                dest = navHit.position;

            // Só redireciona se o novo destino é significativamente diferente.
            float deltaToCurrent = Vector3.Distance(_lastSentDestination, dest);
            bool shouldRedirect  = deltaToCurrent >= redirectThreshold;

            if (shouldRedirect)
            {
                if (_agent != null && _agent.isOnNavMesh)
                    _agent.SetDestination(dest);

                _lastSentDestination = dest;
            }

            // Throttle do envio ao servidor (independente de shouldRedirect, para
            // garantir que o servidor sempre tenha o destino mais recente)
            if (Time.time - _lastCmdMoveTime >= cmdMoveInterval)
            {
                _lastCmdMoveTime = Time.time;
                CmdMoveTo(dest);
            }

            if (showIndicator) SpawnMoveIndicator(hit.point);
            return true;
        }

        // ══════════════════════════════════════════════════════════════════
        // Teclado
        // ══════════════════════════════════════════════════════════════════

        private void HandleSkillInput()
        {
            if (_skillSystem == null) return;
            if (_playerEntity != null && _playerEntity.IsDead) return;
            if (UIInputUtils.IsTypingInInputField()) return;

            if (Input.GetKeyDown(KeyCode.Q)) { _holdMoving = false; _skillSystem.TryUseSkill(0); }
            if (Input.GetKeyDown(KeyCode.W)) { _holdMoving = false; _skillSystem.TryUseSkill(1); }
            if (Input.GetKeyDown(KeyCode.E)) { _holdMoving = false; _skillSystem.TryUseSkill(2); }
            if (Input.GetKeyDown(KeyCode.R)) { _holdMoving = false; _skillSystem.TryUseSkill(3); }
            if (Input.GetKeyDown(KeyCode.C)) AttributeWindowUI.Instance?.Toggle();
        }

        private void HandleUIInput()
        {
            if (UIInputUtils.IsTypingInInputField()) return;

            if (Input.GetKeyDown(KeyCode.I))
            {
                EnsureCursorVisible();
                InventoryUI.Instance?.Toggle();
            }
            if (Input.GetKeyDown(KeyCode.P))
            {
                EnsureCursorVisible();
                PowerGemUI.Instance?.Toggle();
            }
        }

        private void EnsureCursorVisible()
        {
            if (!_orbiting)
            {
                Cursor.visible   = true;
                Cursor.lockState = CursorLockMode.None;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Câmera
        // ══════════════════════════════════════════════════════════════════

        private void HandleCameraOrbit()
        {
            if (Input.GetMouseButtonDown(1))
            {
                _orbiting        = true;
                Cursor.visible   = false;
                Cursor.lockState = CursorLockMode.Locked;
            }
            if (Input.GetMouseButtonUp(1))
            {
                _orbiting        = false;
                Cursor.visible   = true;
                Cursor.lockState = CursorLockMode.None;
            }

            if (_orbiting)
            {
                _yaw   += Input.GetAxis("Mouse X") * orbitSensitivity;
                _pitch -= Input.GetAxis("Mouse Y") * orbitSensitivity;
                _pitch  = Mathf.Clamp(_pitch, PITCH_MIN, PITCH_MAX);

                if (_yaw > 360f)  _yaw -= 360f;
                if (_yaw < -360f) _yaw += 360f;
            }

            _distance -= Input.GetAxis("Mouse ScrollWheel") * zoomSensitivity;
            _distance  = Mathf.Clamp(_distance, DIST_MIN, DIST_MAX);
        }

        private void UpdateCameraPosition()
        {
            if (_cam == null) return;

            Quaternion rot   = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3    pivot = transform.position + Vector3.up * cameraHeight;
            Vector3    dir   = rot * new Vector3(0f, 0f, -1f);

            float effectiveDistance = _distance;
            int   occlusionMask = cameraOcclusionLayer != 0
                ? (int)cameraOcclusionLayer
                : (int)terrainLayer;

            if (occlusionMask != 0
                && Physics.SphereCast(pivot, CAM_SKIN_WIDTH, dir, out RaycastHit camHit,
                                      _distance, occlusionMask))
            {
                effectiveDistance = Mathf.Max(DIST_MIN, camHit.distance - CAM_SKIN_WIDTH);
            }

            Vector3 target = pivot + dir * effectiveDistance;

            if (target.y < transform.position.y + 0.5f)
                target.y = transform.position.y + 0.5f;

            _cam.transform.position = Vector3.SmoothDamp(
                _cam.transform.position, target, ref _camVelocity, cameraSmoothTime);
            _cam.transform.LookAt(pivot);
        }

        // ══════════════════════════════════════════════════════════════════
        // Commands
        // ══════════════════════════════════════════════════════════════════

        [Command]
        public void CmdMoveTo(Vector3 destination)
        {
            // Sanity: rejeita NaN/Infinity de cliente malformado
            if (float.IsNaN(destination.x) || float.IsInfinity(destination.x)
                || float.IsNaN(destination.y) || float.IsInfinity(destination.y)
                || float.IsNaN(destination.z) || float.IsInfinity(destination.z))
            {
                Debug.LogWarning($"[Security] CmdMoveTo com coordenadas inválidas.");
                return;
            }

            var netPlayer = GetComponent<NetworkPlayer>();
            if (netPlayer == null || netPlayer.Dead) return;

            float dist = Vector3.Distance(transform.position, destination);
            if (dist > MAX_MOVE_DIST)
            {
                if (Time.time - _lastSecurityWarnTime >= SECURITY_WARN_INTERVAL)
                {
                    _lastSecurityWarnTime = Time.time;
                    Debug.LogWarning($"[Security] CmdMoveTo suspeito: dist={dist:0.0} | {netPlayer.CharacterName}");
                }
                return;
            }

            if (_agent == null) return;

            Vector3 finalDest = destination;
            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 3f, NavMesh.AllAreas)
                || NavMesh.SamplePosition(destination, out hit, 6f, NavMesh.AllAreas))
            {
                finalDest = hit.position;
            }
            else if (debugMovement)
            {
                Debug.LogWarning($"[Server] CmdMoveTo: destino fora do NavMesh para {netPlayer.CharacterName}");
            }

            // SetDestination puro — sem ResetPath, sem velocity=0.
            _agent.SetDestination(finalDest);
        }

        // ══════════════════════════════════════════════════════════════════
        // API pública
        // ══════════════════════════════════════════════════════════════════

        public void SetEnabled(bool value)
        {
            enabled = value;
            if (!value)
            {
                _holdMoving = false;
                _basicAttack?.CancelAutoAttack();
                _skillSystem?.CancelPendingWalk();

                _orbiting        = false;
                Cursor.visible   = true;
                Cursor.lockState = CursorLockMode.None;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Helpers
        // ══════════════════════════════════════════════════════════════════

        private void SpawnMoveIndicator(Vector3 pos)
        {
            if (moveIndicatorPrefab == null) return;
            var go = Instantiate(moveIndicatorPrefab,
                pos + Vector3.up * 0.02f,
                Quaternion.Euler(90f, 0f, 0f));
            Destroy(go, 0.8f);
        }
    }
}
