using System;
using System.Collections.Generic;
using System.Linq;
using DataDrivenGoap.Core;

namespace DataDrivenGoap.Simulation
{
    public sealed class RoleScheduleBlock
    {
        public string RoleId { get; }
        public string Task { get; }
        public string GotoTag { get; }
        public double StartHour { get; }
        public double EndHour { get; }
        public IReadOnlyCollection<int> DaysOfWeek { get; }
        public IReadOnlyCollection<string> Seasons { get; }
        public string Label { get; }

        public RoleScheduleBlock(string roleId, string gotoTag, string task, double startHour, double endHour, IEnumerable<int> daysOfWeek, IEnumerable<string> seasons, string label = null)
        {
            RoleId = roleId ?? string.Empty;
            GotoTag = gotoTag ?? string.Empty;
            Task = task ?? string.Empty;
            StartHour = NormalizeHour(startHour);
            EndHour = NormalizeHour(endHour);
            DaysOfWeek = daysOfWeek?.Distinct().ToArray() ?? Array.Empty<int>();
            Seasons = seasons?.Select(s => (s ?? string.Empty).Trim().ToLowerInvariant()).Where(s => s.Length > 0).Distinct().ToArray() ?? Array.Empty<string>();
            Label = string.IsNullOrWhiteSpace(label) ? Task : label.Trim();
        }

        public bool WrapsMidnight => StartHour > EndHour && Math.Abs(StartHour - EndHour) > 1e-6;

        private static double NormalizeHour(double hour)
        {
            if (double.IsNaN(hour) || double.IsInfinity(hour)) return 0.0;
            if (hour < 0.0) return 0.0;
            if (hour > 24.0) return 24.0;
            return hour;
        }

        public bool MatchesDay(int dayOfWeek)
        {
            if (DaysOfWeek.Count == 0) return true;
            return DaysOfWeek.Contains(((dayOfWeek % 7) + 7) % 7);
        }

        public bool MatchesSeason(string season)
        {
            if (Seasons.Count == 0) return true;
            if (string.IsNullOrWhiteSpace(season)) return false;
            return Seasons.Contains(season.Trim().ToLowerInvariant());
        }
    }

    public sealed class RoleScheduleDefinition
    {
        public string RoleId { get; }
        public IReadOnlyList<RoleScheduleBlock> Blocks { get; }

        public RoleScheduleDefinition(string roleId, IEnumerable<RoleScheduleBlock> blocks)
        {
            RoleId = roleId ?? string.Empty;
            Blocks = (blocks ?? Array.Empty<RoleScheduleBlock>()).Where(b => b != null).ToArray();
        }
    }

    public sealed class RoleScheduleEvaluation
    {
        public RoleScheduleDefinition Schedule { get; }
        public RoleScheduleBlock ActiveBlock { get; }
        public RoleScheduleBlock UpcomingBlock { get; }
        public ThingId TargetId { get; }
        public double MinutesIntoBlock { get; }
        public double MinutesRemaining { get; }
        public double MinutesUntilStart { get; }
        public string EffectiveTask { get; }
        public string EffectiveGotoTag { get; }
        public string ActiveEventId { get; }
        public string ActiveEventLabel { get; }

        public bool HasActiveBlock => ActiveBlock != null;
        public bool HasUpcomingBlock => UpcomingBlock != null;

        internal RoleScheduleEvaluation(
            RoleScheduleDefinition schedule,
            RoleScheduleBlock active,
            RoleScheduleBlock upcoming,
            ThingId target,
            double minutesInto,
            double minutesRemaining,
            double minutesUntilStart,
            string effectiveTask,
            string effectiveGotoTag,
            string eventId,
            string eventLabel)
        {
            Schedule = schedule;
            ActiveBlock = active;
            UpcomingBlock = upcoming;
            TargetId = target;
            MinutesIntoBlock = minutesInto;
            MinutesRemaining = minutesRemaining;
            MinutesUntilStart = minutesUntilStart;
            EffectiveTask = effectiveTask ?? string.Empty;
            EffectiveGotoTag = effectiveGotoTag ?? string.Empty;
            ActiveEventId = eventId ?? string.Empty;
            ActiveEventLabel = eventLabel ?? string.Empty;
        }
    }

    public sealed class RoleScheduleService
    {
        private readonly Dictionary<ThingId, RoleScheduleDefinition> _assignments = new Dictionary<ThingId, RoleScheduleDefinition>();
        private readonly Dictionary<ThingId, Dictionary<string, ThingId>> _actorTargets = new Dictionary<ThingId, Dictionary<string, ThingId>>();
        private readonly object _gate = new object();

        public IWeatherQuery WeatherQuery { get; set; }
        public ICalendarEventQuery EventQuery { get; set; }

