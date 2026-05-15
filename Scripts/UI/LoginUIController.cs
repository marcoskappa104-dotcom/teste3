using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Mirror;
using RPG.Managers;
using RPG.Network;

namespace RPG.UI
{
    /// <summary>
    /// Tela de login. Comunica-se com ClientAuthHandler para autenticar.
    /// Mostra mensagens de erro e estado de conexão.
    /// </summary>
    public class LoginUIController : MonoBehaviour
    {
        [Header("Painéis")]
        [SerializeField] private GameObject loginPanel;
        [SerializeField] private GameObject createAccountPanel;

        [Header("Login")]
        [SerializeField] private TMP_InputField loginUsernameInput;
        [SerializeField] private TMP_InputField loginPasswordInput;
        [SerializeField] private Button         loginButton;
        [SerializeField] private Button         openCreateAccountButton;
        [SerializeField] private TMP_Text       loginErrorText;
        [SerializeField] private TMP_Text       loginStatusText;

        [Header("Criar Conta")]
        [SerializeField] private TMP_InputField createUsernameInput;
        [SerializeField] private TMP_InputField createPasswordInput;
        [SerializeField] private TMP_InputField createConfirmPasswordInput;
        [SerializeField] private Button         submitCreateButton;
        [SerializeField] private Button         backToLoginButton;
        [SerializeField] private TMP_Text       createErrorText;
        [SerializeField] private TMP_Text       createSuccessText;

        private const float SUCCESS_RETURN_DELAY = 1.5f;
        private const int   MIN_USERNAME_LENGTH  = 4;

        private void Start()
        {
            ShowLoginPanel();

            loginButton.onClick.AddListener(OnLoginClicked);
            openCreateAccountButton.onClick.AddListener(ShowCreateAccountPanel);
            submitCreateButton.onClick.AddListener(OnCreateAccountClicked);
            backToLoginButton.onClick.AddListener(ShowLoginPanel);

            loginUsernameInput.onSubmit.AddListener(_ => OnLoginClicked());
            loginPasswordInput.onSubmit.AddListener(_ => OnLoginClicked());

            if (ClientAuthHandler.Instance != null)
            {
                ClientAuthHandler.Instance.OnLoginResult         += HandleLoginResult;
                ClientAuthHandler.Instance.OnCreateAccountResult += HandleCreateAccountResult;
                ClientAuthHandler.Instance.OnServerDisconnected  += HandleServerDisconnected;
            }
            else
            {
                Debug.LogWarning("[LoginUI] ClientAuthHandler não encontrado.");
                SetStatus("Erro: ClientAuthHandler não encontrado.", isError: true);
            }

            UpdateConnectionStatus();
        }

        private void OnDestroy()
        {
            if (ClientAuthHandler.Instance != null)
            {
                ClientAuthHandler.Instance.OnLoginResult         -= HandleLoginResult;
                ClientAuthHandler.Instance.OnCreateAccountResult -= HandleCreateAccountResult;
                ClientAuthHandler.Instance.OnServerDisconnected  -= HandleServerDisconnected;
            }
        }

        private void UpdateConnectionStatus()
        {
            if (!NetworkClient.isConnected && !NetworkServer.active)
                SetStatus("Aguardando conexão com o servidor...");
            else if (NetworkClient.isConnected)
                SetStatus("Conectado.", isError: false);
        }

        // ══════════════════════════════════════════════════════════════════
        // Painéis
        // ══════════════════════════════════════════════════════════════════

        private void ShowLoginPanel()
        {
            loginPanel.SetActive(true);
            createAccountPanel.SetActive(false);
            if (loginErrorText) loginErrorText.text = "";
            SetStatus("");
            ClearLoginFields();
        }

        private void ShowCreateAccountPanel()
        {
            loginPanel.SetActive(false);
            createAccountPanel.SetActive(true);
            if (createErrorText)   createErrorText.text   = "";
            if (createSuccessText) createSuccessText.text = "";
            ClearCreateFields();
        }

