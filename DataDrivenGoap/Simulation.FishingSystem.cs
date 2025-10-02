using System;
using System.Collections.Generic;
using System.Linq;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;

namespace DataDrivenGoap.Simulation
{
    public readonly struct FishingOperationResult
    {
        public bool Success { get; }
        public IReadOnlyList<InventoryDelta> InventoryChanges { get; }
        public string CatchId { get; }
        public string ItemId { get; }
        public int Quantity { get; }
        public string SkillId { get; }
        public double SkillXp { get; }

        public FishingOperationResult(bool success, IReadOnlyList<InventoryDelta> inventoryChanges, string catchId, string itemId, int quantity, string skillId, double skillXp)
        {
            Success = success;
            InventoryChanges = inventoryChanges ?? Array.Empty<InventoryDelta>();
            CatchId = catchId ?? string.Empty;
            ItemId = itemId ?? string.Empty;
            Quantity = quantity;
            SkillId = skillId ?? string.Empty;
            SkillXp = double.IsFinite(skillXp) ? Math.Max(0.0, skillXp) : 0.0;
        }

        public static FishingOperationResult Failed => new FishingOperationResult(false, Array.Empty<InventoryDelta>(), string.Empty, string.Empty, 0, string.Empty, 0.0);
    }

    public sealed class FishingSystem : IFishingQuery
    {
        private sealed class FishingCatchDefinition
        {
            public string Id { get; }
            public string ItemId { get; }
            public int MinQuantity { get; }
            public int MaxQuantity { get; }
            public HashSet<string> Seasons { get; }
            public HashSet<string> Weather { get; }
            public bool ShallowOnly { get; }
            public bool DeepOnly { get; }
            public double Weight { get; }
            public string BaitItemId { get; }
            public int CastsPerSpot { get; }
            public string SkillId { get; }
            public double SkillXp { get; }

