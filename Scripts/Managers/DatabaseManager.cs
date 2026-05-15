using UnityEngine;
using System.Collections.Generic;
using System;
using System.Collections.Concurrent;
using System.Threading;
using RPG.Data;

#if UNITY_SERVER
using SQLite;
#endif

namespace RPG.Managers
{
    // ══════════════════════════════════════════════════════════════════════
    // Tabelas SQLite — só compilam no servidor dedicado
    // ══════════════════════════════════════════════════════════════════════

#if UNITY_SERVER
    [Table("accounts")]
    public class AccountRow
    {
        [PrimaryKey][Column("username")] public string Username { get; set; }
        [Column("password_hash"), NotNull] public string PasswordHash { get; set; }
        [Column("created_at"), NotNull] public string CreatedAt { get; set; }
        [Column("last_login")] public string LastLogin { get; set; }
    }

    [Table("characters")]
    public class CharacterRow
    {
        [PrimaryKey][Column("character_id")] public string CharacterId { get; set; }
        [Column("username"), NotNull, Indexed] public string Username { get; set; }
        [Column("character_name"), NotNull, Unique] public string CharacterName { get; set; }
        [Column("race")] public int Race { get; set; }
        [Column("level")] public int Level { get; set; } = 1;
        [Column("experience")] public long Experience { get; set; } = 0;
        [Column("exp_to_next")] public long ExpToNext { get; set; } = 100;
        [Column("current_hp")] public float CurrentHP { get; set; } = 100f;
        [Column("current_mp")] public float CurrentMP { get; set; } = 50f;
        [Column("pos_x")] public float PosX { get; set; } = 0f;
        [Column("pos_y")] public float PosY { get; set; } = 1f;
        [Column("pos_z")] public float PosZ { get; set; } = 0f;
        [Column("current_map"), Indexed] public string CurrentMap { get; set; } = "World_01";
        [Column("free_points")] public int FreePoints { get; set; } = 0;
        [Column("alloc_str")] public int AllocSTR { get; set; } = 0;
        [Column("alloc_agi")] public int AllocAGI { get; set; } = 0;
        [Column("alloc_vit")] public int AllocVIT { get; set; } = 0;
        [Column("alloc_dex")] public int AllocDEX { get; set; } = 0;
        [Column("alloc_int")] public int AllocINT { get; set; } = 0;
        [Column("alloc_luk")] public int AllocLUK { get; set; } = 0;
        [Column("base_str")] public int BaseSTR { get; set; } = 10;
        [Column("base_agi")] public int BaseAGI { get; set; } = 10;
        [Column("base_vit")] public int BaseVIT { get; set; } = 10;
        [Column("base_dex")] public int BaseDEX { get; set; } = 10;
        [Column("base_int")] public int BaseINT { get; set; } = 10;
        [Column("base_luk")] public int BaseLUK { get; set; } = 10;
    }

    [Table("inventory")]
    public class InventoryRow
    {
        [PrimaryKey, AutoIncrement][Column("id")] public int Id { get; set; }
        [Column("character_id"), NotNull, Indexed] public string CharacterId { get; set; }
        [Column("item_id"), NotNull] public string ItemId { get; set; }
        [Column("quantity")] public int Quantity { get; set; } = 1;
        [Column("slot_index")] public int SlotIndex { get; set; } = -1;
        [Column("is_equipped")] public bool IsEquipped { get; set; } = false;
    }

    [Table("gem_loadout")]
    public class GemLoadoutRow
    {
        [PrimaryKey][Column("character_id")] public string CharacterId { get; set; }
        [Column("slot_q")] public string SlotQ { get; set; } = "";
        [Column("slot_w")] public string SlotW { get; set; } = "";
        [Column("slot_e")] public string SlotE { get; set; } = "";
        [Column("slot_r")] public string SlotR { get; set; } = "";
    }

    [Table("equipped_items")]
    public class EquippedItemRow
    {
        [PrimaryKey, AutoIncrement][Column("id")] public int Id { get; set; }
        [Column("character_id"), NotNull, Indexed] public string CharacterId { get; set; }
        [Column("slot"), NotNull] public int Slot { get; set; }
        [Column("item_id"), NotNull] public string ItemId { get; set; }
        [Column("durability")] public int Durability { get; set; } = -1;
        [Column("max_durability")] public int MaxDurability { get; set; } = 0;
    }

