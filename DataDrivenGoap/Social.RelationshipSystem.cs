using System;
using System.Collections.Generic;
using System.Linq;
using DataDrivenGoap.Compatibility;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;
using DataDrivenGoap.World;

namespace DataDrivenGoap.Social
{
    internal sealed class RelationshipDefinition
    {
        public string Id { get; }
        public bool Symmetric { get; }
        public double MinValue { get; }
        public double MaxValue { get; }
        public double DefaultValue { get; }
        public double? DecayPerDay { get; }
        public string Description { get; }

        public RelationshipDefinition(RelationshipTypeConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrWhiteSpace(config.id))
                throw new ArgumentException("Relationship id must be provided", nameof(config));

            if (double.IsNaN(config.minValue) || double.IsInfinity(config.minValue))
                throw new ArgumentException("Relationship minValue must be a finite number", nameof(config));
            if (double.IsNaN(config.maxValue) || double.IsInfinity(config.maxValue))
                throw new ArgumentException("Relationship maxValue must be a finite number", nameof(config));
            if (config.minValue > config.maxValue)
                throw new ArgumentException("Relationship minValue must be less than or equal to maxValue", nameof(config));

            if (double.IsNaN(config.defaultValue) || double.IsInfinity(config.defaultValue))
                throw new ArgumentException("Relationship defaultValue must be a finite number", nameof(config));
            if (config.defaultValue < config.minValue || config.defaultValue > config.maxValue)
                throw new ArgumentException("Relationship defaultValue must be within the configured range", nameof(config));

