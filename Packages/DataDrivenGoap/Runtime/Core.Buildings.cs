using System;
using System.Collections.Generic;
using System.Linq;

namespace DataDrivenGoap.Core
{
    public readonly struct RectInt : IEquatable<RectInt>
    {
        public int MinX { get; }
        public int MinY { get; }
        public int MaxX { get; }
        public int MaxY { get; }

        public bool IsEmpty => MaxX < MinX || MaxY < MinY;

        public RectInt(int minX, int minY, int maxX, int maxY)
        {
            if (maxX < minX)
                throw new ArgumentException("maxX must be greater than or equal to minX", nameof(maxX));
            if (maxY < minY)
                throw new ArgumentException("maxY must be greater than or equal to minY", nameof(maxY));
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        public bool Contains(GridPos pos)
        {
            if (IsEmpty) return false;
            return pos.X >= MinX && pos.X <= MaxX && pos.Y >= MinY && pos.Y <= MaxY;
        }

        public bool Equals(RectInt other) => MinX == other.MinX && MinY == other.MinY && MaxX == other.MaxX && MaxY == other.MaxY;
        public override bool Equals(object obj) => obj is RectInt r && Equals(r);
        public override int GetHashCode() => HashCode.Combine(MinX, MinY, MaxX, MaxY);
        public override string ToString() => IsEmpty ? "<empty>" : $"[{MinX},{MinY}]-[{MaxX},{MaxY}]";
    }

    public sealed class BuildingOpenHours
    {
        public IReadOnlyCollection<int> DaysOfWeek { get; }
        public IReadOnlyCollection<string> Seasons { get; }
        public double StartHour { get; }
        public double EndHour { get; }

        public BuildingOpenHours(IEnumerable<int> daysOfWeek, IEnumerable<string> seasons, double startHour, double endHour)
        {
            DaysOfWeek = daysOfWeek?.Distinct().ToArray() ?? Array.Empty<int>();
            Seasons = seasons?.Select(s => (s ?? string.Empty).Trim().ToLowerInvariant()).Where(s => s.Length > 0).Distinct().ToArray() ?? Array.Empty<string>();
            StartHour = ClampHour(startHour);
            EndHour = ClampHour(endHour);
        }

        private static double ClampHour(double hour)
        {
            if (double.IsNaN(hour) || double.IsInfinity(hour)) return 0.0;
            if (hour < 0.0) return 0.0;
            if (hour > 24.0) return 24.0;
            return hour;
        }

        public bool MatchesDay(int dayOfWeek)
        {
            if (DaysOfWeek.Count == 0) return true;
            return DaysOfWeek.Contains(dayOfWeek);
        }

        public bool MatchesSeason(string season)
        {
            if (Seasons.Count == 0) return true;
            if (string.IsNullOrWhiteSpace(season)) return false;
            return Seasons.Contains(season.Trim().ToLowerInvariant());
        }

        public bool IsOpen(double hour)
        {
            hour = ClampHour(hour);
            if (Math.Abs(StartHour - EndHour) < 1e-6)
                return true; // open all day
            if (StartHour < EndHour)
                return hour >= StartHour && hour < EndHour;
            // overnight schedule, wraps past midnight
            return hour >= StartHour || hour < EndHour;
        }
    }

    public sealed class BuildingInfo
    {
        public RectInt? Area { get; }
        public bool IsOpenFlag { get; }
        public int Capacity { get; }
        public IReadOnlyList<GridPos> ServicePoints { get; }
        public IReadOnlyList<BuildingOpenHours> OpenHours { get; }

        private readonly GridPos[] _servicePoints;
        private readonly BuildingOpenHours[] _openHours;

        public BuildingInfo(RectInt? area, bool isOpenFlag, int capacity, IEnumerable<GridPos> servicePoints, IEnumerable<BuildingOpenHours> openHours)
        {
            Area = area;
            IsOpenFlag = isOpenFlag;
            Capacity = capacity < 0 ? 0 : capacity;
            _servicePoints = servicePoints?.ToArray() ?? Array.Empty<GridPos>();
            _openHours = openHours?.ToArray() ?? Array.Empty<BuildingOpenHours>();
            ServicePoints = _servicePoints;
            OpenHours = _openHours;
        }

        private BuildingInfo(RectInt? area, bool isOpenFlag, int capacity, GridPos[] servicePoints, BuildingOpenHours[] openHours)
        {
            Area = area;
            IsOpenFlag = isOpenFlag;
            Capacity = capacity < 0 ? 0 : capacity;
            _servicePoints = servicePoints ?? Array.Empty<GridPos>();
            _openHours = openHours ?? Array.Empty<BuildingOpenHours>();
            ServicePoints = _servicePoints;
            OpenHours = _openHours;
        }

        public BuildingInfo WithOpenFlag(bool isOpen)
        {
            if (isOpen == IsOpenFlag)
                return this;
            return new BuildingInfo(Area, isOpen, Capacity, _servicePoints, _openHours);
        }

        public bool IsOpen(WorldTimeSnapshot time)
        {
            if (!IsOpenFlag)
                return false;
            if (time == null)
                return IsOpenFlag;
            if (OpenHours.Count == 0)
                return true;
            double hour = time.TimeOfDay.TotalHours;
            int day = ((time.DayOfYear - 1) % 7 + 7) % 7;
            string season = time.SeasonName ?? string.Empty;
            foreach (var window in OpenHours)
            {
                if (!window.MatchesDay(day))
                    continue;
                if (!window.MatchesSeason(season))
                    continue;
                if (window.IsOpen(hour))
                    return true;
            }
            return false;
        }
    }
}