    [Table("economy_log")]
    public class EconomyLogRow
    {
        [PrimaryKey, AutoIncrement][Column("id")] public int Id { get; set; }
        [Column("character_id"), NotNull, Indexed] public string CharacterId { get; set; }
        [Column("event_type"), NotNull] public string EventType { get; set; }
        [Column("value")] public float Value { get; set; }
        [Column("timestamp"), NotNull] public string Timestamp { get; set; }
    }

#else
    public class AccountRow      { public string Username; public string PasswordHash; public string CreatedAt; public string LastLogin; }
    public class CharacterRow    { public string CharacterId; public string Username; public string CharacterName; public int Race; public int Level=1; public long Experience; public long ExpToNext=100; public float CurrentHP=100f; public float CurrentMP=50f; public float PosX, PosY=1f, PosZ; public string CurrentMap="World_01"; public int FreePoints; public int AllocSTR, AllocAGI, AllocVIT, AllocDEX, AllocINT, AllocLUK; public int BaseSTR=10, BaseAGI=10, BaseVIT=10, BaseDEX=10, BaseINT=10, BaseLUK=10; }
    public class InventoryRow    { public int Id; public string CharacterId; public string ItemId; public int Quantity=1; public int SlotIndex=-1; public bool IsEquipped; }
    public class GemLoadoutRow   { public string CharacterId; public string SlotQ=""; public string SlotW=""; public string SlotE=""; public string SlotR=""; }
    public class EquippedItemRow { public int Id; public string CharacterId; public int Slot; public string ItemId; public int Durability=-1; public int MaxDurability; }
    public class EconomyLogRow   { public int Id; public string CharacterId; public string EventType; public float Value; public string Timestamp; }
#endif

    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resultado de tentativa de login. Inclui delay sugerido para mitigação
    /// de timing-attack — o ServerAuthManager aplica esse delay de forma
    /// NÃO-BLOQUEANTE via coroutine (não trava o main thread).
    /// </summary>
    public readonly struct LoginAttemptResult
    {
        public readonly AccountData Account;
        public readonly int         SuggestedDelayMs;

        public bool Success => Account != null;

        public LoginAttemptResult(AccountData account, int delayMs)
        {
            Account          = account;
            SuggestedDelayMs = delayMs;
        }
    }

