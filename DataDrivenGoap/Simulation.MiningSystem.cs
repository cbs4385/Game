using System;
using System.Collections.Generic;
using System.Linq;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;

namespace DataDrivenGoap.Simulation
{
    public readonly struct MiningOperationResult
    {
        public bool Success { get; }
        public IReadOnlyList<InventoryDelta> InventoryChanges { get; }
        public string OreId { get; }
        public string ItemId { get; }
        public int Quantity { get; }
        public string SkillId { get; }
        public double SkillXp { get; }

        public MiningOperationResult(bool success, IReadOnlyList<InventoryDelta> inventoryChanges, string oreId, string itemId, int quantity, string skillId, double skillXp)
        {
            Success = success;
            InventoryChanges = inventoryChanges ?? Array.Empty<InventoryDelta>();
            OreId = oreId ?? string.Empty;
            ItemId = itemId ?? string.Empty;
            Quantity = quantity;
            SkillId = skillId ?? string.Empty;
            SkillXp = double.IsFinite(skillXp) ? Math.Max(0.0, skillXp) : 0.0;
        }

        public static MiningOperationResult Failed => new MiningOperationResult(false, Array.Empty<InventoryDelta>(), string.Empty, string.Empty, 0, string.Empty, 0.0);
    }

    public sealed class MiningSystem : IMiningQuery
    {
        private sealed class MiningOreDefinition
        {
            public string Id { get; }
            public string ItemId { get; }
            public int MinQuantity { get; }
            public int MaxQuantity { get; }
            public HashSet<string> Seasons { get; }
            public HashSet<string> Weather { get; }
            public HashSet<string> Biomes { get; }
            public double Weight { get; }
            public int HitsPerNode { get; }
            public string RequiredToolId { get; }
            public int RequiredToolTier { get; }
            public string SkillId { get; }
            public double SkillXp { get; }

            public MiningOreDefinition(MiningOreConfig cfg)
            {
                if (cfg == null)
                    throw new ArgumentNullException(nameof(cfg));

                if (string.IsNullOrWhiteSpace(cfg.id))
                    throw new ArgumentException("Mining ore definitions must include an id.", nameof(cfg));
                Id = cfg.id.Trim();

                if (string.IsNullOrWhiteSpace(cfg.itemId))
                    throw new ArgumentException("Mining ore definitions must include an itemId.", nameof(cfg));
                ItemId = cfg.itemId.Trim();

                if (!cfg.minQuantity.HasValue)
                    throw new ArgumentException("Mining ore definitions must include a minimum quantity.", nameof(cfg));
                int min = cfg.minQuantity.Value;
                if (min <= 0)
                    throw new ArgumentOutOfRangeException(nameof(cfg.minQuantity), min, "Minimum quantity must be positive.");

                if (!cfg.maxQuantity.HasValue)
                    throw new ArgumentException("Mining ore definitions must include a maximum quantity.", nameof(cfg));
                int max = cfg.maxQuantity.Value;
                if (max < min)
                    throw new ArgumentOutOfRangeException(nameof(cfg.maxQuantity), max, "Maximum quantity must be at least the minimum quantity.");
                MinQuantity = min;
                MaxQuantity = max;

                Seasons = BuildSet(cfg.seasons);
                Weather = BuildSet(cfg.weather);
                Biomes = BuildSet(cfg.biomes);

                if (!cfg.weight.HasValue)
                    throw new ArgumentException("Mining ore definitions must include a weight.", nameof(cfg));
                double weight = cfg.weight.Value;
                if (!double.IsFinite(weight) || weight <= 0)
                    throw new ArgumentOutOfRangeException(nameof(cfg.weight), weight, "Weight must be a positive, finite value.");
                Weight = weight;

                if (!cfg.hitsPerNode.HasValue)
                    throw new ArgumentException("Mining ore definitions must include hitsPerNode.", nameof(cfg));
                int hits = cfg.hitsPerNode.Value;
                if (hits <= 0)
                    throw new ArgumentOutOfRangeException(nameof(cfg.hitsPerNode), hits, "hitsPerNode must be positive.");
                HitsPerNode = hits;

                RequiredToolId = string.IsNullOrWhiteSpace(cfg.requiredToolId) ? null : cfg.requiredToolId.Trim();
                RequiredToolTier = Math.Max(0, cfg.requiredToolTier ?? 0);

                SkillId = string.IsNullOrWhiteSpace(cfg.skill) ? null : cfg.skill.Trim();
                if (cfg.skillXp.HasValue)
                {
                    double xp = cfg.skillXp.Value;
                    if (!double.IsFinite(xp) || xp < 0.0)
                        throw new ArgumentOutOfRangeException(nameof(cfg.skillXp), xp, "Skill XP must be a non-negative finite value.");
                    SkillXp = xp;
                }
                else
                {
                    SkillXp = 0.0;
                }
            }

