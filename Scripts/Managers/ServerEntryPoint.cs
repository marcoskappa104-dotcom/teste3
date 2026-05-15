using UnityEngine;
using UnityEngine.SceneManagement;

namespace RPG.Network
{
    /// <summary>
    /// Em builds dedicated server, força carregar a GameplayScene logo na inicialização.
    /// O servidor não precisa de uma cena dedicada — ele compila sem gráficos
    /// e usa a mesma GameplayScene do cliente.
    ///
    /// Coloque este componente em um GameObject da LoginScene.
    /// </summary>
    public class ServerEntryPoint : MonoBehaviour
    {
        [Header("Cena do servidor (precisa estar no Build Settings)")]
        [SerializeField] private string serverSceneName = "GameplayScene";

        private void Awake()
        {
            if (!NetworkConnectionBootstrapper.IsServerBuild()) return;

            string currentScene = SceneManager.GetActiveScene().name;
            if (currentScene == serverSceneName) return;

            Debug.Log($"[ServerEntryPoint] Servidor detectado. Carregando '{serverSceneName}'.");
            SceneManager.LoadScene(serverSceneName);
        }
    }
}
