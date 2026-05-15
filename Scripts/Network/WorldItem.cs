using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.UI;
using System.Collections;

namespace RPG.Network
{
    /// <summary>
    /// Item dropado no mundo. Sincronizado via Mirror.
    ///
    /// Visual:
    ///   O bobbing é aplicado em _visualRoot (filho do transform raiz) para
    ///   não interferir com o NetworkTransform do objeto principal. O prefab
    ///   DEVE ter um filho VisualRoot configurado — não criamos um automaticamente
    ///   em runtime porque mexer em hierarquia depois do Spawn é perigoso.
    ///
    /// Pickup:
    ///   Cliente chama CmdPickUp(myNetId). Servidor valida distância,
    ///   adiciona ao inventário do jogador e destrói o objeto.
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 3 — robustez) ===
    ///
    ///   1. CmdPickUp ATOMIC:
    ///      Antes, era teoricamente possível duas chamadas Cmd quase
    ///      simultâneas chegarem ao servidor antes do _picked = true,
    ///      causando duplicação (cada uma chamava ServerAddItem). Agora
    ///      setamos _picked = true ANTES de chamar ServerAddItem, e
    ///      revertemos se a adição falhar.
    ///
    ///   2. VALIDAÇÃO DE _itemId ANTES DE PICKUP:
    ///      Se SyncVar ainda não chegou (raro mas possível em conexões
    ///      lentas), _itemId pode estar vazio. Agora retornamos cedo.
    ///
    ///   3. RpcPickupFeedback SAFE PARA NULL localPlayer:
    ///      Em servidor dedicado isBatchMode pode estar false em alguns
    ///      setups (host-with-no-headless), então a checagem de localPlayer
    ///      é mais defensiva.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class WorldItem : NetworkBehaviour
    {
        [Header("Visual")]
        [SerializeField] private SpriteRenderer spriteRenderer;
        [SerializeField] private TMPro.TMP_Text nameLabel;
        [SerializeField] private GameObject     glowEffect;

        [Header("Visual Root (filho que recebe o bobbing)")]
        [Tooltip("Filho do transform raiz. Configure no prefab; em runtime usa o próprio transform como fallback (jitter possível).")]
        [SerializeField] private Transform visualRoot;

        [Header("Configuração")]
        [SerializeField] private float despawnTime  = 60f;
        [SerializeField] private float bobAmplitude = 0.15f;
        [SerializeField] private float bobFrequency = 1.5f;
        [SerializeField] private float pickupRadius = 2.5f;

        [SyncVar(hook = nameof(OnItemIdChanged))] private string _itemId = "";

        public string ItemId => _itemId;

        private bool  _picked;
        private float _startLocalY;
        private bool  _hasVisualRoot;

        // ══════════════════════════════════════════════════════════════════
        // Server
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public void ServerInitialize(string itemId)
        {
            _itemId = itemId;
            StartCoroutine(AutoDespawn());
        }

        [Server]
        private IEnumerator AutoDespawn()
        {
            yield return new WaitForSeconds(despawnTime);
            if (!_picked && isServer)
                NetworkServer.Destroy(gameObject);
        }

        // ══════════════════════════════════════════════════════════════════
        // Client
        // ══════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _hasVisualRoot = visualRoot != null;
            if (!_hasVisualRoot)
            {
                Debug.LogWarning($"[WorldItem] '{name}': visualRoot não configurado no prefab. " +
                                 "Bobbing aplicado no transform raiz pode causar jitter em multiplayer.");
            }
        }

        public override void OnStartClient()
        {
            if (_hasVisualRoot)
                _startLocalY = visualRoot.localPosition.y;

            RefreshVisual(_itemId);
        }

        private void OnItemIdChanged(string oldId, string newId) => RefreshVisual(newId);

        private void RefreshVisual(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return;
            var item = ItemDatabase.Instance?.GetItem(itemId);
            if (item == null) return;

            if (spriteRenderer != null && item.Icon != null)
                spriteRenderer.sprite = item.Icon;

            if (nameLabel != null)
            {
                nameLabel.text  = item.DisplayName;
                nameLabel.color = item.RarityColor;
            }

            if (glowEffect != null)
                glowEffect.SetActive(item.Rarity >= ItemRarity.Rare);
        }

        private void Update()
        {
            if (!isClient || !_hasVisualRoot) return;

            float newLocalY = _startLocalY
                + Mathf.Sin(Time.time * bobFrequency * Mathf.PI * 2f) * bobAmplitude;
            var localPos = visualRoot.localPosition;
            localPos.y = newLocalY;
            visualRoot.localPosition = localPos;
        }

        // ══════════════════════════════════════════════════════════════════
        // Pickup
        // ══════════════════════════════════════════════════════════════════

        [Command(requiresAuthority = false)]
        public void CmdPickUp(uint playerNetId)
        {
            if (_picked) return;
            if (string.IsNullOrEmpty(_itemId))
            {
                Debug.LogWarning("[WorldItem] CmdPickUp em item sem _itemId — SyncVar ainda não chegou?");
                return;
            }

            // O(1) — sem race condition de iteração
            NetworkPlayer player = null;
            if (NetworkServer.spawned.TryGetValue(playerNetId, out var identity))
                player = identity?.GetComponent<NetworkPlayer>();

            if (player == null || player.Dead) return;

            float dist = Vector3.Distance(transform.position, player.transform.position);
            if (dist > pickupRadius * 2f)
            {
                Debug.LogWarning($"[WorldItem] Pickup fora de range: {dist:0.1}u por {player.CharacterName}");
                return;
            }

            var inventory = player.GetComponent<NetworkInventory>();
            if (inventory == null) return;

            // ATOMIC: marca como pego ANTES de adicionar ao inventário.
            // Se ServerAddItem falhar (inventário cheio), revertemos.
            // Isso evita race condition de pickup duplo entre 2 Cmds rápidos.
            _picked = true;

            int slotIndex = inventory.ServerAddItem(_itemId);
            if (slotIndex < 0)
            {
                // Reverte: o player não conseguiu pegar (inventário cheio etc).
                // Outro player ainda pode tentar.
                _picked = false;
                return;
            }

            StopAllCoroutines();

            var    item     = ItemDatabase.Instance?.GetItem(_itemId);
            string itemName = item?.DisplayName ?? _itemId;
            Color  color    = item?.RarityColor ?? Color.white;
            RpcPickupFeedback(playerNetId, itemName, color);

            NetworkServer.Destroy(gameObject);
        }

        [ClientRpc]
        private void RpcPickupFeedback(uint playerNetId, string itemName, Color rarityColor)
        {
            // Servidor dedicado puro nem precisa avaliar o feedback
            if (Application.isBatchMode) return;

            var localPlayer = NetworkClient.localPlayer;
            if (localPlayer == null) return;
            if (localPlayer.netId != playerNetId) return;

            FloatingTextManager.Instance?.Show(
                $"+ {itemName}", transform.position + Vector3.up, rarityColor);
            UIManager.Instance?.ShowMessage($"Coletou: {itemName}");
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0f, 1f, 0f, 0.3f);
            Gizmos.DrawSphere(transform.position, pickupRadius);
        }
#endif
    }
}
