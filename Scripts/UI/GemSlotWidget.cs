using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using RPG.Data;

namespace RPG.UI
{
    /// <summary>
    /// Slot Q/W/E/R do PowerGemUI. Mostra ícone da joia equipada,
    /// label do hotkey e estado de seleção/highlight.
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 6) ===
    ///   - Visual atualizado via cores em vez de Outline opcional
    ///     (mais portátil, sem dependência de Outline component).
    /// </summary>
    public class GemSlotWidget : MonoBehaviour,
        IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        [Header("Refs visuais")]
        [SerializeField] private Image    _iconImage;
        [SerializeField] private Image    _background;
        [SerializeField] private TMP_Text _hotkeyLabel;
        [SerializeField] private Sprite   _emptyIcon;

        [Header("Cores")]
        [SerializeField] private Color _normalColor    = new Color(0.15f, 0.15f, 0.18f, 1f);
        [SerializeField] private Color _selectedColor  = new Color(0.95f, 0.8f, 0.3f, 1f);
        [SerializeField] private Color _highlightColor = new Color(0.4f, 0.7f, 1f, 1f);

        public System.Action OnClicked;
        public System.Action OnHoverEnter;
        public System.Action OnHoverExit;

        private string _itemId;
        private bool   _selected;
        private bool   _highlighted;

        public void SetHotkeyLabel(string label)
        {
            if (_hotkeyLabel != null) _hotkeyLabel.text = label;
        }

        public void SetGem(ItemData item, string itemId)
        {
            _itemId = itemId;

            if (_iconImage == null) return;

            if (item != null && item.Icon != null)
            {
                _iconImage.sprite  = item.Icon;
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

        public void SetSelected(bool selected)
        {
            _selected = selected;
            ApplyBackgroundColor();
        }

        public void SetHighlight(bool highlighted)
        {
            _highlighted = highlighted;
            ApplyBackgroundColor();
        }

        private void ApplyBackgroundColor()
        {
            if (_background == null) return;
            if (_selected)         _background.color = _selectedColor;
            else if (_highlighted) _background.color = _highlightColor;
            else                   _background.color = _normalColor;
        }

        // ── EventSystem ────────────────────────────────────────────────────

        public void OnPointerClick(PointerEventData eventData) => OnClicked?.Invoke();
        public void OnPointerEnter(PointerEventData eventData) => OnHoverEnter?.Invoke();
        public void OnPointerExit (PointerEventData eventData) => OnHoverExit?.Invoke();
    }
}
