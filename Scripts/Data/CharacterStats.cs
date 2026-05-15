using System;
using UnityEngine;

namespace RPG.Data
{
    [Serializable]
    public enum CharacterRace
    {
        Human,
        Elf,
        Dwarf,
        Orc,
        Undead
    }

    [Serializable]
    public class BaseAttributes
    {
        public int STR = 10;
        public int AGI = 10;
        public int VIT = 10;
        public int DEX = 10;
        public int INT = 10;
        public int LUK = 10;
    }

    /// <summary>
    /// Bônus de raça. Imutável após criação — instâncias compartilhadas
    /// (não modifique campos depois de criar).
    /// </summary>
    [Serializable]
    public class RaceBonus
    {
        public int STR, AGI, VIT, DEX, INT, LUK;
    }

    /// <summary>
    /// Stats finais derivados dos atributos base + raça + alocações + equipamento + buffs.
    /// Imutável após criação — para modificar use Clone() e altere a cópia.
    /// </summary>
    [Serializable]
    public class DerivedStats
    {
        public float MaxHP;
        public float MaxMP;
        public float ATK;
        public float MATK;
        public float DEF;
        public float MDEF;
        public float ASPD;        // ataques por segundo
        public float MoveSpeed;   // m/s no NavMeshAgent
        public float CastSpeed;   // reduz CastTime: effective = base / (1 + CastSpeed/100)
        public float HIT;
        public float FLEE;
        public float CRIT;
        public float CritDMG;
        public float HPRegen;     // por tick (a cada 5s)
        public float MPRegen;     // por tick (a cada 5s)
        public float Penetration;
        public float MagicPenetration;
        public float DamageReduction;
        public float ResistFire;
        public float ResistIce;
        public float ResistPoison;
        public float ResistLightning;

        public DerivedStats Clone() => (DerivedStats)MemberwiseClone();
    }

    [Serializable]
    public class EquipmentBonuses
    {
        public int   STR, AGI, VIT, DEX, INT, LUK;
        public float ATK, DEF, MATK, MDEF;
        public float HPBonus, MPBonus;
        public float ResistFire, ResistIce, ResistPoison, ResistLightning;
    }

    [Serializable]
    public class BuffBonuses
    {
        public int   STR, AGI, VIT, DEX, INT, LUK;
        public float ATKMultiplier = 1f;
        public float DEFMultiplier = 1f;
    }

    /// <summary>
    /// Calculadora de stats. Todos os métodos são puros (sem side-effects) e thread-safe.
    /// Aceita System.Random opcional para uso no servidor fora do main thread.
    ///
    /// === CORREÇÕES DESTA VERSÃO ===
    ///
    ///   1. GetRaceBonus SEM ALOCAÇÃO:
    ///      Antes, cada chamada criava `new RaceBonus { ... }` — chamado em
    ///      validação de equip (a cada hover!) e em todo recálculo de stats.
    ///      Agora retorna instâncias estáticas pré-alocadas, compartilhadas
    ///      por todos os callers.
    ///
    ///   IMPORTANTE: nunca modifique os campos do retorno; é uma instância
    ///   compartilhada. Se precisar modificar, faça uma cópia primeiro.
    /// </summary>
    public static class StatsCalculator
    {
        // ── Constantes base ────────────────────────────────────────────────
        public const int   BASE_HP         = 50;
        public const int   BASE_MP         = 30;
        public const float BASE_ASPD       = 0.8f;
        public const float BASE_MOVESPEED  = 4.0f;
        public const float MAX_ASPD        = 4.0f;
        public const float MIN_ASPD        = 0.3f;
        public const float MAX_MOVESPEED   = 7.5f;
        public const float MIN_MOVESPEED   = 3.0f;
        public const float MIN_CRIT_DMG    = 1.5f;
        public const float MAX_CRIT_DMG    = 3.0f;
        public const float MAX_PENETRATION = 200f;
        public const float MAX_RESIST      = 75f;

