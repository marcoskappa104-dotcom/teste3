using UnityEngine;
using RPG.Data;
using RPG.Network;
using System.Collections.Generic;

namespace RPG.UI
{
    /// <summary>
    /// Painel lateral do inventário: mostra slots de equipamento (helmet,
    /// chest, weapon, etc).
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 6) ===
    ///
    ///   1. CLEANUP DE INSCRIÇÃO EM OnDestroy:
    ///      Antes, se o GameObject fosse destruído sem unbind manual,
    ///      ficava um listener em OnEquipmentChanged. Defesa.
    ///
    ///   2. DESCOBERTA AUTOMÁTICA DE SLOTS:
    ///      Se a lista _slots não estiver preenchida no Inspector, agora
    ///      descobrimos via GetComponentsInChildren. Reduz fricção de setup.
    /// </summary>
    public class EquipmentPanelUI : MonoBehaviour
    {
        [Header("Slots de Equipamento")]
        [Tooltip("Configure manualmente OU deixe vazio para auto-descobrir nos children.")]
        [SerializeField] private List<EquipmentSlotUI> _slots = new();

        private NetworkInventory _inventory;

        private void Awake()
        {
            // Auto-descoberta se vazio (ergonomia)
            if (_slots.Count == 0)
            {
                var found = GetComponentsInChildren<EquipmentSlotUI>(includeInactive: true);
                _slots.AddRange(found);
            }

            foreach (var s in _slots)
            {
                if (s == null) continue;
                s.OnSlotClicked += OnSlotClicked;
            }
        }

        private void OnDestroy()
        {
            UnbindFromInventory();

            foreach (var s in _slots)
            {
                if (s == null) continue;
                s.OnSlotClicked -= OnSlotClicked;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Bind
        // ══════════════════════════════════════════════════════════════════

        public void BindInventory(NetworkInventory inventory)
        {
            UnbindFromInventory();
            _inventory = inventory;

            if (_inventory != null)
            {
                _inventory.OnEquipmentChanged += RefreshAll;
                RefreshAll();
            }
            else
            {
                ClearAllSlots();
            }
        }

        private void UnbindFromInventory()
        {
            if (_inventory != null)
                _inventory.OnEquipmentChanged -= RefreshAll;
            _inventory = null;
        }

        // ══════════════════════════════════════════════════════════════════
        // Refresh
        // ══════════════════════════════════════════════════════════════════

        public void RefreshAll()
        {
            if (_inventory == null) { ClearAllSlots(); return; }

            foreach (var slot in _slots)
            {
                if (slot == null) continue;

                string itemId = _inventory.GetEquipped(slot.Slot);
                var    item   = string.IsNullOrEmpty(itemId)
                              ? null
                              : ItemDatabase.Instance?.GetItem(itemId);

                slot.SetItem(item);
            }
        }

        private void ClearAllSlots()
        {
            foreach (var slot in _slots)
            {
                if (slot == null) continue;
                slot.SetItem(null);
                slot.SetSelected(false);
            }
        }

        public void ClearSelection()
        {
            foreach (var slot in _slots)
            {
                if (slot == null) continue;
                slot.SetSelected(false);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Callbacks
        // ══════════════════════════════════════════════════════════════════

        private void OnSlotClicked(EquipmentSlotUI slot)
        {
            if (slot == null || _inventory == null) return;

            string itemId = _inventory.GetEquipped(slot.Slot);
            if (string.IsNullOrEmpty(itemId))
            {
                ClearSelection();
                InventoryUI.Instance?.CloseActionPanelExternal();
                return;
            }

            var item = ItemDatabase.Instance?.GetItem(itemId);
            if (item == null)
            {
                ClearSelection();
                return;
            }

            ClearSelection();
            slot.SetSelected(true);

            InventoryUI.Instance?.ShowActionPanelForEquipment(slot.Slot, item);
        }
    }
}