            public FishingCatchDefinition(FishingCatchConfig cfg)
            {
                if (cfg == null)
                    throw new ArgumentNullException(nameof(cfg));

                if (string.IsNullOrWhiteSpace(cfg.id))
                    throw new ArgumentException("Fishing catch id is required.", nameof(cfg));

                Id = cfg.id.Trim();

                if (string.IsNullOrWhiteSpace(cfg.itemId))
                    throw new ArgumentException("Fishing catch item id is required.", nameof(cfg));

                ItemId = cfg.itemId.Trim();

                if (!cfg.minQuantity.HasValue)
                    throw new ArgumentException("Fishing catch minimum quantity is required.", nameof(cfg));

                int min = cfg.minQuantity.Value;
                if (min <= 0)
                    throw new ArgumentOutOfRangeException(nameof(cfg.minQuantity), "Catch minimum quantity must be positive.");

                if (!cfg.maxQuantity.HasValue)
                    throw new ArgumentException("Fishing catch maximum quantity is required.", nameof(cfg));

                int max = cfg.maxQuantity.Value;
                if (max <= 0)
                    throw new ArgumentOutOfRangeException(nameof(cfg.maxQuantity), "Catch maximum quantity must be positive.");
                if (max < min)
                    throw new ArgumentOutOfRangeException(nameof(cfg.maxQuantity), "Catch maximum quantity cannot be less than minimum quantity.");
                MinQuantity = min;
                MaxQuantity = max;

                Seasons = BuildSet(cfg.seasons);
                Weather = BuildSet(cfg.weather);

                ShallowOnly = cfg.shallowOnly;
                DeepOnly = cfg.deepOnly;

                if (!cfg.weight.HasValue)
                    throw new ArgumentException("Fishing catch weight is required.", nameof(cfg));

                double weight = cfg.weight.Value;
                if (!double.IsFinite(weight) || weight <= 0)
                    throw new ArgumentOutOfRangeException(nameof(cfg.weight), "Catch weight must be a finite, positive value.");
                Weight = weight;

                BaitItemId = string.IsNullOrWhiteSpace(cfg.baitItemId) ? null : cfg.baitItemId.Trim();
                if (!cfg.castsPerSpot.HasValue)
                    throw new ArgumentException("Fishing catch casts per spot is required.", nameof(cfg));

                int casts = cfg.castsPerSpot.Value;
                if (casts <= 0)
                    throw new ArgumentOutOfRangeException(nameof(cfg.castsPerSpot), "Casts per spot must be positive.");
                CastsPerSpot = casts;

                SkillId = string.IsNullOrWhiteSpace(cfg.skill) ? null : cfg.skill.Trim();
                if (cfg.skillXp.HasValue)
                {
                    double xp = cfg.skillXp.Value;
                    if (!double.IsFinite(xp) || xp < 0.0)
                        throw new ArgumentOutOfRangeException(nameof(cfg.skillXp), "Fishing catch skillXp must be a non-negative finite value when provided.");
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

            public bool AllowsDepth(bool isShallow)
            {
                if (ShallowOnly && DeepOnly)
                    return true;
                if (ShallowOnly)
                    return isShallow;
                if (DeepOnly)
                    return !isShallow;
                return true;
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
        }

        private sealed class FishingSpotState
        {
            public ThingId Id { get; }
            public GridPos Position { get; }
            public bool IsShallow { get; }
            public bool Active { get; set; }
            public FishingCatchDefinition ActiveCatch { get; set; }
            public double NextRespawnDay { get; set; }
            public int RemainingCasts { get; set; }

            public FishingSpotState(ThingId id, GridPos position, bool isShallow)
            {
                Id = id;
                Position = position;
                IsShallow = isShallow;
                Active = false;
                ActiveCatch = null;
                NextRespawnDay = 0.0;
                RemainingCasts = 0;
            }
        }

        private readonly object _gate = new object();
        private readonly Dictionary<ThingId, FishingSpotState> _spots = new Dictionary<ThingId, FishingSpotState>();
        private readonly List<FishingCatchDefinition> _catches;
        private readonly Dictionary<string, FishingCatchDefinition> _catchesById;
        private readonly Random _rng;
        private readonly bool _enabled;
        private readonly double _respawnIntervalDays;
        private readonly int _maxActiveSpots;
        private readonly double _spotDensityPer100Tiles;
        private readonly SkillProgressionSystem _skillProgression;

        private double _currentWorldDay;

        public FishingSystem(FishingSystemConfig config, int rngSeed, SkillProgressionSystem skillProgression = null)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            if (config.catches == null || config.catches.Length == 0)
                throw new ArgumentException("Fishing catch configuration list cannot be null or empty.", nameof(config));

            _enabled = config.enabled;
            if (_enabled)
            {
                double respawnHours = config.respawnHours;
                if (!double.IsFinite(respawnHours) || respawnHours <= 0)
                    throw new ArgumentOutOfRangeException(nameof(config.respawnHours), "Fishing respawn hours must be a finite, positive value.");
                _respawnIntervalDays = respawnHours / 24.0;

                if (!double.IsFinite(config.spotsPer100Tiles) || config.spotsPer100Tiles <= 0)
                    throw new ArgumentOutOfRangeException(nameof(config.spotsPer100Tiles), "Fishing spot density must be a finite, positive value.");
                _spotDensityPer100Tiles = config.spotsPer100Tiles;

                if (config.maxActiveSpots <= 0)
                    throw new ArgumentOutOfRangeException(nameof(config.maxActiveSpots), "Max active fishing spots must be positive.");
                _maxActiveSpots = config.maxActiveSpots;
            }
            else
            {
                _respawnIntervalDays = 0.0;
                _spotDensityPer100Tiles = 0.0;
                _maxActiveSpots = 0;
            }
            _rng = new Random(rngSeed);
            _skillProgression = skillProgression;

            _catches = new List<FishingCatchDefinition>();
            _catchesById = new Dictionary<string, FishingCatchDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var cfg in config.catches)
            {
                if (cfg == null)
                    throw new ArgumentException("Fishing catch configuration entries cannot be null.", nameof(config));

                var def = new FishingCatchDefinition(cfg);
                _catches.Add(def);
                _catchesById[def.Id] = def;
            }
        }