        public void Register(ThingId actor, RoleScheduleDefinition role)
        {
            if (string.IsNullOrEmpty(actor.Value))
                throw new ArgumentException("Actor id cannot be blank", nameof(actor));
            if (role == null || role.Blocks.Count == 0)
                return;
            lock (_gate)
            {
                _assignments[actor] = role;
            }
        }

        public void RegisterAssignments(ThingId actor, IReadOnlyDictionary<string, ThingId> assignments)
        {
            if (string.IsNullOrEmpty(actor.Value))
                throw new ArgumentException("Actor id cannot be blank", nameof(actor));
            if (assignments == null || assignments.Count == 0)
                return;

            lock (_gate)
            {
                if (!_actorTargets.TryGetValue(actor, out var map))
                {
                    map = new Dictionary<string, ThingId>(StringComparer.OrdinalIgnoreCase);
                    _actorTargets[actor] = map;
                }

                foreach (var kv in assignments)
                {
                    if (kv.Key == null)
                        continue;
                    string key = kv.Key.Trim();
                    if (key.Length == 0)
                        continue;
                    if (string.IsNullOrEmpty(kv.Value.Value))
                        map.Remove(key);
                    else
                        map[key] = kv.Value;
                }

                if (map.Count == 0)
                    _actorTargets.Remove(actor);
            }
        }

        public bool TryGetSchedule(ThingId actor, out RoleScheduleDefinition schedule)
        {
            lock (_gate)
            {
                return _assignments.TryGetValue(actor, out schedule);
            }
        }

        public RoleScheduleEvaluation Evaluate(IWorldSnapshot snapshot, ThingId actor)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            if (!TryGetSchedule(actor, out var schedule) || schedule == null)
                return null;

            if (snapshot.Time == null)
                return null;

            double hour = snapshot.Time.TimeOfDay.TotalHours;
            int day = ((snapshot.Time.DayOfYear - 1) % 7 + 7) % 7;
            string season = snapshot.Time.SeasonName ?? string.Empty;

            RoleScheduleBlock activeBlock = null;
            double minutesInto = 0.0;
            double minutesRemaining = 0.0;

            foreach (var block in schedule.Blocks)
            {
                if (!block.MatchesSeason(season))
                    continue;
                if (IsBlockActive(block, day, hour, season, out minutesInto, out minutesRemaining))
                {
                    activeBlock = block;
                    break;
                }
            }

            RoleScheduleBlock upcomingBlock = null;
            double minutesUntilStart = double.PositiveInfinity;
            if (activeBlock == null)
            {
                foreach (var block in schedule.Blocks)
                {
                    if (!block.MatchesSeason(season))
                        continue;
                    double diff = MinutesUntilStart(block, day, hour);
                    if (double.IsNaN(diff) || double.IsInfinity(diff))
                        continue;
                    if (diff < minutesUntilStart)
                    {
                        minutesUntilStart = diff;
                        upcomingBlock = block;
                    }
                }
            }

            var relevantBlock = activeBlock ?? upcomingBlock;
            if (relevantBlock == null)
                return null;

            string gotoTag = relevantBlock.GotoTag;
            string effectiveTask = relevantBlock.Task;
            string eventId = string.Empty;
            string eventLabel = string.Empty;

            if (WeatherQuery?.CurrentWeather.CancelOutdoorShifts ?? false)
                gotoTag = "indoor_common";

            if (EventQuery != null && EventQuery.TryGetScheduleDirective(snapshot, actor, schedule, relevantBlock, effectiveTask, gotoTag, out var directive))
            {
                if (!string.IsNullOrWhiteSpace(directive.TaskId))
                    effectiveTask = directive.TaskId;
                if (!string.IsNullOrWhiteSpace(directive.GotoTag))
                    gotoTag = directive.GotoTag;
                eventId = directive.EventId ?? string.Empty;
                eventLabel = directive.EventLabel ?? string.Empty;
            }

            var targetId = FindNearestTarget(snapshot, actor, gotoTag);
            return new RoleScheduleEvaluation(
                schedule,
                activeBlock,
                upcomingBlock,
                targetId ?? default,
                minutesInto,
                minutesRemaining,
                double.IsPositiveInfinity(minutesUntilStart) ? double.PositiveInfinity : minutesUntilStart,
                effectiveTask,
                gotoTag ?? string.Empty,
                eventId,
                eventLabel);
        }

        public bool TryEvaluate(IWorldSnapshot snapshot, ThingId actor, out RoleScheduleEvaluation evaluation)
        {
            evaluation = Evaluate(snapshot, actor);
            return evaluation != null;
        }

