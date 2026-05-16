using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Character;
using RPG.UI;
using RPG.Network;
using RPG.Data;

namespace RPG.Combat
{
    /// <summary>
    /// Auto-ataque básico, baseado no PERFIL DA ARMA EQUIPADA.
    ///
    /// === COMO FUNCIONA ===
    ///   1. Cliente lê o WeaponAttackProfile do item na slot Weapon do inventário.
    ///   2. O perfil determina: range, melee/projétil, físico/mágico, multiplicador,
    ///      animação, custo de mana, velocidade.
    ///   3. Cliente persegue até entrar em range, ataca em intervalos de
    ///      (1/ASPD) * profile.AttackIntervalMultiplier.
    ///   4. Cmd enviado ao servidor inclui o range pretendido — servidor compara
    ///      contra o range do perfil real da arma equipada (que ele conhece),
    ///      então cliente NÃO pode reportar range maior que tem.
    ///
    /// === REATIVIDADE ===
    ///   - Inscreve-se em OnEquipmentChanged. Trocar de arma durante auto-ataque
    ///     atualiza o perfil ativo em tempo real (com cancel suave se a nova
    ///     arma não tem range).
    ///
    /// === MUDANÇAS PRINCIPAIS VS VERSÃO ANTERIOR ===
    ///   - attackRange e attackInterval REMOVIDOS dos campos serializados:
    ///     agora vêm do perfil da arma.
    ///   - Suporte a projétil (Cmd separado: CmdBasicAttackRanged).
    ///   - Suporte a custo de mana no básico (cajado, varinha).
    /// </summary>
    [RequireComponent(typeof(PlayerEntity))]
    [RequireComponent(typeof(NetworkIdentity))]
    public class BasicAttackSystem : NetworkBehaviour
    {
        [Header("Configuração Geral")]
        [Tooltip("Janela para reconhecer duplo-clique (s).")]
        [SerializeField] private float doubleClickTime = 0.35f;

        [Tooltip("Frequência máxima de envio de CmdMoveTo durante perseguição (s).")]
        [SerializeField] private float moveCommandInterval = 0.18f;

        [Tooltip("Distância mínima para considerar troca de destino na perseguição.")]
        [SerializeField] private float chaseRedirectThreshold = 0.5f;

        [Header("Debug")]
        [SerializeField] private bool debugLogs = false;

        // Constantes de tuning
        private const float DEST_FRACTION      = 0.80f;
        private const float RANGE_CHECK_MARGIN = 1.05f;
        private const float CHASE_STOP_DIST    = 0.15f;
        private const float IDLE_STOP_DIST     = 0.5f;
        private const float MIN_INTERVAL       = 0.2f;
        private const float MAX_INTERVAL       = 3f;
        private const float ROTATION_SPEED     = 12f;

        // ── Componentes ────────────────────────────────────────────────────
        private PlayerEntity            _player;
        private NavMeshAgent            _agent;
        private Animator                _animator;
        private NetworkPlayerController _controller;
        private SkillSystem             _skillSystem;
        private NetworkIdentity         _identity;
        private NetworkInventory        _inventory;

        // ── Estado ─────────────────────────────────────────────────────────
        private NetworkMonsterEntity _attackTarget;
        private bool                 _autoAttacking;
        private float                _attackTimer;
        private float                _lastMoveCmd;
        private Vector3              _lastChaseDestination = Vector3.positiveInfinity;

        private float                _lastClickTime = -999f;
        private NetworkMonsterEntity _lastClickTarget;

        // Perfil da arma em uso. Cacheado e refeito quando o inventário muda.
        private WeaponAttackProfile _currentProfile;

        // ── Subscrições para cleanup ───────────────────────────────────────
        private bool _subscribedToPlayerEvents;
        private bool _subscribedToInventoryEvents;

