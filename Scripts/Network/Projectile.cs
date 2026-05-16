using UnityEngine;
using Mirror;
using RPG.Character;

namespace RPG.Network
{
    /// <summary>
    /// Projétil server-authoritative. Spawn pelo servidor após validar o ataque
    /// ranged; viaja em direção ao alvo; ao impacto, servidor aplica o dano e
    /// destrói o projétil.
    ///
    /// === DESIGN ===
    ///   - O DANO já foi calculado quando o projétil nasceu (no momento do ataque).
    ///     Isso é deliberado: garante que o resultado é coerente com os stats
    ///     do atacante naquele instante, mesmo se ele morrer enquanto o projétil
    ///     voa, ou se o alvo trocar de armadura.
    ///   - O HOMING é leve: o projétil ajusta a direção a cada frame para seguir
    ///     o alvo, mas com velocidade angular limitada. Se o alvo morrer ou
    ///     for muito longe, segue em linha reta e morre por timeout.
    ///   - Não usa colliders/Rigidbody — distância manual é mais determinística
    ///     em multiplayer e mais barata.
    ///
    /// === COMO USAR ===
    /// 1. Prefab precisa de: NetworkIdentity, NetworkTransform (para sync visual),
    ///    e este componente. Sem Rigidbody.
    /// 2. Registre o prefab no RPGNetworkManager.spawnablePrefabs.
    /// 3. O servidor chama ServerInitialize(...) imediatamente após NetworkServer.Spawn.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class Projectile : NetworkBehaviour
    {
        [Header("Configuração")]
        [Tooltip("Velocidade angular máxima (deg/s) para seguir o alvo.")]
        [SerializeField] private float maxTurnRate = 360f;

        [Tooltip("Distância de impacto (m). Quando chegar a essa distância do alvo, aplica dano.")]
        [SerializeField] private float impactDistance = 0.6f;

        [Tooltip("Tempo máximo de vida em segundos. Auto-destroi se não acertar.")]
        [SerializeField] private float maxLifetime = 6f;

        [Tooltip("Efeito visual ao impacto (opcional, instanciado client-side).")]
        [SerializeField] private GameObject hitVfxPrefab;

        // ── Dados de runtime (apenas servidor escreve; SyncVars para client follow) ──

        [SyncVar] private uint    _targetNetId;
        [SyncVar] private Vector3 _initialDirection;

        // Estado lógico só no servidor
        private float                _speed;
        private float                _damage;
        private bool                 _crit;
        private float                _spawnTime;
        private NetworkBehaviour     _serverTarget;
        private bool                 _hitProcessed;

        // No cliente: fallback se o NetworkTransform falhar
        private float _clientSpawnTime;

        // ══════════════════════════════════════════════════════════════════
        // API do servidor
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Inicializa o projétil no servidor. DEVE ser chamado IMEDIATAMENTE
        /// após NetworkServer.Spawn(prefab).
        /// </summary>
        [Server]
        public void ServerInitialize(NetworkBehaviour target, float speed, float damage, bool crit)
        {
            _serverTarget     = target;
            _speed            = Mathf.Max(1f, speed);
            _damage           = Mathf.Max(0f, damage);
            _crit             = crit;
            _spawnTime        = Time.time;
            _hitProcessed     = false;
            _targetNetId      = target != null && target.netIdentity != null ? target.netIdentity.netId : 0u;

            // Direção inicial em direção ao alvo (ou frente do projétil se alvo nulo)
            if (target != null)
            {
                Vector3 dir = (target.transform.position - transform.position);
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.001f)
                {
                    _initialDirection = dir.normalized;
                    transform.rotation = Quaternion.LookRotation(_initialDirection);
                }
                else
                {
                    _initialDirection = transform.forward;
                }
            }
            else
            {
                _initialDirection = transform.forward;
            }
        }

