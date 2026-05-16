using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Mirror;
using RPG.Data;
using RPG.Combat;
using System.Text;

namespace RPG.UI
{
    /// <summary>
    /// ItemTooltipUI — tooltip com seções dinâmicas.
    ///
    /// === MUDANÇAS DESTA VERSÃO (sistema de armas) ===
    ///   - Nova seção "Arma" exibida para itens com IsWeapon = true.
    ///     Mostra: tipo (Espada/Arco/Cajado), range, físico/mágico,
    ///     multiplicador de dano, custo de mana (se houver).
    ///   - O StringBuilder compartilhado continua sendo reutilizado.
    /// </summary>
    public class ItemTooltipUI : MonoBehaviour
    {
        public static ItemTooltipUI Instance { get; private set; }

        [Header("Refs principais")]
        [SerializeField] private CanvasGroup _canvasGroup;
        [SerializeField] private RectTransform _root;

        [Header("Cabeçalho")]
        [SerializeField] private TMP_Text _itemNameText;
        [SerializeField] private TMP_Text _itemTypeText;
        [SerializeField] private TMP_Text _descriptionText;

        [Header("Seção de Arma (Equipment + IsWeapon)")]
        [Tooltip("Opcional. Se nulo, info de arma vai pro statsSection.")]
        [SerializeField] private GameObject _weaponSection;
        [SerializeField] private TMP_Text   _weaponText;

        [Header("Seção de Stats (Equipment)")]
        [SerializeField] private GameObject _statsSection;
        [SerializeField] private TMP_Text   _statsText;

        [Header("Seção de Requisitos (Equipment)")]
        [SerializeField] private GameObject _requirementsSection;
        [SerializeField] private TMP_Text   _requirementsText;

        [Header("Seção de Skill (PowerGem)")]
        [SerializeField] private GameObject _skillSection;
        [SerializeField] private TMP_Text   _skillText;

        [Header("Seção de Consumível")]
        [SerializeField] private GameObject _consumableSection;
        [SerializeField] private TMP_Text   _consumableText;

        [Header("Posicionamento")]
        [SerializeField] private Vector2 _offset = new Vector2(20f, 0f);

        private readonly StringBuilder _sharedSB = new StringBuilder(256);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Hide();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ══════════════════════════════════════════════════════════════════
        // API pública
        // ══════════════════════════════════════════════════════════════════

        public void ShowForItem(ItemData item, RectTransform anchor)
        {
            if (item == null) { Hide(); return; }

            PopulateContent(item);
            PositionByAnchor(anchor);
            ApplyVisibility(true);
        }

        public void Show(ItemData item)
        {
            if (item == null) { Hide(); return; }

            PopulateContent(item);
            PositionByCursor();
            ApplyVisibility(true);
        }

        public void Hide()
        {
            if (_canvasGroup != null) _canvasGroup.alpha = 0f;
            gameObject.SetActive(false);
        }

        // ══════════════════════════════════════════════════════════════════
        // Conteúdo
        // ══════════════════════════════════════════════════════════════════

        private void PopulateContent(ItemData item)
        {
            if (_itemNameText != null)
            {
                _itemNameText.text  = item.DisplayName;
                _itemNameText.color = item.RarityColor;
            }
            if (_itemTypeText != null)
                _itemTypeText.text = $"{item.RarityDisplayName} — {GetTypeDisplay(item)}";

            if (_descriptionText != null)
            {
                _descriptionText.text = item.Description;
                _descriptionText.gameObject.SetActive(!string.IsNullOrEmpty(item.Description));
            }

            ShowWeaponSection(item);
            ShowStatsSection(item);
            ShowRequirementsSection(item);
            ShowSkillSection(item);
            ShowConsumableSection(item);
        }

        private void ApplyVisibility(bool visible)
        {
            if (_canvasGroup != null)
            {
                _canvasGroup.alpha          = visible ? 1f : 0f;
                _canvasGroup.blocksRaycasts = false;
                _canvasGroup.interactable   = false;
            }
            gameObject.SetActive(visible);
        }

        // ── Arma (nova seção) ──────────────────────────────────────────────