        public bool  IsAutoAttacking => _autoAttacking;
        public float AttackRange     => _currentProfile?.Range ?? 2.5f;
        public WeaponAttackProfile CurrentProfile => _currentProfile;

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            _player      = GetComponent<PlayerEntity>();
            _agent       = GetComponent<NavMeshAgent>();
            _animator    = GetComponentInChildren<Animator>();
            _controller  = GetComponent<NetworkPlayerController>();
            _skillSystem = GetComponent<SkillSystem>();
            _identity    = GetComponent<NetworkIdentity>();
            _inventory   = GetComponent<NetworkInventory>();

            _currentProfile = WeaponAttackProfile.Default(WeaponType.Unarmed);
        }

        public override void OnStartLocalPlayer()
        {
            SubscribeToPlayerEvents();
            SubscribeToInventoryEvents();
            RefreshWeaponProfile();
        }

        public override void OnStopClient()
        {
            UnsubscribeFromPlayerEvents();
            UnsubscribeFromInventoryEvents();
            CancelAutoAttackSoft();
        }

        private void OnDisable()
        {
            if (_autoAttacking) CancelAutoAttackSoft();
        }

        private void OnDestroy()
        {
            UnsubscribeFromPlayerEvents();
            UnsubscribeFromInventoryEvents();
        }

        private void SubscribeToPlayerEvents()
        {
            if (_subscribedToPlayerEvents || _player == null) return;

            _player.OnDeathChanged  += OnPlayerDeathChanged;
            _player.OnTargetChanged += OnPlayerTargetChanged;

            _subscribedToPlayerEvents = true;
        }

        private void UnsubscribeFromPlayerEvents()
        {
            if (!_subscribedToPlayerEvents || _player == null) return;

            _player.OnDeathChanged  -= OnPlayerDeathChanged;
            _player.OnTargetChanged -= OnPlayerTargetChanged;

            _subscribedToPlayerEvents = false;
        }

        private void SubscribeToInventoryEvents()
        {
            if (_subscribedToInventoryEvents || _inventory == null) return;
            _inventory.OnEquipmentChanged += OnEquipmentChanged;
            _subscribedToInventoryEvents = true;
        }

        private void UnsubscribeFromInventoryEvents()
        {
            if (!_subscribedToInventoryEvents || _inventory == null) return;
            _inventory.OnEquipmentChanged -= OnEquipmentChanged;
            _subscribedToInventoryEvents = false;
        }

        private void OnPlayerDeathChanged(bool isDead)
        {
            if (isDead && _autoAttacking)
            {
                Log("Player morreu — auto-ataque cancelado.");
                CancelAutoAttack();
            }
        }

        private void OnPlayerTargetChanged(ITargetable newTarget)
        {
            if (_autoAttacking && newTarget != (ITargetable)_attackTarget)
                CancelAutoAttackSoft();
        }

        private void OnEquipmentChanged()
        {
            var oldProfile = _currentProfile;
            RefreshWeaponProfile();

            // Se trocou pra um tipo de arma muito diferente durante combate,
            // cancela o auto-ataque pra evitar comportamento inesperado.
            // Ex: trocar arco por espada com alvo a 10m de distância.
            if (_autoAttacking && oldProfile != null && _currentProfile != null
                && _attackTarget != null)
            {
                float dist = Vector3.Distance(transform.position, _attackTarget.Position);
                float newRange = _currentProfile.Range * RANGE_CHECK_MARGIN;
                if (dist > newRange)
                {
                    Log($"Mudança de arma deixou alvo fora de range — perseguindo com novo range {_currentProfile.Range:0.0}.");
                    // Não cancela; o loop principal vai reposicionar.
                }
            }
        }

        /// <summary>
        /// Recalcula _currentProfile baseado no item no slot Weapon.
        /// </summary>
        private void RefreshWeaponProfile()
        {
            if (_inventory == null)
            {
                _currentProfile = WeaponAttackProfile.Default(WeaponType.Unarmed);
                return;
            }

            string weaponId = _inventory.GetEquipped(EquipmentSlot.Weapon);
            if (string.IsNullOrEmpty(weaponId))
            {
                _currentProfile = WeaponAttackProfile.Default(WeaponType.Unarmed);
                Log("Sem arma — usando perfil Unarmed.");
                return;
            }

            var item = ItemDatabase.Instance?.GetItem(weaponId);
            if (item == null || !item.IsWeapon)
            {
                _currentProfile = WeaponAttackProfile.Default(WeaponType.Unarmed);
                return;
            }

            _currentProfile = item.GetEffectiveAttackProfile();
            Log($"Arma equipada: {item.DisplayName} ({_currentProfile.Type}, range {_currentProfile.Range:0.0})");
        }

