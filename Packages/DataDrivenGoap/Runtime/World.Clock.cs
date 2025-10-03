using System;
using System.Linq;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;

namespace DataDrivenGoap.World
{
    public sealed class WorldClock
    {
        private sealed class CalendarDefinition
        {
            public double RealSecondsPerDay { get; }
            public double WorldSecondsPerDay { get; }
            public double TimeScale { get; }
            public int DaysPerMonth { get; }
            public int SeasonLengthDays { get; }
            public string[] Seasons { get; }
            public int StartYear { get; }
            public int StartDayOfYear { get; }
            public double StartTimeOfDaySeconds { get; }

            public int DaysPerYear => SeasonLengthDays * Seasons.Length;
            public double StartOffsetSeconds => ((StartDayOfYear - 1) * WorldSecondsPerDay) + StartTimeOfDaySeconds;

            public CalendarDefinition(TimeConfig config)
            {
                if (config == null) throw new ArgumentNullException(nameof(config));

                if (double.IsNaN(config.dayLengthSeconds) || double.IsInfinity(config.dayLengthSeconds) || config.dayLengthSeconds <= 0)
                    throw new ArgumentException("Time configuration requires a positive finite dayLengthSeconds", nameof(config));
                RealSecondsPerDay = config.dayLengthSeconds;

                if (config.worldHoursPerDay <= 0)
                    throw new ArgumentException("Time configuration requires worldHoursPerDay to be greater than zero", nameof(config));
                if (config.minutesPerHour <= 0)
                    throw new ArgumentException("Time configuration requires minutesPerHour to be greater than zero", nameof(config));
                if (config.secondsPerMinute <= 0)
                    throw new ArgumentException("Time configuration requires secondsPerMinute to be greater than zero", nameof(config));

                int hours = config.worldHoursPerDay;
                int minutes = config.minutesPerHour;
                int seconds = config.secondsPerMinute;

                WorldSecondsPerDay = (double)hours * minutes * seconds;
                if (double.IsNaN(WorldSecondsPerDay) || double.IsInfinity(WorldSecondsPerDay) || WorldSecondsPerDay <= 0)
                    throw new ArgumentException("Computed world seconds per day must be a positive finite number", nameof(config));
                TimeScale = WorldSecondsPerDay / RealSecondsPerDay;

                if (config.daysPerMonth <= 0)
                    throw new ArgumentException("Time configuration requires daysPerMonth to be greater than zero", nameof(config));
                DaysPerMonth = config.daysPerMonth;

                if (config.seasonLengthDays <= 0)
                    throw new ArgumentException("Time configuration requires seasonLengthDays to be greater than zero", nameof(config));
                if (config.seasonLengthDays < DaysPerMonth)
                    throw new ArgumentException("seasonLengthDays must be greater than or equal to daysPerMonth", nameof(config));
                if (config.seasonLengthDays % DaysPerMonth != 0)
                    throw new ArgumentException("seasonLengthDays must be an integer multiple of daysPerMonth", nameof(config));
                SeasonLengthDays = config.seasonLengthDays;

                if (config.seasons == null)
                    throw new ArgumentException("Time configuration must include a seasons array", nameof(config));
                Seasons = config.seasons
                    .Select(season => season ?? throw new ArgumentException("Seasons cannot contain null entries", nameof(config)))
                    .Select(season => season.Trim())
                    .ToArray();
                if (Seasons.Length == 0 || Seasons.Any(string.IsNullOrWhiteSpace))
                    throw new ArgumentException("Time configuration must specify non-empty season names", nameof(config));

                if (config.startYear <= 0)
                    throw new ArgumentException("Time configuration requires startYear to be greater than zero", nameof(config));
                StartYear = config.startYear;

                int daysPerYear = DaysPerYear;
                if (daysPerYear <= 0)
                    throw new ArgumentException("Computed days per year must be greater than zero", nameof(config));

                if (config.startDayOfYear <= 0 || config.startDayOfYear > daysPerYear)
                    throw new ArgumentException("startDayOfYear must fall within the computed calendar year", nameof(config));
                StartDayOfYear = config.startDayOfYear;

                if (!config.startTimeOfDayHours.HasValue)
                    throw new ArgumentException("Time configuration must provide startTimeOfDayHours", nameof(config));
                double startHours = config.startTimeOfDayHours.Value;
                if (double.IsNaN(startHours) || double.IsInfinity(startHours))
                    throw new ArgumentException("startTimeOfDayHours must be a finite number", nameof(config));
                if (startHours < 0 || startHours > hours)
                    throw new ArgumentException("startTimeOfDayHours must fall within the configured day length", nameof(config));
                StartTimeOfDaySeconds = startHours * minutes * seconds;
            }

