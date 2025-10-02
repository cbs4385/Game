using System;
using System.Collections.Generic;
using System.Globalization;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;
using DataDrivenGoap.Persistence;

namespace DataDrivenGoap.Items
{
    public sealed class ShopStockEntry
    {
        public ItemDefinition Item { get; }
        public int Quantity { get; private set; }
        public int MaxQuantity { get; }
        public string RestockRule { get; }
        public double? PriceOverride { get; }

        public ShopStockEntry(ItemDefinition item, int quantity, int maxQuantity, string restockRule, double? priceOverride)
        {
            Item = item ?? throw new ArgumentNullException(nameof(item));
            Quantity = Math.Max(0, quantity);
            MaxQuantity = Math.Max(0, maxQuantity);
            if (string.IsNullOrWhiteSpace(restockRule))
                throw new ArgumentException("Shop stock entry must provide a restock rule.", nameof(restockRule));
            RestockRule = restockRule.Trim();

            if (priceOverride.HasValue)
            {
                if (double.IsNaN(priceOverride.Value) || double.IsInfinity(priceOverride.Value) || priceOverride.Value <= 0.0)
                    throw new ArgumentOutOfRangeException(nameof(priceOverride), "Shop stock entry price overrides must be finite and greater than zero.");
            }

            PriceOverride = priceOverride;
        }

        public void SetQuantity(int qty)
        {
            Quantity = Math.Max(0, Math.Min(qty, MaxQuantity));
        }

        public int Consume(int amount)
        {
            if (amount <= 0)
                return 0;
            int take = Math.Max(0, Math.Min(amount, Quantity));
            Quantity -= take;
            return take;
        }

        public int Accept(int amount)
        {
            if (amount <= 0)
                return 0;
            int space = Math.Max(0, MaxQuantity - Quantity);
            int added = Math.Min(space, amount);
            Quantity += added;
            return added;
        }
    }

    public readonly struct ShopTransactionResult
    {
        public int Quantity { get; }
        public double UnitPrice { get; }
        public double TotalPrice { get; }

        public ShopTransactionResult(int quantity, double unitPrice)
        {
            Quantity = quantity;
            UnitPrice = unitPrice;
            TotalPrice = unitPrice * quantity;
        }
    }

    public sealed class ShopInstance
    {
        private readonly InventorySystem _inventorySystem;
        private readonly ThingId _owner;
        private readonly List<ShopStockEntry> _stock;
        private int _lastRestockDay;
        private double _restockHour;

        public ThingId Owner => _owner;
        public IReadOnlyList<ShopStockEntry> Stock => _stock;
        public double Markup { get; }
        public double Markdown { get; }

        public ShopInstance(ThingId owner, InventorySystem inventorySystem, ShopConfig config, ItemCatalog catalog)
        {
            _owner = owner;
            _inventorySystem = inventorySystem ?? throw new ArgumentNullException(nameof(inventorySystem));
            if (config == null)
                throw new ArgumentNullException(nameof(config), "Shop configuration must be provided.");
            if (catalog == null)
                throw new ArgumentNullException(nameof(catalog));
            if (!_inventorySystem.HasInventory(owner))
                throw new InvalidOperationException($"Inventory for shop owner '{owner.Value}' must be configured before creating the shop instance.");

            _stock = new List<ShopStockEntry>();
            _lastRestockDay = -1;
            _restockHour = ValidateRestockHour(config.restockHour);
            Markup = ValidateMultiplier(config.markup, nameof(ShopConfig.markup));
            Markdown = ValidateMultiplier(config.markdown, nameof(ShopConfig.markdown));

            var stockConfigs = config.stock ?? throw new ArgumentException("Shop configuration must declare its stock entries.", nameof(config));
            if (stockConfigs.Length == 0)
                throw new ArgumentException("Shop configuration must declare at least one stock entry.", nameof(config));

            for (int i = 0; i < stockConfigs.Length; i++)
            {
                var entry = stockConfigs[i] ?? throw new ArgumentException($"Shop configuration contains a null stock entry at index {i}.", nameof(config));
                if (string.IsNullOrWhiteSpace(entry.item))
                    throw new ArgumentException($"Shop stock entry at index {i} must specify an item id.", nameof(config));
                if (!catalog.TryGet(entry.item, out var item))
                    throw new ArgumentException($"Shop stock entry '{entry.item}' references an unknown item id.", nameof(config));
                string normalizedRestock = NormalizeRestockRule(entry.restock, entry.item);
                int qty = Math.Max(0, entry.quantity);
                var stockEntry = new ShopStockEntry(item, qty, Math.Max(qty, item.StackSize), normalizedRestock, entry.price);
                _stock.Add(stockEntry);
            }
        }

