using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;
using DataDrivenGoap.Persistence;

namespace DataDrivenGoap.Simulation
{
    public readonly struct AnimalProduceYield
    {
        public string ItemId { get; }
        public int Quantity { get; }

        public AnimalProduceYield(string itemId, int quantity)
        {
            ItemId = itemId ?? string.Empty;
            Quantity = quantity;
        }
    }

    public readonly struct AnimalOperationResult
    {
        public bool Success { get; }
        public IReadOnlyList<InventoryDelta> InventoryChanges { get; }
        public IReadOnlyList<AnimalProduceYield> ProduceYields { get; }

        public AnimalOperationResult(bool success, IReadOnlyList<InventoryDelta> inventoryChanges, IReadOnlyList<AnimalProduceYield> produceYields)
        {
            Success = success;
            InventoryChanges = inventoryChanges ?? Array.Empty<InventoryDelta>();
            ProduceYields = produceYields ?? Array.Empty<AnimalProduceYield>();
        }

        public static AnimalOperationResult Failed => new AnimalOperationResult(false, Array.Empty<InventoryDelta>(), Array.Empty<AnimalProduceYield>());
    }

    public sealed class AnimalSystem : IAnimalQuery
    {
        private sealed class ProduceDefinition
        {
            public string ItemId { get; }
            public int Quantity { get; }
            public double IntervalHours { get; }
            public double HappinessThreshold { get; }
            public double HungerThreshold { get; }

            public ProduceDefinition(ThingId ownerId, AnimalProduceConfig config, int index)
            {
                if (config == null)
                    throw new InvalidDataException($"Animal '{ownerId.Value}' has a null produce entry at index {index}.");

                if (string.IsNullOrWhiteSpace(config.item))
                    throw new InvalidDataException($"Animal '{ownerId.Value}' produce entry {index} is missing a valid 'item'.");
                ItemId = config.item.Trim();

                if (config.quantity <= 0)
                    throw new InvalidDataException($"Animal '{ownerId.Value}' produce '{ItemId}' must specify a positive 'quantity'.");
                Quantity = config.quantity;

                if (!double.IsFinite(config.intervalHours) || config.intervalHours <= 0.0)
                    throw new InvalidDataException($"Animal '{ownerId.Value}' produce '{ItemId}' must specify a positive 'intervalHours'.");
                IntervalHours = config.intervalHours;

                if (config.happinessThreshold is null || !double.IsFinite(config.happinessThreshold.Value) || config.happinessThreshold.Value < 0.0 || config.happinessThreshold.Value > 1.0)
                    throw new InvalidDataException($"Animal '{ownerId.Value}' produce '{ItemId}' must specify 'happinessThreshold' between 0 and 1.");
                HappinessThreshold = config.happinessThreshold.Value;

                if (config.hungerThreshold is null || !double.IsFinite(config.hungerThreshold.Value) || config.hungerThreshold.Value < 0.0 || config.hungerThreshold.Value > 1.0)
                    throw new InvalidDataException($"Animal '{ownerId.Value}' produce '{ItemId}' must specify 'hungerThreshold' between 0 and 1.");
                HungerThreshold = config.hungerThreshold.Value;
            }
        }

        private sealed class AnimalDefinition
        {
            public ThingId Id { get; }
            public string Species { get; }
            public string FeedItemId { get; }
            public int FeedQuantity { get; }
            public double HungerRatePerHour { get; }
            public double HappinessDecayPerHour { get; }
            public double FeedHappinessGain { get; }
            public double BrushHappinessGain { get; }
            public double BrushCooldownHours { get; }
            public IReadOnlyList<ProduceDefinition> Produce { get; }