            public bool AllowsSeason(string season)
            {
                if (Seasons == null || Seasons.Count == 0)
                    return true;
                if (string.IsNullOrWhiteSpace(season))
                    return false;
                return Seasons.Contains(season.Trim().ToLowerInvariant());
            }

            public bool AllowsWeather(string weatherId)
            {
                if (Weather == null || Weather.Count == 0)
                    return true;
                if (string.IsNullOrWhiteSpace(weatherId))
                    return false;
                return Weather.Contains(weatherId.Trim().ToLowerInvariant());
            }

            public bool AllowsBiome(HashSet<string> nodeBiomes)
            {
                if (Biomes == null || Biomes.Count == 0)
                    return true;
                if (nodeBiomes == null || nodeBiomes.Count == 0)
                    return false;
                foreach (var biome in nodeBiomes)
                {
                    if (Biomes.Contains(biome))
                        return true;
                }
                return false;
            }

            public int SampleQuantity(Random rng)
            {
                if (rng == null || MinQuantity >= MaxQuantity)
                    return MinQuantity;
                return rng.Next(MinQuantity, MaxQuantity + 1);
            }

            private static HashSet<string> BuildSet(IEnumerable<string> values)
            {
                if (values == null)
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var value in values)
                {
                    if (string.IsNullOrWhiteSpace(value))
                        continue;
                    set.Add(value.Trim().ToLowerInvariant());
                }
                return set;
            }
        }

        private sealed class MiningLayerDefinition
        {
            public string Id { get; }
            public HashSet<string> Biomes { get; }
            public List<MiningOreDefinition> Ores { get; }

            public MiningLayerDefinition(MiningLayerConfig cfg, IDictionary<string, MiningOreDefinition> oreLookup)
            {
                if (cfg == null)
                    throw new ArgumentNullException(nameof(cfg));
                if (string.IsNullOrWhiteSpace(cfg.id))
                    throw new ArgumentException("Mining layer definitions must include an id.", nameof(cfg));
                Id = cfg.id.Trim();

                Biomes = BuildSet(cfg.biomes);

                if (cfg.oreIds == null || cfg.oreIds.Length == 0)
                    throw new ArgumentException($"Mining layer '{Id}' must include at least one ore id.", nameof(cfg));

                Ores = new List<MiningOreDefinition>();
                foreach (var oreId in cfg.oreIds)
                {
                    if (string.IsNullOrWhiteSpace(oreId))
                        continue;
                    var normalized = oreId.Trim();
                    if (!oreLookup.TryGetValue(normalized, out var def))
                        throw new ArgumentException($"Mining layer '{Id}' references unknown ore id '{normalized}'.", nameof(cfg));
                    if (!Ores.Contains(def))
                        Ores.Add(def);
                }

                if (Ores.Count == 0)
                    throw new ArgumentException($"Mining layer '{Id}' must include at least one valid ore id.", nameof(cfg));
            }

            public bool AllowsBiomes(HashSet<string> nodeBiomes)
            {
                if (Biomes == null || Biomes.Count == 0)
                    return true;
                if (nodeBiomes == null || nodeBiomes.Count == 0)
                    return false;
                foreach (var biome in nodeBiomes)
                {
                    if (Biomes.Contains(biome))
                        return true;
                }
                return false;
            }

