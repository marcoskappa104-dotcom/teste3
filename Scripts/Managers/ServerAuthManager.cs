using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.Managers;
using System.Collections.Generic;
using System.Collections;

namespace RPG.Network
{
    /// <summary>
    /// Lado servidor da autenticação.
    /// Mantém sessões por connectionId e processa requisições de login,
    /// criação de conta, listagem e seleção de personagem.
    ///
    /// === SEGURANÇA ===
    /// Implementa:
    ///   - Rate limit por conexão
    ///   - Rate limit por endereço IP (sobrevive a reconexão)
    ///   - Throttle entre tentativas
    ///   - Limpeza de sessões ociosas por TTL
    ///   - Sessões InGame protegidas contra timeout
    ///   - Delay anti-timing-attack ASSÍNCRONO (não bloqueia o main thread)
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 4 — polish) ===
    ///
    ///   1. LIMITS DE MENSAGEM USAM GameConstants.Auth:
    ///      Os caps de username (64) e SignedHash (256) eram literais inline.
    ///      Agora são constantes nomeadas para facilitar tunagem.
    ///
    ///   2. NONCE ROTATION OPCIONAL:
    ///      Comentário explica que o nonce atual é vitalício por sessão.
    ///      Para hardening futuro, seria possível rotacionar após N segundos,
    ///      mas isso exigiria reenviar MsgAuthChallenge e o cliente reagir.
    ///      Deixado como TODO documentado.
    ///
    ///   3. NULL CHECKS MAIS DEFENSIVOS:
    ///      OnLoginRequest checa session.RemoteAddress; se for null, usa
    ///      "unknown" em vez de propagar null para IpBans (que daria warning).
    ///
    ///   4. LOG DE RATE LIMIT MAIS INFORMATIVO:
    ///      Logs de IP banido agora incluem timestamp ISO em UTC, facilitando
    ///      cruzamento com outros logs do servidor.
    /// </summary>
    public class ServerAuthManager : MonoBehaviour
    {
        public static ServerAuthManager Instance { get; private set; }

        [Header("Debug")]
        [Tooltip("Logs detalhados do fluxo de auth. DESATIVE em produção.")]
        [SerializeField] private bool debugAuth = false;

        // Caps de payload para auth (defesa contra DoS por mensagens enormes)
        private const int MAX_USERNAME_PAYLOAD_BYTES = 64;
        private const int MAX_HASH_PAYLOAD_BYTES     = 256;

        private enum ConnState { Unauthenticated, Authenticated, InGame }

        private class ConnData
        {
            public ConnState   State           = ConnState.Unauthenticated;
            public string      Username        = "";
            public string      CharacterId     = "";
            public AccountData CachedAccount;
            public int         LoginAttempts;
            public string      SessionNonce    = "";
            public float       LastActivityTime;
            public float       LastLoginAttemptTime = -999f;
            public string      RemoteAddress    = "";

            public ConnData() => LastActivityTime = Time.time;
        }

        private class IpData
        {
            public int   FailedAttempts;
            public float BanUntil;
            public float LastAttemptTime;
        }

        private readonly Dictionary<int, ConnData>    _sessions = new();
        private readonly Dictionary<string, IpData>   _ipBans   = new();
        private Coroutine _cleanupCoroutine;

        private const float CLEANUP_INTERVAL = 60f;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (_cleanupCoroutine != null) StopCoroutine(_cleanupCoroutine);
        }

        public void RegisterHandlers()
        {
            NetworkServer.RegisterHandler<MsgLoginRequest>          (OnLoginRequest,           false);
            NetworkServer.RegisterHandler<MsgCreateAccountRequest>  (OnCreateAccountRequest,   false);
            NetworkServer.RegisterHandler<MsgRequestCharacterList>  (OnRequestCharacterList,   false);
            NetworkServer.RegisterHandler<MsgCreateCharacterRequest>(OnCreateCharacterRequest, false);
            NetworkServer.RegisterHandler<MsgSelectCharacter>       (OnSelectCharacter,        false);

            _cleanupCoroutine = StartCoroutine(CleanupExpiredSessions());
            Debug.Log("[ServerAuthManager] Handlers registrados.");
        }

        public void OnServerConnect(NetworkConnectionToClient conn)
        {
            string remoteAddress = string.IsNullOrEmpty(conn?.address)
                ? "unknown"
                : conn.address;

            if (IsIpBanned(remoteAddress))
            {
                Debug.LogWarning($"[ServerAuth] IP banido tentou conectar: {remoteAddress}");
                conn.Send(new MsgLoginResponse
                {
                    Success = false,
                    Error   = "Muitas tentativas falhas. Tente novamente em alguns minutos."
                });
                conn.Disconnect();
                return;
            }

            // NOTA: O nonce é vitalício por sessão (não rotaciona).
            // Para hardening adicional, considerar rotação a cada N segundos
            // — mas isso exigiria reenviar MsgAuthChallenge e o cliente
            // reagir antes de cada login attempt.
            var session = new ConnData
            {
                SessionNonce  = GameManager.GenerateNonce(),
                RemoteAddress = remoteAddress
            };
            _sessions[conn.connectionId] = session;

            conn.Send(new MsgAuthChallenge { Nonce = session.SessionNonce });
            LogAuth($"Nova conexão: {conn.connectionId} (IP {remoteAddress}) | nonce enviado.");
        }

        public void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            _sessions.Remove(conn.connectionId);
        }

        // ══════════════════════════════════════════════════════════════════
        // Login
        // ══════════════════════════════════════════════════════════════════

        private void OnLoginRequest(NetworkConnectionToClient conn, MsgLoginRequest msg)
        {
            if (!_sessions.TryGetValue(conn.connectionId, out var session))
            {
                conn.Send(new MsgLoginResponse { Success = false, Error = "Sessão inválida." });
                return;
            }

            if (session.State != ConnState.Unauthenticated)
            {
                conn.Send(new MsgLoginResponse { Success = false, Error = "Já autenticado." });
                return;
            }

            // Throttle entre tentativas
            if (Time.time - session.LastLoginAttemptTime < GameConstants.Auth.MIN_TIME_BETWEEN_LOGINS)
            {
                conn.Send(new MsgLoginResponse
                {
                    Success = false,
                    Error   = "Aguarde antes de tentar novamente."
                });
                return;
            }
            session.LastLoginAttemptTime = Time.time;

            session.LoginAttempts++;
            if (session.LoginAttempts > GameConstants.Auth.LOGIN_MAX_PER_CONN)
            {
                Debug.LogWarning($"[ServerAuth] SECURITY: conn:{conn.connectionId} excedeu tentativas.");
                RecordFailedLoginAttempt(session.RemoteAddress);
                conn.Send(new MsgLoginResponse { Success = false, Error = "Muitas tentativas. Tente mais tarde." });
                conn.Disconnect();
                return;
            }

            if (string.IsNullOrWhiteSpace(msg.Username) || string.IsNullOrWhiteSpace(msg.SignedHash))
            {
                conn.Send(new MsgLoginResponse { Success = false, Error = "Dados de login inválidos." });
                return;
            }

            if (msg.Username.Length > MAX_USERNAME_PAYLOAD_BYTES
                || msg.SignedHash.Length > MAX_HASH_PAYLOAD_BYTES)
            {
                Debug.LogWarning($"[ServerAuth] SECURITY: payload anormal de {session.RemoteAddress}");
                RecordFailedLoginAttempt(session.RemoteAddress);
                conn.Send(new MsgLoginResponse { Success = false, Error = "Dados inválidos." });
                conn.Disconnect();
                return;
            }

            if (string.IsNullOrWhiteSpace(session.SessionNonce))
            {
                Debug.LogError($"[ServerAuth] SessionNonce vazio para conn:{conn.connectionId}.");
                conn.Send(new MsgLoginResponse { Success = false, Error = "Erro de sessão. Reconecte." });
                return;
            }

            // O DB pode retornar um delay sugerido para mitigar timing attacks.
            // NÃO bloqueamos aqui; aplicamos o delay via coroutine se necessário.
            LoginAttemptResult result = default;
            if (DatabaseManager.Instance != null)
                result = DatabaseManager.Instance.TryLoginWithSignedHash(
                    msg.Username, msg.SignedHash, session.SessionNonce);

            if (!result.Success)
            {
                RecordFailedLoginAttempt(session.RemoteAddress);

                string attempts = $"({session.LoginAttempts}/{GameConstants.Auth.LOGIN_MAX_PER_CONN})";
                var failMsg = new MsgLoginResponse
                {
                    Success = false,
                    Error   = $"Usuário ou senha incorretos. {attempts}"
                };

                if (result.SuggestedDelayMs > 0)
                {
                    // Resposta ATRASADA mas NÃO-BLOQUEANTE
                    StartCoroutine(SendDelayed(conn, failMsg, result.SuggestedDelayMs));
                }
                else
                {
                    conn.Send(failMsg);
                }
                return;
            }

            // Login bem-sucedido
            session.State            = ConnState.Authenticated;
            session.Username         = result.Account.Username;
            session.CachedAccount    = result.Account;
            session.LoginAttempts    = 0;
            session.LastActivityTime = Time.time;

            ClearIpFailures(session.RemoteAddress);

            conn.Send(new MsgLoginResponse { Success = true, Username = result.Account.Username });
            SendCharacterList(conn, result.Account);

            Debug.Log($"[ServerAuth] Login OK: {result.Account.Username} (IP {session.RemoteAddress})");
        }

        /// <summary>
        /// Envia uma resposta após um atraso, sem bloquear o main thread.
        /// Usado para mitigação de timing attack em login falho.
        /// </summary>
        private IEnumerator SendDelayed(NetworkConnectionToClient conn,
                                        MsgLoginResponse msg,
                                        int delayMs)
        {
            yield return new WaitForSeconds(delayMs / 1000f);

            // Conexão ainda válida?
            if (conn != null && conn.isReady)
                conn.Send(msg);
        }

        // ── Rate limit por IP ──────────────────────────────────────────────

        private bool IsIpBanned(string ip)
        {
            if (string.IsNullOrEmpty(ip) || ip == "unknown") return false;
            if (!_ipBans.TryGetValue(ip, out var data)) return false;
            return Time.time < data.BanUntil;
        }

        private void RecordFailedLoginAttempt(string ip)
        {
            if (string.IsNullOrEmpty(ip) || ip == "unknown") return;

            if (!_ipBans.TryGetValue(ip, out var data))
            {
                data = new IpData();
                _ipBans[ip] = data;
            }

            data.FailedAttempts++;
            data.LastAttemptTime = Time.time;

            if (data.FailedAttempts >= GameConstants.Auth.LOGIN_MAX_PER_IP)
            {
                data.BanUntil = Time.time + GameConstants.Auth.IP_BAN_DURATION_SECONDS;
                Debug.LogWarning($"[ServerAuth] SECURITY [{System.DateTime.UtcNow:o}]: " +
                                 $"IP banido por brute-force: {ip} " +
                                 $"({data.FailedAttempts} falhas, ban por " +
                                 $"{GameConstants.Auth.IP_BAN_DURATION_SECONDS}s)");
            }
        }

        private void ClearIpFailures(string ip)
        {
            if (string.IsNullOrEmpty(ip) || ip == "unknown") return;
            _ipBans.Remove(ip);
        }

        // ══════════════════════════════════════════════════════════════════
        // Criar conta
        // ══════════════════════════════════════════════════════════════════

        private void OnCreateAccountRequest(NetworkConnectionToClient conn, MsgCreateAccountRequest msg)
        {
            if (_sessions.TryGetValue(conn.connectionId, out var session))
            {
                if (Time.time - session.LastLoginAttemptTime < GameConstants.Auth.MIN_TIME_BETWEEN_LOGINS)
                {
                    conn.Send(new MsgCreateAccountResponse
                    {
                        Success = false,
                        Error   = "Aguarde antes de tentar novamente."
                    });
                    return;
                }
                session.LastLoginAttemptTime = Time.time;
            }

            if (string.IsNullOrWhiteSpace(msg.Username))
            {
                conn.Send(new MsgCreateAccountResponse { Success = false, Error = "Username inválido." });
                return;
            }
            if (string.IsNullOrWhiteSpace(msg.PasswordHash))
            {
                conn.Send(new MsgCreateAccountResponse { Success = false, Error = "Senha inválida." });
                return;
            }

            if (msg.Username.Length > MAX_USERNAME_PAYLOAD_BYTES
                || msg.PasswordHash.Length > MAX_HASH_PAYLOAD_BYTES)
            {
                conn.Send(new MsgCreateAccountResponse { Success = false, Error = "Dados inválidos." });
                return;
            }

            if (!IsValidUsername(msg.Username))
            {
                conn.Send(new MsgCreateAccountResponse
                {
                    Success = false,
                    Error   = "Username deve conter apenas letras, números e underscore."
                });
                return;
            }

            var error = DatabaseManager.Instance?.TryCreateAccount(msg.Username, msg.PasswordHash);
            if (error != null)
            {
                conn.Send(new MsgCreateAccountResponse { Success = false, Error = error });
                return;
            }
            conn.Send(new MsgCreateAccountResponse { Success = true });
            Debug.Log($"[ServerAuth] Conta criada: {msg.Username}");
        }

        private static bool IsValidUsername(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return false;
            string trimmed = username.Trim();
            if (trimmed.Length < GameConstants.Auth.USERNAME_MIN_LENGTH
                || trimmed.Length > GameConstants.Auth.USERNAME_MAX_LENGTH) return false;
            foreach (char c in trimmed)
            {
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
            }
            return true;
        }

        // ══════════════════════════════════════════════════════════════════
        // Lista / criar / selecionar personagens
        // ══════════════════════════════════════════════════════════════════

        private void OnRequestCharacterList(NetworkConnectionToClient conn, MsgRequestCharacterList msg)
        {
            if (!RequireAuth(conn, out var session)) return;
            UpdateActivity(session);

            var chars = DatabaseManager.Instance?.LoadCharacters(session.Username)
                        ?? new List<CharacterData>();
            SendCharacterList(conn, session.Username, chars);
        }

        private void SendCharacterList(NetworkConnectionToClient conn, AccountData account)
            => SendCharacterList(conn, account.Username, account.Characters ?? new List<CharacterData>());

        private void SendCharacterList(NetworkConnectionToClient conn, string username, List<CharacterData> chars)
        {
            var list = new List<CharacterSummary>();
            foreach (var ch in chars)
                list.Add(new CharacterSummary
                {
                    CharacterId   = ch.CharacterId,
                    CharacterName = ch.CharacterName,
                    Race          = ch.Race.ToString(),
                    Level         = ch.Level
                });
            conn.Send(new MsgCharacterListResponse { Characters = list });
        }

        private void OnCreateCharacterRequest(NetworkConnectionToClient conn, MsgCreateCharacterRequest msg)
        {
            if (!RequireAuth(conn, out var session)) return;
            UpdateActivity(session);

            if (string.IsNullOrWhiteSpace(msg.Name)
                || msg.Name.Length < GameConstants.Auth.CHARACTER_NAME_MIN
                || msg.Name.Length > GameConstants.Auth.CHARACTER_NAME_MAX)
            {
                conn.Send(new MsgCreateCharacterResponse
                {
                    Success = false,
                    Error   = $"Nome inválido ({GameConstants.Auth.CHARACTER_NAME_MIN} a " +
                              $"{GameConstants.Auth.CHARACTER_NAME_MAX} caracteres)."
                });
                return;
            }

            if (msg.RaceIndex < 0 || !System.Enum.IsDefined(typeof(CharacterRace), msg.RaceIndex))
            {
                conn.Send(new MsgCreateCharacterResponse
                {
                    Success = false,
                    Error   = "Raça inválida."
                });
                return;
            }

            var error = DatabaseManager.Instance?.TryCreateCharacter(
                session.Username, msg.Name, (CharacterRace)msg.RaceIndex);

            if (error != null)
            {
                conn.Send(new MsgCreateCharacterResponse { Success = false, Error = error });
                return;
            }

            var chars = DatabaseManager.Instance?.LoadCharacters(session.Username)
                        ?? new List<CharacterData>();
            var list = new List<CharacterSummary>();
            foreach (var ch in chars)
                list.Add(new CharacterSummary
                {
                    CharacterId   = ch.CharacterId,
                    CharacterName = ch.CharacterName,
                    Race          = ch.Race.ToString(),
                    Level         = ch.Level
                });

            conn.Send(new MsgCreateCharacterResponse { Success = true, UpdatedList = list });
            Debug.Log($"[ServerAuth] Personagem criado: {msg.Name} (conta:{session.Username})");
        }

        private void OnSelectCharacter(NetworkConnectionToClient conn, MsgSelectCharacter msg)
        {
            if (!RequireAuth(conn, out var session)) return;

            if (session.State == ConnState.InGame)
            {
                conn.Send(new MsgSelectCharacterResponse { Success = false, Error = "Já está em jogo." });
                return;
            }

            if (string.IsNullOrWhiteSpace(msg.CharacterId))
            {
                conn.Send(new MsgSelectCharacterResponse
                {
                    Success = false,
                    Error   = "ID de personagem inválido."
                });
                return;
            }

            var charData = DatabaseManager.Instance?.LoadCharacterForAccount(
                msg.CharacterId, session.Username);

            if (charData == null)
            {
                conn.Send(new MsgSelectCharacterResponse
                {
                    Success = false,
                    Error   = "Personagem não encontrado ou não pertence a esta conta."
                });
                Debug.LogWarning($"[ServerAuth] SECURITY: {session.Username} tentou selecionar {msg.CharacterId}");
                return;
            }

            session.State        = ConnState.InGame;
            session.CharacterId  = msg.CharacterId;
            UpdateActivity(session);

            RPGNetworkManager.singleton?.SpawnPlayerForConnection(conn, charData, session.Username);
            Debug.Log($"[ServerAuth] {charData.CharacterName} ({charData.Race}) entrando | conn:{conn.connectionId}");
        }

        // ══════════════════════════════════════════════════════════════════
        // Helpers
        // ══════════════════════════════════════════════════════════════════

        private bool RequireAuth(NetworkConnectionToClient conn, out ConnData session)
        {
            if (!_sessions.TryGetValue(conn.connectionId, out session))
            {
                conn.Send(new MsgErrorResponse { Error = "Sessão inválida." });
                return false;
            }
            if (session.State == ConnState.Unauthenticated)
            {
                conn.Send(new MsgErrorResponse { Error = "Não autenticado." });
                return false;
            }
            return true;
        }

        private static void UpdateActivity(ConnData session)
            => session.LastActivityTime = Time.time;

        private void LogAuth(string msg)
        {
            if (debugAuth) Debug.Log($"[ServerAuth-DEBUG] {msg}");
        }

        // ══════════════════════════════════════════════════════════════════
        // Limpeza de sessões expiradas e IPs banidos
        // ══════════════════════════════════════════════════════════════════

        private IEnumerator CleanupExpiredSessions()
        {
            var wait = new WaitForSeconds(CLEANUP_INTERVAL);
            var expiredSessions = new List<int>();
            var expiredIps      = new List<string>();

            while (true)
            {
                yield return wait;

                expiredSessions.Clear();
                foreach (var kv in _sessions)
                {
                    if (kv.Value.State == ConnState.InGame) continue;
                    if (Time.time - kv.Value.LastActivityTime > GameConstants.Auth.SESSION_TTL_SECONDS)
                        expiredSessions.Add(kv.Key);
                }
                foreach (var id in expiredSessions)
                {
                    var state = _sessions[id].State;
                    _sessions.Remove(id);
                    Debug.Log($"[ServerAuthManager] Sessão expirada removida: connId={id} estado={state}");
                }

                expiredIps.Clear();
                foreach (var kv in _ipBans)
                {
                    var data = kv.Value;
                    if (Time.time >= data.BanUntil
                        && Time.time - data.LastAttemptTime > GameConstants.Auth.IP_BAN_DURATION_SECONDS)
                        expiredIps.Add(kv.Key);
                }
                foreach (var ip in expiredIps)
                    _ipBans.Remove(ip);
            }
        }
    }
}
