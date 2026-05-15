using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace RPG.UI
{
    /// <summary>
    /// Slot da barra de skills (Q/W/E/R no HUD). Mostra ícone da skill,
    /// hotkey e cooldown visual (fill radial inverso).
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 6) ===
    ///   - StartCooldown agora trata duration<=0 (limpa cooldown imediatamente).
    ///   - SetIcon fallback claro quando icon==null (slot vazio).
    ///   - Update só roda quando _cooldownActive (perf trivial).
    /// </summary>
    public class SkillSlotUI : MonoBehaviour
    {
        [Header("Refs visuais")]
        [SerializeField] private Image    _iconImage;
        [SerializeField] private Image    _cooldownOverlay; // fillAmount=1 quando full cooldown
        [SerializeField] private TMP_Text _cooldownText;
        [SerializeField] private TMP_Text _hotkeyText;

        private float _cooldownDuration;
        private float _cooldownEndTime;
        private bool  _cooldownActive;

        private void Awake()
        {
            if (_cooldownOverlay != null) _cooldownOverlay.fillAmount = 0f;
            if (_cooldownText    != null) _cooldownText.text          = "";
        }

        public void SetIcon(Sprite icon)
        {
            if (_iconImage == null) return;
            if (icon != null)
            {
                _iconImage.sprite  = icon;
                _iconImage.color   = Color.white;
                _iconImage.enabled = true;
            }
            else
            {
                _iconImage.enabled = false;
            }
        }

        public void SetHotkey(string label)
        {
            if (_hotkeyText != null) _hotkeyText.text = label;
        }

        public void StartCooldown(float duration)
        {
            if (duration <= 0f)
            {
                _cooldownActive = false;
                if (_cooldownOverlay != null) _cooldownOverlay.fillAmount = 0f;
                if (_cooldownText    != null) _cooldownText.text          = "";
                return;
            }

            _cooldownDuration = duration;
            _cooldownEndTime  = Time.time + duration;
            _cooldownActive   = true;

            if (_cooldownOverlay != null) _cooldownOverlay.fillAmount = 1f;
        }

        private void Update()
        {
            if (!_cooldownActive) return;

            float remaining = _cooldownEndTime - Time.time;
            if (remaining <= 0f)
            {
                _cooldownActive = false;
                if (_cooldownOverlay != null) _cooldownOverlay.fillAmount = 0f;
                if (_cooldownText    != null) _cooldownText.text          = "";
                return;
            }

            if (_cooldownOverlay != null)
                _cooldownOverlay.fillAmount = remaining / _cooldownDuration;

            if (_cooldownText != null)
                _cooldownText.text = remaining >= 1f
                    ? Mathf.CeilToInt(remaining).ToString()
                    : remaining.ToString("0.0");
        }
    }
}
