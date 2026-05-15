using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using RPG.Data;

namespace RPG.UI
{
    /// <summary>
    /// Slot individual do grid de inventário.
    /// Mostra ícone, quantidade (para stackable) e dispara eventos.
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 6) ===
    ///   - Visual de seleção via cor (sem dependência de Outline).
    ///   - Layout consistente com EquipmentSlotUI e GemSlotWidget.
    /// </summary>
    public class InventorySlotUI : MonoBehaviour,
        IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        [Header("Refs visuais")]
        [SerializeField] private Image    _iconImage;
        [SerializeField] private Image    _background;
        [SerializeField] private TMP_Text _quantityText;
        [SerializeField] private TMP_Text _rarityBorder; // opcional

        [Header("Cores")]
        [SerializeField] private Color _normalColor   = new Color(0.18f, 0.18f, 0.22f, 1f);
        [SerializeField] private Color _selectedColor = new Color(0.95f, 0.8f, 0.3f, 1f);

        public InventorySlotData SlotData { get; private set; }
        public ItemData          ItemData { get; private set; }
        public bool              IsEmpty  => ItemData == null;

        public event System.Action<InventorySlotUI> OnSlotClicked;
        public event System.Action<InventorySlotUI> OnSlotHoverEnter;
        public event System.Action<InventorySlotUI> OnSlotHoverExit;

        // ══════════════════════════════════════════════════════════════════
        // API pública
        // ══════════════════════════════════════════════════════════════════

        public void Setup(InventorySlotData data, ItemData item)
        {
            SlotData = data;
            ItemData = item;

            if (item == null) { ApplyEmptyVisual(); return; }

            if (_iconImage != null)
            {
                _iconImage.sprite  = item.Icon;
                _iconImage.color   = Color.white;
                _iconImage.enabled = item.Icon != null;
            }

            if (_quantityText != null)
            {
                if (item.IsStackable && data.Quantity > 1)
                {
                    _quantityText.text    = data.Quantity.ToString();
                    _quantityText.enabled = true;
                }
                else
                {
                    _quantityText.enabled = false;
                }
            }

            if (_rarityBorder != null)
                _rarityBorder.color = item.RarityColor;
        }

        public void SetEmpty()
        {
            SlotData = default;
            ItemData = null;
            ApplyEmptyVisual();
        }

        public void SetSelected(bool selected)
        {
            if (_background != null)
                _background.color = selected ? _selectedColor : _normalColor;
        }

        private void ApplyEmptyVisual()
        {
            if (_iconImage    != null) _iconImage.enabled    = false;
            if (_quantityText != null) _quantityText.enabled = false;
        }

        // ══════════════════════════════════════════════════════════════════
        // EventSystem
        // ══════════════════════════════════════════════════════════════════

        public void OnPointerClick(PointerEventData eventData) => OnSlotClicked?.Invoke(this);
        public void OnPointerEnter(PointerEventData eventData) => OnSlotHoverEnter?.Invoke(this);
        public void OnPointerExit (PointerEventData eventData) => OnSlotHoverExit?.Invoke(this);
    }
}
