using System;
using System.Collections.Generic;
using System.Linq;
using DataDrivenGoap.Config;

namespace DataDrivenGoap.Items
{
    public sealed class ItemDefinition
    {
        private readonly HashSet<string> _tags;
        private readonly HashSet<string> _toolFlags;
        private readonly HashSet<string> _giftAffinities;

        public string Id { get; }
        public IReadOnlyCollection<string> Tags => _tags;
        public int StackSize { get; }
        public int Quality { get; }
        public double BuyPrice { get; }
        public double SellPrice { get; }
        public IReadOnlyCollection<string> ToolFlags => _toolFlags;
        public IReadOnlyCollection<string> GiftAffinities => _giftAffinities;
        public IReadOnlyDictionary<string, string> Effects { get; }
        public string SpriteSlug { get; }

        public ItemDefinition(
            string id,
            IEnumerable<string> tags,
            int stackSize,
            int quality,
            double buyPrice,
            double sellPrice,
            IEnumerable<string> toolFlags,
            IEnumerable<string> giftAffinities,
            IReadOnlyDictionary<string, string> effects,
            string spriteSlug)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Item id must be provided", nameof(id));
            Id = id.Trim();

            if (stackSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(stackSize), $"Item '{Id}' must declare a positive stack size.");
            StackSize = stackSize;

            Quality = quality;

            if (double.IsNaN(buyPrice) || double.IsInfinity(buyPrice) || buyPrice < 0)
                throw new ArgumentOutOfRangeException(nameof(buyPrice), $"Item '{Id}' must declare a finite, non-negative buy price.");
            BuyPrice = buyPrice;

            if (double.IsNaN(sellPrice) || double.IsInfinity(sellPrice) || sellPrice < 0)
                throw new ArgumentOutOfRangeException(nameof(sellPrice), $"Item '{Id}' must declare a finite, non-negative sell price.");
            SellPrice = sellPrice;

