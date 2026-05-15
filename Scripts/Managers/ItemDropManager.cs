using UnityEngine;
using Mirror;
using RPG.Data;
using System.Collections.Generic;

namespace RPG.Managers
{
    /// <summary>
    /// Sorteia e spawna item drops quando um monstro morre.
    /// Toda lógica é server-side.
    ///
    /// Valida o item no ItemDatabase ANTES de instanciar — evita memory leak
    /// de GameObjects órfãos quando o item não existe.
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 6) ===
    ///
    ///   1. VALIDAÇÃO DE PREFAB EM AWAKE:
    ///      Antes, se worldItemPrefab não estivesse configurado, só descobríamos
    ///      no primeiro ServerSpawnDrop. Agora logamos erro grave no Awake.
    ///
    ///   2. CAP DE DROPS POR SPAWN:
    ///      Adicionado MAX_DROPS_PER_SPAWN = 16 como sanity check contra
    ///      configurações abusivas (guaranteedDrops com 100 itens, etc).
    /// </summary>
    public class ItemDropManager : MonoBehaviour
    {
        public static ItemDropManager Instance { get; private set; }

        [Header("Prefab do Item no Mundo")]
        [Tooltip("Precisa ter NetworkIdentity + WorldItem.")]
        [SerializeField] private GameObject worldItemPrefab;

        [Header("Tabela de Drop Global (fallback)")]
        [Tooltip("Itens que qualquer monstro pode dropar se não tiver tabela própria.")]
        [SerializeField] private List<ItemData> globalDropTable = new List<ItemData>();

        [Header("Configuração")]
        [SerializeField] private float spawnHeightOffset = 0.3f;
        [SerializeField] private float dropScatterRadius = 0.8f;

        private const int MAX_DROPS_PER_SPAWN = 16;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            if (worldItemPrefab == null)
                Debug.LogError("[ItemDropManager] worldItemPrefab NÃO CONFIGURADO. " +
                               "Drops não funcionarão!");
            else if (worldItemPrefab.GetComponent<RPG.Network.WorldItem>() == null)
                Debug.LogError("[ItemDropManager] worldItemPrefab não tem WorldItem component.");
            else if (worldItemPrefab.GetComponent<NetworkIdentity>() == null)
                Debug.LogError("[ItemDropManager] worldItemPrefab não tem NetworkIdentity.");
        }

        /// <summary>
        /// Sorteia drops para um monstro morto.
        /// guaranteedDrops são sempre spawnados (independente de chance).
        /// </summary>
        [Server]
        public void ServerSpawnDrop(
            Vector3        position,
            float          dropChance      = 50f,
            List<ItemData> customDropTable = null,
            List<string>   guaranteedDrops = null)
        {
            if (!NetworkServer.active) return;

            if (worldItemPrefab == null)
            {
                Debug.LogWarning("[ItemDropManager] worldItemPrefab não configurado.");
                return;
            }

            int dropIndex = 0;

            // Drops garantidos (com cap defensivo)
            if (guaranteedDrops != null)
            {
                int limit = Mathf.Min(guaranteedDrops.Count, MAX_DROPS_PER_SPAWN);
                for (int i = 0; i < limit; i++)
                {
                    Vector3 pos = ScatterPosition(position, dropIndex++);
                    SpawnWorldItem(pos, guaranteedDrops[i]);
                }
            }

            // Drop aleatório
            if (Random.Range(0f, 100f) > dropChance) return;

            var table = (customDropTable != null && customDropTable.Count > 0)
                ? customDropTable
                : globalDropTable;

            string droppedId = ItemDatabase.RollDrop(table);
            if (!string.IsNullOrEmpty(droppedId) && dropIndex < MAX_DROPS_PER_SPAWN)
            {
                Vector3 pos = ScatterPosition(position, dropIndex);
                SpawnWorldItem(pos, droppedId);
            }
        }

        /// <summary>
        /// Valida o item ANTES de instanciar para evitar memory leak.
        /// </summary>
        [Server]
        private void SpawnWorldItem(Vector3 position, string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return;

            if (ItemDatabase.Instance == null)
            {
                Debug.LogWarning($"[ItemDropManager] ItemDatabase.Instance nulo. Drop '{itemId}' ignorado.");
                return;
            }

            if (!ItemDatabase.Instance.Contains(itemId))
            {
                Debug.LogWarning($"[ItemDropManager] Item '{itemId}' não existe no banco. Ignorado.");
                return;
            }

            var go   = Instantiate(worldItemPrefab, position, Quaternion.identity);
            var item = go.GetComponent<RPG.Network.WorldItem>();

            if (item == null)
            {
                // Não deveria acontecer (validamos em Awake), mas defesa em profundidade
                Debug.LogError("[ItemDropManager] worldItemPrefab não tem WorldItem component.");
                Destroy(go);
                return;
            }

            item.ServerInitialize(itemId);
            NetworkServer.Spawn(go);
        }

        /// <summary>
        /// Distribui drops em padrão de espiral (ângulo dourado).
        /// </summary>
        private Vector3 ScatterPosition(Vector3 center, int index)
        {
            if (index == 0) return center + Vector3.up * spawnHeightOffset;

            float angle = index * 137.5f * Mathf.Deg2Rad; // ângulo dourado
            float r     = dropScatterRadius * (0.5f + 0.5f * (index % 3) / 3f);
            return new Vector3(
                center.x + Mathf.Cos(angle) * r,
                center.y + spawnHeightOffset,
                center.z + Mathf.Sin(angle) * r);
        }
    }
}