        private static bool IsBlockActive(RoleScheduleBlock block, int currentDay, double hour, string season, out double minutesInto, out double minutesRemaining)
        {
            minutesInto = 0.0;
            minutesRemaining = 0.0;
            if (block == null)
                return false;
            if (!block.MatchesSeason(season))
                return false;

            double start = block.StartHour;
            double end = block.EndHour;
            bool wraps = block.WrapsMidnight;

            bool matchesToday = block.MatchesDay(currentDay);
            int previousDay = (currentDay + 6) % 7;
            bool matchesPrevious = block.MatchesDay(previousDay);

            if (Math.Abs(start - end) < 1e-6)
            {
                if (!matchesToday)
                    return false;
                minutesInto = hour * 60.0;
                minutesRemaining = (24.0 - hour) * 60.0;
                return true;
            }

            if (!wraps)
            {
                if (!matchesToday)
                    return false;
                if (hour < start || hour >= end)
                    return false;
                minutesInto = (hour - start) * 60.0;
                minutesRemaining = (end - hour) * 60.0;
                return true;
            }

            if (matchesToday && hour >= start)
            {
                minutesInto = (hour - start) * 60.0;
                minutesRemaining = ((24.0 - hour) + end) * 60.0;
                return true;
            }

            if (matchesPrevious && hour < end)
            {
                minutesInto = ((24.0 - start) + hour) * 60.0;
                minutesRemaining = (end - hour) * 60.0;
                return true;
            }

            return false;
        }

        private static double MinutesUntilStart(RoleScheduleBlock block, int currentDay, double hour)
        {
            if (block == null)
                return double.PositiveInfinity;

            double start = block.StartHour;
            double end = block.EndHour;
            bool wraps = block.WrapsMidnight;

            if (Math.Abs(start - end) < 1e-6)
            {
                return block.MatchesDay(currentDay) ? 0.0 : double.PositiveInfinity;
            }

            if (!block.MatchesDay(currentDay))
                return double.PositiveInfinity;

            if (!wraps)
            {
                if (hour <= start)
                    return (start - hour) * 60.0;
                return double.PositiveInfinity;
            }

            if (hour < start)
                return (start - hour) * 60.0;

            if (hour >= start)
                return double.PositiveInfinity;

            return double.PositiveInfinity;
        }

        private ThingId? ResolveAssignmentTarget(IWorldSnapshot snapshot, ThingId actor, string key)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(actor.Value) || string.IsNullOrWhiteSpace(key))
                return null;

            ThingId candidate = default;
            bool found;
            lock (_gate)
            {
                found = _actorTargets.TryGetValue(actor, out var map) && map.TryGetValue(key, out candidate) && !string.IsNullOrEmpty(candidate.Value);
            }

            if (!found)
                return null;

            return snapshot.GetThing(candidate) != null ? candidate : (ThingId?)null;
        }

        private ThingId? FindNearestTarget(IWorldSnapshot snapshot, ThingId actor, string tag)
        {
            if (snapshot == null)
                return null;
            if (string.IsNullOrWhiteSpace(tag))
                return null;

            var actorView = snapshot.GetThing(actor);
            if (actorView == null)
                return null;

            string normalized = tag.Trim();
            if (normalized.StartsWith("thing:", StringComparison.OrdinalIgnoreCase))
            {
                string id = normalized.Substring(6).Trim();
                if (id.Length == 0)
                    return null;
                var thingId = new ThingId(id);
                return snapshot.GetThing(thingId) != null ? thingId : (ThingId?)null;
            }

            if (normalized.StartsWith("assignment:", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring("assignment:".Length).Trim();
            }

            if (!string.IsNullOrWhiteSpace(normalized))
            {
                var assigned = ResolveAssignmentTarget(snapshot, actor, normalized);
                if (assigned.HasValue)
                    return assigned.Value;
            }

            if (normalized.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            {
                string typeName = normalized.Substring(5).Trim();
                if (typeName.Length == 0)
                    return null;
                return FindNearestByPredicate(snapshot, actorView, candidate => string.Equals(candidate.Type, typeName, StringComparison.OrdinalIgnoreCase));
            }

            return FindNearestByPredicate(snapshot, actorView, candidate =>
                candidate.Tags != null && candidate.Tags.Any(t => string.Equals(t, normalized, StringComparison.OrdinalIgnoreCase)));
        }

        private static ThingId? FindNearestByPredicate(IWorldSnapshot snapshot, ThingView actorView, Func<ThingView, bool> predicate)
        {
            ThingView best = null;
            int bestDist = int.MaxValue;
            foreach (var candidate in snapshot.AllThings())
            {
                if (candidate == null)
                    continue;
                if (!predicate(candidate))
                    continue;
                int dist = GridPos.Manhattan(actorView.Position, candidate.Position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = candidate;
                }
            }
            return best?.Id;
        }
    }
}