            if (tags == null)
                throw new ArgumentNullException(nameof(tags), $"Item '{Id}' must declare its tags.");
            _tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag))
                    throw new ArgumentException($"Item '{Id}' contains an invalid tag entry.", nameof(tags));
                _tags.Add(NormalizeTag(tag));
            }

            _toolFlags = new HashSet<string>((toolFlags ?? Array.Empty<string>()).Select(NormalizeTag), StringComparer.OrdinalIgnoreCase);
            _giftAffinities = new HashSet<string>((giftAffinities ?? Array.Empty<string>()).Select(NormalizeTag), StringComparer.OrdinalIgnoreCase);

            if (effects == null)
                throw new ArgumentNullException(nameof(effects), $"Item '{Id}' must declare its effects.");
            var effectMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in effects)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key))
                    throw new ArgumentException($"Item '{Id}' contains an effect with no type.", nameof(effects));
                if (kvp.Value == null)
                    throw new ArgumentException($"Item '{Id}' contains an effect with no value.", nameof(effects));
                effectMap.Add(kvp.Key.Trim(), kvp.Value);
            }
            Effects = effectMap;

            SpriteSlug = string.IsNullOrWhiteSpace(spriteSlug) ? null : spriteSlug.Trim();
        }

        public bool HasTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return false;
            return _tags.Contains(NormalizeTag(tag));
        }

        public bool HasToolFlag(string flag)
        {
            if (string.IsNullOrWhiteSpace(flag))
                return false;
            return _toolFlags.Contains(NormalizeTag(flag));
        }

        public bool MatchesPredicate(string predicate)
        {
            if (string.IsNullOrWhiteSpace(predicate))
                return false;
            predicate = predicate.Trim();
            if (predicate.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
                return HasTag(predicate.Substring(4));
            if (predicate.StartsWith("tool:", StringComparison.OrdinalIgnoreCase))
                return HasToolFlag(predicate.Substring(5));
            if (predicate.StartsWith("gift:", StringComparison.OrdinalIgnoreCase))
                return _giftAffinities.Contains(NormalizeTag(predicate.Substring(5)));
            if (predicate.Contains(':'))
            {
                var parts = predicate.Split(new[] { ':' }, 2);
                if (parts.Length == 2)
                    return HasTag(parts[1]);
            }
            return string.Equals(Id, predicate, StringComparison.OrdinalIgnoreCase) || HasTag(predicate);
        }

        private static string NormalizeTag(string tag)
        {
            return (tag ?? string.Empty).Trim().ToLowerInvariant();
        }
    }

    public sealed class RecipeDefinition
    {
        public string Id { get; }
        public IReadOnlyDictionary<string, int> Inputs { get; }
        public IReadOnlyDictionary<string, int> Outputs { get; }
        public IReadOnlyCollection<string> Stations { get; }
        public IReadOnlyCollection<RecipeSkillGate> Gates { get; }
        public double TimeSeconds { get; }

        public RecipeDefinition(
            string id,
            IReadOnlyDictionary<string, int> inputs,
            IReadOnlyDictionary<string, int> outputs,
            IEnumerable<string> stations,
            IEnumerable<RecipeSkillGate> gates,
            double timeSeconds)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Recipe id must be provided", nameof(id));
            Id = id.Trim();

            if (inputs == null || inputs.Count == 0)
                throw new ArgumentException($"Recipe '{Id}' must declare at least one input.", nameof(inputs));
            var inputMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in inputs)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key))
                    throw new ArgumentException($"Recipe '{Id}' contains an input with no item id.", nameof(inputs));
                if (kvp.Value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(inputs), $"Recipe '{Id}' input '{kvp.Key}' must have a positive quantity.");
                if (!inputMap.TryAdd(kvp.Key.Trim(), kvp.Value))
                    throw new ArgumentException($"Recipe '{Id}' defines duplicate input for '{kvp.Key}'.", nameof(inputs));
            }
            Inputs = inputMap;

            if (outputs == null || outputs.Count == 0)
                throw new ArgumentException($"Recipe '{Id}' must declare at least one output.", nameof(outputs));
            var outputMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in outputs)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key))
                    throw new ArgumentException($"Recipe '{Id}' contains an output with no item id.", nameof(outputs));
                if (kvp.Value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(outputs), $"Recipe '{Id}' output '{kvp.Key}' must have a positive quantity.");
                if (!outputMap.TryAdd(kvp.Key.Trim(), kvp.Value))
                    throw new ArgumentException($"Recipe '{Id}' defines duplicate output for '{kvp.Key}'.", nameof(outputs));
            }
            Outputs = outputMap;

            Stations = stations?.Select(s => (s ?? string.Empty).Trim()).Where(s => s.Length > 0).ToArray() ?? Array.Empty<string>();

            if (gates == null)
                throw new ArgumentNullException(nameof(gates), $"Recipe '{Id}' must provide its gates (use an empty collection when none are required).");
            Gates = gates.ToArray();

            if (double.IsNaN(timeSeconds) || double.IsInfinity(timeSeconds) || timeSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(timeSeconds), $"Recipe '{Id}' must declare a finite, non-negative duration.");
            TimeSeconds = timeSeconds;
        }
    }

    public readonly struct RecipeSkillGate
    {
        public string Skill { get; }
        public int Level { get; }

        public RecipeSkillGate(string skill, int level)
        {
            if (string.IsNullOrWhiteSpace(skill))
                throw new ArgumentException("Recipe skill gate must declare a skill.", nameof(skill));
            if (level < 0)
                throw new ArgumentOutOfRangeException(nameof(level), "Recipe skill gate level must be non-negative.");
            Skill = skill.Trim();
            Level = level;
        }
    }

    public sealed class ItemCatalog
    {
        private readonly Dictionary<string, ItemDefinition> _items;
        private readonly List<RecipeDefinition> _recipes;
        private readonly Dictionary<string, RecipeDefinition> _recipesById;

        public IReadOnlyDictionary<string, ItemDefinition> Items => _items;
        public IReadOnlyList<RecipeDefinition> Recipes => _recipes;

        public ItemCatalog(IEnumerable<ItemConfig> items, IEnumerable<RecipeConfig> recipes)
        {
            _items = new Dictionary<string, ItemDefinition>(StringComparer.OrdinalIgnoreCase);
            if (items != null)
            {
                foreach (var cfg in items)
                {
                    if (cfg == null)
                        throw new InvalidOperationException("Encountered a null item configuration entry.");
                    if (string.IsNullOrWhiteSpace(cfg.id))
                        throw new InvalidOperationException("Item configuration entry is missing an id.");

                    if (cfg.tags == null)
                        throw new InvalidOperationException($"Item '{cfg.id}' must declare its tags (provide an empty array if none).");

                    if (cfg.effects == null)
                        throw new InvalidOperationException($"Item '{cfg.id}' must declare its effects (provide an empty array if none).");

                    if (cfg.price == null)
                        throw new InvalidOperationException($"Item '{cfg.id}' must declare pricing information.");

                    if (!cfg.price.buy.HasValue)
                        throw new InvalidOperationException($"Item '{cfg.id}' must declare a buy price.");

                    if (!cfg.price.sell.HasValue)
                        throw new InvalidOperationException($"Item '{cfg.id}' must declare a sell price.");

                    var effects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var effect in cfg.effects)
                    {
                        if (effect == null)
                            throw new InvalidOperationException($"Item '{cfg.id}' contains a null effect entry.");
                        if (string.IsNullOrWhiteSpace(effect.type))
                            throw new InvalidOperationException($"Item '{cfg.id}' contains an effect with no type.");
                        if (effect.value == null)
                            throw new InvalidOperationException($"Item '{cfg.id}' effect '{effect.type}' is missing a value.");
                        if (!effects.TryAdd(effect.type.Trim(), effect.value))
                            throw new InvalidOperationException($"Item '{cfg.id}' defines duplicate effect '{effect.type}'.");
                    }

                    var def = new ItemDefinition(
                        cfg.id,
                        cfg.tags,
                        cfg.stackSize,
                        cfg.quality,
                        cfg.price.buy.Value,
                        cfg.price.sell.Value,
                        cfg.tools ?? Array.Empty<string>(),
                        cfg.gifts ?? Array.Empty<string>(),
                        effects,
                        cfg.spriteSlug);

                    if (!_items.TryAdd(def.Id, def))
                        throw new InvalidOperationException($"Duplicate item id '{def.Id}' detected in configuration.");
                }
            }

            _recipes = new List<RecipeDefinition>();
            _recipesById = new Dictionary<string, RecipeDefinition>(StringComparer.OrdinalIgnoreCase);
            if (recipes != null)
            {
                foreach (var cfg in recipes)
                {
                    if (cfg == null)
                        throw new InvalidOperationException("Encountered a null recipe configuration entry.");
                    if (string.IsNullOrWhiteSpace(cfg.id))
                        throw new InvalidOperationException("Recipe configuration entry is missing an id.");

                    if (cfg.inputs == null || cfg.inputs.Count == 0)
                        throw new InvalidOperationException($"Recipe '{cfg.id}' must declare at least one input item.");

                    var inputs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in cfg.inputs)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key))
                            throw new InvalidOperationException($"Recipe '{cfg.id}' has an input with an empty item id.");
                        if (kv.Value <= 0)
                            throw new InvalidOperationException($"Recipe '{cfg.id}' input '{kv.Key}' must be a positive quantity.");
                        if (!inputs.TryAdd(kv.Key.Trim(), kv.Value))
                            throw new InvalidOperationException($"Recipe '{cfg.id}' defines duplicate input for '{kv.Key}'.");
                    }

                    if (cfg.outputs == null || cfg.outputs.Count == 0)
                        throw new InvalidOperationException($"Recipe '{cfg.id}' must declare at least one output item.");

                    var outputs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in cfg.outputs)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key))
                            throw new InvalidOperationException($"Recipe '{cfg.id}' has an output with an empty item id.");
                        if (kv.Value <= 0)
                            throw new InvalidOperationException($"Recipe '{cfg.id}' output '{kv.Key}' must be a positive quantity.");
                        if (!outputs.TryAdd(kv.Key.Trim(), kv.Value))
                            throw new InvalidOperationException($"Recipe '{cfg.id}' defines duplicate output for '{kv.Key}'.");
                    }

                    if (cfg.gates == null)
                        throw new InvalidOperationException($"Recipe '{cfg.id}' must declare its gates (use an empty array if none).");

                    var gates = new List<RecipeSkillGate>();
                    foreach (var gateCfg in cfg.gates)
                    {
                        if (gateCfg == null)
                            throw new InvalidOperationException($"Recipe '{cfg.id}' contains a null gate entry.");
                        gates.Add(new RecipeSkillGate(gateCfg.skill, gateCfg.level));
                    }

                    if (double.IsNaN(cfg.time) || double.IsInfinity(cfg.time))
                        throw new InvalidOperationException($"Recipe '{cfg.id}' must declare a finite crafting time.");

                    var recipe = new RecipeDefinition(cfg.id, inputs, outputs, cfg.stations, gates, cfg.time);
                    _recipes.Add(recipe);
                    if (!_recipesById.TryAdd(recipe.Id, recipe))
                        throw new InvalidOperationException($"Duplicate recipe id '{recipe.Id}' detected in configuration.");
                }
            }
        }

        public bool TryGetRecipe(string recipeId, out RecipeDefinition recipe)
        {
            if (string.IsNullOrWhiteSpace(recipeId))
            {
                recipe = null;
                return false;
            }

            return _recipesById.TryGetValue(recipeId.Trim(), out recipe);
        }

        public bool TryGet(string itemId, out ItemDefinition item)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                item = null;
                return false;
            }
            return _items.TryGetValue(itemId.Trim(), out item);
        }

        public IEnumerable<ItemDefinition> ResolvePredicate(string predicate)
        {
            if (string.IsNullOrWhiteSpace(predicate))
                yield break;
            predicate = predicate.Trim();
            if (_items.TryGetValue(predicate, out var exact))
            {
                yield return exact;
                yield break;
            }

            foreach (var item in _items.Values)
            {
                if (item.MatchesPredicate(predicate))
                    yield return item;
            }
        }

        public bool MatchesPredicate(ItemDefinition item, string predicate)
        {
            if (item == null)
                return false;
            return item.MatchesPredicate(predicate);
        }
    }
}
