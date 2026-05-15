using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RPG.Character;
using RPG.Data;
using NetworkPlayer = RPG.Network.NetworkPlayer;

namespace RPG.UI
{
    /// <summary>
    /// AttributeWindowUI — Janela de atributos (tecla C).
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 5 — performance) ===
    ///
    ///   1. NÃO RECALCULA STATS NO CLIENTE:
    ///      Antes, RefreshAll chamava StatsCalculator.Calculate toda vez que
    ///      o painel atualizava, replicando a lógica do servidor no cliente.
    ///      Isso era:
    ///        - Custoso (CalculateInternal aloca DerivedStats)
    ///        - Redundante (NetworkPlayer já tem MaxHP/MaxMP via SyncVars)
    ///        - Risco de drift (se a fórmula mudar, esquece de atualizar aqui)
    ///
    ///      Agora usamos diretamente _player.Stats (PlayerEntity já recebe
    ///      stats do servidor via SyncVars), evitando recálculo no cliente.
    ///      A janela exibe stats AUTORITATIVOS, não predição local.
    ///
    ///   2. CLEANUP MAIS DEFENSIVO:
    ///      Removida a dependência do cancelInvoke (FinishAllocatingTimeout).
    ///      O timeout agora é uma Coroutine que pode ser cancelada explicitamente,
    ///      e é cancelada em Close() e em mudanças de player.
    /// </summary>
    public class AttributeWindowUI : MonoBehaviour
    {
        public static AttributeWindowUI Instance { get; private set; }

        [Header("Painel")]
        [SerializeField] private GameObject windowPanel;

        [Header("Header")]
        [SerializeField] private TMP_Text charNameText;
        [SerializeField] private TMP_Text raceText;
        [SerializeField] private TMP_Text levelText;
        [SerializeField] private Button   closeButton;

        [Header("Pontos Livres")]
        [SerializeField] private GameObject freePointsBanner;
        [SerializeField] private TMP_Text   freePointsText;

        [Header("Atributos Base — Textos")]
        [SerializeField] private TMP_Text strValueText;
        [SerializeField] private TMP_Text agiValueText;
        [SerializeField] private TMP_Text vitValueText;
        [SerializeField] private TMP_Text dexValueText;
        [SerializeField] private TMP_Text intValueText;
        [SerializeField] private TMP_Text lukValueText;

        [Header("Atributos Base — Botões +")]
        [SerializeField] private Button strPlusButton;
        [SerializeField] private Button agiPlusButton;
        [SerializeField] private Button vitPlusButton;
        [SerializeField] private Button dexPlusButton;
        [SerializeField] private Button intPlusButton;
        [SerializeField] private Button lukPlusButton;

        [Header("Status Derivados")]
        [SerializeField] private TMP_Text hpDerivedText;
        [SerializeField] private TMP_Text mpDerivedText;
        [SerializeField] private TMP_Text atkText;
        [SerializeField] private TMP_Text matkText;
        [SerializeField] private TMP_Text defText;
        [SerializeField] private TMP_Text mdefText;
        [SerializeField] private TMP_Text aspdText;
        [SerializeField] private TMP_Text hitText;
        [SerializeField] private TMP_Text fleeText;
        [SerializeField] private TMP_Text critText;
        [SerializeField] private TMP_Text hpregenText;
        [SerializeField] private TMP_Text mpregenText;

        [Header("XP")]
        [SerializeField] private Slider   xpBar;
        [SerializeField] private TMP_Text xpText;

        [Header("Configuração")]
        [Tooltip("Tempo máximo de espera pela confirmação do servidor (fallback para alta latência).")]
        [SerializeField] private float allocateConfirmTimeout = 1.5f;

        private PlayerEntity  _player;
        private NetworkPlayer _netPlayer;
        private bool          _isOpen;
        private bool          _allocating;

        private int _pendingAllocIndex = -1;

        // Coroutine de timeout substituindo Invoke(string) — cancelável de forma confiável
        private Coroutine _allocateTimeoutCoroutine;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            if (windowPanel != null) windowPanel.SetActive(false);
            _isOpen = false;

            if (closeButton    != null) closeButton.onClick.AddListener(Close);
            if (strPlusButton  != null) strPlusButton.onClick.AddListener(() => RequestAllocate(0));
            if (agiPlusButton  != null) agiPlusButton.onClick.AddListener(() => RequestAllocate(1));
            if (vitPlusButton  != null) vitPlusButton.onClick.AddListener(() => RequestAllocate(2));
            if (dexPlusButton  != null) dexPlusButton.onClick.AddListener(() => RequestAllocate(3));
            if (intPlusButton  != null) intPlusButton.onClick.AddListener(() => RequestAllocate(4));
            if (lukPlusButton  != null) lukPlusButton.onClick.AddListener(() => RequestAllocate(5));
        }

        private void OnDestroy()
        {
            StopAllocateTimeout();
            UnsubscribeFromPlayer();
            if (Instance == this) Instance = null;
        }

        private void StopAllocateTimeout()
        {
            if (_allocateTimeoutCoroutine != null)
            {
                StopCoroutine(_allocateTimeoutCoroutine);
                _allocateTimeoutCoroutine = null;
            }
        }

        // ── Vínculo com PlayerEntity ───────────────────────────────────────

        public void BindPlayer(PlayerEntity player)
        {
            if (player == null) return;

            var newNetPlayer = player.GetComponent<NetworkPlayer>();
            bool samePlayer  = (_player == player && _netPlayer == newNetPlayer);
            if (samePlayer) return;

            // Cancela qualquer timeout pendente do player anterior
            StopAllocateTimeout();
            _allocating        = false;
            _pendingAllocIndex = -1;

            UnsubscribeFromPlayer();

            _player    = player;
            _netPlayer = newNetPlayer;

            _player.OnStatsChanged += OnDataChanged;
            _player.OnInitialized  += OnDataChanged;
            _player.OnHPChanged    += OnHPMPChanged;
            _player.OnMPChanged    += OnHPMPChanged;

            if (player.IsInitialized) RefreshAll();

            // Log com fallback para nome do GameObject
            string charName = player.Data?.CharacterName;
            if (string.IsNullOrEmpty(charName))
                charName = newNetPlayer?.CharacterName;
            if (string.IsNullOrEmpty(charName))
                charName = player.gameObject.name;

            Debug.Log($"[AttributeWindowUI] Vinculado a {charName}");
        }

        private void UnsubscribeFromPlayer()
        {
            if (_player == null) return;
            _player.OnStatsChanged -= OnDataChanged;
            _player.OnInitialized  -= OnDataChanged;
            _player.OnHPChanged    -= OnHPMPChanged;
            _player.OnMPChanged    -= OnHPMPChanged;
        }

        // ── Callbacks de eventos ───────────────────────────────────────────

        private void OnDataChanged()
        {
            if (_isOpen) RefreshAll();
        }

        private void OnHPMPChanged(float _, float __)
        {
            if (!_isOpen || _player == null || !_player.IsInitialized) return;
            RefreshHPMP();
        }

        public void OnFreePointsUpdated(int newPoints)
        {
            StopAllocateTimeout();
            FinishAllocating();
            RefreshFreePointsBanner(newPoints);
            RefreshPlusButtons(newPoints);
            if (_isOpen) RefreshAll();
        }

        // ── Abrir / Fechar ─────────────────────────────────────────────────

        public void Toggle() { if (_isOpen) Close(); else Open(); }

        public void Open()
        {
            if (windowPanel == null) return;
            _isOpen = true;
            windowPanel.SetActive(true);
            RefreshAll();
        }

        public void Close()
        {
            if (windowPanel == null) return;
            _isOpen = false;
            windowPanel.SetActive(false);

            // Cancela qualquer alocação pendente ao fechar
            StopAllocateTimeout();
            if (_allocating) FinishAllocating();
        }

        // ── Refresh ────────────────────────────────────────────────────────

        /// <summary>
        /// Atualiza TODO o painel. Usa stats AUTORITATIVOS de _player.Stats
        /// (recebidos do servidor via SyncVars) em vez de recalcular localmente.
        /// </summary>
        private void RefreshAll()
        {
            if (_player == null || !_player.IsInitialized) return;
            var data  = _player.Data;
            var stats = _player.Stats;
            if (data == null || stats == null) return;

            int level      = _netPlayer != null ? _netPlayer.Level                : data.Level;
            long exp       = _netPlayer != null ? _netPlayer.Experience            : data.Experience;
            long expToNext = _netPlayer != null ? _netPlayer.ExperienceToNextLevel : data.ExperienceToNextLevel;
            int freePoints = _netPlayer != null ? _netPlayer.FreeAttributePoints   : data.FreeAttributePoints;

            int allocSTR = _netPlayer != null ? _netPlayer.AllocatedSTR : data.AllocatedSTR;
            int allocAGI = _netPlayer != null ? _netPlayer.AllocatedAGI : data.AllocatedAGI;
            int allocVIT = _netPlayer != null ? _netPlayer.AllocatedVIT : data.AllocatedVIT;
            int allocDEX = _netPlayer != null ? _netPlayer.AllocatedDEX : data.AllocatedDEX;
            int allocINT = _netPlayer != null ? _netPlayer.AllocatedINT : data.AllocatedINT;
            int allocLUK = _netPlayer != null ? _netPlayer.AllocatedLUK : data.AllocatedLUK;

            int baseSTR = _netPlayer != null ? _netPlayer.BaseSTR : data.BaseAttributes.STR;
            int baseAGI = _netPlayer != null ? _netPlayer.BaseAGI : data.BaseAttributes.AGI;
            int baseVIT = _netPlayer != null ? _netPlayer.BaseVIT : data.BaseAttributes.VIT;
            int baseDEX = _netPlayer != null ? _netPlayer.BaseDEX : data.BaseAttributes.DEX;
            int baseINT = _netPlayer != null ? _netPlayer.BaseINT : data.BaseAttributes.INT;
            int baseLUK = _netPlayer != null ? _netPlayer.BaseLUK : data.BaseAttributes.LUK;

            float curHp = _netPlayer != null ? _netPlayer.CurrentHP : _player.CurrentHP;
            float curMp = _netPlayer != null ? _netPlayer.CurrentMP : _player.CurrentMP;

            RefreshHeader(data.CharacterName, data.Race, level);
            RefreshBaseAttributes(
                data.Race,
                baseSTR, baseAGI, baseVIT, baseDEX, baseINT, baseLUK,
                allocSTR, allocAGI, allocVIT, allocDEX, allocINT, allocLUK);

            // USA STATS AUTORITATIVOS — sem recalcular no cliente
            RefreshDerivedStats(stats, curHp, curMp);

            RefreshXPBar(exp, expToNext);
            RefreshFreePointsBanner(freePoints);
            RefreshPlusButtons(freePoints);
        }

        private void RefreshHPMP()
        {
            if (_player == null) return;

            float curHp = _netPlayer != null ? _netPlayer.CurrentHP : _player.CurrentHP;
            float curMp = _netPlayer != null ? _netPlayer.CurrentMP : _player.CurrentMP;
            float maxHp = _netPlayer != null ? _netPlayer.MaxHP     : (_player.Stats?.MaxHP ?? 1f);
            float maxMp = _netPlayer != null ? _netPlayer.MaxMP     : (_player.Stats?.MaxMP ?? 1f);

            if (hpDerivedText != null) hpDerivedText.text = $"{curHp:0} / {maxHp:0}";
            if (mpDerivedText != null) mpDerivedText.text = $"{curMp:0} / {maxMp:0}";
        }

        private void RefreshHeader(string charName, CharacterRace race, int level)
        {
            if (charNameText != null) charNameText.text = charName;
            if (raceText     != null) raceText.text     = RaceDisplayName(race);
            if (levelText    != null) levelText.text    = $"Nível {level}";
        }

        private void RefreshBaseAttributes(
            CharacterRace race,
            int baseSTR, int baseAGI, int baseVIT,
            int baseDEX, int baseINT, int baseLUK,
            int allocSTR, int allocAGI, int allocVIT,
            int allocDEX, int allocINT, int allocLUK)
        {
            var bonus = StatsCalculator.GetRaceBonus(race);

            int totalSTR = baseSTR + bonus.STR + allocSTR;
            int totalAGI = baseAGI + bonus.AGI + allocAGI;
            int totalVIT = baseVIT + bonus.VIT + allocVIT;
            int totalDEX = baseDEX + bonus.DEX + allocDEX;
            int totalINT = baseINT + bonus.INT + allocINT;
            int totalLUK = baseLUK + bonus.LUK + allocLUK;

            int bonusSTR = bonus.STR + allocSTR;
            int bonusAGI = bonus.AGI + allocAGI;
            int bonusVIT = bonus.VIT + allocVIT;
            int bonusDEX = bonus.DEX + allocDEX;
            int bonusINT = bonus.INT + allocINT;
            int bonusLUK = bonus.LUK + allocLUK;

            SetAttrText(strValueText, totalSTR, bonusSTR, _allocating && _pendingAllocIndex == 0);
            SetAttrText(agiValueText, totalAGI, bonusAGI, _allocating && _pendingAllocIndex == 1);
            SetAttrText(vitValueText, totalVIT, bonusVIT, _allocating && _pendingAllocIndex == 2);
            SetAttrText(dexValueText, totalDEX, bonusDEX, _allocating && _pendingAllocIndex == 3);
            SetAttrText(intValueText, totalINT, bonusINT, _allocating && _pendingAllocIndex == 4);
            SetAttrText(lukValueText, totalLUK, bonusLUK, _allocating && _pendingAllocIndex == 5);
        }

        private void SetAttrText(TMP_Text label, int total, int bonus, bool isPending = false)
        {
            if (label == null) return;

            if (isPending)
            {
                label.text = bonus > 0
                    ? $"{total} <color=#88FF88>(+{bonus})</color> <color=#FFD700>↑</color>"
                    : $"{total} <color=#FFD700>↑</color>";
            }
            else if (bonus > 0)
            {
                label.text = $"{total} <color=#88FF88>(+{bonus})</color>";
            }
            else
            {
                label.text = $"{total}";
            }
        }

        /// <summary>
        /// Exibe stats derivados. Recebe DerivedStats já calculado pelo servidor
        /// e MaxHP/MaxMP via SyncVars (em vez de recalcular).
        /// </summary>
        private void RefreshDerivedStats(DerivedStats s, float hp, float mp)
        {
            // Usa MaxHP/MaxMP do NetworkPlayer se disponível, caso contrário do stats
            float maxHp = _netPlayer != null ? _netPlayer.MaxHP : s.MaxHP;
            float maxMp = _netPlayer != null ? _netPlayer.MaxMP : s.MaxMP;

            if (hpDerivedText != null) hpDerivedText.text = $"{hp:0} / {maxHp:0}";
            if (mpDerivedText != null) mpDerivedText.text = $"{mp:0} / {maxMp:0}";
            if (atkText       != null) atkText.text       = $"{s.ATK:0}";
            if (matkText      != null) matkText.text      = $"{s.MATK:0}";
            if (defText       != null) defText.text       = $"{s.DEF:0}";
            if (mdefText      != null) mdefText.text      = $"{s.MDEF:0}";
            if (aspdText      != null) aspdText.text      = $"{s.ASPD:0.00}";
            if (hitText       != null) hitText.text       = $"{s.HIT:0}";
            if (fleeText      != null) fleeText.text      = $"{s.FLEE:0}";
            if (critText      != null) critText.text      = $"{s.CRIT:0.0}%";
            if (hpregenText   != null) hpregenText.text   = $"{s.HPRegen:0.0}/5s";
            if (mpregenText   != null) mpregenText.text   = $"{s.MPRegen:0.0}/5s";
        }

        public void RefreshXPBar(long exp, long expToNext)
        {
            if (xpBar != null)
            {
                xpBar.maxValue = Mathf.Max(1f, expToNext);
                xpBar.value    = exp;
            }
            if (xpText != null) xpText.text = $"{exp} / {expToNext} XP";
        }

        private void RefreshFreePointsBanner(int freePoints)
        {
            bool has = freePoints > 0;
            if (freePointsBanner != null) freePointsBanner.SetActive(has);
            if (freePointsText != null && has)
                freePointsText.text = freePoints == 1
                    ? "1 ponto disponível!"
                    : $"{freePoints} pontos disponíveis!";
        }

        private void RefreshPlusButtons(int freePoints)
        {
            bool can = freePoints > 0 && !_allocating;

            if (strPlusButton != null) strPlusButton.interactable = can;
            if (agiPlusButton != null) agiPlusButton.interactable = can;
            if (vitPlusButton != null) vitPlusButton.interactable = can;
            if (dexPlusButton != null) dexPlusButton.interactable = can;
            if (intPlusButton != null) intPlusButton.interactable = can;
            if (lukPlusButton != null) lukPlusButton.interactable = can;
        }

        // ── Alocação de Pontos ─────────────────────────────────────────────

        private void RequestAllocate(int attributeIndex)
        {
            if (_allocating) return;
            if (_netPlayer == null)
            {
                UIManager.Instance?.ShowMessage("Alocação requer conexão com o servidor.");
                return;
            }
            if (_netPlayer.FreeAttributePoints <= 0)
            {
                UIManager.Instance?.ShowMessage("Sem pontos disponíveis!");
                return;
            }

            _allocating         = true;
            _pendingAllocIndex  = attributeIndex;

            SetPlusButtonsInteractable(false);

            _netPlayer.CmdAllocateAttribute(attributeIndex);

            if (_isOpen) RefreshAll();

            // Timeout via coroutine (cancelável de forma confiável)
            StopAllocateTimeout();
            _allocateTimeoutCoroutine = StartCoroutine(AllocateTimeoutCoroutine());
        }

        private System.Collections.IEnumerator AllocateTimeoutCoroutine()
        {
            yield return new WaitForSeconds(allocateConfirmTimeout);

            _allocateTimeoutCoroutine = null;

            if (!_allocating) yield break;

            Debug.LogWarning("[AttributeWindowUI] Timeout aguardando confirmação de alocação. " +
                             "Re-habilitando botões.");
            FinishAllocating();
        }

        private void FinishAllocating()
        {
            if (!_allocating) return;

            _allocating        = false;
            _pendingAllocIndex = -1;

            int fp = _netPlayer != null ? _netPlayer.FreeAttributePoints : 0;
            RefreshPlusButtons(fp);

            if (_isOpen) RefreshAll();
        }

        private void SetPlusButtonsInteractable(bool value)
        {
            if (strPlusButton != null) strPlusButton.interactable = value;
            if (agiPlusButton != null) agiPlusButton.interactable = value;
            if (vitPlusButton != null) vitPlusButton.interactable = value;
            if (dexPlusButton != null) dexPlusButton.interactable = value;
            if (intPlusButton != null) intPlusButton.interactable = value;
            if (lukPlusButton != null) lukPlusButton.interactable = value;
        }

        private static string RaceDisplayName(CharacterRace race) => race switch
        {
            CharacterRace.Human  => "Humano",
            CharacterRace.Elf    => "Elfo",
            CharacterRace.Dwarf  => "Anão",
            CharacterRace.Orc    => "Orc",
            CharacterRace.Undead => "Morto-Vivo",
            _ => race.ToString()
        };
    }
}
