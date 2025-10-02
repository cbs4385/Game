using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using DataDrivenGoap.Concurrency;
using DataDrivenGoap.Core;
using DataDrivenGoap.Execution;
using DataDrivenGoap.Items;
using DataDrivenGoap.Simulation;
using DataDrivenGoap.World;

namespace DataDrivenGoap.Persistence
{
    public sealed class SnapshotApplier
    {
        private static readonly IReadOnlyDictionary<int, Action<SnapshotManifest>> MigrationTable =
            new Dictionary<int, Action<SnapshotManifest>>
            {
                { 1, _ => { } }
            };

        private readonly WorldClock _clock;
        private readonly ShardedWorld _world;
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
        private readonly IReadOnlyDictionary<string, ActorHost> _actors;
        private readonly SkillProgressionSystem _skills;
        private readonly QuestSystem _quests;
        private readonly JsonSerializerOptions _json;

        public SnapshotApplier(
            WorldClock clock,
            ShardedWorld world,
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
            SkillProgressionSystem skills,
            QuestSystem quests)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _world = world ?? throw new ArgumentNullException(nameof(world));
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
            _actors = BuildActorLookup(actors);
            _skills = skills;
            _quests = quests;
            _json = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        public void Load(Stream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var manifest = ReadEntry<SnapshotManifest>(archive, "manifest.json");
            if (manifest == null)
                throw new InvalidDataException("Snapshot manifest missing");
            EnsureVersionSupported(manifest);

            var clock = ReadChunk<ClockState>(archive, manifest, "clock.json");
            if (clock != null)
                ApplyClock(clock);

            var world = ReadChunk<WorldStateChunk>(archive, manifest, "world.json");
            if (world != null)
                _world.ApplyState(world);

            var inv = ReadChunk<InventorySystemState>(archive, manifest, "inventory.json");
            _inventory?.ApplyState(inv);

            var shops = ReadChunk<ShopSystemState>(archive, manifest, "shops.json");
            _shops?.ApplyState(shops);

            var crops = ReadChunk<CropSystemState>(archive, manifest, "crops.json");
            _crops?.ApplyState(crops);

            var animals = ReadChunk<AnimalSystemState>(archive, manifest, "animals.json");
            _animals?.ApplyState(animals);

            var fishing = ReadChunk<FishingSystemState>(archive, manifest, "fishing.json");
            _fishing?.ApplyState(fishing);

            var foraging = ReadChunk<ForagingSystemState>(archive, manifest, "foraging.json");
            _foraging?.ApplyState(foraging);

            var mining = ReadChunk<MiningSystemState>(archive, manifest, "mining.json");
            _mining?.ApplyState(mining);

            var weather = ReadChunk<WeatherSystemState>(archive, manifest, "weather.json");
            _weather?.ApplyState(weather);

            var calendar = ReadChunk<CalendarSystemState>(archive, manifest, "calendar.json");
            _calendar?.ApplyState(calendar);

            var reservations = ReadChunk<List<ReservationState>>(archive, manifest, "reservations.json");
            _reservations?.ApplyState(reservations);

            var skills = ReadChunk<SkillProgressionState>(archive, manifest, "skills.json");
            _skills?.ApplyState(skills);

            var quests = ReadChunk<QuestSystemState>(archive, manifest, "quests.json");
            _quests?.ApplyState(quests);

            var actorCollection = ReadChunk<ActorHostCollectionState>(archive, manifest, "actors.json");
            if (actorCollection?.actors != null)
            {
                foreach (var actor in actorCollection.actors)
                {
                    if (actor?.actorId == null)
                        continue;
                    if (_actors.TryGetValue(actor.actorId, out var host))
                        host.ApplyState(actor);
                }
            }
        }

        private static void EnsureVersionSupported(SnapshotManifest manifest)
        {
            if (manifest == null)
                throw new InvalidDataException("Snapshot manifest missing");
            if (!MigrationTable.TryGetValue(manifest.version, out var migrate) || migrate == null)
                throw new NotSupportedException($"Save version {manifest.version} is not supported. Supported versions: {string.Join(", ", MigrationTable.Keys)}");
            migrate(manifest);
        }

        private void ApplyClock(ClockState clock)
        {
            if (clock == null)
                return;
            var snapshot = new WorldTimeSnapshot(
                clock.totalWorldSeconds,
                clock.totalWorldDays,
                clock.timeScale,
                clock.secondsPerDay,
                clock.secondsIntoDay,
                clock.dayOfYear,
                clock.dayOfMonth,
                clock.month,
                clock.seasonIndex,
                clock.seasonName,
                clock.year,
                clock.daysPerMonth,
                clock.seasonLengthDays,
                clock.daysPerYear);
            _clock.ApplySnapshot(snapshot);
        }

        private T ReadEntry<T>(ZipArchive archive, string name)
        {
            if (archive == null || string.IsNullOrWhiteSpace(name))
                return default;
            var entry = archive.GetEntry(name);
            if (entry == null)
                return default;
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<T>(stream, _json);
        }

        private T ReadChunk<T>(ZipArchive archive, SnapshotManifest manifest, string name)
        {
            if (manifest == null || manifest.chunks == null)
                return default;
            if (!manifest.chunks.TryGetValue(name, out var path))
                return default;
            return ReadEntry<T>(archive, path);
        }

        private static IReadOnlyDictionary<string, ActorHost> BuildActorLookup(IEnumerable<ActorHost> actors)
        {
            var dict = new Dictionary<string, ActorHost>(StringComparer.OrdinalIgnoreCase);
            if (actors == null)
                return dict;
            foreach (var actor in actors)
            {
                if (actor == null)
                    continue;
                dict[actor.Id.Value] = actor;
            }
            return dict;
        }
    }
}