        public double SpotDensityPer100Tiles => _spotDensityPer100Tiles;

        public void RegisterSpot(ThingId id, GridPos position, bool isShallow)
        {
            if (string.IsNullOrWhiteSpace(id.Value))
                return;

            lock (_gate)
            {
                if (_spots.ContainsKey(id))
                    return;
                _spots[id] = new FishingSpotState(id, position, isShallow);
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
                int activeCount = _spots.Values.Count(s => s.Active && s.RemainingCasts > 0);
                foreach (var spot in _spots.Values)
                {
                    if (!spot.Active || spot.RemainingCasts <= 0)
                    {
                        if (_maxActiveSpots > 0 && activeCount >= _maxActiveSpots)
                            continue;
                        if (_currentWorldDay < spot.NextRespawnDay)
                            continue;

                        var catchDef = ChooseCatchForSpot(spot, season, weatherId);
                        if (catchDef == null)
                        {
                            spot.Active = false;
                            spot.ActiveCatch = null;
                            spot.RemainingCasts = 0;
                            continue;
                        }

                        spot.Active = true;
                        spot.ActiveCatch = catchDef;
                        spot.RemainingCasts = catchDef.CastsPerSpot;
                        activeCount++;
                        continue;
                    }

                    // Ensure the active catch is still allowed in the new conditions.
                    if (spot.ActiveCatch != null)
                    {
                        bool allowed = spot.ActiveCatch.AllowsSeason(season) && spot.ActiveCatch.AllowsWeather(weatherId) && spot.ActiveCatch.AllowsDepth(spot.IsShallow);
                        if (!allowed)
                        {
                            spot.Active = false;
                            spot.ActiveCatch = null;
                            spot.RemainingCasts = 0;
                            activeCount = Math.Max(0, activeCount - 1);
                        }
                    }
                }
            }
        }