        private void Update()
        {
            if (!isLocalPlayer) return;
            if (_player == null || !_player.IsInitialized) return;

            if (_player.IsDead)
            {
                if (_autoAttacking) CancelAutoAttack();
                return;
            }

            if (_autoAttacking) UpdateAutoAttack();
        }

        // ── API pública ────────────────────────────────────────────────────

        public bool TryRegisterClick(NetworkMonsterEntity monster)
        {
            if (IsTargetGone(monster)) return false;

            float now           = Time.time;
            bool  isDoubleClick = (now - _lastClickTime) <= doubleClickTime
                                  && _lastClickTarget == monster;

            _lastClickTime   = now;
            _lastClickTarget = monster;

            if (isDoubleClick)
            {
                StartAutoAttack(monster);
                return true;
            }
            return false;
        }

        public void CancelAutoAttack()
        {
            if (!_autoAttacking) return;
            CancelAutoAttackSoft();
            StopAgentMovement();
        }

        public void CancelAutoAttackSoft()
        {
            if (!_autoAttacking) return;

            _autoAttacking        = false;
            _attackTarget         = null;
            _attackTimer          = 0f;
            _lastChaseDestination = Vector3.positiveInfinity;
            Log("Auto-ataque cancelado (soft).");
        }

        // ── Início ─────────────────────────────────────────────────────────

        private void StartAutoAttack(NetworkMonsterEntity monster)
        {
            // Garante que temos o perfil mais atual da arma equipada
            RefreshWeaponProfile();

            _skillSystem?.CancelPendingWalkSoft();
            CancelAutoAttackSoft();

            _attackTarget         = monster;
            _autoAttacking        = true;
            _attackTimer          = GetAttackInterval();
            _lastChaseDestination = Vector3.positiveInfinity;

            _player.SetTarget(monster);
            UIManager.Instance?.UpdateTargetPanel(monster);

            Log($"Auto-ataque iniciado ({_currentProfile.Type}) → {monster.DisplayName}");
        }

        // ── Loop principal ─────────────────────────────────────────────────

        private void UpdateAutoAttack()
        {
            if (IsTargetGone(_attackTarget))
            {
                Log("Alvo destruído ou morto — cancelando.");
                CancelAutoAttack();
                _player.ClearTarget();
                UIManager.Instance?.ClearTargetPanel();
                return;
            }

            if (!IsCurrentTargetStillSame())
            {
                CancelAutoAttackSoft();
                return;
            }

            float dist           = Vector3.Distance(transform.position, _attackTarget.Position);
            float range          = _currentProfile.Range;
            float effectiveRange = range * RANGE_CHECK_MARGIN;

            if (dist > effectiveRange)
                ChaseTarget(range);
            else
                AttackTarget();
        }

        private void AttackTarget()
        {
            if (_agent != null && _agent.isOnNavMesh && _agent.hasPath)
            {
                _agent.ResetPath();
                _agent.stoppingDistance = IDLE_STOP_DIST;
                _lastChaseDestination   = Vector3.positiveInfinity;
            }

            _attackTimer += Time.deltaTime;
            if (_attackTimer >= GetAttackInterval())
            {
                _attackTimer = 0f;
                ExecuteBasicAttack();
            }

            RotateTowardsTarget();
        }

        private void RotateTowardsTarget()
        {
            if (_attackTarget == null) return;

            Vector3 dir = _attackTarget.Position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(
                    transform.rotation,
                    Quaternion.LookRotation(dir),
                    ROTATION_SPEED * Time.deltaTime);
        }