            private static HashSet<string> BuildSet(IEnumerable<string> values)
            {
                if (values == null)
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var value in values)
                {
                    if (string.IsNullOrWhiteSpace(value))
                        continue;
                    set.Add(value.Trim().ToLowerInvariant());
                }
                return set;
            }
        }

        private sealed class MiningNodeState
        {
            public ThingId Id { get; }
            public GridPos Position { get; }
            public string LayerId { get; }
            public HashSet<string> Biomes { get; }
            public bool Active { get; set; }
            public MiningOreDefinition ActiveOre { get; set; }
            public int RemainingHits { get; set; }
            public double NextRespawnDay { get; set; }

            public MiningNodeState(ThingId id, GridPos position, string layerId, IEnumerable<string> biomes)
            {
                Id = id;
                Position = position;
                LayerId = layerId ?? string.Empty;
                Biomes = BuildSet(biomes);
                Active = false;
                ActiveOre = null;
                RemainingHits = 0;
                NextRespawnDay = 0.0;
            }

            private static HashSet<string> BuildSet(IEnumerable<string> biomes)
            {
                if (biomes == null)
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var biome in biomes)
                {
                    if (string.IsNullOrWhiteSpace(biome))
                        continue;
                    set.Add(biome.Trim().ToLowerInvariant());
                }
                return set;
            }
        }

        private readonly object _gate = new object();
        private readonly Dictionary<ThingId, MiningNodeState> _nodes = new Dictionary<ThingId, MiningNodeState>();
        private readonly Dictionary<string, MiningOreDefinition> _oresById;
        private readonly Dictionary<string, MiningLayerDefinition> _layersById;
        private readonly Random _rng;
        private readonly bool _enabled;
        private readonly double _respawnIntervalDays;
        private readonly int _maxActiveNodes;
        private readonly SkillProgressionSystem _skillProgression;
        private double _currentWorldDay;

        public MiningSystem(MiningSystemConfig config, int rngSeed, SkillProgressionSystem skillProgression = null)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            _enabled = config.enabled;
            if (_enabled)
            {
                if (!double.IsFinite(config.respawnHours) || config.respawnHours <= 0)
                    throw new ArgumentOutOfRangeException(nameof(config.respawnHours), config.respawnHours, "Mining respawn hours must be a finite positive value.");
                _respawnIntervalDays = config.respawnHours / 24.0;

                if (config.maxActiveNodes < 0)
                    throw new ArgumentOutOfRangeException(nameof(config.maxActiveNodes), config.maxActiveNodes, "Max active nodes cannot be negative.");
                _maxActiveNodes = config.maxActiveNodes;
            }
            else
            {
                _respawnIntervalDays = 0.0;
                _maxActiveNodes = 0;
            }

            _rng = new Random(rngSeed ^ 0x41ac9bf7);
            _skillProgression = skillProgression;

            if (config.ores == null || config.ores.Length == 0)
                throw new ArgumentException("Mining configuration must include at least one ore definition.", nameof(config));

            _oresById = new Dictionary<string, MiningOreDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var oreConfig in config.ores)
            {
                if (oreConfig == null)
                    throw new ArgumentException("Mining ore configuration cannot contain null entries.", nameof(config));
                var def = new MiningOreDefinition(oreConfig);
                _oresById[def.Id] = def;
            }

            if (config.layers == null || config.layers.Length == 0)
                throw new ArgumentException("Mining configuration must include at least one layer definition.", nameof(config));

            _layersById = new Dictionary<string, MiningLayerDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var layerConfig in config.layers)
            {
                if (layerConfig == null)
                    throw new ArgumentException("Mining layer configuration cannot contain null entries.", nameof(config));
                var layer = new MiningLayerDefinition(layerConfig, _oresById);
                _layersById[layer.Id] = layer;
            }
        }

        public void RegisterNode(ThingId id, GridPos position, string layerId, IEnumerable<string> biomes)
        {
            if (string.IsNullOrWhiteSpace(id.Value))
                return;
            if (string.IsNullOrWhiteSpace(layerId))
                throw new ArgumentException("Mining nodes must reference a layer id.", nameof(layerId));

            lock (_gate)
            {
                if (_nodes.ContainsKey(id))
                    return;

                if (!_layersById.ContainsKey(layerId.Trim()))
                    throw new InvalidOperationException($"Mining node '{id.Value}' references unknown layer '{layerId}'.");

                var state = new MiningNodeState(id, position, layerId.Trim(), biomes);
                _nodes[id] = state;
            }
        }

        public void ClearNodes()
        {
            lock (_gate)
            {
                _nodes.Clear();
            }
        }

        public void Tick(WorldTimeSnapshot time, WeatherSnapshot weather)
        {
            if (!_enabled || time == null)
                return;

            string season = time.SeasonName?.Trim()?.ToLowerInvariant();
            string weatherId = weather.Id?.Trim()?.ToLowerInvariant();

            lock (_gate)
            {
                _currentWorldDay = time.TotalWorldDays;
                int activeCount = _nodes.Values.Count(n => n.Active && n.RemainingHits > 0);
                foreach (var node in _nodes.Values)
                {
                    if (node.Active && node.RemainingHits > 0)
                        continue;
                    if (_maxActiveNodes > 0 && activeCount >= _maxActiveNodes)
                        break;
                    if (_currentWorldDay < node.NextRespawnDay)
                        continue;

                    var ore = ChooseOreForNode(node, season, weatherId);
                    if (ore == null)
                    {
                        node.Active = false;
                        node.ActiveOre = null;
                        node.RemainingHits = 0;
                        continue;
                    }

                    node.Active = true;
                    node.ActiveOre = ore;
                    node.RemainingHits = Math.Max(1, ore.HitsPerNode);
                    activeCount++;
                }
            }
        }

        public MiningOperationResult Apply(MiningOperation operation)
        {
            ThingId xpActor = default;
            string xpSkill = null;
            double xpAmount = 0.0;
            MiningOperationResult result = MiningOperationResult.Failed;

            if (!_enabled)
                return MiningOperationResult.Failed;

            if (operation.Node.Equals(default))
                return MiningOperationResult.Failed;

            lock (_gate)
            {
                if (!_nodes.TryGetValue(operation.Node, out var node))
                    goto Exit;
                if (!node.Active || node.RemainingHits <= 0)
                    goto Exit;
                if (node.ActiveOre == null)
                    goto Exit;

                var ore = node.ActiveOre;
                if (!string.IsNullOrWhiteSpace(ore.RequiredToolId))
                {
                    if (string.IsNullOrWhiteSpace(operation.ToolItemId) ||
                        !string.Equals(ore.RequiredToolId, operation.ToolItemId, StringComparison.OrdinalIgnoreCase))
                    {
                        goto Exit;
                    }
                }
                if (ore.RequiredToolTier > 0 && operation.ToolTier < ore.RequiredToolTier)
                    goto Exit;

                int quantity = ore.SampleQuantity(_rng);
                if (quantity <= 0)
                    goto Exit;

                var deltas = new List<InventoryDelta>
                {
                    new InventoryDelta(operation.Actor, ore.ItemId, quantity, remove: false)
                };

                node.RemainingHits--;
                if (node.RemainingHits <= 0)
                {
                    node.Active = false;
                    node.ActiveOre = null;
                    node.RemainingHits = 0;
                    node.NextRespawnDay = _currentWorldDay + _respawnIntervalDays;
                }

                result = new MiningOperationResult(true, deltas, ore.Id, ore.ItemId, quantity, ore.SkillId, ore.SkillXp);
                if (result.Success && result.SkillXp > 0.0 && !string.IsNullOrWhiteSpace(result.SkillId))
                {
                    xpActor = operation.Actor;
                    xpSkill = result.SkillId;
                    xpAmount = result.SkillXp;
                }
            }

        Exit:
            if (xpAmount > 0.0 && !string.IsNullOrWhiteSpace(xpSkill) && !string.IsNullOrWhiteSpace(xpActor.Value))
                _skillProgression?.AddExperience(xpActor, xpSkill, xpAmount);

            return result;
        }

        public bool TryGet(ThingId nodeId, out MiningNodeSnapshot state)
        {
            lock (_gate)
            {
                if (_nodes.TryGetValue(nodeId, out var node))
                {
                    bool hasOre = node.Active && node.RemainingHits > 0 && node.ActiveOre != null;
                    string oreId = hasOre ? node.ActiveOre.Id : string.Empty;
                    string itemId = hasOre ? node.ActiveOre.ItemId : string.Empty;
                    string requiredTool = hasOre && !string.IsNullOrWhiteSpace(node.ActiveOre.RequiredToolId)
                        ? node.ActiveOre.RequiredToolId
                        : string.Empty;
                    int requiredTier = hasOre ? Math.Max(0, node.ActiveOre.RequiredToolTier) : 0;
                    state = new MiningNodeSnapshot(
                        exists: true,
                        layerId: node.LayerId,
                        hasOre: hasOre,
                        oreId: oreId,
                        itemId: itemId,
                        requiredToolId: requiredTool,
                        requiredToolTier: requiredTier);
                    return true;
                }
            }

            state = default;
            return false;
        }

        public int CountAvailableNodes()
        {
            lock (_gate)
            {
                return _nodes.Values.Count(n => n.Active && n.RemainingHits > 0);
            }
        }

        public MiningSystemState CaptureState()
        {
            lock (_gate)
            {
                var state = new MiningSystemState
                {
                    currentWorldDay = _currentWorldDay,
                    nodes = new List<MiningNodeRecord>(_nodes.Count)
                };

                foreach (var node in _nodes.Values)
                {
                    state.nodes.Add(new MiningNodeRecord
                    {
                        id = node.Id.Value,
                        layerId = node.LayerId,
                        active = node.Active,
                        oreId = node.ActiveOre?.Id,
                        nextRespawnDay = node.NextRespawnDay,
                        remainingHits = node.RemainingHits
                    });
                }

                return state;
            }
        }

        public void ApplyState(MiningSystemState state)
        {
            if (state == null)
                return;

            lock (_gate)
            {
                _currentWorldDay = state.currentWorldDay;
                if (state.nodes == null)
                    return;

                foreach (var record in state.nodes)
                {
                    if (string.IsNullOrWhiteSpace(record.id))
                        continue;
                    var id = new ThingId(record.id);
                    if (!_nodes.TryGetValue(id, out var node))
                        continue;

                    node.Active = record.active;
                    node.NextRespawnDay = record.nextRespawnDay;
                    node.RemainingHits = Math.Max(0, record.remainingHits);

                    if (!string.IsNullOrWhiteSpace(record.oreId) && _oresById.TryGetValue(record.oreId, out var ore))
                    {
                        if (_layersById.TryGetValue(node.LayerId, out var layer) && layer.Ores.Contains(ore))
                        {
                            node.ActiveOre = ore;
                        }
                        else
                        {
                            node.ActiveOre = null;
                            node.Active = false;
                            node.RemainingHits = 0;
                        }
                    }
                    else
                    {
                        node.ActiveOre = null;
                        node.Active = false;
                        node.RemainingHits = 0;
                    }

                    if (node.Active && node.RemainingHits <= 0)
                    {
                        node.Active = false;
                        node.ActiveOre = null;
                    }
                }
            }
        }

        private MiningOreDefinition ChooseOreForNode(MiningNodeState node, string season, string weatherId)
        {
            if (!_layersById.TryGetValue(node.LayerId, out var layer))
                return null;
            if (!layer.AllowsBiomes(node.Biomes))
                return null;

            var candidates = new List<(MiningOreDefinition ore, double weight)>();
            foreach (var ore in layer.Ores)
            {
                if (!ore.AllowsSeason(season))
                    continue;
                if (!ore.AllowsWeather(weatherId))
                    continue;
                if (!ore.AllowsBiome(node.Biomes))
                    continue;
                candidates.Add((ore, ore.Weight));
            }

            if (candidates.Count == 0)
                return null;
            if (candidates.Count == 1)
                return candidates[0].ore;

            double totalWeight = candidates.Sum(c => c.weight);
            if (totalWeight <= 0)
                return candidates[_rng.Next(candidates.Count)].ore;

            double sample = _rng.NextDouble() * totalWeight;
            double cumulative = 0.0;
            foreach (var (ore, weight) in candidates)
            {
                cumulative += weight;
                if (sample <= cumulative)
                    return ore;
            }

            return candidates.Last().ore;
        }
    }

    public sealed class MiningSystemState
    {
        public double currentWorldDay { get; set; }
        public List<MiningNodeRecord> nodes { get; set; }
    }

    public sealed class MiningNodeRecord
    {
        public string id { get; set; }
        public string layerId { get; set; }
        public bool active { get; set; }
        public string oreId { get; set; }
        public double nextRespawnDay { get; set; }
        public int remainingHits { get; set; }
    }
}
