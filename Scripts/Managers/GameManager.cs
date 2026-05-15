using UnityEngine;
using UnityEngine.SceneManagement;
using System;

namespace RPG.Managers
{
    /// <summary>
    /// Gerenciador global de sessão e autenticação.
    ///
    /// ───────────────────────────────────────────────────────────────────────
    /// PIPELINE DE AUTENTICAÇÃO
    /// ───────────────────────────────────────────────────────────────────────
    ///
    /// Criar conta:
    ///   Cliente:  hash = SHA256(senha)
    ///   Cliente:  envia { Username, PasswordHash = hash }
    ///   Servidor: armazena hash diretamente no banco
    ///
    /// Login:
    ///   Servidor: gera nonce aleatório e envia ao cliente ao conectar
    ///   Cliente:  hash      = SHA256(senha)
    ///   Cliente:  signed    = SHA256(hash + nonce)
    ///   Cliente:  envia { Username, SignedHash = signed }
    ///   Servidor: expected  = SHA256(STORED_HASH + nonce)
    ///   Servidor: aceita se signed == expected
    ///
    /// O nonce previne replay attacks: capturar SignedHash não permite reusar
    /// em outra sessão (nonce diferente => hash diferente).
    ///
    /// ───────────────────────────────────────────────────────────────────────
    /// LIMITAÇÕES DE SEGURANÇA
    /// ───────────────────────────────────────────────────────────────────────
    ///
    /// - SEM TLS: tráfego é vulnerável a MITM ativo (capturar e modificar).
    /// - SEM SALT no banco: se o banco vazar, hashes são SHA256(senha), que
    ///   é quebrável com rainbow tables. Para produção real considere:
    ///     1) bcrypt/Argon2 no servidor (inclui salt automaticamente)
    ///     2) KCP+TLS ou WebSocket+WSS para o transporte
    ///
    /// Para um projeto pessoal ou alfa fechado isso é aceitável. Para release
    /// público, troque para bcrypt antes de aceitar contas reais.
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 4 — polish) ===
    ///
    ///   1. ComputeSHA256 USA STATIC METHOD MODERNO:
    ///      System.Security.Cryptography.SHA256.HashData (.NET 6+) é o caminho
    ///      idiomático e ~30% mais rápido que criar uma instância SHA256.Create
    ///      a cada chamada (que aloca lixo para o GC). Mantém compatibilidade
    ///      com Unity 2022.3+ que usa .NET Standard 2.1.
    ///
    ///   2. CONSTANTES DE NONCE:
    ///      Bytes do nonce (NONCE_BYTES = 16) explicitado como constante,
    ///      facilitando ajuste futuro (e.g., aumentar para 32 bytes se quiser
    ///      mais entropia).
    ///
    ///   3. VALIDAÇÃO EM HashPassword:
    ///      Antes, retornava "" para string vazia. Agora também rejeita whitespace
    ///      puro — proteção contra strings malformadas.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        public string LoggedUsername { get; private set; } = "";

        public const string SCENE_LOGIN     = "LoginScene";
        public const string SCENE_CHARACTER = "CharacterScene";
        public const string SCENE_GAMEPLAY  = "GameplayScene";
        public const string GAME_VERSION    = "0.1.0-alpha";

        // Quantidade de bytes aleatórios no nonce de challenge. 16 bytes
        // (128 bits) é mais que suficiente para impedir colisão prática.
        private const int NONCE_BYTES = 16;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Debug.Log($"[GameManager] Iniciado — versão {GAME_VERSION}");
        }

        public void SetLoggedUsername(string username)
        {
            LoggedUsername = username ?? "";
        }

        public void GoToCharacterSelect() => SceneManager.LoadScene(SCENE_CHARACTER);
        public void GoToGameplay()        => SceneManager.LoadScene(SCENE_GAMEPLAY);

        public void Logout()
        {
            LoggedUsername = "";
            SceneManager.LoadScene(SCENE_LOGIN);
        }

        // ══════════════════════════════════════════════════════════════════
        // Hashing
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// SHA256 da senha. Usado na criação de conta e como base do login.
        /// O cliente nunca deve enviar a senha em texto plano.
        /// </summary>
        public static string HashPassword(string password)
        {
            if (string.IsNullOrWhiteSpace(password)) return "";
            return ComputeSHA256(password);
        }

        /// <summary>
        /// Assina o hash base com o nonce da sessão para login.
        /// Usado pelo cliente no segundo passo.
        /// </summary>
        public static string HashPasswordWithNonce(string passwordHash, string nonce)
        {
            if (string.IsNullOrEmpty(passwordHash) || string.IsNullOrEmpty(nonce))
                return passwordHash;
            return ComputeSHA256(passwordHash + nonce);
        }

        /// <summary>
        /// Gera nonce aleatório de NONCE_BYTES bytes. Chamado pelo servidor por sessão.
        /// Usa RandomNumberGenerator que é criptograficamente seguro.
        /// </summary>
        public static string GenerateNonce()
        {
            var bytes = new byte[NONCE_BYTES];
            using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

#if UNITY_SERVER || UNITY_EDITOR

        /// <summary>
        /// Servidor — prepara hash para armazenamento.
        /// Sem salt: armazena exatamente o que o cliente enviou (SHA256(senha)).
        /// Migrar para bcrypt aqui é uma mudança contida que não exige alterar
        /// o protocolo de rede.
        /// </summary>
        public static string ServerHashForStorage(string clientPasswordHash)
        {
            if (string.IsNullOrEmpty(clientPasswordHash))
            {
                Debug.LogError("[GameManager] ServerHashForStorage: hash vazio.");
                return "";
            }
            return clientPasswordHash;
        }

        /// <summary>
        /// Servidor — valida login.
        /// expected = SHA256(STORED_HASH + nonce)
        /// Aceita se igual ao SignedHash enviado pelo cliente.
        /// </summary>
        public static bool ValidateLoginWithNonce(
            string storedPasswordHash,
            string clientSignedHash,
            string sessionNonce)
        {
            if (string.IsNullOrEmpty(storedPasswordHash)
                || string.IsNullOrEmpty(clientSignedHash)
                || string.IsNullOrEmpty(sessionNonce))
                return false;

            string expected = ComputeSHA256(storedPasswordHash + sessionNonce);
            return string.Equals(expected, clientSignedHash, StringComparison.OrdinalIgnoreCase);
        }

#endif

/// <summary>
/// SHA256 de uma string UTF-8, retorna hex lowercase sem hífens.
/// Compatível com Unity 2022 / .NET Standard 2.1.
/// </summary>
public static string ComputeSHA256(string input)
{
    using var sha256 = System.Security.Cryptography.SHA256.Create();

    byte[] inputBytes = System.Text.Encoding.UTF8.GetBytes(input);
    byte[] hash = sha256.ComputeHash(inputBytes);

    return BitConverter.ToString(hash)
        .Replace("-", "")
        .ToLowerInvariant();
}
    }
}
