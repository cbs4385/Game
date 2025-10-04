using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DataDrivenGoap.Config;
using DataDrivenGoap.Concurrency;
using DataDrivenGoap.Core;
using DataDrivenGoap.Execution;
using DataDrivenGoap.Items;
using DataDrivenGoap.Planning;
using DataDrivenGoap.Simulation;
using DataDrivenGoap.Social;
using DataDrivenGoap.World;
using UnityEngine;
using RectInt = DataDrivenGoap.Core.RectInt;

public sealed class GoapSimulationBootstrapper : MonoBehaviour
{
    [SerializeField] private string datasetRelativePath = "Packages/DataDrivenGoap/Runtime/Data";
    [SerializeField] private string demoSettingsFile = "demo.settings.json";
    [SerializeField] private string actionsFile = "actions.json";
    [SerializeField] private string goalsFile = "goals.json";

    private sealed class ThingSeed
    {
        public ThingId Id;
        public string Type = string.Empty;
        public GridPos Position;
        public BuildingInfo Building;
        public readonly List<string> Tags = new List<string>();
        public readonly Dictionary<string, double> Attributes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class TileClassification
    {
        public bool[,] Walkable;
        public bool[,] Water;
        public bool[,] Shallow;
        public bool[,] Forest;
        public bool[,] Farmland;
        public bool[,] Coastal;
    }

    private sealed class Color32Comparer : IEqualityComparer<Color32>
    {
        public bool Equals(Color32 x, Color32 y) => x.r == y.r && x.g == y.g && x.b == y.b;
        public int GetHashCode(Color32 obj) => (obj.r << 16) | (obj.g << 8) | obj.b;
    }

    public sealed class SimulationReadyEventArgs : EventArgs
    {
        public SimulationReadyEventArgs(
            ShardedWorld world,
            IReadOnlyList<(ThingId Id, VillagePawn Pawn)> actors,
            string datasetRoot,
            Texture2D mapTexture,
            WorldClock clock,
            IReadOnlyDictionary<ThingId, ActorHostDiagnostics> actorDiagnostics,
            string cameraPawnId)
        {
            World = world ?? throw new ArgumentNullException(nameof(world));
            ActorDefinitions = actors ?? throw new ArgumentNullException(nameof(actors));
            DatasetRoot = datasetRoot ?? throw new ArgumentNullException(nameof(datasetRoot));
            MapTexture = mapTexture ?? throw new ArgumentNullException(nameof(mapTexture));
            Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            ActorDiagnostics = actorDiagnostics ?? throw new ArgumentNullException(nameof(actorDiagnostics));
            CameraPawnId = string.IsNullOrWhiteSpace(cameraPawnId) ? null : cameraPawnId.Trim();
        }

        public ShardedWorld World { get; }
        public IReadOnlyList<(ThingId Id, VillagePawn Pawn)> ActorDefinitions { get; }
        public string DatasetRoot { get; }
        public Texture2D MapTexture { get; }
        public WorldClock Clock { get; }
        public IReadOnlyDictionary<ThingId, ActorHostDiagnostics> ActorDiagnostics { get; }
        public string CameraPawnId { get; }
    }

    public event EventHandler<SimulationReadyEventArgs> Bootstrapped;

    private readonly List<ActorHost> _actorHosts = new List<ActorHost>();
    private readonly Dictionary<ThingId, ActorHost> _actorHostById = new Dictionary<ThingId, ActorHost>();
    private readonly List<(ThingId Id, VillagePawn Pawn)> _actorDefinitions = new List<(ThingId, VillagePawn)>();
    private readonly Dictionary<ThingId, ActorHostDiagnostics> _actorDiagnostics = new Dictionary<ThingId, ActorHostDiagnostics>();
    private readonly Dictionary<string, ThingId> _locationToThing = new Dictionary<string, ThingId>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ThingId, ThingSeed> _seedByThing = new Dictionary<ThingId, ThingSeed>();

    private SimulationReadyEventArgs _readyEventArgs;
    private ShardedWorld _world;
    private WorldClock _clock;
    private NeedScheduler _needScheduler;
    private ReservationService _reservations;
    private JsonDrivenPlanner _planner;
    private ExecutorRegistry _executors;
    private InventorySystem _inventorySystem;
    private ItemCatalog _itemCatalog;
    private CraftingSystem _craftingSystem;
    private ShopSystem _shopSystem;
    private CropSystem _cropSystem;
    private AnimalSystem _animalSystem;
    private FishingSystem _fishingSystem;
    private ForagingSystem _foragingSystem;
    private MiningSystem _miningSystem;
    private WeatherSystem _weatherSystem;
    private CalendarEventSystem _calendarSystem;
    private RoleScheduleService _scheduleService;
    private SocialRelationshipSystem _socialSystem;
    private SkillProgressionSystem _skillSystem;
    private QuestSystem _questSystem;
    private DialogueDatabaseConfig _dialogueDatabase;
    private ScheduleDatabaseConfig _scheduleDatabase;
    private DemoConfig _demoConfig;
    private TileClassification _tiles;
    private Texture2D _mapTexture;
    private List<ActionConfig> _actionConfigs;
    private List<GoalConfig> _goalConfigs;
    private string[] _needAttributeNames = Array.Empty<string>();

    private bool _simulationRunning;

    private void Awake()
    {
        Bootstrap();
    }

    public bool HasBootstrapped => _readyEventArgs != null;

    public SimulationReadyEventArgs LatestBootstrap => _readyEventArgs ?? throw new InvalidOperationException("Bootstrap has not completed yet.");