        private void ShowWeaponSection(ItemData item)
        {
            // Se o painel dedicado não existir, integra no statsSection
            GameObject target = _weaponSection;
            TMP_Text   label  = _weaponText;
            if (target == null)
            {
                // Sem painel dedicado — não exibe seção própria.
                // (Pode-se mesclar no _statsText, mas para evitar string mixing
                // deixamos só o painel dedicado.)
                if (_weaponSection != null) _weaponSection.SetActive(false);
                return;
            }

            if (!item.IsWeapon)
            {
                target.SetActive(false);
                return;
            }

            var profile = item.GetEffectiveAttackProfile();

            _sharedSB.Clear();
            _sharedSB.AppendLine($"<b>{item.WeaponTypeDisplayName}</b>");

            string damageColor = profile.IsPhysical ? "#FFB870" : "#80B0FF";
            string damageType  = profile.IsPhysical ? "Físico"  : "Mágico";
            _sharedSB.AppendLine($"<color={damageColor}>Tipo: {damageType}</color>");

            _sharedSB.AppendLine($"Alcance: {profile.Range:0.#}m");

            // Multiplicador de dano só vale a pena mostrar se != 1
            if (!Mathf.Approximately(profile.DamageMultiplier, 1f))
            {
                string mod = profile.DamageMultiplier > 1f ? "#88FF88" : "#FFB870";
                _sharedSB.AppendLine($"<color={mod}>Dano: {profile.DamageMultiplier:0.##}×</color>");
            }

            // Velocidade só se notavelmente diferente
            if (profile.AttackIntervalMultiplier < 0.95f)
                _sharedSB.AppendLine("<color=#88FF88>Velocidade: rápido</color>");
            else if (profile.AttackIntervalMultiplier > 1.1f)
                _sharedSB.AppendLine("<color=#FFB870>Velocidade: lento</color>");

            if (profile.UsesProjectile)
                _sharedSB.AppendLine("<color=#AACCFF>Ataque à distância</color>");

            if (profile.ManaCost > 0f)
                _sharedSB.AppendLine($"<color=#80B0FF>Custo: {profile.ManaCost:0.#} MP/ataque</color>");

            if (label != null) label.text = _sharedSB.ToString().TrimEnd();
            target.SetActive(true);
        }

        // ── Stats ──────────────────────────────────────────────────────────

        private void ShowStatsSection(ItemData item)
        {
            if (_statsSection == null) return;

            if (!item.IsEquipment || !item.HasAnyBonus())
            {
                _statsSection.SetActive(false);
                return;
            }

            _sharedSB.Clear();
            AppendStatLine(_sharedSB, "ATK",  item.BonusATK);
            AppendStatLine(_sharedSB, "DEF",  item.BonusDEF);
            AppendStatLine(_sharedSB, "MATK", item.BonusMATK);
            AppendStatLine(_sharedSB, "MDEF", item.BonusMDEF);
            AppendStatLine(_sharedSB, "STR",  item.BonusSTR);
            AppendStatLine(_sharedSB, "AGI",  item.BonusAGI);
            AppendStatLine(_sharedSB, "VIT",  item.BonusVIT);
            AppendStatLine(_sharedSB, "DEX",  item.BonusDEX);
            AppendStatLine(_sharedSB, "INT",  item.BonusINT);
            AppendStatLine(_sharedSB, "LUK",  item.BonusLUK);
            AppendStatLine(_sharedSB, "HP Máx.", item.BonusHP);
            AppendStatLine(_sharedSB, "MP Máx.", item.BonusMP);
            AppendResistLine(_sharedSB, "Resist. Fogo",   item.BonusResistFire);
            AppendResistLine(_sharedSB, "Resist. Gelo",   item.BonusResistIce);
            AppendResistLine(_sharedSB, "Resist. Veneno", item.BonusResistPoison);
            AppendResistLine(_sharedSB, "Resist. Raio",   item.BonusResistLightning);

            if (item.MaxDurability > 0)
            {
                _sharedSB.AppendLine();
                _sharedSB.Append($"<color=#AAAAAA>Durabilidade: {item.MaxDurability}/{item.MaxDurability}</color>");
            }

            if (_statsText != null) _statsText.text = _sharedSB.ToString().TrimEnd();
            _statsSection.SetActive(_sharedSB.Length > 0);
        }

        private static void AppendStatLine(StringBuilder sb, string label, int value)
        {
            if (value == 0) return;
            string sign  = value > 0 ? "+" : "";
            string color = value > 0 ? "#66FF66" : "#FF6666";
            sb.AppendLine($"<color={color}>{sign}{value} {label}</color>");
        }

        private static void AppendStatLine(StringBuilder sb, string label, float value)
        {
            if (Mathf.Approximately(value, 0f)) return;
            string sign  = value > 0f ? "+" : "";
            string color = value > 0f ? "#66FF66" : "#FF6666";
            sb.AppendLine($"<color={color}>{sign}{value:0.#} {label}</color>");
        }

