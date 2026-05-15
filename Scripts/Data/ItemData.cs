using UnityEngine;
using RPG.Combat;

namespace RPG.Data
{
    public enum ItemType
    {
        PowerGem,    // Joia do Poder — concede uma skill
        Equipment,   // Armadura, arma, escudo, anéis
        Consumable,  // Poção, comida
        Misc         // Materiais, quest items
    }

    public enum ItemRarity
    {
        Common,
        Uncommon,
        Rare,
        Epic,
        Legendary
    }

    /// <summary>
    /// ScriptableObject que define um item do jogo.
    /// O ItemId é a chave de banco — NUNCA altere após o item estar em uso por jogadores.
    /// </summary>
    [CreateAssetMenu(menuName = "RPG/Item Data", fileName = "Item_New")]
    public class ItemData : ScriptableObject
    {
        // ── Identificação ──────────────────────────────────────────────────
        [Header("Identificação")]
        [Tooltip("ID único e estável. NUNCA altere após criar personagens com este item.")]
        public string ItemId = "item_001";

        public string DisplayName = "Item";

        [TextArea(2, 4)]
        public string Description = "Descrição do item.";

        public ItemType   Type   = ItemType.Misc;
        public ItemRarity Rarity = ItemRarity.Common;

        [Header("Visual")]
        public Sprite Icon;

        [Header("Drop")]
        [Tooltip("Peso de drop relativo. 100 = comum, 1 = raríssimo.")]
        [Range(0, 100)]
        public int DropWeight = 10;

        // ── PowerGem ───────────────────────────────────────────────────────
        [Header("PowerGem (use apenas se Type == PowerGem)")]
        public SkillData EmbeddedSkill;

        // ── Equipment ──────────────────────────────────────────────────────
        [Header("Equipment (use apenas se Type == Equipment)")]
        [Tooltip("Slot onde o item se encaixa. Ring1/Ring2 e Earring1/Earring2 são intercambiáveis.")]
        public EquipmentSlot EquipSlot = EquipmentSlot.None;

        [Header("Bônus de Atributo")]
        public int BonusSTR;
        public int BonusAGI;
        public int BonusVIT;
        public int BonusDEX;
        public int BonusINT;
        public int BonusLUK;

        [Header("Bônus de Combate")]
        public float BonusATK;
        public float BonusDEF;
        public float BonusMATK;
        public float BonusMDEF;
        public float BonusHP;
        public float BonusMP;

        [Header("Resistências Elementais (0–75)")]
        [Range(0f, 75f)] public float BonusResistFire;
        [Range(0f, 75f)] public float BonusResistIce;
        [Range(0f, 75f)] public float BonusResistPoison;
        [Range(0f, 75f)] public float BonusResistLightning;

        [Header("Requisitos para Equipar")]
        [Tooltip("Validados server-side. Cliente usa apenas para tooltip.")]
        public EquipmentRequirements Requirements = new EquipmentRequirements();

        [Header("Durabilidade (futuro)")]
        [Tooltip("0 = indestrutível. >0 = degradável.")]
        public int MaxDurability = 0;

        // ── Consumable ─────────────────────────────────────────────────────
        [Header("Consumable (use apenas se Type == Consumable)")]
        public float HealAmount   = 0f;
        public float ManaAmount   = 0f;
        public float BuffDuration = 0f;

        // ── Helpers ────────────────────────────────────────────────────────

        public bool IsPowerGem   => Type == ItemType.PowerGem;
        public bool IsEquipment  => Type == ItemType.Equipment && EquipSlot != EquipmentSlot.None;
        public bool IsConsumable => Type == ItemType.Consumable && (HealAmount > 0f || ManaAmount > 0f);

        public Color RarityColor => Rarity switch
        {
            ItemRarity.Common    => new Color(0.8f, 0.8f, 0.8f),
            ItemRarity.Uncommon  => new Color(0.3f, 0.8f, 0.3f),
            ItemRarity.Rare      => new Color(0.2f, 0.5f, 1.0f),
            ItemRarity.Epic      => new Color(0.7f, 0.2f, 0.9f),
            ItemRarity.Legendary => new Color(1.0f, 0.6f, 0.1f),
            _                    => Color.white
        };

        public string RarityDisplayName => Rarity switch
        {
            ItemRarity.Common    => "Comum",
            ItemRarity.Uncommon  => "Incomum",
            ItemRarity.Rare      => "Raro",
            ItemRarity.Epic      => "Épico",
            ItemRarity.Legendary => "Lendário",
            _                    => Rarity.ToString()
        };

        /// <summary>True se este equipamento concede algum bônus relevante.</summary>
        public bool HasAnyBonus()
        {
            if (!IsEquipment) return false;
            return BonusSTR != 0 || BonusAGI != 0 || BonusVIT != 0
                || BonusDEX != 0 || BonusINT != 0 || BonusLUK != 0
                || BonusATK > 0f || BonusDEF > 0f || BonusMATK > 0f || BonusMDEF > 0f
                || BonusHP > 0f || BonusMP > 0f
                || BonusResistFire > 0f || BonusResistIce > 0f
                || BonusResistPoison > 0f || BonusResistLightning > 0f;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (Type == ItemType.Equipment && EquipSlot == EquipmentSlot.None)
                Debug.LogWarning($"[ItemData] '{name}' é Equipment mas EquipSlot está vazio.");

            if (Type != ItemType.Equipment && EquipSlot != EquipmentSlot.None)
                Debug.LogWarning($"[ItemData] '{name}' tem EquipSlot mas Type não é Equipment.");

            if (Type == ItemType.PowerGem && EmbeddedSkill == null)
                Debug.LogWarning($"[ItemData] '{name}' é PowerGem mas EmbeddedSkill é nulo.");

            if (Type == ItemType.Consumable && HealAmount == 0f && ManaAmount == 0f)
                Debug.LogWarning($"[ItemData] '{name}' é Consumable mas não restaura HP nem MP.");

            if (Requirements != null && Requirements.MinLevel < 1)
                Requirements.MinLevel = 1;

            if (MaxDurability < 0) MaxDurability = 0;
        }
#endif
    }
}
