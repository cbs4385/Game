using System;
using System.Collections.Generic;
using System.Linq;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;
using DataDrivenGoap.Persistence;

namespace DataDrivenGoap.Simulation
{
    public interface ICalendarEventQuery
    {
        bool TryGetScheduleDirective(
            IWorldSnapshot snapshot,
            ThingId actor,
            RoleScheduleDefinition schedule,
            RoleScheduleBlock block,
            string defaultTask,
            string defaultGotoTag,
            out ScheduleDirective directive);

        bool IsEventActive(string eventId);
        IReadOnlyCollection<ActiveEventInfo> ActiveEvents { get; }
        bool TryStartEvent(string eventId);
    }

    public readonly struct ScheduleDirective
    {
        public string EventId { get; }
        public string EventLabel { get; }
        public string TaskId { get; }
        public string GotoTag { get; }

        public ScheduleDirective(string eventId, string eventLabel, string taskId, string gotoTag)
        {
            EventId = eventId ?? string.Empty;
            EventLabel = eventLabel ?? string.Empty;
            TaskId = taskId ?? string.Empty;
            GotoTag = gotoTag ?? string.Empty;
        }
    }

    public readonly struct ActiveEventInfo
    {
        public string Id { get; }
        public string Label { get; }

        public ActiveEventInfo(string id, string label)
        {
            Id = id ?? string.Empty;
            Label = label ?? string.Empty;
        }
    }

    public sealed class CalendarEventSystem : ICalendarEventQuery
    {
        private sealed class ThingIdIgnoreCaseComparer : IEqualityComparer<ThingId>
        {
            public static readonly ThingIdIgnoreCaseComparer Instance = new ThingIdIgnoreCaseComparer();

            public bool Equals(ThingId x, ThingId y)
            {
                return string.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(ThingId obj)
            {
                return StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Value ?? string.Empty);
            }
        }

        private sealed class ScheduleOverride
        {
            public string RoleId { get; }
            public HashSet<ThingId> Actors { get; }
            public string GotoTag { get; }
            public string TaskId { get; }

            public ScheduleOverride(CalendarEventScheduleOverrideConfig cfg)
            {
                RoleId = cfg?.role?.Trim() ?? string.Empty;
                TaskId = cfg?.task?.Trim() ?? string.Empty;
                GotoTag = cfg?.gotoTag?.Trim() ?? string.Empty;
                Actors = new HashSet<ThingId>(ThingIdIgnoreCaseComparer.Instance);
                foreach (var actor in cfg?.actors ?? Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(actor))
                        continue;
                    Actors.Add(new ThingId(actor.Trim()));
                }
            }

            public bool Matches(ThingId actorId, string roleId)
            {
                if (Actors.Count > 0)
                    return Actors.Contains(actorId);
                if (string.IsNullOrWhiteSpace(RoleId))
                    return false;
                return string.Equals(RoleId, roleId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }

        private sealed class ActiveEventState
        {
            public CalendarEventConfig Config { get; }
            public Dictionary<(ThingId thing, string attr), double?> AttributeRestore { get; }
            public List<ThingId> SpawnedThings { get; }
            public List<ScheduleOverride> Overrides { get; }

            public ActiveEventState(CalendarEventConfig config)
            {
                Config = config;
                AttributeRestore = new Dictionary<(ThingId, string), double?>();
                SpawnedThings = new List<ThingId>();
                Overrides = new List<ScheduleOverride>();
                foreach (var sched in config?.@do?.schedule ?? Array.Empty<CalendarEventScheduleOverrideConfig>())
                {
                    if (sched == null)
                        continue;
                    Overrides.Add(new ScheduleOverride(sched));
                }
            }
        }

        private static readonly ThingId WorldThing = new ThingId("$world");

        private readonly object _gate = new object();
        private readonly IWorld _world;
        private readonly IWeatherQuery _weather;
        private readonly IReadOnlyList<CalendarEventConfig> _events;
        private readonly Dictionary<string, ActiveEventState> _active = new Dictionary<string, ActiveEventState>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ActiveEventInfo> _activeInfos = new List<ActiveEventInfo>();

        public CalendarEventSystem(
            IWorld world,
            IWeatherQuery weather,
            IEnumerable<CalendarEventConfig> events)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _weather = weather;
            _events = (events ?? Array.Empty<CalendarEventConfig>()).Where(e => e != null && !string.IsNullOrWhiteSpace(e.id)).ToArray();
            foreach (var evt in _events)
                ValidateEventConfiguration(evt);
        }