            public AnimalDefinition(ThingId id, AnimalConfig config)
            {
                Id = id;
                if (config == null)
                    throw new InvalidDataException($"Animal definition '{id.Value}' is missing configuration.");

                Species = string.IsNullOrWhiteSpace(config.species) ? string.Empty : config.species.Trim();
                FeedItemId = string.IsNullOrWhiteSpace(config.feedItemId) ? string.Empty : config.feedItemId.Trim();

                if (config.feedQuantity <= 0)
                    throw new InvalidDataException($"Animal '{id.Value}' must specify a positive 'feedQuantity'.");
                FeedQuantity = config.feedQuantity;

                if (!double.IsFinite(config.hungerHours) || config.hungerHours <= 0.0)
                    throw new InvalidDataException($"Animal '{id.Value}' must specify a positive 'hungerHours'.");
                HungerRatePerHour = 1.0 / config.hungerHours;

                if (!double.IsFinite(config.happinessHalfLifeHours) || config.happinessHalfLifeHours <= 0.0)
                    throw new InvalidDataException($"Animal '{id.Value}' must specify a positive 'happinessHalfLifeHours'.");
                HappinessDecayPerHour = 1.0 / config.happinessHalfLifeHours;

                if (!double.IsFinite(config.feedHappiness) || config.feedHappiness < 0.0 || config.feedHappiness > 1.0)
                    throw new InvalidDataException($"Animal '{id.Value}' must specify 'feedHappiness' between 0 and 1.");
                FeedHappinessGain = config.feedHappiness;

                if (!double.IsFinite(config.brushHappiness) || config.brushHappiness < 0.0 || config.brushHappiness > 1.0)
                    throw new InvalidDataException($"Animal '{id.Value}' must specify 'brushHappiness' between 0 and 1.");
                BrushHappinessGain = config.brushHappiness;

                if (!double.IsFinite(config.brushCooldownHours) || config.brushCooldownHours <= 0.0)
                    throw new InvalidDataException($"Animal '{id.Value}' must specify a positive 'brushCooldownHours'.");
                BrushCooldownHours = config.brushCooldownHours;

                Produce = (config.produce ?? Array.Empty<AnimalProduceConfig>())
                    .Select((p, index) => new ProduceDefinition(id, p, index))
                    .ToArray();
            }
        }

        private sealed class ProduceState
        {
            public double TimerHours;
            public bool Ready;
        }

        private sealed class AnimalState
        {
            public double Hunger;
            public double Happiness;
            public double LastFedHours;
            public double LastBrushedHours;
            public List<ProduceState> Produce;

            public AnimalState()
            {
                Hunger = 0.3;
                Happiness = 0.7;
                LastFedHours = 0.0;
                LastBrushedHours = 0.0;
                Produce = new List<ProduceState>();
            }
        }

        private readonly object _gate = new object();
        private readonly Dictionary<ThingId, AnimalDefinition> _definitions;
        private readonly Dictionary<ThingId, AnimalState> _states;
        private double _lastWorldHours = double.NaN;
        private bool _growthPaused;

        public AnimalSystem(IEnumerable<AnimalConfig> configs)
        {
            _definitions = new Dictionary<ThingId, AnimalDefinition>();
            if (configs != null)
            {
                foreach (var cfg in configs)
                {
                    if (cfg == null || string.IsNullOrWhiteSpace(cfg.id))
                        continue;
                    var id = new ThingId(cfg.id.Trim());
                    _definitions[id] = new AnimalDefinition(id, cfg);
                }
            }

            _states = new Dictionary<ThingId, AnimalState>();
        }

        public void RegisterAnimal(ThingId id)
        {
            if (string.IsNullOrWhiteSpace(id.Value))
                return;
            lock (_gate)
            {
                EnsureState(id);
            }
        }

        public void ApplyWeatherEffects(bool growthPaused)
        {
            lock (_gate)
            {
                _growthPaused = growthPaused;
            }
        }

        public void Tick(WorldTimeSnapshot time)
        {
            if (time == null)
                return;

            double currentHours = time.TotalWorldSeconds / 3600.0;
            if (!double.IsFinite(currentHours))
                return;

            lock (_gate)
            {
                if (double.IsNaN(_lastWorldHours))
                    _lastWorldHours = currentHours;

                double delta = Math.Max(0.0, currentHours - _lastWorldHours);
                if (delta > 0.0)
                {
                    foreach (var kv in _states)
                    {
                        var id = kv.Key;
                        if (!_definitions.TryGetValue(id, out var def))
                            continue;
                        AdvanceState(def, kv.Value, delta);
                    }

                    _lastWorldHours = currentHours;
                }
            }
        }