        private static void AppendResistLine(StringBuilder sb, string label, float value)
        {
            if (Mathf.Approximately(value, 0f)) return;
            string sign  = value > 0f ? "+" : "";
            string color = value > 0f ? "#66FFAA" : "#FF6666";
            sb.AppendLine($"<color={color}>{sign}{value:0.#}% {label}</color>");
        }

        // ── Requisitos ─────────────────────────────────────────────────────

        private void ShowRequirementsSection(ItemData item)
        {
            if (_requirementsSection == null) return;

            if (!item.IsEquipment || item.Requirements == null)
            {
                _requirementsSection.SetActive(false);
                return;
            }

            var req = item.Requirements;
            bool hasAny = req.MinLevel > 1
                       || req.MinSTR > 0 || req.MinAGI > 0 || req.MinVIT > 0
                       || req.MinDEX > 0 || req.MinINT > 0 || req.MinLUK > 0
                       || req.AllowedRaces != CharacterRaceFlags.All;

            if (!hasAny) { _requirementsSection.SetActive(false); return; }

            _sharedSB.Clear();
            _sharedSB.AppendLine("<b>Requisitos:</b>");

            bool hasPlayer = TryGetLocalPlayerStats(
                out int level, out int str, out int agi, out int vit,
                out int dex, out int intt, out int luk, out CharacterRace race);

            if (req.MinLevel > 1)
                _sharedSB.AppendLine(FormatReq($"Nível {req.MinLevel}+", hasPlayer && level >= req.MinLevel));
            if (req.MinSTR > 0) _sharedSB.AppendLine(FormatReq($"STR {req.MinSTR}+", hasPlayer && str  >= req.MinSTR));
            if (req.MinAGI > 0) _sharedSB.AppendLine(FormatReq($"AGI {req.MinAGI}+", hasPlayer && agi  >= req.MinAGI));
            if (req.MinVIT > 0) _sharedSB.AppendLine(FormatReq($"VIT {req.MinVIT}+", hasPlayer && vit  >= req.MinVIT));
            if (req.MinDEX > 0) _sharedSB.AppendLine(FormatReq($"DEX {req.MinDEX}+", hasPlayer && dex  >= req.MinDEX));
            if (req.MinINT > 0) _sharedSB.AppendLine(FormatReq($"INT {req.MinINT}+", hasPlayer && intt >= req.MinINT));
            if (req.MinLUK > 0) _sharedSB.AppendLine(FormatReq($"LUK {req.MinLUK}+", hasPlayer && luk  >= req.MinLUK));

            if (req.AllowedRaces != CharacterRaceFlags.All)
            {
                bool ok = !hasPlayer || (req.AllowedRaces & EquipmentSlotEx.ToFlag(race)) != 0;
                _sharedSB.AppendLine(FormatReq(EquipmentSlotEx.FlagsDisplayName(req.AllowedRaces), ok));
            }

            if (_requirementsText != null) _requirementsText.text = _sharedSB.ToString().TrimEnd();
            _requirementsSection.SetActive(true);
        }

        private static string FormatReq(string text, bool met)
            => met
                ? $"<color=#88FF88>✓ {text}</color>"
                : $"<color=#FF6666>✗ {text}</color>";

        private static bool TryGetLocalPlayerStats(out int level, out int str, out int agi, out int vit,
                                                   out int dex, out int intt, out int luk,
                                                   out CharacterRace race)
        {
            level = 1; str = 0; agi = 0; vit = 0; dex = 0; intt = 0; luk = 0;
            race  = CharacterRace.Human;

            var localId = NetworkClient.localPlayer;
            if (localId == null) return false;

            var np = localId.GetComponent<RPG.Network.NetworkPlayer>();
            if (np == null) return false;

            level = np.Level;

            if (!System.Enum.TryParse<CharacterRace>(np.RaceStr, out race))
                race = CharacterRace.Human;

            var raceBonus = StatsCalculator.GetRaceBonus(race);

            str  = np.BaseSTR + raceBonus.STR + np.AllocatedSTR;
            agi  = np.BaseAGI + raceBonus.AGI + np.AllocatedAGI;
            vit  = np.BaseVIT + raceBonus.VIT + np.AllocatedVIT;
            dex  = np.BaseDEX + raceBonus.DEX + np.AllocatedDEX;
            intt = np.BaseINT + raceBonus.INT + np.AllocatedINT;
            luk  = np.BaseLUK + raceBonus.LUK + np.AllocatedLUK;

            return true;
        }

