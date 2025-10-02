using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using DataDrivenGoap.Concurrency;
using DataDrivenGoap.Core;
using DataDrivenGoap.Execution;
using DataDrivenGoap.Items;
using DataDrivenGoap.Simulation;
using DataDrivenGoap.World;

namespace DataDrivenGoap.Persistence
{
    public sealed class SnapshotBuilder
    {
        private readonly IWorld _world;
        private readonly WorldClock _clock;
        private readonly InventorySystem _inventory;
        private readonly ShopSystem _shops;
        private readonly CropSystem _crops;
        private readonly AnimalSystem _animals;
        private readonly FishingSystem _fishing;
        private readonly ForagingSystem _foraging;
        private readonly MiningSystem _mining;
        private readonly WeatherSystem _weather;
        private readonly CalendarEventSystem _calendar;
        private readonly ReservationService _reservations;
        private readonly IReadOnlyCollection<ActorHost> _actors;
        private readonly SkillProgressionSystem _skills;
        private readonly QuestSystem _quests;
        private readonly JsonSerializerOptions _json;

        public int Version { get; }

        public SnapshotBuilder(
            IWorld world,
            WorldClock clock,
            InventorySystem inventory,
            ShopSystem shops,
            CropSystem crops,
            AnimalSystem animals,
            FishingSystem fishing,
            ForagingSystem foraging,
            MiningSystem mining,
            WeatherSystem weather,
            CalendarEventSystem calendar,
            ReservationService reservations,
            IEnumerable<ActorHost> actors,
            int version = 1,
            SkillProgressionSystem skills = null,
            QuestSystem quests = null)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _inventory = inventory;
            _shops = shops;
            _crops = crops;
            _animals = animals;
            _fishing = fishing;
            _foraging = foraging;
            _mining = mining;
            _weather = weather;
            _calendar = calendar;
            _reservations = reservations;
            _actors = actors?.ToArray() ?? Array.Empty<ActorHost>();
            _skills = skills;
            _quests = quests;
            Version = Math.Max(1, version);
            _json = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = null
            };
        }

        public void WriteSnapshot(Stream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            var snapshot = _world.Snap();
            var manifest = new SnapshotManifest
            {
                version = Version,
                savedAtUtc = DateTime.UtcNow,
                tick = snapshot?.Version ?? 0,
            };

            AddChunk(archive, manifest, "clock.json", BuildClockState(snapshot));
            AddChunk(archive, manifest, "world.json", BuildWorldState());
            if (_inventory != null)
                AddChunk(archive, manifest, "inventory.json", _inventory.CaptureState());
            if (_shops != null)
                AddChunk(archive, manifest, "shops.json", _shops.CaptureState());
            if (_crops != null)
                AddChunk(archive, manifest, "crops.json", _crops.CaptureState());
            if (_animals != null)
                AddChunk(archive, manifest, "animals.json", _animals.CaptureState());
            if (_fishing != null)
                AddChunk(archive, manifest, "fishing.json", _fishing.CaptureState());
            if (_foraging != null)
                AddChunk(archive, manifest, "foraging.json", _foraging.CaptureState());
            if (_mining != null)
                AddChunk(archive, manifest, "mining.json", _mining.CaptureState());
            if (_weather != null)
                AddChunk(archive, manifest, "weather.json", _weather.CaptureState());
            if (_calendar != null)
                AddChunk(archive, manifest, "calendar.json", _calendar.CaptureState());
            if (_reservations != null)
                AddChunk(archive, manifest, "reservations.json", _reservations.CaptureState());
            if (_skills != null)
                AddChunk(archive, manifest, "skills.json", _skills.CaptureState());
            if (_quests != null)
                AddChunk(archive, manifest, "quests.json", _quests.CaptureState());
            if (_actors.Count > 0)
            {
                var actorState = new ActorHostCollectionState
                {
                    actors = _actors
                        .Select(a => a?.CaptureState())
                        .Where(s => s != null)
                        .ToList()
                };
                AddChunk(archive, manifest, "actors.json", actorState);
            }

            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using (var writer = new Utf8JsonWriter(manifestEntry.Open(), new JsonWriterOptions { Indented = true }))
            {
                JsonSerializer.Serialize(writer, manifest, _json);
            }
        }

        private ClockState BuildClockState(IWorldSnapshot snapshot)
        {
            var time = snapshot?.Time ?? _clock.Snapshot();
            return new ClockState
            {
                totalWorldSeconds = time.TotalWorldSeconds,
                totalWorldDays = time.TotalWorldDays,
                timeScale = time.TimeScale,
                secondsPerDay = time.SecondsPerDay,
                secondsIntoDay = time.TimeOfDay.TotalSeconds,
                dayOfYear = time.DayOfYear,
                dayOfMonth = time.DayOfMonth,
                month = time.Month,
                seasonIndex = time.SeasonIndex,
                seasonName = time.SeasonName,
                year = time.Year,
                daysPerMonth = time.DaysPerMonth,
                seasonLengthDays = time.SeasonLengthDays,
                daysPerYear = time.DaysPerYear,
            };
        }

        private WorldStateChunk BuildWorldState()
        {
            if (!(_world is ShardedWorld sharded))
                throw new InvalidOperationException("SnapshotBuilder requires a ShardedWorld instance.");
            return sharded.CaptureState();
        }

        private void AddChunk(ZipArchive archive, SnapshotManifest manifest, string fileName, object payload)
        {
            if (payload == null)
                return;
            var entry = archive.CreateEntry(fileName, CompressionLevel.Optimal);
            using (var stream = entry.Open())
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                JsonSerializer.Serialize(writer, payload, payload.GetType(), _json);
            }
            manifest.chunks[fileName] = fileName;
        }
    }
}