        public AnimalOperationResult Apply(AnimalOperation operation)
        {
            if (string.IsNullOrWhiteSpace(operation.Animal.Value))
                return AnimalOperationResult.Failed;

            lock (_gate)
            {
                if (!_definitions.TryGetValue(operation.Animal, out var def))
                    return AnimalOperationResult.Failed;

                var state = EnsureState(operation.Animal, def);
                switch (operation.Kind)
                {
                    case AnimalOperationKind.Feed:
                        return ApplyFeed(def, state, operation);
                    case AnimalOperationKind.Brush:
                        return ApplyBrush(def, state, operation);
                    case AnimalOperationKind.Collect:
                        return ApplyCollect(def, state, operation);
                    default:
                        return AnimalOperationResult.Failed;
                }
            }
        }

        public bool TryGet(ThingId animal, out AnimalStateSnapshot state)
        {
            lock (_gate)
            {
                if (!_definitions.TryGetValue(animal, out var def))
                {
                    state = default;
                    return false;
                }

                var s = EnsureState(animal, def);
                double now = double.IsNaN(_lastWorldHours) ? 0.0 : _lastWorldHours;
                bool hasProduce = s.Produce.Any(p => p.Ready);
                double minReady = double.PositiveInfinity;
                for (int i = 0; i < def.Produce.Count && i < s.Produce.Count; i++)
                {
                    var produceDef = def.Produce[i];
                    var produceState = s.Produce[i];
                    if (produceState.Ready)
                    {
                        minReady = 0.0;
                        break;
                    }

                    double remaining = produceDef.IntervalHours - produceState.TimerHours;
                    if (remaining < minReady)
                        minReady = remaining;
                }

                if (double.IsPositiveInfinity(minReady))
                    minReady = 0.0;

                double hoursSinceFed = Math.Max(0.0, now - s.LastFedHours);
                double hoursSinceBrushed = Math.Max(0.0, now - s.LastBrushedHours);
                bool needsBrush = hoursSinceBrushed >= def.BrushCooldownHours;

                state = new AnimalStateSnapshot(
                    true,
                    def.Species,
                    Math.Clamp(s.Hunger, 0.0, 1.0),
                    Math.Clamp(s.Happiness, 0.0, 1.0),
                    hasProduce,
                    Math.Max(0.0, minReady),
                    hoursSinceFed,
                    hoursSinceBrushed,
                    needsBrush);
                return true;
            }
        }

        public AnimalSummarySnapshot SnapshotSummary()
        {
            lock (_gate)
            {
                int total = 0;
                int hungry = 0;
                int produce = 0;
                int needsBrush = 0;
                double now = double.IsNaN(_lastWorldHours) ? 0.0 : _lastWorldHours;

                foreach (var kv in _states)
                {
                    if (!_definitions.TryGetValue(kv.Key, out var def))
                        continue;
                    total++;
                    var s = kv.Value;
                    if (s.Hunger >= 0.5)
                        hungry++;
                    if (s.Produce.Any(p => p.Ready))
                        produce++;
                    if (now - s.LastBrushedHours >= def.BrushCooldownHours)
                        needsBrush++;
                }

                return new AnimalSummarySnapshot(total, hungry, produce, needsBrush);
            }
        }

        public AnimalSystemState CaptureState()
        {
            lock (_gate)
            {
                var state = new AnimalSystemState
                {
                    lastWorldHours = _lastWorldHours,
                    growthPaused = _growthPaused
                };

                foreach (var kv in _states)
                {
                    var animalId = kv.Key;
                    var s = kv.Value;
                    var snapshot = new AnimalStateData
                    {
                        id = animalId.Value,
                        hunger = s.Hunger,
                        happiness = s.Happiness,
                        lastFedHours = s.LastFedHours,
                        lastBrushedHours = s.LastBrushedHours,
                    };

                    foreach (var produce in s.Produce)
                    {
                        snapshot.produce.Add(new AnimalProduceStateData
                        {
                            timerHours = produce.TimerHours,
                            ready = produce.Ready
                        });
                    }

                    state.animals.Add(snapshot);
                }

                return state;
            }
        }

