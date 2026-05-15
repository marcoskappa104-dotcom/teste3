using UnityEngine;
using Mirror;
using RPG.UI;
using RPG.Character;

namespace RPG.Network
{
    /// <summary>
    /// Conecta o UIManager ao PlayerEntity local quando ele chega.
    /// Em multiplayer o player spawna depois da UI existir, então fazemos
    /// retry leve até a conexão.
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 6) ===
    ///
    ///   1. CLEANUP EM OnDestroy:
    ///      Antes, se o GameObject fosse destruído enquanto havia subscrição
    ///      em OnInitialized, ficava um listener pendurado. Agora limpamos.
    ///
    ///   2. CHECAGEM DE BATCH MODE:
    ///      Servidor dedicado puro nem tenta — desabilita-se em Awake.
    /// </summary>
    public class NetworkUIConnector : MonoBehaviour
    {
        private const float RETRY_INTERVAL = 0.5f;

        private bool         _connected;
        private float        _retryTimer;
        private PlayerEntity _subscribedEntity;

        private void Awake()
        {
            // Servidor dedicado puro não tem UI nem PlayerEntity local
            if (Application.isBatchMode)
            {
                enabled = false;
                return;
            }
        }

        private void Start() => TryConnect();

        private void OnDestroy()
        {
            // Desinscreve se ficamos esperando inicialização
            if (_subscribedEntity != null)
            {
                _subscribedEntity.OnInitialized -= OnPlayerInitialized;
                _subscribedEntity = null;
            }
        }

        private void Update()
        {
            if (_connected) return;

            _retryTimer += Time.deltaTime;
            if (_retryTimer >= RETRY_INTERVAL)
            {
                _retryTimer = 0f;
                TryConnect();
            }
        }

        private void TryConnect()
        {
            if (_connected) return;
            if (!NetworkClient.active) return;
            if (NetworkClient.localPlayer == null) return;

            var playerEntity = NetworkClient.localPlayer.GetComponent<PlayerEntity>();
            if (playerEntity == null) return;

            if (playerEntity.IsInitialized)
            {
                BindUI(playerEntity);
            }
            else
            {
                // Evita múltiplos binds caso entre aqui várias vezes
                if (_subscribedEntity != playerEntity)
                {
                    if (_subscribedEntity != null)
                        _subscribedEntity.OnInitialized -= OnPlayerInitialized;

                    playerEntity.OnInitialized += OnPlayerInitialized;
                    _subscribedEntity = playerEntity;
                }
            }
        }

        private void OnPlayerInitialized()
        {
            if (_connected) return;

            var playerEntity = NetworkClient.localPlayer?.GetComponent<PlayerEntity>();
            if (playerEntity != null)
                BindUI(playerEntity);
        }

        private void BindUI(PlayerEntity playerEntity)
        {
            if (_connected) return;
            _connected = true;

            // Limpa subscrição usada apenas como gatilho
            if (_subscribedEntity != null)
            {
                _subscribedEntity.OnInitialized -= OnPlayerInitialized;
                _subscribedEntity = null;
            }

            UIManager.Instance?.BindLocalPlayer(playerEntity);
            Debug.Log("[NetworkUIConnector] HUD conectado ao player local.");
        }
    }
}