        public void Restock(WorldTimeSnapshot time)
        {
            if (time == null)
                return;
            int day = time.DayOfYear;
            if (day == _lastRestockDay)
            {
                if (!ShouldRestockOnHour(time))
                    return;
            }
            _lastRestockDay = day;

            foreach (var entry in _stock)
            {
                if (entry == null)
                    continue;
                bool shouldRestock = ShouldRestockEntry(entry, time);
                if (!shouldRestock)
                    continue;
                if (!_inventorySystem.HasInventory(_owner))
                    continue;
                var inv = _inventorySystem.GetInventory(_owner);
                if (inv == null)
                    continue;
                inv.TryRemove(entry.Item.Id, int.MaxValue);
                entry.SetQuantity(entry.MaxQuantity);
                int remainder = inv.TryAddItem(entry.Item.Id, entry.MaxQuantity);
                if (remainder > 0)
                    entry.SetQuantity(entry.MaxQuantity - remainder);
            }
        }

        private bool ShouldRestockEntry(ShopStockEntry entry, WorldTimeSnapshot time)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            string rule = entry.RestockRule.Trim().ToLowerInvariant();
            if (rule == "never")
                return false;
            if (rule == "hourly")
                return ShouldRestockOnHour(time);
            if (rule == "daily")
                return ShouldRestockOnHour(time);
            if (rule.StartsWith("every:", StringComparison.Ordinal))
            {
                var token = rule.Substring("every:".Length);
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0)
                {
                    return time.DayOfYear % days == 0 && ShouldRestockOnHour(time);
                }
            }
            throw new InvalidOperationException($"Unrecognised restock rule '{entry.RestockRule}' for item '{entry.Item?.Id}'.");
        }

        private bool ShouldRestockOnHour(WorldTimeSnapshot time)
        {
            if (time == null)
                throw new ArgumentNullException(nameof(time));
            double hour = time.TimeOfDay.TotalHours;
            if (double.IsNaN(hour) || double.IsInfinity(hour))
                throw new InvalidOperationException("World time snapshot has an invalid time of day value.");
            return hour >= _restockHour;
        }

        private static double ValidateRestockHour(double? restockHour)
        {
            if (!restockHour.HasValue)
                throw new ArgumentException("Shop configuration must define a restock hour.", nameof(ShopConfig.restockHour));
            double value = restockHour.Value;
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(nameof(ShopConfig.restockHour), "Shop restock hour must be a finite number.");
            if (value < 0.0 || value >= 24.0)
                throw new ArgumentOutOfRangeException(nameof(ShopConfig.restockHour), "Shop restock hour must be within [0, 24).");
            return value;
        }

        private static double ValidateMultiplier(double? value, string propertyName)
        {
            if (!value.HasValue)
                throw new ArgumentException($"Shop configuration must define a {propertyName} value.", propertyName);
            double multiplier = value.Value;
            if (double.IsNaN(multiplier) || double.IsInfinity(multiplier))
                throw new ArgumentOutOfRangeException(propertyName, $"Shop {propertyName} must be finite.");
            if (multiplier <= 0.0)
                throw new ArgumentOutOfRangeException(propertyName, $"Shop {propertyName} must be greater than zero.");
            return multiplier;
        }

        private static string NormalizeRestockRule(string restockRule, string itemId)
        {
            if (string.IsNullOrWhiteSpace(restockRule))
                throw new ArgumentException($"Shop stock entry '{itemId}' must declare a restock rule.", nameof(restockRule));

            string normalized = restockRule.Trim().ToLowerInvariant();
            if (normalized == "never" || normalized == "hourly" || normalized == "daily")
                return normalized;

            if (normalized.StartsWith("every:", StringComparison.Ordinal))
            {
                var token = normalized.Substring("every:".Length);
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) || days <= 0)
                    throw new ArgumentException($"Shop stock entry '{itemId}' declares an invalid restock interval '{restockRule}'.", nameof(restockRule));
                return $"every:{days}";
            }

            throw new ArgumentException($"Shop stock entry '{itemId}' declares an unknown restock rule '{restockRule}'.", nameof(restockRule));
        }

        private static double ResolveSalePrice(ShopStockEntry entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));
            if (entry.Item == null)
                throw new InvalidOperationException("Shop stock entry is missing its item definition.");

            double price = entry.PriceOverride ?? entry.Item.BuyPrice;
            if (price <= 0.0 && entry.Item.SellPrice > 0.0)
                price = entry.Item.SellPrice;

            if (double.IsNaN(price) || double.IsInfinity(price) || price <= 0.0)
                throw new InvalidOperationException($"No valid sale price is defined for item '{entry.Item.Id}'.");

            return price;
        }

        private static double ResolvePurchasePrice(ShopStockEntry entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));
            if (entry.Item == null)
                throw new InvalidOperationException("Shop stock entry is missing its item definition.");

            double price = entry.PriceOverride ?? entry.Item.SellPrice;
            if (price <= 0.0 && entry.Item.BuyPrice > 0.0)
                price = entry.Item.BuyPrice;

            if (double.IsNaN(price) || double.IsInfinity(price) || price <= 0.0)
                throw new InvalidOperationException($"No valid purchase price is defined for item '{entry.Item.Id}'.");

            return price;
        }

        private ShopStockEntry FindEntry(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                return null;
            foreach (var entry in _stock)
            {
                if (entry?.Item == null)
                    continue;
                if (string.Equals(entry.Item.Id, itemId, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
            return null;
        }

        public bool TrySellToCustomer(ThingId customer, string itemId, int quantity, out ShopTransactionResult result)
        {
            result = default;
            var entry = FindEntry(itemId);
            if (entry == null || entry.Item == null)
                return false;
            if (quantity <= 0)
                return false;

            double basePrice = ResolveSalePrice(entry);
            double unitPrice = basePrice * Markup;

            int toSell = entry.Consume(quantity);
            if (toSell <= 0)
                return false;

            int removed = _inventorySystem.RemoveItem(_owner, itemId, toSell);
            if (removed <= 0)
            {
                entry.SetQuantity(entry.Quantity + toSell);
                return false;
            }
            if (removed < toSell)
                entry.SetQuantity(entry.Quantity + (toSell - removed));

            int added = _inventorySystem.AddItem(customer, itemId, removed, entry.Item.Quality);
            if (added <= 0)
            {
                // Refund items back to the shop inventory
                _inventorySystem.AddItem(_owner, itemId, removed);
                entry.SetQuantity(entry.Quantity + removed);
                return false;
            }

            if (added < removed)
            {
                int refund = removed - added;
                _inventorySystem.AddItem(_owner, itemId, refund);
                entry.SetQuantity(entry.Quantity + refund);
            }

            result = new ShopTransactionResult(added, unitPrice);
            return true;
        }

        public bool TryBuyFromCustomer(ThingId customer, string itemId, int quantity, out ShopTransactionResult result)
        {
            result = default;
            var entry = FindEntry(itemId);
            if (entry == null || entry.Item == null)
                return false;
            if (quantity <= 0)
                return false;

            double basePrice = ResolvePurchasePrice(entry);
            double unitPrice = basePrice * Markdown;

            int capacity = Math.Max(0, entry.MaxQuantity - entry.Quantity);
            if (capacity <= 0)
                return false;

            int toBuy = Math.Min(quantity, capacity);
            int removed = _inventorySystem.RemoveItem(customer, itemId, toBuy);
            if (removed <= 0)
                return false;

            int accepted = _inventorySystem.AddItem(_owner, itemId, removed, entry.Item.Quality);
            if (accepted <= 0)
            {
                // Give back to the customer
                _inventorySystem.AddItem(customer, itemId, removed, entry.Item.Quality);
                return false;
            }

            if (accepted < removed)
            {
                int refund = removed - accepted;
                _inventorySystem.AddItem(customer, itemId, refund, entry.Item.Quality);
            }

            entry.Accept(accepted);
            result = new ShopTransactionResult(accepted, unitPrice);
            return true;
        }

        public ShopState CaptureState()
        {
            var state = new ShopState
            {
                ownerId = _owner.Value,
                markup = Markup,
                markdown = Markdown,
                lastRestockDay = _lastRestockDay,
                restockHour = _restockHour
            };

            foreach (var entry in _stock)
            {
                if (entry?.Item == null)
                    continue;
                state.stock.Add(new ShopStockState
                {
                    itemId = entry.Item.Id,
                    quantity = entry.Quantity,
                    maxQuantity = entry.MaxQuantity,
                    restockRule = entry.RestockRule,
                    priceOverride = entry.PriceOverride
                });
            }

            return state;
        }

        public void ApplyState(ShopState state)
        {
            if (state == null)
                return;
            _lastRestockDay = state.lastRestockDay;
            if (!double.IsNaN(state.restockHour) && !double.IsInfinity(state.restockHour))
                _restockHour = state.restockHour;

            if (state.stock == null)
                return;

            foreach (var stockState in state.stock)
            {
                if (stockState == null || string.IsNullOrWhiteSpace(stockState.itemId))
                    continue;
                var entry = FindEntry(stockState.itemId.Trim());
                if (entry == null)
                    continue;
                int qty = Math.Max(0, stockState.quantity);
                if (stockState.maxQuantity > 0)
                    qty = Math.Min(qty, stockState.maxQuantity);
                entry.SetQuantity(qty);
            }
        }
    }

    public sealed class ShopSystem
    {
        private readonly Dictionary<ThingId, ShopInstance> _shops;
        private readonly InventorySystem _inventorySystem;
        private readonly ItemCatalog _catalog;

        public ShopSystem(InventorySystem inventorySystem, ItemCatalog catalog)
        {
            _inventorySystem = inventorySystem ?? throw new ArgumentNullException(nameof(inventorySystem));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _shops = new Dictionary<ThingId, ShopInstance>();
        }

        public void RegisterShop(ThingId owner, ShopConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config), "Shop configuration must be provided.");

            EnsureInventory(owner, config);
            var shop = new ShopInstance(owner, _inventorySystem, config, _catalog);
            _shops[owner] = shop;
        }

        private void EnsureInventory(ThingId owner, ShopConfig config)
        {
            ValidateInventoryConfig(config.inventory);

            if (_inventorySystem.HasInventory(owner))
                return;

            _inventorySystem.ConfigureInventory(owner, config.inventory);
        }

        private void ValidateInventoryConfig(InventoryConfig inventory)
        {
            if (inventory == null)
                throw new ArgumentException("Shop configuration must provide an inventory definition.", nameof(ShopConfig.inventory));

            if (inventory.slots <= 0)
                throw new ArgumentOutOfRangeException(nameof(inventory.slots), "Shop inventory must declare a positive slot count.");

            if (inventory.stackSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(inventory.stackSize), "Shop inventory must declare a positive stack size.");

            var startEntries = inventory.start ?? Array.Empty<InventoryItemConfig>();
            for (int i = 0; i < startEntries.Length; i++)
            {
                var entry = startEntries[i] ?? throw new ArgumentException($"Inventory start entry at index {i} must not be null.", nameof(inventory.start));
                if (string.IsNullOrWhiteSpace(entry.id))
                    throw new ArgumentException($"Inventory start entry at index {i} must declare an item id.", nameof(inventory.start));
                if (entry.quantity <= 0)
                    throw new ArgumentOutOfRangeException(nameof(InventoryItemConfig.quantity), $"Inventory start entry '{entry.id}' must declare a positive quantity.");
                if (!_catalog.TryGet(entry.id, out _))
                    throw new ArgumentException($"Inventory start entry references unknown item id '{entry.id}'.", nameof(inventory.start));
                if (entry.quality.HasValue && entry.quality.Value < 0)
                    throw new ArgumentOutOfRangeException(nameof(InventoryItemConfig.quality), $"Inventory start entry '{entry.id}' quality must be non-negative when provided.");
            }
        }

        public ShopInstance GetShop(ThingId owner)
        {
            _shops.TryGetValue(owner, out var shop);
            return shop;
        }

        public void Tick(WorldTimeSnapshot time)
        {
            foreach (var shop in _shops.Values)
            {
                shop?.Restock(time);
            }
        }

        public bool TryProcessTransaction(ShopTransaction txn, out ShopTransactionResult result)
        {
            result = default;
            if (txn.Quantity <= 0 || string.IsNullOrWhiteSpace(txn.ItemId))
                return false;
            if (!_shops.TryGetValue(txn.Shop, out var shop) || shop == null)
                return false;

            return txn.Kind == ShopTransactionKind.Sale
                ? shop.TryBuyFromCustomer(txn.Actor, txn.ItemId, txn.Quantity, out result)
                : shop.TrySellToCustomer(txn.Actor, txn.ItemId, txn.Quantity, out result);
        }

        public ShopSystemState CaptureState()
        {
            var state = new ShopSystemState();
            foreach (var shop in _shops.Values)
            {
                var snapshot = shop?.CaptureState();
                if (snapshot != null)
                    state.shops.Add(snapshot);
            }
            return state;
        }

        public void ApplyState(ShopSystemState state)
        {
            if (state?.shops == null)
                return;
            foreach (var shopState in state.shops)
            {
                if (shopState == null || string.IsNullOrWhiteSpace(shopState.ownerId))
                    continue;
                var owner = new ThingId(shopState.ownerId.Trim());
                if (_shops.TryGetValue(owner, out var shop))
                    shop.ApplyState(shopState);
            }
        }
    }
}