        public override void OnStartClient()
        {
            _clientSpawnTime = Time.time;
            // Cliente alinha o yaw inicial; o resto vem via NetworkTransform
            if (_initialDirection.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(_initialDirection);
        }

        // ══════════════════════════════════════════════════════════════════
        // Update
        // ══════════════════════════════════════════════════════════════════

        private void Update()
        {
            // Servidor: lógica autoritativa de movimento e impacto
            if (isServer)
            {
                ServerUpdate();
                return;
            }

            // Cliente sem NetworkTransform: movimento dead-reckoning suave
            // (caso NetworkTransform esteja configurado no prefab, isso será sobrescrito)
            if (Time.time - _clientSpawnTime > maxLifetime + 0.5f)
            {
                // Failsafe: cliente nunca deve destruir o objeto, mas se está
                // muito além do tempo de vida e o servidor não destruiu,
                // ao menos esconde para não acumular visual.
                gameObject.SetActive(false);
            }
        }

        [Server]
        private void ServerUpdate()
        {
            // Timeout: nunca passa do maxLifetime
            if (Time.time - _spawnTime > maxLifetime)
            {
                NetworkServer.Destroy(gameObject);
                return;
            }

            Vector3 desiredDir = _initialDirection;

            // Homing leve: se o alvo ainda existe e está vivo, atualizamos a direção
            if (_serverTarget != null && !TargetIsDeadOrGone(_serverTarget))
            {
                Vector3 toTarget = _serverTarget.transform.position - transform.position;
                toTarget.y = 0f;
                float sqr = toTarget.sqrMagnitude;

                if (sqr > 0.001f)
                {
                    desiredDir = toTarget.normalized;

                    // Impacto?
                    if (sqr <= impactDistance * impactDistance)
                    {
                        ApplyImpact();
                        return;
                    }
                }
            }

            // Rotação clamped (homing suave)
            Vector3 currentForward = transform.forward;
            currentForward.y = 0f;
            if (currentForward.sqrMagnitude > 0.001f)
            {
                currentForward.Normalize();
                float angle = Vector3.Angle(currentForward, desiredDir);
                float maxStep = maxTurnRate * Time.deltaTime;
                if (angle > maxStep)
                {
                    Vector3 cross = Vector3.Cross(currentForward, desiredDir);
                    float sign    = Mathf.Sign(cross.y);
                    Quaternion q  = Quaternion.AngleAxis(maxStep * sign, Vector3.up);
                    desiredDir    = q * currentForward;
                }
                transform.rotation = Quaternion.LookRotation(desiredDir);
            }

            // Movimento
            transform.position += transform.forward * (_speed * Time.deltaTime);
        }

        [Server]
        private bool TargetIsDeadOrGone(NetworkBehaviour nb)
        {
            if (nb == null) return true;
            if (nb is ITargetable t) return t.IsDead;
            return false;
        }

        [Server]
        private void ApplyImpact()
        {
            if (_hitProcessed) return;
            _hitProcessed = true;

            // Aplica dano dependendo do tipo de alvo
            if (_serverTarget is NetworkMonsterEntity monster && !monster.IsDead)
            {
                // Não passa pelo pipeline normal porque o dano já foi calculado.
                // Usamos a API server-side direta para aplicar e mostrar feedback.
                monster.ServerTakeProjectileDamage(_damage, _crit);
            }
            else if (_serverTarget is NetworkPlayer player && !player.Dead)
            {
                // (Reservado para PvP futuro — atualmente projéteis monstro→player
                // não usam essa rota, mas a integração está pronta.)
                player.ServerApplyDamageWithFeedback(_damage);
            }

            RpcOnImpact(transform.position);
            NetworkServer.Destroy(gameObject);
        }

        // ══════════════════════════════════════════════════════════════════
        // VFX no cliente
        // ══════════════════════════════════════════════════════════════════

        [ClientRpc]
        private void RpcOnImpact(Vector3 pos)
        {
            if (Application.isBatchMode) return;
            if (hitVfxPrefab != null)
            {
                var vfx = Instantiate(hitVfxPrefab, pos, Quaternion.identity);
                Destroy(vfx, 2f);
            }
        }
    }
}
