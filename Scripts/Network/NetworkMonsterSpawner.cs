using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Mirror;

namespace RPG.Network
{
    /// <summary>
    /// Spawna monstros em zonas ou pontos fixos quando o servidor inicia.
    /// Aguarda o NavMesh estar pronto antes de spawnar (evita erros de agent).
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 3 — robustez) ===
    ///
    ///   1. VALIDAÇÃO DE PREFAB MAIS ROBUSTA:
    ///      ValidateGroup agora também verifica:
    ///        - NetworkMonsterEntity tem NavMeshAgent (RequireComponent já cobre
    ///          em runtime, mas o check explícito facilita debug)
    ///        - SpawnCount > 0 (configurar 0 manualmente é provavelmente bug)
    ///
    ///   2. FALLBACK PARA EXTRAPOLAÇÃO DE NAVMESH:
    ///      Se a posição candidata estiver fora da rede de raycast (ex:
    ///      Terrain com altura inconsistente), tentamos SamplePosition com
    ///      raio maior antes de desistir.
    ///
    ///   3. EARLY RETURN EM SERVIDOR INATIVO:
    ///      Start checa NetworkServer.active no início; sem isso, em modo
    ///      cliente puro o componente ficava ativo gastando ciclos.
    /// </summary>
    public class NetworkMonsterSpawner : MonoBehaviour
    {
        [System.Serializable]
        public class SpawnGroup
        {
            [Header("Prefab")]
            [Tooltip("Precisa ter NetworkIdentity + NetworkMonsterEntity.")]
            public GameObject monsterPrefab;

            [Header("Modo Zona")]
            public bool      useFixedPoints = false;
            public Transform zoneCenter;
            public float     zoneRadius     = 15f;
            public int       spawnCount     = 3;

            [Header("Modo Pontos Fixos")]
            public Transform[] fixedSpawnPoints;

            [Header("Patrulha")]
            [Tooltip("Raio de patrulha por mob. 0 = sentinela (parado).")]
            public float patrolRadius = 12f;

            [Tooltip("Rótulo usado em logs e gizmos.")]
            public string groupLabel = "Grupo";
        }

        [SerializeField] private SpawnGroup[] spawnGroups;
        [SerializeField] private bool         logSpawns = true;

        [Header("Timing")]
        [Tooltip("Segundos para aguardar o NavMesh estar pronto.")]
        [SerializeField] private float navMeshWaitTimeout = 8f;

        [Tooltip("Delay entre spawns (evita flood de mensagens).")]
        [SerializeField] private float spawnDelay = 0.05f;

        private const int   NAVMESH_ATTEMPTS          = 20;
        private const float NAVMESH_SAMPLE_RADIUS     = 3f;
        private const float NAVMESH_SAMPLE_RADIUS_BIG = 8f;
        private const float MIN_DIST_BETWEEN_MOBS     = 2f;

        private void Start()
        {
            if (!NetworkServer.active) return;

            if (spawnGroups == null || spawnGroups.Length == 0)
            {
                Debug.LogWarning("[NetworkMonsterSpawner] Nenhum SpawnGroup configurado.");
                return;
            }

            StartCoroutine(SpawnWhenNavMeshReady());
        }

        private IEnumerator SpawnWhenNavMeshReady()
        {
            float elapsed = 0f;
            bool  navMeshReady = false;

            while (elapsed < navMeshWaitTimeout)
            {
                if (NavMesh.SamplePosition(Vector3.zero, out _, 200f, NavMesh.AllAreas)
                    || IsNavMeshAvailableNearGroups())
                {
                    navMeshReady = true;
                    break;
                }
                elapsed += 0.2f;
                yield return new WaitForSeconds(0.2f);
            }

            if (!navMeshReady)
            {
                Debug.LogError("[NetworkMonsterSpawner] NavMesh não encontrado após " +
                               $"{navMeshWaitTimeout}s. Verifique:\n" +
                               "1. A cena tem NavMesh baked? (Window → AI → Navigation → Bake)\n" +
                               "2. O Terrain tem o layer correto para o NavMesh?");
                yield break;
            }

            yield return SpawnAllWithDelay();
        }

        private bool IsNavMeshAvailableNearGroups()
        {
            foreach (var group in spawnGroups)
            {
                if (group == null) continue;

                Vector3 testPos = Vector3.zero;
                if (!group.useFixedPoints && group.zoneCenter != null)
                    testPos = group.zoneCenter.position;
                else if (group.useFixedPoints
                         && group.fixedSpawnPoints != null
                         && group.fixedSpawnPoints.Length > 0
                         && group.fixedSpawnPoints[0] != null)
                    testPos = group.fixedSpawnPoints[0].position;

                if (NavMesh.SamplePosition(testPos, out _, 20f, NavMesh.AllAreas))
                    return true;
            }
            return false;
        }

        private IEnumerator SpawnAllWithDelay()
        {
            int totalSpawned = 0;

            foreach (var group in spawnGroups)
            {
                if (group == null || !ValidateGroup(group)) continue;

                if (group.useFixedPoints)
                {
                    if (group.fixedSpawnPoints == null) continue;
                    foreach (var point in group.fixedSpawnPoints)
                    {
                        if (point == null) continue;
                        SpawnMonster(group, SnapToNavMesh(point.position));
                        totalSpawned++;
                        if (spawnDelay > 0f)
                            yield return new WaitForSeconds(spawnDelay);
                    }
                }
                else
                {
                    if (group.zoneCenter == null)
                    {
                        Debug.LogWarning($"[NetworkMonsterSpawner] '{group.groupLabel}': zoneCenter não configurado.");
                        continue;
                    }

                    var usedPositions = new List<Vector3>();
                    for (int i = 0; i < group.spawnCount; i++)
                    {
                        Vector3? pos = FindSpawnPositionInZone(
                            group.zoneCenter.position, group.zoneRadius, usedPositions);

                        if (pos == null)
                        {
                            Debug.LogWarning($"[NetworkMonsterSpawner] '{group.groupLabel}': " +
                                             $"posição não encontrada para mob {i + 1}/{group.spawnCount}.");
                            continue;
                        }

                        usedPositions.Add(pos.Value);
                        SpawnMonster(group, pos.Value);
                        totalSpawned++;

                        if (spawnDelay > 0f)
                            yield return new WaitForSeconds(spawnDelay);
                    }
                }
            }

            if (logSpawns)
                Debug.Log($"[NetworkMonsterSpawner] Total: {totalSpawned} monstros.");
        }

        private void SpawnMonster(SpawnGroup group, Vector3 position)
        {
            var mob = Instantiate(group.monsterPrefab, position, Quaternion.identity);

            // Configura SetSpawnData ANTES do Spawn para que OnStartServer já tenha o home
            var entity = mob.GetComponent<NetworkMonsterEntity>();
            entity?.SetSpawnData(position, group.patrolRadius);

            NetworkServer.Spawn(mob);
        }

        private Vector3? FindSpawnPositionInZone(Vector3 center, float radius, List<Vector3> usedPositions)
        {
            for (int attempt = 0; attempt < NAVMESH_ATTEMPTS; attempt++)
            {
                Vector2 rand2D    = Random.insideUnitCircle * radius;
                Vector3 candidate = center + new Vector3(rand2D.x, 0f, rand2D.y);

                // Ajusta altura pelo terreno
                if (Physics.Raycast(candidate + Vector3.up * 20f, Vector3.down,
                                    out RaycastHit hit, 40f))
                    candidate = hit.point;

                // Tenta raio normal; se falhar, tenta raio maior antes de desistir
                bool sampled = NavMesh.SamplePosition(candidate, out NavMeshHit navHit,
                                                     NAVMESH_SAMPLE_RADIUS, NavMesh.AllAreas)
                            || NavMesh.SamplePosition(candidate, out navHit,
                                                     NAVMESH_SAMPLE_RADIUS_BIG, NavMesh.AllAreas);
                if (!sampled) continue;

                Vector3 pos = navHit.position;

                bool tooClose = false;
                foreach (var used in usedPositions)
                {
                    if (Vector3.Distance(pos, used) < MIN_DIST_BETWEEN_MOBS)
                    { tooClose = true; break; }
                }

                if (!tooClose) return pos;
            }

            return null;
        }

        private Vector3 SnapToNavMesh(Vector3 position)
        {
            if (NavMesh.SamplePosition(position, out NavMeshHit hit,
                                       NAVMESH_SAMPLE_RADIUS, NavMesh.AllAreas))
                return hit.position;

            if (NavMesh.SamplePosition(position, out hit,
                                       NAVMESH_SAMPLE_RADIUS_BIG, NavMesh.AllAreas))
                return hit.position;

            Debug.LogWarning($"[NetworkMonsterSpawner] Ponto {position} fora do NavMesh.");
            return position;
        }

        private bool ValidateGroup(SpawnGroup group)
        {
            if (group.monsterPrefab == null)
            {
                Debug.LogWarning($"[NetworkMonsterSpawner] '{group.groupLabel}': prefab nulo.");
                return false;
            }
            if (group.monsterPrefab.GetComponent<NetworkIdentity>() == null)
            {
                Debug.LogError($"[NetworkMonsterSpawner] '{group.monsterPrefab.name}' sem NetworkIdentity.");
                return false;
            }
            if (group.monsterPrefab.GetComponent<NetworkMonsterEntity>() == null)
            {
                Debug.LogError($"[NetworkMonsterSpawner] '{group.monsterPrefab.name}' sem NetworkMonsterEntity.");
                return false;
            }
            if (group.monsterPrefab.GetComponent<NavMeshAgent>() == null)
            {
                Debug.LogError($"[NetworkMonsterSpawner] '{group.monsterPrefab.name}' sem NavMeshAgent.");
                return false;
            }
            if (!group.useFixedPoints && group.spawnCount <= 0)
            {
                Debug.LogWarning($"[NetworkMonsterSpawner] '{group.groupLabel}': spawnCount = 0.");
                return false;
            }
            return true;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (spawnGroups == null) return;

            foreach (var group in spawnGroups)
            {
                if (group == null) continue;

                if (!group.useFixedPoints && group.zoneCenter != null)
                {
                    UnityEditor.Handles.color = new Color(0.2f, 0.5f, 1f, 0.15f);
                    UnityEditor.Handles.DrawSolidDisc(group.zoneCenter.position, Vector3.up, group.zoneRadius);

                    UnityEditor.Handles.color = new Color(0.2f, 0.5f, 1f, 0.8f);
                    UnityEditor.Handles.DrawWireDisc(group.zoneCenter.position, Vector3.up, group.zoneRadius);

                    UnityEditor.Handles.color = new Color(1f, 0.85f, 0f, 0.08f);
                    UnityEditor.Handles.DrawSolidDisc(group.zoneCenter.position, Vector3.up, group.patrolRadius);

                    UnityEditor.Handles.color = new Color(1f, 0.85f, 0f, 0.6f);
                    UnityEditor.Handles.DrawWireDisc(group.zoneCenter.position, Vector3.up, group.patrolRadius);

                    UnityEditor.Handles.Label(
                        group.zoneCenter.position + Vector3.up * 0.5f,
                        $"{group.groupLabel} ×{group.spawnCount}");
                }
                else if (group.useFixedPoints && group.fixedSpawnPoints != null)
                {
                    foreach (var pt in group.fixedSpawnPoints)
                    {
                        if (pt == null) continue;
                        Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.8f);
                        Gizmos.DrawSphere(pt.position, 0.4f);

                        if (group.patrolRadius > 0f)
                        {
                            UnityEditor.Handles.color = new Color(1f, 0.85f, 0f, 0.5f);
                            UnityEditor.Handles.DrawWireDisc(pt.position, Vector3.up, group.patrolRadius);
                        }

                        UnityEditor.Handles.Label(pt.position + Vector3.up * 0.6f, group.groupLabel);
                    }
                }
            }
        }
#endif
    }
}
