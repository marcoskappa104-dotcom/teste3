using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Character;
using RPG.UI;
using RPG.Network;

namespace RPG.Combat
{
    /// <summary>
    /// Auto-ataque básico (sem custo de mana, sem cooldown explícito — apenas ASPD).
    ///
    /// Disparado por duplo-clique em um monstro. Persegue até entrar no range,
    /// para, ataca em intervalos de 1/ASPD, e cancela se o alvo morrer ou mudar.
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 1 — robustez) ===
    ///
    ///   1. SUBSCRIÇÃO EM OnDeathChanged:
    ///      Antes, se o player morresse durante auto-ataque, _autoAttacking
    ///      ficava true. Ao reviver, o sistema retomava ataque contra alvo
    ///      stale. Agora, OnDeathChanged dispara CancelAutoAttack() e
    ///      mantém o estado consistente.
    ///
    ///   2. SUBSCRIÇÃO EM OnTargetChanged:
    ///      Se outro sistema (NetworkPlayerController) limpar o alvo (ex:
    ///      jogador clicou no chão para mover), o auto-ataque é cancelado
    ///      automaticamente. Antes, a detecção dependia de IsCurrentTargetStillSame
    ///      checando a cada Update.
    ///
    ///   3. CLEANUP COMPLETO EM OnDisable / OnDestroy:
    ///      Desinscreve dos eventos do PlayerEntity para evitar memory leaks
    ///      e callbacks órfãos em cleanup de cena.
    ///
    ///   4. ATTACK_TIMER RESETADO EM SOFT CANCEL:
    ///      Antes, CancelAutoAttackSoft não tocava no _attackTimer. Em
    ///      cenários de cancel+restart rápido o primeiro ataque podia
    ///      disparar imediatamente.
    /// </summary>
    [RequireComponent(typeof(PlayerEntity))]
    [RequireComponent(typeof(NetworkIdentity))]
    public class BasicAttackSystem : NetworkBehaviour
    {
        [Header("Ataque")]
        [Tooltip("Distância máxima de ataque (m).")]
        [SerializeField] private float attackRange = 2.5f;

        [Tooltip("Intervalo fixo se useCharacterASPD = false.")]
        [SerializeField] private float attackInterval = 1.2f;

        [Tooltip("Se true, usa 1/ASPD do personagem como intervalo.")]
        [SerializeField] private bool useCharacterASPD = true;

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
        private const float MIN_INTERVAL       = 0.3f;
        private const float MAX_INTERVAL       = 3f;
        private const float ROTATION_SPEED     = 12f;

        // ── Componentes ────────────────────────────────────────────────────
        private PlayerEntity            _player;
        private NavMeshAgent            _agent;
        private Animator                _animator;
        private NetworkPlayerController _controller;
        private SkillSystem             _skillSystem;
        private NetworkIdentity         _identity;

        // ── Estado ─────────────────────────────────────────────────────────
        private NetworkMonsterEntity _attackTarget;
        private bool                 _autoAttacking;
        private float                _attackTimer;
        private float                _lastMoveCmd;
        private Vector3              _lastChaseDestination = Vector3.positiveInfinity;

        private float                _lastClickTime = -999f;
        private NetworkMonsterEntity _lastClickTarget;

        // ── Subscrições para cleanup ───────────────────────────────────────
        private bool _subscribedToPlayerEvents;

        public bool  IsAutoAttacking => _autoAttacking;
        public float AttackRange     => attackRange;

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            _player      = GetComponent<PlayerEntity>();
            _agent       = GetComponent<NavMeshAgent>();
            _animator    = GetComponentInChildren<Animator>();
            _controller  = GetComponent<NetworkPlayerController>();
            _skillSystem = GetComponent<SkillSystem>();
            _identity    = GetComponent<NetworkIdentity>();
        }

        public override void OnStartLocalPlayer()
        {
            SubscribeToPlayerEvents();
        }

        public override void OnStopClient()
        {
            UnsubscribeFromPlayerEvents();
            CancelAutoAttackSoft();
        }

        private void OnDisable()
        {
            if (_autoAttacking) CancelAutoAttackSoft();
        }

        private void OnDestroy()
        {
            UnsubscribeFromPlayerEvents();
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
            // Alvo mudou ou foi limpo externamente — cancela soft (sem mexer no agent).
            if (_autoAttacking && newTarget != (ITargetable)_attackTarget)
                CancelAutoAttackSoft();
        }

        private void Update()
        {
            if (!isLocalPlayer) return;
            if (_player == null || !_player.IsInitialized) return;

            // Belt-and-suspenders: se por algum motivo o evento de morte não
            // chegou, garante consistência aqui também.
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

        /// <summary>
        /// Cancela o auto-ataque E para o agent. Use quando realmente quiser
        /// que o player pare (ex: morte, mudança de UI, alvo morto).
        /// </summary>
        public void CancelAutoAttack()
        {
            if (!_autoAttacking) return;
            CancelAutoAttackSoft();
            StopAgentMovement();
        }

        /// <summary>
        /// Cancela apenas o ESTADO de auto-ataque, sem mexer no agent.
        /// Use quando o player vai continuar se movendo (clique para mover
        /// noutro ponto). Evita o jitter de parar-acelerar.
        /// </summary>
        public void CancelAutoAttackSoft()
        {
            if (!_autoAttacking) return;

            _autoAttacking        = false;
            _attackTarget         = null;
            _attackTimer          = 0f; // reset para não disparar imediatamente em restart
            _lastChaseDestination = Vector3.positiveInfinity;
            Log("Auto-ataque cancelado (soft).");
        }

        // ── Início ─────────────────────────────────────────────────────────

        private void StartAutoAttack(NetworkMonsterEntity monster)
        {
            _skillSystem?.CancelPendingWalkSoft();
            CancelAutoAttackSoft();

            _attackTarget         = monster;
            _autoAttacking        = true;
            _attackTimer          = GetAttackInterval();
            _lastChaseDestination = Vector3.positiveInfinity;

            _player.SetTarget(monster);
            UIManager.Instance?.UpdateTargetPanel(monster);

            Log($"Auto-ataque iniciado → {monster.DisplayName}");
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
            float effectiveRange = attackRange * RANGE_CHECK_MARGIN;

            if (dist > effectiveRange)
                ChaseTarget();
            else
                AttackTarget();
        }

        private void AttackTarget()
        {
            // Para o agent SUAVEMENTE — apenas se ainda tem path.
            // Não chamamos StopAgentMovement aqui porque o agent já vai parar
            // naturalmente ao chegar perto do alvo.
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

        private void ChaseTarget()
        {
            if (_attackTarget == null || _agent == null || !_agent.isOnNavMesh) return;

            Vector3 destination = CalculateChaseDestination(_attackTarget.Position);

            // Só chama SetDestination quando o destino mudou significativamente.
            // SetDestination repetido com mesmo valor causa recálculo de path,
            // que é o que produz o "stutter" durante perseguição.
            if (Vector3.Distance(destination, _lastChaseDestination) >= chaseRedirectThreshold)
            {
                _agent.stoppingDistance = CHASE_STOP_DIST;
                _agent.SetDestination(destination);
                _lastChaseDestination = destination;
            }

            // Throttle do envio ao servidor
            if (Time.time - _lastMoveCmd >= moveCommandInterval)
            {
                _lastMoveCmd = Time.time;
                _controller?.CmdMoveTo(destination);
            }
        }

        private Vector3 CalculateChaseDestination(Vector3 targetPos)
        {
            Vector3 toTarget = targetPos - transform.position;
            float   dist     = toTarget.magnitude;

            float safeStopDist = attackRange * DEST_FRACTION;
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

            _animator?.SetTrigger("Attack");

            if (_identity != null)
            {
                _attackTarget.CmdBasicAttack(_identity.netId, attackRange);
                Log($"CmdBasicAttack → {_attackTarget.DisplayName}");
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private float GetAttackInterval()
        {
            if (useCharacterASPD && _player.IsInitialized && _player.Stats != null)
                return Mathf.Clamp(1f / Mathf.Max(0.1f, _player.Stats.ASPD),
                                   MIN_INTERVAL, MAX_INTERVAL);
            return attackInterval;
        }

        /// <summary>
        /// Para o agent de forma suave: limpa path e ajusta stoppingDistance.
        /// NÃO zera velocity (deixa o brake natural desacelerar).
        /// </summary>
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
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, attackRange);

            Gizmos.color = new Color(0f, 1f, 0.5f, 0.25f);
            Gizmos.DrawWireSphere(transform.position, attackRange * DEST_FRACTION);
        }
#endif
    }
}