        // ── Coeficientes de atributo ───────────────────────────────────────
        private const float HP_PER_VIT      = 15f;
        private const float HP_PER_STR      = 3f;
        private const float HP_PER_LEVEL    = 8f;
        private const float MP_PER_INT      = 12f;
        private const float MP_PER_DEX      = 2f;
        private const float MP_PER_LEVEL    = 4f;
        private const float ATK_PER_STR     = 1.2f;
        private const float ATK_PER_DEX     = 0.4f;
        private const float MATK_PER_INT    = 1.5f;
        private const float MATK_PER_DEX    = 0.4f;
        private const float DEF_PER_VIT     = 1.0f;
        private const float DEF_PER_STR     = 0.2f;
        private const float MDEF_PER_INT    = 1.0f;
        private const float MDEF_PER_VIT    = 0.3f;
        private const float ASPD_PER_AGI    = 0.015f;
        private const float ASPD_PER_DEX    = 0.008f;
        private const float MOVE_PER_AGI    = 0.025f;
        private const float HIT_PER_DEX     = 2.0f;
        private const float HIT_PER_LUK     = 0.3f;
        private const float FLEE_PER_AGI    = 2.0f;
        private const float FLEE_PER_LUK    = 0.2f;
        private const float CRIT_PER_LUK    = 0.25f;
        private const float CRITDMG_PER_LUK = 0.01f;
        private const float HPREGEN_PER_VIT = 0.8f;
        private const float HPREGEN_PER_LVL = 0.3f;
        private const float MPREGEN_PER_INT = 0.6f;
        private const float MPREGEN_PER_LVL = 0.2f;
        private const float CAST_PER_DEX    = 0.4f;
        private const float CAST_PER_INT    = 0.3f;
        private const float PEN_PER_STR     = 0.15f;
        private const float MPEN_PER_INT    = 0.12f;
        private const float DMGRED_PER_VIT  = 0.08f;

        // Multiplicador de stats por nível para monstros (Lv1 = 1.0x, Lv99 ≈ 3.45x)
        private const float MONSTER_STAT_PER_LEVEL = 0.025f;

        // ══════════════════════════════════════════════════════════════════
        // Bônus de raça — instâncias estáticas compartilhadas
        // (ler-apenas; não modificar os campos)
        // ══════════════════════════════════════════════════════════════════

        private static readonly RaceBonus HumanBonus  = new RaceBonus { STR=2, AGI=2, VIT=2, DEX=2, INT=2, LUK=5 };
        private static readonly RaceBonus ElfBonus    = new RaceBonus { STR=0, AGI=5, VIT=0, DEX=5, INT=5, LUK=3 };
        private static readonly RaceBonus DwarfBonus  = new RaceBonus { STR=5, AGI=0, VIT=8, DEX=2, INT=0, LUK=2 };
        private static readonly RaceBonus OrcBonus    = new RaceBonus { STR=8, AGI=2, VIT=5, DEX=0, INT=0, LUK=0 };
        private static readonly RaceBonus UndeadBonus = new RaceBonus { STR=2, AGI=2, VIT=0, DEX=2, INT=8, LUK=0 };
        private static readonly RaceBonus EmptyBonus  = new RaceBonus();

        /// <summary>
        /// Retorna o bônus de raça. A instância retornada é COMPARTILHADA
        /// (zero alocação). NUNCA modifique seus campos.
        /// </summary>
        public static RaceBonus GetRaceBonus(CharacterRace race)
        {
            return race switch
            {
                CharacterRace.Human  => HumanBonus,
                CharacterRace.Elf    => ElfBonus,
                CharacterRace.Dwarf  => DwarfBonus,
                CharacterRace.Orc    => OrcBonus,
                CharacterRace.Undead => UndeadBonus,
                _                    => EmptyBonus
            };
        }

        /// <summary>
        /// Calcula stats derivados para JOGADORES.
        /// Não modifica nenhum dos parâmetros recebidos. Thread-safe.
        /// </summary>
        public static DerivedStats Calculate(
            BaseAttributes   baseAttr,
            int              level,
            CharacterRace    race,
            int              allocSTR  = 0,
            int              allocAGI  = 0,
            int              allocVIT  = 0,
            int              allocDEX  = 0,
            int              allocINT  = 0,
            int              allocLUK  = 0,
            EquipmentBonuses equip     = null,
            BuffBonuses      buff      = null)
        {
            if (baseAttr == null) baseAttr = new BaseAttributes();
            equip ??= new EquipmentBonuses();
            buff  ??= new BuffBonuses();

            var raceBonus = GetRaceBonus(race);

            allocSTR = Math.Max(0, allocSTR);
            allocAGI = Math.Max(0, allocAGI);
            allocVIT = Math.Max(0, allocVIT);
            allocDEX = Math.Max(0, allocDEX);
            allocINT = Math.Max(0, allocINT);
            allocLUK = Math.Max(0, allocLUK);

            float STR = Math.Max(1f, baseAttr.STR + raceBonus.STR + allocSTR + equip.STR + buff.STR);
            float AGI = Math.Max(1f, baseAttr.AGI + raceBonus.AGI + allocAGI + equip.AGI + buff.AGI);
            float VIT = Math.Max(1f, baseAttr.VIT + raceBonus.VIT + allocVIT + equip.VIT + buff.VIT);
            float DEX = Math.Max(1f, baseAttr.DEX + raceBonus.DEX + allocDEX + equip.DEX + buff.DEX);
            float INT = Math.Max(1f, baseAttr.INT + raceBonus.INT + allocINT + equip.INT + buff.INT);
            float LUK = Math.Max(1f, baseAttr.LUK + raceBonus.LUK + allocLUK + equip.LUK + buff.LUK);

            return CalculateInternal(STR, AGI, VIT, DEX, INT, LUK, level, equip, buff);
        }