        public FishingOperationResult Apply(FishingOperation operation)
        {
            ThingId xpActor = default;
            string xpSkill = null;
            double xpAmount = 0.0;
            FishingOperationResult result = FishingOperationResult.Failed;

            if (!_enabled)
                return FishingOperationResult.Failed;

            if (operation.Spot.Equals(default(ThingId)))
                return FishingOperationResult.Failed;

            lock (_gate)
            {
                if (!_spots.TryGetValue(operation.Spot, out var spot))
                    goto Exit;
                if (!spot.Active || spot.ActiveCatch == null || spot.RemainingCasts <= 0)
                    goto Exit;

                var catchDef = spot.ActiveCatch;
                if (!catchDef.AllowsDepth(spot.IsShallow))
                {
                    spot.Active = false;
                    spot.ActiveCatch = null;
                    spot.RemainingCasts = 0;
                    goto Exit;
                }

                if (!string.IsNullOrWhiteSpace(catchDef.BaitItemId))
                {
                    if (string.IsNullOrWhiteSpace(operation.BaitItemId))
                        goto Exit;
                    if (!string.Equals(catchDef.BaitItemId, operation.BaitItemId, StringComparison.OrdinalIgnoreCase))
                        goto Exit;
                }

                var changes = new List<InventoryDelta>();
                string baitId = string.IsNullOrWhiteSpace(operation.BaitItemId) ? null : operation.BaitItemId.Trim();
                int baitQuantity = operation.BaitQuantity > 0 ? operation.BaitQuantity : 1;
                if (!string.IsNullOrWhiteSpace(baitId))
                    changes.Add(new InventoryDelta(operation.Actor, baitId, baitQuantity, true));

                int quantity = catchDef.SampleQuantity(_rng);
                if (!string.IsNullOrWhiteSpace(catchDef.ItemId) && quantity > 0)
                    changes.Add(new InventoryDelta(operation.Actor, catchDef.ItemId, quantity, false));

                spot.RemainingCasts--;
                if (spot.RemainingCasts <= 0)
                {
                    spot.Active = false;
                    spot.ActiveCatch = null;
                    spot.NextRespawnDay = _currentWorldDay + _respawnIntervalDays;
                    spot.RemainingCasts = 0;
                }

                result = new FishingOperationResult(true, changes, catchDef.Id, catchDef.ItemId, quantity, catchDef.SkillId, catchDef.SkillXp);
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

        public bool TryGet(ThingId spotId, out FishingSpotSnapshot state)
        {
            lock (_gate)
            {
                if (_spots.TryGetValue(spotId, out var spot))
                {
                    state = new FishingSpotSnapshot(true, spot.IsShallow, spot.Active && spot.RemainingCasts > 0, spot.ActiveCatch?.Id ?? string.Empty, spot.ActiveCatch?.ItemId ?? string.Empty);
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
                return _spots.Values.Count(s => s.Active && s.RemainingCasts > 0);
            }
        }

        public FishingSystemState CaptureState()
        {
            lock (_gate)
            {
                var state = new FishingSystemState
                {
                    currentWorldDay = _currentWorldDay,
                    spots = new List<FishingSpotRecord>(_spots.Count)
                };

                foreach (var spot in _spots.Values)
                {
                    state.spots.Add(new FishingSpotRecord
                    {
                        id = spot.Id.Value,
                        isShallow = spot.IsShallow,
                        active = spot.Active,
                        catchId = spot.ActiveCatch?.Id,
                        nextRespawnDay = spot.NextRespawnDay,
                        remainingCasts = spot.RemainingCasts
                    });
                }

                return state;
            }
        }

        public void ApplyState(FishingSystemState state)
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
                    spot.RemainingCasts = Math.Max(0, record.remainingCasts);

                    if (!record.active || spot.RemainingCasts <= 0)
                    {
                        spot.Active = false;
                        spot.ActiveCatch = null;
                        spot.RemainingCasts = Math.Max(0, spot.RemainingCasts);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(record.catchId)
                        && _catchesById.TryGetValue(record.catchId, out var catchDef)
                        && catchDef.AllowsDepth(spot.IsShallow))
                    {
                        spot.Active = true;
                        spot.ActiveCatch = catchDef;
                        if (spot.RemainingCasts <= 0)
                        {
                            spot.Active = false;
                            spot.ActiveCatch = null;
                        }
                    }
                    else
                    {
                        spot.Active = false;
                        spot.ActiveCatch = null;
                        spot.RemainingCasts = 0;
                    }
                }
            }
        }

        private FishingCatchDefinition ChooseCatchForSpot(FishingSpotState spot, string season, string weatherId)
        {
            if (_catches == null || _catches.Count == 0)
                return null;

            var candidates = new List<FishingCatchDefinition>();
            double totalWeight = 0.0;
            foreach (var def in _catches)
            {
                if (def == null)
                    continue;
                if (!def.AllowsDepth(spot.IsShallow))
                    continue;
                if (!def.AllowsSeason(season))
                    continue;
                if (!def.AllowsWeather(weatherId))
                    continue;
                candidates.Add(def);
                totalWeight += def.Weight;
            }

            if (candidates.Count == 0 || totalWeight <= 0)
                return null;

            double roll = _rng.NextDouble() * totalWeight;
            double sum = 0.0;
            foreach (var def in candidates)
            {
                sum += def.Weight;
                if (roll <= sum)
                    return def;
            }

            return candidates[candidates.Count - 1];
        }
    }

    public sealed class FishingSystemState
    {
        public double currentWorldDay { get; set; }
        public List<FishingSpotRecord> spots { get; set; }
    }

    public sealed class FishingSpotRecord
    {
        public string id { get; set; }
        public bool isShallow { get; set; }
        public bool active { get; set; }
        public string catchId { get; set; }
        public double nextRespawnDay { get; set; }
        public int remainingCasts { get; set; }
    }
}
