using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Alias para resolver ambiguidade com UnityEngine.NetworkPlayer (legacy multiplayer)
using NetworkPlayer = RPG.Network.NetworkPlayer;

namespace RPG.UI
{
    /// <summary>
    /// Tela de morte. Mostra mensagem e botão de respawn.
    /// API estática (Show/Hide) usada por NetworkPlayer.RpcPlayerDied/RpcOnRespawned.
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 6) ===
    ///
    ///   1. COROUTINE EM VEZ DE Invoke:
    ///      Antes, o "timer de respawn" usava Invoke(string) que é frágil
    ///      (precisa de string exata, não dá pra cancelar precisamente
    ///      individualmente). Trocado por Coroutine.
    ///
    ///   2. CLEANUP ROBUSTO EM OnDisable/OnDestroy:
    ///      Coroutine cancelada + referência ao NetworkPlayer limpa.
    ///
    ///   3. CANVASGROUP GUARD:
    ///      Se _canvasGroup estiver null, fallback para SetActive no painel
    ///      raiz. Antes assumia que sempre teria CanvasGroup.
    /// </summary>
    public class DeathScreenUI : MonoBehaviour
    {
        private static DeathScreenUI _instance;

        [Header("Refs")]
        [SerializeField] private GameObject  _root;
        [SerializeField] private CanvasGroup _canvasGroup;
        [SerializeField] private TMP_Text    _titleText;
        [SerializeField] private TMP_Text    _countdownText;
        [SerializeField] private Button      _respawnButton;

        [Header("Config")]
        [SerializeField] private float _respawnDelay   = 3f;
        [SerializeField] private float _fadeInDuration = 0.5f;

        private NetworkPlayer _player;
        private Coroutine     _countdownCoroutine;
        private Coroutine     _fadeCoroutine;

        // ══════════════════════════════════════════════════════════════════
        // API estática
        // ══════════════════════════════════════════════════════════════════

        public static void Show(NetworkPlayer player)
        {
            if (_instance == null)
            {
                Debug.LogWarning("[DeathScreenUI] Não há instância na cena!");
                return;
            }
            _instance.ShowInternal(player);
        }

        public static void Hide() => _instance?.HideInternal();

        // ══════════════════════════════════════════════════════════════════
        // Lifecycle
        // ══════════════════════════════════════════════════════════════════

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            HideInternal(immediate: true);

            if (_respawnButton != null)
                _respawnButton.onClick.AddListener(OnRespawnClicked);
        }

        private void OnDisable()
        {
            StopAllLocalCoroutines();
        }

        private void OnDestroy()
        {
            StopAllLocalCoroutines();
            if (_respawnButton != null)
                _respawnButton.onClick.RemoveListener(OnRespawnClicked);
            _player = null;
            if (_instance == this) _instance = null;
        }

        private void StopAllLocalCoroutines()
        {
            if (_countdownCoroutine != null)
            {
                StopCoroutine(_countdownCoroutine);
                _countdownCoroutine = null;
            }
            if (_fadeCoroutine != null)
            {
                StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = null;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Show / Hide
        // ══════════════════════════════════════════════════════════════════

        private void ShowInternal(NetworkPlayer player)
        {
            _player = player;

            if (_root != null) _root.SetActive(true);
            if (_titleText != null) _titleText.text = "Você morreu!";

            if (_respawnButton != null) _respawnButton.interactable = false;

            StopAllLocalCoroutines();
            _fadeCoroutine      = StartCoroutine(FadeIn());
            _countdownCoroutine = StartCoroutine(RespawnCountdown());
        }

        private void HideInternal(bool immediate = false)
        {
            StopAllLocalCoroutines();
            _player = null;

            if (_canvasGroup != null)
            {
                _canvasGroup.alpha          = 0f;
                _canvasGroup.blocksRaycasts = false;
                _canvasGroup.interactable   = false;
            }

            if (_root != null) _root.SetActive(false);
        }

        private IEnumerator FadeIn()
        {
            if (_canvasGroup == null) yield break;

            _canvasGroup.alpha          = 0f;
            _canvasGroup.blocksRaycasts = true;
            _canvasGroup.interactable   = true;

            float elapsed = 0f;
            while (elapsed < _fadeInDuration)
            {
                elapsed            += Time.unscaledDeltaTime;
                _canvasGroup.alpha  = Mathf.Clamp01(elapsed / _fadeInDuration);
                yield return null;
            }

            _canvasGroup.alpha = 1f;
            _fadeCoroutine     = null;
        }

        private IEnumerator RespawnCountdown()
        {
            float remaining = _respawnDelay;

            while (remaining > 0f)
            {
                if (_countdownText != null)
                    _countdownText.text = $"Respawn em {remaining:0.0}s";
                remaining -= Time.unscaledDeltaTime;
                yield return null;
            }

            if (_countdownText != null) _countdownText.text = "Pronto!";
            if (_respawnButton != null) _respawnButton.interactable = true;

            _countdownCoroutine = null;
        }

        private void OnRespawnClicked()
        {
            if (_player == null) return;
            _player.CmdRequestRespawn();
            if (_respawnButton != null) _respawnButton.interactable = false;
        }
    }
}