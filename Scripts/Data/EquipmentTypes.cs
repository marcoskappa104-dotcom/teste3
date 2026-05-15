using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace RPG.Data
{
    /// <summary>
    /// Slots de equipamento. Os reservados (Cape, Necklace, Earring1/2) já existem
    /// no protocolo de rede — para ativá-los basta incluí-los em ActiveSlots e
    /// criar o widget de UI correspondente.
    /// </summary>
    public enum EquipmentSlot : byte
    {
        None      = 0,

        // Ativos
        Weapon    = 1,
        Shield    = 2,
        Helmet    = 3,
        Armor     = 4,
        Gloves    = 5,
        Boots     = 6,
        Ring1     = 7,
        Ring2     = 8,

        // Reservados (protocolo já compatível)
        Cape      = 9,
        Necklace  = 10,
        Earring1  = 11,
        Earring2  = 12,
    }

    [Flags]
    public enum CharacterRaceFlags : byte
    {
        None   = 0,
        Human  = 1 << 0,
        Elf    = 1 << 1,
        Dwarf  = 1 << 2,
        Orc    = 1 << 3,
        Undead = 1 << 4,
        All    = Human | Elf | Dwarf | Orc | Undead
    }

    /// <summary>
    /// Item equipado — sincronizado via SyncList.
    /// Apenas o ItemId trafega na rede; o ItemData completo é resolvido no
    /// cliente via ItemDatabase. Durability/MaxDurability fazem parte do
    /// protocolo desde já para suportar degradação futura.
    /// </summary>
    [Serializable]
    public struct EquippedItemData : IEquatable<EquippedItemData>
    {
        public byte   Slot;
        public string ItemId;
        public int    Durability;     // -1 = indestrutível
        public int    MaxDurability;  // 0 = indestrutível

        public EquipmentSlot SlotEnum
        {
            get => (EquipmentSlot)Slot;
            set => Slot = (byte)value;
        }

        public bool IsEmpty => string.IsNullOrEmpty(ItemId);

        public static EquippedItemData Make(EquipmentSlot slot, string itemId,
                                            int durability = -1, int maxDurability = 0)
        {
            return new EquippedItemData
            {
                Slot          = (byte)slot,
                ItemId        = itemId ?? "",
                Durability    = maxDurability > 0
                    ? Math.Max(0, durability < 0 ? maxDurability : durability)
                    : -1,
                MaxDurability = maxDurability
            };
        }

        public bool Equals(EquippedItemData other)
            => Slot == other.Slot
               && ItemId == other.ItemId
               && Durability == other.Durability
               && MaxDurability == other.MaxDurability;

        public override bool Equals(object obj) => obj is EquippedItemData e && Equals(e);

        public override int GetHashCode()
            => unchecked((Slot * 397) ^ (ItemId?.GetHashCode() ?? 0)
                         ^ Durability ^ MaxDurability);
    }

    /// <summary>
    /// Requisitos para equipar. Validados SOMENTE no servidor.
    /// O cliente os usa apenas para tooltips informativos.
    /// </summary>
    [Serializable]
    public class EquipmentRequirements
    {
        public int MinLevel = 1;
        public int MinSTR, MinAGI, MinVIT, MinDEX, MinINT, MinLUK;

        [Tooltip("Bit-flags de raças permitidas. Padrão: All.")]
        public CharacterRaceFlags AllowedRaces = CharacterRaceFlags.All;

        public bool Check(int level,
                          int totalSTR, int totalAGI, int totalVIT,
                          int totalDEX, int totalINT, int totalLUK,
                          CharacterRace race,
                          out string failReason)
        {
            if (level < MinLevel)
            { failReason = $"Requer nível {MinLevel} (você é {level})."; return false; }

            if (totalSTR < MinSTR) { failReason = $"Requer STR {MinSTR}."; return false; }
            if (totalAGI < MinAGI) { failReason = $"Requer AGI {MinAGI}."; return false; }
            if (totalVIT < MinVIT) { failReason = $"Requer VIT {MinVIT}."; return false; }
            if (totalDEX < MinDEX) { failReason = $"Requer DEX {MinDEX}."; return false; }
            if (totalINT < MinINT) { failReason = $"Requer INT {MinINT}."; return false; }
            if (totalLUK < MinLUK) { failReason = $"Requer LUK {MinLUK}."; return false; }

            var raceFlag = EquipmentSlotEx.ToFlag(race);
            if ((AllowedRaces & raceFlag) == 0)
            { failReason = "Sua raça não pode equipar este item."; return false; }

            failReason = null;
            return true;
        }

        public bool CheckBasic(int level,
                               int totalSTR, int totalAGI, int totalVIT,
                               int totalDEX, int totalINT, int totalLUK)
        {
            return level    >= MinLevel
                && totalSTR >= MinSTR && totalAGI >= MinAGI && totalVIT >= MinVIT
                && totalDEX >= MinDEX && totalINT >= MinINT && totalLUK >= MinLUK;
        }
    }

    public static class EquipmentSlotEx
    {
        /// <summary>
        /// Slots visíveis hoje. Adicione novos aqui + crie EquipmentSlotUI no painel.
        /// </summary>
        public static readonly EquipmentSlot[] ActiveSlots =
        {
            EquipmentSlot.Weapon,
            EquipmentSlot.Shield,
            EquipmentSlot.Helmet,
            EquipmentSlot.Armor,
            EquipmentSlot.Gloves,
            EquipmentSlot.Boots,
            EquipmentSlot.Ring1,
            EquipmentSlot.Ring2,
        };

        private static readonly HashSet<EquipmentSlot> _activeSet = new HashSet<EquipmentSlot>(ActiveSlots);

        // Cache para evitar alocação em hot paths (validações por hit)
        private static readonly CharacterRaceFlags[] _raceFlagCache;

        static EquipmentSlotEx()
        {
            var values = (CharacterRace[])Enum.GetValues(typeof(CharacterRace));
            int maxIndex = 0;
            foreach (var r in values) maxIndex = Math.Max(maxIndex, (int)r);

            _raceFlagCache = new CharacterRaceFlags[maxIndex + 1];
            foreach (var r in values)
            {
                _raceFlagCache[(int)r] = r switch
                {
                    CharacterRace.Human  => CharacterRaceFlags.Human,
                    CharacterRace.Elf    => CharacterRaceFlags.Elf,
                    CharacterRace.Dwarf  => CharacterRaceFlags.Dwarf,
                    CharacterRace.Orc    => CharacterRaceFlags.Orc,
                    CharacterRace.Undead => CharacterRaceFlags.Undead,
                    _                    => CharacterRaceFlags.None
                };
            }
        }

        public static bool IsActive(EquipmentSlot slot) => _activeSet.Contains(slot);

        public static bool IsRing(EquipmentSlot slot)
            => slot == EquipmentSlot.Ring1 || slot == EquipmentSlot.Ring2;

        public static bool IsEarring(EquipmentSlot slot)
            => slot == EquipmentSlot.Earring1 || slot == EquipmentSlot.Earring2;

        public static string DisplayName(EquipmentSlot slot) => slot switch
        {
            EquipmentSlot.Weapon   => "Arma",
            EquipmentSlot.Shield   => "Escudo",
            EquipmentSlot.Helmet   => "Elmo",
            EquipmentSlot.Armor    => "Armadura",
            EquipmentSlot.Gloves   => "Luvas",
            EquipmentSlot.Boots    => "Botas",
            EquipmentSlot.Ring1    => "Anel I",
            EquipmentSlot.Ring2    => "Anel II",
            EquipmentSlot.Cape     => "Capa",
            EquipmentSlot.Necklace => "Colar",
            EquipmentSlot.Earring1 => "Brinco I",
            EquipmentSlot.Earring2 => "Brinco II",
            EquipmentSlot.None     => "Nenhum",
            _                      => slot.ToString()
        };

        /// <summary>
        /// Conversão race→flag SEM alocação ou Enum.Parse. Usa array cacheado.
        /// </summary>
        public static CharacterRaceFlags ToFlag(CharacterRace race)
        {
            int idx = (int)race;
            if (idx < 0 || idx >= _raceFlagCache.Length) return CharacterRaceFlags.None;
            return _raceFlagCache[idx];
        }

        public static string FlagsDisplayName(CharacterRaceFlags flags)
        {
            if (flags == CharacterRaceFlags.None) return "Nenhuma raça";
            if (flags == CharacterRaceFlags.All)  return "Todas as raças";

            var names = new List<string>(5);
            if ((flags & CharacterRaceFlags.Human)  != 0) names.Add("Humanos");
            if ((flags & CharacterRaceFlags.Elf)    != 0) names.Add("Elfos");
            if ((flags & CharacterRaceFlags.Dwarf)  != 0) names.Add("Anões");
            if ((flags & CharacterRaceFlags.Orc)    != 0) names.Add("Orcs");
            if ((flags & CharacterRaceFlags.Undead) != 0) names.Add("Mortos-Vivos");
            return $"Apenas {string.Join(", ", names)}";
        }

        public static bool CanItemFitInSlot(EquipmentSlot itemSlot, EquipmentSlot targetSlot)
        {
            if (itemSlot == targetSlot) return true;
            if (IsRing(itemSlot)    && IsRing(targetSlot))    return true;
            if (IsEarring(itemSlot) && IsEarring(targetSlot)) return true;
            return false;
        }

        /// <summary>
        /// Agrega todos os bônus de equipamento em um único EquipmentBonuses.
        /// Itens não encontrados no ItemDatabase são silenciosamente ignorados
        /// (ex: removidos em patch — não crasha).
        /// </summary>
        public static EquipmentBonuses AggregateBonuses(IEnumerable<EquippedItemData> equipped)
        {
            var b = new EquipmentBonuses();
            if (equipped == null) return b;

            var db = ItemDatabase.Instance;
            if (db == null) return b;

            foreach (var slot in equipped)
            {
                if (slot.IsEmpty) continue;

                var item = db.GetItem(slot.ItemId);
                if (item == null || !item.IsEquipment) continue;

                b.STR += item.BonusSTR;
                b.AGI += item.BonusAGI;
                b.VIT += item.BonusVIT;
                b.DEX += item.BonusDEX;
                b.INT += item.BonusINT;
                b.LUK += item.BonusLUK;

                b.ATK     += item.BonusATK;
                b.DEF     += item.BonusDEF;
                b.MATK    += item.BonusMATK;
                b.MDEF    += item.BonusMDEF;
                b.HPBonus += item.BonusHP;
                b.MPBonus += item.BonusMP;

                b.ResistFire      += item.BonusResistFire;
                b.ResistIce       += item.BonusResistIce;
                b.ResistPoison    += item.BonusResistPoison;
                b.ResistLightning += item.BonusResistLightning;
            }
            return b;
        }
    }
}
