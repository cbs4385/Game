using System;
using System.Collections.Generic;
using System.Linq;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;
using DataDrivenGoap.Persistence;
using DataDrivenGoap.World;

namespace DataDrivenGoap.Simulation
{
    public sealed class WeatherSystem : IWeatherQuery
    {
        private sealed class WeatherState
        {
            public string Id { get; }
            public string DisplayName { get; }
            public bool AutoWaterCrops { get; }
            public bool GrowthPaused { get; }
            public bool CancelOutdoorShifts { get; }
            public double PathCostMultiplier { get; }
            public double BaseWeight { get; }
            public Dictionary<string, double> TransitionWeights { get; }
            public Dictionary<string, double> SeasonWeights { get; }
            public double GustMin { get; }
            public double GustMax { get; }

            public WeatherState(WeatherStateConfig config)
            {
                if (config == null)
                    throw new ArgumentNullException(nameof(config));
                if (string.IsNullOrWhiteSpace(config.id))
                    throw new ArgumentException("Weather state must define an id", nameof(config));

                var id = config.id.Trim();
                Id = id;

                if (string.IsNullOrWhiteSpace(config.displayName))
                    throw new ArgumentException($"Weather state '{id}' must define a display name.", nameof(config));
                DisplayName = config.displayName.Trim();

                AutoWaterCrops = config.autoWaterCrops;
                GrowthPaused = config.growthPause;
                CancelOutdoorShifts = config.cancelOutdoorShifts;

                if (!double.IsFinite(config.pathCostMultiplier) || config.pathCostMultiplier <= 0)
                    throw new ArgumentException($"Weather state '{id}' must define a positive, finite path cost multiplier.", nameof(config));
                PathCostMultiplier = config.pathCostMultiplier;

                if (!double.IsFinite(config.weight) || config.weight <= 0)
                    throw new ArgumentException($"Weather state '{id}' must define a positive, finite base weight.", nameof(config));
                BaseWeight = config.weight;

                if (config.transitions == null)
                    throw new ArgumentException($"Weather state '{id}' must define a transitions collection.", nameof(config));
                TransitionWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var transition in config.transitions)
                {
                    if (transition == null)
                        throw new ArgumentException($"Weather state '{id}' contains a null transition entry.", nameof(config));
                    if (string.IsNullOrWhiteSpace(transition.to))
                        throw new ArgumentException($"Weather state '{id}' contains a transition with an empty target id.", nameof(config));
                    var targetId = transition.to.Trim();
                    double weight = transition.weight;
                    if (!double.IsFinite(weight) || weight <= 0)
                        throw new ArgumentException($"Weather state '{id}' transition to '{targetId}' must define a positive, finite weight.", nameof(config));
                    TransitionWeights[targetId] = weight;
                }