    public IReadOnlyList<string> NeedAttributeNames => _needAttributeNames;

    public bool TryGetActorPlanStatus(ThingId actorId, out ActorPlanStatus status)
    {
        if (string.IsNullOrWhiteSpace(actorId.Value))
        {
            status = null;
            return false;
        }

        if (!_actorHostById.TryGetValue(actorId, out var host) || host == null)
        {
            status = null;
            return false;
        }

        status = host.SnapshotPlanStatus();
        return status != null;
    }

    private void Start()
    {
        StartSimulation();
    }

    private void StartSimulation()
    {
        if (_simulationRunning)
        {
            return;
        }

        if (_world == null)
        {
            throw new InvalidOperationException("World must be initialized before starting the simulation.");
        }

        _simulationRunning = true;
        try
        {
            if (_needScheduler != null && _needScheduler.HasNeeds)
            {
                _needScheduler.Start();
            }

            for (var i = 0; i < _actorHosts.Count; i++)
            {
                var actor = _actorHosts[i];
                if (actor == null)
                {
                    throw new InvalidOperationException($"Actor host list contains a null reference during startup (index {i}).");
                }

                actor.Start();
            }
        }
        catch
        {
            StopSimulationCore();
            _simulationRunning = false;
            throw;
        }
    }

    private void StopSimulation()
    {
        if (!_simulationRunning)
        {
            return;
        }

        StopSimulationCore();
        _simulationRunning = false;
    }

    private void StopSimulationCore()
    {
        for (var i = 0; i < _actorHosts.Count; i++)
        {
            var actor = _actorHosts[i];
            if (actor == null)
            {
                throw new InvalidOperationException($"Actor host list contains a null reference during shutdown (index {i}).");
            }

            actor.RequestStop();
        }

        for (var i = 0; i < _actorHosts.Count; i++)
        {
            var actor = _actorHosts[i];
            if (actor == null)
            {
                throw new InvalidOperationException($"Actor host list contains a null reference during shutdown (index {i}).");
            }

            actor.FinishStop();
        }

        if (_needScheduler != null)
        {
            _needScheduler.Stop();
        }
    }

    private void Update()
    {
        if (_world == null)
        {
            return;
        }

        var time = _clock.Snapshot();
        var weather = _weatherSystem?.CurrentWeather ?? default;

        _calendarSystem?.Tick(time);
        _weatherSystem?.Tick(time);
        _shopSystem?.Tick(time);
        _cropSystem?.Tick(time);
        _animalSystem?.Tick(time);
        _fishingSystem?.Tick(time, weather);
        _foragingSystem?.Tick(time, weather);
        _miningSystem?.Tick(time, weather);
    }

    private void OnDestroy()
    {
        StopSimulation();

        _actorHosts.Clear();
        _actorDiagnostics.Clear();
        _actorHostById.Clear();

        _worldLogger?.Dispose();
        _worldLogger = null;

        if (_mapTexture != null)
        {
            Destroy(_mapTexture);
            _mapTexture = null;
        }

        _needAttributeNames = Array.Empty<string>();
        _readyEventArgs = null;
        _world = null;
        _clock = null;
        _needScheduler = null;
        _reservations = null;
        _planner = null;
        _executors = null;
        _inventorySystem = null;
        _itemCatalog = null;
        _craftingSystem = null;
        _shopSystem = null;
        _cropSystem = null;
        _animalSystem = null;
        _fishingSystem = null;
        _foragingSystem = null;
        _miningSystem = null;
        _weatherSystem = null;
        _calendarSystem = null;
        _scheduleService = null;
        _socialSystem = null;
        _skillSystem = null;
        _questSystem = null;
        _dialogueDatabase = null;
        _scheduleDatabase = null;
        _demoConfig = null;
        _tiles = null;
        _actionConfigs = null;
        _goalConfigs = null;

        Bootstrapped = null;
    }

    private WorldLogger _worldLogger;

