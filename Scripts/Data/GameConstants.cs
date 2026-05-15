namespace RPG
{
    /// <summary>
    /// Constantes globais do jogo. Use estas referências em vez de hard-coded
    /// para facilitar tuning posterior.
    ///
    /// === COMO USAR ===
    ///   - Para constantes de servidor: GameConstants.Server.X
    ///   - Para constantes de cliente:  GameConstants.Client.X
    ///   - Para constantes de combate:  GameConstants.Combat.X
    ///
    /// === SOBRE ALTERAÇÕES ===
    ///   Mudar uma constante aqui afeta TODOS os usos. Antes de alterar,
    ///   leia os comentários sobre o impacto.
    /// </summary>
    public static class GameConstants
    {
        // ══════════════════════════════════════════════════════════════════
        // Limites globais
        // ══════════════════════════════════════════════════════════════════
        public const int  MAX_CHARACTERS_PER_ACCOUNT = 5;
        public const int  MAX_LEVEL                  = 99;
        public const int  POINTS_PER_LEVEL_UP        = 5;
        public const int  MAX_ALLOCATED_PER_STAT     = 300;

        // ══════════════════════════════════════════════════════════════════
        // Inventário
        // ══════════════════════════════════════════════════════════════════
        public const int  MAX_INVENTORY_SLOTS = 60;
        public const int  POWER_GEM_SLOTS     = 4;

        // ══════════════════════════════════════════════════════════════════
        // Servidor
        // ══════════════════════════════════════════════════════════════════
        public static class Server
        {
            /// <summary>Salva personagem a cada N segundos.</summary>
            public const float AUTO_SAVE_INTERVAL_SECONDS = 60f;

            /// <summary>Regen HP/MP a cada N segundos (fora de combate).</summary>
            public const float REGEN_INTERVAL_SECONDS = 5f;

            /// <summary>Não regen por N segundos após receber dano.</summary>
            public const float REGEN_COMBAT_SUPPRESSION_SECONDS = 8f;

            /// <summary>Range máximo aceito do cliente em CmdMoveTo (anti-cheat).</summary>
            public const float MAX_MOVE_COMMAND_DISTANCE = 120f;

            /// <summary>Range máximo do auto-ataque básico (cap server-side).</summary>
            public const float MAX_PLAYER_ATTACK_RANGE = 6f;

            /// <summary>Tolerância no range para reduzir falsos positivos por latência.</summary>
            public const float ATTACK_RANGE_TOLERANCE = 1.15f;

            /// <summary>Cap defensivo no cooldown (segundos).</summary>
            public const float MAX_SKILL_COOLDOWN_SECONDS = 300f;

            /// <summary>Cap defensivo no XP por chamada (anti-overflow).</summary>
            public const long  MAX_XP_PER_GRANT = 1_000_000L;
        }

        // ══════════════════════════════════════════════════════════════════
        // Cliente / UX
        // ══════════════════════════════════════════════════════════════════
        public static class Client
        {
            public const float DOUBLE_CLICK_WINDOW_SECONDS = 0.35f;
            public const float CMD_MOVE_RATE_LIMIT_SECONDS = 0.15f;
            public const float WALK_TO_RANGE_TIMEOUT       = 15f;
        }

        // ══════════════════════════════════════════════════════════════════
        // Combate
        // ══════════════════════════════════════════════════════════════════
        public static class Combat
        {
            public const float MIN_DAMAGE                  = 1f;
            public const float DAMAGE_MIN_REDUCTION_FACTOR = 100f; // DEF/(DEF+100)

            // Stats caps
            public const float MAX_ASPD       = 4.0f;
            public const float MIN_ASPD       = 0.3f;
            public const float MAX_MOVESPEED  = 7.5f;
            public const float MIN_MOVESPEED  = 3.0f;
            public const float MAX_RESIST     = 75f;
            public const float MAX_HP         = 500_000f;
            public const float MAX_MP         = 200_000f;
        }

        // ══════════════════════════════════════════════════════════════════
        // Autenticação
        // ══════════════════════════════════════════════════════════════════
        public static class Auth
        {
            public const int   USERNAME_MIN_LENGTH      = 4;
            public const int   USERNAME_MAX_LENGTH      = 20;
            public const int   CHARACTER_NAME_MIN       = 2;
            public const int   CHARACTER_NAME_MAX       = 20;

            public const int   LOGIN_MAX_PER_CONN       = 5;
            public const int   LOGIN_MAX_PER_IP         = 15;
            public const float IP_BAN_DURATION_SECONDS  = 300f;
            public const float MIN_TIME_BETWEEN_LOGINS  = 0.5f;
            public const float SESSION_TTL_SECONDS      = 300f;
            public const float NONCE_WAIT_TIMEOUT       = 5f;
        }

        // ══════════════════════════════════════════════════════════════════
        // Bem-estar do jogador (vida útil de monstro, drops)
        // ══════════════════════════════════════════════════════════════════
        public static class World
        {
            public const float WORLD_ITEM_DESPAWN_SECONDS = 60f;
            public const float MONSTER_RESPAWN_SECONDS    = 15f;
            public const float MONSTER_BODY_FADE_DELAY    = 5f;
            public const float MONSTER_BODY_FADE_DURATION = 1f;
        }
    }
}
