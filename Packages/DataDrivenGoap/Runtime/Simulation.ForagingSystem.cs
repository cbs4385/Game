using System;
using System.Collections.Generic;
using System.Linq;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;

namespace DataDrivenGoap.Simulation
{
    public readonly struct ForagingOperationResult
    {
        public bool Success { get; }
        public IReadOnlyList<InventoryDelta> InventoryChanges { get; }
        public string ResourceId { get; }
        public string ItemId { get; }
        public int Quantity { get; }
        public string SkillId { get; }
        public double SkillXp { get; }

        public ForagingOperationResult(bool success, IReadOnlyList<InventoryDelta> inventoryChanges, string resourceId, string itemId, int quantity, string skillId, double skillXp)
        {
            Success = success;
            InventoryChanges = inventoryChanges ?? Array.Empty<InventoryDelta>();
            ResourceId = resourceId ?? string.Empty;
            ItemId = itemId ?? string.Empty;
            Quantity = quantity;
            SkillId = skillId ?? string.Empty;
            SkillXp = double.IsFinite(skillXp) ? Math.Max(0.0, skillXp) : 0.0;
        }

        public static ForagingOperationResult Failed => new ForagingOperationResult(false, Array.Empty<InventoryDelta>(), string.Empty, string.Empty, 0, string.Empty, 0.0);
    }

    public sealed class ForagingSystem : IForagingQuery
    {
        [Flags]
        private enum ForageBiome
        {
            None = 0,
            Forest = 1 << 0,
            Coast = 1 << 1
        }

        private sealed class ForagingResourceDefinition
        {
            public string Id { get; }
            public string ItemId { get; }
            public int MinQuantity { get; }
            public int MaxQuantity { get; }
            public HashSet<string> Seasons { get; }
            public HashSet<string> Weather { get; }
            public ForageBiome Biomes { get; }
            public double Weight { get; }
            public int GathersPerSpot { get; }
            public string SkillId { get; }
            public double SkillXp { get; }

            public ForagingResourceDefinition(ForagingResourceConfig cfg)
            {
                if (cfg == null)
                    throw new ArgumentNullException(nameof(cfg));

                if (string.IsNullOrWhiteSpace(cfg.id))
                    throw new ArgumentException("Foraging resources must define a non-empty id.", nameof(ForagingResourceConfig.id));
                Id = cfg.id.Trim();

                if (string.IsNullOrWhiteSpace(cfg.itemId))
                    throw new ArgumentException("Foraging resources must define a non-empty itemId.", nameof(ForagingResourceConfig.itemId));
                ItemId = cfg.itemId.Trim();

                if (!cfg.minQuantity.HasValue)
                    throw new ArgumentException("Foraging resources must define a minimum quantity.", nameof(ForagingResourceConfig.minQuantity));
                int min = cfg.minQuantity.Value;
                if (min <= 0)
                    throw new ArgumentOutOfRangeException(nameof(cfg.minQuantity), min, "Minimum quantity must be positive.");

                if (!cfg.maxQuantity.HasValue)
                    throw new ArgumentException("Foraging resources must define a maximum quantity.", nameof(ForagingResourceConfig.maxQuantity));
                int max = cfg.maxQuantity.Value;
                if (max < min)
                    throw new ArgumentOutOfRangeException(nameof(cfg.maxQuantity), max, "Maximum quantity must be greater than or equal to the minimum quantity.");
                MinQuantity = min;
                MaxQuantity = max;

                Seasons = BuildSet(cfg.seasons);
                Weather = BuildSet(cfg.weather);
                Biomes = BuildBiomes(cfg.biomes);

                if (!cfg.weight.HasValue)
                    throw new ArgumentException("Foraging resources must define a weight.", nameof(ForagingResourceConfig.weight));
                double weight = cfg.weight.Value;
                if (!double.IsFinite(weight) || weight <= 0)
                    throw new ArgumentOutOfRangeException(nameof(cfg.weight), weight, "Weight must be a positive, finite value.");
                Weight = weight;

                if (!cfg.gathersPerSpot.HasValue)
                    throw new ArgumentException("Foraging resources must define gathers per spot.", nameof(ForagingResourceConfig.gathersPerSpot));
                int gathers = cfg.gathersPerSpot.Value;
                if (gathers <= 0)
                    throw new ArgumentOutOfRangeException(nameof(cfg.gathersPerSpot), gathers, "Gathers per spot must be positive.");
                GathersPerSpot = gathers;

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

            public bool AllowsBiome(ForageBiome biome)
            {
                if (Biomes == ForageBiome.None)
                    return true;
                return (Biomes & biome) != 0;
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
                foreach (var v in values)
                {
                    if (string.IsNullOrWhiteSpace(v))
                        continue;
                    set.Add(v.Trim().ToLowerInvariant());
                }
                return set;
            }

            private static ForageBiome BuildBiomes(IEnumerable<string> values)
            {
                if (values == null)
                    return ForageBiome.None;
                ForageBiome biome = ForageBiome.None;
                foreach (var v in values)
                {
                    if (string.IsNullOrWhiteSpace(v))
                        continue;
                    var text = v.Trim().ToLowerInvariant();
                    if (text == "forest" || text == "woods")
                        biome |= ForageBiome.Forest;
                    else if (text == "coast" || text == "coastal" || text == "shore" || text == "beach")
                        biome |= ForageBiome.Coast;
                }
                return biome;
            }
        }

