using System;

namespace DataDrivenGoap.Core
{
    public sealed class WorldTimeSnapshot
    {
        public double TotalWorldSeconds { get; }
        public double TotalWorldDays { get; }
        public double TimeScale { get; }
        public double SecondsPerDay { get; }
        public TimeSpan TimeOfDay { get; }
        public int DayOfYear { get; }
        public int DayOfMonth { get; }
        public int Month { get; }
        public int SeasonIndex { get; }
        public string SeasonName { get; }
        public int Year { get; }
        public int DaysPerMonth { get; }
        public int SeasonLengthDays { get; }
        public int DaysPerYear { get; }

        public double DayFraction => SecondsPerDay > 0 ? TimeOfDay.TotalSeconds / SecondsPerDay : 0.0;

        internal WorldTimeSnapshot(
            double totalWorldSeconds,
            double totalWorldDays,
            double timeScale,
            double secondsPerDay,
            double secondsIntoDay,
            int dayOfYear,
            int dayOfMonth,
            int month,
            int seasonIndex,
            string seasonName,
            int year,
            int daysPerMonth,
            int seasonLengthDays,
            int daysPerYear)
        {
            TotalWorldSeconds = totalWorldSeconds;
            TotalWorldDays = totalWorldDays;
            TimeScale = timeScale;
            SecondsPerDay = secondsPerDay;
            var clampedSeconds = secondsIntoDay;
            if (double.IsNaN(clampedSeconds) || double.IsInfinity(clampedSeconds))
                clampedSeconds = 0.0;
            if (clampedSeconds < 0) clampedSeconds = 0;
            if (secondsPerDay > 0 && clampedSeconds > secondsPerDay)
                clampedSeconds = secondsPerDay;
            TimeOfDay = TimeSpan.FromSeconds(clampedSeconds);
            DayOfYear = dayOfYear;
            DayOfMonth = dayOfMonth;
            Month = month;
            SeasonIndex = seasonIndex;
            SeasonName = seasonName ?? string.Empty;
            Year = year;
            DaysPerMonth = daysPerMonth;
            SeasonLengthDays = seasonLengthDays;
            DaysPerYear = daysPerYear;
        }
    }
}
