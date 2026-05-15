using System;
using Mirror;

namespace RPG.Data
{
    /// <summary>
    /// Slot do inventário sincronizado via SyncList.
    /// SlotIndex é único e estável — nunca muda após atribuído.
    /// </summary>
    [Serializable]
    public struct InventorySlotData : NetworkMessage
    {
        public int    SlotIndex;
        public string ItemId;
        public int    Quantity;

        public bool IsEmpty => string.IsNullOrEmpty(ItemId);

        public static InventorySlotData Empty(int slotIndex) => new InventorySlotData
        {
            SlotIndex = slotIndex,
            ItemId    = "",
            Quantity  = 0
        };
    }

    /// <summary>
    /// Os 4 slots de Joia do Poder (Q, W, E, R).
    /// Sincronizado via SyncVars no NetworkInventory.
    /// </summary>
    [Serializable]
    public struct PowerGemLoadout : NetworkMessage
    {
        public string SlotQ;
        public string SlotW;
        public string SlotE;
        public string SlotR;

        public string GetSlot(int index) => index switch
        {
            0 => SlotQ ?? "",
            1 => SlotW ?? "",
            2 => SlotE ?? "",
            3 => SlotR ?? "",
            _ => ""
        };

        public PowerGemLoadout WithSlot(int index, string itemId)
        {
            var copy = this;
            switch (index)
            {
                case 0: copy.SlotQ = itemId; break;
                case 1: copy.SlotW = itemId; break;
                case 2: copy.SlotE = itemId; break;
                case 3: copy.SlotR = itemId; break;
            }
            return copy;
        }
    }
}