        private void ChaseTarget(float weaponRange)
        {
            if (_attackTarget == null || _agent == null || !_agent.isOnNavMesh) return;

            Vector3 destination = CalculateChaseDestination(_attackTarget.Position, weaponRange);

            if (Vector3.Distance(destination, _lastChaseDestination) >= chaseRedirectThreshold)
            {
                _agent.stoppingDistance = CHASE_STOP_DIST;
                _agent.SetDestination(destination);
                _lastChaseDestination = destination;
            }

            if (Time.time - _lastMoveCmd >= moveCommandInterval)
            {
                _lastMoveCmd = Time.time;
                _controller?.CmdMoveTo(destination);
            }
        }

        private Vector3 CalculateChaseDestination(Vector3 targetPos, float weaponRange)
        {
            Vector3 toTarget = targetPos - transform.position;
            float   dist     = toTarget.magnitude;

            float safeStopDist = weaponRange * DEST_FRACTION;
            if (dist <= safeStopDist * 0.95f)
                return transform.position;

            Vector3 direction   = toTarget.normalized;
            Vector3 destination = targetPos - direction * safeStopDist;

            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                return hit.position;

            return destination;
        }

        // ── Execução do ataque ─────────────────────────────────────────────

        private void ExecuteBasicAttack()
        {
            if (IsTargetGone(_attackTarget)) return;
            if (_identity == null) return;

            string animTrigger = !string.IsNullOrEmpty(_currentProfile.AnimTrigger)
                ? _currentProfile.AnimTrigger
                : "Attack";
            _animator?.SetTrigger(animTrigger);

            // Servidor já conhece a arma e perfil — o cliente envia apenas
            // a INTENÇÃO. O servidor valida e aplica.
            //
            // Cmd único para ambos tipos: servidor decide melee/projétil
            // baseado no perfil real da arma equipada (não confia no cliente).
            _attackTarget.CmdBasicAttack(_identity.netId, _currentProfile.Range);

            Log($"CmdBasicAttack → {_attackTarget.DisplayName} (perfil: {_currentProfile.Type})");
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private float GetAttackInterval()
        {
            float baseInterval = 1.2f;
            if (_player.IsInitialized && _player.Stats != null)
                baseInterval = 1f / Mathf.Max(0.1f, _player.Stats.ASPD);

            float modifier = _currentProfile?.AttackIntervalMultiplier ?? 1f;
            return Mathf.Clamp(baseInterval * modifier, MIN_INTERVAL, MAX_INTERVAL);
        }

        private void StopAgentMovement()
        {
            if (_agent == null || !_agent.isOnNavMesh) return;
            _agent.ResetPath();
            _agent.stoppingDistance = IDLE_STOP_DIST;
            _lastChaseDestination   = Vector3.positiveInfinity;
        }

        private bool IsCurrentTargetStillSame()
        {
            if (_player.CurrentTarget == null) return false;
            var current = _player.CurrentTarget as NetworkMonsterEntity;
            return current == _attackTarget && current != null;
        }

        private static bool IsTargetGone(NetworkMonsterEntity target)
            => target == null || target.IsDead;

        private void Log(string msg)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (debugLogs) Debug.Log($"[BasicAttackSystem] {msg}");
#endif
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            float r = _currentProfile?.Range ?? 2.5f;

            // Cor por categoria visual
            Color rangeColor = new Color(1f, 0.5f, 0f, 0.4f);
            if (_currentProfile != null)
            {
                if (_currentProfile.UsesProjectile && !_currentProfile.IsPhysical)
                    rangeColor = new Color(0.3f, 0.6f, 1f, 0.4f); // mágico = azul
                else if (_currentProfile.UsesProjectile)
                    rangeColor = new Color(0.7f, 1f, 0.3f, 0.4f); // arco = verde
            }

            Gizmos.color = rangeColor;
            Gizmos.DrawWireSphere(transform.position, r);

            Gizmos.color = new Color(0f, 1f, 0.5f, 0.25f);
            Gizmos.DrawWireSphere(transform.position, r * DEST_FRACTION);
        }
#endif
    }
}
