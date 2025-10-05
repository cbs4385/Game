using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.Persistence;

namespace DataDrivenGoap.Items
{
    public readonly struct InventoryStackView
    {
        public ItemDefinition Item { get; }
        public int Quantity { get; }
        public int Quality { get; }

        public InventoryStackView(ItemDefinition item, int quantity, int quality)
        {
            Item = item;
            Quantity = quantity;
            Quality = quality;
        }
    }

    public sealed class InventoryDefinition
    {
        public int Slots { get; }
        public int StackSize { get; }

        public InventoryDefinition(int slots, int stackSize)
        {
            Slots = Math.Max(0, slots);
            StackSize = Math.Max(1, stackSize);
        }

        public static InventoryDefinition FromConfig(InventoryConfig cfg)
        {
            if (cfg == null)
                return new InventoryDefinition(0, 1);
            return new InventoryDefinition(cfg.slots, cfg.stackSize);
        }
    }

    internal sealed class InventorySlot
    {
        public ItemDefinition Item { get; private set; }
        public int Quantity { get; private set; }
        public int Quality { get; private set; }
        private readonly InventoryDefinition _definition;

        public InventorySlot(InventoryDefinition definition)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            Item = null;
            Quantity = 0;
            Quality = 0;
        }

        public bool IsEmpty => Item == null || Quantity <= 0;
        public int Capacity => Item == null ? _definition.StackSize : Math.Min(_definition.StackSize, Item.StackSize);

        public int Add(ItemDefinition item, int quantity, int quality)
        {
            if (item == null || quantity <= 0)
                return quantity;

            if (IsEmpty)
            {
                int toAdd = Math.Min(quantity, Math.Max(1, Math.Min(_definition.StackSize, item.StackSize)));
                Item = item;
                Quantity = toAdd;
                Quality = quality;
                return quantity - toAdd;
            }

            if (!ReferenceEquals(Item, item))
                return quantity;

            int cap = Capacity;
            int space = Math.Max(0, cap - Quantity);
            int added = Math.Min(quantity, space);
            Quantity += added;
            Quality = (Quality + quality) / 2;
            return quantity - added;
        }

        public int Remove(string predicate, ItemCatalog catalog, int desired)
        {
            if (IsEmpty || desired <= 0)
                return 0;
            if (!catalog.MatchesPredicate(Item, predicate))
                return 0;
            int removed = Math.Min(desired, Quantity);
            Quantity -= removed;
            if (Quantity <= 0)
            {
                Item = null;
                Quality = 0;
                Quantity = 0;
            }
            return removed;
        }