        /// <summary>
        /// Calcula stats para monstros: sem bônus de raça, escala com nível.
        /// </summary>
        public static DerivedStats CalculateForMonster(BaseAttributes baseAttr, int level)
        {
            if (baseAttr == null) baseAttr = new BaseAttributes();
            int clampedLevel = Math.Max(1, level);

            float levelMult = 1f + (clampedLevel - 1) * MONSTER_STAT_PER_LEVEL;

            float STR = Math.Max(1f, baseAttr.STR * levelMult);
            float AGI = Math.Max(1f, baseAttr.AGI * levelMult);
            float VIT = Math.Max(1f, baseAttr.VIT * levelMult);
            float DEX = Math.Max(1f, baseAttr.DEX * levelMult);
            float INT = Math.Max(1f, baseAttr.INT * levelMult);
            float LUK = Math.Max(1f, baseAttr.LUK * levelMult);

            return CalculateInternal(STR, AGI, VIT, DEX, INT, LUK, clampedLevel,
                                     new EquipmentBonuses(), new BuffBonuses());
        }

        private static DerivedStats CalculateInternal(
            float STR, float AGI, float VIT,
            float DEX, float INT, float LUK,
            int level,
            EquipmentBonuses equip,
            BuffBonuses buff)
        {
            var s = new DerivedStats();

            s.MaxHP = BASE_HP + (VIT * HP_PER_VIT) + (STR * HP_PER_STR)
                    + (level * HP_PER_LEVEL) + equip.HPBonus;
            s.MaxMP = BASE_MP + (INT * MP_PER_INT) + (DEX * MP_PER_DEX)
                    + (level * MP_PER_LEVEL) + equip.MPBonus;
            s.MaxHP = Math.Max(1f, s.MaxHP);
            s.MaxMP = Math.Max(1f, s.MaxMP);

            s.ATK  = ((STR * ATK_PER_STR)  + (DEX * ATK_PER_DEX)  + level + equip.ATK)  * buff.ATKMultiplier;
            s.MATK = ((INT * MATK_PER_INT) + (DEX * MATK_PER_DEX) + level + equip.MATK) * buff.ATKMultiplier;
            s.DEF  = ((VIT * DEF_PER_VIT)  + (STR * DEF_PER_STR)  + equip.DEF)  * buff.DEFMultiplier;
            s.MDEF = ((INT * MDEF_PER_INT) + (VIT * MDEF_PER_VIT) + equip.MDEF) * buff.DEFMultiplier;

            s.ASPD      = Mathf.Clamp(BASE_ASPD + (AGI * ASPD_PER_AGI) + (DEX * ASPD_PER_DEX),
                                      MIN_ASPD, MAX_ASPD);
            s.MoveSpeed = Mathf.Clamp(BASE_MOVESPEED + (AGI * MOVE_PER_AGI),
                                      MIN_MOVESPEED, MAX_MOVESPEED);

            s.HIT  = (DEX * HIT_PER_DEX)  + (LUK * HIT_PER_LUK);
            s.FLEE = (AGI * FLEE_PER_AGI) + (LUK * FLEE_PER_LUK);

            s.CRIT = LUK * CRIT_PER_LUK;

            float lukAbove50 = Math.Max(0f, LUK - 50f);
            s.CritDMG = Mathf.Clamp(MIN_CRIT_DMG + (lukAbove50 * CRITDMG_PER_LUK),
                                    MIN_CRIT_DMG, MAX_CRIT_DMG);

            s.HPRegen = (VIT * HPREGEN_PER_VIT) + (level * HPREGEN_PER_LVL);
            s.MPRegen = (INT * MPREGEN_PER_INT) + (level * MPREGEN_PER_LVL);

            s.CastSpeed = (DEX * CAST_PER_DEX) + (INT * CAST_PER_INT);

            s.Penetration      = Mathf.Min(STR * PEN_PER_STR,  MAX_PENETRATION);
            s.MagicPenetration = Mathf.Min(INT * MPEN_PER_INT, MAX_PENETRATION);
            s.DamageReduction  = VIT * DMGRED_PER_VIT;

            s.ResistFire      = Mathf.Clamp(equip.ResistFire,      0f, MAX_RESIST);
            s.ResistIce       = Mathf.Clamp(equip.ResistIce,       0f, MAX_RESIST);
            s.ResistPoison    = Mathf.Clamp(equip.ResistPoison,    0f, MAX_RESIST);
            s.ResistLightning = Mathf.Clamp(equip.ResistLightning, 0f, MAX_RESIST);

            return s;
        }