        public IReadOnlyCollection<ActiveEventInfo> ActiveEvents
        {
            get
            {
                lock (_gate)
                    return _activeInfos.ToArray();
            }
        }

        public bool IsEventActive(string eventId)
        {
            if (string.IsNullOrWhiteSpace(eventId))
                return false;
            lock (_gate)
                return _active.ContainsKey(eventId.Trim());
        }

        public bool TryStartEvent(string eventId)
        {
            if (string.IsNullOrWhiteSpace(eventId))
                return false;

            string normalized = eventId.Trim();
            CalendarEventConfig cfg = null;

            lock (_gate)
            {
                if (_active.ContainsKey(normalized))
                    return false;

                cfg = _events.FirstOrDefault(e => string.Equals(e?.id, normalized, StringComparison.OrdinalIgnoreCase));
            }

            if (cfg == null)
                return false;

            var snapshot = _world.Snap();
            StartEvent(cfg, snapshot);
            return true;
        }

        public bool TryGetScheduleDirective(
            IWorldSnapshot snapshot,
            ThingId actor,
            RoleScheduleDefinition schedule,
            RoleScheduleBlock block,
            string defaultTask,
            string defaultGotoTag,
            out ScheduleDirective directive)
        {
            directive = default;
            lock (_gate)
            {
                foreach (var state in _active.Values)
                {
                    foreach (var ov in state.Overrides)
                    {
                        if (ov == null)
                            continue;
                        if (!ov.Matches(actor, schedule?.RoleId))
                            continue;
                        string task = string.IsNullOrWhiteSpace(ov.TaskId) ? defaultTask : ov.TaskId;
                        string gotoTag = string.IsNullOrWhiteSpace(ov.GotoTag) ? defaultGotoTag : ov.GotoTag;
                        directive = new ScheduleDirective(state.Config?.id, state.Config?.label, task, gotoTag);
                        return true;
                    }
                }
            }
            return false;
        }

        public void Tick(WorldTimeSnapshot time)
        {
            if (time == null)
                return;

            var snapshot = _world.Snap();
            var weatherId = _weather?.CurrentWeather.Id;
            var toStart = new List<CalendarEventConfig>();
            var toEnd = new List<CalendarEventConfig>();

            lock (_gate)
            {
                foreach (var cfg in _events)
                {
                    bool isActive = _active.ContainsKey(cfg.id);
                    bool shouldBeActive = ShouldBeActive(cfg, time, weatherId);
                    if (shouldBeActive && !isActive)
                        toStart.Add(cfg);
                    else if (!shouldBeActive && isActive)
                        toEnd.Add(cfg);
                }
            }

            foreach (var cfg in toEnd)
                StopEvent(cfg);
            foreach (var cfg in toStart)
                StartEvent(cfg, snapshot);

            lock (_gate)
            {
                _activeInfos.Clear();
                foreach (var entry in _active)
                {
                    var cfg = entry.Value.Config;
                    _activeInfos.Add(new ActiveEventInfo(cfg?.id ?? string.Empty, cfg?.label ?? cfg?.id ?? string.Empty));
                }
            }
        }

        public CalendarSystemState CaptureState()
        {
            lock (_gate)
            {
                var state = new CalendarSystemState();
                foreach (var entry in _active)
                {
                    var active = entry.Value;
                    var snapshot = new ActiveCalendarEventState
                    {
                        eventId = active.Config?.id ?? string.Empty,
                    };

                    foreach (var kv in active.AttributeRestore)
                    {
                        string key = $"{kv.Key.thing.Value}|{kv.Key.attr}";
                        snapshot.attributeRestore[key] = kv.Value;
                    }

                    foreach (var thing in active.SpawnedThings)
                    {
                        if (!string.IsNullOrWhiteSpace(thing.Value))
                            snapshot.spawnedThings.Add(thing.Value);
                    }

                    foreach (var ov in active.Overrides)
                    {
                        if (ov == null)
                            continue;
                        var ostate = new ScheduleOverrideState
                        {
                            roleId = ov.RoleId,
                            gotoTag = ov.GotoTag,
                            taskId = ov.TaskId,
                            actors = ov.Actors.Select(a => a.Value).ToList()
                        };
                        snapshot.overrides.Add(ostate);
                    }

                    state.activeEvents.Add(snapshot);
                }
                return state;
            }
        }