    private void Bootstrap()
    {
        StopSimulation();

        _simulationRunning = false;
        _actorHosts.Clear();
        _actorHostById.Clear();
        _actorDefinitions.Clear();
        _locationToThing.Clear();
        _seedByThing.Clear();
        _actorDiagnostics.Clear();
        _needAttributeNames = Array.Empty<string>();

        if (_mapTexture != null)
        {
            Destroy(_mapTexture);
            _mapTexture = null;
        }

        string projectRoot = ResolveProjectRoot();
        string datasetRoot = Path.GetFullPath(Path.Combine(projectRoot, datasetRelativePath ?? string.Empty));
        if (!Directory.Exists(datasetRoot))
        {
            throw new DirectoryNotFoundException($"Dataset directory '{datasetRoot}' does not exist.");
        }

        string demoPath = RequireFile(datasetRoot, demoSettingsFile, nameof(demoSettingsFile));
        string actionsPath = RequireFile(datasetRoot, actionsFile, nameof(actionsFile));
        string goalsPath = RequireFile(datasetRoot, goalsFile, nameof(goalsFile));

        _demoConfig = ConfigLoader.LoadDemoConfig(demoPath);
        _actionConfigs = ConfigLoader.LoadActions(actionsPath);
        _goalConfigs = ConfigLoader.LoadGoals(goalsPath);
        var itemConfigs = ConfigLoader.LoadItems(RequireFile(datasetRoot, _demoConfig.items?.catalog, "items.catalog"));
        var recipeConfigs = ConfigLoader.LoadRecipes(RequireFile(datasetRoot, _demoConfig.items?.recipes, "items.recipes"));
        var cropConfigs = ConfigLoader.LoadCrops(RequireFile(datasetRoot, _demoConfig.farming?.crops, "farming.crops"));
        var animalConfigs = ConfigLoader.LoadAnimals(RequireFile(datasetRoot, _demoConfig.livestock?.animals, "livestock.animals"));
        _scheduleDatabase = ConfigLoader.LoadSchedules(RequireFile(datasetRoot, _demoConfig.schedules?.path, "schedules.path"));
        var eventDatabase = ConfigLoader.LoadCalendarEvents(RequireFile(datasetRoot, _demoConfig.events?.path, "events.path"));
        _dialogueDatabase = ConfigLoader.LoadDialogue(RequireFile(datasetRoot, _demoConfig.dialogue?.path, "dialogue.path"));
        var questDatabase = ConfigLoader.LoadQuests(RequireFile(datasetRoot, _demoConfig.quests?.path, "quests.path"));
        var villageConfig = ConfigLoader.LoadVillageConfig(RequireFile(datasetRoot, _demoConfig.world?.map?.data, "world.map.data"));
        string mapImagePath = RequireFile(datasetRoot, _demoConfig.world?.map?.image, "world.map.image");

        _clock = new WorldClock(_demoConfig.time);
        _reservations = new ReservationService();
        _itemCatalog = new ItemCatalog(itemConfigs, recipeConfigs);
        _inventorySystem = new InventorySystem(_itemCatalog);
        _craftingSystem = new CraftingSystem(_itemCatalog, _inventorySystem);
        _shopSystem = new ShopSystem(_inventorySystem, _itemCatalog);
        _skillSystem = new SkillProgressionSystem();
        _cropSystem = new CropSystem(cropConfigs, _demoConfig.world.rngSeed, _skillSystem);
        _animalSystem = new AnimalSystem(animalConfigs);
        _fishingSystem = new FishingSystem(_demoConfig.fishing, _demoConfig.world.rngSeed, _skillSystem);
        _foragingSystem = new ForagingSystem(_demoConfig.foraging, _demoConfig.world.rngSeed, _skillSystem);
        _miningSystem = new MiningSystem(_demoConfig.mining, _demoConfig.world.rngSeed, _skillSystem);
        _questSystem = new QuestSystem(questDatabase?.quests ?? Array.Empty<QuestConfig>());
        _scheduleService = new RoleScheduleService();

        var mapData = LoadTileClassification(mapImagePath, _demoConfig.world.map, villageConfig);
        _tiles = mapData.Classification;
        _mapTexture = mapData.Texture;

        var seeds = BuildThingSeeds(_demoConfig.world, villageConfig);
        var facts = BuildWorldFacts(_demoConfig.world);

        _world = new ShardedWorld(
            _demoConfig.world.width,
            _demoConfig.world.height,
            _demoConfig.world.blockedChance,
            _demoConfig.world.shards,
            _demoConfig.world.rngSeed,
            seeds.Select(s => (s.Id, s.Type, (IEnumerable<string>)s.Tags, s.Position, (IDictionary<string, double>)s.Attributes, s.Building)),
            facts,
            _clock,
            _tiles.Walkable);

        _skillSystem.AttachWorld(_world);
        _cropSystem.SetSkillProgression(_skillSystem);
        InitializeMiningNodes();
        InitializeFishingSpots();
        InitializeForagingSpots();

        _socialSystem = new SocialRelationshipSystem(_world, _clock, _demoConfig.social);
        _needScheduler = new NeedScheduler(_world, _clock, _demoConfig.needs);
        _needAttributeNames = (_demoConfig?.needs?.needs ?? Array.Empty<NeedConfig>())
            .Where(n => n != null && !string.IsNullOrWhiteSpace(n.attribute))
            .Select(n => n.attribute.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _weatherSystem = new WeatherSystem(_world, _cropSystem, _animalSystem, _scheduleService, _demoConfig.weather, _demoConfig.world.rngSeed);
        _calendarSystem = new CalendarEventSystem(_world, _weatherSystem, eventDatabase?.events ?? Array.Empty<CalendarEventConfig>());
        _scheduleService.EventQuery = _calendarSystem;
        _scheduleService.WeatherQuery = _weatherSystem;

        ConfigureInventoriesAndCurrency();
        ConfigureScheduleDefinitions();

        string logRoot = Path.Combine(Application.persistentDataPath, "goap-logs");
        Directory.CreateDirectory(logRoot);
        _worldLogger = new WorldLogger(Path.Combine(logRoot, "world.log.txt"));

        _executors = new ExecutorRegistry();
        _planner = new JsonDrivenPlanner(
            _actionConfigs,
            _goalConfigs,
            _reservations,
            _scheduleService,
            _inventorySystem,
            _cropSystem,
            _animalSystem,
            _fishingSystem,
            _miningSystem,
            _foragingSystem,
            _questSystem,
            _weatherSystem,
            _craftingSystem,
            _skillSystem);

        BuildActorHosts(logRoot);
        NotifyBootstrapped(datasetRoot);
        StartSimulation();
    }

    private void NotifyBootstrapped(string datasetRoot)
    {
        if (string.IsNullOrWhiteSpace(datasetRoot))
        {
            throw new ArgumentException("Dataset root must be a valid, non-empty path.", nameof(datasetRoot));
        }

        if (_world == null)
        {
            throw new InvalidOperationException("World must be initialized before publishing bootstrap completion.");
        }

        if (_mapTexture == null)
        {
            throw new InvalidOperationException("Map texture must be initialized before publishing bootstrap completion.");
        }

        var actors = _actorDefinitions.ToArray();
        if (_clock == null)
        {
            throw new InvalidOperationException("World clock must be initialized before publishing bootstrap completion.");
        }

        _readyEventArgs = new SimulationReadyEventArgs(
            _world,
            Array.AsReadOnly(actors),
            datasetRoot,
            _mapTexture,
            _clock,
            new Dictionary<ThingId, ActorHostDiagnostics>(_actorDiagnostics),
            _demoConfig?.observer?.cameraPawn);
        Bootstrapped?.Invoke(this, _readyEventArgs);
    }

    private void ConfigureScheduleDefinitions()
    {
        var roleSchedules = new Dictionary<string, RoleScheduleDefinition>(StringComparer.OrdinalIgnoreCase);
        if (_scheduleDatabase?.roles != null)
        {
            foreach (var role in _scheduleDatabase.roles)
            {
                if (role == null || string.IsNullOrWhiteSpace(role.id))
                {
                    continue;
                }

                var blocks = new List<RoleScheduleBlock>();
                foreach (var block in role.blocks ?? Array.Empty<RoleBlockConfig>())
                {
                    var built = BuildRoleBlock(role.id, block);
                    if (built != null)
                    {
                        blocks.Add(built);
                    }
                }

                if (blocks.Count > 0)
                {
                    roleSchedules[role.id.Trim()] = new RoleScheduleDefinition(role.id.Trim(), blocks);
                }
            }
        }

        var pawnOverrides = new Dictionary<string, RoleScheduleDefinition>(StringComparer.OrdinalIgnoreCase);
        if (_scheduleDatabase?.pawns != null)
        {
            foreach (var pawn in _scheduleDatabase.pawns)
            {
                if (pawn == null || string.IsNullOrWhiteSpace(pawn.pawn))
                {
                    continue;
                }

                var blocks = new List<RoleScheduleBlock>();
                foreach (var block in pawn.blocks ?? Array.Empty<RoleBlockConfig>())
                {
                    var built = BuildRoleBlock(pawn.role ?? pawn.pawn, block);
                    if (built != null)
                    {
                        blocks.Add(built);
                    }
                }

                if (blocks.Count > 0)
                {
                    pawnOverrides[pawn.pawn.Trim()] = new RoleScheduleDefinition(pawn.role ?? pawn.pawn, blocks);
                }
            }
        }

        foreach (var entry in _actorDefinitions)
        {
            RoleScheduleDefinition schedule = null;
            if (pawnOverrides.TryGetValue(entry.Id.Value, out var overrideSchedule))
            {
                schedule = overrideSchedule;
            }
            else if (!string.IsNullOrWhiteSpace(entry.Pawn.role) && roleSchedules.TryGetValue(entry.Pawn.role.Trim(), out var roleSchedule))
            {
                schedule = roleSchedule;
            }

            if (schedule != null)
            {
                _scheduleService.Register(entry.Id, schedule);
            }

            var assignments = BuildAssignments(entry.Id, entry.Pawn);
            if (assignments.Count > 0)
            {
                _scheduleService.RegisterAssignments(entry.Id, assignments);
            }
        }
    }

    private Dictionary<string, ThingId> BuildAssignments(ThingId actorId, VillagePawn pawn)
    {
        var assignments = new Dictionary<string, ThingId>(StringComparer.OrdinalIgnoreCase);
        if (pawn == null)
        {
            return assignments;
        }

        if (pawn.home != null && !string.IsNullOrWhiteSpace(pawn.home.location))
        {
            if (_locationToThing.TryGetValue(pawn.home.location.Trim(), out var homeThing) && !homeThing.Equals(default(ThingId)))
            {
                assignments["type:home"] = homeThing;
                assignments["home"] = homeThing;
                AssignTagMappings(assignments, homeThing);
            }
        }

        if (pawn.workplace != null && !string.IsNullOrWhiteSpace(pawn.workplace.location))
        {
            if (_locationToThing.TryGetValue(pawn.workplace.location.Trim(), out var workThing) && !workThing.Equals(default(ThingId)))
            {
                assignments["type:workplace"] = workThing;
                assignments["workplace"] = workThing;
                if (!string.IsNullOrWhiteSpace(pawn.role))
                {
                    assignments[$"type:{pawn.role.Trim().ToLowerInvariant()}"] = workThing;
                }
                AssignTagMappings(assignments, workThing);
            }
        }

        return assignments;
    }

    private void AssignTagMappings(IDictionary<string, ThingId> assignments, ThingId thing)
    {
        if (!_seedByThing.TryGetValue(thing, out var seed))
        {
            return;
        }

        foreach (var tag in seed.Tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            assignments[tag.Trim()] = thing;
        }
    }

    private RoleScheduleBlock BuildRoleBlock(string roleId, RoleBlockConfig block)
    {
        if (block == null)
        {
            return null;
        }

        double start = ParseHour(block.start, "schedule start time");
        double end = ParseHour(block.end, "schedule end time");
        var days = (block.days ?? Array.Empty<string>()).Select(ParseDayOfWeek).Where(v => v.HasValue).Select(v => v.Value);
        return new RoleScheduleBlock(
            roleId ?? string.Empty,
            block.@goto ?? string.Empty,
            block.task ?? string.Empty,
            start,
            end,
            days,
            block.seasons ?? Array.Empty<string>(),
            label: null);
    }

    private void ConfigureInventoriesAndCurrency()
    {
        var actorConfig = _demoConfig.actors;
        if (actorConfig == null)
        {
            return;
        }

        foreach (var actor in _actorDefinitions)
        {
            if (actorConfig.inventory != null)
            {
                _inventorySystem.ConfigureInventory(actor.Id, actorConfig.inventory);
            }

            if (actorConfig.currency.HasValue)
            {
                _inventorySystem.SetCurrency(actor.Id, actorConfig.currency.Value);
            }
        }
    }

    private void BuildActorHosts(string logRoot)
    {
        foreach (var entry in _actorDefinitions)
        {
            var host = new ActorHost(
                _world,
                _planner,
                _executors,
                _reservations,
                entry.Id,
                _demoConfig.simulation.actorHostSeed,
                logRoot,
                _demoConfig.simulation.priorityJitter,
                _scheduleService,
                _inventorySystem,
                _shopSystem,
                _socialSystem,
                _cropSystem,
                _animalSystem,
                _miningSystem,
                _fishingSystem,
                _foragingSystem,
                _skillSystem,
                _questSystem,
                _worldLogger);
            _actorHosts.Add(host);
            var diagnostics = host.Diagnostics ?? throw new InvalidOperationException($"Actor host '{entry.Id.Value}' did not expose diagnostics.");
            if (_actorDiagnostics.ContainsKey(entry.Id))
            {
                throw new InvalidOperationException($"Duplicate diagnostics registration detected for actor '{entry.Id.Value}'.");
            }

            _actorDiagnostics[entry.Id] = diagnostics;
            if (_actorHostById.ContainsKey(entry.Id))
            {
                throw new InvalidOperationException($"Duplicate actor host registered for '{entry.Id.Value}'.");
            }
            _actorHostById[entry.Id] = host;
        }
    }

    private List<ThingSeed> BuildThingSeeds(WorldConfig worldConfig, VillageConfig villageConfig)
    {
        var seeds = new List<ThingSeed>();
        if (worldConfig == null)
        {
            throw new InvalidDataException("Demo configuration missing world definition.");
        }

        AddBuildingPrototypes(worldConfig, villageConfig, seeds);
        AddConfiguredThings(worldConfig, seeds);
        AddActorSeeds(villageConfig, seeds);
        return seeds;
    }

    private void AddActorSeeds(VillageConfig villageConfig, List<ThingSeed> seeds)
    {
        if (villageConfig?.pawns?.pawns == null)
        {
            return;
        }

        var actorConfig = _demoConfig.actors ?? throw new InvalidDataException("actors section missing in demo config.");
        var rng = new System.Random(_demoConfig.simulation.actorHostSeed);

        foreach (var pawn in villageConfig.pawns.pawns)
        {
            if (pawn == null || string.IsNullOrWhiteSpace(pawn.id))
            {
                continue;
            }

            var id = new ThingId(pawn.id.Trim());
            var seed = new ThingSeed
            {
                Id = id,
                Type = actorConfig.type ?? "pawn",
                Position = ResolvePawnPosition(pawn)
            };

            foreach (var tag in actorConfig.tags ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    seed.Tags.Add(tag.Trim());
                }
            }

            if (!string.IsNullOrWhiteSpace(pawn.role))
            {
                seed.Tags.Add(pawn.role.Trim());
                seed.Tags.Add($"role:{pawn.role.Trim().ToLowerInvariant()}");
            }

            foreach (var kv in actorConfig.attributes ?? new Dictionary<string, AttributeInitConfig>())
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null)
                {
                    continue;
                }

                double value = ResolveAttributeInitialValue(kv.Value, rng);
                seed.Attributes[kv.Key.Trim()] = value;
            }

