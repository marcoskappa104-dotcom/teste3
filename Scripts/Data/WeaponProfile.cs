using System;
using UnityEngine;

namespace RPG.Data
{
    /// <summary>
    /// Categoria da arma. Define como o ataque básico se comporta:
    ///   - Tipo de dano (físico/mágico)
    ///   - Range
    ///   - Se usa projétil ou hit instantâneo
    ///   - Multiplicador de ATK/MATK
    ///   - Animação a tocar
    ///
    /// IMPORTANTE: Não confundir com SkillType. WeaponType é uma propriedade
    /// da ARMA (item). SkillType é uma propriedade da SKILL (joia).
    /// </summary>
    [Serializable]
    public enum WeaponType : byte
    {
        Unarmed = 0, // Soco — melee curto, físico, fraco
        Sword   = 1, // Espada/machado/lança — melee, físico
        Bow     = 2, // Arco/besta — projétil físico longo
        Staff   = 3, // Cajado — projétil mágico médio (magic missile básico)
        Dagger  = 4, // Adaga — melee curtíssimo, físico, ataque rápido
        Wand    = 5, // Varinha — projétil mágico curto
    }

    /// <summary>
    /// Perfil de ataque básico associado a um WeaponType.
    /// O servidor consulta este perfil para validar e calcular o ataque.
    /// O cliente o usa para feedback visual (range circle, animação).
    ///
    /// Imutável após Awake — não mutar campos em runtime.
    /// </summary>
    [Serializable]
    public class WeaponAttackProfile
    {
        [Tooltip("Categoria da arma à qual este perfil se aplica.")]
        public WeaponType Type = WeaponType.Unarmed;

        [Header("Alcance")]
        [Tooltip("Distância máxima para iniciar o ataque (metros).")]
        [Range(0.5f, 30f)]
        public float Range = 2.5f;

        [Header("Dano")]
        [Tooltip("True = dano físico (ATK/DEF). False = dano mágico (MATK/MDEF).")]
        public bool IsPhysical = true;

        [Tooltip("Multiplicador aplicado a ATK (físico) ou MATK (mágico). 1.0 = padrão.")]
        [Range(0.1f, 5f)]
        public float DamageMultiplier = 1.0f;

        [Header("Velocidade")]
        [Tooltip("Multiplicador no intervalo de ataque base (1/ASPD).\n" +
                 "<1 = ataca mais rápido (adagas). >1 = ataca mais devagar (machados).")]
        [Range(0.3f, 3f)]
        public float AttackIntervalMultiplier = 1.0f;

        [Header("Projétil (apenas Bow/Staff/Wand)")]
        [Tooltip("Se true, dispara um projétil visual. Servidor aplica dano quando o projétil chega.")]
        public bool UsesProjectile = false;

        [Tooltip("Velocidade do projétil em m/s. Ignorado se UsesProjectile=false.")]
        [Range(5f, 80f)]
        public float ProjectileSpeed = 25f;

        [Header("Animação")]
        [Tooltip("Trigger do Animator. Padrão: Attack.")]
        public string AnimTrigger = "Attack";

        [Header("Custo de Mana (apenas armas mágicas)")]
        [Tooltip("Mana consumida por ataque básico. 0 = grátis.")]
        [Range(0f, 50f)]
        public float ManaCost = 0f;

        // ──────────────────────────────────────────────────────────────────
        // Perfis padrão (fallback quando o item não define um próprio)
        // ──────────────────────────────────────────────────────────────────

        public static WeaponAttackProfile Default(WeaponType type) => type switch
        {
            WeaponType.Sword => new WeaponAttackProfile
            {
                Type = WeaponType.Sword,
                Range = 2.5f,
                IsPhysical = true,
                DamageMultiplier = 1.0f,
                AttackIntervalMultiplier = 1.0f,
                UsesProjectile = false,
                AnimTrigger = "AttackMelee",
                ManaCost = 0f
            },

            WeaponType.Dagger => new WeaponAttackProfile
            {
                Type = WeaponType.Dagger,
                Range = 2.0f,
                IsPhysical = true,
                DamageMultiplier = 0.75f, // dano menor compensado pela velocidade
                AttackIntervalMultiplier = 0.65f, // ~1.5x mais rápido
                UsesProjectile = false,
                AnimTrigger = "AttackMelee",
                ManaCost = 0f
            },

            WeaponType.Bow => new WeaponAttackProfile
            {
                Type = WeaponType.Bow,
                Range = 14f,
                IsPhysical = true,
                DamageMultiplier = 1.0f,
                AttackIntervalMultiplier = 1.15f, // ligeiramente mais lento
                UsesProjectile = true,
                ProjectileSpeed = 30f,
                AnimTrigger = "AttackRanged",
                ManaCost = 0f
            },

            WeaponType.Staff => new WeaponAttackProfile
            {
                Type = WeaponType.Staff,
                Range = 10f,
                IsPhysical = false, // mágico!
                DamageMultiplier = 1.0f,
                AttackIntervalMultiplier = 1.25f, // mais lento
                UsesProjectile = true,
                ProjectileSpeed = 18f,
                AnimTrigger = "AttackCast",
                ManaCost = 2f
            },

            WeaponType.Wand => new WeaponAttackProfile
            {
                Type = WeaponType.Wand,
                Range = 7f,
                IsPhysical = false,
                DamageMultiplier = 0.85f,
                AttackIntervalMultiplier = 0.9f,
                UsesProjectile = true,
                ProjectileSpeed = 22f,
                AnimTrigger = "AttackCast",
                ManaCost = 1f
            },

            // Unarmed e qualquer fallback
            _ => new WeaponAttackProfile
            {
                Type = WeaponType.Unarmed,
                Range = 1.8f,
                IsPhysical = true,
                DamageMultiplier = 0.6f, // socos doem menos que espadas
                AttackIntervalMultiplier = 0.95f,
                UsesProjectile = false,
                AnimTrigger = "AttackMelee",
                ManaCost = 0f
            },
        };

        public WeaponAttackProfile Clone() => (WeaponAttackProfile)MemberwiseClone();
    }
}