        public void ApplyState(CalendarSystemState state)
        {
            lock (_gate)
            {
                _active.Clear();
                _activeInfos.Clear();
                if (state?.activeEvents == null)
                    return;

                foreach (var entry in state.activeEvents)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.eventId))
                        continue;
                    var cfg = _events.FirstOrDefault(e => string.Equals(e.id, entry.eventId.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (cfg == null)
                        continue;

                    var active = new ActiveEventState(cfg);

                    if (entry.attributeRestore != null)
                    {
                        foreach (var kv in entry.attributeRestore)
                        {
                            if (string.IsNullOrWhiteSpace(kv.Key))
                                continue;
                            var parts = kv.Key.Split('|', 2);
                            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                                continue;
                            var thing = new ThingId(parts[0].Trim());
                            active.AttributeRestore[(thing, parts[1].Trim())] = kv.Value;
                        }
                    }

                    if (entry.spawnedThings != null)
                    {
                        foreach (var tid in entry.spawnedThings)
                        {
                            if (string.IsNullOrWhiteSpace(tid))
                                continue;
                            active.SpawnedThings.Add(new ThingId(tid.Trim()));
                        }
                    }

                    if (entry.overrides != null)
                    {
                        foreach (var ov in entry.overrides)
                        {
                            if (ov == null)
                                continue;
                            var cfgOverride = new CalendarEventScheduleOverrideConfig
                            {
                                role = ov.roleId,
                                gotoTag = ov.gotoTag,
                                task = ov.taskId,
                                actors = ov.actors?.ToArray()
                            };
                            active.Overrides.Add(new ScheduleOverride(cfgOverride));
                        }
                    }

                    _active[cfg.id] = active;
                    _activeInfos.Add(new ActiveEventInfo(cfg.id, cfg.label ?? cfg.id));
                }
            }
        }

        private static void ValidateEventConfiguration(CalendarEventConfig cfg)
        {
            if (cfg == null)
                throw new ArgumentNullException(nameof(cfg));

            var when = cfg.when;
            if (when == null)
                throw new InvalidOperationException($"Calendar event '{cfg.id ?? string.Empty}' is missing required schedule information.");

            if (!when.startHour.HasValue)
                throw new InvalidOperationException($"Calendar event '{cfg.id ?? string.Empty}' must define a startHour.");

            if (!when.endHour.HasValue)
                throw new InvalidOperationException($"Calendar event '{cfg.id ?? string.Empty}' must define an endHour.");

            ValidateHour(when.startHour.Value, cfg.id, "startHour");
            ValidateHour(when.endHour.Value, cfg.id, "endHour");
        }

        private static void ValidateHour(double value, string eventId, string fieldName)
        {
            if (!double.IsFinite(value))
                throw new InvalidOperationException($"Calendar event '{eventId ?? string.Empty}' has non-finite {fieldName}.");

            if (value < 0.0 || value > 24.0)
                throw new InvalidOperationException($"Calendar event '{eventId ?? string.Empty}' has {fieldName} outside the [0, 24] range.");
        }

        private static bool ShouldBeActive(CalendarEventConfig cfg, WorldTimeSnapshot time, string weatherId)
        {
            if (cfg == null || time == null)
                return false;

            var when = cfg.when;
            if (when == null || !when.startHour.HasValue || !when.endHour.HasValue)
                return false;
            if (when.seasons != null && when.seasons.Length > 0)
            {
                bool seasonMatch = when.seasons.Any(s => string.Equals(s?.Trim(), time.SeasonName, StringComparison.OrdinalIgnoreCase));
                if (!seasonMatch)
                    return false;
            }

            int dayOfSeason = 1;
            if (time.SeasonLengthDays > 0)
                dayOfSeason = ((time.DayOfYear - 1) % time.SeasonLengthDays) + 1;

            if (when.daysOfSeason != null && when.daysOfSeason.Length > 0)
            {
                bool match = when.daysOfSeason.Any(d => d == dayOfSeason);
                if (!match)
                    return false;
            }
            else if (when.startDayOfSeason.HasValue || when.endDayOfSeason.HasValue)
            {
                int start = when.startDayOfSeason ?? dayOfSeason;
                int end = when.endDayOfSeason ?? dayOfSeason;
                if (dayOfSeason < start || dayOfSeason > end)
                    return false;
            }

            if (when.weekdays != null && when.weekdays.Length > 0)
            {
                int dayOfWeek = ((time.DayOfYear - 1) % 7 + 7) % 7;
                bool match = when.weekdays.Any(w => DayMatches(w, dayOfWeek));
                if (!match)
                    return false;
            }

            double startHour = when.startHour.Value;
            double endHour = when.endHour.Value;
            double hour = time.TimeOfDay.TotalHours;
            if (!IsWithinHourWindow(hour, startHour, endHour))
                return false;

            if (when.weather != null && when.weather.Length > 0)
            {
                if (string.IsNullOrWhiteSpace(weatherId))
                    return false;
                bool match = when.weather.Any(w => string.Equals(w?.Trim(), weatherId, StringComparison.OrdinalIgnoreCase));
                if (!match)
                    return false;
            }

            return true;
        }