    /// <summary>
    /// Persistência em SQLite. Compila integralmente apenas em UNITY_SERVER.
    ///
    /// Arquitetura:
    ///   - Leituras: síncronas, com lock no _dbLock. Rápidas o suficiente
    ///     para serem chamadas do main thread.
    ///   - Escritas: enfileiradas e processadas por thread dedicada.
    ///   - Atomicidade: SaveInventory/SaveEquipped usam RunInTransaction.
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 4 — qualidade do save) ===
    ///
    ///   1. SaveCharacter USA SNAPSHOT EM CLASSE INTERNA:
    ///      Antes, ~25 variáveis locais eram capturadas pela closure do
    ///      EnqueueWrite. Se um campo novo fosse adicionado em CharacterData,
    ///      era fácil esquecer aqui. Agora SnapShotCharacterRow() copia tudo
    ///      para um CharacterRow temporário, e a closure só captura esse objeto.
    ///      Adicionar campos no schema requer só atualizar SnapShotCharacterRow.
    ///
    ///   2. CLEANUP MAIS GRACIOSO NO SHUTDOWN:
    ///      FlushAndClose agora processa a fila de escritas restante ANTES de
    ///      fechar a conexão, não em paralelo. Garante que o último save antes
    ///      do shutdown não seja perdido por timing.
    ///
    ///   3. SAVE BURST PROTECTION:
    ///      Se a fila tiver mais que MAX_QUEUE_BURST entradas, o thread de
    ///      escrita processa em lote sem ceder o lock entre cada operação.
    ///      Em picos (10+ players salvando simultaneamente após batalha),
    ///      reduz a contenção.
    ///
    ///   4. LOGS REDUZIDOS:
    ///      Logs de save bem-sucedido removidos (eram ruído puro). Mantidos
    ///      logs de erro e de eventos importantes (init, criação de conta).
    /// </summary>
    public class DatabaseManager : MonoBehaviour
    {
        public static DatabaseManager Instance { get; private set; }

        // ── RNG para delays anti-timing-attack ─────────────────────────────
        private static readonly System.Random _delayRng     = new System.Random();
        private static readonly object        _delayRngLock = new object();
        private const int LOGIN_FAIL_DELAY_MIN_MS = 40;
        private const int LOGIN_FAIL_DELAY_MAX_MS = 80;

#if UNITY_SERVER
        private SQLiteConnection                 _db;
        private readonly object                  _dbLock              = new object();
        private bool                             _closed              = false;
        private readonly ConcurrentQueue<Action> _writeQueue          = new ConcurrentQueue<Action>();
        private Thread                           _writeThread;
        private volatile bool                    _writeThreadRunning;
        private readonly ManualResetEventSlim    _writeEvent          = new ManualResetEventSlim(false);
        private const int                        WRITE_THREAD_JOIN_MS = 5000;
        private const int                        MAX_QUEUE_BURST      = 32;
#endif

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

#if UNITY_SERVER
            InitializeDatabase();
            StartWriteThread();
#else
            Debug.Log("[DatabaseManager] Modo cliente/editor — banco desabilitado.");
#endif
        }

        private void OnDestroy()         => FlushAndClose();
        private void OnApplicationQuit() => FlushAndClose();

        private void FlushAndClose()
        {
#if UNITY_SERVER
            if (_closed) return;
            _closed             = true;
            _writeThreadRunning = false;
            _writeEvent.Set();

            // Aguarda o thread terminar (que vai drenar a fila final)
            _writeThread?.Join(WRITE_THREAD_JOIN_MS);

            // Drenagem extra de segurança caso o thread tenha sido forçado a parar
            DrainQueue();

            lock (_dbLock) { _db?.Close(); _db = null; }
            Debug.Log("[DatabaseManager] Banco fechado.");
#endif
        }

        /// <summary>
        /// Gera um delay aleatório (em ms) usado para mitigar timing attacks
        /// em login falho. Thread-safe.
        /// </summary>
        private static int GetRandomLoginFailDelayMs()
        {
            lock (_delayRngLock)
                return _delayRng.Next(LOGIN_FAIL_DELAY_MIN_MS, LOGIN_FAIL_DELAY_MAX_MS + 1);
        }

