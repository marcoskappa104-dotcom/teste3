using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Managers;
using RPG.Data;

namespace RPG.Network
{
    /// <summary>
    /// NetworkManager especializado para o RPG.
    ///
    /// Responsabilidades:
    ///   - Inicializa ServerAuthManager e registra handlers.
    ///   - Aguarda MsgClientSceneReady antes de spawnar o player (garante NavMesh pronto).
    ///   - Mantém spawn points por raça.
    ///   - Registra prefabs de monstro/itens uma única vez.
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 3 — robustez) ===
    ///
    ///   1. SCENE CHANGE LIMPA PENDING SPAWNS:
    ///      Antes, OnServerSceneChanged só re-registrava prefabs. Mas se a
    ///      cena mudou enquanto havia _pendingSpawns, eles ficavam apontando
    ///      para connectionIds de uma cena anterior. Agora limpamos.
    ///
    ///   2. VALIDAÇÃO DE PREFAB COM AVISO ESPECÍFICO:
    ///      RegisterSpawnablePrefabs agora loga o nome do prefab problemático
    ///      em vez de uma mensagem genérica — facilita debug.
    ///
    ///   3. CLEANUP DE COROUTINES PENDENTES NO SERVER STOP:
    ///      Antes, se o servidor parasse com spawn coroutines ativas, elas
    ///      ficavam tentando spawnar contra um NetworkServer fechado. Agora
    ///      OnStopServer cancela todas.
    /// </summary>
    public class RPGNetworkManager : NetworkManager
    {
        public static new RPGNetworkManager singleton =>
            NetworkManager.singleton as RPGNetworkManager;

        private const float SPAWN_NAVMESH_RADIUS    = 15f;
        private const float SPAWN_NAVMESH_TIMEOUT   = 5f;
        private const float PENDING_SPAWN_TIMEOUT   = 30f;
        private const float CLEANUP_PENDING_SPAWN_S = 5f;

        private static readonly Dictionary<CharacterRace, Vector3> RaceSpawnPoints = new()
        {
            { CharacterRace.Human,  new Vector3(   0f, 1f,   0f) },
            { CharacterRace.Elf,    new Vector3(  20f, 1f,  10f) },
            { CharacterRace.Dwarf,  new Vector3( -20f, 1f,  10f) },
            { CharacterRace.Orc,    new Vector3(   0f, 1f,  30f) },
            { CharacterRace.Undead, new Vector3( -20f, 1f, -10f) },
        };

        [Header("Spawnable Prefabs")]
        [Tooltip("Prefabs de monstros e itens (precisam ter NetworkIdentity).")]
        [SerializeField] private List<GameObject> spawnablePrefabs = new List<GameObject>();

        private struct PendingSpawn
        {
            public NetworkConnectionToClient Conn;
            public CharacterData             CharData;
            public string                    AccountUsername;
            public float                     ExpiresAt;
        }

        private readonly Dictionary<int, PendingSpawn> _pendingSpawns   = new();
        private readonly Dictionary<int, Coroutine>    _spawnCoroutines = new();
        private Coroutine _cleanupCoroutine;

        private bool              _prefabsRegistered;
        private ServerAuthManager _authManager;

        // ══════════════════════════════════════════════════════════════════
        // Lifecycle
        // ══════════════════════════════════════════════════════════════════

        public override void Start()
        {
            base.Start();
            RegisterSpawnablePrefabs();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            if (playerPrefab == null)
                Debug.LogError("[RPGNetworkManager] playerPrefab não configurado!");

            _authManager = GetComponent<ServerAuthManager>();
            if (_authManager == null)
                _authManager = gameObject.AddComponent<ServerAuthManager>();

            _authManager.RegisterHandlers();

            NetworkServer.RegisterHandler<MsgClientSceneReady>(OnClientSceneReady, false);

            _cleanupCoroutine = StartCoroutine(CleanExpiredPendingSpawns());
        }

        public override void OnStopServer()
        {
            // Cancela TODAS as spawn coroutines pendentes — sem isso, podem
            // tentar AddPlayerForConnection contra um NetworkServer já fechado.
            foreach (var kv in _spawnCoroutines)
                if (kv.Value != null) StopCoroutine(kv.Value);
            _spawnCoroutines.Clear();
            _pendingSpawns.Clear();

            if (_cleanupCoroutine != null)
            {
                StopCoroutine(_cleanupCoroutine);
                _cleanupCoroutine = null;
            }

            base.OnStopServer();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            if (!_prefabsRegistered)
                RegisterSpawnablePrefabs();
        }

        public override void OnServerSceneChanged(string sceneName)
        {
            base.OnServerSceneChanged(sceneName);

            // Limpa estado pendente — connectionIds anteriores não são mais válidos
            foreach (var kv in _spawnCoroutines)
                if (kv.Value != null) StopCoroutine(kv.Value);
            _spawnCoroutines.Clear();
            _pendingSpawns.Clear();

            // Re-registra ao trocar de cena (novas dungeons podem ter prefabs únicos)
            _prefabsRegistered = false;
            RegisterSpawnablePrefabs();
        }

        // ══════════════════════════════════════════════════════════════════
        // Conexões
        // ══════════════════════════════════════════════════════════════════

        public override void OnServerConnect(NetworkConnectionToClient conn)
        {
            base.OnServerConnect(conn);
            _authManager?.OnServerConnect(conn);
        }

        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            _pendingSpawns.Remove(conn.connectionId);

            if (_spawnCoroutines.TryGetValue(conn.connectionId, out var coroutine))
            {
                if (coroutine != null) StopCoroutine(coroutine);
                _spawnCoroutines.Remove(conn.connectionId);
            }

            _authManager?.OnServerDisconnect(conn);
            base.OnServerDisconnect(conn);
        }

        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            // Spawn é controlado por ServerAuthManager via SpawnPlayerForConnection
        }

        public override void OnClientConnect()
        {
            base.OnClientConnect();
        }

        public override void OnClientDisconnect()
        {
            base.OnClientDisconnect();
            ClientAuthHandler.Instance?.OnDisconnectedFromServer();
        }

        // ══════════════════════════════════════════════════════════════════
        // Spawn do player
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public void SpawnPlayerForConnection(
            NetworkConnectionToClient conn,
            CharacterData charData,
            string accountUsername)
        {
            if (playerPrefab == null)
            {
                Debug.LogError("[RPGNetworkManager] playerPrefab nulo no spawn.");
                conn.Send(new MsgSelectCharacterResponse
                {
                    Success = false,
                    Error   = "Erro interno do servidor."
                });
                return;
            }

            _pendingSpawns[conn.connectionId] = new PendingSpawn
            {
                Conn            = conn,
                CharData        = charData,
                AccountUsername = accountUsername,
                ExpiresAt       = Time.time + PENDING_SPAWN_TIMEOUT
            };

            conn.Send(new MsgSelectCharacterResponse { Success = true });
        }

        [Server]
        private void OnClientSceneReady(NetworkConnectionToClient conn, MsgClientSceneReady msg)
        {
            if (!_pendingSpawns.TryGetValue(conn.connectionId, out var pending)) return;

            if (Time.time > pending.ExpiresAt)
            {
                Debug.LogWarning($"[RPGNetworkManager] Spawn expirado: {pending.CharData?.CharacterName}");
                _pendingSpawns.Remove(conn.connectionId);
                return;
            }

            _pendingSpawns.Remove(conn.connectionId);

            var coroutine = StartCoroutine(DoSpawnPlayer(conn, pending.CharData, pending.AccountUsername));
            _spawnCoroutines[conn.connectionId] = coroutine;
        }

        [Server]
        private IEnumerator DoSpawnPlayer(
            NetworkConnectionToClient conn,
            CharacterData charData,
            string accountUsername)
        {
            int connId = conn?.connectionId ?? -1;

            if (conn == null || !conn.isReady)
            {
                _spawnCoroutines.Remove(connId);
                yield break;
            }

            Vector3 spawnPos = GetSpawnPositionForRace(charData.Race, charData);

            // Espera o NavMesh disponibilizar a posição
            float elapsed = 0f;
            while (elapsed < SPAWN_NAVMESH_TIMEOUT)
            {
                if (NavMesh.SamplePosition(spawnPos, out NavMeshHit hit, SPAWN_NAVMESH_RADIUS, NavMesh.AllAreas))
                {
                    spawnPos = hit.position;
                    break;
                }
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (conn == null || !conn.isReady || !NetworkServer.active)
            {
                _spawnCoroutines.Remove(connId);
                yield break;
            }

            var playerGO = Instantiate(playerPrefab, spawnPos, Quaternion.identity);
            NetworkServer.AddPlayerForConnection(conn, playerGO);

            var netPlayer = playerGO.GetComponent<NetworkPlayer>();
            if (netPlayer != null)
                netPlayer.ServerInitialize(charData, accountUsername);
            else
                Debug.LogError("[RPGNetworkManager] playerPrefab não tem NetworkPlayer.");

            _spawnCoroutines.Remove(connId);

            Debug.Log($"[Server] Spawnado: {charData.CharacterName} ({charData.Race}) | connId={connId}");
        }

        [Server]
        private IEnumerator CleanExpiredPendingSpawns()
        {
            var wait = new WaitForSeconds(CLEANUP_PENDING_SPAWN_S);
            var toRemove = new List<int>();

            while (true)
            {
                yield return wait;

                toRemove.Clear();
                foreach (var kv in _pendingSpawns)
                {
                    if (Time.time > kv.Value.ExpiresAt)
                        toRemove.Add(kv.Key);
                }
                foreach (var id in toRemove)
                    _pendingSpawns.Remove(id);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Spawn points
        // ══════════════════════════════════════════════════════════════════

        public Vector3 GetSpawnPositionForRace(CharacterRace race, CharacterData charData)
        {
            var saved = new Vector3(charData.PosX, charData.PosY, charData.PosZ);
            if (saved.sqrMagnitude > 0.01f &&
                NavMesh.SamplePosition(saved, out NavMeshHit savedHit, 5f, NavMesh.AllAreas))
            {
                return savedHit.position;
            }

            if (RaceSpawnPoints.TryGetValue(race, out Vector3 racePos))
                return racePos;

            Debug.LogWarning($"[RPGNetworkManager] Raça {race} sem spawn point. Usando origem.");
            return Vector3.zero;
        }

        // ══════════════════════════════════════════════════════════════════
        // Registro de prefabs
        // ══════════════════════════════════════════════════════════════════

        private void RegisterSpawnablePrefabs()
        {
            if (_prefabsRegistered) return;

            int registered = 0;
            int skipped    = 0;

            foreach (var prefab in spawnablePrefabs)
            {
                if (prefab == null)
                {
                    skipped++;
                    continue;
                }

                var identity = prefab.GetComponent<NetworkIdentity>();
                if (identity == null)
                {
                    Debug.LogError($"[RPGNetworkManager] '{prefab.name}' sem NetworkIdentity — ignorado.");
                    continue;
                }
                if (!NetworkClient.prefabs.ContainsKey(identity.assetId))
                {
                    NetworkClient.RegisterPrefab(prefab);
                    registered++;
                }
            }

            _prefabsRegistered = true;
            if (registered > 0)
                Debug.Log($"[RPGNetworkManager] {registered} prefabs registrados.");
            if (skipped > 0)
                Debug.LogWarning($"[RPGNetworkManager] {skipped} entradas nulas em spawnablePrefabs.");
        }
    }
}
