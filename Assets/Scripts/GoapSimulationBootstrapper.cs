using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DataDrivenGoap.Config;
using DataDrivenGoap.Concurrency;
using DataDrivenGoap.Core;
using DataDrivenGoap.Execution;
using DataDrivenGoap.Effects;
using DataDrivenGoap.Items;
using DataDrivenGoap.Planning;
using DataDrivenGoap.Simulation;
using DataDrivenGoap.Social;
using DataDrivenGoap.World;
using UnityEngine;
using RectInt = DataDrivenGoap.Core.RectInt;

public readonly struct ThingPlanParticipation : IEquatable<ThingPlanParticipation>
{
    public ThingPlanParticipation(string goalId, string actionId, string activity, bool moveToTarget)
    {
        if (string.IsNullOrWhiteSpace(goalId))
        {
            throw new ArgumentException("Goal id must be provided for plan participation entries.", nameof(goalId));
        }

        if (string.IsNullOrWhiteSpace(actionId))
        {
            throw new ArgumentException("Action id must be provided for plan participation entries.", nameof(actionId));
        }

        GoalId = goalId.Trim();
        ActionId = actionId.Trim();
        Activity = activity?.Trim() ?? string.Empty;
        MoveToTarget = moveToTarget;
    }

    public string GoalId { get; }
    public string ActionId { get; }
    public string Activity { get; }
    public bool MoveToTarget { get; }

    public bool Equals(ThingPlanParticipation other)
    {
        return MoveToTarget == other.MoveToTarget &&
               string.Equals(GoalId, other.GoalId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(ActionId, other.ActionId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Activity, other.Activity, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object obj)
    {
        return obj is ThingPlanParticipation other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(GoalId ?? string.Empty);
            hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(ActionId ?? string.Empty);
            hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Activity ?? string.Empty);
            hash = (hash * 397) ^ MoveToTarget.GetHashCode();
            return hash;
        }
    }

    public override string ToString()
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "Goal: {0} — Action: {1} (Activity {2}, MoveToTarget: {3})",
            GoalId,
            ActionId,
            string.IsNullOrEmpty(Activity) ? "<none>" : Activity,
            MoveToTarget);
    }
}

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
        public sealed class TileClassificationSnapshot
        {
            public TileClassificationSnapshot(
                bool[,] walkable,
                bool[,] water,
                bool[,] shallow,
                bool[,] forest,
                bool[,] farmland,
                bool[,] coastal)
            {
                Walkable = walkable ?? throw new ArgumentNullException(nameof(walkable));
                Water = water ?? throw new ArgumentNullException(nameof(water));
                Shallow = shallow ?? throw new ArgumentNullException(nameof(shallow));
                Forest = forest ?? throw new ArgumentNullException(nameof(forest));
                Farmland = farmland ?? throw new ArgumentNullException(nameof(farmland));
                Coastal = coastal ?? throw new ArgumentNullException(nameof(coastal));

                ValidateLayerDimensions(Water, nameof(Water));
                ValidateLayerDimensions(Shallow, nameof(Shallow));
                ValidateLayerDimensions(Forest, nameof(Forest));
                ValidateLayerDimensions(Farmland, nameof(Farmland));
                ValidateLayerDimensions(Coastal, nameof(Coastal));
            }

            public int Width => Walkable.GetLength(0);
            public int Height => Walkable.GetLength(1);

            public bool[,] Walkable { get; }
            public bool[,] Water { get; }
            public bool[,] Shallow { get; }
            public bool[,] Forest { get; }
            public bool[,] Farmland { get; }
            public bool[,] Coastal { get; }

            private void ValidateLayerDimensions(bool[,] layer, string layerName)
            {
                if (layer.GetLength(0) != Width || layer.GetLength(1) != Height)
                {
                    throw new ArgumentException(
                        $"Tile classification layer '{layerName}' dimensions {layer.GetLength(0)}x{layer.GetLength(1)} do not match walkable dimensions {Width}x{Height}.",
                        layerName);
                }
            }
        }

        public SimulationReadyEventArgs(
            ShardedWorld world,
            IReadOnlyList<(ThingId Id, VillagePawn Pawn)> actors,
            string datasetRoot,
            Texture2D mapTexture,
            WorldClock clock,
            IReadOnlyDictionary<ThingId, ActorHostDiagnostics> actorDiagnostics,
            string cameraPawnId,
            IReadOnlyList<ThingId> manualPawnIds,
            ThingId? playerPawnId,
            TileClassificationSnapshot tileClassification,
            bool showOnlySelectedPawn)
        {
            World = world ?? throw new ArgumentNullException(nameof(world));
            ActorDefinitions = actors ?? throw new ArgumentNullException(nameof(actors));
            DatasetRoot = datasetRoot ?? throw new ArgumentNullException(nameof(datasetRoot));
            MapTexture = mapTexture ?? throw new ArgumentNullException(nameof(mapTexture));
            Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            ActorDiagnostics = actorDiagnostics ?? throw new ArgumentNullException(nameof(actorDiagnostics));
            CameraPawnId = string.IsNullOrWhiteSpace(cameraPawnId) ? null : cameraPawnId.Trim();
            ManualPawnIds = manualPawnIds ?? Array.AsReadOnly(Array.Empty<ThingId>());
            PlayerPawnId = playerPawnId;
            TileClassification = tileClassification ?? throw new ArgumentNullException(nameof(tileClassification));
            ShowOnlySelectedPawn = showOnlySelectedPawn;
        }

        public ShardedWorld World { get; }
        public IReadOnlyList<(ThingId Id, VillagePawn Pawn)> ActorDefinitions { get; }
        public string DatasetRoot { get; }
        public Texture2D MapTexture { get; }
        public WorldClock Clock { get; }
        public IReadOnlyDictionary<ThingId, ActorHostDiagnostics> ActorDiagnostics { get; }
        public string CameraPawnId { get; }
        public IReadOnlyList<ThingId> ManualPawnIds { get; }
        public ThingId? PlayerPawnId { get; }
        public TileClassificationSnapshot TileClassification { get; }
        public bool ShowOnlySelectedPawn { get; }
    }

    public event EventHandler<SimulationReadyEventArgs> Bootstrapped;

    private sealed class ManualActorState
    {
        public ManualActorState(System.Random rng)
        {
            Rng = rng ?? throw new ArgumentNullException(nameof(rng));
        }

        public System.Random Rng { get; }
        public Dictionary<string, DateTime> PlanCooldownUntil { get; } =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
    }

    private readonly List<ActorHost> _actorHosts = new List<ActorHost>();
    private readonly Dictionary<ThingId, ActorHost> _actorHostById = new Dictionary<ThingId, ActorHost>();
    private readonly List<(ThingId Id, VillagePawn Pawn)> _actorDefinitions = new List<(ThingId, VillagePawn)>();
    private readonly Dictionary<ThingId, ActorHostDiagnostics> _actorDiagnostics = new Dictionary<ThingId, ActorHostDiagnostics>();
    private readonly Dictionary<string, ThingId> _locationToThing = new Dictionary<string, ThingId>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ThingId, ThingSeed> _seedByThing = new Dictionary<ThingId, ThingSeed>();
    private readonly HashSet<ThingId> _manualPawnIds = new HashSet<ThingId>();
    private readonly Dictionary<string, ManualActorState> _manualActorStates =
        new Dictionary<string, ManualActorState>(StringComparer.OrdinalIgnoreCase);

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
    private Dictionary<string, ThingPlanParticipation[]> _thingPlanParticipationByTag =
        new Dictionary<string, ThingPlanParticipation[]>(StringComparer.Ordinal);
    private string[] _needAttributeNames = Array.Empty<string>();
    private ThingId? _playerPawnId;
    private VillageConfig _villageConfig;

    private bool _simulationRunning;

    private void Awake()
    {
        Bootstrap();
    }

    public bool HasBootstrapped => _readyEventArgs != null;

    public SimulationReadyEventArgs LatestBootstrap => _readyEventArgs ?? throw new InvalidOperationException("Bootstrap has not completed yet.");

    public IReadOnlyList<string> NeedAttributeNames => _needAttributeNames;

    public IReadOnlyList<ThingPlanParticipation> GetThingPlanParticipation(ThingId thingId, IReadOnlyCollection<string> tags)
    {
        if (_readyEventArgs == null)
        {
            throw new InvalidOperationException("GetThingPlanParticipation cannot be used before the simulation bootstrap completes.");
        }

        if (string.IsNullOrWhiteSpace(thingId.Value))
        {
            throw new ArgumentException("A valid thing id must be supplied to query plan participation.", nameof(thingId));
        }

        if (tags == null || tags.Count == 0)
        {
            return Array.Empty<ThingPlanParticipation>();
        }

        var index = _thingPlanParticipationByTag ?? throw new InvalidOperationException("Plan participation index has not been constructed.");
        var results = new List<ThingPlanParticipation>();
        var dedupe = new HashSet<ThingPlanParticipation>();

        foreach (var tag in tags)
        {
            var normalized = NormalizeParticipationTag(tag);
            if (normalized == null)
            {
                continue;
            }

            if (!index.TryGetValue(normalized, out var entries) || entries == null)
            {
                continue;
            }

            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (dedupe.Add(entry))
                {
                    results.Add(entry);
                }
            }
        }

        if (results.Count == 0)
        {
            return Array.Empty<ThingPlanParticipation>();
        }

        results.Sort(CompareParticipation);
        return results.ToArray();
    }

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

    public long ExecuteManualPlanStep(
        ThingId actorId,
        int planStepIndex,
        ThingId? expectedTargetId,
        GridPos? expectedTargetPosition,
        long expectedSnapshotVersion)
    {
        if (planStepIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(planStepIndex), "Plan step index must be non-negative.");
        }

        if (expectedSnapshotVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSnapshotVersion), "Snapshot version must be non-negative.");
        }

        var (state, priorityJitter) = PrepareManualPlanExecution(actorId);
        var snapshot = _world.Snap();
        if (snapshot == null)
        {
            throw new InvalidOperationException("World snapshot could not be captured for manual plan execution.");
        }

        if (snapshot.Version != expectedSnapshotVersion)
        {
            throw new InvalidOperationException(
                $"Manual plan snapshot version mismatch. Expected {expectedSnapshotVersion}, but world reports {snapshot.Version}.");
        }

        var actorThing = snapshot.GetThing(actorId);
        if (actorThing == null)
        {
            throw new InvalidOperationException($"Manual actor '{actorId.Value}' is not present in the current world snapshot.");
        }

        var plan = _planner.Plan(snapshot, actorId, null, priorityJitter, state.Rng);
        if (plan == null || plan.Steps == null || plan.Steps.Count == 0)
        {
            throw new InvalidOperationException($"Manual actor '{actorId.Value}' does not have a valid plan to execute.");
        }

        if (planStepIndex >= plan.Steps.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(planStepIndex),
                $"Manual actor '{actorId.Value}' plan does not contain a step at index {planStepIndex}.");
        }

        return ExecuteManualPlanStepInternal(
            actorId,
            planStepIndex,
            expectedTargetId,
            expectedTargetPosition,
            snapshot,
            plan,
            state,
            expectedActivityName: null);
    }

    public long ExecuteManualPlanStepSequence(
        ThingId actorId,
        int planStepIndex,
        string expectedActivity,
        ThingId? expectedTargetId,
        GridPos? expectedTargetPosition,
        long expectedSnapshotVersion,
        int guardIterationLimit = 16)
    {
        if (planStepIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(planStepIndex), "Plan step index must be non-negative.");
        }

        if (expectedSnapshotVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSnapshotVersion), "Snapshot version must be non-negative.");
        }

        if (guardIterationLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(guardIterationLimit), "Guard iteration limit must be positive.");
        }

        if (string.IsNullOrWhiteSpace(expectedActivity))
        {
            throw new ArgumentException("Manual plan execution requires the expected activity identifier.", nameof(expectedActivity));
        }

        var (state, priorityJitter) = PrepareManualPlanExecution(actorId);
        long currentExpectedVersion = expectedSnapshotVersion;

        for (int iteration = 0; iteration < guardIterationLimit; iteration++)
        {
            var snapshot = _world.Snap();
            if (snapshot == null)
            {
                throw new InvalidOperationException("World snapshot could not be captured for manual plan execution.");
            }

            if (snapshot.Version != currentExpectedVersion)
            {
                throw new InvalidOperationException(
                    $"Manual plan snapshot version mismatch. Expected {currentExpectedVersion}, but world reports {snapshot.Version}.");
            }

            var actorThing = snapshot.GetThing(actorId);
            if (actorThing == null)
            {
                throw new InvalidOperationException($"Manual actor '{actorId.Value}' is not present in the current world snapshot.");
            }

            var plan = _planner.Plan(snapshot, actorId, null, priorityJitter, state.Rng);
            if (plan == null || plan.Steps == null || plan.Steps.Count == 0)
            {
                throw new InvalidOperationException($"Manual actor '{actorId.Value}' does not have a valid plan to execute.");
            }

            int resolvedIndex = ResolveManualPlanStepIndex(
                plan,
                planStepIndex,
                expectedActivity,
                expectedTargetId,
                expectedTargetPosition,
                snapshot);

            planStepIndex = resolvedIndex;
            var targetStep = plan.Steps[resolvedIndex];
            if (targetStep == null)
            {
                throw new InvalidOperationException(
                    $"Manual actor '{actorId.Value}' plan step at index {resolvedIndex} is null.");
            }

            if (targetStep.Preconditions == null || targetStep.Preconditions(snapshot))
            {
                ThingId? resolvedTargetId = expectedTargetId;
                GridPos? resolvedTargetPosition = expectedTargetPosition;

                if (!resolvedTargetId.HasValue && !string.IsNullOrWhiteSpace(targetStep.Target.Value))
                {
                    resolvedTargetId = targetStep.Target;
                }

                if (!resolvedTargetPosition.HasValue && resolvedTargetId.HasValue)
                {
                    var resolvedTargetThing = snapshot.GetThing(resolvedTargetId.Value);
                    if (resolvedTargetThing == null)
                    {
                        throw new InvalidOperationException(
                            $"Manual plan target '{resolvedTargetId.Value.Value ?? "<unknown>"}' is not present in the current world snapshot.");
                    }

                    resolvedTargetPosition = resolvedTargetThing.Position;
                }

                return ExecuteManualPlanStepInternal(
                    actorId,
                    resolvedIndex,
                    resolvedTargetId,
                    resolvedTargetPosition,
                    snapshot,
                    plan,
                    state,
                    targetStep.ActivityName);
            }

            if (resolvedIndex == 0)
            {
                throw new InvalidOperationException(
                    $"Manual plan step '{expectedActivity}' preconditions are not satisfied and there are no prior steps to execute.");
            }

            bool executedPrefixStep = false;
            for (int prefixIndex = 0; prefixIndex < resolvedIndex; prefixIndex++)
            {
                var prefixStep = plan.Steps[prefixIndex];
                if (prefixStep == null)
                {
                    throw new InvalidOperationException(
                        $"Manual plan contains a null step at index {prefixIndex} while preparing to execute '{expectedActivity}'.");
                }

                if (prefixStep.Preconditions != null && !prefixStep.Preconditions(snapshot))
                {
                    continue;
                }

                ThingId? prefixTarget = string.IsNullOrWhiteSpace(prefixStep.Target.Value)
                    ? (ThingId?)null
                    : prefixStep.Target;

                long postVersion = ExecuteManualPlanStepInternal(
                    actorId,
                    prefixIndex,
                    prefixTarget,
                    expectedTargetPosition: null,
                    snapshot,
                    plan,
                    state,
                    prefixStep.ActivityName);

                currentExpectedVersion = postVersion;
                executedPrefixStep = true;
                break;
            }

            if (!executedPrefixStep)
            {
                throw new InvalidOperationException(
                    $"Manual plan step '{expectedActivity}' preconditions remain unsatisfied because no prior steps were executable.");
            }
        }

        throw new InvalidOperationException(
            $"Manual plan step '{expectedActivity}' preconditions were not satisfied within {guardIterationLimit} sequential execution attempts.");
    }

    private (ManualActorState State, double PriorityJitter) PrepareManualPlanExecution(ThingId actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId.Value))
        {
            throw new ArgumentException("Manual actor id must be provided.", nameof(actorId));
        }

        if (_readyEventArgs == null)
        {
            throw new InvalidOperationException("Manual plan execution cannot occur before the simulation bootstrap completes.");
        }

        if (_world == null || _planner == null || _executors == null || _reservations == null)
        {
            throw new InvalidOperationException(
                "Manual plan execution requires the world, planner, executor registry, and reservation service to be initialized.");
        }

        if (!_manualPawnIds.Contains(actorId))
        {
            throw new InvalidOperationException($"Actor '{actorId.Value}' is not configured for manual plan execution.");
        }

        double priorityJitter = _demoConfig?.simulation?.priorityJitter ?? 0.0;
        var state = GetManualActorState(actorId);
        return (state, priorityJitter);
    }

    private long ExecuteManualPlanStepInternal(
        ThingId actorId,
        int planStepIndex,
        ThingId? expectedTargetId,
        GridPos? expectedTargetPosition,
        IWorldSnapshot snapshot,
        Plan plan,
        ManualActorState state,
        string expectedActivityName)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (plan == null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        if (planStepIndex < 0 || planStepIndex >= plan.Steps.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(planStepIndex));
        }

        var step = plan.Steps[planStepIndex];
        if (step == null)
        {
            throw new InvalidOperationException(
                $"Manual actor '{actorId.Value}' plan step at index {planStepIndex} is null.");
        }

        if (!step.Actor.Equals(actorId))
        {
            throw new InvalidOperationException(
                $"Manual actor '{actorId.Value}' plan step at index {planStepIndex} targets actor '{step.Actor.Value ?? "<unknown>"}'.");
        }

        if (!string.IsNullOrWhiteSpace(expectedActivityName) &&
            !string.Equals(step.ActivityName ?? string.Empty, expectedActivityName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Manual plan step at index {planStepIndex} resolved to activity '{step.ActivityName ?? "<unknown>"}', expected '{expectedActivityName}'.");
        }

        EnsureManualPlanNotOnCooldown(state, step);

        var stepTarget = step.Target;
        bool targetSpecified = !string.IsNullOrWhiteSpace(stepTarget.Value);
        ThingView targetThing = null;
        if (targetSpecified)
        {
            targetThing = snapshot.GetThing(stepTarget);
            if (targetThing == null)
            {
                throw new InvalidOperationException(
                    $"Manual plan target '{stepTarget.Value}' is not present in the snapshot used to generate the plan.");
            }
        }

        if (expectedTargetId.HasValue)
        {
            if (!targetSpecified)
            {
                throw new InvalidOperationException(
                    $"Manual plan step '{step.ActivityName ?? "<unknown>"}' does not declare a target but the caller expected '{expectedTargetId.Value.Value}'.");
            }

            if (!stepTarget.Equals(expectedTargetId.Value))
            {
                throw new InvalidOperationException(
                    $"Manual plan step target mismatch. Planner selected '{stepTarget.Value}', but caller expected '{expectedTargetId.Value.Value}'.");
            }
        }

        if (expectedTargetPosition.HasValue)
        {
            if (!targetSpecified)
            {
                throw new InvalidOperationException(
                    $"Manual plan step '{step.ActivityName ?? "<unknown>"}' does not have a target position to validate.");
            }

            if (!targetThing.Position.Equals(expectedTargetPosition.Value))
            {
                var expected = expectedTargetPosition.Value;
                var actual = targetThing.Position;
                throw new InvalidOperationException(
                    $"Manual plan target '{stepTarget.Value}' moved from ({actual.X}, {actual.Y}) to ({expected.X}, {expected.Y}) relative to the caller's expectations.");
            }
        }

        if (step.Preconditions != null && !step.Preconditions(snapshot))
        {
            throw new InvalidOperationException(
                $"Manual plan step '{step.ActivityName ?? "<unknown>"}' preconditions are not satisfied.");
        }

        double durationSeconds = 0.0;
        if (step.DurationSeconds != null)
        {
            durationSeconds = step.DurationSeconds(snapshot);
            if (double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds))
            {
                throw new InvalidOperationException(
                    $"Manual plan step '{step.ActivityName ?? "<unknown>"}' produced a non-finite duration.");
            }

            durationSeconds = Math.Max(0.0, durationSeconds);
        }

        var planId = Guid.NewGuid();
        if (!_reservations.TryAcquireAll(step.Reservations, planId, actorId))
        {
            throw new InvalidOperationException(
                $"Manual plan step '{step.ActivityName ?? "<unknown>"}' could not acquire required reservations.");
        }

        try
        {
            var executor = _executors.Resolve(step.ActivityName);
            if (executor == null)
            {
                throw new InvalidOperationException(
                    $"No executor registered for activity '{step.ActivityName ?? "<unknown>"}'.");
            }

            var context = new ExecutionContext(snapshot, actorId, state.Rng);
            var progress = executor.Run(step, context, out var batch);
            if (progress != ExecProgress.Completed)
            {
                throw new InvalidOperationException(
                    $"Manual plan step '{step.ActivityName ?? "<unknown>"}' did not complete execution (status {progress}).");
            }

            if (batch.BaseVersion != snapshot.Version)
            {
                throw new InvalidOperationException(
                    $"Manual plan step '{step.ActivityName ?? "<unknown>"}' produced effects for snapshot version {batch.BaseVersion}, but execution used snapshot {snapshot.Version}.");
            }

            var commit = _world.TryCommit(batch);
            if (commit == CommitResult.Conflict)
            {
                throw new InvalidOperationException(
                    $"Manual plan step '{step.ActivityName ?? "<unknown>"}' conflicted while applying its effects.");
            }

            var postSnapshot = _world.Snap();
            if (postSnapshot == null)
            {
                throw new InvalidOperationException("Failed to capture post-execution snapshot for manual plan step.");
            }

            var worldTime = FormatWorldTime(postSnapshot.Time);
            var worldDay = FormatWorldDay(postSnapshot.Time);
            var executionContext = BuildExecutionContextString(planId, plan.GoalId, step.ActivityName, snapshot.Version, worldTime, worldDay);

            ApplyManualPostCommitEffects(actorId, step.ActivityName, planId, batch, executionContext);
            _worldLogger?.LogEffectSummary(actorId, planId, step.ActivityName, batch, snapshot.Version, worldTime, worldDay);
            RegisterManualPlanCooldowns(state, step, durationSeconds, batch.PlanCooldowns);

            return postSnapshot.Version;
        }
        finally
        {
            _reservations.ReleaseAll(step.Reservations, planId, actorId);
        }
    }

    private int ResolveManualPlanStepIndex(
        Plan plan,
        int hintedIndex,
        string expectedActivity,
        ThingId? expectedTargetId,
        GridPos? expectedTargetPosition,
        IWorldSnapshot snapshot)
    {
        if (plan == null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (plan.Steps == null || plan.Steps.Count == 0)
        {
            throw new InvalidOperationException("Plan does not contain any steps.");
        }

        bool MatchesDescriptor(PlanStep candidate)
        {
            if (candidate == null)
            {
                return false;
            }

            if (!string.Equals(candidate.ActivityName ?? string.Empty, expectedActivity, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!expectedTargetId.HasValue)
            {
                return true;
            }

            return candidate.Target.Equals(expectedTargetId.Value);
        }

        if (hintedIndex >= 0 && hintedIndex < plan.Steps.Count)
        {
            var hintedStep = plan.Steps[hintedIndex];
            if (MatchesDescriptor(hintedStep))
            {
                ValidateTargetLocation(expectedTargetId, expectedTargetPosition, snapshot);
                return hintedIndex;
            }
        }

        for (int i = 0; i < plan.Steps.Count; i++)
        {
            if (MatchesDescriptor(plan.Steps[i]))
            {
                ValidateTargetLocation(expectedTargetId, expectedTargetPosition, snapshot);
                return i;
            }
        }

        string targetText = expectedTargetId.HasValue ? expectedTargetId.Value.Value ?? "<none>" : "<none>";
        throw new InvalidOperationException(
            $"Manual plan step '{expectedActivity}' targeting '{targetText}' could not be located in the current plan.");
    }

    private static void ValidateTargetLocation(
        ThingId? expectedTargetId,
        GridPos? expectedTargetPosition,
        IWorldSnapshot snapshot)
    {
        if (!expectedTargetId.HasValue || !expectedTargetPosition.HasValue)
        {
            return;
        }

        var targetThing = snapshot.GetThing(expectedTargetId.Value);
        if (targetThing == null)
        {
            throw new InvalidOperationException(
                $"Manual plan target '{expectedTargetId.Value.Value ?? "<unknown>"}' is not present in the current world snapshot.");
        }

        if (!targetThing.Position.Equals(expectedTargetPosition.Value))
        {
            var expected = expectedTargetPosition.Value;
            var actual = targetThing.Position;
            throw new InvalidOperationException(
                $"Manual plan target '{expectedTargetId.Value.Value ?? "<unknown>"}' moved from ({actual.X}, {actual.Y}) to ({expected.X}, {expected.Y}).");
        }
    }

    private ManualActorState GetManualActorState(ThingId actorId)
    {
        string key = actorId.Value ?? throw new InvalidOperationException("Manual actor id must provide a string value.");
        if (!_manualActorStates.TryGetValue(key, out var state) || state == null)
        {
            int seed = _demoConfig?.simulation?.actorHostSeed ?? 0;
            state = new ManualActorState(new System.Random(seed ^ actorId.GetHashCode()));
            _manualActorStates[key] = state;
        }

        return state;
    }

    private void EnsureManualPlanNotOnCooldown(ManualActorState state, PlanStep step)
    {
        if (state == null || step == null)
        {
            return;
        }

        string key = BuildCooldownKey(step.ActivityName, step.Target);
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (!state.PlanCooldownUntil.TryGetValue(key, out var until))
        {
            return;
        }

        if (until <= DateTime.UtcNow)
        {
            state.PlanCooldownUntil.Remove(key);
            return;
        }

        double remaining = Math.Max(0.0, (until - DateTime.UtcNow).TotalSeconds);
        string remainingText = remaining.ToString("0.###", CultureInfo.InvariantCulture);
        throw new InvalidOperationException(
            $"Manual plan step '{step.ActivityName ?? "<unknown>"}' targeting '{step.Target.Value ?? "<none>"}' is on cooldown for another {remainingText} seconds.");
    }

    private void RegisterManualPlanCooldowns(ManualActorState state, PlanStep step, double durationSeconds, PlanCooldownRequest[] requests)
    {
        if (state == null || step == null)
        {
            return;
        }

        if (requests == null || requests.Length == 0)
        {
            return;
        }

        string activity = step.ActivityName ?? string.Empty;
        for (int i = 0; i < requests.Length; i++)
        {
            var request = requests[i];
            var scope = request.Scope;
            if (string.IsNullOrWhiteSpace(scope.Value))
            {
                scope = step.Target;
            }

            string key = BuildCooldownKey(activity, scope);
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            double seconds = Math.Max(0.0, request.Seconds);
            if (request.UseStepDuration)
            {
                seconds = Math.Max(seconds, durationSeconds);
            }

            if (seconds <= 0.0)
            {
                continue;
            }

            state.PlanCooldownUntil[key] = DateTime.UtcNow.AddSeconds(seconds);
        }
    }

    private static string BuildCooldownKey(string activity, ThingId scope)
    {
        string act = activity ?? string.Empty;
        string target = scope.Value ?? string.Empty;
        return string.Concat(act, "|", target);
    }

    private static string BuildExecutionContextString(
        Guid planId,
        string goalId,
        string activity,
        long snapshotVersion,
        string worldTime,
        string worldDay)
    {
        string planText = planId == Guid.Empty ? "<none>" : planId.ToString();
        string goalText = string.IsNullOrWhiteSpace(goalId) ? "<none>" : goalId;
        string activityText = string.IsNullOrWhiteSpace(activity) ? "<unknown>" : activity;
        string wt = string.IsNullOrWhiteSpace(worldTime) ? "<unknown>" : worldTime;
        string wd = string.IsNullOrWhiteSpace(worldDay) ? "<unknown>" : worldDay;
        return $"plan={planText} goal={goalText} activity={activityText} snapshot_version={snapshotVersion} world_time={wt} world_day={wd}";
    }

    private static string FormatWorldTime(WorldTimeSnapshot time)
    {
        if (time == null)
        {
            return "<unknown>";
        }

        string date = time.Year > 0 && time.Month > 0 && time.DayOfMonth > 0
            ? $"{time.Year:D4}-{time.Month:D2}-{time.DayOfMonth:D2}"
            : $"day{time.DayOfYear:D3}";
        string tod = time.TimeOfDay.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
        return string.Concat(date, "T", tod);
    }

    private static string FormatWorldDay(WorldTimeSnapshot time)
    {
        if (time == null)
        {
            return "<unknown>";
        }

        return time.TotalWorldDays.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void ApplyManualPostCommitEffects(
        ThingId actorId,
        string activityName,
        Guid planId,
        in EffectBatch batch,
        string executionContext)
    {
        ProcessInventoryChanges(batch.InventoryOps, null, executionContext);

        if (_shopSystem != null && batch.ShopTransactions != null)
        {
            foreach (var txn in batch.ShopTransactions)
            {
                if (!_shopSystem.TryProcessTransaction(txn, out var result) || result.Quantity <= 0)
                {
                    continue;
                }

                double total = result.TotalPrice;
                if (_inventorySystem != null)
                {
                    double actorDelta = txn.Kind == ShopTransactionKind.Sale ? total : -total;
                    double shopDelta = -actorDelta;
                    double actorBalance = _inventorySystem.AdjustCurrency(txn.Actor, actorDelta);
                    double shopBalance = _inventorySystem.AdjustCurrency(txn.Shop, shopDelta);
                    _worldLogger?.LogCurrencyChange(txn.Actor, actorDelta, actorBalance, "shop_txn", executionContext);
                    _worldLogger?.LogCurrencyChange(txn.Shop, shopDelta, shopBalance, "shop_txn", executionContext);
                }

                _worldLogger?.LogShopTransaction(txn.Shop, txn.Actor, txn.ItemId, result.Quantity, total, txn.Kind, executionContext);
            }
        }

        ProcessCurrencyChanges(batch.CurrencyOps, null, executionContext);

        if (_socialSystem != null && batch.RelationshipOps != null)
        {
            foreach (var rel in batch.RelationshipOps)
            {
                double delta = rel.ExplicitDelta ?? LookupGiftDelta(rel);
                if (Math.Abs(delta) < 1e-6)
                {
                    continue;
                }

                _socialSystem.AdjustRelationship(rel.From, rel.To, rel.RelationshipId, delta);
                _worldLogger?.LogCustom(
                    "relationship",
                    rel.From,
                    $"to={rel.To.Value ?? "<unknown>"} rel={rel.RelationshipId ?? "<none>"} delta={delta.ToString("0.###", CultureInfo.InvariantCulture)}",
                    executionContext);
            }
        }

        if (_cropSystem != null && batch.CropOps != null)
        {
            foreach (var op in batch.CropOps)
            {
                var result = _cropSystem.Apply(op);
                if (!result.Success)
                {
                    continue;
                }

                ProcessInventoryChanges(result.InventoryChanges, "crop", executionContext);

                if (result.HarvestYields != null)
                {
                    foreach (var yield in result.HarvestYields)
                    {
                        if (yield.Quantity <= 0 || string.IsNullOrWhiteSpace(yield.ItemId))
                        {
                            continue;
                        }

                        _worldLogger?.LogInventoryChange(op.Actor, yield.ItemId, yield.Quantity, "crop", executionContext);
                    }
                }
            }
        }

        if (_animalSystem != null && batch.AnimalOps != null)
        {
            foreach (var op in batch.AnimalOps)
            {
                var result = _animalSystem.Apply(op);
                if (!result.Success)
                {
                    continue;
                }

                ProcessInventoryChanges(result.InventoryChanges, "animal", executionContext);

                if (result.ProduceYields != null)
                {
                    foreach (var yield in result.ProduceYields)
                    {
                        if (yield.Quantity <= 0 || string.IsNullOrWhiteSpace(yield.ItemId))
                        {
                            continue;
                        }

                        _worldLogger?.LogInventoryChange(op.Actor, yield.ItemId, yield.Quantity, "animal", executionContext);
                    }
                }
            }
        }

        if (_miningSystem != null && batch.MiningOps != null)
        {
            foreach (var op in batch.MiningOps)
            {
                var result = _miningSystem.Apply(op);
                if (!result.Success)
                {
                    continue;
                }

                ProcessInventoryChanges(result.InventoryChanges, "mining", executionContext);
                GrantSkillExperience(op.Actor, result.SkillId, result.SkillXp, "mining", executionContext);

                if (!string.IsNullOrWhiteSpace(result.ItemId) && result.Quantity > 0)
                {
                    _worldLogger?.LogCustom(
                        "mine_extract",
                        op.Actor,
                        $"node={op.Node.Value ?? "<unknown>"} item={result.ItemId} qty={result.Quantity}",
                        executionContext);
                }
            }
        }

        if (_fishingSystem != null && batch.FishingOps != null)
        {
            foreach (var op in batch.FishingOps)
            {
                var result = _fishingSystem.Apply(op);
                if (!result.Success)
                {
                    continue;
                }

                ProcessInventoryChanges(result.InventoryChanges, "fishing", executionContext);
                GrantSkillExperience(op.Actor, result.SkillId, result.SkillXp, "fishing", executionContext);

                if (!string.IsNullOrWhiteSpace(result.ItemId) && result.Quantity > 0)
                {
                    _worldLogger?.LogCustom(
                        "fish_catch",
                        op.Actor,
                        $"spot={op.Spot.Value ?? "<unknown>"} item={result.ItemId} qty={result.Quantity}",
                        executionContext);
                }
            }
        }

        if (_foragingSystem != null && batch.ForagingOps != null)
        {
            foreach (var op in batch.ForagingOps)
            {
                var result = _foragingSystem.Apply(op);
                if (!result.Success)
                {
                    continue;
                }

                ProcessInventoryChanges(result.InventoryChanges, "forage", executionContext);
                GrantSkillExperience(op.Actor, result.SkillId, result.SkillXp, "foraging", executionContext);

                if (!string.IsNullOrWhiteSpace(result.ItemId) && result.Quantity > 0)
                {
                    _worldLogger?.LogCustom(
                        "forage_collect",
                        op.Actor,
                        $"spot={op.Spot.Value ?? "<unknown>"} item={result.ItemId} qty={result.Quantity}",
                        executionContext);
                }
            }
        }

        if (_questSystem != null && batch.QuestOps != null)
        {
            foreach (var op in batch.QuestOps)
            {
                var result = _questSystem.Apply(op);
                if (!result.Success)
                {
                    continue;
                }

                ProcessInventoryChanges(result.InventoryChanges, "quest", executionContext);
                ProcessCurrencyChanges(result.CurrencyChanges, "quest", executionContext);

                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    _worldLogger?.LogQuestEvent(
                        op.Actor,
                        op.QuestId,
                        result.Status.ToString(),
                        result.ObjectiveId,
                        result.ObjectiveProgress,
                        result.ObjectiveRequired,
                        result.Message,
                        executionContext);
                }
            }
        }
    }

    private void ProcessInventoryChanges(IEnumerable<InventoryDelta> operations, string sourceTag, string executionContext)
    {
        if (_inventorySystem == null || operations == null)
        {
            return;
        }

        string source = string.IsNullOrWhiteSpace(sourceTag) ? "effect" : sourceTag.Trim();
        foreach (var op in operations)
        {
            if (string.IsNullOrWhiteSpace(op.ItemId) || op.Quantity <= 0)
            {
                continue;
            }

            int processed = op.Remove
                ? _inventorySystem.RemoveItem(op.Owner, op.ItemId, op.Quantity)
                : _inventorySystem.AddItem(op.Owner, op.ItemId, op.Quantity);

            if (processed <= 0)
            {
                continue;
            }

            int signedQty = op.Remove ? -processed : processed;
            _worldLogger?.LogInventoryChange(op.Owner, op.ItemId, signedQty, source, executionContext);
        }
    }

    private void ProcessCurrencyChanges(IEnumerable<CurrencyDelta> operations, string sourceTag, string executionContext)
    {
        if (_inventorySystem == null || operations == null)
        {
            return;
        }

        string source = string.IsNullOrWhiteSpace(sourceTag) ? "effect" : sourceTag.Trim();
        foreach (var delta in operations)
        {
            if (Math.Abs(delta.Amount) < 1e-6)
            {
                continue;
            }

            double balance = _inventorySystem.AdjustCurrency(delta.Owner, delta.Amount);
            _worldLogger?.LogCurrencyChange(delta.Owner, delta.Amount, balance, source, executionContext);
        }
    }

    private void GrantSkillExperience(ThingId actor, string skillId, double amount, string sourceTag, string executionContext)
    {
        if (_skillSystem == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(actor.Value) || string.IsNullOrWhiteSpace(skillId))
        {
            return;
        }

        if (!double.IsFinite(amount) || amount <= 0.0)
        {
            return;
        }

        _skillSystem.AddExperience(actor, skillId, amount);
        string tag = string.IsNullOrWhiteSpace(sourceTag) ? "effect" : sourceTag;
        string message = $"skill={skillId} amount={amount.ToString("0.###", CultureInfo.InvariantCulture)} source={tag}";
        _worldLogger?.LogCustom("skill_xp", actor, message, executionContext);
    }

    private double LookupGiftDelta(RelationshipDelta rel)
    {
        if (_inventorySystem == null || string.IsNullOrWhiteSpace(rel.ItemId))
        {
            return 0.0;
        }

        if (!_inventorySystem.TryGetItemDefinition(rel.ItemId, out var item) || item == null)
        {
            return 0.0;
        }

        foreach (var affinity in item.GiftAffinities)
        {
            if (string.IsNullOrWhiteSpace(affinity))
            {
                continue;
            }

            var parts = affinity.Split(':');
            if (parts.Length != 2)
            {
                continue;
            }

            if (!string.Equals(parts[0], rel.RelationshipId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return 0.0;
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
        _thingPlanParticipationByTag.Clear();
        _manualActorStates.Clear();
        _villageConfig = null;

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
        _manualPawnIds.Clear();
        _manualActorStates.Clear();
        _playerPawnId = null;
        _thingPlanParticipationByTag.Clear();

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
        _thingPlanParticipationByTag = BuildThingPlanParticipationIndex(_goalConfigs, _actionConfigs);
        var itemConfigs = ConfigLoader.LoadItems(RequireFile(datasetRoot, _demoConfig.items?.catalog, "items.catalog"));
        var recipeConfigs = ConfigLoader.LoadRecipes(RequireFile(datasetRoot, _demoConfig.items?.recipes, "items.recipes"));
        var cropConfigs = ConfigLoader.LoadCrops(RequireFile(datasetRoot, _demoConfig.farming?.crops, "farming.crops"));
        var animalConfigs = ConfigLoader.LoadAnimals(RequireFile(datasetRoot, _demoConfig.livestock?.animals, "livestock.animals"));
        _scheduleDatabase = ConfigLoader.LoadSchedules(RequireFile(datasetRoot, _demoConfig.schedules?.path, "schedules.path"));
        var eventDatabase = ConfigLoader.LoadCalendarEvents(RequireFile(datasetRoot, _demoConfig.events?.path, "events.path"));
        _dialogueDatabase = ConfigLoader.LoadDialogue(RequireFile(datasetRoot, _demoConfig.dialogue?.path, "dialogue.path"));
        var questDatabase = ConfigLoader.LoadQuests(RequireFile(datasetRoot, _demoConfig.quests?.path, "quests.path"));
        _villageConfig = ConfigLoader.LoadVillageConfig(RequireFile(datasetRoot, _demoConfig.world?.map?.data, "world.map.data"));
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

        var mapData = LoadTileClassification(mapImagePath, _demoConfig.world.map, _villageConfig);
        _tiles = mapData.Classification;
        _mapTexture = mapData.Texture;

        var seeds = BuildThingSeeds(_demoConfig.world, _villageConfig);
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
        string worldLogPath = Path.Combine(logRoot, "world.log.txt");
        bool worldLoggingEnabled = _demoConfig?.simulation?.worldLoggingEnabled ?? false;

        if (worldLoggingEnabled)
        {
            Directory.CreateDirectory(logRoot);
            _worldLogger = new WorldLogger(worldLogPath, enabled: true);
        }
        else
        {
            DeleteWorldLogFiles(worldLogPath);
            _worldLogger = null;
        }

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
        PerformInitialShopRestock();
        NotifyBootstrapped(datasetRoot);
        StartSimulation();
    }

    private static Dictionary<string, ThingPlanParticipation[]> BuildThingPlanParticipationIndex(
        IEnumerable<GoalConfig> goals,
        IEnumerable<ActionConfig> actions)
    {
        var result = new Dictionary<string, ThingPlanParticipation[]>(StringComparer.Ordinal);
        if (goals == null)
        {
            return result;
        }

        var actionById = (actions ?? Enumerable.Empty<ActionConfig>())
            .Where(action => action != null && !string.IsNullOrWhiteSpace(action.id))
            .ToDictionary(action => action.id.Trim(), StringComparer.OrdinalIgnoreCase);

        var working = new Dictionary<string, List<ThingPlanParticipation>>(StringComparer.Ordinal);

        foreach (var goal in goals)
        {
            if (goal == null || string.IsNullOrWhiteSpace(goal.id) || goal.actions == null)
            {
                continue;
            }

            var goalId = goal.id.Trim();
            foreach (var goalAction in goal.actions)
            {
                if (goalAction == null)
                {
                    continue;
                }

                var normalizedTag = NormalizeParticipationTag(goalAction.target?.tag);
                if (normalizedTag == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(goalAction.id))
                {
                    continue;
                }

                var actionId = goalAction.id.Trim();
                actionById.TryGetValue(actionId, out var actionConfig);
                var activity = actionConfig?.activity;
                var participation = new ThingPlanParticipation(goalId, actionId, activity, goalAction.moveToTarget);

                if (!working.TryGetValue(normalizedTag, out var list))
                {
                    list = new List<ThingPlanParticipation>();
                    working[normalizedTag] = list;
                }

                if (!list.Contains(participation))
                {
                    list.Add(participation);
                }
            }
        }

        foreach (var entry in working)
        {
            var items = entry.Value;
            items.Sort(CompareParticipation);
            result[entry.Key] = items.ToArray();
        }

        return result;
    }

    private static string NormalizeParticipationTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        return tag.Trim().ToLowerInvariant();
    }

    private static int CompareParticipation(ThingPlanParticipation x, ThingPlanParticipation y)
    {
        int goalCompare = string.Compare(x.GoalId, y.GoalId, StringComparison.OrdinalIgnoreCase);
        if (goalCompare != 0)
        {
            return goalCompare;
        }

        int actionCompare = string.Compare(x.ActionId, y.ActionId, StringComparison.OrdinalIgnoreCase);
        if (actionCompare != 0)
        {
            return actionCompare;
        }

        int activityCompare = string.Compare(x.Activity, y.Activity, StringComparison.OrdinalIgnoreCase);
        if (activityCompare != 0)
        {
            return activityCompare;
        }

        return x.MoveToTarget.CompareTo(y.MoveToTarget);
    }

    private void PerformInitialShopRestock()
    {
        if (_clock == null)
        {
            throw new InvalidOperationException("World clock must be initialized before performing the bootstrap restock.");
        }

        if (_shopSystem == null)
        {
            throw new InvalidOperationException("Shop system must be initialized before performing the bootstrap restock.");
        }

        if (_inventorySystem == null)
        {
            throw new InvalidOperationException("Inventory system must be initialized before performing the bootstrap restock.");
        }

        var bootstrapTime = _clock.Snapshot();
        _shopSystem.Tick(bootstrapTime);

        var generalStoreId = new ThingId("store_generalstore");
        var generalStoreInventory = _inventorySystem.GetInventory(generalStoreId);
        if (generalStoreInventory == null)
        {
            throw new InvalidOperationException(
                "Inventory for 'store_generalstore' was not initialized after the bootstrap restock tick.");
        }

        const string breadItemId = "bread_loaf";
        var inventoryStacks = generalStoreInventory.GetStacks();
        bool hasBread = inventoryStacks.Any(stack =>
            stack.Item != null &&
            stack.Quantity > 0 &&
            string.Equals(stack.Item.Id, breadItemId, StringComparison.OrdinalIgnoreCase));
        if (!hasBread)
        {
            throw new InvalidOperationException(
                "Inventory for 'store_generalstore' did not contain item 'bread_loaf' after the bootstrap restock tick.");
        }

        var generalStore = _shopSystem.GetShop(generalStoreId);
        if (generalStore == null)
        {
            throw new InvalidOperationException(
                "Shop 'store_generalstore' was not registered after the bootstrap restock tick.");
        }

        bool hasStock = generalStore.Stock.Any(entry => entry != null && entry.Quantity > 0);
        if (!hasStock)
        {
            throw new InvalidOperationException(
                "Shop 'store_generalstore' did not report stocked inventory after the bootstrap restock tick.");
        }
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

        if (_tiles == null)
        {
            throw new InvalidOperationException("Tile classification must be initialized before publishing bootstrap completion.");
        }

        var manual = _manualPawnIds.ToArray();
        var tileClassification = new SimulationReadyEventArgs.TileClassificationSnapshot(
            CloneTileLayer(_tiles.Walkable),
            CloneTileLayer(_tiles.Water),
            CloneTileLayer(_tiles.Shallow),
            CloneTileLayer(_tiles.Forest),
            CloneTileLayer(_tiles.Farmland),
            CloneTileLayer(_tiles.Coastal));
        _readyEventArgs = new SimulationReadyEventArgs(
            _world,
            Array.AsReadOnly(actors),
            datasetRoot,
            _mapTexture,
            _clock,
            new Dictionary<ThingId, ActorHostDiagnostics>(_actorDiagnostics),
            _demoConfig?.observer?.cameraPawn,
            Array.AsReadOnly(manual),
            _playerPawnId,
            tileClassification,
            _demoConfig?.observer?.showOnlySelectedPawn ?? false);
        Bootstrapped?.Invoke(this, _readyEventArgs);
    }

    private static bool[,] CloneTileLayer(bool[,] source)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var clone = new bool[source.GetLength(0), source.GetLength(1)];
        Array.Copy(source, clone, source.Length);
        return clone;
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
            if (_manualPawnIds.Contains(entry.Id))
            {
                continue;
            }

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
        var actorConfig = _demoConfig.actors ?? throw new InvalidDataException("actors section missing in demo config.");
        var playerConfig = _demoConfig.player ?? throw new InvalidDataException("player section missing in demo config.");
        if (_playerPawnId == null)
        {
            throw new InvalidOperationException("Player pawn id was not initialized before configuring inventories.");
        }

        foreach (var actor in _actorDefinitions)
        {
            bool isPlayer = actor.Id.Equals(_playerPawnId.Value);
            if (isPlayer)
            {
                if (playerConfig.inventory != null)
                {
                    _inventorySystem.ConfigureInventory(actor.Id, playerConfig.inventory);
                }

                if (playerConfig.currency.HasValue)
                {
                    _inventorySystem.SetCurrency(actor.Id, playerConfig.currency.Value);
                }

                continue;
            }

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
            if (_manualPawnIds.Contains(entry.Id))
            {
                continue;
            }

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
                _worldLogger,
                enablePerActorLogging: false);
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

    private static void DeleteWorldLogFiles(string worldLogPath)
    {
        if (string.IsNullOrWhiteSpace(worldLogPath))
        {
            throw new ArgumentException("World log path must be a valid, non-empty string.", nameof(worldLogPath));
        }

        string directory = Path.GetDirectoryName(worldLogPath);
        string fileName = Path.GetFileName(worldLogPath);

        if (string.IsNullOrEmpty(fileName))
        {
            throw new ArgumentException("World log path must include a file name.", nameof(worldLogPath));
        }

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            return;
        }

        if (File.Exists(worldLogPath))
        {
            File.Delete(worldLogPath);
        }

        string searchDirectory = string.IsNullOrEmpty(directory) ? Directory.GetCurrentDirectory() : directory;
        if (!Directory.Exists(searchDirectory))
        {
            return;
        }

        foreach (var candidate in Directory.GetFiles(searchDirectory, fileName + ".*"))
        {
            if (IsWorldLogSegment(candidate, fileName))
            {
                File.Delete(candidate);
            }
        }
    }

    private static bool IsWorldLogSegment(string candidatePath, string baseFileName)
    {
        string candidate = Path.GetFileName(candidatePath);
        if (string.IsNullOrEmpty(candidate) || !candidate.StartsWith(baseFileName, StringComparison.Ordinal))
        {
            return false;
        }

        if (candidate.Length <= baseFileName.Length + 1 || candidate[baseFileName.Length] != '.')
        {
            return false;
        }

        for (int i = baseFileName.Length + 1; i < candidate.Length; i++)
        {
            if (!char.IsDigit(candidate[i]))
            {
                return false;
            }
        }

        return true;
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

    private static void AddTagIfMissing(List<string> tags, string tag)
    {
        if (tags == null || string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        string trimmed = tag.Trim();
        if (tags.Any(existing => string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        tags.Add(trimmed);
    }

    private void AddActorSeeds(VillageConfig villageConfig, List<ThingSeed> seeds)
    {
        if (villageConfig?.pawns?.pawns == null)
        {
            return;
        }

        var actorConfig = _demoConfig.actors ?? throw new InvalidDataException("actors section missing in demo config.");
        var playerConfig = _demoConfig.player ?? throw new InvalidDataException("player section missing in demo config.");
        if (string.IsNullOrWhiteSpace(playerConfig.id))
        {
            throw new InvalidDataException("player.id must be specified in demo config.");
        }

        var expectedPlayerId = new ThingId(playerConfig.id.Trim());
        bool playerFound = false;
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
                AddTagIfMissing(seed.Tags, tag);
            }

            bool isPlayer = id.Equals(expectedPlayerId);
            if (isPlayer)
            {
                playerFound = true;
                _playerPawnId = id;
                if (!playerConfig.allowAiFallback)
                {
                    _manualPawnIds.Add(id);
                }

                foreach (var tag in playerConfig.tags ?? Array.Empty<string>())
                {
                    AddTagIfMissing(seed.Tags, tag);
                }
            }

            if (!string.IsNullOrWhiteSpace(pawn.role))
            {
                AddTagIfMissing(seed.Tags, pawn.role);
                AddTagIfMissing(seed.Tags, $"role:{pawn.role.Trim().ToLowerInvariant()}");
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

            if (isPlayer && playerConfig.attributes != null)
            {
                foreach (var kvp in playerConfig.attributes)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key))
                    {
                        continue;
                    }

                    seed.Attributes[kvp.Key.Trim()] = kvp.Value;
                }
            }

            if (isPlayer)
            {
                string spawnId = playerConfig.spawn?.id?.Trim();
                if (string.IsNullOrWhiteSpace(spawnId))
                {
                    throw new InvalidDataException("player.spawn.id must be specified in demo config.");
                }

                if (!_locationToThing.TryGetValue(spawnId, out var spawnThing) || !_seedByThing.TryGetValue(spawnThing, out var spawnSeed))
                {
                    throw new InvalidDataException($"Player spawn location '{spawnId}' could not be resolved to a world thing.");
                }

                seed.Position = spawnSeed.Position;
            }

            seeds.Add(seed);
            _seedByThing[id] = seed;
            _actorDefinitions.Add((id, pawn));
        }

        if (!playerFound)
        {
            throw new InvalidDataException($"Demo dataset did not define a pawn with id '{expectedPlayerId.Value}' for the player.");
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
        var unresolvedLocations = new List<string>();

        if (TryResolvePositionFromLocation(pawn?.home?.location, out var resolvedPosition, out var resolvedLocation, out var locationSpecified))
        {
            return resolvedPosition;
        }

        if (locationSpecified)
        {
            unresolvedLocations.Add(resolvedLocation);
        }

        if (TryResolvePositionFromLocation(pawn?.workplace?.location, out resolvedPosition, out resolvedLocation, out locationSpecified))
        {
            return resolvedPosition;
        }

        if (locationSpecified && !unresolvedLocations.Contains(resolvedLocation, StringComparer.OrdinalIgnoreCase))
        {
            unresolvedLocations.Add(resolvedLocation);
        }

        if (unresolvedLocations.Count > 0)
        {
            string pawnId = string.IsNullOrWhiteSpace(pawn?.id) ? "<unknown>" : pawn.id.Trim();
            throw new InvalidDataException($"Pawn '{pawnId}' references unresolved locations: {string.Join(", ", unresolvedLocations)}.");
        }

        return CreateWorldPosition(_demoConfig.world.width / 2, _demoConfig.world.height / 2);
    }

    private bool TryResolvePositionFromLocation(string locationId, out GridPos position, out string resolvedLocationId, out bool locationSpecified)
    {
        position = default;
        resolvedLocationId = null;

        if (string.IsNullOrWhiteSpace(locationId))
        {
            locationSpecified = false;
            return false;
        }

        resolvedLocationId = locationId.Trim();
        locationSpecified = true;

        if (_locationToThing.TryGetValue(resolvedLocationId, out var thing) && _seedByThing.TryGetValue(thing, out var seed))
        {
            position = seed.Position;
            return true;
        }

        var villageConfig = _villageConfig;
        if (villageConfig != null)
        {
            var center = ResolveLocationCenter(villageConfig, resolvedLocationId);
            if (center.HasValue)
            {
                position = center.Value;
                return true;
            }
        }

        return false;
    }

    private void AddConfiguredThings(WorldConfig worldConfig, List<ThingSeed> seeds)
    {
        if (worldConfig == null)
        {
            throw new ArgumentNullException(nameof(worldConfig));
        }

        var configuredThings = worldConfig.things ?? Array.Empty<ThingSpawnConfig>();
        for (int i = 0; i < configuredThings.Length; i++)
        {
            var thing = configuredThings[i];
            if (thing == null)
            {
                throw new InvalidDataException($"World configuration contains a null thing entry at index {i}.");
            }

            if (string.IsNullOrWhiteSpace(thing.id))
            {
                throw new InvalidDataException($"World configuration thing entry at index {i} must declare an id.");
            }

            var trimmedId = thing.id.Trim();
            if (string.IsNullOrWhiteSpace(thing.type))
            {
                throw new InvalidDataException($"Thing '{trimmedId}' must declare a type in the world configuration.");
            }

            var id = new ThingId(trimmedId);
            var seed = new ThingSeed
            {
                Id = id,
                Type = thing.type.Trim(),
                Position = CreateWorldPosition(thing.x, thing.y),
                Building = BuildBuildingInfo(thing.building, thing.building?.service_points, thing.building?.area)
            };

            foreach (var tag in thing.tags ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(tag))
                {
                    throw new InvalidDataException($"Thing '{trimmedId}' includes an invalid tag entry.");
                }

                seed.Tags.Add(tag.Trim());
            }

            foreach (var kv in thing.attributes ?? new Dictionary<string, double>())
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                {
                    throw new InvalidDataException($"Thing '{trimmedId}' attributes contain a blank key.");
                }

                seed.Attributes[kv.Key.Trim()] = kv.Value;
            }

            if (thing.container?.inventory != null)
            {
                if (_inventorySystem == null)
                {
                    throw new InvalidOperationException($"Inventory system must be initialized before configuring container inventory for '{trimmedId}'.");
                }

                _inventorySystem.ConfigureInventory(id, thing.container.inventory);
            }

            if (thing.currency.HasValue)
            {
                if (_inventorySystem == null)
                {
                    throw new InvalidOperationException($"Inventory system must be initialized before assigning currency to '{trimmedId}'.");
                }

                _inventorySystem.SetCurrency(id, thing.currency.Value);
            }

            if (thing.building?.shop != null)
            {
                if (_shopSystem == null)
                {
                    throw new InvalidOperationException($"Shop system must be initialized before registering shop for '{trimmedId}'.");
                }

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
                var position = ResolveLocationCenter(villageConfig, annotation.location) ?? CreateWorldPosition(_demoConfig.world.width / 2, _demoConfig.world.height / 2);
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
            return CreateWorldPositionFromDouble(center[0], center[1]);
        }

        double[] bbox = location.bbox ?? Array.Empty<double>();
        if (bbox.Length >= 4)
        {
            double x = (bbox[0] + bbox[2]) * 0.5;
            double y = (bbox[1] + bbox[3]) * 0.5;
            return CreateWorldPositionFromDouble(x, y);
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

    private GridPos CreateWorldPosition(int x, int y)
    {
        return CreateWorldPosition((int?)x, (int?)y);
    }

    private GridPos CreateWorldPosition(int? x, int? y)
    {
        if (_demoConfig?.world == null)
        {
            throw new InvalidOperationException("World configuration must be loaded before converting coordinates.");
        }

        int width = _demoConfig.world.width;
        int height = _demoConfig.world.height;
        int clampedX = ClampCoordinate(x, width);
        int clampedY = ClampCoordinate(y, height);
        int flippedY = FlipYCoordinate(clampedY, height);
        return new GridPos(clampedX, flippedY);
    }

    private GridPos CreateWorldPositionFromDouble(double x, double y)
    {
        int xi = (int)Math.Round(x, MidpointRounding.AwayFromZero);
        int yi = (int)Math.Round(y, MidpointRounding.AwayFromZero);
        return CreateWorldPosition(xi, yi);
    }

    private static int FlipYCoordinate(int y, int height)
    {
        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "World height must be positive to convert coordinates.");
        }

        int flipped = (height - 1) - y;
        if (flipped < 0)
        {
            return 0;
        }

        if (flipped >= height)
        {
            return height - 1;
        }

        return flipped;
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
                int xValue = ConvertCoordinate(resolvedX, "service point x");
                x = ClampCoordinate(xValue, _demoConfig.world.width);
            }

            if (point.y.HasValue)
            {
                var resolvedY = TranslateServicePointCoordinate(point.y.Value, boundingBox, axis: 1);
                int yValue = ConvertCoordinate(resolvedY, "service point y");
                int clampedY = ClampCoordinate(yValue, _demoConfig.world.height);
                y = FlipYCoordinate(clampedY, _demoConfig.world.height);
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

    private RectInt? BuildWorldArea(BuildingAreaConfig areaConfig)
    {
        if (areaConfig == null)
        {
            return null;
        }

        if (_demoConfig?.world == null)
        {
            throw new InvalidOperationException("World configuration must be loaded before converting building areas.");
        }

        int width = _demoConfig.world.width;
        int height = _demoConfig.world.height;

        int minX = ClampCoordinate(areaConfig.x, width);
        int minYRaw = ClampCoordinate(areaConfig.y, height);
        int maxX = ClampCoordinate((areaConfig.x ?? 0) + Math.Max(0, (areaConfig.width ?? 0) - 1), width);
        int maxYRaw = ClampCoordinate((areaConfig.y ?? 0) + Math.Max(0, (areaConfig.height ?? 0) - 1), height);

        int minY = FlipYCoordinate(maxYRaw, height);
        int maxY = FlipYCoordinate(minYRaw, height);
        if (minY > maxY)
        {
            (minY, maxY) = (maxY, minY);
        }

        return new RectInt(minX, minY, maxX, maxY);
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
            area = BuildWorldArea(config.area);
        }
        else if (areaConfig != null)
        {
            area = BuildWorldArea(areaConfig);
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
            py = FlipYCoordinate(py, _demoConfig.world.height);
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
                _fishingSystem.RegisterSpot(id, CreateWorldPosition(x, y), _tiles.Shallow[x, y]);
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
                _foragingSystem.RegisterSpot(id, CreateWorldPosition(x, y), forest, coast);
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
            var position = CreateWorldPosition(node.x, node.y);
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
                    var pixel = pixels[y * width + x];
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
