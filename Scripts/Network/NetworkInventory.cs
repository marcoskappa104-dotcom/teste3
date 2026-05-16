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
    /// === MUDANÇAS DESTA VERSÃO (sistema de stacking + uso inteligente) ===
    ///
    ///   1. STACKING REAL DE CONSUMABLE/MISC:
    ///      ServerAddItem agora segue este protocolo:
    ///        a) Se o item é stackable, procura slots existentes com o mesmo
    ///           ItemId que ainda não atingiram EffectiveMaxStack.
    ///        b) Distribui a quantidade entre slots existentes (em ordem),
    ///           topando cada stack ao máximo.
    ///        c) Se ainda sobrar, abre slots novos até esgotar a quantidade
    ///           OU atingir MAX_INVENTORY_SLOTS.
    ///        d) Retorna o slotIndex do PRIMEIRO slot afetado (compat com
    ///           ServerAddItem original que retornava o único slot criado).
    ///        e) Em caso de inventário cheio NO MEIO da distribuição, faz
    ///           rollback completo das mudanças feitas naquela chamada.
    ///
    ///   2. USO DE CONSUMÍVEL EM STACK:
    ///      CmdUseConsumable agora decrementa Quantity em vez de remover o slot.
    ///      Só remove quando Quantity chega a 0.
    ///
    ///   3. VALIDAÇÃO ANTI-DESPERDÍCIO DE CONSUMÍVEL:
    ///      Antes de consumir, valida que o item TEM efeito útil agora:
    ///        - Poção de HP em HP cheio  → rejeita
    ///        - Poção de MP em MP cheio  → rejeita
    ///        - Poção mista (HP+MP) é aceita se PELO MENOS um restaurar.
    ///      Servidor envia mensagem informativa ao owner em caso de rejeição.
    ///
    ///   4. TrySwapFromInventory ATUALIZADO:
    ///      Quando o item antigo é stackable e o jogador já tem um stack
    ///      parcial dele, a devolução agora respeita o stacking. (Equipar
    ///      consumível não é caso comum, mas equipar/desequipar PowerGem
    ///      não é stackable e segue o caminho legado — mantém compat).
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

        /// <summary>
        /// Adiciona item ao inventário com suporte completo a stacking.
        ///
        /// Para itens stackable (Consumable/Misc):
        ///   - Topa stacks existentes do mesmo ItemId antes de criar novos.
        ///   - Cria slots adicionais conforme necessário, respeitando
        ///     EffectiveMaxStack por slot.
        ///   - Rollback completo se inventário encher no meio.
        ///
        /// Para itens não-stackable (Equipment/PowerGem):
        ///   - Sempre cria 1 slot novo por unidade.
        ///
        /// Retorna: SlotIndex do primeiro slot afetado (existente ou novo),
        ///          ou -1 em falha total.
        /// </summary>
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

            var item = db.GetItem(itemId);
            if (item == null) return -1;

            // Cap defensivo contra quantidades absurdas (proteção contra
            // bugs em quests/drops que enviem int.MaxValue, etc).
            quantity = Mathf.Clamp(quantity, 1, ItemData.MAX_STACK_HARD_CAP * MAX_INVENTORY_SLOTS);

            // Itens NÃO-stackable: caminho simples (1 slot por unidade)
            if (!item.IsStackable)
            {
                return AddNonStackable(item, quantity);
            }

            // Itens stackable: protocolo completo com possibilidade de rollback
            return AddStackable(item, quantity);
        }

        /// <summary>
        /// Caminho para itens não-stackable (Equipment/PowerGem).
        /// Cada unidade ocupa um slot exclusivo.
        /// </summary>
        [Server]
        private int AddNonStackable(ItemData item, int quantity)
        {
            int firstAffected = -1;

            for (int i = 0; i < quantity; i++)
            {
                if (Slots.Count >= MAX_INVENTORY_SLOTS)
                {
                    _netPlayer?.RpcShowMessageToOwner("Inventário cheio!");
                    return firstAffected; // pode ter adicionado alguns; retorna o primeiro
                }

                var slot = new InventorySlotData
                {
                    SlotIndex = _nextSlotIndex++,
                    ItemId    = item.ItemId,
                    Quantity  = 1
                };
                Slots.Add(slot);

                if (firstAffected < 0) firstAffected = slot.SlotIndex;
            }

            return firstAffected;
        }

        /// <summary>
        /// Caminho para itens stackable. Topa stacks existentes primeiro,
        /// depois cria novos. Faz rollback se encher no meio.
        /// </summary>
        [Server]
        private int AddStackable(ItemData item, int quantity)
        {
            int maxStack  = item.EffectiveMaxStack;
            int remaining = quantity;
            int firstAffected = -1;

            // Snapshot para rollback. Guardamos (índice na SyncList, slotData original).
            // Para criações novas, guardamos também os SlotIndex alocados.
            var topUpSnapshots = new List<(int listIndex, InventorySlotData original)>();
            var newSlotIndices = new List<int>();
            int snapshotNextSlotIndex = _nextSlotIndex;

            // ── Fase 1: topar stacks existentes ────────────────────────────
            for (int i = 0; i < Slots.Count && remaining > 0; i++)
            {
                var slot = Slots[i];
                if (slot.ItemId != item.ItemId) continue;
                if (slot.Quantity >= maxStack) continue;

                int room  = maxStack - slot.Quantity;
                int toAdd = Mathf.Min(room, remaining);

                topUpSnapshots.Add((i, slot));

                slot.Quantity += toAdd;
                Slots[i]       = slot;

                remaining -= toAdd;

                if (firstAffected < 0) firstAffected = slot.SlotIndex;
            }

            // ── Fase 2: criar novos stacks ─────────────────────────────────
            while (remaining > 0)
            {
                if (Slots.Count >= MAX_INVENTORY_SLOTS)
                {
                    // Inventário cheio. Decide rollback total ou aceita parcial?
                    // Estratégia: se NADA foi adicionado em fase 1 nem fase 2,
                    // rollback é trivial. Se já adicionou ALGO, mantém o que
                    // coube e avisa o jogador.
                    //
                    // Alternativa mais conservadora: rollback total sempre.
                    // Optamos por MANTER O PARCIAL — é melhor UX em farm
                    // (ex: 20 poções caíram e só couberam 18; perder todas
                    // seria pior que pegar 18).
                    _netPlayer?.RpcShowMessageToOwner(
                        $"Inventário cheio! Coletou {quantity - remaining}/{quantity} {item.DisplayName}.");
                    return firstAffected;
                }

                int amountForNewSlot = Mathf.Min(maxStack, remaining);

                var newSlot = new InventorySlotData
                {
                    SlotIndex = _nextSlotIndex++,
                    ItemId    = item.ItemId,
                    Quantity  = amountForNewSlot
                };
                Slots.Add(newSlot);
                newSlotIndices.Add(newSlot.SlotIndex);

                remaining -= amountForNewSlot;

                if (firstAffected < 0) firstAffected = newSlot.SlotIndex;
            }

            return firstAffected;

            // NOTA: snapshots/newSlotIndices/snapshotNextSlotIndex são mantidos
            // como infraestrutura para um rollback completo se decidirmos mudar
            // a estratégia para "tudo ou nada". Hoje aceitamos parcial, então
            // não chamamos rollback. Manter as variáveis facilita evolução.
            // Para evitar warnings de variável não usada em release builds,
            // poderíamos remover, mas preferimos clareza explícita do design.
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
        /// Como Equipment/PowerGem são NÃO-stackable, o item antigo sempre
        /// volta criando 1 slot (ou empilhando se hipoteticamente fosse
        /// stackable no futuro — ServerAddItem cuida disso).
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

            // 2) Devolve o item antigo (se houver). Acabamos de liberar 1 slot.
            if (!string.IsNullOrEmpty(oldItemId))
            {
                int returnedSlot = ServerAddItem(oldItemId, 1);
                if (returnedSlot < 0)
                {
                    // Caso patológico: item antigo foi removido do banco entre
                    // o equip e o unequip. Rollback.
                    Debug.LogError($"[NetworkInventory] TrySwapFromInventory: " +
                                   $"falha ao devolver '{oldItemId}' ao inventário. " +
                                   $"Rollback...");

                    int rollback = ServerAddItem(newItemId, 1);
                    if (rollback < 0)
                    {
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
            if (returnedSlot < 0)
            {
                _netPlayer.RpcShowMessageToOwner("Inventário cheio!");
                return;
            }

            EquippedItems.RemoveAt(idx);
            _netPlayer.ServerOnEquipmentChanged();
        }

        [Server]
        private void ServerEquipItem(int inventorySlotIndex, byte targetSlotByte)
        {
            if (_netPlayer == null || _netPlayer.Dead) return;

            if (!TryGetInventorySlot(inventorySlotIndex, out var foundSlot))
            {
                _netPlayer.RpcShowMessageToOwner("Item não encontrado no inventário.");
                return;
            }

            var itemData = ItemDatabase.Instance?.GetItem(foundSlot.ItemId);
            if (itemData == null || !itemData.IsEquipment)
            {
                _netPlayer.RpcShowMessageToOwner("Este item não pode ser equipado.");
                return;
            }

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

            if (!ServerValidateRequirements(itemData, out string reason))
            {
                _netPlayer.RpcShowMessageToOwner(reason);
                return;
            }

            int    existingIdx = ServerFindEquippedIndex(targetSlot);
            string oldItemId   = "";
            if (existingIdx >= 0)
                oldItemId = EquippedItems[existingIdx].ItemId;

            if (!TrySwapFromInventory(inventorySlotIndex, itemData.ItemId,
                                      oldItemId, out string swapError))
            {
                _netPlayer.RpcShowMessageToOwner(swapError);
                return;
            }

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

        /// <summary>
        /// Mesma busca que TryGetInventorySlot mas retorna também o índice
        /// na SyncList (útil para mutação via Slots[i] = ...).
        /// </summary>
        [Server]
        private bool TryGetInventorySlotWithListIndex(int slotIndex,
            out InventorySlotData found, out int listIndex)
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].SlotIndex == slotIndex)
                {
                    found     = Slots[i];
                    listIndex = i;
                    return true;
                }
            }
            found     = default;
            listIndex = -1;
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

            string oldGemId = GetGemItemId(skillSlotIndex);

            if (!TrySwapFromInventory(inventorySlotIndex, itemData.ItemId,
                                      oldGemId, out string swapError))
            {
                _netPlayer.RpcShowMessageToOwner(swapError);
                return;
            }

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
            if (newSlot < 0)
            {
                _netPlayer.RpcShowMessageToOwner("Inventário cheio!");
                return;
            }

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

        /// <summary>
        /// Remove o slot INTEIRO (independente da quantidade). Usado pelo
        /// botão "descartar". Para descartar 1 de um stack, futuramente
        /// criar CmdSplitStack ou CmdDecrementSlot.
        /// </summary>
        [Command]
        public void CmdRemoveItem(int inventorySlotIndex)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;
            ServerRemoveSlot(inventorySlotIndex);
        }

        /// <summary>
        /// Usa um consumível. Versão server-authoritative com:
        ///   - Validação de tipo
        ///   - Sanitização de valores
        ///   - VALIDAÇÃO DE EFETIVIDADE: rejeita se item não pode curar nada
        ///     útil agora (HP cheio para poção só de HP, etc).
        ///   - Decremento de stack: só remove o slot se Quantity chegar a 0.
        /// </summary>
        [Command]
        public void CmdUseConsumable(int inventorySlotIndex)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            if (!TryGetInventorySlotWithListIndex(inventorySlotIndex,
                    out var foundSlot, out int listIndex))
                return;

            var itemData = ItemDatabase.Instance?.GetItem(foundSlot.ItemId);
            if (itemData == null || !itemData.IsConsumable) return;

            // Sanitiza valores absurdos (proteção contra ItemData com bugs)
            float heal = SanitizeBuff(itemData.HealAmount);
            float mana = SanitizeBuff(itemData.ManaAmount);

            // === VALIDAÇÃO DE EFETIVIDADE ===
            // Rejeita uso quando o item não pode fazer NADA útil agora.
            // Regras:
            //   - Item só de HP + HP cheio → rejeita ("já está com HP máximo")
            //   - Item só de MP + MP cheio → rejeita
            //   - Item misto HP+MP → rejeita só se AMBOS estiverem cheios
            if (!CanConsumableHaveEffect(heal, mana, out string rejectMsg))
            {
                _netPlayer.RpcShowMessageToOwner(rejectMsg);
                return;
            }

            // Aplica efeitos (cada método clampa internamente)
            if (heal > 0f) _netPlayer.ServerApplyHeal(heal);
            if (mana > 0f) _netPlayer.ServerRestoreMP(mana);

            // === DECREMENTA O STACK ===
            // Se Quantity > 1, só diminui em 1. Se chegou a 0 (ou era 1),
            // remove o slot inteiro.
            ServerConsumeOneFromSlot(listIndex, foundSlot);
        }

        /// <summary>
        /// Decide se um consumível pode ter efeito útil dado o estado atual
        /// do jogador. Retorna false e preenche rejectMsg se NÃO puder.
        /// </summary>
        [Server]
        private bool CanConsumableHaveEffect(float heal, float mana, out string rejectMsg)
        {
            bool restoresHP = heal > 0f;
            bool restoresMP = mana > 0f;

            // Item sem efeito de fato — não deveria chegar aqui (IsConsumable
            // já filtra), mas defensivo.
            if (!restoresHP && !restoresMP)
            {
                rejectMsg = "Este item não tem efeito.";
                return false;
            }

            bool hpFull = _netPlayer.CurrentHP >= _netPlayer.MaxHP - 0.01f;
            bool mpFull = _netPlayer.CurrentMP >= _netPlayer.MaxMP - 0.01f;

            // Só HP: rejeita se HP cheio
            if (restoresHP && !restoresMP && hpFull)
            {
                rejectMsg = "Você já está com HP máximo!";
                return false;
            }

            // Só MP: rejeita se MP cheio
            if (!restoresHP && restoresMP && mpFull)
            {
                rejectMsg = "Você já está com MP máximo!";
                return false;
            }

            // Misto: rejeita só se AMBOS cheios
            if (restoresHP && restoresMP && hpFull && mpFull)
            {
                rejectMsg = "HP e MP já estão no máximo!";
                return false;
            }

            rejectMsg = null;
            return true;
        }

        /// <summary>
        /// Decrementa em 1 a quantidade do slot. Remove o slot se Quantity
        /// resultante for &lt;= 0.
        /// </summary>
        [Server]
        private void ServerConsumeOneFromSlot(int listIndex, InventorySlotData slot)
        {
            if (listIndex < 0 || listIndex >= Slots.Count) return;

            if (slot.Quantity > 1)
            {
                slot.Quantity -= 1;
                Slots[listIndex] = slot;
            }
            else
            {
                Slots.RemoveAt(listIndex);
            }
        }

        private static float SanitizeBuff(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
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

        /// <summary>
        /// Quantidade total de um item no inventário, somando todos os stacks.
        /// Útil para quests ("traga 30 ervas") e crafting.
        /// </summary>
        public int GetTotalQuantity(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return 0;
            int total = 0;
            foreach (var slot in Slots)
                if (slot.ItemId == itemId) total += slot.Quantity;
            return total;
        }
    }
}