        // ── Skill ──────────────────────────────────────────────────────────

        private void ShowSkillSection(ItemData item)
        {
            if (_skillSection == null) return;

            if (!item.IsPowerGem || item.EmbeddedSkill == null)
            {
                _skillSection.SetActive(false);
                return;
            }

            var skill = item.EmbeddedSkill;
            _sharedSB.Clear();
            _sharedSB.AppendLine($"<b>Concede: {skill.Name}</b>");
            _sharedSB.AppendLine($"Custo: {skill.ManaCost} MP");
            _sharedSB.AppendLine($"Cooldown: {skill.Cooldown:0.#}s");

            if (_skillText != null) _skillText.text = _sharedSB.ToString().TrimEnd();
            _skillSection.SetActive(true);
        }

        // ── Consumível ─────────────────────────────────────────────────────

        private void ShowConsumableSection(ItemData item)
        {
            if (_consumableSection == null) return;

            if (!item.IsConsumable) { _consumableSection.SetActive(false); return; }

            _sharedSB.Clear();
            if (item.HealAmount > 0f)   _sharedSB.AppendLine($"<color=#66FF66>+{item.HealAmount:0} HP</color>");
            if (item.ManaAmount > 0f)   _sharedSB.AppendLine($"<color=#66AAFF>+{item.ManaAmount:0} MP</color>");
            if (item.BuffDuration > 0f) _sharedSB.AppendLine($"Duração: {item.BuffDuration:0.#}s");

            if (_consumableText != null) _consumableText.text = _sharedSB.ToString().TrimEnd();
            _consumableSection.SetActive(_sharedSB.Length > 0);
        }

        // ══════════════════════════════════════════════════════════════════
        // Posicionamento (inalterado)
        // ══════════════════════════════════════════════════════════════════

        private void PositionByAnchor(RectTransform anchor)
        {
            if (_root == null || anchor == null)
            {
                PositionByCursor();
                return;
            }

            Canvas canvas = _root.GetComponentInParent<Canvas>();
            if (canvas == null) return;

            Vector3[] corners = new Vector3[4];
            anchor.GetWorldCorners(corners);

            Vector3 worldPos = corners[2];
            worldPos += new Vector3(_offset.x, _offset.y, 0f) * canvas.scaleFactor / 100f;
            _root.position = worldPos;

            ClampToScreen(canvas);
        }

        private void PositionByCursor()
        {
            if (_root == null) return;
            Canvas canvas = _root.GetComponentInParent<Canvas>();
            if (canvas == null) return;

            Vector3 mousePos = Input.mousePosition;
            mousePos.x += _offset.x;
            mousePos.y += _offset.y;

            if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                mousePos.z = 0f;
                _root.position = mousePos;
            }
            else
            {
                Camera cam = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
                if (cam != null)
                {
                    mousePos.z = canvas.planeDistance;
                    Vector3 worldPos = cam.ScreenToWorldPoint(mousePos);
                    _root.position = worldPos;
                }
            }

            ClampToScreen(canvas);
        }

        private void ClampToScreen(Canvas canvas)
        {
            if (_root == null || canvas == null) return;

            Vector3[] corners = new Vector3[4];
            _root.GetWorldCorners(corners);

            var canvasRect = canvas.transform as RectTransform;
            if (canvasRect == null) return;

            Vector3[] canvasCorners = new Vector3[4];
            canvasRect.GetWorldCorners(canvasCorners);

            float dx = 0f, dy = 0f;
            if (corners[2].x > canvasCorners[2].x) dx = canvasCorners[2].x - corners[2].x;
            if (corners[0].x < canvasCorners[0].x) dx = canvasCorners[0].x - corners[0].x;
            if (corners[1].y > canvasCorners[1].y) dy = canvasCorners[1].y - corners[1].y;
            if (corners[0].y < canvasCorners[0].y) dy = canvasCorners[0].y - corners[0].y;

            if (dx != 0f || dy != 0f)
                _root.position += new Vector3(dx, dy, 0f);
        }

        private static string GetTypeDisplay(ItemData item)
        {
            if (item.IsWeapon)     return item.WeaponTypeDisplayName;
            if (item.IsEquipment)  return EquipmentSlotEx.DisplayName(item.EquipSlot);
            if (item.IsPowerGem)   return "Joia do Poder";
            if (item.IsConsumable) return "Consumível";
            return item.Type.ToString();
        }
    }
}