        public InventoryStackView ToView()
        {
            if (IsEmpty)
                return new InventoryStackView(null, 0, 0);
            return new InventoryStackView(Item, Quantity, Quality);
        }
    }

    public sealed class InventoryComponent
    {
        private readonly List<InventorySlot> _slots;
        private readonly ItemCatalog _catalog;
        private readonly ThingId _owner;

        public InventoryDefinition Definition { get; }
        public ThingId Owner => _owner;

        public InventoryComponent(ThingId owner, InventoryDefinition definition, ItemCatalog catalog)
        {
            _owner = owner;
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _slots = Enumerable.Range(0, definition.Slots).Select(_ => new InventorySlot(definition)).ToList();
        }

        public IReadOnlyList<InventoryStackView> GetStacks()
        {
            return _slots.Select(s => s.ToView()).Where(v => v.Item != null && v.Quantity > 0).ToList();
        }

        public int Count(string predicate)
        {
            if (string.IsNullOrWhiteSpace(predicate))
                return 0;
            int total = 0;
            foreach (var slot in _slots)
            {
                if (slot.IsEmpty)
                    continue;
                if (!_catalog.MatchesPredicate(slot.Item, predicate))
                    continue;
                total += slot.Quantity;
            }
            return total;
        }

        public bool Has(string predicate)
        {
            return Count(predicate) > 0;
        }

        public int TryAddItem(string itemId, int quantity, int quality = 0)
        {
            if (!_catalog.TryGet(itemId, out var item))
                return quantity;
            int remaining = quantity;
            foreach (var slot in _slots)
            {
                if (slot.IsEmpty)
                    continue;
                if (!ReferenceEquals(slot.Item, item))
                    continue;
                remaining = slot.Add(item, remaining, quality);
                if (remaining <= 0)
                    return 0;
            }

            foreach (var slot in _slots)
            {
                if (!slot.IsEmpty)
                    continue;
                remaining = slot.Add(item, remaining, quality);
                if (remaining <= 0)
                    return 0;
            }
            return remaining;
        }

        public int TryRemove(string predicate, int quantity)
        {
            if (string.IsNullOrWhiteSpace(predicate) || quantity <= 0)
                return 0;
            int removed = 0;
            foreach (var slot in _slots)
            {
                if (slot.IsEmpty)
                    continue;
                int toRemove = Math.Min(quantity - removed, slot.Quantity);
                int actual = slot.Remove(predicate, _catalog, toRemove);
                removed += actual;
                if (removed >= quantity)
                    break;
            }
            return removed;
        }

        public void Clear()
        {
            foreach (var slot in _slots)
            {
                slot.Remove(slot.Item?.Id, _catalog, int.MaxValue);
            }
        }

        internal InventoryOwnerState CaptureState(double currency)
        {
            var state = new InventoryOwnerState
            {
                ownerId = _owner.Value,
                slots = Definition.Slots,
                stackSize = Definition.StackSize,
                currency = currency
            };

            foreach (var slot in _slots)
            {
                var view = slot.ToView();
                if (view.Item == null || view.Quantity <= 0)
                    continue;
                state.stacks.Add(new InventoryStackState
                {
                    itemId = view.Item.Id,
                    quantity = view.Quantity,
                    quality = view.Quality
                });
            }

            return state;
        }

        internal void ApplyState(IEnumerable<InventoryStackState> stacks)
        {
            Clear();
            if (stacks == null)
                return;
            foreach (var stack in stacks)
            {
                if (stack == null || string.IsNullOrWhiteSpace(stack.itemId) || stack.quantity <= 0)
                    continue;
                TryAddItem(stack.itemId.Trim(), stack.quantity, stack.quality);
            }
        }
    }

    public sealed class InventorySystem : IInventoryQuery
    {
        private readonly Dictionary<ThingId, InventoryComponent> _inventories;
        private readonly Dictionary<ThingId, double> _wallets;
        private readonly ItemCatalog _catalog;
        private readonly object _gate = new object();

        public InventorySystem(ItemCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _inventories = new Dictionary<ThingId, InventoryComponent>();
            _wallets = new Dictionary<ThingId, double>();
        }

        public InventoryComponent ConfigureInventory(ThingId owner, InventoryConfig cfg)
        {
            var ownerId = owner.Value;
            if (string.IsNullOrWhiteSpace(ownerId))
                throw new InvalidDataException("Inventory owner must have a valid identifier before configuration.");

            ownerId = ownerId.Trim();

            var definition = InventoryDefinition.FromConfig(cfg);
            var component = new InventoryComponent(owner, definition, _catalog);
            lock (_gate)
            {
                _inventories[owner] = component;

                var startEntries = cfg?.start ?? Array.Empty<InventoryItemConfig>();
                for (int i = 0; i < startEntries.Length; i++)
                {
                    var entry = startEntries[i];
                    if (entry == null)
                        throw new InvalidDataException($"Inventory '{ownerId}' start entry at index {i} is null.");

                    var itemId = entry.id?.Trim();
                    if (string.IsNullOrWhiteSpace(itemId))
                        throw new InvalidDataException($"Inventory '{ownerId}' start entry at index {i} must declare an item id.");

                    if (entry.quantity <= 0)
                        throw new InvalidDataException($"Inventory '{ownerId}' start entry '{itemId}' must declare a positive quantity.");

                    int quality = entry.quality ?? 0;
                    if (quality < 0)
                        throw new InvalidDataException($"Inventory '{ownerId}' start entry '{itemId}' quality must be non-negative.");

                    int remainder = component.TryAddItem(itemId, entry.quantity, quality);
                    if (remainder != 0)
                        throw new InvalidDataException($"Inventory '{ownerId}' start entry '{itemId}' could not be stocked; remaining quantity: {remainder}.");
                }
            }

            return component;
        }

        public bool HasInventory(ThingId owner)
        {
            lock (_gate)
                return _inventories.ContainsKey(owner);
        }

        public InventoryComponent GetInventory(ThingId owner)
        {
            lock (_gate)
            {
                _inventories.TryGetValue(owner, out var inv);
                return inv;
            }
        }

        public bool Has(ThingId owner, string predicate)
        {
            return Count(owner, predicate) > 0;
        }

        public int Count(ThingId owner, string predicate)
        {
            lock (_gate)
            {
                if (!_inventories.TryGetValue(owner, out var inv))
                    return 0;
                return inv.Count(predicate);
            }
        }

        public IReadOnlyList<InventoryStackView> Snapshot(ThingId owner)
        {
            lock (_gate)
            {
                if (!_inventories.TryGetValue(owner, out var inv))
                    return Array.Empty<InventoryStackView>();
                return inv.GetStacks();
            }
        }

        public void SetCurrency(ThingId owner, double amount)
        {
            if (double.IsNaN(amount) || double.IsInfinity(amount))
                amount = 0.0;
            lock (_gate)
            {
                _wallets[owner] = amount;
            }
        }

        public double GetCurrency(ThingId owner)
        {
            lock (_gate)
            {
                return _wallets.TryGetValue(owner, out var value) ? value : 0.0;
            }
        }

        public double AdjustCurrency(ThingId owner, double delta)
        {
            lock (_gate)
            {
                double current = GetCurrency(owner);
                double next = current + delta;
                if (double.IsNaN(next) || double.IsInfinity(next))
                    next = current;
                next = Math.Max(0.0, next);
                _wallets[owner] = next;
                return next;
            }
        }

        public int AddItem(ThingId owner, string itemId, int quantity, int quality = 0)
        {
            if (quantity <= 0)
                return 0;
            lock (_gate)
            {
                if (!_inventories.TryGetValue(owner, out var inv))
                    return 0;
                int remainder = inv.TryAddItem(itemId, quantity, quality);
                return quantity - remainder;
            }
        }

        public int RemoveItem(ThingId owner, string predicate, int quantity)
        {
            if (quantity <= 0)
                return 0;
            lock (_gate)
            {
                if (!_inventories.TryGetValue(owner, out var inv))
                    return 0;
                return inv.TryRemove(predicate, quantity);
            }
        }

        public bool TryGetItemDefinition(string itemId, out ItemDefinition item)
        {
            return _catalog.TryGet(itemId, out item);
        }

        public InventorySystemState CaptureState()
        {
            var state = new InventorySystemState();
            lock (_gate)
            {
                foreach (var kv in _inventories)
                {
                    var owner = kv.Key;
                    var component = kv.Value;
                    double currency = _wallets.TryGetValue(owner, out var bal) ? bal : 0.0;
                    state.owners.Add(component.CaptureState(currency));
                }

                foreach (var kv in _wallets)
                {
                    if (_inventories.ContainsKey(kv.Key))
                        continue;
                    state.owners.Add(new InventoryOwnerState
                    {
                        ownerId = kv.Key.Value,
                        slots = 0,
                        stackSize = 1,
                        currency = kv.Value
                    });
                }
            }
            return state;
        }

        public void ApplyState(InventorySystemState state)
        {
            lock (_gate)
            {
                _inventories.Clear();
                _wallets.Clear();
                if (state?.owners == null)
                    return;

                foreach (var ownerState in state.owners)
                {
                    if (ownerState == null || string.IsNullOrWhiteSpace(ownerState.ownerId))
                        continue;
                    var owner = new ThingId(ownerState.ownerId.Trim());
                    var definition = new InventoryDefinition(ownerState.slots, ownerState.stackSize);
                    var component = new InventoryComponent(owner, definition, _catalog);
                    component.ApplyState(ownerState.stacks);
                    _inventories[owner] = component;
                    _wallets[owner] = double.IsFinite(ownerState.currency) ? ownerState.currency : 0.0;
                }
            }
        }
    }
}