            public WorldTimeSnapshot BuildSnapshot(double elapsedRealSeconds)
            {
                if (double.IsNaN(elapsedRealSeconds) || double.IsInfinity(elapsedRealSeconds))
                    elapsedRealSeconds = 0.0;
                if (elapsedRealSeconds < 0) elapsedRealSeconds = 0.0;

                double totalWorldSeconds = (elapsedRealSeconds * TimeScale) + StartOffsetSeconds;
                return BuildSnapshotFromWorldSeconds(totalWorldSeconds);
            }

            public WorldTimeSnapshot BuildSnapshotFromWorldSeconds(double totalWorldSeconds)
            {
                if (double.IsNaN(totalWorldSeconds) || double.IsInfinity(totalWorldSeconds))
                    totalWorldSeconds = 0.0;
                if (totalWorldSeconds < 0.0)
                    totalWorldSeconds = 0.0;

                double totalWorldDays = totalWorldSeconds / WorldSecondsPerDay;

                long wholeDays = (long)Math.Floor(totalWorldDays);
                double secondsIntoDay = totalWorldSeconds - (wholeDays * WorldSecondsPerDay);
                if (secondsIntoDay < 0) secondsIntoDay = 0;

                int daysPerYear = DaysPerYear <= 0 ? 1 : DaysPerYear;
                long absoluteDayIndex = wholeDays;
                long startOffsetDays = StartDayOfYear - 1;
                long totalDaysWithOffset = absoluteDayIndex + startOffsetDays;

                int year = StartYear;
                if (daysPerYear > 0)
                {
                    year += (int)(totalDaysWithOffset / daysPerYear);
                }

                int dayOfYear;
                if (daysPerYear > 0)
                {
                    dayOfYear = (int)(totalDaysWithOffset % daysPerYear) + 1;
                }
                else
                {
                    dayOfYear = 1;
                }

                int month = (DaysPerMonth > 0) ? ((dayOfYear - 1) / DaysPerMonth) + 1 : 1;
                int dayOfMonth = (DaysPerMonth > 0) ? ((dayOfYear - 1) % DaysPerMonth) + 1 : dayOfYear;

                int seasonIndex = (SeasonLengthDays > 0) ? Math.Min(Seasons.Length - 1, (dayOfYear - 1) / SeasonLengthDays) : 0;
                if (seasonIndex < 0) seasonIndex = 0;
                string seasonName = Seasons.Length > 0 ? Seasons[seasonIndex] : string.Empty;

                return new WorldTimeSnapshot(
                    totalWorldSeconds,
                    totalWorldDays,
                    TimeScale,
                    WorldSecondsPerDay,
                    secondsIntoDay,
                    dayOfYear,
                    dayOfMonth,
                    month,
                    seasonIndex,
                    seasonName,
                    year,
                    DaysPerMonth,
                    SeasonLengthDays,
                    DaysPerYear <= 0 ? SeasonLengthDays : DaysPerYear);
            }
        }

        private readonly CalendarDefinition _calendar;
        private readonly DateTime _startUtc;
        private readonly object _gate = new object();
        private double? _overrideWorldSeconds;
        private DateTime? _overrideAppliedUtc;

        public double SecondsPerDay => _calendar.WorldSecondsPerDay;
        public double TimeScale => _calendar.TimeScale;
        public int DaysPerMonth => _calendar.DaysPerMonth;
        public int SeasonLengthDays => _calendar.SeasonLengthDays;
        public string[] Seasons => _calendar.Seasons.ToArray();

        public WorldClock(TimeConfig config)
        {
            _calendar = new CalendarDefinition(config);
            _startUtc = DateTime.UtcNow;
        }

        public WorldTimeSnapshot Snapshot()
        {
            lock (_gate)
            {
                if (_overrideWorldSeconds.HasValue)
                {
                    double baseSeconds = Math.Max(0.0, _overrideWorldSeconds.Value);
                    double elapsedReal = 0.0;
                    if (_overrideAppliedUtc.HasValue)
                    {
                        elapsedReal = (DateTime.UtcNow - _overrideAppliedUtc.Value).TotalSeconds;
                        if (double.IsNaN(elapsedReal) || double.IsInfinity(elapsedReal) || elapsedReal < 0.0)
                            elapsedReal = 0.0;
                    }
                    double totalWorldSeconds = baseSeconds + (elapsedReal * _calendar.TimeScale);
                    return _calendar.BuildSnapshotFromWorldSeconds(totalWorldSeconds);
                }

                var now = DateTime.UtcNow;
                var elapsed = (now - _startUtc).TotalSeconds;
                return _calendar.BuildSnapshot(elapsed);
            }
        }

        public void ApplySnapshot(WorldTimeSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            lock (_gate)
            {
                double totalSeconds = snapshot.TotalWorldSeconds;
                if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds) || totalSeconds < 0.0)
                    totalSeconds = 0.0;
                _overrideWorldSeconds = totalSeconds;
                _overrideAppliedUtc = DateTime.UtcNow;
            }
        }
    }
}