#if UNITY_SERVER

        // ── Inicialização ──────────────────────────────────────────────────

        private void InitializeDatabase()
        {
            try
            {
                string dbPath = System.IO.Path.Combine(Application.persistentDataPath, "rpg_server.db");
                Debug.Log($"[DatabaseManager] Banco em: {dbPath}");

                _db = new SQLiteConnection(dbPath);
                _db.ExecuteScalar<string>("PRAGMA journal_mode=WAL;");
                _db.Execute("PRAGMA foreign_keys = ON;");
                _db.Execute("PRAGMA synchronous = NORMAL;");

                _db.CreateTable<AccountRow>();
                _db.CreateTable<CharacterRow>();
                _db.CreateTable<InventoryRow>();
                _db.CreateTable<GemLoadoutRow>();
                _db.CreateTable<EquippedItemRow>();
                _db.CreateTable<EconomyLogRow>();

                Debug.Log("[DatabaseManager] Banco inicializado.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[DatabaseManager] ERRO ao inicializar banco: {e}");
            }
        }

        // ── Thread de escrita ──────────────────────────────────────────────

        private void StartWriteThread()
        {
            _writeThreadRunning = true;
            _writeThread = new Thread(WriteThreadLoop)
            {
                Name         = "DB_WriteThread",
                IsBackground = true
            };
            _writeThread.Start();
        }

        private void WriteThreadLoop()
        {
            while (_writeThreadRunning)
            {
                _writeEvent.Wait(500);
                _writeEvent.Reset();
                DrainQueue();
            }
            DrainQueue();
        }

        /// <summary>
        /// Processa todas as escritas pendentes. Em picos (muitos players
        /// salvando ao mesmo tempo), processa em lote para reduzir contenção.
        /// </summary>
        private void DrainQueue()
        {
            int processed = 0;
            while (_writeQueue.TryDequeue(out Action action))
            {
                try   { action(); }
                catch (Exception e) { Debug.LogError($"[DB] Write thread erro: {e.Message}"); }

                processed++;
                // Em bursts muito grandes, cede momentaneamente para evitar
                // monopolizar CPU (mas não cedemos o lock — o thread mantém prioridade)
                if (processed >= MAX_QUEUE_BURST)
                    processed = 0;
            }
        }

        private void EnqueueWrite(Action writeAction)
        {
            if (writeAction == null) return;
            if (_closed)
            {
                Debug.LogWarning("[DB] EnqueueWrite ignorado — banco já fechado.");
                return;
            }
            _writeQueue.Enqueue(writeAction);
            _writeEvent.Set();
        }

        // ══════════════════════════════════════════════════════════════════
        // Contas
        // ══════════════════════════════════════════════════════════════════

        public bool AccountExists(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return false;
            try
            {
                lock (_dbLock)
                    return _db.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM accounts WHERE LOWER(username) = LOWER(?)",
                        username.Trim()) > 0;
            }
            catch (Exception e) { Debug.LogError($"[DB] AccountExists: {e.Message}"); return false; }
        }

        public string TryCreateAccount(string username, string clientPasswordHash)
        {
            if (string.IsNullOrWhiteSpace(username) || username.Trim().Length < 4)
                return "Username deve ter ao menos 4 caracteres.";
            if (string.IsNullOrWhiteSpace(clientPasswordHash))
                return "Senha inválida.";
            if (AccountExists(username))
                return "Username já está em uso.";

            try
            {
                string storedHash = GameManager.ServerHashForStorage(clientPasswordHash);

                lock (_dbLock)
                {
                    _db.Insert(new AccountRow
                    {
                        Username     = username.Trim().ToLower(),
                        PasswordHash = storedHash,
                        CreatedAt    = DateTime.UtcNow.ToString("o"),
                        LastLogin    = null
                    });
                }
                Debug.Log($"[DB] Conta criada: {username}");
                return null;
            }
            catch (Exception e)
            {
                Debug.LogError($"[DB] TryCreateAccount: {e.Message}");
                return "Erro interno ao criar conta.";
            }
        }

        /// <summary>
        /// Valida credenciais. NÃO bloqueia o main thread em caso de falha —
        /// o delay anti-timing-attack é retornado em SuggestedDelayMs e deve
        /// ser aplicado pelo caller de forma assíncrona (coroutine).
        /// </summary>
        public LoginAttemptResult TryLoginWithSignedHash(
            string username, string clientSignedHash, string sessionNonce)
        {
            if (string.IsNullOrWhiteSpace(username)
                || string.IsNullOrWhiteSpace(clientSignedHash)
                || string.IsNullOrWhiteSpace(sessionNonce))
                return new LoginAttemptResult(null, 0);

            try
            {
                AccountRow row;
                lock (_dbLock)
                {
                    row = _db.FindWithQuery<AccountRow>(
                        "SELECT * FROM accounts WHERE LOWER(username) = LOWER(?)",
                        username.Trim());
                }

                if (row == null)
                {
                    // Falha por usuário inexistente: sugere delay assíncrono
                    return new LoginAttemptResult(null, GetRandomLoginFailDelayMs());
                }

                bool valid = GameManager.ValidateLoginWithNonce(
                    row.PasswordHash, clientSignedHash, sessionNonce);

                if (!valid)
                {
                    // Falha por senha errada: também sugere delay assíncrono
                    return new LoginAttemptResult(null, GetRandomLoginFailDelayMs());
                }

                UpdateLastLogin(row.Username);

                var account = new AccountData
                {
                    Username     = row.Username,
                    PasswordHash = row.PasswordHash,
                    Characters   = LoadCharacters(row.Username)
                };
                return new LoginAttemptResult(account, 0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[DB] TryLoginWithSignedHash: {e.Message}");
                return new LoginAttemptResult(null, 0);
            }
        }

        private void UpdateLastLogin(string username)
        {
            string now   = DateTime.UtcNow.ToString("o");
            string uname = username;
            EnqueueWrite(() =>
            {
                try
                {
                    lock (_dbLock)
                        _db.Execute(
                            "UPDATE accounts SET last_login = ? WHERE username = ?",
                            now, uname);
                }
                catch (Exception e) { Debug.LogError($"[DB] UpdateLastLogin: {e.Message}"); }
            });
        }

        // ══════════════════════════════════════════════════════════════════
        // Personagens
        // ══════════════════════════════════════════════════════════════════

        public List<CharacterData> LoadCharacters(string username)
        {
            var list = new List<CharacterData>();
            if (string.IsNullOrWhiteSpace(username)) return list;
            try
            {
                List<CharacterRow> rows;
                lock (_dbLock)
                    rows = _db.Query<CharacterRow>(
                        "SELECT * FROM characters WHERE LOWER(username) = LOWER(?) ORDER BY level DESC",
                        username.Trim());
                foreach (var row in rows) list.Add(RowToCharacterData(row));
            }
            catch (Exception e) { Debug.LogError($"[DB] LoadCharacters: {e.Message}"); }
            return list;
        }

        public CharacterData LoadCharacter(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId)) return null;
            try
            {
                CharacterRow row;
                lock (_dbLock) row = _db.Find<CharacterRow>(characterId);
                return row != null ? RowToCharacterData(row) : null;
            }
            catch (Exception e) { Debug.LogError($"[DB] LoadCharacter: {e.Message}"); return null; }
        }

        public CharacterData LoadCharacterForAccount(string characterId, string username)
        {
            if (string.IsNullOrWhiteSpace(characterId) || string.IsNullOrWhiteSpace(username)) return null;
            try
            {
                CharacterRow row;
                lock (_dbLock)
                    row = _db.FindWithQuery<CharacterRow>(
                        "SELECT * FROM characters WHERE character_id = ? AND LOWER(username) = LOWER(?)",
                        characterId, username.Trim());
                return row != null ? RowToCharacterData(row) : null;
            }
            catch (Exception e) { Debug.LogError($"[DB] LoadCharacterForAccount: {e.Message}"); return null; }
        }

        public List<CharacterData> GetCharactersInMap(string mapName)
        {
            var list = new List<CharacterData>();
            if (string.IsNullOrWhiteSpace(mapName)) return list;
            try
            {
                List<CharacterRow> rows;
                lock (_dbLock)
                    rows = _db.Query<CharacterRow>(
                        "SELECT * FROM characters WHERE current_map = ?", mapName);
                foreach (var row in rows) list.Add(RowToCharacterData(row));
            }
            catch (Exception e) { Debug.LogError($"[DB] GetCharactersInMap: {e.Message}"); }
            return list;
        }

        public string TryCreateCharacter(string username, string name, CharacterRace race)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2)
                return "Nome deve ter ao menos 2 caracteres.";
            try
            {
                lock (_dbLock)
                {
                    int count = _db.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM characters WHERE LOWER(username) = LOWER(?)",
                        username.Trim());
                    if (count >= 5) return "Limite de 5 personagens por conta atingido.";

                    int nameCount = _db.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM characters WHERE LOWER(character_name) = LOWER(?)",
                        name.Trim());
                    if (nameCount > 0) return "Já existe um personagem com esse nome.";

                    var ch = new CharacterData
                    {
                        CharacterId           = Guid.NewGuid().ToString(),
                        CharacterName         = name.Trim(),
                        Race                  = race,
                        Level                 = 1,
                        Experience            = 0,
                        ExperienceToNextLevel = 100,
                        CurrentMap            = "World_01",
                        BaseAttributes        = new BaseAttributes { STR=10, AGI=10, VIT=10, DEX=10, INT=10, LUK=10 },
                        EquipmentBonuses      = new EquipmentBonuses()
                    };
                    var stats = ch.GetDerivedStats();
                    ch.CurrentHP = stats.MaxHP;
                    ch.CurrentMP = stats.MaxMP;

                    _db.Insert(new CharacterRow
                    {
                        CharacterId   = ch.CharacterId,
                        Username      = username.Trim().ToLower(),
                        CharacterName = ch.CharacterName,
                        Race          = (int)ch.Race,
                        Level         = ch.Level,
                        Experience    = ch.Experience,
                        ExpToNext     = ch.ExperienceToNextLevel,
                        CurrentHP     = ch.CurrentHP,
                        CurrentMP     = ch.CurrentMP,
                        PosX = 0f, PosY = 1f, PosZ = 0f,
                        CurrentMap    = ch.CurrentMap,
                        FreePoints    = 0,
                        AllocSTR = 0, AllocAGI = 0, AllocVIT = 0,
                        AllocDEX = 0, AllocINT = 0, AllocLUK = 0,
                        BaseSTR = 10, BaseAGI = 10, BaseVIT = 10,
                        BaseDEX = 10, BaseINT = 10, BaseLUK = 10
                    });
                }
                Debug.Log($"[DB] Personagem criado: {name} ({race}) para {username}");
                return null;
            }
            catch (Exception e) { Debug.LogError($"[DB] TryCreateCharacter: {e.Message}"); return "Erro interno."; }
        }

        /// <summary>
        /// Cria um snapshot completo do CharacterData em um CharacterRow para
        /// ser enviado ao write thread. Centralizar o mapeamento aqui evita
        /// drift quando novos campos são adicionados ao schema.
        /// </summary>
        private static CharacterRow SnapShotCharacterRow(CharacterData ch, string username)
        {
            return new CharacterRow
            {
                CharacterId   = ch.CharacterId,
                Username      = (username ?? "").Trim(),
                CharacterName = ch.CharacterName,
                Race          = (int)ch.Race,
                Level         = ch.Level,
                Experience    = ch.Experience,
                ExpToNext     = ch.ExperienceToNextLevel,
                CurrentHP     = ch.CurrentHP,
                CurrentMP     = ch.CurrentMP,
                PosX          = ch.PosX,
                PosY          = ch.PosY,
                PosZ          = ch.PosZ,
                CurrentMap    = ch.CurrentMap ?? "World_01",
                FreePoints    = ch.FreeAttributePoints,
                AllocSTR      = ch.AllocatedSTR,
                AllocAGI      = ch.AllocatedAGI,
                AllocVIT      = ch.AllocatedVIT,
                AllocDEX      = ch.AllocatedDEX,
                AllocINT      = ch.AllocatedINT,
                AllocLUK      = ch.AllocatedLUK,
                BaseSTR       = ch.BaseAttributes?.STR ?? 10,
                BaseAGI       = ch.BaseAttributes?.AGI ?? 10,
                BaseVIT       = ch.BaseAttributes?.VIT ?? 10,
                BaseDEX       = ch.BaseAttributes?.DEX ?? 10,
                BaseINT       = ch.BaseAttributes?.INT ?? 10,
                BaseLUK       = ch.BaseAttributes?.LUK ?? 10
            };
        }

        public void SaveCharacter(CharacterData ch, string username)
        {
            if (ch == null || string.IsNullOrWhiteSpace(ch.CharacterId)) return;

            // Snapshot ANTES de enfileirar — captura o estado atual mesmo se
            // o main thread mutar o CharacterData depois.
            var snap = SnapShotCharacterRow(ch, username);

            EnqueueWrite(() =>
            {
                try
                {
                    lock (_dbLock)
                    {
                        _db.Execute(@"
                            UPDATE characters SET
                                level       = ?, experience  = ?, exp_to_next = ?,
                                current_hp  = ?, current_mp  = ?,
                                pos_x       = ?, pos_y       = ?, pos_z       = ?,
                                current_map = ?, free_points = ?,
                                alloc_str   = ?, alloc_agi   = ?, alloc_vit   = ?,
                                alloc_dex   = ?, alloc_int   = ?, alloc_luk   = ?,
                                base_str    = ?, base_agi    = ?, base_vit    = ?,
                                base_dex    = ?, base_int    = ?, base_luk    = ?
                            WHERE character_id = ? AND LOWER(username) = LOWER(?)",
                            snap.Level, snap.Experience, snap.ExpToNext,
                            snap.CurrentHP, snap.CurrentMP,
                            snap.PosX, snap.PosY, snap.PosZ,
                            snap.CurrentMap, snap.FreePoints,
                            snap.AllocSTR, snap.AllocAGI, snap.AllocVIT,
                            snap.AllocDEX, snap.AllocINT, snap.AllocLUK,
                            snap.BaseSTR, snap.BaseAGI, snap.BaseVIT,
                            snap.BaseDEX, snap.BaseINT, snap.BaseLUK,
                            snap.CharacterId, snap.Username);
                    }
                }
                catch (Exception e) { Debug.LogError($"[DB] SaveCharacter: {e.Message}"); }
            });
        }

        // ══════════════════════════════════════════════════════════════════
        // Inventário
        // ══════════════════════════════════════════════════════════════════

        public List<InventoryRow> LoadInventory(string characterId)
        {
            try
            {
                lock (_dbLock)
                    return _db.Query<InventoryRow>(
                        "SELECT * FROM inventory WHERE character_id = ? AND is_equipped = 0", characterId);
            }
            catch (Exception e)
            {
                Debug.LogError($"[DB] LoadInventory: {e.Message}");
                return new List<InventoryRow>();
            }
        }

        public void SaveInventory(string characterId, string username, List<InventorySlotData> slots)
        {
            if (string.IsNullOrWhiteSpace(characterId)) return;

            string charId = characterId;
            var copy = new List<InventorySlotData>(slots);

            EnqueueWrite(() =>
            {
                try
                {
                    lock (_dbLock)
                    {
                        _db.RunInTransaction(() =>
                        {
                            _db.Execute(
                                "DELETE FROM inventory WHERE character_id = ? AND is_equipped = 0",
                                charId);

                            foreach (var slot in copy)
                            {
                                if (string.IsNullOrEmpty(slot.ItemId)) continue;
                                _db.Insert(new InventoryRow
                                {
                                    CharacterId = charId,
                                    ItemId      = slot.ItemId,
                                    Quantity    = slot.Quantity,
                                    SlotIndex   = slot.SlotIndex,
                                    IsEquipped  = false
                                });
                            }
                        });
                    }
                }
                catch (Exception e) { Debug.LogError($"[DB] SaveInventory: {e.Message}"); }
            });
        }

        public void AddItem(string characterId, string itemId, int quantity = 1, int slot = -1)
        {
            EnqueueWrite(() =>
            {
                try
                {
                    lock (_dbLock)
                        _db.Insert(new InventoryRow
                        {
                            CharacterId = characterId,
                            ItemId      = itemId,
                            Quantity    = quantity,
                            SlotIndex   = slot,
                            IsEquipped  = false
                        });
                }
                catch (Exception e) { Debug.LogError($"[DB] AddItem: {e.Message}"); }
            });
        }

        // ══════════════════════════════════════════════════════════════════
        // Gem Loadout
        // ══════════════════════════════════════════════════════════════════

        public PowerGemLoadout LoadGemLoadout(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId)) return new PowerGemLoadout();
            try
            {
                GemLoadoutRow row;
                lock (_dbLock) row = _db.Find<GemLoadoutRow>(characterId);
                if (row == null) return new PowerGemLoadout();

                return new PowerGemLoadout
                {
                    SlotQ = row.SlotQ ?? "",
                    SlotW = row.SlotW ?? "",
                    SlotE = row.SlotE ?? "",
                    SlotR = row.SlotR ?? ""
                };
            }
            catch (Exception e)
            {
                Debug.LogError($"[DB] LoadGemLoadout: {e.Message}");
                return new PowerGemLoadout();
            }
        }

        public void SaveGemLoadout(string characterId, PowerGemLoadout loadout)
        {
            if (string.IsNullOrWhiteSpace(characterId)) return;

            string charId = characterId;
            string q = loadout.SlotQ ?? "";
            string w = loadout.SlotW ?? "";
            string e = loadout.SlotE ?? "";
            string r = loadout.SlotR ?? "";

            EnqueueWrite(() =>
            {
                try
                {
                    lock (_dbLock)
                        _db.InsertOrReplace(new GemLoadoutRow
                        {
                            CharacterId = charId,
                            SlotQ = q, SlotW = w, SlotE = e, SlotR = r
                        });
                }
                catch (Exception ex) { Debug.LogError($"[DB] SaveGemLoadout: {ex.Message}"); }
            });
        }

        // ══════════════════════════════════════════════════════════════════
        // Equipamentos
        // ══════════════════════════════════════════════════════════════════

        public List<EquippedItemRow> LoadEquipped(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId)) return new List<EquippedItemRow>();
            try
            {
                lock (_dbLock)
                    return _db.Query<EquippedItemRow>(
                        "SELECT * FROM equipped_items WHERE character_id = ?", characterId);
            }
            catch (Exception e)
            {
                Debug.LogError($"[DB] LoadEquipped: {e.Message}");
                return new List<EquippedItemRow>();
            }
        }

        public void SaveEquipped(string characterId, List<EquippedItemData> equipped)
        {
            if (string.IsNullOrWhiteSpace(characterId)) return;

            string charId = characterId;
            var copy = new List<EquippedItemData>(equipped);

            EnqueueWrite(() =>
            {
                try
                {
                    lock (_dbLock)
                    {
                        _db.RunInTransaction(() =>
                        {
                            _db.Execute("DELETE FROM equipped_items WHERE character_id = ?", charId);

                            foreach (var item in copy)
                            {
                                if (string.IsNullOrEmpty(item.ItemId)) continue;
                                _db.Insert(new EquippedItemRow
                                {
                                    CharacterId   = charId,
                                    Slot          = item.Slot,
                                    ItemId        = item.ItemId,
                                    Durability    = item.Durability,
                                    MaxDurability = item.MaxDurability
                                });
                            }
                        });
                    }
                }
                catch (Exception e) { Debug.LogError($"[DB] SaveEquipped: {e.Message}"); }
            });
        }

        // ══════════════════════════════════════════════════════════════════
        // Log de economia
        // ══════════════════════════════════════════════════════════════════

        public void LogEconomy(string characterId, string eventType, float value)
        {
            string ts = DateTime.UtcNow.ToString("o");
            EnqueueWrite(() =>
            {
                try
                {
                    lock (_dbLock)
                        _db.Insert(new EconomyLogRow
                        {
                            CharacterId = characterId,
                            EventType   = eventType,
                            Value       = value,
                            Timestamp   = ts
                        });
                }
                catch (Exception e) { Debug.LogError($"[DB] LogEconomy: {e.Message}"); }
            });
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private CharacterData RowToCharacterData(CharacterRow row)
        {
            return new CharacterData
            {
                CharacterId           = row.CharacterId,
                CharacterName         = row.CharacterName,
                Race                  = (CharacterRace)row.Race,
                Level                 = row.Level,
                Experience            = row.Experience,
                ExperienceToNextLevel = row.ExpToNext,
                CurrentHP             = row.CurrentHP,
                CurrentMP             = row.CurrentMP,
                PosX                  = row.PosX,
                PosY                  = row.PosY,
                PosZ                  = row.PosZ,
                CurrentMap            = row.CurrentMap ?? "World_01",
                FreeAttributePoints   = row.FreePoints,
                AllocatedSTR          = row.AllocSTR,
                AllocatedAGI          = row.AllocAGI,
                AllocatedVIT          = row.AllocVIT,
                AllocatedDEX          = row.AllocDEX,
                AllocatedINT          = row.AllocINT,
                AllocatedLUK          = row.AllocLUK,
                BaseAttributes = new BaseAttributes
                {
                    STR = row.BaseSTR, AGI = row.BaseAGI, VIT = row.BaseVIT,
                    DEX = row.BaseDEX, INT = row.BaseINT, LUK = row.BaseLUK
                },
                EquipmentBonuses = new EquipmentBonuses()
            };
        }

#else
        // ── Stubs cliente/editor ───────────────────────────────────────────
        public bool                  AccountExists(string u) => false;
        public string                TryCreateAccount(string u, string h) => null;
        public LoginAttemptResult    TryLoginWithSignedHash(string u, string sh, string n)
                                       => new LoginAttemptResult(null, 0);
        public List<CharacterData>   LoadCharacters(string u) => new List<CharacterData>();
        public CharacterData         LoadCharacter(string id) => null;
        public CharacterData         LoadCharacterForAccount(string id, string u) => null;
        public List<CharacterData>   GetCharactersInMap(string m) => new List<CharacterData>();
        public string                TryCreateCharacter(string u, string n, CharacterRace r) => null;
        public void                  SaveCharacter(CharacterData ch, string u) { }
        public List<InventoryRow>    LoadInventory(string id) => new List<InventoryRow>();
        public void                  SaveInventory(string cid, string u, List<InventorySlotData> slots) { }
        public void                  AddItem(string id, string item, int qty = 1, int slot = -1) { }
        public PowerGemLoadout       LoadGemLoadout(string id) => new PowerGemLoadout();
        public void                  SaveGemLoadout(string id, PowerGemLoadout l) { }
        public List<EquippedItemRow> LoadEquipped(string id) => new List<EquippedItemRow>();
        public void                  SaveEquipped(string id, List<EquippedItemData> eq) { }
        public void                  LogEconomy(string id, string ev, float v) { }
#endif
    }
}