        // ══════════════════════════════════════════════════════════════════
        // Fórmulas de dano
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Pipeline:
        ///   1. Subtrai DamageReduction (flat) do ATK
        ///   2. DEF efetiva = DEF - Penetration (não pode ficar negativa)
        ///   3. Redução percentual: DEF_efetiva / (DEF_efetiva + 100)
        ///   4. Garante dano mínimo de 1 antes do crítico
        ///   5. Aplica crítico se houver
        /// </summary>
        public static float CalculatePhysicalDamage(
            float atk, float def,
            bool  isCrit,
            float critDmgMult  = 1.5f,
            float penetration  = 0f,
            float dmgReduction = 0f)
        {
            float rawDmg       = Math.Max(0f, atk - dmgReduction);
            float effectiveDef = Math.Max(0f, def - penetration);
            float reduction    = effectiveDef / (effectiveDef + 100f);
            float finalDmg     = rawDmg * (1f - reduction);

            finalDmg = Math.Max(1f, finalDmg);
            if (isCrit) finalDmg *= critDmgMult;

            return Mathf.Floor(finalDmg);
        }

        /// <summary>
        /// Dano mágico. DamageReduction é menos eficaz contra magia (0.5x).
        /// </summary>
        public static float CalculateMagicDamage(
            float matk, float mdef,
            bool  isCrit,
            float critDmgMult      = 1.5f,
            float magicPenetration = 0f,
            float dmgReduction     = 0f)
        {
            float rawDmg        = Math.Max(0f, matk - dmgReduction * 0.5f);
            float effectiveMdef = Math.Max(0f, mdef - magicPenetration);
            float reduction     = effectiveMdef / (effectiveMdef + 100f);
            float finalDmg      = rawDmg * (1f - reduction);

            finalDmg = Math.Max(1f, finalDmg);
            if (isCrit) finalDmg *= critDmgMult;

            return Mathf.Floor(finalDmg);
        }

        /// <summary>
        /// CastTime efetivo considerando CastSpeed do caster.
        /// effective = base / (1 + CastSpeed / 100)
        /// </summary>
        public static float CalculateEffectiveCastTime(float baseCastTime, float castSpeed)
        {
            if (baseCastTime <= 0f) return 0f;
            float divisor = 1f + Mathf.Max(0f, castSpeed) / 100f;
            return baseCastTime / divisor;
        }

        /// <summary>
        /// Rola crítico. Passe rng para uso no servidor fora do main thread.
        /// </summary>
        public static bool RollCrit(float critChance, System.Random rng = null)
        {
            float roll = rng != null
                ? (float)(rng.NextDouble() * 100.0)
                : UnityEngine.Random.Range(0f, 100f);
            return roll < critChance;
        }

        /// <summary>
        /// Rola acerto. Quando hit e flee são 0, usa 50% base
        /// (evita NaN da divisão 0/0).
        /// </summary>
        public static bool RollHit(float hit, float flee, System.Random rng = null)
        {
            float hitChance;

            if (hit <= 0f && flee <= 0f)
            {
                hitChance = 50f;
            }
            else
            {
                float total = hit + flee;
                hitChance = Mathf.Clamp((hit / total) * 100f, 5f, 95f);
            }

            float roll = rng != null
                ? (float)(rng.NextDouble() * 100.0)
                : UnityEngine.Random.Range(0f, 100f);
            return roll < hitChance;
        }

#if UNITY_EDITOR
        public static string DebugSummary(DerivedStats s, int level)
        {
            return $"[Lv{level}] HP:{s.MaxHP:0} MP:{s.MaxMP:0} | " +
                   $"ATK:{s.ATK:0} MATK:{s.MATK:0} DEF:{s.DEF:0} MDEF:{s.MDEF:0} | " +
                   $"ASPD:{s.ASPD:0.00}/s SPD:{s.MoveSpeed:0.0} | " +
                   $"HIT:{s.HIT:0} FLEE:{s.FLEE:0} CRIT:{s.CRIT:0.0}% CritDMG:{s.CritDMG:0.00}x | " +
                   $"Pen:{s.Penetration:0} MagPen:{s.MagicPenetration:0} DmgRed:{s.DamageReduction:0} | " +
                   $"Regen HP:{s.HPRegen:0.0}/5s MP:{s.MPRegen:0.0}/5s | CastSpd:{s.CastSpeed:0.0}";
        }
#endif
    }
}