        private static bool DayMatches(string weekday, int dayIndex)
        {
            if (string.IsNullOrWhiteSpace(weekday))
                return false;
            var normalized = weekday.Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "mon":
                case "monday":
                    return dayIndex == 0;
                case "tue":
                case "tues":
                case "tuesday":
                    return dayIndex == 1;
                case "wed":
                case "weds":
                case "wednesday":
                    return dayIndex == 2;
                case "thu":
                case "thur":
                case "thurs":
                case "thursday":
                    return dayIndex == 3;
                case "fri":
                case "friday":
                    return dayIndex == 4;
                case "sat":
                case "saturday":
                    return dayIndex == 5;
                case "sun":
                case "sunday":
                    return dayIndex == 6;
                default:
                    return false;
            }
        }

        private static bool IsWithinHourWindow(double hour, double start, double end)
        {
            hour = ClampHour(hour);
            start = ClampHour(start);
            end = ClampHour(end);
            if (Math.Abs(end - start) < 1e-6)
                return true;
            if (start <= end)
                return hour >= start && hour < end;
            return hour >= start || hour < end;
        }

        private static double ClampHour(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0.0;
            if (value < 0.0)
                return 0.0;
            if (value > 24.0)
                return 24.0;
            return value;
        }

        private void StartEvent(CalendarEventConfig cfg, IWorldSnapshot snapshot)
        {
            if (cfg == null)
                return;

            var batch = new EffectBatch
            {
                Reads = Array.Empty<ReadSetEntry>(),
                Writes = Array.Empty<WriteSetEntry>(),
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
                ForagingOps = Array.Empty<ForagingOperation>(),
            };

            var writes = new List<WriteSetEntry>();
            var facts = new List<FactDelta>();
            var spawns = new List<ThingSpawnRequest>();
            var cooldowns = new List<PlanCooldownRequest>();
            var currency = new List<CurrencyDelta>();
            var relationships = new List<RelationshipDelta>();

            var state = new ActiveEventState(cfg);

            foreach (var fact in cfg.@do?.facts ?? Array.Empty<CalendarEventFactConfig>())
            {
                if (fact == null || string.IsNullOrWhiteSpace(fact.pred))
                    continue;
                var a = ParseThingId(fact.a);
                var b = ParseThingId(fact.b);
                facts.Add(new FactDelta { Pred = fact.pred.Trim(), A = a, B = b, Add = true });
            }

            foreach (var attr in cfg.@do?.attributes ?? Array.Empty<CalendarEventAttributeConfig>())
            {
                if (attr == null || string.IsNullOrWhiteSpace(attr.thing) || string.IsNullOrWhiteSpace(attr.attribute))
                    continue;
                var thingId = ParseThingId(attr.thing);
                if (string.IsNullOrEmpty(thingId.Value))
                    continue;
                double value = attr.value;
                double? prior = null;
                var view = snapshot?.GetThing(thingId);
                if (view != null && view.Attributes != null && view.Attributes.TryGetValue(attr.attribute, out var existing))
                    prior = existing;
                state.AttributeRestore[(thingId, attr.attribute)] = prior;
                writes.Add(new WriteSetEntry(thingId, attr.attribute, value));
            }

            foreach (var spawn in cfg.@do?.spawns ?? Array.Empty<CalendarEventSpawnConfig>())
            {
                if (spawn == null || string.IsNullOrWhiteSpace(spawn.id))
                    continue;
                var request = new ThingSpawnRequest
                {
                    Id = new ThingId(spawn.id.Trim()),
                    Type = spawn.type ?? string.Empty,
                    Tags = (spawn.tags ?? Array.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToArray(),
                    Attributes = (spawn.attributes ?? Array.Empty<CalendarEventSpawnAttributeConfig>())
                        .Where(a => a != null && !string.IsNullOrWhiteSpace(a.name))
                        .Select(a => new ThingAttributeValue(a.name.Trim(), a.value))
                        .ToArray(),
                    Position = new GridPos(spawn.x, spawn.y)
                };
                spawns.Add(request);
                state.SpawnedThings.Add(request.Id);
            }

            foreach (var effect in cfg.effects ?? Array.Empty<CalendarEventEffectConfig>())
            {
                if (effect == null || string.IsNullOrWhiteSpace(effect.type))
                    continue;
                var type = effect.type.Trim().ToLowerInvariant();
                switch (type)
                {
                    case "plan_cooldown":
                    {
                        var scope = ParseThingId(string.IsNullOrWhiteSpace(effect.target) ? effect.who : effect.target);
                        double seconds = Math.Max(0.0, effect.seconds ?? 0.0);
                        cooldowns.Add(new PlanCooldownRequest(scope, seconds, effect.useStepDuration));
                        break;
                    }
                    case "currency":
                    {
                        var who = ParseThingId(effect.who);
                        if (!string.IsNullOrEmpty(who.Value) && effect.amount.HasValue)
                            currency.Add(new CurrencyDelta(who, effect.amount.Value));
                        break;
                    }
                    case "relationship":
                    {
                        var who = ParseThingId(effect.who);
                        var target = ParseThingId(effect.target);
                        if (!string.IsNullOrWhiteSpace(effect.relationship) && !string.IsNullOrEmpty(who.Value) && !string.IsNullOrEmpty(target.Value) && effect.amount.HasValue)
                        {
                            relationships.Add(new RelationshipDelta(who, target, effect.relationship.Trim(), effect.item, effect.amount));
                        }
                        break;
                    }
                }
            }

            batch.Writes = writes.ToArray();
            batch.FactDeltas = facts.ToArray();
            batch.Spawns = spawns.ToArray();
            batch.PlanCooldowns = cooldowns.ToArray();
            batch.CurrencyOps = currency.ToArray();
            batch.RelationshipOps = relationships.ToArray();

            _world.TryCommit(batch);

            lock (_gate)
            {
                _active[cfg.id] = state;
            }
        }

        private void StopEvent(CalendarEventConfig cfg)
        {
            if (cfg == null)
                return;

            ActiveEventState state;
            lock (_gate)
            {
                if (!_active.TryGetValue(cfg.id, out state))
                    return;
                _active.Remove(cfg.id);
            }

            var writes = new List<WriteSetEntry>();
            var facts = new List<FactDelta>();
            var despawns = new List<ThingId>();

            foreach (var entry in state.AttributeRestore)
            {
                var key = entry.Key;
                double value = entry.Value ?? 0.0;
                writes.Add(new WriteSetEntry(key.thing, key.attr, value));
            }

            foreach (var fact in cfg.@do?.facts ?? Array.Empty<CalendarEventFactConfig>())
            {
                if (fact == null || string.IsNullOrWhiteSpace(fact.pred))
                    continue;
                var a = ParseThingId(fact.a);
                var b = ParseThingId(fact.b);
                facts.Add(new FactDelta { Pred = fact.pred.Trim(), A = a, B = b, Add = false });
            }

            foreach (var spawn in state.SpawnedThings)
            {
                if (string.IsNullOrEmpty(spawn.Value))
                    continue;
                despawns.Add(spawn);
            }

            if (writes.Count == 0 && facts.Count == 0 && despawns.Count == 0)
                return;

            var batch = new EffectBatch
            {
                Writes = writes.ToArray(),
                FactDeltas = facts.ToArray(),
                Despawns = despawns.ToArray(),
                Reads = Array.Empty<ReadSetEntry>(),
                Spawns = Array.Empty<ThingSpawnRequest>(),
                PlanCooldowns = Array.Empty<PlanCooldownRequest>(),
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

        private static ThingId ParseThingId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return default;
            return new ThingId(raw.Trim());
        }
    }
}
