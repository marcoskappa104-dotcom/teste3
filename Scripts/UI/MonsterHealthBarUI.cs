using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace RPG.UI
{
    /// <summary>
    /// Barra de vida flutuante sobre monstros (worldspace canvas).
    /// Acompanha o monstro via parent transform e gira para encarar a câmera.
    /// Auto-esconde quando HP está full.
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 6) ===
    ///
    ///   1. CACHE DE Camera.main:
    ///      Antes, LateUpdate fazia Camera.main toda frame (FindGameObjectWithTag
    ///      internamente). Agora cacheamos uma vez; refrescamos se virar null.
    ///
    ///   2. NÃO EXECUTA EM SERVIDOR DEDICADO:
    ///      enabled=false em batchmode evita LateUpdate desnecessário.
    /// </summary>
    public class MonsterHealthBarUI : MonoBehaviour
    {
        [Header("Refs")]
        [SerializeField] private Slider   _hpBar;
        [SerializeField] private TMP_Text _nameText;
        [SerializeField] private TMP_Text _levelText;
        [SerializeField] private CanvasGroup _canvasGroup;

        [Header("Comportamento")]
        [SerializeField] private bool _hideWhenFull = true;
        [SerializeField] private Vector3 _worldOffset = new Vector3(0f, 2.2f, 0f);

        private Transform _target;
        private Camera    _camera;

        private void Awake()
        {
            if (Application.isBatchMode)
            {
                enabled = false;
                gameObject.SetActive(false);
            }
        }

        public void SetTarget(Transform t, string monsterName, int level)
        {
            _target = t;
            if (_nameText  != null) _nameText.text  = monsterName ?? "";
            if (_levelText != null) _levelText.text = level > 0 ? $"Lv {level}" : "";
        }

        /// <summary>
        /// Atualiza a barra de vida. Chamado pelo NetworkMonsterEntity
        /// quando o HP do monstro muda.
        /// </summary>
        public void UpdateBar(float current, float max)
        {
            if (_hpBar != null)
            {
                _hpBar.maxValue = Mathf.Max(1f, max);
                _hpBar.value    = current;
            }

            if (_hideWhenFull)
            {
                bool show = current < max - 0.01f;
                SetVisible(show);
            }
        }

        /// <summary>Alias retrocompatível para UpdateBar.</summary>
        public void SetHP(float current, float max) => UpdateBar(current, max);

        public void SetVisible(bool visible)
        {
            if (_canvasGroup != null) _canvasGroup.alpha = visible ? 1f : 0f;
            else gameObject.SetActive(visible);
        }

        private void LateUpdate()
        {
            if (_target == null) return;

            transform.position = _target.position + _worldOffset;

            if (_camera == null) _camera = Camera.main;
            if (_camera != null)
                transform.forward = (transform.position - _camera.transform.position).normalized;
        }
    }
}