        public void ApplyState(AnimalSystemState state)
        {
            if (state == null)
                return;

            lock (_gate)
            {
                _states.Clear();

                if (state.animals != null)
                {
                    foreach (var animal in state.animals)
                    {
                        if (animal == null || string.IsNullOrWhiteSpace(animal.id))
                            continue;
                        var id = new ThingId(animal.id.Trim());
                        if (!_definitions.TryGetValue(id, out var def))
                            continue;
                        var s = new AnimalState
                        {
                            Hunger = Math.Clamp(animal.hunger, 0.0, 1.0),
                            Happiness = Math.Clamp(animal.happiness, 0.0, 1.0),
                            LastFedHours = Math.Max(0.0, animal.lastFedHours),
                            LastBrushedHours = Math.Max(0.0, animal.lastBrushedHours),
                            Produce = new List<ProduceState>()
                        };

                        foreach (var produceDef in def.Produce)
                        {
                            s.Produce.Add(new ProduceState());
                        }

                        if (animal.produce != null)
                        {
                            int limit = Math.Min(s.Produce.Count, animal.produce.Count);
                            for (int i = 0; i < limit; i++)
                            {
                                var incoming = animal.produce[i];
                                if (incoming == null)
                                    continue;
                                s.Produce[i].TimerHours = Math.Max(0.0, incoming.timerHours);
                                s.Produce[i].Ready = incoming.ready;
                            }
                        }

                        _states[id] = s;
                    }
                }

                _lastWorldHours = state.lastWorldHours;
                _growthPaused = state.growthPaused;
            }
        }

        private AnimalState EnsureState(ThingId id)
        {
            if (!_states.TryGetValue(id, out var state))
            {
                state = new AnimalState();
                state.Produce = new List<ProduceState>();
                if (_definitions.TryGetValue(id, out var def))
                {
                    foreach (var _ in def.Produce)
                        state.Produce.Add(new ProduceState());
                }

                double now = double.IsNaN(_lastWorldHours) ? 0.0 : _lastWorldHours;
                state.LastFedHours = now;
                state.LastBrushedHours = now;
                _states[id] = state;
            }

            return state;
        }

        private AnimalState EnsureState(ThingId id, AnimalDefinition def)
        {
            if (!_states.TryGetValue(id, out var state))
            {
                state = new AnimalState();
                state.Produce = new List<ProduceState>();
                foreach (var _ in def.Produce)
                    state.Produce.Add(new ProduceState());
                double now = double.IsNaN(_lastWorldHours) ? 0.0 : _lastWorldHours;
                state.LastFedHours = now;
                state.LastBrushedHours = now;
                _states[id] = state;
            }
            else if (state.Produce.Count != def.Produce.Count)
            {
                // Rebuild produce list if definition changed.
                var list = new List<ProduceState>(def.Produce.Count);
                for (int i = 0; i < def.Produce.Count; i++)
                {
                    if (i < state.Produce.Count)
                        list.Add(state.Produce[i]);
                    else
                        list.Add(new ProduceState());
                }
                state.Produce = list;
            }

            return state;
        }