        private sealed class ForagingSpotState
        {
            public ThingId Id { get; }
            public GridPos Position { get; }
            public ForageBiome Biomes { get; }
            public bool Active { get; set; }
            public ForagingResourceDefinition ActiveResource { get; set; }
            public double NextRespawnDay { get; set; }
            public int RemainingGathers { get; set; }

            public ForagingSpotState(ThingId id, GridPos position, ForageBiome biomes)
            {
                Id = id;
                Position = position;
                Biomes = biomes;
                Active = false;
                ActiveResource = null;
                NextRespawnDay = 0.0;
                RemainingGathers = 0;
            }
        }

        private readonly object _gate = new object();
        private readonly Dictionary<ThingId, ForagingSpotState> _spots = new Dictionary<ThingId, ForagingSpotState>();
        private readonly List<ForagingResourceDefinition> _resources;
        private readonly Dictionary<string, ForagingResourceDefinition> _resourcesById;
        private readonly Random _rng;
        private readonly bool _enabled;
        private readonly double _respawnIntervalDays;
        private readonly int _maxActiveSpots;
        private readonly double _forestDensityPer100Tiles;
        private readonly double _coastDensityPer100Tiles;
        private readonly SkillProgressionSystem _skillProgression;

        private double _currentWorldDay;

        public ForagingSystem(ForagingSystemConfig config, int rngSeed, SkillProgressionSystem skillProgression = null)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            _enabled = config.enabled;

            if (!double.IsFinite(config.respawnHours))
                throw new ArgumentOutOfRangeException(nameof(config.respawnHours), config.respawnHours, "Respawn hours must be a positive, finite value.");
            if (config.respawnHours < 1.0)
                throw new ArgumentOutOfRangeException(nameof(config.respawnHours), config.respawnHours, "Respawn hours must be at least one hour.");
            _respawnIntervalDays = config.respawnHours / 24.0;

            if (config.maxActiveSpots < 0)
                throw new ArgumentOutOfRangeException(nameof(config.maxActiveSpots), config.maxActiveSpots, "Max active spots cannot be negative.");
            _maxActiveSpots = config.maxActiveSpots;

            if (!double.IsFinite(config.forestSpotsPer100Tiles))
                throw new ArgumentOutOfRangeException(nameof(config.forestSpotsPer100Tiles), config.forestSpotsPer100Tiles, "Forest spot density must be a finite, non-negative value.");
            if (config.forestSpotsPer100Tiles < 0)
                throw new ArgumentOutOfRangeException(nameof(config.forestSpotsPer100Tiles), config.forestSpotsPer100Tiles, "Forest spot density must not be negative.");
            _forestDensityPer100Tiles = config.forestSpotsPer100Tiles;

            if (!double.IsFinite(config.coastSpotsPer100Tiles))
                throw new ArgumentOutOfRangeException(nameof(config.coastSpotsPer100Tiles), config.coastSpotsPer100Tiles, "Coast spot density must be a finite, non-negative value.");
            if (config.coastSpotsPer100Tiles < 0)
                throw new ArgumentOutOfRangeException(nameof(config.coastSpotsPer100Tiles), config.coastSpotsPer100Tiles, "Coast spot density must not be negative.");
            _coastDensityPer100Tiles = config.coastSpotsPer100Tiles;

