using UnityEngine;
using TMPro;
using UnityEngine.UI;
using Mirror;
using RPG.Data;
using RPG.Network;
using RPG.Combat;

namespace RPG.UI
{
    /// <summary>
    /// HUD pequeno que mostra a arma equipada e o tipo de ataque básico.
    /// Exibe ícone + nome + tipo de dano (Físico/Mágico) + range.
    ///
    /// Reativo: atualiza automaticamente quando o jogador troca de arma.
    /// </summary>
    public class WeaponHudInfo : MonoBehaviour
    {
        [Header("Refs visuais")]
        [SerializeField] private Image    _weaponIcon;
        [SerializeField] private Sprite   _unarmedIcon;
        [SerializeField] private TMP_Text _weaponNameText;
        [SerializeField] private TMP_Text _attackInfoText;

        [Header("Cores")]
        [SerializeField] private Color _physicalColor = new Color(1f, 0.7f, 0.4f);
        [SerializeField] private Color _magicalColor  = new Color(0.5f, 0.7f, 1f);

        private NetworkInventory _inventory;
        private bool             _subscribed;

        private void Update()
        {
            if (_inventory != null) return;
            TryBind();
        }

        private void OnDestroy()
        {
            Unsubscribe();
        }

        private void TryBind()
        {
            if (!NetworkClient.active || NetworkClient.localPlayer == null) return;

            var inv = NetworkClient.localPlayer.GetComponent<NetworkInventory>();
            if (inv == null) return;

            _inventory = inv;
            _inventory.OnEquipmentChanged += Refresh;
            _subscribed = true;

            Refresh();
        }

        private void Unsubscribe()
        {
            if (!_subscribed || _inventory == null) return;
            _inventory.OnEquipmentChanged -= Refresh;
            _subscribed = false;
        }

        private void Refresh()
        {
            if (_inventory == null) return;

            string weaponId = _inventory.GetEquipped(EquipmentSlot.Weapon);
            ItemData item = string.IsNullOrEmpty(weaponId)
                ? null
                : ItemDatabase.Instance?.GetItem(weaponId);

            if (item == null || !item.IsWeapon)
            {
                ShowUnarmed();
                return;
            }

            ShowWeapon(item);
        }

        private void ShowUnarmed()
        {
            if (_weaponIcon != null)
            {
                _weaponIcon.sprite = _unarmedIcon;
                _weaponIcon.enabled = _unarmedIcon != null;
                _weaponIcon.color = Color.white;
            }
            if (_weaponNameText != null)
                _weaponNameText.text = "Sem arma";
            if (_attackInfoText != null)
            {
                _attackInfoText.color = _physicalColor;
                var prof = WeaponAttackProfile.Default(WeaponType.Unarmed);
                _attackInfoText.text = $"Soco · {prof.Range:0.#}m";
            }
        }

        private void ShowWeapon(ItemData item)
        {
            var profile = item.GetEffectiveAttackProfile();

            if (_weaponIcon != null)
            {
                _weaponIcon.sprite  = item.Icon;
                _weaponIcon.enabled = item.Icon != null;
                _weaponIcon.color   = Color.white;
            }

            if (_weaponNameText != null)
            {
                _weaponNameText.text  = item.DisplayName;
                _weaponNameText.color = item.RarityColor;
            }

            if (_attackInfoText != null)
            {
                _attackInfoText.color = profile.IsPhysical ? _physicalColor : _magicalColor;
                string damageType = profile.IsPhysical ? "Físico" : "Mágico";
                _attackInfoText.text = $"{damageType} · {profile.Range:0.#}m";
            }
        }
    }
}