                if (config.seasonWeights == null)
                    throw new ArgumentException($"Weather state '{id}' must define season weights.", nameof(config));
                SeasonWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in config.seasonWeights)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key))
                        throw new ArgumentException($"Weather state '{id}' contains a season weight with an empty season id.", nameof(config));
                    var seasonId = kv.Key.Trim().ToLowerInvariant();
                    double weight = kv.Value;
                    if (!double.IsFinite(weight) || weight <= 0)
                        throw new ArgumentException($"Weather state '{id}' season '{seasonId}' must define a positive, finite weight.", nameof(config));
                    SeasonWeights[seasonId] = weight;
                }

                if (!config.gustStrengthMin.HasValue || !config.gustStrengthMax.HasValue)
                    throw new ArgumentException($"Weather state '{id}' must define both gust strength minimum and maximum.", nameof(config));
                double gustMin = config.gustStrengthMin.Value;
                double gustMax = config.gustStrengthMax.Value;
                if (!double.IsFinite(gustMin) || !double.IsFinite(gustMax))
                    throw new ArgumentException($"Weather state '{id}' must define finite gust strength bounds.", nameof(config));
                if (gustMax < gustMin)
                    throw new ArgumentException($"Weather state '{id}' gust strength maximum must be greater than or equal to the minimum.", nameof(config));
                GustMin = gustMin;
                GustMax = gustMax;
            }
        }

        private static readonly ThingId WorldThingId = new ThingId("$world");

        private readonly object _gate = new object();
        private readonly Dictionary<string, WeatherState> _states;
        private readonly WeatherState _defaultState;
        private readonly bool _enabled;
        private readonly IWorld _world;
        private readonly CropSystem _cropSystem;
        private readonly AnimalSystem _animalSystem;
        private readonly RoleScheduleService _scheduleService;
        private readonly Random _rng;
        private readonly double _dawnHour;
        private readonly double _gustIntervalHours;

        private WeatherState _currentState;
        private WeatherSnapshot _currentSnapshot;
        private volatile bool _initialized;
        private double _currentDayIndex = double.NaN;
        private int _lastGustHour = int.MinValue;

        public WeatherSystem(
            IWorld world,
            CropSystem cropSystem,
            AnimalSystem animalSystem,
            RoleScheduleService scheduleService,
            WeatherSystemConfig config,
            int rngSeed)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _cropSystem = cropSystem;
            _animalSystem = animalSystem;
            _scheduleService = scheduleService;
            if (_scheduleService != null)
                _scheduleService.WeatherQuery = this;
            _rng = new Random(rngSeed);

            if (config == null)
                throw new ArgumentNullException(nameof(config));
            _enabled = config.enabled;
            _dawnHour = NormalizeHour(config.dawnHour);
            _gustIntervalHours = ValidateGustInterval(config.gustIntervalHours);

            _states = BuildStates(config.states);
            if (_states.Count == 0)
                throw new ArgumentException("Weather configuration must define at least one valid weather state.", nameof(config));

            if (string.IsNullOrWhiteSpace(config.defaultState))
                throw new ArgumentException("Weather configuration must specify a default state id.", nameof(config));

            if (!_states.TryGetValue(config.defaultState.Trim(), out _defaultState))
                throw new ArgumentException($"Weather configuration default state '{config.defaultState}' could not be resolved.", nameof(config));

            InitializeFromDefaultState();
        }

        public WeatherSnapshot CurrentWeather
        {
            get
            {
                EnsureInitialized();
                lock (_gate)
                    return _currentSnapshot;
            }
        }

        public bool IsWeather(string weatherId)
        {
            EnsureInitialized();
            if (string.IsNullOrWhiteSpace(weatherId))
                return false;
            var id = weatherId.Trim();
            lock (_gate)
                return string.Equals(_currentSnapshot.Id, id, StringComparison.OrdinalIgnoreCase);
        }

        public WeatherSystemState CaptureState()
        {
            EnsureInitialized();
            lock (_gate)
            {
                return new WeatherSystemState
                {
                    currentStateId = _currentState?.Id,
                    currentSnapshot = ToSnapshotData(_currentSnapshot),
                    currentDayIndex = _currentDayIndex,
                    lastGustHour = _lastGustHour,
                    rng = RandomStateSerializer.Capture(_rng)
                };
            }
        }

        public void ApplyState(WeatherSystemState state)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));

            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(state.currentStateId))
                    throw new InvalidOperationException("Weather system state must include the current state id.");
                var stateId = state.currentStateId.Trim();
                if (!_states.TryGetValue(stateId, out var s))
                    throw new InvalidOperationException($"Weather system state references unknown state '{state.currentStateId}'.");
                _currentState = s;
                _currentSnapshot = FromSnapshotData(state.currentSnapshot);
                if (!string.Equals(_currentSnapshot.Id, _currentState.Id, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Weather system state snapshot id '{_currentSnapshot.Id}' does not match the current state '{_currentState.Id}'.");
                _currentDayIndex = state.currentDayIndex;
                _lastGustHour = state.lastGustHour;
                RandomStateSerializer.Apply(_rng, state.rng);
                _initialized = true;
            }

            ApplyWeather(_currentSnapshot, null);
        }

        private static WeatherSnapshotData ToSnapshotData(WeatherSnapshot snapshot)
        {
            return new WeatherSnapshotData
            {
                id = snapshot.Id,
                displayName = snapshot.DisplayName,
                autoWaterCrops = snapshot.AutoWaterCrops,
                growthPaused = snapshot.GrowthPaused,
                cancelOutdoorShifts = snapshot.CancelOutdoorShifts,
                pathCostMultiplier = snapshot.PathCostMultiplier,
                gustStrength = snapshot.GustStrength
            };
        }

        private static WeatherSnapshot FromSnapshotData(WeatherSnapshotData data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (string.IsNullOrWhiteSpace(data.id))
                throw new ArgumentException("Weather snapshot data must include an id.", nameof(data));
            if (string.IsNullOrWhiteSpace(data.displayName))
                throw new ArgumentException("Weather snapshot data must include a display name.", nameof(data));
            if (!double.IsFinite(data.pathCostMultiplier) || data.pathCostMultiplier <= 0)
                throw new ArgumentException("Weather snapshot data must include a positive, finite path cost multiplier.", nameof(data));
            if (!double.IsFinite(data.gustStrength))
                throw new ArgumentException("Weather snapshot data must include a finite gust strength.", nameof(data));

            return new WeatherSnapshot(
                data.id.Trim(),
                data.displayName.Trim(),
                data.autoWaterCrops,
                data.growthPaused,
                data.cancelOutdoorShifts,
                data.pathCostMultiplier,
                data.gustStrength);
        }

        public void Tick(WorldTimeSnapshot time)
        {
            if (time == null)
                return;

            WeatherSnapshot snapshotToApply = default;
            string previousId = null;
            bool applyWeather = false;
            bool gustChanged = false;

            double dayIndex = Math.Floor(time.TotalWorldDays);
            double hour = time.TimeOfDay.TotalHours;
            string season = NormalizeSeason(time.SeasonName);

            lock (_gate)
            {
                if (_currentState == null)
                {
                    var initial = _enabled ? ChooseNextState(null, season) : _defaultState;
                    if (initial == null)
                        return;
                    _currentState = initial;
                    _currentDayIndex = dayIndex;
                    _lastGustHour = (int)Math.Floor(hour);
                    snapshotToApply = BuildSnapshot(initial, SampleGust(initial));
                    _currentSnapshot = snapshotToApply;
                    applyWeather = true;
                    previousId = null;
                }
                else if (!_enabled)
                {
                    if (double.IsNaN(_currentDayIndex))
                        _currentDayIndex = dayIndex;
                    if (!string.Equals(_currentState.Id, _currentSnapshot.Id, StringComparison.Ordinal))
                    {
                        snapshotToApply = BuildSnapshot(_currentState, _currentSnapshot.GustStrength);
                        _currentSnapshot = snapshotToApply;
                        applyWeather = true;
                        previousId = _currentState.Id;
                    }
                    else if (ShouldUpdateGust(hour))
                    {
                        _lastGustHour = (int)Math.Floor(hour);
                        snapshotToApply = BuildSnapshot(_currentState, SampleGust(_currentState));
                        _currentSnapshot = snapshotToApply;
                        gustChanged = true;
                    }
                }
                else
                {
                    bool newDay = double.IsNaN(_currentDayIndex) || dayIndex > _currentDayIndex;
                    if (newDay && hour >= _dawnHour)
                    {
                        previousId = _currentState.Id;
                        var next = ChooseNextState(_currentState, season) ?? _currentState;
                        _currentState = next;
                        _currentDayIndex = dayIndex;
                        _lastGustHour = (int)Math.Floor(hour);
                        snapshotToApply = BuildSnapshot(next, SampleGust(next));
                        _currentSnapshot = snapshotToApply;
                        applyWeather = true;
                    }
                    else if (ShouldUpdateGust(hour))
                    {
                        _lastGustHour = (int)Math.Floor(hour);
                        snapshotToApply = BuildSnapshot(_currentState, SampleGust(_currentState));
                        _currentSnapshot = snapshotToApply;
                        gustChanged = true;
                    }
                }
            }

            if (applyWeather)
            {
                ApplyWeather(snapshotToApply, previousId);
            }
            else if (gustChanged)
            {
                ApplyGust(snapshotToApply);
            }
        }

        private void ApplyWeather(WeatherSnapshot snapshot, string previousId)
        {
            if (string.IsNullOrEmpty(snapshot.Id))
                return;

            var snap = _world.Snap();
            var writes = new List<WriteSetEntry>
            {
                new WriteSetEntry(WorldThingId, "weather_pathCostMult", snapshot.PathCostMultiplier),
                new WriteSetEntry(WorldThingId, "weather_cancelOutdoorShifts", snapshot.CancelOutdoorShifts ? 1.0 : 0.0),
                new WriteSetEntry(WorldThingId, "weather_autoWaterCrops", snapshot.AutoWaterCrops ? 1.0 : 0.0),
                new WriteSetEntry(WorldThingId, "weather_growthPaused", snapshot.GrowthPaused ? 1.0 : 0.0),
                new WriteSetEntry(WorldThingId, "weather_gustStrength", snapshot.GustStrength)
            };

            var facts = new List<FactDelta>();
            if (!string.IsNullOrWhiteSpace(previousId))
            {
                facts.Add(new FactDelta
                {
                    Pred = "weather",
                    A = new ThingId(previousId),
                    B = new ThingId(string.Empty),
                    Add = false
                });
            }

            facts.Add(new FactDelta
            {
                Pred = "weather",
                A = new ThingId(snapshot.Id),
                B = new ThingId(string.Empty),
                Add = true
            });

            var batch = new EffectBatch
            {
                BaseVersion = snap.Version,
                Reads = Array.Empty<ReadSetEntry>(),
                Writes = writes.ToArray(),
                FactDeltas = facts.ToArray(),
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

            _world.TryCommit(batch);
            _cropSystem?.ApplyWeatherEffects(snapshot.AutoWaterCrops, snapshot.GrowthPaused);
            _animalSystem?.ApplyWeatherEffects(snapshot.GrowthPaused);
        }

        private void ApplyGust(WeatherSnapshot snapshot)
        {
            var snap = _world.Snap();
            var batch = new EffectBatch
            {
                BaseVersion = snap.Version,
                Reads = Array.Empty<ReadSetEntry>(),
                Writes = new[] { new WriteSetEntry(WorldThingId, "weather_gustStrength", snapshot.GustStrength) },
                FactDeltas = Array.Empty<FactDelta>(),
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

            _world.TryCommit(batch);
        }

        private WeatherState ChooseNextState(WeatherState current, string season)
        {
            season = NormalizeSeason(season);
            var candidates = new List<(WeatherState State, double Weight)>();

            foreach (var state in _states.Values)
            {
                double weight = 0.0;
                if (current != null && current.TransitionWeights.TryGetValue(state.Id, out var transitionWeight))
                    weight = transitionWeight;
                if (weight <= 0)
                    weight = state.BaseWeight;
                if (weight <= 0)
                    continue;

                if (!string.IsNullOrEmpty(season) && state.SeasonWeights.TryGetValue(season, out var seasonWeight))
                    weight *= seasonWeight;

                if (weight <= 0 || double.IsNaN(weight) || double.IsInfinity(weight))
                    continue;

                candidates.Add((state, weight));
            }

            if (candidates.Count == 0)
                return current ?? _defaultState;

            double total = candidates.Sum(c => c.Weight);
            if (total <= 0 || double.IsNaN(total) || double.IsInfinity(total))
                return candidates[0].State;

            double roll = _rng.NextDouble() * total;
            foreach (var candidate in candidates)
            {
                roll -= candidate.Weight;
                if (roll <= 1e-9)
                    return candidate.State;
            }

            return candidates[^1].State;
        }

        private void InitializeFromDefaultState()
        {
            if (_defaultState == null)
                throw new InvalidOperationException("Weather system default state could not be resolved.");

            var snapshot = BuildSnapshot(_defaultState, SampleGust(_defaultState));
            ValidateSnapshot(_defaultState, snapshot);

            _currentState = _defaultState;
            _currentSnapshot = snapshot;
            _initialized = true;
        }

        private void EnsureInitialized()
        {
            if (!_initialized)
                throw new InvalidOperationException("Weather system has not been initialized.");
        }

        private static void ValidateSnapshot(WeatherState state, WeatherSnapshot snapshot)
        {
            if (state == null)
                throw new InvalidOperationException("Weather snapshot validation requires a weather state.");
            if (snapshot.Equals(default))
                throw new InvalidOperationException($"Weather state '{state.Id}' produced an uninitialized snapshot.");
            if (!string.Equals(state.Id, snapshot.Id, StringComparison.Ordinal))
                throw new InvalidOperationException($"Weather snapshot id '{snapshot.Id}' does not match weather state '{state.Id}'.");
            if (string.IsNullOrWhiteSpace(snapshot.DisplayName))
                throw new InvalidOperationException($"Weather state '{state.Id}' produced a snapshot without a display name.");
            if (!double.IsFinite(snapshot.PathCostMultiplier) || snapshot.PathCostMultiplier <= 0.0)
                throw new InvalidOperationException($"Weather state '{state.Id}' produced an invalid path cost multiplier.");
            if (!double.IsFinite(snapshot.GustStrength))
                throw new InvalidOperationException($"Weather state '{state.Id}' produced an invalid gust strength.");
        }

        private WeatherSnapshot BuildSnapshot(WeatherState state, double gust)
        {
            if (state == null)
                throw new InvalidOperationException("Cannot build a weather snapshot without a weather state.");
            if (!double.IsFinite(gust))
                throw new InvalidOperationException($"Weather state '{state.Id}' produced a non-finite gust strength.");
            return new WeatherSnapshot(
                state.Id,
                state.DisplayName,
                state.AutoWaterCrops,
                state.GrowthPaused,
                state.CancelOutdoorShifts,
                state.PathCostMultiplier,
                gust);
        }

        private double SampleGust(WeatherState state)
        {
            if (state == null)
                return 0.0;
            double min = state.GustMin;
            double max = state.GustMax;
            if (max <= min)
                return min;
            double span = max - min;
            return min + (_rng.NextDouble() * span);
        }

        private static double NormalizeHour(double hour)
        {
            if (!double.IsFinite(hour))
                throw new ArgumentException("Weather dawn hour must be a finite value.", nameof(hour));
            if (hour < 0.0 || hour > 24.0)
                throw new ArgumentOutOfRangeException(nameof(hour), hour, "Weather dawn hour must be between 0 and 24 hours.");
            return hour;
        }

        private static double ValidateGustInterval(double gustIntervalHours)
        {
            if (!double.IsFinite(gustIntervalHours))
                throw new ArgumentException("Weather gust interval must be a finite value.", nameof(gustIntervalHours));
            if (gustIntervalHours <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(gustIntervalHours), gustIntervalHours, "Weather gust interval must be greater than zero hours.");
            return gustIntervalHours;
        }

        private bool ShouldUpdateGust(double hour)
        {
            if (_gustIntervalHours <= 0)
                return false;
            int currentHour = (int)Math.Floor(hour);
            if (_lastGustHour == int.MinValue)
                return true;
            return currentHour - _lastGustHour >= _gustIntervalHours;
        }

        private static string NormalizeSeason(string season)
        {
            return string.IsNullOrWhiteSpace(season) ? string.Empty : season.Trim().ToLowerInvariant();
        }

        private Dictionary<string, WeatherState> BuildStates(IEnumerable<WeatherStateConfig> configs)
        {
            if (configs == null)
                throw new ArgumentException("Weather configuration must include a collection of states.", nameof(configs));

            var map = new Dictionary<string, WeatherState>(StringComparer.OrdinalIgnoreCase);

            foreach (var cfg in configs)
            {
                if (cfg == null || string.IsNullOrWhiteSpace(cfg.id))
                    continue;
                var state = new WeatherState(cfg);
                map[state.Id] = state;
            }

            return map;
        }
    }
}
