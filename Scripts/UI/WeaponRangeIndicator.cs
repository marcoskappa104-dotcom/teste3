using UnityEngine;
using RPG.Character;
using RPG.Combat;

namespace RPG.UI
{
    /// <summary>
    /// Desenha um círculo no chão indicando o alcance do ataque básico
    /// quando o jogador segura a tecla configurada (default: Left Alt).
    ///
    /// Útil para visualizar a diferença entre arco (range longo) e espada
    /// (range curto). Reativo a troca de arma.
    ///
    /// SETUP:
    ///   1. Adicione este componente em algum lugar da cena (ou no playerPrefab).
    ///   2. Configure um prefab de "ring" decal — pode ser um Quad com material
    ///      transparente e textura de círculo. O componente escala dinamicamente.
    ///   3. Atribua showKey (default Alt esquerdo).
    /// </summary>
    public class WeaponRangeIndicator : MonoBehaviour
    {
        [Header("Visual")]
        [Tooltip("Prefab do círculo (Quad com material transparente). " +
                 "Será escalado para o range da arma.")]
        [SerializeField] private GameObject ringPrefab;

        [Tooltip("Offset vertical para evitar z-fight com o chão.")]
        [SerializeField] private float groundOffset = 0.02f;

        [Header("Cores por tipo de ataque")]
        [SerializeField] private Color meleeColor   = new Color(1f, 0.5f, 0f, 0.5f);
        [SerializeField] private Color rangedColor  = new Color(0.6f, 1f, 0.3f, 0.5f);
        [SerializeField] private Color magicColor   = new Color(0.4f, 0.6f, 1f, 0.5f);

        [Header("Input")]
        [SerializeField] private KeyCode showKey = KeyCode.LeftAlt;

        [Tooltip("Se true, mostra apenas enquanto a tecla está PRESSIONADA. " +
                 "Se false, alterna ao pressionar.")]
        [SerializeField] private bool holdToShow = true;

        // ── Estado ─────────────────────────────────────────────────────────
        private GameObject        _ringInstance;
        private Renderer          _ringRenderer;
        private MaterialPropertyBlock _propBlock;
        private BasicAttackSystem _attackSystem;
        private PlayerEntity      _player;
        private bool              _toggleVisible;

        private void Start()
        {
            _propBlock = new MaterialPropertyBlock();

            if (ringPrefab != null)
            {
                _ringInstance = Instantiate(ringPrefab, transform);
                _ringInstance.SetActive(false);
                _ringRenderer = _ringInstance.GetComponentInChildren<Renderer>();
            }
        }

        private void OnDestroy()
        {
            if (_ringInstance != null) Destroy(_ringInstance);
        }

        /// <summary>
        /// Vincula o indicador ao jogador local. Chamado pelo NetworkUIConnector
        /// ou pelo BasicAttackSystem após OnStartLocalPlayer.
        /// </summary>
        public void BindToLocalPlayer(PlayerEntity player)
        {
            _player       = player;
            _attackSystem = player?.GetComponent<BasicAttackSystem>();
        }

        private void Update()
        {
            // Tentativa tardia de bind se ainda não temos referência
            if (_player == null) return;
            if (_ringInstance == null) return;

            bool shouldShow = HoldOrToggleVisible();

            if (!shouldShow)
            {
                if (_ringInstance.activeSelf)
                    _ringInstance.SetActive(false);
                return;
            }

            UpdateRing();
        }

        private bool HoldOrToggleVisible()
        {
            if (UIInputUtils.IsTypingInInputField()) return false;

            if (holdToShow)
                return Input.GetKey(showKey);

            if (Input.GetKeyDown(showKey)) _toggleVisible = !_toggleVisible;
            return _toggleVisible;
        }

        private void UpdateRing()
        {
            if (_attackSystem == null) return;
            var profile = _attackSystem.CurrentProfile;
            if (profile == null) return;

            // Posição: no chão sob o jogador
            Vector3 pos = _player.transform.position;
            pos.y += groundOffset;
            _ringInstance.transform.position = pos;
            _ringInstance.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            // Escala: diâmetro = 2 * range
            float diameter = profile.Range * 2f;
            _ringInstance.transform.localScale = new Vector3(diameter, diameter, 1f);

            // Cor baseada no tipo de ataque
            if (_ringRenderer != null)
            {
                Color c = profile.UsesProjectile
                    ? (profile.IsPhysical ? rangedColor : magicColor)
                    : meleeColor;

                _ringRenderer.GetPropertyBlock(_propBlock);
                _propBlock.SetColor("_Color",     c);
                _propBlock.SetColor("_BaseColor", c);
                _ringRenderer.SetPropertyBlock(_propBlock);
            }

            if (!_ringInstance.activeSelf) _ringInstance.SetActive(true);
        }
    }
}