        // ══════════════════════════════════════════════════════════════════
        // Ações
        // ══════════════════════════════════════════════════════════════════

        private void OnLoginClicked()
        {
            if (loginErrorText) loginErrorText.text = "";

            string user = loginUsernameInput.text.Trim();
            string pass = loginPasswordInput.text;

            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            {
                SetLoginError("Preencha usuário e senha.");
                return;
            }

            if (!NetworkClient.isConnected)
            {
                SetLoginError("Sem conexão com o servidor.");
                return;
            }

            SetStatus("Autenticando...");
            SetInputsInteractable(false);
            ClientAuthHandler.Instance?.SendLogin(user, pass);
        }

        private void OnCreateAccountClicked()
        {
            if (createErrorText)   createErrorText.text   = "";
            if (createSuccessText) createSuccessText.text = "";

            string user    = createUsernameInput.text.Trim();
            string pass    = createPasswordInput.text;
            string confirm = createConfirmPasswordInput.text;

            if (user.Length < MIN_USERNAME_LENGTH)
            {
                SetCreateError($"Username: mínimo {MIN_USERNAME_LENGTH} caracteres.");
                return;
            }
            if (string.IsNullOrWhiteSpace(pass))
            {
                SetCreateError("Digite uma senha.");
                return;
            }
            if (pass != confirm)
            {
                SetCreateError("Senhas não coincidem.");
                return;
            }
            if (!NetworkClient.isConnected)
            {
                SetCreateError("Sem conexão com o servidor.");
                return;
            }

            submitCreateButton.interactable = false;
            ClientAuthHandler.Instance?.SendCreateAccount(user, pass);
        }

        // ══════════════════════════════════════════════════════════════════
        // Handlers
        // ══════════════════════════════════════════════════════════════════

        private void HandleLoginResult(bool success, string error)
        {
            SetInputsInteractable(true);
            SetStatus("");

            if (success)
                GameManager.Instance?.GoToCharacterSelect();
            else
                SetLoginError(error ?? "Erro de login.");
        }

        private void HandleCreateAccountResult(bool success, string error)
        {
            submitCreateButton.interactable = true;
            if (success)
            {
                if (createSuccessText) createSuccessText.text = "Conta criada! Faça login.";
                ClearCreateFields();
                Invoke(nameof(ShowLoginPanel), SUCCESS_RETURN_DELAY);
            }
            else
            {
                SetCreateError(error ?? "Erro ao criar conta.");
            }
        }

        private void HandleServerDisconnected()
        {
            SetInputsInteractable(true);
            SetStatus("Desconectado do servidor.", isError: true);
        }

        // ══════════════════════════════════════════════════════════════════
        // Helpers
        // ══════════════════════════════════════════════════════════════════

        private void SetStatus(string msg, bool isError = false)
        {
            if (loginStatusText == null) return;
            loginStatusText.text  = msg;
            loginStatusText.color = isError ? Color.red : Color.white;
        }

        private void SetLoginError(string msg)
        {
            if (loginErrorText != null) loginErrorText.text = msg;
        }

        private void SetCreateError(string msg)
        {
            if (createErrorText != null) createErrorText.text = msg;
        }

        private void SetInputsInteractable(bool value)
        {
            if (loginButton)             loginButton.interactable             = value;
            if (openCreateAccountButton) openCreateAccountButton.interactable = value;
            if (loginUsernameInput)      loginUsernameInput.interactable      = value;
            if (loginPasswordInput)      loginPasswordInput.interactable      = value;
        }

        private void ClearLoginFields()
        {
            if (loginUsernameInput) loginUsernameInput.text = "";
            if (loginPasswordInput) loginPasswordInput.text = "";
        }

        private void ClearCreateFields()
        {
            if (createUsernameInput)        createUsernameInput.text        = "";
            if (createPasswordInput)        createPasswordInput.text        = "";
            if (createConfirmPasswordInput) createConfirmPasswordInput.text = "";
        }
    }
}
