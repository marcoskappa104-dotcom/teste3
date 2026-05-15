using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG.Data
{
    /// <summary>
    /// Dados persistentes de um personagem.
    /// Toda mudança que afeta stats derivados deve passar pelos setters/métodos
    /// públicos — campos públicos existem apenas por compatibilidade com serialização.
    ///
    /// === MELHORIAS DESTA VERSÃO ===
    ///
    ///   1. CACHE OPCIONAL DE DerivedStats:
    ///      GetDerivedStats() agora aceita um parâmetro 'useCache' que, quando true,
    ///      retorna a versão cacheada se nenhuma mudança ocorreu desde o último cálculo.
    ///      Use cache em hot paths (validação de equip a cada hover).
    ///      NÃO use em fluxos onde stats podem mudar entre chamadas (regen loop, etc).
    ///
    ///   2. CACHE INVALIDATION EXPLÍCITO:
    ///      InvalidateStatsCache() força recálculo na próxima chamada.
    ///      Chame após Level++, AllocatedX++, ou EquipmentBonuses change.
    /// </summary>
    [Serializable]
    public class CharacterData
    {
        public const int MAX_LEVEL              = 99;
        public const int MAX_ALLOCATED_PER_STAT = 300;
        public const int POINTS_PER_LEVEL_UP    = 5;

        public string        CharacterId;
        public string        CharacterName;
        public CharacterRace Race;

        public int RaceInt
        {
            get => (int)Race;
            set => Race = (CharacterRace)value;
        }

        private int _level = 1;
        public int Level
        {
            get => _level;
            set
            {
                int clamped = Math.Max(1, Math.Min(value, MAX_LEVEL));
                if (clamped != _level) _statsCacheDirty = true;
                _level = clamped;
            }
        }

        public long Experience            = 0;
        public long ExperienceToNextLevel = 100;

        public BaseAttributes    BaseAttributes   = new BaseAttributes();
        public EquipmentBonuses  EquipmentBonuses = new EquipmentBonuses();

        public float  PosX, PosY, PosZ;
        public string CurrentMap = "World_01";

        public float CurrentHP;
        public float CurrentMP;

        public int FreeAttributePoints = 0;

        public int AllocatedSTR, AllocatedAGI, AllocatedVIT;
        public int AllocatedDEX, AllocatedINT, AllocatedLUK;

        // ── Cache de DerivedStats (opt-in via useCache=true) ───────────────
        [NonSerialized] private DerivedStats _cachedStats;
        [NonSerialized] private bool         _statsCacheDirty = true;

        /// <summary>
        /// Marca o cache como sujo. Chame após qualquer mutação que afete stats.
        /// </summary>
        public void InvalidateStatsCache() => _statsCacheDirty = true;

        /// <summary>
        /// Retorna stats derivados. Se useCache=true e nada mudou desde o último
        /// cálculo, retorna a versão cacheada (zero alocação).
        ///
        /// IMPORTANTE: O cache não considera 'buff' como parte do estado;
        /// se você passar um buff, o cache é IGNORADO.
        /// </summary>
        public DerivedStats GetDerivedStats(BuffBonuses buff = null, bool useCache = false)
        {
            // Cache só vale quando não há buff envolvido
            if (useCache && buff == null && !_statsCacheDirty && _cachedStats != null)
                return _cachedStats;

            var stats = StatsCalculator.Calculate(
                BaseAttributes,
                Level,
                Race,
                AllocatedSTR, AllocatedAGI, AllocatedVIT,
                AllocatedDEX, AllocatedINT, AllocatedLUK,
                EquipmentBonuses,
                buff);

            if (buff == null)
            {
                _cachedStats     = stats;
                _statsCacheDirty = false;
            }

            return stats;
        }

        public static long GetExperienceForLevel(int level)
        {
            int clamped = Math.Max(1, Math.Min(level, MAX_LEVEL));
            return (long)(100.0 * Math.Pow(clamped, 1.5));
        }

        /// <summary>
        /// Adiciona experiência e processa level-ups em cascata.
        /// Retorna true se houve ao menos um level up.
        /// </summary>
        public bool AddExperience(long amount)
        {
            if (amount <= 0) return false;
            if (Level >= MAX_LEVEL) return false;

            Experience += amount;
            bool leveled = false;

            while (Experience >= ExperienceToNextLevel && Level < MAX_LEVEL)
            {
                Experience          -= ExperienceToNextLevel;
                Level++;
                FreeAttributePoints += POINTS_PER_LEVEL_UP;

                ExperienceToNextLevel = Level >= MAX_LEVEL
                    ? 0L
                    : GetExperienceForLevel(Level);

                leveled = true;
            }

            if (Level >= MAX_LEVEL)
            {
                ExperienceToNextLevel = 0;
                if (Experience < 0) Experience = 0;
            }

            if (leveled) _statsCacheDirty = true;
            return leveled;
        }

        public CharacterData Clone()
        {
            return new CharacterData
            {
                CharacterId           = CharacterId,
                CharacterName         = CharacterName,
                Race                  = Race,
                _level                = _level, // bypass setter to preserve cache state
                Experience            = Experience,
                ExperienceToNextLevel = ExperienceToNextLevel,
                PosX = PosX, PosY = PosY, PosZ = PosZ,
                CurrentMap            = CurrentMap,
                CurrentHP             = CurrentHP,
                CurrentMP             = CurrentMP,
                FreeAttributePoints   = FreeAttributePoints,
                AllocatedSTR = AllocatedSTR, AllocatedAGI = AllocatedAGI,
                AllocatedVIT = AllocatedVIT, AllocatedDEX = AllocatedDEX,
                AllocatedINT = AllocatedINT, AllocatedLUK = AllocatedLUK,
                _statsCacheDirty      = true, // Clone começa com cache sujo
                BaseAttributes = new BaseAttributes
                {
                    STR = BaseAttributes.STR, AGI = BaseAttributes.AGI,
                    VIT = BaseAttributes.VIT, DEX = BaseAttributes.DEX,
                    INT = BaseAttributes.INT, LUK = BaseAttributes.LUK
                },
                EquipmentBonuses = new EquipmentBonuses
                {
                    STR             = EquipmentBonuses.STR,
                    AGI             = EquipmentBonuses.AGI,
                    VIT             = EquipmentBonuses.VIT,
                    DEX             = EquipmentBonuses.DEX,
                    INT             = EquipmentBonuses.INT,
                    LUK             = EquipmentBonuses.LUK,
                    ATK             = EquipmentBonuses.ATK,
                    DEF             = EquipmentBonuses.DEF,
                    MATK            = EquipmentBonuses.MATK,
                    MDEF            = EquipmentBonuses.MDEF,
                    HPBonus         = EquipmentBonuses.HPBonus,
                    MPBonus         = EquipmentBonuses.MPBonus,
                    ResistFire      = EquipmentBonuses.ResistFire,
                    ResistIce       = EquipmentBonuses.ResistIce,
                    ResistPoison    = EquipmentBonuses.ResistPoison,
                    ResistLightning = EquipmentBonuses.ResistLightning
                }
            };
        }
    }

    /// <summary>
    /// Container leve usado apenas em mensagens de rede.
    /// </summary>
    [Serializable]
    public class AccountData
    {
        public string              Username;
        public string              PasswordHash;
        public List<CharacterData> Characters = new List<CharacterData>();
        public string              LastLogin;
    }
}