            _rng = new Random(rngSeed ^ 0x5a2194c3);
            _skillProgression = skillProgression;

            if (config.resources == null)
                throw new ArgumentException("Foraging system must define at least one resource.", nameof(ForagingSystemConfig.resources));

            _resources = new List<ForagingResourceDefinition>(config.resources.Length);
            _resourcesById = new Dictionary<string, ForagingResourceDefinition>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < config.resources.Length; i++)
            {
                var resourceConfig = config.resources[i];
                if (resourceConfig == null)
                    throw new ArgumentException($"Foraging resources cannot contain null entries (index {i}).", nameof(ForagingSystemConfig.resources));

                var def = new ForagingResourceDefinition(resourceConfig);
                _resources.Add(def);
                _resourcesById[def.Id] = def;
            }

            if (_resources.Count == 0)
                throw new ArgumentException("Foraging system must define at least one resource.", nameof(ForagingSystemConfig.resources));
        }

        public double ForestSpotDensityPer100Tiles => _forestDensityPer100Tiles;
        public double CoastSpotDensityPer100Tiles => _coastDensityPer100Tiles;

        public void RegisterSpot(ThingId id, GridPos position, bool forestBiome, bool coastBiome)
        {
            if (string.IsNullOrWhiteSpace(id.Value))
                return;

            ForageBiome biome = ForageBiome.None;
            if (forestBiome)
                biome |= ForageBiome.Forest;
            if (coastBiome)
                biome |= ForageBiome.Coast;
            if (biome == ForageBiome.None)
            {
                // If callers do not specify a biome we treat the spot as being
                // available to both forest and coastal resources.  Previously
                // this silently defaulted to "forest", which made it very easy
                // to misconfigure custom maps and unintentionally block
                // coastal-only forageables from ever spawning.  Falling back to
                // the union keeps the system permissive while still allowing
                // biome-restricted resources to filter themselves out.
                biome = ForageBiome.Forest | ForageBiome.Coast;
            }

            lock (_gate)
            {
                if (_spots.ContainsKey(id))
                    return;
                _spots[id] = new ForagingSpotState(id, position, biome);
            }
        }

        public void ClearSpots()
        {
            lock (_gate)
            {
                _spots.Clear();
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
                int activeCount = _spots.Values.Count(s => s.Active && s.RemainingGathers > 0);
                foreach (var spot in _spots.Values)
                {
                    if (spot.Active && spot.RemainingGathers > 0)
                        continue;
                    if (_maxActiveSpots > 0 && activeCount >= _maxActiveSpots)
                        break;
                    if (_currentWorldDay < spot.NextRespawnDay)
                        continue;

                    var resource = ChooseResourceForSpot(spot, season, weatherId);
                    if (resource == null)
                        continue;

                    spot.ActiveResource = resource;
                    spot.Active = true;
                    spot.RemainingGathers = Math.Max(1, resource.GathersPerSpot);
                    activeCount++;
                }
            }
        }