            Id = config.id.Trim();
            Symmetric = config.symmetric;
            MinValue = config.minValue;
            MaxValue = config.maxValue;
            DefaultValue = config.defaultValue;
            DecayPerDay = config.decayPerDay;
            Description = config.description;
        }
    }

    /// <summary>
    /// Maintains directed relationship scores between pawns. Relationship values are stored
    /// as actor attributes using the pattern <c>social.&lt;relationshipId&gt;.&lt;targetId&gt;</c>
    /// so they participate in the existing world snapshot system and persist in logs.
    /// </summary>
    public sealed class SocialRelationshipSystem
    {
        public const string AttributePrefix = "social";

        private readonly IWorld _world;
        private readonly Dictionary<string, RelationshipDefinition> _definitions;
        private readonly bool _enabled;

        public SocialRelationshipSystem(IWorld world, WorldClock clock, SocialInteractionConfig config)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            if (config == null) throw new ArgumentNullException(nameof(config));

            _enabled = config.enabled;
            _definitions = new Dictionary<string, RelationshipDefinition>(StringComparer.OrdinalIgnoreCase);

            if (!_enabled)
            {
                return;
            }

            if (config.relationshipTypes == null || config.relationshipTypes.Length == 0)
                throw new ArgumentException("At least one relationship type must be configured", nameof(config));

            foreach (var type in config.relationshipTypes)
            {
                if (type == null)
                    throw new ArgumentException("Relationship type entries cannot be null", nameof(config));

                var def = new RelationshipDefinition(type);
                if (_definitions.ContainsKey(def.Id))
                    throw new ArgumentException($"Duplicate relationship id '{def.Id}' in configuration", nameof(config));
                _definitions[def.Id] = def;
            }

            if (config.seeds == null)
                throw new ArgumentException("Relationship seeds collection must be provided (may be empty)", nameof(config));

            foreach (var seed in config.seeds)
            {
                if (seed == null)
                    throw new ArgumentException("Relationship seed entries cannot be null", nameof(config));
                SeedRelationship(seed);
            }
        }

        public IReadOnlyCollection<string> RelationshipIds => _definitions.Keys;

        public static string BuildAttributeKey(string relationshipId, ThingId target)
        {
            if (string.IsNullOrWhiteSpace(relationshipId))
                throw new ArgumentException("Relationship id is required", nameof(relationshipId));
            return $"{AttributePrefix}.{relationshipId.Trim()}.{target.Value}";
        }

        public double GetRelationship(IWorldSnapshot snapshot, ThingId from, ThingId to, string relationshipId)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (!_definitions.TryGetValue(relationshipId, out var def))
                throw new ArgumentException($"Unknown relationship id '{relationshipId}'", nameof(relationshipId));

            var fromThing = snapshot.GetThing(from);
            if (fromThing == null)
            {
                return def.DefaultValue;
            }

            if (TryGetRelationshipValue(fromThing, def, to, out var value))
            {
                return value;
            }

            return def.DefaultValue;
        }

        public double AdjustRelationship(ThingId from, ThingId to, string relationshipId, double delta)
        {
            return MutateRelationship(from, to, relationshipId, current => current + delta);
        }

        public double SetRelationship(ThingId from, ThingId to, string relationshipId, double value)
        {
            return MutateRelationship(from, to, relationshipId, _ => value);
        }

        private double MutateRelationship(ThingId from, ThingId to, string relationshipId, Func<double, double> projector)
        {
            if (!_enabled) return 0.0;
            if (!_definitions.TryGetValue(relationshipId, out var def))
                throw new ArgumentException($"Unknown relationship id '{relationshipId}'", nameof(relationshipId));

            for (int attempt = 0; attempt < 8; attempt++)
            {
                var snapshot = _world.Snap();
                var fromThing = snapshot.GetThing(from);
                var toThing = snapshot.GetThing(to);
                if (fromThing == null || toThing == null)
                {
                    return def.DefaultValue;
                }

                TryGetRelationshipValue(fromThing, def, to, out var current);
                double targetValue = MathUtilities.Clamp(projector(current), def.MinValue, def.MaxValue);
                if (Math.Abs(targetValue - current) < 1e-6)
                {
                    return targetValue;
                }

                var writes = new List<WriteSetEntry>
                {
                    new WriteSetEntry(from, BuildAttributeKey(def.Id, to), targetValue)
                };
                var reads = new List<ReadSetEntry>
                {
                    new ReadSetEntry(from, BuildAttributeKey(def.Id, to), current)
                };

                if (def.Symmetric)
                {
                    TryGetRelationshipValue(toThing, def, from, out var reverseCurrent);
                    double reverseTarget = MathUtilities.Clamp(projector(reverseCurrent), def.MinValue, def.MaxValue);
                    if (Math.Abs(reverseTarget - reverseCurrent) >= 1e-6)
                    {
                        writes.Add(new WriteSetEntry(to, BuildAttributeKey(def.Id, from), reverseTarget));
                        reads.Add(new ReadSetEntry(to, BuildAttributeKey(def.Id, from), reverseCurrent));
                    }
                }

                var batch = new EffectBatch
                {
                    BaseVersion = snapshot.Version,
                    Reads = reads.ToArray(),
                    Writes = writes.ToArray(),
                    FactDeltas = null,
                    Spawns = Array.Empty<ThingSpawnRequest>(),
                    PlanCooldowns = Array.Empty<PlanCooldownRequest>(),
                    Despawns = Array.Empty<ThingId>(),
                    InventoryOps = Array.Empty<InventoryDelta>(),
                    CurrencyOps = Array.Empty<CurrencyDelta>(),
                ShopTransactions = Array.Empty<ShopTransaction>(),
                RelationshipOps = Array.Empty<RelationshipDelta>(),
                CropOps = Array.Empty<CropOperation>(),
                AnimalOps = Array.Empty<AnimalOperation>(),
                MiningOps = Array.Empty<MiningOperation>(),
                FishingOps = Array.Empty<FishingOperation>(),
                ForagingOps = Array.Empty<ForagingOperation>()
            };

                if (_world.TryCommit(batch) == CommitResult.Committed)
                {
                    return targetValue;
                }
            }

            throw new InvalidOperationException("Failed to update relationship after multiple retries");
        }

        private void SeedRelationship(RelationshipSeedConfig seed)
        {
            if (seed == null) throw new ArgumentNullException(nameof(seed));
            if (string.IsNullOrWhiteSpace(seed.from))
                throw new ArgumentException("Seed configuration must include a 'from' actor id", nameof(seed));
            if (string.IsNullOrWhiteSpace(seed.to))
                throw new ArgumentException("Seed configuration must include a 'to' actor id", nameof(seed));
            if (string.IsNullOrWhiteSpace(seed.type))
                throw new ArgumentException("Seed configuration must include a relationship 'type'", nameof(seed));

            var from = new ThingId(seed.from.Trim());
            var to = new ThingId(seed.to.Trim());
            if (!_definitions.TryGetValue(seed.type.Trim(), out var def))
            {
                throw new ArgumentException($"Unknown relationship id '{seed.type}' in relationship seed", nameof(seed));
            }

            var snapshot = _world.Snap();
            var fromThing = snapshot.GetThing(from);
            var toThing = snapshot.GetThing(to);
            if (fromThing == null || toThing == null)
                throw new ArgumentException("Relationship seed refers to unknown actor id", nameof(seed));

            MutateRelationship(from, to, def.Id, _ => seed.value);
        }

        private static bool TryGetRelationshipValue(ThingView thing, RelationshipDefinition def, ThingId target, out double value)
        {
            value = def.DefaultValue;
            if (thing?.Attributes == null)
            {
                return false;
            }

            var key = BuildAttributeKey(def.Id, target);
            if (thing.Attributes.TryGetValue(key, out var existing))
            {
                value = MathUtilities.Clamp(existing, def.MinValue, def.MaxValue);
                return true;
            }

            return false;
        }
    }
}
