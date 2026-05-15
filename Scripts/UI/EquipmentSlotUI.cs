using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using RPG.Data;

namespace RPG.UI
{
    /// <summary>
    /// Slot individual de equipamento no EquipmentPanelUI.
    /// Mostra ícone do item equipado (ou ícone de slot vazio se nada).
    /// Clique dispara OnSlotClicked; hover mostra tooltip.
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 6) ===
    ///   - Tooltip helper centralizado (usa ItemTooltipUI.Instance).
    ///   - Highlight/selected gerenciado via image color para evitar
    ///     dependência de Outline component opcional.
    /// </summary>
    public class EquipmentSlotUI : MonoBehaviour,
        IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        [Header("Configuração do Slot")]
        [SerializeField] private EquipmentSlot _slot = EquipmentSlot.Helmet;
        [SerializeField] private Sprite        _emptyIcon;

        [Header("Refs visuais")]
        [SerializeField] private Image    _iconImage;
        [SerializeField] private Image    _background;
        [SerializeField] private TMP_Text _slotLabel;

        [Header("Cores")]
        [SerializeField] private Color _normalColor   = new Color(0.15f, 0.15f, 0.18f, 1f);
        [SerializeField] private Color _selectedColor = new Color(0.95f, 0.8f, 0.3f, 1f);

        public EquipmentSlot Slot     => _slot;
        public ItemData      ItemData { get; private set; }
        public bool          IsEmpty  => ItemData == null;

        public event System.Action<EquipmentSlotUI> OnSlotClicked;

        private void Awake()
        {
            if (_slotLabel != null)
                _slotLabel.text = EquipmentSlotEx.DisplayName(_slot);
            SetSelected(false);
            UpdateVisual();
        }

        public void SetItem(ItemData item)
        {
            ItemData = item;
            UpdateVisual();
        }

        public void SetSelected(bool selected)
        {
            if (_background != null)
                _background.color = selected ? _selectedColor : _normalColor;
        }

        private void UpdateVisual()
        {
            if (_iconImage != null)
            {
                if (ItemData != null && ItemData.Icon != null)
                {
                    _iconImage.sprite  = ItemData.Icon;
                    _iconImage.color   = Color.white;
                    _iconImage.enabled = true;
                }
                else if (_emptyIcon != null)
                {
                    _iconImage.sprite  = _emptyIcon;
                    _iconImage.color   = new Color(1f, 1f, 1f, 0.35f);
                    _iconImage.enabled = true;
                }
                else
                {
                    _iconImage.enabled = false;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Eventos do EventSystem
        // ══════════════════════════════════════════════════════════════════

        public void OnPointerClick(PointerEventData eventData)
        {
            OnSlotClicked?.Invoke(this);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (ItemData != null)
                ItemTooltipUI.Instance?.ShowForItem(ItemData, transform as RectTransform);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            ItemTooltipUI.Instance?.Hide();
        }
    }
}
