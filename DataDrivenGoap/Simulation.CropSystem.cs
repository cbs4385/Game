using System;
using System.Collections.Generic;
using System.Linq;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;
using DataDrivenGoap.Persistence;

namespace DataDrivenGoap.Simulation
{
    public readonly struct CropHarvestYield
    {
        public string ItemId { get; }
        public int Quantity { get; }

        public CropHarvestYield(string itemId, int quantity)
        {
            ItemId = itemId ?? string.Empty;
            Quantity = quantity;
        }
    }

    public readonly struct CropOperationResult
    {
        public bool Success { get; }
        public IReadOnlyList<InventoryDelta> InventoryChanges { get; }
        public IReadOnlyList<CropHarvestYield> HarvestYields { get; }

        public CropOperationResult(bool success, IReadOnlyList<InventoryDelta> inventoryChanges, IReadOnlyList<CropHarvestYield> harvestYields)
        {
            Success = success;
            InventoryChanges = inventoryChanges ?? Array.Empty<InventoryDelta>();
            HarvestYields = harvestYields ?? Array.Empty<CropHarvestYield>();
        }

        public static CropOperationResult Failed => new CropOperationResult(false, Array.Empty<InventoryDelta>(), Array.Empty<CropHarvestYield>());
    }

    public sealed class CropSystem : ICropQuery
    {
        private sealed class CropYieldDefinition
        {
            public string ItemId { get; }
            public int Min { get; }
            public int Max { get; }

            public CropYieldDefinition(CropYieldConfig cfg)
            {
                if (cfg == null)
                    throw new ArgumentNullException(nameof(cfg));
                if (string.IsNullOrWhiteSpace(cfg.item))
                    throw new ArgumentException("Crop yield must specify an item id", nameof(CropYieldConfig.item));
                if (cfg.min <= 0)
                    throw new ArgumentOutOfRangeException(nameof(CropYieldConfig.min), "Crop yield minimum quantity must be positive");
                if (cfg.max < cfg.min)
                    throw new ArgumentException("Crop yield maximum quantity must be greater than or equal to the minimum", nameof(CropYieldConfig.max));

                ItemId = cfg.item.Trim();
                Min = cfg.min;
                Max = cfg.max;
            }

            public int Sample(Random rng)
            {
                if (rng == null || Min == Max)
                    return Min;
                return rng.Next(Min, Max + 1);
            }
        }

        private sealed class CropDefinition
        {
            public string Id { get; }
            public int[] StageDurations { get; }
            public HashSet<string> Seasons { get; }
            public int? RegrowthDays { get; }
            public bool HydrationNeeded { get; }
            public bool WitherOnSeasonChange { get; }
            public string SeedItemId { get; }
            public CropYieldDefinition Yield { get; }
            public string SkillId { get; }
            public double SkillXp { get; }

            public CropDefinition(CropConfig config)
            {
                if (config == null)
                    throw new ArgumentNullException(nameof(config));
                if (string.IsNullOrWhiteSpace(config.id))
                    throw new ArgumentException("Crop configuration must specify an id", nameof(config));

                Id = config.id.Trim();
                if (config.growthStages == null || config.growthStages.Length == 0)
                    throw new ArgumentException($"Crop '{Id}' must define at least one growth stage", nameof(CropConfig.growthStages));
                if (config.growthStages.Any(v => v <= 0))
                    throw new ArgumentException($"Crop '{Id}' growth stages must all be positive integers", nameof(CropConfig.growthStages));
                StageDurations = config.growthStages.ToArray();

                Seasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var season in config.seasons ?? Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(season))
                        continue;
                    Seasons.Add(season.Trim().ToLowerInvariant());
                }

                if (config.regrowthDays.HasValue && config.regrowthDays.Value > 0)
                    RegrowthDays = config.regrowthDays.Value;

