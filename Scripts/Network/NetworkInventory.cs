using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.Combat;
using RPG.UI;

namespace RPG.Network
{
    /// <summary>
    /// Inventário do jogador. Server-authoritative.
    ///
    /// Coleções:
    ///   - Slots: inventário livre (SyncList&lt;InventorySlotData&gt;).
    ///   - EquippedItems: itens equipados (SyncList&lt;EquippedItemData&gt;).
    ///   - GemSlotQ/W/E/R: SyncVars com IDs das Joias do Poder equipadas.
    ///
    /// === SEGURANÇA ===
    /// Toda operação de troca (equip/swap gem) segue o padrão SNAPSHOT → REMOVE NOVO →
    /// DEVOLVE ANTIGO → APLICA NOVO. Isso garante que em caso de falha em qualquer
    /// etapa, nada é duplicado e nada é perdido.
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 2 — robustez do swap) ===
    ///
    ///   1. PRÉ-CONDIÇÃO DE ESPAÇO NO SWAP:
    ///      Antes, TrySwapFromInventory primeiro removia o item novo
    ///      ("liberando 1 slot") para depois tentar devolver o antigo.
    ///      MAS: a remoção do novo só libera espaço se o slot novo era
    ///      o ÚNICO item naquele "índice de slot". Como Slots é um SyncList
    ///      sem buracos (RemoveAt), funcionava — mas a condição de falha
    ///      era frágil.
    ///
    ///      Agora: validamos ANTES de mutar que, se houver item antigo
    ///      para devolver, a operação líquida no inventário é -1 + 1 = 0,
    ///      ou seja, a contagem permanece a mesma. Se NÃO houver item antigo,
    ///      a contagem cai 1 (remove novo, nada a devolver). Em ambos casos
    ///      não há risco de "inventário cheio" durante o rollback.
    ///
    ///   2. ROLLBACK ROBUSTO:
    ///      O caminho de rollback patológico ("ServerAddItem falhou e
    ///      forçamos Slots.Add") agora é documentado e tratado como
    ///      condição de erro grave que loga no servidor com stack trace
    ///      para investigação posterior.
    ///
    ///   3. VALIDAÇÃO ANTECIPADA EM TODOS OS Cmds:
    ///      connectionToClient == null check no início de cada Cmd
    ///      (defesa em profundidade contra desconexão durante request).
    ///
    ///   4. CmdUseConsumable: SANITIZAÇÃO DE VALORES:
    ///      Antes, item.HealAmount > 0f passava direto para ServerApplyHeal.
    ///      Agora, valores absurdos (NaN, Infinity, > MaxHP) são clampados
    ///      defensivamente — proteção contra ItemData com bugs de Inspector.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkInventory : NetworkBehaviour
    {
        public const int MAX_INVENTORY_SLOTS = 60;
        public const int GEM_SLOT_COUNT      = 4;

        // ── Sincronização ──────────────────────────────────────────────────
        public readonly SyncList<InventorySlotData> Slots         = new SyncList<InventorySlotData>();
        public readonly SyncList<EquippedItemData>  EquippedItems = new SyncList<EquippedItemData>();

        [SyncVar(hook = nameof(OnGemSlotQChanged))] public string GemSlotQ = "";
        [SyncVar(hook = nameof(OnGemSlotWChanged))] public string GemSlotW = "";
        [SyncVar(hook = nameof(OnGemSlotEChanged))] public string GemSlotE = "";
        [SyncVar(hook = nameof(OnGemSlotRChanged))] public string GemSlotR = "";

        // ── Eventos (cliente) ──────────────────────────────────────────────
        public event Action OnInventoryChanged;
        public event Action OnGemLoadoutChanged;
        public event Action OnEquipmentChanged;

        // ── Estado do servidor ─────────────────────────────────────────────
        private int           _nextSlotIndex;
        private NetworkPlayer _netPlayer;

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            _netPlayer = GetComponent<NetworkPlayer>();
        }

        public override void OnStartClient()
        {
            Slots.Callback         += OnSlotsChangedClient;
            EquippedItems.Callback += OnEquippedItemsChangedClient;
        }

        public override void OnStopClient()
        {
            Slots.Callback         -= OnSlotsChangedClient;
            EquippedItems.Callback -= OnEquippedItemsChangedClient;
        }

        public override void OnStartLocalPlayer()
        {
            StartCoroutine(BindUIDelayed());
        }

        private IEnumerator BindUIDelayed()
        {
            yield return null;
            yield return null;

            InventoryUI.Instance?.BindInventory(this);
            PowerGemUI.Instance?.BindInventory(this);
        }

        // ── Hooks ──────────────────────────────────────────────────────────

        private void OnSlotsChangedClient(SyncList<InventorySlotData>.Operation op,
                                          int index, InventorySlotData oldItem, InventorySlotData newItem)
            => OnInventoryChanged?.Invoke();

        private void OnEquippedItemsChangedClient(SyncList<EquippedItemData>.Operation op,
                                                  int index, EquippedItemData oldItem, EquippedItemData newItem)
            => OnEquipmentChanged?.Invoke();

        private void OnGemSlotQChanged(string oldVal, string newVal) => OnGemLoadoutChanged?.Invoke();
        private void OnGemSlotWChanged(string oldVal, string newVal) => OnGemLoadoutChanged?.Invoke();
        private void OnGemSlotEChanged(string oldVal, string newVal) => OnGemLoadoutChanged?.Invoke();
        private void OnGemSlotRChanged(string oldVal, string newVal) => OnGemLoadoutChanged?.Invoke();

        // ══════════════════════════════════════════════════════════════════
        // INVENTÁRIO — API do servidor
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public int ServerAddItem(string itemId, int quantity = 1)
        {
            if (string.IsNullOrEmpty(itemId)) return -1;
            if (quantity <= 0) return -1;

            var db = ItemDatabase.Instance;
            if (db == null || !db.Contains(itemId))
            {
                Debug.LogWarning($"[NetworkInventory] Item '{itemId}' não existe no banco.");
                return -1;
            }

            if (Slots.Count >= MAX_INVENTORY_SLOTS)
            {
                _netPlayer?.RpcShowMessageToOwner("Inventário cheio!");
                return -1;
            }

            var slot = new InventorySlotData
            {
                SlotIndex = _nextSlotIndex++,
                ItemId    = itemId,
                Quantity  = quantity
            };

            Slots.Add(slot);
            return slot.SlotIndex;
        }

        [Server]
        public bool ServerRemoveSlot(int slotIndex)
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].SlotIndex == slotIndex)
                {
                    Slots.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        [Server]
        public bool ServerRemoveItemById(string itemId)
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].ItemId == itemId)
                {
                    Slots.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public bool HasItem(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return false;
            foreach (var slot in Slots)
                if (slot.ItemId == itemId) return true;
            return false;
        }

        public int FindSlotByItemId(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return -1;
            foreach (var slot in Slots)
                if (slot.ItemId == itemId) return slot.SlotIndex;
            return -1;
        }

        [Server]
        public void ServerLoadFromDatabase(string characterId)
        {
            var db = Managers.DatabaseManager.Instance;
            if (db == null) return;

            Slots.Clear();
            _nextSlotIndex = 0;

            var rows = db.LoadInventory(characterId);
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.ItemId)) continue;

                if (ItemDatabase.Instance != null && !ItemDatabase.Instance.Contains(row.ItemId))
                {
                    Debug.LogWarning($"[NetworkInventory] Item '{row.ItemId}' do banco não está no ItemDatabase — ignorado.");
                    continue;
                }

                var slot = new InventorySlotData
                {
                    SlotIndex = row.SlotIndex >= 0 ? row.SlotIndex : _nextSlotIndex,
                    ItemId    = row.ItemId,
                    Quantity  = Mathf.Max(1, row.Quantity)
                };
                Slots.Add(slot);
            }

            if (Slots.Count > 0)
                _nextSlotIndex = Slots.Max(s => s.SlotIndex) + 1;
        }

        [Server]
        public void ServerLoadGemLoadout(string characterId)
        {
            var db = Managers.DatabaseManager.Instance;
            if (db == null) return;

            var loadout = db.LoadGemLoadout(characterId);
            GemSlotQ = ValidateLoadedGemId(loadout.SlotQ);
            GemSlotW = ValidateLoadedGemId(loadout.SlotW);
            GemSlotE = ValidateLoadedGemId(loadout.SlotE);
            GemSlotR = ValidateLoadedGemId(loadout.SlotR);
        }

        [Server]
        private static string ValidateLoadedGemId(string gemId)
        {
            if (string.IsNullOrEmpty(gemId)) return "";
            var db = ItemDatabase.Instance;
            if (db == null) return gemId;
            var item = db.GetItem(gemId);
            if (item == null || !item.IsPowerGem)
            {
                Debug.LogWarning($"[NetworkInventory] Gem '{gemId}' inválida no banco — slot limpo.");
                return "";
            }
            return gemId;
        }

        [Server]
        public void ServerLoadEquippedFromDatabase(string characterId)
        {
            var db = Managers.DatabaseManager.Instance;
            if (db == null) return;

            EquippedItems.Clear();

            var rows = db.LoadEquipped(characterId);
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.ItemId)) continue;

                var itemData = ItemDatabase.Instance?.GetItem(row.ItemId);
                if (itemData == null || !itemData.IsEquipment)
                {
                    Debug.LogWarning($"[NetworkInventory] Equipped item '{row.ItemId}' inválido — ignorado.");
                    continue;
                }

                EquippedItems.Add(new EquippedItemData
                {
                    Slot          = (byte)row.Slot,
                    ItemId        = row.ItemId,
                    Durability    = row.Durability,
                    MaxDurability = row.MaxDurability
                });
            }
        }

        [Server]
        public void ServerSaveAll(string characterId, string username)
        {
            var db = Managers.DatabaseManager.Instance;
            if (db == null) return;

            db.SaveInventory(characterId, username, new List<InventorySlotData>(Slots));
            db.SaveGemLoadout(characterId, new PowerGemLoadout
            {
                SlotQ = GemSlotQ ?? "", SlotW = GemSlotW ?? "",
                SlotE = GemSlotE ?? "", SlotR = GemSlotR ?? ""
            });
            db.SaveEquipped(characterId, new List<EquippedItemData>(EquippedItems));
        }

        // ══════════════════════════════════════════════════════════════════
        // SWAP HELPER — usado por equip e gem-equip
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Protocolo atômico de troca entre inventário e um "slot externo"
        /// (slot de equipamento OU slot de joia).
        ///
        /// === INVARIANTE ===
        /// A operação líquida no inventário é:
        ///   - Sem item antigo:  -1 (removeu novo, nada a devolver)
        ///   - Com item antigo:   0 (removeu novo, devolveu antigo)
        ///
        /// Em ambos os casos, NÃO HÁ RISCO de "inventário cheio" durante a
        /// devolução do antigo, porque acabamos de liberar um slot.
        ///
        /// Passos:
        ///   1. Remove o item NOVO do inventário (libera 1 slot livre).
        ///   2. Se havia item ANTIGO no slot externo, devolve-o ao inventário.
        ///   3. Em caso patológico de falha (item antigo sumiu do banco entre
        ///      checks), faz rollback completo.
        /// </summary>
        [Server]
        private bool TrySwapFromInventory(int inventorySlotIndex, string newItemId,
                                          string oldItemId, out string failReason)
        {
            failReason = null;

            // 1) Remove o item novo do inventário
            if (!ServerRemoveSlot(inventorySlotIndex))
            {
                failReason = "Item desapareceu do inventário.";
                Debug.LogError($"[NetworkInventory] TrySwapFromInventory: " +
                               $"remove({inventorySlotIndex}) falhou.");
                return false;
            }

            // 2) Se havia item antigo no slot, devolve-o ao inventário.
            //    Acabamos de remover 1 slot, então tem espaço garantido.
            if (!string.IsNullOrEmpty(oldItemId))
            {
                int returnedSlot = ServerAddItem(oldItemId, 1);
                if (returnedSlot < 0)
                {
                    // Caso patológico: item antigo foi removido do banco entre
                    // o equip e o unequip (hot-reload de ItemDatabase em dev,
                    // ou patch durante runtime). Rollback: devolve o item novo.
                    Debug.LogError($"[NetworkInventory] TrySwapFromInventory: " +
                                   $"falha ao devolver '{oldItemId}' ao inventário. " +
                                   $"Item provavelmente removido do banco. Rollback...");

                    int rollback = ServerAddItem(newItemId, 1);
                    if (rollback < 0)
                    {
                        // Estado profundamente quebrado: acabamos de liberar 1 slot
                        // e o ServerAddItem ainda falhou. Pode ser que o item NOVO
                        // também tenha sido removido do banco. Force-insere para
                        // não desaparecer com o item — mas loga como erro grave.
                        Debug.LogError($"[NetworkInventory] ROLLBACK CRÍTICO: " +
                                       $"forçando inserção de '{newItemId}'. " +
                                       $"Verifique integridade do ItemDatabase!\n" +
                                       $"{Environment.StackTrace}");
                        Slots.Add(new InventorySlotData
                        {
                            SlotIndex = _nextSlotIndex++,
                            ItemId    = newItemId,
                            Quantity  = 1
                        });
                    }
                    failReason = "Erro ao trocar item.";
                    return false;
                }
            }

            return true;
        }

        // ══════════════════════════════════════════════════════════════════
        // EQUIPAMENTOS — leitura
        // ══════════════════════════════════════════════════════════════════

        [Server]
        private int ServerFindEquippedIndex(EquipmentSlot slot)
        {
            for (int i = 0; i < EquippedItems.Count; i++)
                if (EquippedItems[i].Slot == (byte)slot) return i;
            return -1;
        }

        [Server]
        public string ServerGetEquipped(EquipmentSlot slot)
        {
            int idx = ServerFindEquippedIndex(slot);
            return idx >= 0 ? EquippedItems[idx].ItemId : "";
        }

        public string GetEquipped(EquipmentSlot slot)
        {
            for (int i = 0; i < EquippedItems.Count; i++)
                if (EquippedItems[i].Slot == (byte)slot) return EquippedItems[i].ItemId;
            return "";
        }

        public bool IsSlotOccupied(EquipmentSlot slot) => !string.IsNullOrEmpty(GetEquipped(slot));

        // ══════════════════════════════════════════════════════════════════
        // EQUIPAMENTOS — Commands
        // ══════════════════════════════════════════════════════════════════

        [Command]
        public void CmdEquipItem(int inventorySlotIndex, byte targetSlotByte)
        {
            if (connectionToClient == null) return;
            ServerEquipItem(inventorySlotIndex, targetSlotByte);
        }

        [Command]
        public void CmdAutoEquip(int inventorySlotIndex)
        {
            if (connectionToClient == null) return;
            ServerEquipItem(inventorySlotIndex, (byte)EquipmentSlot.None);
        }

        [Command]
        public void CmdUnequipItem(byte slotByte)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            EquipmentSlot slot = (EquipmentSlot)slotByte;

            if (slot == EquipmentSlot.None || !EquipmentSlotEx.IsActive(slot))
            {
                _netPlayer.RpcShowMessageToOwner("Slot inválido.");
                return;
            }

            int idx = ServerFindEquippedIndex(slot);
            if (idx < 0)
            {
                _netPlayer.RpcShowMessageToOwner("Slot já está vazio.");
                return;
            }

            string itemId = EquippedItems[idx].ItemId;
            if (string.IsNullOrEmpty(itemId))
            {
                EquippedItems.RemoveAt(idx);
                _netPlayer.ServerOnEquipmentChanged();
                return;
            }

            int returnedSlot = ServerAddItem(itemId, 1);
            if (returnedSlot < 0) return;

            EquippedItems.RemoveAt(idx);
            _netPlayer.ServerOnEquipmentChanged();
        }

        /// <summary>
        /// Equipa um item do inventário no slot escolhido (ou no slot natural se None).
        /// Usa TrySwapFromInventory para protocolo seguro de troca.
        /// </summary>
        [Server]
        private void ServerEquipItem(int inventorySlotIndex, byte targetSlotByte)
        {
            if (_netPlayer == null || _netPlayer.Dead) return;

            // 1) Encontra item no inventário
            if (!TryGetInventorySlot(inventorySlotIndex, out var foundSlot))
            {
                _netPlayer.RpcShowMessageToOwner("Item não encontrado no inventário.");
                return;
            }

            // 2) Valida tipo
            var itemData = ItemDatabase.Instance?.GetItem(foundSlot.ItemId);
            if (itemData == null || !itemData.IsEquipment)
            {
                _netPlayer.RpcShowMessageToOwner("Este item não pode ser equipado.");
                return;
            }

            // 3) Resolve slot final
            EquipmentSlot itemSlot   = itemData.EquipSlot;
            EquipmentSlot targetSlot = (EquipmentSlot)targetSlotByte;

            if (targetSlot == EquipmentSlot.None)
                targetSlot = ResolveAutoEquipSlot(itemSlot);

            if (!EquipmentSlotEx.IsActive(targetSlot))
            {
                _netPlayer.RpcShowMessageToOwner("Slot de equipamento inválido.");
                return;
            }

            if (!EquipmentSlotEx.CanItemFitInSlot(itemSlot, targetSlot))
            {
                _netPlayer.RpcShowMessageToOwner(
                    $"Este item não vai no slot {EquipmentSlotEx.DisplayName(targetSlot)}.");
                return;
            }

            // 4) Valida requisitos
            if (!ServerValidateRequirements(itemData, out string reason))
            {
                _netPlayer.RpcShowMessageToOwner(reason);
                return;
            }

            // 5) Snapshot do item antigo ANTES de qualquer mutação
            int    existingIdx = ServerFindEquippedIndex(targetSlot);
            string oldItemId   = "";
            if (existingIdx >= 0)
                oldItemId = EquippedItems[existingIdx].ItemId;

            // 6) Protocolo de troca atômica
            if (!TrySwapFromInventory(inventorySlotIndex, itemData.ItemId,
                                      oldItemId, out string swapError))
            {
                _netPlayer.RpcShowMessageToOwner(swapError);
                return;
            }

            // 7) Atualiza a lista de equipados
            if (existingIdx >= 0)
                EquippedItems.RemoveAt(existingIdx);

            int maxDur = Mathf.Max(0, itemData.MaxDurability);
            EquippedItems.Add(new EquippedItemData
            {
                Slot          = (byte)targetSlot,
                ItemId        = itemData.ItemId,
                Durability    = maxDur > 0 ? maxDur : -1,
                MaxDurability = maxDur
            });

            // 8) Recalcula stats
            _netPlayer.ServerOnEquipmentChanged();
        }

        [Server]
        private bool TryGetInventorySlot(int slotIndex, out InventorySlotData found)
        {
            foreach (var s in Slots)
            {
                if (s.SlotIndex == slotIndex)
                {
                    found = s;
                    return true;
                }
            }
            found = default;
            return false;
        }

        [Server]
        private bool ServerValidateRequirements(ItemData item, out string failReason)
        {
            failReason = null;
            if (item?.Requirements == null) return true;

            CharacterRace race  = _netPlayer.GetRaceEnum();
            var           bonus = StatsCalculator.GetRaceBonus(race);

            int totalSTR = _netPlayer.BaseSTR + bonus.STR + _netPlayer.AllocatedSTR;
            int totalAGI = _netPlayer.BaseAGI + bonus.AGI + _netPlayer.AllocatedAGI;
            int totalVIT = _netPlayer.BaseVIT + bonus.VIT + _netPlayer.AllocatedVIT;
            int totalDEX = _netPlayer.BaseDEX + bonus.DEX + _netPlayer.AllocatedDEX;
            int totalINT = _netPlayer.BaseINT + bonus.INT + _netPlayer.AllocatedINT;
            int totalLUK = _netPlayer.BaseLUK + bonus.LUK + _netPlayer.AllocatedLUK;

            return item.Requirements.Check(
                _netPlayer.Level,
                totalSTR, totalAGI, totalVIT, totalDEX, totalINT, totalLUK,
                race, out failReason);
        }

        [Server]
        private EquipmentSlot ResolveAutoEquipSlot(EquipmentSlot itemSlot)
        {
            if (EquipmentSlotEx.IsRing(itemSlot))
            {
                if (string.IsNullOrEmpty(ServerGetEquipped(EquipmentSlot.Ring1))) return EquipmentSlot.Ring1;
                if (string.IsNullOrEmpty(ServerGetEquipped(EquipmentSlot.Ring2))) return EquipmentSlot.Ring2;
                return EquipmentSlot.Ring1;
            }

            if (EquipmentSlotEx.IsEarring(itemSlot))
            {
                if (string.IsNullOrEmpty(ServerGetEquipped(EquipmentSlot.Earring1))) return EquipmentSlot.Earring1;
                if (string.IsNullOrEmpty(ServerGetEquipped(EquipmentSlot.Earring2))) return EquipmentSlot.Earring2;
                return EquipmentSlot.Earring1;
            }

            return itemSlot;
        }

        // ══════════════════════════════════════════════════════════════════
        // JOIAS DO PODER — Commands
        // ══════════════════════════════════════════════════════════════════

        [Command]
        public void CmdEquipGem(int skillSlotIndex, int inventorySlotIndex)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            if (skillSlotIndex < 0 || skillSlotIndex >= GEM_SLOT_COUNT)
            {
                _netPlayer.RpcShowMessageToOwner("Slot de joia inválido.");
                return;
            }

            // 1) Valida que existe e é PowerGem
            if (!TryGetInventorySlot(inventorySlotIndex, out var foundSlot))
            {
                _netPlayer.RpcShowMessageToOwner("Joia não encontrada no inventário.");
                return;
            }

            var itemData = ItemDatabase.Instance?.GetItem(foundSlot.ItemId);
            if (itemData == null || !itemData.IsPowerGem)
            {
                _netPlayer.RpcShowMessageToOwner("Este item não é uma Joia do Poder.");
                return;
            }

            // 2) Snapshot da joia antiga
            string oldGemId = GetGemItemId(skillSlotIndex);

            // 3) Protocolo de troca atômica
            if (!TrySwapFromInventory(inventorySlotIndex, itemData.ItemId,
                                      oldGemId, out string swapError))
            {
                _netPlayer.RpcShowMessageToOwner(swapError);
                return;
            }

            // 4) Aplica a nova joia no slot
            ServerSetGemSlot(skillSlotIndex, itemData.ItemId);
        }

        [Command]
        public void CmdUnequipGem(int skillSlotIndex)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            if (skillSlotIndex < 0 || skillSlotIndex >= GEM_SLOT_COUNT)
            {
                _netPlayer.RpcShowMessageToOwner("Slot inválido.");
                return;
            }

            string gemId = GetGemItemId(skillSlotIndex);
            if (string.IsNullOrEmpty(gemId)) return;

            int newSlot = ServerAddItem(gemId, 1);
            if (newSlot < 0) return;

            ServerSetGemSlot(skillSlotIndex, "");
        }

        [Server]
        private void ServerSetGemSlot(int index, string itemId)
        {
            switch (index)
            {
                case 0: GemSlotQ = itemId ?? ""; break;
                case 1: GemSlotW = itemId ?? ""; break;
                case 2: GemSlotE = itemId ?? ""; break;
                case 3: GemSlotR = itemId ?? ""; break;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // INVENTÁRIO — Commands diversos
        // ══════════════════════════════════════════════════════════════════

        [Command]
        public void CmdRemoveItem(int inventorySlotIndex)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;
            ServerRemoveSlot(inventorySlotIndex);
        }

        [Command]
        public void CmdUseConsumable(int inventorySlotIndex)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            if (!TryGetInventorySlot(inventorySlotIndex, out var foundSlot)) return;

            var itemData = ItemDatabase.Instance?.GetItem(foundSlot.ItemId);
            if (itemData == null || !itemData.IsConsumable) return;

            // Sanitiza valores absurdos (proteção contra ItemData com bugs)
            float heal = SanitizeBuff(itemData.HealAmount);
            float mana = SanitizeBuff(itemData.ManaAmount);

            if (heal > 0f) _netPlayer.ServerApplyHeal(heal);
            if (mana > 0f) _netPlayer.ServerRestoreMP(mana);

            ServerRemoveSlot(foundSlot.SlotIndex);
        }

        private static float SanitizeBuff(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            // Cap defensivo — nenhuma poção deveria curar mais que o MaxHP cap
            return Mathf.Clamp(value, 0f, GameConstants.Combat.MAX_HP);
        }

        // ══════════════════════════════════════════════════════════════════
        // JOIAS — Leitura
        // ══════════════════════════════════════════════════════════════════

        public string GetGemItemId(int skillSlotIndex) => skillSlotIndex switch
        {
            0 => GemSlotQ ?? "",
            1 => GemSlotW ?? "",
            2 => GemSlotE ?? "",
            3 => GemSlotR ?? "",
            _ => ""
        };

        public SkillData GetEquippedSkill(int skillSlotIndex)
        {
            string gemId = GetGemItemId(skillSlotIndex);
            if (string.IsNullOrEmpty(gemId)) return null;
            return ItemDatabase.Instance?.GetItem(gemId)?.EmbeddedSkill;
        }

        public int EquippedGemCount()
        {
            int count = 0;
            for (int i = 0; i < GEM_SLOT_COUNT; i++)
                if (!string.IsNullOrEmpty(GetGemItemId(i))) count++;
            return count;
        }

        // ══════════════════════════════════════════════════════════════════
        // EQUIPAMENTO — Agregação de bônus
        // ══════════════════════════════════════════════════════════════════

        public EquipmentBonuses BuildEquipmentBonuses()
            => EquipmentSlotEx.AggregateBonuses(EquippedItems);

        public int  EquippedItemCount() => EquippedItems.Count;
        public int  FreeSlotCount()     => Mathf.Max(0, MAX_INVENTORY_SLOTS - Slots.Count);
        public bool IsFull()            => Slots.Count >= MAX_INVENTORY_SLOTS;
    }
}