        private void AdvanceState(AnimalDefinition def, AnimalState state, double deltaHours)
        {
            if (state == null)
                return;

            if (deltaHours <= 0.0)
                return;

            state.Hunger = Math.Clamp(state.Hunger + def.HungerRatePerHour * deltaHours, 0.0, 1.0);
            double hungerPenalty = state.Hunger >= 0.8 ? 1.5 : (state.Hunger >= 0.6 ? 1.2 : 1.0);
            state.Happiness = Math.Clamp(state.Happiness - def.HappinessDecayPerHour * deltaHours * hungerPenalty, 0.0, 1.0);

            if (state.Produce.Count == 0)
                return;

            for (int i = 0; i < state.Produce.Count && i < def.Produce.Count; i++)
            {
                var produceDef = def.Produce[i];
                var produceState = state.Produce[i];
                if (produceState.Ready)
                    continue;

                bool conditionsMet = state.Hunger <= produceDef.HungerThreshold && state.Happiness >= produceDef.HappinessThreshold;
                if (!conditionsMet || _growthPaused)
                {
                    // Allow timers to slowly regress if animals are unhappy or starving.
                    produceState.TimerHours = Math.Max(0.0, produceState.TimerHours - deltaHours * 0.25);
                    continue;
                }

                produceState.TimerHours += deltaHours;
                if (produceState.TimerHours >= produceDef.IntervalHours)
                {
                    produceState.TimerHours = produceDef.IntervalHours;
                    produceState.Ready = true;
                }
            }
        }

        private AnimalOperationResult ApplyFeed(AnimalDefinition def, AnimalState state, AnimalOperation operation)
        {
            string feedItem = !string.IsNullOrWhiteSpace(operation.ItemId) ? operation.ItemId.Trim() : def.FeedItemId;
            if (!string.IsNullOrWhiteSpace(def.FeedItemId) && !string.Equals(feedItem, def.FeedItemId, StringComparison.OrdinalIgnoreCase))
                return AnimalOperationResult.Failed;

            int quantity = operation.Quantity > 0 ? operation.Quantity : def.FeedQuantity;
            if (quantity <= 0)
                quantity = def.FeedQuantity;
            if (quantity <= 0)
                quantity = 1;

            var inventory = new List<InventoryDelta>();
            if (!string.IsNullOrWhiteSpace(feedItem) && quantity > 0)
                inventory.Add(new InventoryDelta(operation.Actor, feedItem, quantity, true));

            state.Hunger = 0.0;
            state.Happiness = Math.Clamp(state.Happiness + def.FeedHappinessGain, 0.0, 1.0);
            state.LastFedHours = double.IsNaN(_lastWorldHours) ? 0.0 : _lastWorldHours;

            return new AnimalOperationResult(true, inventory, Array.Empty<AnimalProduceYield>());
        }

        private AnimalOperationResult ApplyBrush(AnimalDefinition def, AnimalState state, AnimalOperation operation)
        {
            double now = double.IsNaN(_lastWorldHours) ? 0.0 : _lastWorldHours;
            if (now - state.LastBrushedHours < def.BrushCooldownHours - 1e-3)
                return AnimalOperationResult.Failed;

            state.Happiness = Math.Clamp(state.Happiness + def.BrushHappinessGain, 0.0, 1.0);
            state.LastBrushedHours = now;
            return new AnimalOperationResult(true, Array.Empty<InventoryDelta>(), Array.Empty<AnimalProduceYield>());
        }

        private AnimalOperationResult ApplyCollect(AnimalDefinition def, AnimalState state, AnimalOperation operation)
        {
            if (state.Produce.Count == 0)
                return AnimalOperationResult.Failed;

            var inventory = new List<InventoryDelta>();
            var yields = new List<AnimalProduceYield>();
            bool any = false;

            for (int i = 0; i < state.Produce.Count && i < def.Produce.Count; i++)
            {
                var produceDef = def.Produce[i];
                var produceState = state.Produce[i];
                if (!produceState.Ready)
                    continue;

                if (!string.IsNullOrWhiteSpace(produceDef.ItemId) && produceDef.Quantity > 0)
                {
                    inventory.Add(new InventoryDelta(operation.Actor, produceDef.ItemId, produceDef.Quantity, false));
                    yields.Add(new AnimalProduceYield(produceDef.ItemId, produceDef.Quantity));
                }

                produceState.Ready = false;
                produceState.TimerHours = 0.0;
                any = true;
            }

            if (!any)
                return AnimalOperationResult.Failed;

            return new AnimalOperationResult(true, inventory, yields);
        }
    }
}