            seeds.Add(seed);
            _seedByThing[id] = seed;
            _actorDefinitions.Add((id, pawn));
        }
    }

    private double ResolveAttributeInitialValue(AttributeInitConfig config, System.Random rng)
    {
        if (config.value.HasValue)
        {
            return config.value.Value;
        }

        double min = config.min ?? 0.0;
        double max = config.max ?? 1.0;
        if (max <= min)
        {
            return min;
        }

        double sample = rng.NextDouble();
        return min + ((max - min) * sample);
    }

    private GridPos ResolvePawnPosition(VillagePawn pawn)
    {
        if (pawn?.home?.location != null && _locationToThing.TryGetValue(pawn.home.location.Trim(), out var homeThing) && _seedByThing.TryGetValue(homeThing, out var homeSeed))
        {
            return homeSeed.Position;
        }

        if (pawn?.workplace?.location != null && _locationToThing.TryGetValue(pawn.workplace.location.Trim(), out var workThing) && _seedByThing.TryGetValue(workThing, out var workSeed))
        {
            return workSeed.Position;
        }

        return new GridPos(_demoConfig.world.width / 2, _demoConfig.world.height / 2);
    }

    private void AddConfiguredThings(WorldConfig worldConfig, List<ThingSeed> seeds)
    {
        foreach (var thing in worldConfig.things ?? Array.Empty<ThingSpawnConfig>())
        {
            if (thing == null || string.IsNullOrWhiteSpace(thing.id))
            {
                continue;
            }

            var id = new ThingId(thing.id.Trim());
            var seed = new ThingSeed
            {
                Id = id,
                Type = thing.type ?? string.Empty,
                Position = new GridPos(ClampCoordinate(thing.x, _demoConfig.world.width), ClampCoordinate(thing.y, _demoConfig.world.height)),
                Building = BuildBuildingInfo(thing.building, thing.building?.service_points, thing.building?.area)
            };

            foreach (var tag in thing.tags ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    seed.Tags.Add(tag.Trim());
                }
            }

            foreach (var kv in thing.attributes ?? new Dictionary<string, double>())
            {
                seed.Attributes[kv.Key.Trim()] = kv.Value;
            }

            if (thing.container?.inventory != null)
            {
                _inventorySystem.ConfigureInventory(id, thing.container.inventory);
            }

            if (thing.currency.HasValue)
            {
                _inventorySystem.SetCurrency(id, thing.currency.Value);
            }

            if (thing.building?.shop != null)
            {
                _shopSystem.RegisterShop(id, thing.building.shop);
            }

            seeds.Add(seed);
            _seedByThing[id] = seed;
        }
    }

    private void AddBuildingPrototypes(WorldConfig worldConfig, VillageConfig villageConfig, List<ThingSeed> seeds)
    {
        if (worldConfig?.map?.buildingPrototypes == null)
        {
            return;
        }

        foreach (var kv in worldConfig.map.buildingPrototypes)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null)
            {
                continue;
            }

            var prototype = kv.Value;
            var matches = ResolveBuildingAnnotations(villageConfig, kv.Key);
            foreach (var annotation in matches)
            {
                var locationId = annotation.location ?? kv.Key;
                var id = new ThingId(BuildPrototypeThingId(prototype, locationId));
                var position = ResolveLocationCenter(villageConfig, annotation.location) ?? new GridPos(_demoConfig.world.width / 2, _demoConfig.world.height / 2);
                var location = ResolveLocation(villageConfig, locationId);
                var boundingBox = ResolveBoundingBox(annotation, location);

                var seed = new ThingSeed
                {
                    Id = id,
                    Type = prototype.type ?? kv.Key,
                    Position = position,
                    Building = BuildBuildingInfo(prototype.building, ConvertServicePoints(prototype.servicePoints, boundingBox), null)
                };

                foreach (var tag in prototype.tags ?? Array.Empty<string>())
                {
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        seed.Tags.Add(tag.Trim());
                    }
                }

                if (!string.IsNullOrWhiteSpace(prototype.type))
                {
                    seed.Tags.Add($"type:{prototype.type.Trim().ToLowerInvariant()}");
                }

                seed.Tags.Add($"location:{locationId.Trim().ToLowerInvariant()}");

                foreach (var kvp in prototype.attributes ?? new Dictionary<string, double>())
                {
                    seed.Attributes[kvp.Key.Trim()] = kvp.Value;
                }

                seeds.Add(seed);
                _seedByThing[id] = seed;
                _locationToThing[locationId.Trim()] = id;

                if (prototype.building?.shop != null)
                {
                    _shopSystem.RegisterShop(id, prototype.building.shop);
                }
            }
        }
    }

    private IEnumerable<VillageBuildingAnnotation> ResolveBuildingAnnotations(VillageConfig villageConfig, string name)
    {
        return villageConfig?.map?.annotations?.buildings?
            .Where(b => string.Equals(b?.name, name, StringComparison.OrdinalIgnoreCase))
            ?? Array.Empty<VillageBuildingAnnotation>();
    }

    private string BuildPrototypeThingId(MapBuildingPrototypeConfig prototype, string locationId)
    {
        string prefix = string.IsNullOrWhiteSpace(prototype?.idPrefix) ? (prototype?.type ?? "building") : prototype.idPrefix;
        return $"{prefix}_{locationId}".Replace(' ', '_').ToLowerInvariant();
    }

    private GridPos? ResolveLocationCenter(VillageConfig villageConfig, string locationId)
    {
        if (string.IsNullOrWhiteSpace(locationId))
        {
            return null;
        }

        if (!villageConfig.locations.TryGetValue(locationId.Trim(), out var location) || location == null)
        {
            return null;
        }

        double[] center = location.center ?? Array.Empty<double>();
        if (center.Length >= 2)
        {
            return new GridPos(
                ClampCoordinate((int)Math.Round(center[0], MidpointRounding.AwayFromZero), _demoConfig.world.width),
                ClampCoordinate((int)Math.Round(center[1], MidpointRounding.AwayFromZero), _demoConfig.world.height));
        }

        double[] bbox = location.bbox ?? Array.Empty<double>();
        if (bbox.Length >= 4)
        {
            double x = (bbox[0] + bbox[2]) * 0.5;
            double y = (bbox[1] + bbox[3]) * 0.5;
            return new GridPos(
                ClampCoordinate((int)Math.Round(x, MidpointRounding.AwayFromZero), _demoConfig.world.width),
                ClampCoordinate((int)Math.Round(y, MidpointRounding.AwayFromZero), _demoConfig.world.height));
        }

        return null;
    }

    private VillageLocation ResolveLocation(VillageConfig villageConfig, string locationId)
    {
        if (string.IsNullOrWhiteSpace(locationId))
        {
            return null;
        }

        if (villageConfig?.locations == null)
        {
            return null;
        }

        villageConfig.locations.TryGetValue(locationId.Trim(), out var location);
        return location;
    }

    private double[] ResolveBoundingBox(VillageBuildingAnnotation annotation, VillageLocation location)
    {
        if (annotation?.bbox != null && annotation.bbox.Length >= 4)
        {
            return annotation.bbox;
        }

        if (location?.bbox != null && location.bbox.Length >= 4)
        {
            return location.bbox;
        }

        return null;
    }

    private int ClampCoordinate(int? coordinate, int max)
    {
        int value = coordinate ?? 0;
        if (value < 0)
        {
            value = 0;
        }
        else if (value >= max)
        {
            value = max - 1;
        }

        return value;
    }

    private ServicePointConfig[] ConvertServicePoints(MapServicePointConfig[] mapServicePoints, double[] boundingBox)
    {
        if (mapServicePoints == null)
        {
            return null;
        }

        var converted = new ServicePointConfig[mapServicePoints.Length];
        for (int i = 0; i < mapServicePoints.Length; i++)
        {
            var point = mapServicePoints[i];
            if (point == null)
            {
                converted[i] = null;
                continue;
            }

            int? x = null;
            int? y = null;

            if (point.x.HasValue)
            {
                var resolvedX = TranslateServicePointCoordinate(point.x.Value, boundingBox, axis: 0);
                x = ConvertCoordinate(resolvedX, "service point x");
            }

            if (point.y.HasValue)
            {
                var resolvedY = TranslateServicePointCoordinate(point.y.Value, boundingBox, axis: 1);
                y = ConvertCoordinate(resolvedY, "service point y");
            }

            converted[i] = new ServicePointConfig
            {
                x = x,
                y = y
            };
        }

        return converted;
    }

    private double TranslateServicePointCoordinate(double value, double[] boundingBox, int axis)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new InvalidDataException($"Invalid service point coordinate: {value}");
        }

        if (boundingBox == null || boundingBox.Length < 4)
        {
            return value;
        }

        double min;
        double max;
        if (axis == 0)
        {
            min = boundingBox[0];
            max = boundingBox[2];
        }
        else
        {
            min = boundingBox[1];
            max = boundingBox[3];
        }

        if (double.IsNaN(min) || double.IsNaN(max) || double.IsInfinity(min) || double.IsInfinity(max))
        {
            throw new InvalidDataException("Bounding box contains invalid values for service point conversion.");
        }

        double span = max - min;
        if (span < 0.0)
        {
            throw new InvalidDataException($"Invalid bounding box span for service point conversion. min:{min} max:{max}");
        }

        double offset = Math.Round(value * span, MidpointRounding.AwayFromZero);
        return min + offset;
    }

    private int ConvertCoordinate(double value, string label)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new InvalidDataException($"Invalid {label} coordinate: {value}");
        }

        var rounded = Math.Round(value);
        if (Math.Abs(value - rounded) > 1e-6)
        {
            throw new InvalidDataException($"{label} coordinate must be an integer value but was {value}");
        }

        return (int)rounded;
    }

    private BuildingInfo BuildBuildingInfo(BuildingConfig config, ServicePointConfig[] servicePoints, BuildingAreaConfig areaConfig)
    {
        if (config == null && areaConfig == null && (servicePoints == null || servicePoints.Length == 0))
        {
            return null;
        }

        RectInt? area = null;
        if (config?.area != null)
        {
            area = new RectInt(
                Math.Max(0, config.area.x ?? 0),
                Math.Max(0, config.area.y ?? 0),
                Math.Max(0, (config.area.x ?? 0) + Math.Max(0, (config.area.width ?? 0) - 1)),
                Math.Max(0, (config.area.y ?? 0) + Math.Max(0, (config.area.height ?? 0) - 1)));
        }
        else if (areaConfig != null)
        {
            area = new RectInt(
                Math.Max(0, areaConfig.x ?? 0),
                Math.Max(0, areaConfig.y ?? 0),
                Math.Max(0, (areaConfig.x ?? 0) + Math.Max(0, (areaConfig.width ?? 0) - 1)),
                Math.Max(0, (areaConfig.y ?? 0) + Math.Max(0, (areaConfig.height ?? 0) - 1)));
        }

        var points = new List<GridPos>();
        foreach (var point in servicePoints ?? Array.Empty<ServicePointConfig>())
        {
            if (point == null)
            {
                continue;
            }

            int px = ClampCoordinate(point.x, _demoConfig.world.width);
            int py = ClampCoordinate(point.y, _demoConfig.world.height);
            points.Add(new GridPos(px, py));
        }

        var openHours = new List<BuildingOpenHours>();
        foreach (var hours in config?.openHours ?? Array.Empty<BuildingOpenHoursConfig>())
        {
            if (hours == null)
            {
                continue;
            }

            var days = (hours.days ?? Array.Empty<string>()).Select(ParseDayOfWeek).Where(v => v.HasValue).Select(v => v.Value);
            var seasons = hours.seasons ?? Array.Empty<string>();
            double start = ParseHour(hours.open, "building open hour");
            double end = ParseHour(hours.close, "building close hour");
            openHours.Add(new BuildingOpenHours(days, seasons, start, end));
        }

        bool open = config?.open ?? true;
        int capacity = config?.capacity ?? 0;
        return new BuildingInfo(area, open, capacity, points, openHours);
    }

    private double ParseHour(string text, string context)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0.0;
        }

        if (TimeSpan.TryParseExact(text.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var span))
        {
            return Math.Clamp(span.TotalHours, 0.0, 24.0);
        }

        throw new InvalidDataException($"Failed to parse {context} value '{text}'. Expected HH:MM format.");
    }

    private int? ParseDayOfWeek(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Trim().ToLowerInvariant() switch
        {
            "sun" => 0,
            "mon" => 1,
            "tue" => 2,
            "wed" => 3,
            "thu" => 4,
            "fri" => 5,
            "sat" => 6,
            _ => null
        };
    }

    private List<Fact> BuildWorldFacts(WorldConfig worldConfig)
    {
        var facts = new List<Fact>();
        foreach (var fact in worldConfig.facts ?? Array.Empty<FactSeedConfig>())
        {
            if (fact == null || string.IsNullOrWhiteSpace(fact.pred))
            {
                continue;
            }

            facts.Add(new Fact(fact.pred.Trim(), new ThingId(fact.a ?? string.Empty), new ThingId(fact.b ?? string.Empty)));
        }

        return facts;
    }

    private void InitializeFishingSpots()
    {
        if (_fishingSystem == null || !_demoConfig.fishing.enabled || _tiles == null)
        {
            return;
        }

        int width = _tiles.Water.GetLength(0);
        int height = _tiles.Water.GetLength(1);
        int waterTiles = 0;
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                if (_tiles.Water[x, y])
                {
                    waterTiles++;
                }
            }
        }

        if (waterTiles == 0)
        {
            return;
        }

        double desired = (waterTiles / 100.0) * Math.Max(0.0, _fishingSystem.SpotDensityPer100Tiles);
        int target = Math.Max(1, (int)Math.Ceiling(desired));
        int interval = Math.Max(1, (int)Math.Sqrt((width * height) / (double)target));
        int counter = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!_tiles.Water[x, y])
                {
                    continue;
                }

                if ((counter++ % interval) != 0)
                {
                    continue;
                }

                var id = new ThingId($"fishing_spot_{x}_{y}");
                _fishingSystem.RegisterSpot(id, new GridPos(x, y), _tiles.Shallow[x, y]);
            }
        }
    }

    private void InitializeForagingSpots()
    {
        if (_foragingSystem == null || !_demoConfig.foraging.enabled || _tiles == null)
        {
            return;
        }

        int width = _tiles.Forest.GetLength(0);
        int height = _tiles.Forest.GetLength(1);
        int counter = 0;
        int interval = Math.Max(1, (int)Math.Sqrt(width * height / 64.0));

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool forest = _tiles.Forest[x, y];
                bool coast = _tiles.Coastal[x, y];
                if (!forest && !coast)
                {
                    continue;
                }

                if ((counter++ % interval) != 0)
                {
                    continue;
                }

                var id = new ThingId($"forage_spot_{x}_{y}");
                _foragingSystem.RegisterSpot(id, new GridPos(x, y), forest, coast);
            }
        }
    }

    private void InitializeMiningNodes()
    {
        if (_miningSystem == null || !_demoConfig.mining.enabled)
        {
            return;
        }

        foreach (var node in _demoConfig.mining.nodes ?? Array.Empty<MiningNodeConfig>())
        {
            if (node == null || string.IsNullOrWhiteSpace(node.id))
            {
                continue;
            }

            var id = new ThingId(node.id.Trim());
            var position = new GridPos(ClampCoordinate(node.x, _demoConfig.world.width), ClampCoordinate(node.y, _demoConfig.world.height));
            var layerId = node.layer ?? string.Empty;
            var biomes = node.biomes ?? Array.Empty<string>();
            _miningSystem.RegisterNode(id, position, layerId, biomes);
        }
    }

    private (TileClassification Classification, Texture2D Texture) LoadTileClassification(string imagePath, WorldMapConfig mapConfig, VillageConfig village)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            throw new InvalidDataException("Map image path is required to build world walkability.");
        }

        byte[] data = File.ReadAllBytes(imagePath);
        Texture2D texture = null;
        try
        {
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (!texture.LoadImage(data))
            {
                throw new InvalidDataException($"Failed to load map image '{imagePath}'.");
            }

            var pixels = texture.GetPixels32();
            int width = texture.width;
            int height = texture.height;

            var colorMap = new Dictionary<Color32, string>(new Color32Comparer());
            foreach (var kv in village?.map?.key ?? new Dictionary<string, string>())
            {
                if (ColorUtility.TryParseHtmlString(kv.Value, out var color))
                {
                    colorMap[new Color32((byte)(color.r * 255f), (byte)(color.g * 255f), (byte)(color.b * 255f), 255)] = kv.Key;
                }
            }

            var classification = new TileClassification
            {
                Walkable = new bool[width, height],
                Water = new bool[width, height],
                Shallow = new bool[width, height],
                Forest = new bool[width, height],
                Farmland = new bool[width, height],
                Coastal = new bool[width, height]
            };

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var pixel = pixels[(height - 1 - y) * width + x];
                    if (!colorMap.TryGetValue(pixel, out var tileId))
                    {
                        throw new InvalidDataException($"Map tile color {pixel} at {x},{y} does not match any key entry.");
                    }

                    if (!mapConfig.tiles.TryGetValue(tileId, out var tile))
                    {
                        throw new InvalidDataException($"Tile '{tileId}' referenced by map image not present in tiles configuration.");
                    }

                    classification.Walkable[x, y] = tile.walkable;
                    classification.Water[x, y] = tile.water;
                    classification.Shallow[x, y] = tile.shallowWater;
                    classification.Forest[x, y] = tile.forest;
                    classification.Farmland[x, y] = tile.farmland;
                    classification.Coastal[x, y] = tile.coastal;
                }
            }

            return (classification, texture);
        }
        catch
        {
            if (texture != null)
            {
                Destroy(texture);
            }

            throw;
        }
    }

    private string ResolveProjectRoot()
    {
        if (!string.IsNullOrEmpty(Application.dataPath))
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        return Directory.GetCurrentDirectory();
    }

    private string RequireFile(string root, string relativePath, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new FileNotFoundException($"Required dataset file for '{propertyName}' was not specified.");
        }

        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Dataset file '{fullPath}' for '{propertyName}' does not exist.");
        }

        return fullPath;
    }
}
