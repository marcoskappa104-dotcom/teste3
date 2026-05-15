using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RPG.Character;
using RPG.Combat;

namespace RPG.UI
{
    /// <summary>
    /// UIManager — HUD principal do jogador.
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 5 — modernização) ===
    ///
    ///   1. FindObjectOfType → FindFirstObjectByType:
    ///      FindObjectOfType está deprecated desde Unity 2023.1 e gera warning
    ///      em projetos modernos. Substituído por FindFirstObjectByType
    ///      (mais rápido também — não filtra ordem por padrão).
    ///
    ///   2. CLEANUP REFORÇADO EM OnDestroy:
    ///      Antes, alguns listeners de botões só eram removidos se a callback
    ///      era não-null. Agora limpamos defensivamente todos.
    ///
    ///   3. RebindLocalPlayer SEPARADO:
    ///      Re-bind do mesmo player não chama UnsubscribeFromPlayer
    ///      desnecessariamente. Otimização menor mas elimina churn.
    /// </summary>
    public class UIManager : MonoBehaviour
    {
        public static UIManager Instance { get; private set; }

        [Header("Player HUD")]
        [SerializeField] private Slider   hpBar;
        [SerializeField] private TMP_Text hpText;
        [SerializeField] private Slider   mpBar;
        [SerializeField] private TMP_Text mpText;
        [SerializeField] private TMP_Text playerNameText;
        [SerializeField] private TMP_Text levelText;

        [Header("Target Panel")]
        [SerializeField] private GameObject targetPanel;
        [SerializeField] private TMP_Text   targetNameText;
        [SerializeField] private Slider     targetHPBar;
        [SerializeField] private TMP_Text   targetHPText;

        [Header("Skill Bar")]
        [SerializeField] private SkillSlotUI[] skillSlots;
        [SerializeField] private string[] hotkeyLabels = { "Q", "W", "E", "R" };

        [Header("Message")]
        [SerializeField] private TMP_Text messageText;
        [SerializeField] private float    messageDisplayTime = 2f;

        [Header("Experience")]
        [SerializeField] private Slider   expBar;
        [SerializeField] private TMP_Text expText;

        [Header("Attribute Window")]
        [SerializeField] private AttributeWindowUI attributeWindow;
        [SerializeField] private Button            attributeWindowButton;

        [Header("Atalhos de UI (opcional)")]
        [SerializeField] private Button inventoryHudButton;
        [SerializeField] private Button powerGemHudButton;

        private PlayerEntity              _player;
        private SkillSystem               _skills;
        private RPG.Network.NetworkPlayer _netPlayer;
        private float                     _messageTimer;

        // Callbacks armazenadas para permitir RemoveListener exato
        private UnityEngine.Events.UnityAction _attributeButtonCallback;
        private UnityEngine.Events.UnityAction _inventoryButtonCallback;
        private UnityEngine.Events.UnityAction _powerGemButtonCallback;

        private bool _hudButtonsRegistered = false;
        private bool _attributeButtonRegistered = false;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            ClearTargetPanel();
            if (messageText != null) messageText.text = "";

            // Botão da janela de atributos — singleton geralmente sempre ativo
            if (attributeWindowButton != null && !_attributeButtonRegistered)
            {
                _attributeButtonCallback = () => attributeWindow?.Toggle();
                attributeWindowButton.onClick.AddListener(_attributeButtonCallback);
                _attributeButtonRegistered = true;
            }

            RegisterHudButtonsSafe();

            // Modo offline (sem rede): tenta achar PlayerEntity na cena
            var player = FindFirstObjectByType<PlayerEntity>();
            if (player != null && player.IsInitialized)
                BindLocalPlayer(player);
        }

        private void OnDestroy()
        {
            UnsubscribeFromPlayer();
            UnsubscribeFromSkills();

            // Remove listeners dos botões usando as referências exatas
            if (attributeWindowButton != null && _attributeButtonCallback != null)
            {
                attributeWindowButton.onClick.RemoveListener(_attributeButtonCallback);
                _attributeButtonCallback = null;
            }

            UnregisterHudButtons();

            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Registra os listeners dos botões de HUD UMA VEZ.
        /// Reentrante: se já registrado, retorna imediatamente.
        /// </summary>
        private void RegisterHudButtonsSafe()
        {
            if (_hudButtonsRegistered) return;

            // Fallback de log se singletons estão null
            if (InventoryUI.Instance == null)
            {
                var found = FindFirstObjectByType<InventoryUI>();
                if (found != null)
                    Debug.LogWarning("[UIManager] InventoryUI.Instance era null — encontrado via FindFirstObjectByType. " +
                                     "Verifique se o GameObject do InventoryUI está ATIVO na hierarquia.");
            }
            if (PowerGemUI.Instance == null)
            {
                var found = FindFirstObjectByType<PowerGemUI>();
                if (found != null)
                    Debug.LogWarning("[UIManager] PowerGemUI.Instance era null — encontrado via FindFirstObjectByType. " +
                                     "Verifique se o GameObject do PowerGemUI está ATIVO na hierarquia.");
            }

            // Inventory button
            if (inventoryHudButton != null && _inventoryButtonCallback == null)
            {
                _inventoryButtonCallback = () =>
                {
                    if (InventoryUI.Instance != null)
                        InventoryUI.Instance.Toggle();
                    else
                        Debug.LogWarning("[UIManager] InventoryUI.Instance é null ao clicar no botão!");
                };
                inventoryHudButton.onClick.AddListener(_inventoryButtonCallback);
            }

            // Power Gem button
            if (powerGemHudButton != null && _powerGemButtonCallback == null)
            {
                _powerGemButtonCallback = () =>
                {
                    if (PowerGemUI.Instance != null)
                        PowerGemUI.Instance.Toggle();
                    else
                        Debug.LogWarning("[UIManager] PowerGemUI.Instance é null ao clicar no botão! " +
                                         "Certifique-se que o GameObject está ATIVO na hierarquia.");
                };
                powerGemHudButton.onClick.AddListener(_powerGemButtonCallback);
            }

            bool nothingToRegister   = inventoryHudButton == null && powerGemHudButton == null;
            bool somethingRegistered = (inventoryHudButton == null || _inventoryButtonCallback != null)
                                    && (powerGemHudButton  == null || _powerGemButtonCallback  != null);

            if (nothingToRegister || somethingRegistered)
                _hudButtonsRegistered = true;
        }

        private void UnregisterHudButtons()
        {
            if (inventoryHudButton != null && _inventoryButtonCallback != null)
            {
                inventoryHudButton.onClick.RemoveListener(_inventoryButtonCallback);
                _inventoryButtonCallback = null;
            }
            if (powerGemHudButton != null && _powerGemButtonCallback != null)
            {
                powerGemHudButton.onClick.RemoveListener(_powerGemButtonCallback);
                _powerGemButtonCallback = null;
            }
            _hudButtonsRegistered = false;
        }

        // ── Vinculação ────────────────────────────────────────────────────

        public void BindLocalPlayer(PlayerEntity player)
        {
            if (player == null) return;

            // Re-bind do MESMO player: só atualiza, sem desinscrever/reinscrever
            if (_player == player)
            {
                attributeWindow?.BindPlayer(player);
                if (player.IsInitialized) ForceRefreshAll();

                // Re-tenta registrar botões (singletons podem ter ficado prontos depois)
                RegisterHudButtonsSafe();
                return;
            }

            // Player diferente — limpa subscrições antigas
            UnsubscribeFromPlayer();
            UnsubscribeFromSkills();

            _player    = player;
            _skills    = player.GetComponent<SkillSystem>();
            _netPlayer = player.GetComponent<RPG.Network.NetworkPlayer>();

            _player.OnHPChanged    += UpdateHP;
            _player.OnMPChanged    += UpdateMP;
            _player.OnStatsChanged += OnStatsChangedHandler;
            _player.OnInitialized  += OnPlayerInitialized;

            if (_skills != null)
            {
                _skills.OnCooldownStarted      += OnSkillCooldown;
                _skills.OnSkillBarNeedsRefresh += InitSkillBar;
                InitSkillBar();
            }

            attributeWindow?.BindPlayer(player);

            var inventory = player.GetComponent<RPG.Network.NetworkInventory>();
            if (inventory != null)
            {
                InventoryUI.Instance?.BindInventory(inventory);
                PowerGemUI.Instance?.BindInventory(inventory);
            }

            RegisterHudButtonsSafe();

            if (player.IsInitialized)
                ForceRefreshAll();
        }

        private void UnsubscribeFromPlayer()
        {
            if (_player == null) return;
            _player.OnHPChanged    -= UpdateHP;
            _player.OnMPChanged    -= UpdateMP;
            _player.OnStatsChanged -= OnStatsChangedHandler;
            _player.OnInitialized  -= OnPlayerInitialized;
        }

        private void UnsubscribeFromSkills()
        {
            if (_skills == null) return;
            _skills.OnCooldownStarted      -= OnSkillCooldown;
            _skills.OnSkillBarNeedsRefresh -= InitSkillBar;
        }

        private void OnPlayerInitialized() => ForceRefreshAll();

        private void OnSkillCooldown(int index, float duration)
        {
            if (skillSlots != null && index >= 0 && index < skillSlots.Length)
                skillSlots[index]?.StartCooldown(duration);
        }

        private void OnStatsChangedHandler()
        {
            if (_player == null || !_player.IsInitialized) return;
            int level = _netPlayer != null ? _netPlayer.Level : (_player.Data?.Level ?? 1);
            if (levelText != null) levelText.text = $"Lv {level}";
        }

        private void InitSkillBar()
        {
            if (_skills == null || skillSlots == null) return;

            for (int i = 0; i < skillSlots.Length; i++)
            {
                if (skillSlots[i] == null) continue;

                var skill = _skills.GetSkill(i);
                skillSlots[i].SetIcon(skill?.Icon);

                if (hotkeyLabels != null && i < hotkeyLabels.Length)
                    skillSlots[i].SetHotkey(hotkeyLabels[i]);
            }
        }

        // ── Update — só timer de mensagem ─────────────────────────────────

        private void Update()
        {
            if (_messageTimer > 0)
            {
                _messageTimer -= Time.deltaTime;
                if (_messageTimer <= 0 && messageText != null)
                    messageText.text = "";
            }
        }

        // ── HP / MP ───────────────────────────────────────────────────────

        private void UpdateHP(float current, float max)
        {
            if (hpBar  != null) { hpBar.maxValue = Mathf.Max(1f, max); hpBar.value = current; }
            if (hpText != null) hpText.text = $"{current:0}/{max:0}";
        }

        private void UpdateMP(float current, float max)
        {
            if (mpBar  != null) { mpBar.maxValue = Mathf.Max(1f, max); mpBar.value = current; }
            if (mpText != null) mpText.text = $"{current:0}/{max:0}";
        }

        private void ForceRefreshAll()
        {
            if (_player == null) return;

            float hp = _player.CurrentHP, maxHp = _player.Stats?.MaxHP ?? 1f;
            float mp = _player.CurrentMP, maxMp = _player.Stats?.MaxMP ?? 1f;

            UpdateHP(hp, maxHp);
            UpdateMP(mp, maxMp);

            if (playerNameText != null) playerNameText.text = _player.Data?.CharacterName ?? "Player";

            int level = _netPlayer != null ? _netPlayer.Level : (_player.Data?.Level ?? 1);
            if (levelText != null) levelText.text = $"Lv {level}";

            if (_netPlayer != null)
                RefreshExpBar(_netPlayer.Experience, _netPlayer.ExperienceToNextLevel);

            InitSkillBar();
        }

        public void RefreshLevel(int newLevel)
        {
            if (levelText != null) levelText.text = $"Lv {newLevel}";
        }

        public void RefreshExpBar(long exp, long expToNext)
        {
            if (expBar  != null) { expBar.maxValue = Mathf.Max(1f, expToNext); expBar.value = exp; }
            if (expText != null) expText.text = $"{exp}/{expToNext}";
        }

        // ── Target Panel ──────────────────────────────────────────────────

        public void UpdateTargetPanel(ITargetable target)
        {
            if (target == null) { ClearTargetPanel(); return; }
            if (targetPanel    != null) targetPanel.SetActive(true);
            if (targetNameText != null) targetNameText.text = target.DisplayName;
            RefreshTargetHP(target);
        }

        public void RefreshTargetPanel(ITargetable target)
        {
            if (target == null || targetPanel == null || !targetPanel.activeSelf) return;
            RefreshTargetHP(target);
        }

        private void RefreshTargetHP(ITargetable target)
        {
            if (targetHPBar  != null) { targetHPBar.maxValue = Mathf.Max(1f, target.MaxHP); targetHPBar.value = target.CurrentHP; }
            if (targetHPText != null) targetHPText.text = $"{target.CurrentHP:0}/{target.MaxHP:0}";
        }

        public void ClearTargetPanel()
        {
            if (targetPanel != null) targetPanel.SetActive(false);
        }

        // ── Message ───────────────────────────────────────────────────────

        public void ShowMessage(string msg)
        {
            if (messageText == null) return;
            messageText.text = msg;
            _messageTimer    = messageDisplayTime;
        }
    }
}
