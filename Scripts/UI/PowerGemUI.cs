using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Mirror;
using RPG.Data;
using RPG.Network;
using System.Collections;

namespace RPG.UI
{
    /// <summary>
    /// PowerGemUI — Janela de encaixe das Joias do Poder (tecla P).
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 5 — robustez) ===
    ///
    ///   1. RE-VALIDAÇÃO NO CLIQUE DO SLOT (equip mode):
    ///      Antes, OpenForEquip validava o item UMA VEZ na abertura. Mas
    ///      entre a abertura e o clique no slot Q/W/E/R, o player podia
    ///      descartar o item (via outra UI), morrer e dropar, etc.
    ///      Agora revalidamos no OnGemSlotClicked ANTES de chamar CmdEquipGem.
    ///
    ///   2. FECHA AUTOMATICAMENTE SE INVENTORY MUDA EM EQUIP MODE:
    ///      Subscreve em OnInventoryChanged enquanto estiver em equip mode.
    ///      Se o item de origem sumir do inventário, fecha a janela
    ///      automaticamente com mensagem informativa.
    ///
    ///   3. UNSUBSCRIBE COMPLETO EM OnDestroy E Close:
    ///      Antes, OnDestroy só desinscrevia OnGemLoadoutChanged. Agora também
    ///      desinscreve OnInventoryChanged (subscrito em equip mode).
    /// </summary>
    public class PowerGemUI : MonoBehaviour
    {
        public static PowerGemUI Instance { get; private set; }

        [Header("Painel raiz")]
        [SerializeField] private GameObject panel;
        [SerializeField] private Button     closeButton;
        [SerializeField] private TMP_Text   titleText;
        [SerializeField] private TMP_Text   instructionText;

        [Header("Slots de Joia (ordem: Q, W, E, R)")]
        [SerializeField] private GemSlotWidget slotQ;
        [SerializeField] private GemSlotWidget slotW;
        [SerializeField] private GemSlotWidget slotE;
        [SerializeField] private GemSlotWidget slotR;

        [Header("Ações")]
        [SerializeField] private Button   unequipButton;
        [SerializeField] private TMP_Text unequipButtonLabel;

        [Header("Bind retry")]
        [Tooltip("Quanto tempo (s) o retry aguarda o NetworkInventory aparecer antes de desistir.")]
        [SerializeField] private float bindRetryTimeout = 10f;

        // ── Estado ─────────────────────────────────────────────────────────
        private NetworkInventory  _inventory;
        private bool              _isOpen    = false;

        private bool              _equipMode = false;
        private InventorySlotData _pendingGemSlot;
        private int               _selectedGemSlotIndex = -1;

        private Coroutine _waitAndBindCoroutine;

        // Subscrição ativa apenas em equip mode
        private bool _subscribedToInventoryChanged;

        private static readonly string[] SlotNames  = { "Q", "W", "E", "R" };
        private static readonly string[] SlotLabels = { "[Q]", "[W]", "[E]", "[R]" };

        // ══════════════════════════════════════════════════════════════════
        // Lifecycle
        // ══════════════════════════════════════════════════════════════════

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            if (panel != null) panel.SetActive(false);

            if (closeButton   != null) closeButton.onClick.AddListener(Close);
            if (unequipButton != null)
            {
                unequipButton.onClick.AddListener(OnUnequipClicked);
                unequipButton.gameObject.SetActive(false);
            }

            SetupSlotWidget(slotQ, 0);
            SetupSlotWidget(slotW, 1);
            SetupSlotWidget(slotE, 2);
            SetupSlotWidget(slotR, 3);

            TryBindInventory();
        }

        private void OnDisable()
        {
            StopWaitAndBindCoroutine();
        }

        private void OnDestroy()
        {
            StopWaitAndBindCoroutine();

            // Limpa TODAS as subscrições (loadout + inventory changed, se ativo)
            if (_inventory != null)
            {
                _inventory.OnGemLoadoutChanged -= OnLoadoutChanged;
                if (_subscribedToInventoryChanged)
                    _inventory.OnInventoryChanged -= OnInventoryChangedEquipMode;
            }
            _subscribedToInventoryChanged = false;

            if (closeButton   != null) closeButton.onClick.RemoveListener(Close);
            if (unequipButton != null) unequipButton.onClick.RemoveListener(OnUnequipClicked);

            if (Instance == this) Instance = null;
        }

        private void StopWaitAndBindCoroutine()
        {
            if (_waitAndBindCoroutine != null)
            {
                StopCoroutine(_waitAndBindCoroutine);
                _waitAndBindCoroutine = null;
            }
        }

        private void SetupSlotWidget(GemSlotWidget widget, int slotIndex)
        {
            if (widget == null) return;
            widget.SetHotkeyLabel(SlotLabels[slotIndex]);
            widget.OnClicked    = () => OnGemSlotClicked(slotIndex);
            widget.OnHoverEnter = () => OnGemSlotHoverEnter(slotIndex);
            widget.OnHoverExit  = () => ItemTooltipUI.Instance?.Hide();
        }

        // ── Vínculo com NetworkInventory ───────────────────────────────────

        public void BindInventory(NetworkInventory inventory)
        {
            if (inventory == null || _inventory == inventory) return;

            // Desinscreve do antigo
            if (_inventory != null)
            {
                _inventory.OnGemLoadoutChanged -= OnLoadoutChanged;
                if (_subscribedToInventoryChanged)
                {
                    _inventory.OnInventoryChanged -= OnInventoryChangedEquipMode;
                    _subscribedToInventoryChanged = false;
                }
            }

            _inventory = inventory;
            _inventory.OnGemLoadoutChanged += OnLoadoutChanged;

            // Se estávamos em equip mode, re-subscreve no novo
            if (_equipMode)
            {
                _inventory.OnInventoryChanged += OnInventoryChangedEquipMode;
                _subscribedToInventoryChanged = true;
            }

            StopWaitAndBindCoroutine();

            if (_isOpen) RefreshSlots();
        }

        private bool TryBindInventory()
        {
            if (_inventory != null) return true;
            if (NetworkClient.localPlayer == null) return false;

            var inv = NetworkClient.localPlayer.GetComponent<NetworkInventory>();
            if (inv == null) return false;

            BindInventory(inv);
            return true;
        }

        private IEnumerator WaitAndBind()
        {
            float elapsed = 0f;
            var wait = new WaitForSeconds(0.2f);

            while (_inventory == null && elapsed < bindRetryTimeout)
            {
                yield return wait;
                elapsed += 0.2f;
                if (TryBindInventory()) break;
            }

            _waitAndBindCoroutine = null;

            if (_inventory == null)
            {
                Debug.LogWarning($"[PowerGemUI] Não foi possível vincular ao NetworkInventory após {bindRetryTimeout}s.");
                if (_isOpen && instructionText != null)
                    instructionText.text = "Erro: inventário indisponível.\nReabra a tela.";
            }
            else if (_isOpen)
            {
                RefreshSlots();
                if (_equipMode) HighlightAllSlots(true);
            }
        }

        private void OnLoadoutChanged()
        {
            if (_isOpen) RefreshSlots();
        }

        /// <summary>
        /// Em equip mode, monitoramos o inventário. Se o item de origem sumir
        /// (descartado, equipado em outro slot, dropado por morte), fechamos
        /// a janela automaticamente — caso contrário, o jogador poderia clicar
        /// num slot Q/W/E/R e o CmdEquipGem chegaria com slotIndex inválido.
        /// </summary>
        private void OnInventoryChangedEquipMode()
        {
            if (!_equipMode || !_isOpen || _inventory == null) return;

            bool stillExists = false;
            foreach (var s in _inventory.Slots)
            {
                if (s.SlotIndex == _pendingGemSlot.SlotIndex
                    && s.ItemId == _pendingGemSlot.ItemId)
                {
                    stillExists = true;
                    break;
                }
            }

            if (!stillExists)
            {
                UIManager.Instance?.ShowMessage("A joia não está mais disponível.");
                Close();
            }
        }

        // ── Abrir / Fechar ─────────────────────────────────────────────────

        public void Toggle()
        {
            if (_isOpen) Close();
            else         OpenBrowse();
        }

        public void OpenBrowse()
        {
            _equipMode            = false;
            _pendingGemSlot       = default;
            _selectedGemSlotIndex = -1;

            // Sai do equip mode: desinscreve do InventoryChanged
            if (_subscribedToInventoryChanged && _inventory != null)
            {
                _inventory.OnInventoryChanged -= OnInventoryChangedEquipMode;
                _subscribedToInventoryChanged = false;
            }

            if (titleText       != null) titleText.text       = "Joias do Poder";
            if (instructionText != null) instructionText.text =
                "Clique em um slot com joia para removê-la.";

            _isOpen = true;
            if (panel         != null) panel.SetActive(true);
            if (unequipButton != null) unequipButton.gameObject.SetActive(false);

            HighlightAllSlots(false);

            if (!TryBindInventory())
            {
                StopWaitAndBindCoroutine();
                _waitAndBindCoroutine = StartCoroutine(WaitAndBind());

                if (instructionText != null)
                    instructionText.text = "Conectando ao inventário...";
                return;
            }

            RefreshSlots();
        }

        public void OpenForEquip(InventorySlotData gemSlotData)
        {
            // Valida que existe e é PowerGem ANTES de abrir
            if (_inventory != null)
            {
                bool found = false;
                foreach (var s in _inventory.Slots)
                    if (s.SlotIndex == gemSlotData.SlotIndex
                        && s.ItemId == gemSlotData.ItemId)
                    { found = true; break; }

                if (!found)
                {
                    Debug.LogWarning("[PowerGemUI] OpenForEquip: slot não encontrado no inventário.");
                    return;
                }
            }

            _equipMode            = true;
            _pendingGemSlot       = gemSlotData;
            _selectedGemSlotIndex = -1;

            // Subscreve em InventoryChanged para detectar se o item sumiu
            if (_inventory != null && !_subscribedToInventoryChanged)
            {
                _inventory.OnInventoryChanged += OnInventoryChangedEquipMode;
                _subscribedToInventoryChanged = true;
            }

            var itemData = ItemDatabase.Instance?.GetItem(gemSlotData.ItemId);
            string gemName = itemData?.DisplayName ?? "Joia";

            if (titleText       != null) titleText.text       = "Equipar Joia";
            if (instructionText != null) instructionText.text =
                $"Escolha o slot para equipar:\n<color=#FFD700>{gemName}</color>";

            _isOpen = true;
            if (panel         != null) panel.SetActive(true);
            if (unequipButton != null) unequipButton.gameObject.SetActive(false);

            if (!TryBindInventory())
            {
                StopWaitAndBindCoroutine();
                _waitAndBindCoroutine = StartCoroutine(WaitAndBind());
                return;
            }

            RefreshSlots();
            HighlightAllSlots(true);
        }

        public void Close()
        {
            StopWaitAndBindCoroutine();

            _isOpen               = false;
            _equipMode            = false;
            _selectedGemSlotIndex = -1;

            // Sai do equip mode: desinscreve do InventoryChanged
            if (_subscribedToInventoryChanged && _inventory != null)
            {
                _inventory.OnInventoryChanged -= OnInventoryChangedEquipMode;
                _subscribedToInventoryChanged = false;
            }

            HighlightAllSlots(false);

            slotQ?.SetSelected(false);
            slotW?.SetSelected(false);
            slotE?.SetSelected(false);
            slotR?.SetSelected(false);

            if (panel         != null) panel.SetActive(false);
            if (unequipButton != null) unequipButton.gameObject.SetActive(false);

            ItemTooltipUI.Instance?.Hide();
        }

        // ── Refresh visual ─────────────────────────────────────────────────

        private void RefreshSlots()
        {
            if (_inventory == null) return;

            RefreshSlotWidget(slotQ, 0);
            RefreshSlotWidget(slotW, 1);
            RefreshSlotWidget(slotE, 2);
            RefreshSlotWidget(slotR, 3);

            if (!_equipMode && instructionText != null && _selectedGemSlotIndex < 0)
            {
                bool anyGem = false;
                for (int i = 0; i < 4; i++)
                    if (!string.IsNullOrEmpty(_inventory.GetGemItemId(i))) { anyGem = true; break; }

                instructionText.text = anyGem
                    ? "Clique em um slot com joia para removê-la."
                    : "Nenhuma joia equipada.\nAbra o inventário (I) para equipar.";
            }
        }

        private void RefreshSlotWidget(GemSlotWidget widget, int slotIndex)
        {
            if (widget == null || _inventory == null) return;

            string gemId   = _inventory.GetGemItemId(slotIndex);
            bool   isEmpty = string.IsNullOrEmpty(gemId);
            var    item    = isEmpty ? null : ItemDatabase.Instance?.GetItem(gemId);

            widget.SetGem(item, isEmpty ? null : gemId);
            widget.SetSelected(slotIndex == _selectedGemSlotIndex);

            if (_equipMode)
                widget.SetHighlight(true);
        }

        private void HighlightAllSlots(bool highlight)
        {
            slotQ?.SetHighlight(highlight);
            slotW?.SetHighlight(highlight);
            slotE?.SetHighlight(highlight);
            slotR?.SetHighlight(highlight);
        }

        // ── Eventos de slot ────────────────────────────────────────────────

        private void OnGemSlotClicked(int slotIndex)
        {
            if (_inventory == null) return;

            if (_equipMode)
            {
                // RE-VALIDAÇÃO ANTES DE ENVIAR Cmd
                // Entre OpenForEquip e este clique, o item pode ter sumido.
                bool stillValid = false;
                foreach (var s in _inventory.Slots)
                {
                    if (s.SlotIndex == _pendingGemSlot.SlotIndex
                        && s.ItemId == _pendingGemSlot.ItemId)
                    { stillValid = true; break; }
                }

                if (!stillValid)
                {
                    UIManager.Instance?.ShowMessage("A joia não está mais disponível.");
                    Close();
                    return;
                }

                _inventory.CmdEquipGem(slotIndex, _pendingGemSlot.SlotIndex);
                HighlightAllSlots(false);
                Close();
                UIManager.Instance?.ShowMessage($"Joia equipada no slot {SlotNames[slotIndex]}!");
            }
            else
            {
                string gemId  = _inventory.GetGemItemId(slotIndex);
                bool   hasGem = !string.IsNullOrEmpty(gemId);

                if (hasGem)
                {
                    _selectedGemSlotIndex = slotIndex;
                    RefreshSlots();

                    if (unequipButton != null)
                    {
                        unequipButton.gameObject.SetActive(true);
                        if (unequipButtonLabel != null)
                            unequipButtonLabel.text = $"Retirar joia do slot {SlotNames[slotIndex]}";
                    }

                    if (instructionText != null)
                    {
                        var item = ItemDatabase.Instance?.GetItem(gemId);
                        string name = item?.DisplayName ?? "joia";
                        instructionText.text =
                            $"Slot {SlotNames[slotIndex]}: <color=#FFD700>{name}</color>\nClique em \"Retirar\" para desequipar.";
                    }
                }
                else
                {
                    _selectedGemSlotIndex = -1;
                    RefreshSlots();
                    if (unequipButton != null) unequipButton.gameObject.SetActive(false);

                    if (instructionText != null)
                        instructionText.text = "Este slot está vazio. Clique em um slot com joia.";
                }
            }
        }

        private void OnGemSlotHoverEnter(int slotIndex)
        {
            if (_inventory == null) return;
            string gemId = _inventory.GetGemItemId(slotIndex);
            if (string.IsNullOrEmpty(gemId)) return;
            var item = ItemDatabase.Instance?.GetItem(gemId);
            if (item != null) ItemTooltipUI.Instance?.Show(item);
        }

        private void OnUnequipClicked()
        {
            if (_selectedGemSlotIndex < 0 || _inventory == null) return;

            string slotName = SlotNames[_selectedGemSlotIndex];
            string gemId    = _inventory.GetGemItemId(_selectedGemSlotIndex);
            var    item     = string.IsNullOrEmpty(gemId) ? null : ItemDatabase.Instance?.GetItem(gemId);
            string gemName  = item?.DisplayName ?? "joia";

            _inventory.CmdUnequipGem(_selectedGemSlotIndex);

            UIManager.Instance?.ShowMessage($"{gemName} removida do slot {slotName}.");

            _selectedGemSlotIndex = -1;
            if (unequipButton != null) unequipButton.gameObject.SetActive(false);

            if (instructionText != null)
                instructionText.text = "Clique em um slot com joia para removê-la.";

            RefreshSlots();
        }
    }
}