        public ForagingOperationResult Apply(ForagingOperation operation)
        {
            ThingId xpActor = default;
            string xpSkill = null;
            double xpAmount = 0.0;
            ForagingOperationResult result = ForagingOperationResult.Failed;

            lock (_gate)
            {
                if (!_enabled)
                    goto Exit;
                if (!_spots.TryGetValue(operation.Spot, out var spot))
                    goto Exit;
                if (!spot.Active || spot.RemainingGathers <= 0)
                    goto Exit;
                if (spot.ActiveResource == null || string.IsNullOrWhiteSpace(spot.ActiveResource.ItemId))
                    goto Exit;

                int quantity = spot.ActiveResource.SampleQuantity(_rng);
                if (quantity <= 0)
                    goto Exit;

                spot.RemainingGathers--;
                if (spot.RemainingGathers <= 0)
                {
                    spot.Active = false;
                    spot.NextRespawnDay = _currentWorldDay + _respawnIntervalDays;
                }

                var delta = new InventoryDelta(operation.Actor, spot.ActiveResource.ItemId, quantity, remove: false);
                result = new ForagingOperationResult(true, new[] { delta }, spot.ActiveResource.Id, spot.ActiveResource.ItemId, quantity, spot.ActiveResource.SkillId, spot.ActiveResource.SkillXp);
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

        public bool TryGet(ThingId spotId, out ForageSpotSnapshot state)
        {
            lock (_gate)
            {
                if (_spots.TryGetValue(spotId, out var spot))
                {
                    bool hasResource = spot.Active && spot.RemainingGathers > 0 && spot.ActiveResource != null;
                    string resourceId = hasResource ? spot.ActiveResource.Id : string.Empty;
                    string itemId = hasResource ? spot.ActiveResource.ItemId ?? string.Empty : string.Empty;
                    state = new ForageSpotSnapshot(
                        true,
                        (spot.Biomes & ForageBiome.Forest) != 0,
                        (spot.Biomes & ForageBiome.Coast) != 0,
                        hasResource,
                        resourceId,
                        itemId);
                    return true;
                }
            }

            state = default;
            return false;
        }

        public int CountAvailableSpots()
        {
            lock (_gate)
            {
                return _spots.Values.Count(s => s.Active && s.RemainingGathers > 0);
            }
        }

        public ForagingSystemState CaptureState()
        {
            lock (_gate)
            {
                var state = new ForagingSystemState
                {
                    currentWorldDay = _currentWorldDay,
                    spots = new List<ForagingSpotRecord>(_spots.Count)
                };

                foreach (var spot in _spots.Values)
                {
                    state.spots.Add(new ForagingSpotRecord
                    {
                        id = spot.Id.Value,
                        active = spot.Active,
                        resourceId = spot.ActiveResource?.Id,
                        nextRespawnDay = spot.NextRespawnDay,
                        remainingGathers = spot.RemainingGathers
                    });
                }

                return state;
            }
        }

        public void ApplyState(ForagingSystemState state)
        {
            if (state == null)
                return;

            lock (_gate)
            {
                _currentWorldDay = state.currentWorldDay;
                if (state.spots == null)
                    return;

                foreach (var record in state.spots)
                {
                    if (string.IsNullOrWhiteSpace(record.id))
                        continue;

                    var id = new ThingId(record.id);
                    if (!_spots.TryGetValue(id, out var spot))
                        continue;

                    spot.NextRespawnDay = record.nextRespawnDay;
                    spot.RemainingGathers = Math.Max(0, record.remainingGathers);

                    if (!record.active || spot.RemainingGathers <= 0)
                    {
                        spot.Active = false;
                        spot.ActiveResource = null;
                        spot.RemainingGathers = Math.Max(0, spot.RemainingGathers);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(record.resourceId)
                        && _resourcesById.TryGetValue(record.resourceId, out var resource)
                        && resource.AllowsBiome(spot.Biomes))
                    {
                        spot.Active = true;
                        spot.ActiveResource = resource;
                        if (spot.RemainingGathers <= 0)
                        {
                            spot.Active = false;
                            spot.ActiveResource = null;
                        }
                    }
                    else
                    {
                        spot.Active = false;
                        spot.ActiveResource = null;
                        spot.RemainingGathers = 0;
                    }
                }
            }
        }

        private ForagingResourceDefinition ChooseResourceForSpot(ForagingSpotState spot, string season, string weatherId)
        {
            if (_resources.Count == 0)
                return null;

            var candidates = new List<(ForagingResourceDefinition def, double weight)>();
            foreach (var res in _resources)
            {
                if (!res.AllowsSeason(season))
                    continue;
                if (!res.AllowsWeather(weatherId))
                    continue;
                if (!res.AllowsBiome(spot.Biomes))
                    continue;
                candidates.Add((res, res.Weight));
            }

            if (candidates.Count == 0)
                return null;
            if (candidates.Count == 1)
                return candidates[0].def;

            double totalWeight = candidates.Sum(c => c.weight);
            if (totalWeight <= 0)
                return candidates[_rng.Next(candidates.Count)].def;

            double sample = _rng.NextDouble() * totalWeight;
            double cumulative = 0.0;
            foreach (var (def, weight) in candidates)
            {
                cumulative += weight;
                if (sample <= cumulative)
                    return def;
            }

            return candidates.Last().def;
        }
    }

    public sealed class ForagingSystemState
    {
        public double currentWorldDay { get; set; }
        public List<ForagingSpotRecord> spots { get; set; }
    }

    public sealed class ForagingSpotRecord
    {
        public string id { get; set; }
        public bool active { get; set; }
        public string resourceId { get; set; }
        public double nextRespawnDay { get; set; }
        public int remainingGathers { get; set; }
    }
}