                HydrationNeeded = config.hydrationNeeded;
                WitherOnSeasonChange = config.witheredOnSeasonChange;
                SeedItemId = string.IsNullOrWhiteSpace(config.seedItemId) ? null : config.seedItemId.Trim();
                Yield = new CropYieldDefinition(config.yield);
                if (string.IsNullOrWhiteSpace(config.skill))
                    throw new ArgumentException($"Crop '{Id}' must specify a skill identifier", nameof(CropConfig.skill));
                SkillId = config.skill.Trim();
                SkillXp = config.skillXp;
            }
        }

        private sealed class TileState
        {
            public bool Tilled;
            public string CropId;
            public int Stage;
            public bool Watered;
            public int DaysInStage;
            public ThingId PlantedBy;
            public int RegrowCounter;
            public bool ReadyToHarvest;
            public int UnwateredDays;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, CropDefinition> _crops;
        private readonly Dictionary<GridPos, TileState> _tiles = new Dictionary<GridPos, TileState>();
        private readonly Dictionary<ThingId, GridPos> _plotLookup = new Dictionary<ThingId, GridPos>();
        private readonly Random _rng;
        private ISkillProgression _skillProgression;

        private double _lastProcessedDay = double.NaN;
        private string _lastSeason;
        private bool _pendingWeatherAutoWater;
        private bool _pendingWeatherGrowthPause;

        public CropSystem(IEnumerable<CropConfig> configs, int rngSeed, ISkillProgression skillProgression = null)
        {
            _crops = new Dictionary<string, CropDefinition>(StringComparer.OrdinalIgnoreCase);
            if (configs != null)
            {
                foreach (var cfg in configs)
                {
                    if (cfg == null || string.IsNullOrWhiteSpace(cfg.id))
                        continue;
                    var def = new CropDefinition(cfg);
                    _crops[def.Id] = def;
                }
            }

            _rng = new Random(rngSeed);
            _skillProgression = skillProgression;
        }

        public void SetSkillProgression(ISkillProgression progression)
        {
            _skillProgression = progression;
        }

        public void RegisterPlot(ThingId id, GridPos position, bool tilled = false)
        {
            if (string.IsNullOrWhiteSpace(id.Value))
                return;

            lock (_gate)
            {
                _plotLookup[id] = position;
                if (!_tiles.TryGetValue(position, out var state))
                {
                    state = new TileState();
                    _tiles[position] = state;
                }
                if (tilled)
                    state.Tilled = true;
            }
        }

        public CropOperationResult Apply(CropOperation operation)
        {
            ThingId xpActor = default;
            double xpAmount = 0.0;
            string xpSkill = null;
            CropOperationResult result;

            lock (_gate)
            {
                if (!TryGetState(operation.Plot, out var state, create: true))
                    return CropOperationResult.Failed;

                switch (operation.Kind)
                {
                    case CropOperationKind.Till:
                        return ApplyTill(state);
                    case CropOperationKind.Plant:
                        return ApplyPlant(operation, state);
                    case CropOperationKind.Water:
                        return ApplyWater(state);
                    case CropOperationKind.Harvest:
                        result = ApplyHarvest(operation, state, out xpAmount, out xpSkill);
                        if (result.Success && xpAmount > 0.0 && !string.IsNullOrWhiteSpace(xpSkill))
                            xpActor = operation.Actor;
                        break;
                    default:
                        return CropOperationResult.Failed;
                }
            }

            if (xpAmount > 0.0 && !string.IsNullOrWhiteSpace(xpSkill) && !string.IsNullOrWhiteSpace(xpActor.Value))
            {
                var skills = _skillProgression;
                skills?.AddExperience(xpActor, xpSkill, xpAmount);
            }

            return result;
        }

        public void ApplyWeatherEffects(bool autoWaterCrops, bool growthPaused)
        {
            lock (_gate)
            {
                _pendingWeatherAutoWater = autoWaterCrops;
                _pendingWeatherGrowthPause = growthPaused;

                if (!autoWaterCrops)
                    return;

                foreach (var state in _tiles.Values)
                {
                    if (string.IsNullOrEmpty(state.CropId))
                        continue;
                    state.Watered = true;
                    state.UnwateredDays = 0;
                }
            }
        }

        private CropOperationResult ApplyTill(TileState state)
        {
            ResetPlot(state, tilled: true);
            return new CropOperationResult(true, Array.Empty<InventoryDelta>(), Array.Empty<CropHarvestYield>());
        }

        private CropOperationResult ApplyPlant(in CropOperation operation, TileState state)
        {
            var def = ResolveCropForPlant(operation.CropId, operation.SeedItemId);
            if (def == null)
                return CropOperationResult.Failed;
            if (!state.Tilled || !string.IsNullOrEmpty(state.CropId))
                return CropOperationResult.Failed;

            state.CropId = def.Id;
            state.Stage = 0;
            state.DaysInStage = 0;
            state.Watered = false;
            state.ReadyToHarvest = false;
            state.RegrowCounter = 0;
            state.UnwateredDays = 0;
            state.PlantedBy = operation.Actor;

            var removals = new List<InventoryDelta>();
            string seedItem = !string.IsNullOrWhiteSpace(operation.SeedItemId)
                ? operation.SeedItemId
                : def.SeedItemId;
            int quantity = operation.SeedQuantity <= 0 ? 1 : operation.SeedQuantity;
            if (!string.IsNullOrWhiteSpace(seedItem))
            {
                removals.Add(new InventoryDelta(operation.Actor, seedItem, quantity, true));
            }

            IReadOnlyList<InventoryDelta> removalResults = removals.Count > 0
                ? (IReadOnlyList<InventoryDelta>)removals
                : Array.Empty<InventoryDelta>();

            return new CropOperationResult(true, removalResults, Array.Empty<CropHarvestYield>());
        }

        private CropOperationResult ApplyWater(TileState state)
        {
            if (string.IsNullOrEmpty(state.CropId))
                return CropOperationResult.Failed;
            state.Watered = true;
            state.UnwateredDays = 0;
            return new CropOperationResult(true, Array.Empty<InventoryDelta>(), Array.Empty<CropHarvestYield>());
        }

        private CropOperationResult ApplyHarvest(in CropOperation operation, TileState state, out double skillXp, out string skillId)
        {
            skillXp = 0.0;
            skillId = null;
            if (string.IsNullOrEmpty(state.CropId) || !state.ReadyToHarvest)
                return CropOperationResult.Failed;
            if (!_crops.TryGetValue(state.CropId, out var def))
            {
                ResetPlot(state, tilled: false);
                return CropOperationResult.Failed;
            }

            int quantity = def.Yield.Sample(_rng);
            var additions = new List<InventoryDelta>();
            var harvest = new List<CropHarvestYield>();
            if (!string.IsNullOrWhiteSpace(def.Yield.ItemId) && quantity > 0)
            {
                additions.Add(new InventoryDelta(operation.Actor, def.Yield.ItemId, quantity, false));
                harvest.Add(new CropHarvestYield(def.Yield.ItemId, quantity));
            }

            if (def.RegrowthDays.HasValue)
            {
                state.RegrowCounter = Math.Max(0, def.RegrowthDays.Value);
                state.ReadyToHarvest = state.RegrowCounter == 0;
                state.DaysInStage = 0;
                state.Stage = def.StageDurations.Length > 0 ? def.StageDurations.Length - 1 : 0;
                state.Watered = false;
                state.UnwateredDays = 0;
                state.Tilled = true;
            }
            else
            {
                ResetPlot(state, tilled: true);
            }

            IReadOnlyList<InventoryDelta> additionResults = additions.Count > 0
                ? (IReadOnlyList<InventoryDelta>)additions
                : Array.Empty<InventoryDelta>();
            IReadOnlyList<CropHarvestYield> harvestResults = harvest.Count > 0
                ? (IReadOnlyList<CropHarvestYield>)harvest
                : Array.Empty<CropHarvestYield>();

            if (def.SkillXp > 0.0 && !string.IsNullOrWhiteSpace(def.SkillId))
            {
                skillXp = def.SkillXp;
                skillId = def.SkillId;
            }

            return new CropOperationResult(true, additionResults, harvestResults);
        }

        public void Tick(WorldTimeSnapshot time)
        {
            if (time == null)
                return;

            lock (_gate)
            {
                string season = NormalizeSeason(time.SeasonName);
                if (_lastSeason == null)
                {
                    _lastSeason = season;
                }
                else if (!string.Equals(_lastSeason, season, StringComparison.Ordinal))
                {
                    _lastSeason = season;
                    HandleSeasonChange(season);
                }

                double currentDay = Math.Floor(time.TotalWorldDays);
                if (double.IsNaN(_lastProcessedDay))
                {
                    _lastProcessedDay = currentDay;
                }

                while (_lastProcessedDay < currentDay)
                {
                    _lastProcessedDay += 1.0;
                    bool autoWater = _pendingWeatherAutoWater;
                    bool growthPaused = _pendingWeatherGrowthPause;
                    _pendingWeatherAutoWater = false;
                    _pendingWeatherGrowthPause = false;
                    AdvanceDay(season, autoWater, growthPaused);
                }
            }
        }

        private void AdvanceDay(string season, bool autoWater, bool growthPaused)
        {
            foreach (var state in _tiles.Values)
            {
                if (string.IsNullOrEmpty(state.CropId))
                {
                    state.Watered = false;
                    state.UnwateredDays = 0;
                    continue;
                }

                if (!_crops.TryGetValue(state.CropId, out var def))
                {
                    ResetPlot(state, tilled: false);
                    continue;
                }

                bool inSeason = def.Seasons.Count == 0 || string.IsNullOrEmpty(season) || def.Seasons.Contains(season);
                if (!inSeason)
                {
                    if (def.WitherOnSeasonChange)
                        ResetPlot(state, tilled: false);
                    continue;
                }

                if (autoWater)
                {
                    state.Watered = true;
                    state.UnwateredDays = 0;
                }

                if (growthPaused)
                {
                    state.UnwateredDays = 0;
                    state.Watered = false;
                    continue;
                }

                if (def.RegrowthDays.HasValue && state.RegrowCounter > 0)
                {
                    if (!def.HydrationNeeded || state.Watered)
                    {
                        state.RegrowCounter = Math.Max(0, state.RegrowCounter - 1);
                        if (state.RegrowCounter <= 0)
                        {
                            state.RegrowCounter = 0;
                            state.ReadyToHarvest = true;
                        }
                    }

                    if (def.HydrationNeeded && !state.Watered)
                    {
                        state.UnwateredDays++;
                        if (state.UnwateredDays >= 2)
                        {
                            ResetPlot(state, tilled: false);
                            continue;
                        }
                    }
                    else
                    {
                        state.UnwateredDays = 0;
                    }

                    state.Watered = false;
                    continue;
                }

                if (state.ReadyToHarvest)
                {
                    state.Watered = false;
                    state.UnwateredDays = 0;
                    continue;
                }

                bool hydrated = !def.HydrationNeeded || state.Watered;
                if (!hydrated)
                {
                    state.UnwateredDays++;
                    if (state.UnwateredDays >= 2)
                    {
                        ResetPlot(state, tilled: false);
                        continue;
                    }
                }
                else
                {
                    state.UnwateredDays = 0;
                }

                if (hydrated)
                {
                    state.DaysInStage++;
                    int index = Math.Clamp(state.Stage, 0, def.StageDurations.Length - 1);
                    int required = def.StageDurations.Length > 0 ? Math.Max(1, def.StageDurations[index]) : 1;
                    if (state.DaysInStage >= required)
                    {
                        state.DaysInStage = 0;
                        if (state.Stage < def.StageDurations.Length - 1)
                        {
                            state.Stage++;
                        }
                        else
                        {
                            state.ReadyToHarvest = true;
                        }
                    }
                }

                state.Watered = false;
            }
        }

        private void HandleSeasonChange(string season)
        {
            foreach (var state in _tiles.Values)
            {
                if (string.IsNullOrEmpty(state.CropId))
                    continue;
                if (!_crops.TryGetValue(state.CropId, out var def))
                {
                    ResetPlot(state, tilled: false);
                    continue;
                }

                bool allowed = def.Seasons.Count == 0 || string.IsNullOrEmpty(season) || def.Seasons.Contains(season);
                if (!allowed && def.WitherOnSeasonChange)
                    ResetPlot(state, tilled: false);
            }
        }

        private CropDefinition ResolveCropForPlant(string cropId, string seedItemId)
        {
            if (!string.IsNullOrWhiteSpace(cropId) && _crops.TryGetValue(cropId, out var defById))
                return defById;
            if (!string.IsNullOrWhiteSpace(seedItemId))
            {
                foreach (var def in _crops.Values)
                {
                    if (!string.IsNullOrWhiteSpace(def.SeedItemId) && string.Equals(def.SeedItemId, seedItemId, StringComparison.OrdinalIgnoreCase))
                        return def;
                }
            }
            return null;
        }

        private bool TryGetState(ThingId plot, out TileState state, bool create)
        {
            state = null;
            if (string.IsNullOrWhiteSpace(plot.Value))
                return false;
            if (!_plotLookup.TryGetValue(plot, out var position))
                return false;
            if (!_tiles.TryGetValue(position, out state) && create)
            {
                state = new TileState();
                _tiles[position] = state;
            }
            return state != null;
        }

        private static void ResetPlot(TileState state, bool tilled)
        {
            state.CropId = null;
            state.Stage = 0;
            state.DaysInStage = 0;
            state.Watered = false;
            state.ReadyToHarvest = false;
            state.RegrowCounter = 0;
            state.UnwateredDays = 0;
            state.Tilled = tilled;
            state.PlantedBy = default;
        }

        private static string NormalizeSeason(string season)
        {
            return string.IsNullOrWhiteSpace(season) ? string.Empty : season.Trim().ToLowerInvariant();
        }

        public bool TryGet(ThingId plot, out CropTileStateSnapshot state)
        {
            lock (_gate)
            {
                if (!TryGetState(plot, out var tile, create: false))
                {
                    state = default;
                    return false;
                }

                state = new CropTileStateSnapshot(
                    true,
                    tile.Tilled,
                    tile.CropId ?? string.Empty,
                    tile.Stage,
                    tile.Watered,
                    tile.DaysInStage,
                    tile.PlantedBy,
                    tile.RegrowCounter,
                    tile.ReadyToHarvest);
                return true;
            }
        }

        public int CountReadyCrops()
        {
            lock (_gate)
            {
                int count = 0;
                foreach (var tile in _tiles.Values)
                {
                    if (!string.IsNullOrEmpty(tile.CropId) && tile.ReadyToHarvest)
                        count++;
                }
                return count;
            }
        }

        public CropSystemState CaptureState()
        {
            lock (_gate)
            {
                var state = new CropSystemState
                {
                    lastProcessedDay = _lastProcessedDay,
                    lastSeason = _lastSeason,
                    pendingAutoWater = _pendingWeatherAutoWater,
                    pendingGrowthPause = _pendingWeatherGrowthPause,
                    rng = RandomStateSerializer.Capture(_rng)
                };

                foreach (var kv in _plotLookup)
                {
                    var plot = kv.Key;
                    var pos = kv.Value;
                    _tiles.TryGetValue(pos, out var tile);
                    state.tiles.Add(new CropTileState
                    {
                        plotId = plot.Value,
                        x = pos.X,
                        y = pos.Y,
                        tilled = tile?.Tilled ?? false,
                        cropId = tile?.CropId ?? string.Empty,
                        stage = tile?.Stage ?? 0,
                        watered = tile?.Watered ?? false,
                        daysInStage = tile?.DaysInStage ?? 0,
                        plantedBy = tile?.PlantedBy.Value,
                        regrowCounter = tile?.RegrowCounter ?? 0,
                        readyToHarvest = tile?.ReadyToHarvest ?? false,
                        unwateredDays = tile?.UnwateredDays ?? 0
                    });
                }

                return state;
            }
        }

        public void ApplyState(CropSystemState state)
        {
            if (state == null)
                return;
            lock (_gate)
            {
                _tiles.Clear();
                _plotLookup.Clear();

                if (state.tiles != null)
                {
                    foreach (var tileState in state.tiles)
                    {
                        if (tileState == null || string.IsNullOrWhiteSpace(tileState.plotId))
                            continue;
                        var plot = new ThingId(tileState.plotId.Trim());
                        var pos = new GridPos(tileState.x, tileState.y);
                        _plotLookup[plot] = pos;
                        if (!_tiles.TryGetValue(pos, out var tile))
                        {
                            tile = new TileState();
                            _tiles[pos] = tile;
                        }
                        tile.Tilled = tileState.tilled;
                        tile.CropId = string.IsNullOrWhiteSpace(tileState.cropId) ? null : tileState.cropId.Trim();
                        tile.Stage = tileState.stage;
                        tile.Watered = tileState.watered;
                        tile.DaysInStage = tileState.daysInStage;
                        tile.PlantedBy = string.IsNullOrWhiteSpace(tileState.plantedBy) ? default : new ThingId(tileState.plantedBy.Trim());
                        tile.RegrowCounter = tileState.regrowCounter;
                        tile.ReadyToHarvest = tileState.readyToHarvest;
                        tile.UnwateredDays = tileState.unwateredDays;
                    }
                }

                _lastProcessedDay = state.lastProcessedDay;
                _lastSeason = state.lastSeason;
                _pendingWeatherAutoWater = state.pendingAutoWater;
                _pendingWeatherGrowthPause = state.pendingGrowthPause;
                RandomStateSerializer.Apply(_rng, state.rng);
            }
        }
    }
}
