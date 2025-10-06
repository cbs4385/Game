
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;
using DataDrivenGoap.Items;
using DataDrivenGoap.Persistence;
using DataDrivenGoap.Simulation;
using DataDrivenGoap.Social;
using GoapExecutionContext = DataDrivenGoap.Core.ExecutionContext;

namespace DataDrivenGoap.Execution
{
    public sealed class ActorHostDiagnostics
    {
        private readonly ThingId _actorId;
        private double _lastUpdateDeltaSeconds = double.NaN;
        private long _lastUpdateTimestampTicks;
        private long _updateCount;

        public ActorHostDiagnostics(ThingId actorId)
        {
            _actorId = actorId;
        }

        public ThingId ActorId => _actorId;

        public double LastUpdateDeltaSeconds => Volatile.Read(ref _lastUpdateDeltaSeconds);

        public DateTime LastUpdateUtc
        {
            get
            {
                var ticks = Volatile.Read(ref _lastUpdateTimestampTicks);
                return ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
            }
        }

        public long UpdateCount => Interlocked.Read(ref _updateCount);

        internal void RecordLoop(DateTime utcNow, double deltaSeconds, bool hasDelta)
        {
            if (utcNow.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("Timestamp must be specified in UTC.", nameof(utcNow));
            }

            Volatile.Write(ref _lastUpdateTimestampTicks, utcNow.Ticks);
            if (hasDelta)
            {
                Volatile.Write(ref _lastUpdateDeltaSeconds, deltaSeconds);
            }

            Interlocked.Increment(ref _updateCount);
        }
    }

    public sealed class ExecutorRegistry : IExecutorRegistry
    {
        private readonly IExecutor _instant = new InstantExecutor();
        public IExecutor Resolve(string activityName) => _instant;
    }

    internal sealed class InstantExecutor : IExecutor
    {
        public ExecProgress Run(PlanStep step, GoapExecutionContext ctx, out EffectBatch effects)
        {
            effects = step.BuildEffects != null
                ? step.BuildEffects(ctx.Snapshot)
                : new EffectBatch
                {
                    BaseVersion = ctx.Snapshot.Version,
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
                    QuestOps = Array.Empty<QuestOperation>()
                };
            return ExecProgress.Completed;
        }
    }

    public sealed class ActorHost
    {
        private const string NoPlanSummary = "<no-plan>";
        private readonly Thread _thread;
        private readonly IWorld _world;
        private readonly IPlanner _planner;
        private readonly IExecutorRegistry _execs;
        private readonly IReservationService _reservations;
        private readonly ThingId _self;
        private readonly Random _rng;
        private readonly PerActorLogger _log;
        private readonly WorldLogger _worldLogger;
        private readonly double _priorityJitterRange;
        private readonly double _loopIntervalMilliseconds;
        private readonly InventorySystem _inventorySystem;
        private readonly ShopSystem _shopSystem;
        private readonly SocialRelationshipSystem _socialSystem;
        private readonly CropSystem _cropSystem;
        private readonly AnimalSystem _animalSystem;
        private readonly MiningSystem _miningSystem;
        private readonly FishingSystem _fishingSystem;
        private readonly ForagingSystem _foragingSystem;
        private readonly QuestSystem _questSystem;
        private readonly SkillProgressionSystem _skillSystem;
        private readonly Dictionary<string, int> _reservationFailureCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _reservationCooldownUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _planCooldownUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly RoleScheduleService _scheduleService;
        private readonly ActorHostDiagnostics _diagnostics;
        private long _lastLoopTimestampTicks;
        private RoleScheduleBlock _activeScheduleBlock;
        private ThingId _activeScheduleTarget;
        private bool _loggedLateWarning;
        private bool? _lastKnownOpenState;
        private volatile bool _stop;
        private string _currentGoalId;
        private DateTime _goalStartUtc;
        private string _lastPlanSummary;
        private readonly object _stateGate = new object();
        private readonly object _planStatusGate = new object();
        private ActorPlanStatus _planStatus;

        public ActorHost(IWorld world, IPlanner planner, IExecutorRegistry execs, IReservationService reservations, ThingId self, int seed, string logDir, double priorityJitterRange, double loopFrequencyHz, RoleScheduleService scheduleService = null, InventorySystem inventorySystem = null, ShopSystem shopSystem = null, SocialRelationshipSystem socialSystem = null, CropSystem cropSystem = null, AnimalSystem animalSystem = null, MiningSystem miningSystem = null, FishingSystem fishingSystem = null, ForagingSystem foragingSystem = null, SkillProgressionSystem skillSystem = null, QuestSystem questSystem = null, WorldLogger worldLogger = null, bool enablePerActorLogging = true)
        {
            _world = world; _planner = planner; _execs = execs; _reservations = reservations; _self = self;
            _rng = new Random(seed ^ self.GetHashCode());
            _log = new PerActorLogger(Path.Combine(logDir, $"pawn-{_self.Value}.log.txt"), enablePerActorLogging);
            _priorityJitterRange = Math.Max(0.0, priorityJitterRange);
            if (double.IsNaN(loopFrequencyHz) || double.IsInfinity(loopFrequencyHz) || loopFrequencyHz <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(loopFrequencyHz), "Loop frequency must be a finite number greater than zero.");
            }
            double interval = 1000.0 / loopFrequencyHz;
            if (double.IsNaN(interval) || double.IsInfinity(interval) || interval <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(loopFrequencyHz), "Loop frequency must produce a positive finite interval.");
            }
            _loopIntervalMilliseconds = interval;
            _thread = new Thread(new ThreadStart(RunLoop)) { IsBackground = true, Name = $"Actor-{_self.Value}" };
            _scheduleService = scheduleService;
            _inventorySystem = inventorySystem;
            _shopSystem = shopSystem;
            _socialSystem = socialSystem;
            _cropSystem = cropSystem;
            _animalSystem = animalSystem;
            _miningSystem = miningSystem;
            _fishingSystem = fishingSystem;
            _foragingSystem = foragingSystem;
            _skillSystem = skillSystem;
            _questSystem = questSystem;
            _worldLogger = worldLogger;
            _worldLogger?.LogActorLifecycle(_self, "start");
            _diagnostics = new ActorHostDiagnostics(_self);
            _planStatus = new ActorPlanStatus(
                _self.Value ?? string.Empty,
                string.Empty,
                NoPlanSummary,
                Array.Empty<string>(),
                string.Empty,
                "initializing",
                DateTime.UtcNow);
        }

        public ThingId Id => _self;

        public ActorHostDiagnostics Diagnostics => _diagnostics;

        public void Start() { _thread.Start(); }
        public void RequestStop()
        {
            _stop = true;
        }

        public void FinishStop()
        {
            _thread.Join();
            if (!string.IsNullOrEmpty(_currentGoalId))
            {
                var now = DateTime.UtcNow;
                var durationMs = (now - _goalStartUtc).TotalMilliseconds;
                _log.RecordGoalDuration(_currentGoalId, durationMs);
                _currentGoalId = null;
            }
            _log.FlushTick();
            _log.Dispose();
            _worldLogger?.LogActorLifecycle(_self, "stop");
            UpdatePlanStatus(string.Empty, "<stopped>", Array.Empty<string>(), string.Empty, "stopped");
        }

        public void Stop()
        {
            RequestStop();
            FinishStop();
        }

        private void RunLoop()
        {
            while (!_stop)
            {
                var loopStartUtc = DateTime.UtcNow;
                bool throttleThisLoop = true;
                var previousTicks = Interlocked.Exchange(ref _lastLoopTimestampTicks, loopStartUtc.Ticks);
                if (previousTicks > 0)
                {
                    var previousUtc = new DateTime(previousTicks, DateTimeKind.Utc);
                    var delta = Math.Max(0.0, (loopStartUtc - previousUtc).TotalSeconds);
                    _diagnostics.RecordLoop(loopStartUtc, delta, true);
                }
                else
                {
                    _diagnostics.RecordLoop(loopStartUtc, double.NaN, false);
                }

                try
                {
                    var snap = _world.Snap();
                    var timeSnapshot = snap?.Time;
                    string worldTime = FormatWorldTime(timeSnapshot);
                    string worldDay = FormatWorldDay(timeSnapshot);
                    long snapshotVersion = snap.Version;
                    HandleScheduleState(snap, worldTime, worldDay);
                    var plan = _planner.Plan(snap, _self, null, _priorityJitterRange, _rng);
                    TrackGoal(plan);
                    var planSteps = BuildPlanStepDescriptions(plan);
                    if (plan == null)
                    {
                        UpdatePlanStatus(string.Empty, NoPlanSummary, Array.Empty<string>(), string.Empty, "no-plan");
                        if (!string.Equals(_lastPlanSummary, NoPlanSummary, StringComparison.Ordinal))
                        {
                            _log.Write($"PLAN none_available world_time={worldTime} world_day={worldDay} snapshot_version={snapshotVersion}");
                            _worldLogger?.LogStep("plan_none", _self, Guid.Empty, $"snapshot_version={snapshotVersion} world_time={worldTime} world_day={worldDay}");
                            _lastPlanSummary = NoPlanSummary;
                        }
                        WaitWithStopCheck(20);
                        continue;
                    }

                    string planSummary = SummarizePlan(plan);
                    if (plan.IsEmpty)
                    {
                        UpdatePlanStatus(plan.GoalId ?? string.Empty, planSummary, planSteps, string.Empty, "plan-empty");
                        if (!string.Equals(_lastPlanSummary, NoPlanSummary, StringComparison.Ordinal))
                        {
                            _log.Write($"PLAN none_available world_time={worldTime} world_day={worldDay} snapshot_version={snapshotVersion}");
                            _worldLogger?.LogStep("plan_none", _self, Guid.Empty, $"snapshot_version={snapshotVersion} world_time={worldTime} world_day={worldDay}");
                            _lastPlanSummary = NoPlanSummary;
                        }
                        WaitWithStopCheck(20);
                        continue;
                    }

                    UpdatePlanStatus(plan.GoalId ?? string.Empty, planSummary, planSteps, string.Empty, "plan-selected");
                    if (!string.Equals(planSummary, _lastPlanSummary, StringComparison.Ordinal))
                    {
                        _log.Write($"PLAN selected goal={plan.GoalId ?? "<none>"} steps={plan.Steps.Count} detail={planSummary} world_time={worldTime} world_day={worldDay} snapshot_version={snapshotVersion}");
                        _worldLogger?.LogPlanSelection(_self, plan.GoalId, planSummary, plan.Steps.Count, snapshotVersion, worldTime, worldDay);
                        _lastPlanSummary = planSummary;
                    }

                    var step = plan.NextStepWhosePreconditionsHold(snap);
                    if (step == null)
                    {
                        UpdatePlanStatus(plan.GoalId ?? string.Empty, planSummary, planSteps, string.Empty, "waiting-preconditions");
                        _log.Write($"PLAN waiting_preconditions goal={plan.GoalId ?? "<none>"} detail={planSummary} world_time={worldTime} world_day={worldDay} snapshot_version={snapshotVersion}");
                        _worldLogger?.LogStep("waiting_preconditions", _self, Guid.Empty, $"goal={plan.GoalId ?? "<none>"} detail={planSummary} snapshot_version={snapshotVersion} world_time={worldTime} world_day={worldDay}");
                        WaitWithStopCheck(15);
                        continue;
                    }

                    var planId = Guid.NewGuid();
                    var stepDescription = DescribeStep(step);
                    string stepKey = BuildStepKey(step);
                    double durSec = (step.DurationSeconds != null) ? Math.Max(0.0, step.DurationSeconds(snap)) : 0.0;
                    double expectedDurationMs = durSec * 1000.0;
                    string currentStepLabel = FormatStepForStatus(step);

                    if (TryGetCooldown(stepKey, out int waitMs))
                    {
                        UpdatePlanStatus(plan.GoalId ?? string.Empty, planSummary, planSteps, currentStepLabel, "cooldown");
                        var end = DateTime.UtcNow.AddMilliseconds(waitMs);
                        _log.Write($"STEP wait_start plan={planId} wait_kind=cooldown expected_ms={waitMs.ToString("0.##", CultureInfo.InvariantCulture)} {stepDescription} world_time={worldTime} world_day={worldDay}");
                        _worldLogger?.LogStep("wait_cooldown_start", _self, planId, $"{stepDescription} expected_ms={waitMs.ToString("0.##", CultureInfo.InvariantCulture)} world_time={worldTime} world_day={worldDay}");
                        double waited = WaitUntil(end);
                        RefreshWorldTime(ref worldTime, ref worldDay);
                        _log.AddWait(waited);
                        _log.Write($"STEP wait_complete plan={planId} wait_kind=cooldown actual_ms={waited.ToString("0.##", CultureInfo.InvariantCulture)} {stepDescription} world_time={worldTime} world_day={worldDay}");
                        _worldLogger?.LogStep("wait_cooldown_complete", _self, planId, $"{stepDescription} actual_ms={waited.ToString("0.##", CultureInfo.InvariantCulture)} world_time={worldTime} world_day={worldDay}");
                    }

                    UpdatePlanStatus(plan.GoalId ?? string.Empty, planSummary, planSteps, currentStepLabel, "executing-step");
                    _log.Write($"STEP begin plan={planId} goal={plan.GoalId ?? "<none>"} {stepDescription} reservations={DescribeReservations(step.Reservations)} duration_ms={expectedDurationMs.ToString("0.##", CultureInfo.InvariantCulture)} world_time={worldTime} world_day={worldDay} snapshot_version={snapshotVersion}");
                    _worldLogger?.LogStep("begin", _self, planId, $"goal={plan.GoalId ?? "<none>"} {stepDescription} reservations={DescribeReservations(step.Reservations)} duration_ms={expectedDurationMs.ToString("0.##", CultureInfo.InvariantCulture)} snapshot_version={snapshotVersion} world_time={worldTime} world_day={worldDay}");

                    if (!_reservations.TryAcquireAll(step.Reservations, planId, _self))
                    {
                        UpdatePlanStatus(plan.GoalId ?? string.Empty, planSummary, planSteps, currentStepLabel, "reservation-failed");
                        _log.Write($"STEP reservation_failed plan={planId} {stepDescription} world_time={worldTime} world_day={worldDay}");
                        _log.IncReservationFailure(step.ActivityName);
                        _worldLogger?.LogStep("reservation_failed", _self, planId, $"{stepDescription} world_time={worldTime} world_day={worldDay}");
                        RegisterReservationFailure(stepKey, planId, stepDescription, ref worldTime, ref worldDay);
                        RefreshWorldTime(ref worldTime, ref worldDay);
                        WaitWithStopCheck(_rng.Next(0, 3));
                        RefreshWorldTime(ref worldTime, ref worldDay);
                        continue;
                    }

                    ResetFailureTracking(stepKey);

                    try
                    {
                        RecordPathMetrics(snap, step);

                        var exec = _execs.Resolve(step.ActivityName);
                        var ctx = new GoapExecutionContext(snap, _self, _rng);
                        if (durSec > 0)
                        {
                            UpdatePlanStatus(plan.GoalId ?? string.Empty, planSummary, planSteps, currentStepLabel, "duration-wait");
                            var end = DateTime.UtcNow.AddMilliseconds(durSec * 1000.0);
                            string expected = (durSec * 1000.0).ToString("0.##", CultureInfo.InvariantCulture);
                            _log.Write($"STEP wait_start plan={planId} wait_kind=duration expected_ms={expected} {stepDescription} world_time={worldTime} world_day={worldDay}");
                            _worldLogger?.LogStep("wait_duration_start", _self, planId, $"{stepDescription} expected_ms={expected} world_time={worldTime} world_day={worldDay}");
                            double waited = WaitUntil(end);
                            RefreshWorldTime(ref worldTime, ref worldDay);
                            _log.AddWait(waited);
                            string actual = waited.ToString("0.##", CultureInfo.InvariantCulture);
                            _log.Write($"STEP wait_complete plan={planId} wait_kind=duration actual_ms={actual} {stepDescription} world_time={worldTime} world_day={worldDay}");
                            _worldLogger?.LogStep("wait_duration_complete", _self, planId, $"{stepDescription} actual_ms={actual} world_time={worldTime} world_day={worldDay}");
                            UpdatePlanStatus(plan.GoalId ?? string.Empty, planSummary, planSteps, currentStepLabel, "executing-step");
                        }

                        EffectBatch batch;
                        var prog = exec.Run(step, ctx, out batch);
                        if (prog == ExecProgress.Completed)
                        {
                            var res = _world.TryCommit(batch);
                            if (res == CommitResult.Conflict)
                            {
                                _log.Write($"STEP commit_conflict plan={planId} {stepDescription} world_time={worldTime} world_day={worldDay}");
                                _log.IncConflict(step.ActivityName);
                                _worldLogger?.LogStep("commit_conflict", _self, planId, $"{stepDescription} world_time={worldTime} world_day={worldDay}");
                            }
                            else
                            {
                                _log.Write($"STEP commit_success plan={planId} {stepDescription} world_time={worldTime} world_day={worldDay}");
                                _log.IncCommit(step.ActivityName);
                                _worldLogger?.LogStep("commit_success", _self, planId, $"{stepDescription} world_time={worldTime} world_day={worldDay}");
                                RefreshWorldTime(ref worldTime, ref worldDay);
                                var executionContext = BuildExecutionContextString(planId, plan.GoalId, step.ActivityName, snapshotVersion, worldTime, worldDay);
                                ApplyPostCommitEffects(batch, executionContext);
                                _log.LogEffectSummary(planId, step.ActivityName, batch, snapshotVersion, worldTime, worldDay);
                                _worldLogger?.LogEffectSummary(_self, planId, step.ActivityName, batch, snapshotVersion, worldTime, worldDay);
                                RegisterPlanCooldowns(step, planId, stepDescription, durSec, batch.PlanCooldowns, worldTime, worldDay);
                            }
                        }
                        else
                        {
                            RefreshWorldTime(ref worldTime, ref worldDay);
                            _log.Write($"STEP execution_result plan={planId} {stepDescription} status={prog} world_time={worldTime} world_day={worldDay}");
                            _worldLogger?.LogStep("execution_result", _self, planId, $"{stepDescription} status={prog} world_time={worldTime} world_day={worldDay}");
                        }
                    }
                    finally
                    {
                        _reservations.ReleaseAll(step.Reservations, planId, _self);
                        RefreshWorldTime(ref worldTime, ref worldDay);
                        _log.Write($"STEP end plan={planId} {stepDescription} world_time={worldTime} world_day={worldDay}");
                        _worldLogger?.LogStep("end", _self, planId, $"{stepDescription} world_time={worldTime} world_day={worldDay}");
                    }

                    WaitWithStopCheck(5);
                }
                catch (Exception ex)
                {
                    string errorWorldTime = "<unknown>", errorWorldDay = "<unknown>";
                    RefreshWorldTime(ref errorWorldTime, ref errorWorldDay);
                    UpdatePlanStatus(string.Empty, "<error>", Array.Empty<string>(), string.Empty, "error");
                    string actorId = _self.Value ?? "<unknown>";
                    string exceptionType = ex.GetType().Name ?? "Exception";
                    string fatalMessage =
                        $"Actor '{actorId}' encountered fatal {exceptionType} at world_time={errorWorldTime} world_day={errorWorldDay}.";
                    _log.Write($"ERROR actor_loop {exceptionType}:{ex.Message} world_time={errorWorldTime} world_day={errorWorldDay}");
                    _worldLogger?.LogInfo($"actor_error actor={actorId} type={exceptionType} message={ex.Message} world_time={errorWorldTime} world_day={errorWorldDay}");
                    throttleThisLoop = false;
                    throw new InvalidOperationException(fatalMessage, ex);
                }
                finally
                {
                    _log.FlushTick();
                    if (throttleThisLoop)
                    {
                        EnforceLoopInterval(loopStartUtc);
                    }
                }
            }
        }

        private void HandleScheduleState(IWorldSnapshot snap, string worldTime, string worldDay)
        {
            if (_scheduleService == null)
                return;

            if (!_scheduleService.TryEvaluate(snap, _self, out var evaluation) || evaluation.ActiveBlock == null)
            {
                if (_activeScheduleBlock != null)
                {
                    _log.Write($"SCHEDULE end role={_activeScheduleBlock.RoleId} task={_activeScheduleBlock.Task} target={_activeScheduleTarget.Value} world_time={worldTime} world_day={worldDay}");
                    _activeScheduleBlock = null;
                    _activeScheduleTarget = default;
                    _lastKnownOpenState = null;
                    _loggedLateWarning = false;
                }
                return;
            }

            var block = evaluation.ActiveBlock;
            var target = evaluation.TargetId;

            bool changed = _activeScheduleBlock != block || !_activeScheduleTarget.Equals(target);
            if (changed)
            {
                if (_activeScheduleBlock != null && !_activeScheduleBlock.Equals(block))
                {
                    _log.Write($"SCHEDULE end role={_activeScheduleBlock.RoleId} task={_activeScheduleBlock.Task} target={_activeScheduleTarget.Value} world_time={worldTime} world_day={worldDay}");
                }
                _activeScheduleBlock = block;
                _activeScheduleTarget = target;
                _lastKnownOpenState = null;
                _loggedLateWarning = false;
                string eventSuffix = string.IsNullOrEmpty(evaluation.ActiveEventId) ? string.Empty : $" event={evaluation.ActiveEventId}";
                _log.Write($"SCHEDULE start role={block.RoleId} task={evaluation.EffectiveTask} goto={evaluation.EffectiveGotoTag} target={target.Value}{eventSuffix} world_time={worldTime} world_day={worldDay}");
            }

            if (string.IsNullOrEmpty(target.Value))
                return;

            var targetThing = snap.GetThing(target);
            if (targetThing != null)
            {
                bool isOpen = targetThing.AttrOrDefault("open", 1.0) > 0.5;
                if (!_lastKnownOpenState.HasValue || _lastKnownOpenState.Value != isOpen)
                {
                    _lastKnownOpenState = isOpen;
                    _log.Write(isOpen ? $"OPEN building={target.Value} world_time={worldTime} world_day={worldDay}" : $"CLOSE building={target.Value} world_time={worldTime} world_day={worldDay}");
                }

                var actorThing = snap.GetThing(_self);
                if (actorThing != null)
                {
                    int distance = DataDrivenGoap.Core.GridPos.Manhattan(actorThing.Position, targetThing.Position);
                    if (!_loggedLateWarning && distance > 2 && evaluation.MinutesIntoBlock > 10.0)
                    {
                        _log.Write($"SCHEDULE late role={block.RoleId} task={evaluation.EffectiveTask} target={target.Value} minutes={evaluation.MinutesIntoBlock.ToString("0.0", CultureInfo.InvariantCulture)} world_time={worldTime} world_day={worldDay}");
                        _loggedLateWarning = true;
                    }
                }
            }
        }

        private void TrackGoal(Plan plan)
        {
            var now = DateTime.UtcNow;
            string newGoal = plan?.GoalId;
            bool hasSteps = plan != null && !plan.IsEmpty;

            if (string.IsNullOrEmpty(newGoal) || !hasSteps)
            {
                if (!string.IsNullOrEmpty(_currentGoalId))
                {
                    var durationMs = (now - _goalStartUtc).TotalMilliseconds;
                    _log.RecordGoalDuration(_currentGoalId, durationMs);
                    _log.IncGoalSatisfied(_currentGoalId);
                    _currentGoalId = null;
                }
                return;
            }

            if (!string.Equals(_currentGoalId, newGoal, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(_currentGoalId))
                {
                    var durationMs = (now - _goalStartUtc).TotalMilliseconds;
                    _log.RecordGoalDuration(_currentGoalId, durationMs);
                }
                _currentGoalId = newGoal;
                _goalStartUtc = now;
                _log.IncGoalSwitch(newGoal);
                _log.IncGoalSelected(newGoal);
            }
        }

        private static string BuildStepKey(PlanStep step)
        {
            if (step == null)
            {
                return string.Empty;
            }

            string activity = step.ActivityName ?? string.Empty;
            string target = step.Target.Value ?? string.Empty;
            return string.Concat(activity, "|", target);
        }

        private static string BuildCooldownKey(string activity, ThingId scope)
        {
            string act = activity ?? string.Empty;
            string target = scope.Value ?? string.Empty;
            return string.Concat(act, "|", target);
        }

        private bool TryGetCooldown(string stepKey, out int waitMs)
        {
            waitMs = 0;
            if (string.IsNullOrEmpty(stepKey))
            {
                return false;
            }

            lock (_stateGate)
            {
                bool found = false;
                waitMs = Math.Max(waitMs, GetRemainingCooldown(_reservationCooldownUntil, stepKey, ref found));
                waitMs = Math.Max(waitMs, GetRemainingCooldown(_planCooldownUntil, stepKey, ref found));
                return found;
            }
        }

        private static int GetRemainingCooldown(Dictionary<string, DateTime> map, string stepKey, ref bool found)
        {
            if (map == null)
            {
                return 0;
            }

            if (map.TryGetValue(stepKey, out var until))
            {
                if (until <= DateTime.UtcNow)
                {
                    map.Remove(stepKey);
                }
                else
                {
                    found = true;
                    return Math.Max(1, (int)(until - DateTime.UtcNow).TotalMilliseconds);
                }
            }

            return 0;
        }

        private void ResetFailureTracking(string stepKey)
        {
            if (string.IsNullOrEmpty(stepKey))
            {
                return;
            }

            lock (_stateGate)
            {
                _reservationFailureCounts.Remove(stepKey);
                if (_reservationCooldownUntil.TryGetValue(stepKey, out var until) && until <= DateTime.UtcNow)
                {
                    _reservationCooldownUntil.Remove(stepKey);
                }
            }
        }

        private void RegisterReservationFailure(string stepKey, Guid planId, string stepDescription, ref string worldTime, ref string worldDay)
        {
            WaitWithStopCheck(_rng.Next(5, 25));
            RefreshWorldTime(ref worldTime, ref worldDay);

            if (string.IsNullOrEmpty(stepKey))
            {
                return;
            }

            bool shouldBackoff = false;
            int cooldownMs = 0;
            lock (_stateGate)
            {
                if (!_reservationFailureCounts.TryGetValue(stepKey, out var count))
                {
                    count = 0;
                }

                count++;
                _reservationFailureCounts[stepKey] = count;

                if (count >= 3)
                {
                    cooldownMs = _rng.Next(40, 120);
                    _reservationCooldownUntil[stepKey] = DateTime.UtcNow.AddMilliseconds(cooldownMs);
                    _reservationFailureCounts[stepKey] = 0;
                    shouldBackoff = true;
                }
            }

            if (shouldBackoff)
            {
                _log.Write($"STEP reservation_backoff plan={planId} {stepDescription} cooldown_ms={cooldownMs} world_time={worldTime} world_day={worldDay}");
                _worldLogger?.LogStep("reservation_backoff", _self, planId, $"{stepDescription} cooldown_ms={cooldownMs} world_time={worldTime} world_day={worldDay}");
                WaitWithStopCheck(cooldownMs);
                RefreshWorldTime(ref worldTime, ref worldDay);
                _lastPlanSummary = null;
            }
        }

        private double WaitUntil(DateTime endUtc, int sliceMilliseconds = 5)
        {
            double waited = 0.0;
            while (!_stop)
            {
                var remaining = endUtc - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                int remainingMs = (int)Math.Ceiling(remaining.TotalMilliseconds);
                int slice = Math.Min(remainingMs, sliceMilliseconds);
                if (slice <= 0)
                {
                    slice = remainingMs;
                }

                Thread.Sleep(slice);
                waited += slice;
            }

            return waited;
        }

        private double WaitWithStopCheck(int milliseconds, int sliceMilliseconds = 5)
        {
            if (milliseconds <= 0 || _stop)
            {
                return 0.0;
            }

            double waited = 0.0;
            int remaining = milliseconds;
            while (!_stop && remaining > 0)
            {
                int slice = Math.Min(remaining, sliceMilliseconds);
                if (slice <= 0)
                {
                    slice = remaining;
                }

                Thread.Sleep(slice);
                waited += slice;
                remaining -= slice;
            }

            return waited;
        }

        private void EnforceLoopInterval(DateTime loopStartUtc)
        {
            if (_loopIntervalMilliseconds <= 0.0)
            {
                return;
            }

            double elapsedMs = (DateTime.UtcNow - loopStartUtc).TotalMilliseconds;
            if (double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs))
            {
                return;
            }

            double remaining = _loopIntervalMilliseconds - elapsedMs;
            if (remaining <= 0.0)
            {
                return;
            }

            if (remaining > int.MaxValue)
            {
                remaining = int.MaxValue;
            }

            int waitMs = (int)Math.Ceiling(remaining);
            if (waitMs <= 0)
            {
                return;
            }

            WaitWithStopCheck(waitMs);
        }

        private void RegisterPlanCooldowns(PlanStep step, Guid planId, string stepDescription, double durationSeconds, PlanCooldownRequest[] requests, string worldTime, string worldDay)
        {
            if (step == null)
            {
                return;
            }

            if (requests == null || requests.Length == 0)
            {
                return;
            }

            string activity = step.ActivityName ?? string.Empty;
            foreach (var req in requests)
            {
                var scope = req.Scope;
                if (string.IsNullOrEmpty(scope.Value))
                {
                    scope = step.Target;
                }

                string key = BuildCooldownKey(activity, scope);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                double seconds = Math.Max(0.0, req.Seconds);
                if (req.UseStepDuration)
                {
                    seconds = Math.Max(seconds, durationSeconds);
                }

                if (seconds <= 0.0)
                {
                    continue;
                }

                lock (_stateGate)
                {
                    _planCooldownUntil[key] = DateTime.UtcNow.AddSeconds(seconds);
                }
                string secondsStr = seconds.ToString("0.###", CultureInfo.InvariantCulture);
                _log.Write($"STEP plan_cooldown plan={planId} {stepDescription} scope={scope.Value ?? "<none>"} seconds={secondsStr} world_time={worldTime} world_day={worldDay}");
                _worldLogger?.LogStep("plan_cooldown", _self, planId, $"{stepDescription} scope={scope.Value ?? "<none>"} seconds={secondsStr} world_time={worldTime} world_day={worldDay}");
            }
        }

        public ActorHostState CaptureState()
        {
            var state = new ActorHostState
            {
                actorId = _self.Value
            };
            lock (_stateGate)
            {
                foreach (var kv in _planCooldownUntil)
                {
                    double remaining = Math.Max(0.0, (kv.Value - DateTime.UtcNow).TotalSeconds);
                    state.planCooldownSecondsRemaining[kv.Key] = remaining;
                }

                foreach (var kv in _reservationCooldownUntil)
                {
                    double remaining = Math.Max(0.0, (kv.Value - DateTime.UtcNow).TotalSeconds);
                    state.reservationCooldownSecondsRemaining[kv.Key] = remaining;
                }
            }
            state.rng = RandomStateSerializer.Capture(_rng);
            return state;
        }

        public void ApplyState(ActorHostState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.actorId))
                return;
            if (!string.Equals(state.actorId.Trim(), _self.Value, StringComparison.OrdinalIgnoreCase))
                return;

            lock (_stateGate)
            {
                _planCooldownUntil.Clear();
                if (state.planCooldownSecondsRemaining != null)
                {
                    foreach (var kv in state.planCooldownSecondsRemaining)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key))
                            continue;
                        var until = DateTime.UtcNow.AddSeconds(Math.Max(0.0, kv.Value));
                        _planCooldownUntil[kv.Key] = until;
                    }
                }

                _reservationCooldownUntil.Clear();
                if (state.reservationCooldownSecondsRemaining != null)
                {
                    foreach (var kv in state.reservationCooldownSecondsRemaining)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key))
                            continue;
                        var until = DateTime.UtcNow.AddSeconds(Math.Max(0.0, kv.Value));
                        _reservationCooldownUntil[kv.Key] = until;
                    }
                }

                _reservationFailureCounts.Clear();
            }

            RandomStateSerializer.Apply(_rng, state.rng);
        }

        private void ApplyPostCommitEffects(in EffectBatch batch, string executionContext)
        {
            ProcessInventoryChanges(batch.InventoryOps, null, executionContext);

            if (_shopSystem != null && batch.ShopTransactions != null)
            {
                foreach (var txn in batch.ShopTransactions)
                {
                    if (!_shopSystem.TryProcessTransaction(txn, out var result) || result.Quantity <= 0)
                        continue;

                    double total = result.TotalPrice;
                    double actorDelta = txn.Kind == ShopTransactionKind.Sale ? total : -total;
                    double shopDelta = -actorDelta;

                    if (_inventorySystem != null)
                    {
                        double newActorBalance = _inventorySystem.AdjustCurrency(txn.Actor, actorDelta);
                        if (txn.Actor.Equals(_self) && Math.Abs(actorDelta) > 1e-6)
                            _log.LogCurrencyChange(txn.Actor, actorDelta, newActorBalance, "shop_txn", executionContext);
                        if (Math.Abs(actorDelta) > 1e-6)
                            _worldLogger?.LogCurrencyChange(txn.Actor, actorDelta, newActorBalance, "shop_txn", executionContext);
                        double newShopBalance = _inventorySystem.AdjustCurrency(txn.Shop, shopDelta);
                        if (txn.Shop.Equals(_self) && Math.Abs(shopDelta) > 1e-6)
                            _log.LogCurrencyChange(txn.Shop, shopDelta, newShopBalance, "shop_txn", executionContext);
                        if (Math.Abs(shopDelta) > 1e-6)
                            _worldLogger?.LogCurrencyChange(txn.Shop, shopDelta, newShopBalance, "shop_txn", executionContext);
                    }

                    if (txn.Actor.Equals(_self))
                        _log.LogShopTransaction(txn.Shop, txn.Actor, txn.ItemId, result.Quantity, total, txn.Kind, executionContext);
                    _worldLogger?.LogShopTransaction(txn.Shop, txn.Actor, txn.ItemId, result.Quantity, total, txn.Kind, executionContext);
                }
            }

            if (_inventorySystem != null && batch.CurrencyOps != null)
            {
                foreach (var delta in batch.CurrencyOps)
                {
                    if (Math.Abs(delta.Amount) < 1e-6)
                        continue;
                    double balance = _inventorySystem.AdjustCurrency(delta.Owner, delta.Amount);
                    if (delta.Owner.Equals(_self))
                        _log.LogCurrencyChange(delta.Owner, delta.Amount, balance, "effect", executionContext);
                    _worldLogger?.LogCurrencyChange(delta.Owner, delta.Amount, balance, "effect", executionContext);
                }
            }

            if (_socialSystem != null && batch.RelationshipOps != null)
            {
                foreach (var rel in batch.RelationshipOps)
                {
                    double delta = rel.ExplicitDelta ?? LookupGiftDelta(rel);
                    if (Math.Abs(delta) < 1e-6)
                        continue;
                    double newValue = _socialSystem.AdjustRelationship(rel.From, rel.To, rel.RelationshipId, delta);
                    if (rel.From.Equals(_self))
                        _log.Write($"RELATIONSHIP adjust target={rel.To.Value} rel={rel.RelationshipId} delta={delta.ToString("0.###", CultureInfo.InvariantCulture)} new={newValue.ToString("0.###", CultureInfo.InvariantCulture)} {executionContext}");
                }
            }

            if (_cropSystem != null && batch.CropOps != null)
            {
                foreach (var op in batch.CropOps)
                {
                    var result = _cropSystem.Apply(op);
                    if (!result.Success)
                    {
                        if (op.Actor.Equals(_self))
                            _log.Write($"CROP failed kind={op.Kind} plot={op.Plot.Value} {executionContext}");
                        continue;
                    }

                    ProcessInventoryChanges(result.InventoryChanges, "crop", executionContext);

                    if (op.Actor.Equals(_self) && result.HarvestYields != null)
                    {
                        foreach (var yield in result.HarvestYields)
                        {
                            if (yield.Quantity <= 0 || string.IsNullOrWhiteSpace(yield.ItemId))
                                continue;
                            _log.Write($"CROP harvest item={yield.ItemId} qty={yield.Quantity} {executionContext}");
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
                        if (op.Actor.Equals(_self))
                            _log.Write($"ANIMAL failed kind={op.Kind} target={op.Animal.Value} {executionContext}");
                        continue;
                    }

                    ProcessInventoryChanges(result.InventoryChanges, "animal", executionContext);

                    if (op.Actor.Equals(_self) && result.ProduceYields != null)
                    {
                        foreach (var yield in result.ProduceYields)
                        {
                            if (yield.Quantity <= 0 || string.IsNullOrWhiteSpace(yield.ItemId))
                                continue;
                            _log.Write($"ANIMAL collect item={yield.ItemId} qty={yield.Quantity} {executionContext}");
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
                        if (op.Actor.Equals(_self))
                            _log.Write($"MINE failed node={op.Node.Value} {executionContext}");
                        continue;
                    }

                    ProcessInventoryChanges(result.InventoryChanges, "mining", executionContext);
                    GrantSkillExperience(op.Actor, result.SkillId, result.SkillXp, "mining", executionContext);

                    if (op.Actor.Equals(_self) && !string.IsNullOrWhiteSpace(result.ItemId) && result.Quantity > 0)
                    {
                        _log.Write($"MINE extract item={result.ItemId} qty={result.Quantity} node={op.Node.Value} {executionContext}");
                        _worldLogger?.LogCustom("mine_extract", _self, $"node={op.Node.Value} item={result.ItemId} qty={result.Quantity}", executionContext);
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
                        if (op.Actor.Equals(_self))
                            _log.Write($"FISH failed spot={op.Spot.Value} {executionContext}");
                        continue;
                    }

                    ProcessInventoryChanges(result.InventoryChanges, "fishing", executionContext);
                    GrantSkillExperience(op.Actor, result.SkillId, result.SkillXp, "fishing", executionContext);

                    if (op.Actor.Equals(_self) && !string.IsNullOrWhiteSpace(result.ItemId) && result.Quantity > 0)
                    {
                        _log.Write($"FISH catch item={result.ItemId} qty={result.Quantity} spot={op.Spot.Value} {executionContext}");
                        _worldLogger?.LogCustom("fish_catch", _self, $"spot={op.Spot.Value} item={result.ItemId} qty={result.Quantity}", executionContext);
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
                        if (op.Actor.Equals(_self))
                            _log.Write($"FORAGE failed spot={op.Spot.Value} {executionContext}");
                        continue;
                    }

                    ProcessInventoryChanges(result.InventoryChanges, "forage", executionContext);
                    GrantSkillExperience(op.Actor, result.SkillId, result.SkillXp, "foraging", executionContext);

                    if (op.Actor.Equals(_self) && !string.IsNullOrWhiteSpace(result.ItemId) && result.Quantity > 0)
                    {
                        _log.Write($"FORAGE gather item={result.ItemId} qty={result.Quantity} spot={op.Spot.Value} {executionContext}");
                        _worldLogger?.LogCustom("forage_collect", _self, $"spot={op.Spot.Value} item={result.ItemId} qty={result.Quantity}", executionContext);
                    }
                }
            }

            if (_questSystem != null && batch.QuestOps != null)
            {
                foreach (var op in batch.QuestOps)
                {
                    var result = _questSystem.Apply(op);
                    if (!result.Success)
                        continue;

                    ProcessInventoryChanges(result.InventoryChanges, "quest", executionContext);
                    ProcessCurrencyChanges(result.CurrencyChanges, "quest", executionContext);

                    if (!string.IsNullOrWhiteSpace(result.Message))
                    {
                        if (op.Actor.Equals(_self))
                        {
                            _log.Write($"QUEST {result.Message} quest={op.QuestId} status={result.Status} objective={result.ObjectiveId} progress={result.ObjectiveProgress}/{Math.Max(1, result.ObjectiveRequired)} {executionContext}");
                        }
                        _worldLogger?.LogQuestEvent(op.Actor, op.QuestId, result.Status.ToString(), result.ObjectiveId, result.ObjectiveProgress, result.ObjectiveRequired, result.Message, executionContext);
                    }
                }
            }
        }

        private void ProcessInventoryChanges(IEnumerable<InventoryDelta> operations, string sourceTag, string executionContext)
        {
            if (_inventorySystem == null || operations == null)
                return;

            foreach (var op in operations)
            {
                if (string.IsNullOrWhiteSpace(op.ItemId) || op.Quantity <= 0)
                    continue;

                int processed = op.Remove
                    ? _inventorySystem.RemoveItem(op.Owner, op.ItemId, op.Quantity)
                    : _inventorySystem.AddItem(op.Owner, op.ItemId, op.Quantity);

                if (processed <= 0)
                    continue;

                if (op.Owner.Equals(_self))
                {
                    string direction = op.Remove ? "remove" : "add";
                    string mode = string.IsNullOrEmpty(sourceTag) ? direction : $"{sourceTag}_{direction}";
                    string ownerId = FormatThingId(op.Owner);
                    string source = string.IsNullOrEmpty(sourceTag) ? "<none>" : sourceTag;
                    _log.Write($"INVENTORY change owner={ownerId} item={op.ItemId} qty={(op.Remove ? -processed : processed)} mode={mode} source={source} {executionContext}");
                }

                int signedQty = op.Remove ? -processed : processed;
                _worldLogger?.LogInventoryChange(op.Owner, op.ItemId, signedQty, sourceTag, executionContext);
            }
        }

        private void ProcessCurrencyChanges(IEnumerable<CurrencyDelta> operations, string sourceTag, string executionContext)
        {
            if (_inventorySystem == null || operations == null)
                return;

            string source = string.IsNullOrWhiteSpace(sourceTag) ? "effect" : sourceTag;
            foreach (var delta in operations)
            {
                if (Math.Abs(delta.Amount) < 1e-6)
                    continue;
                double balance = _inventorySystem.AdjustCurrency(delta.Owner, delta.Amount);
                if (delta.Owner.Equals(_self))
                    _log.LogCurrencyChange(delta.Owner, delta.Amount, balance, source, executionContext);
                _worldLogger?.LogCurrencyChange(delta.Owner, delta.Amount, balance, source, executionContext);
            }
        }

        private void GrantSkillExperience(ThingId actor, string skillId, double amount, string sourceTag, string executionContext)
        {
            if (_skillSystem == null)
                return;
            if (string.IsNullOrWhiteSpace(actor.Value))
                return;
            if (string.IsNullOrWhiteSpace(skillId))
                return;
            if (!double.IsFinite(amount) || amount <= 0.0)
                return;

            _skillSystem.AddExperience(actor, skillId, amount);

            if (actor.Equals(_self))
            {
                string source = string.IsNullOrWhiteSpace(sourceTag) ? "effect" : sourceTag;
                _log.Write($"SKILL gain actor={FormatThingId(actor)} skill={skillId} xp={amount.ToString("0.###", CultureInfo.InvariantCulture)} source={source} {executionContext}");
            }

            string tag = string.IsNullOrWhiteSpace(sourceTag) ? "effect" : sourceTag;
            _worldLogger?.LogCustom("skill_xp", actor, $"skill={skillId} amount={amount.ToString("0.###", CultureInfo.InvariantCulture)} source={tag}", executionContext);
        }

        private double LookupGiftDelta(RelationshipDelta rel)
        {
            if (_inventorySystem == null || string.IsNullOrWhiteSpace(rel.ItemId))
                return 0.0;
            if (!_inventorySystem.TryGetItemDefinition(rel.ItemId, out var item) || item == null)
                return 0.0;
            foreach (var affinity in item.GiftAffinities)
            {
                if (string.IsNullOrWhiteSpace(affinity))
                    continue;
                var parts = affinity.Split(':');
                if (parts.Length != 2)
                    continue;
                if (!string.Equals(parts[0], rel.RelationshipId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    return value;
            }
            return 0.0;
        }

        private void RecordPathMetrics(IWorldSnapshot snap, PlanStep step)
        {
            if (snap == null || step == null) return;
            var actor = snap.GetThing(step.Actor);
            var target = snap.GetThing(step.Target);
            if (actor == null || target == null) return;

            int currentDist = DataDrivenGoap.Core.GridPos.Manhattan(actor.Position, target.Position);
            bool hasPath = snap.TryFindNextStep(actor.Position, target.Position, out var next);
            bool blocked = !hasPath;
            bool detour = false;
            if (hasPath)
            {
                int nextDist = DataDrivenGoap.Core.GridPos.Manhattan(next, target.Position);
                detour = nextDist >= currentDist;
            }

            _log.RecordPathSample(currentDist, blocked, detour);
        }

        private static string SummarizePlan(Plan plan)
        {
            if (plan == null)
            {
                return "<null>";
            }

            var steps = plan.Steps;
            if (steps == null || steps.Count == 0)
            {
                return $"{plan.GoalId ?? "<no-goal>"}|<empty>";
            }

            var parts = new string[steps.Count];
            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                string target = FormatThingId(step.Target);
                parts[i] = string.IsNullOrEmpty(target) ? step.ActivityName : $"{step.ActivityName}->{target}";
            }

            return $"{plan.GoalId ?? "<no-goal>"}|{string.Join("|", parts)}";
        }

        public ActorPlanStatus SnapshotPlanStatus()
        {
            lock (_planStatusGate)
            {
                return _planStatus;
            }
        }

        private void UpdatePlanStatus(string goalId, string planSummary, IReadOnlyList<string> steps, string currentStep, string state)
        {
            var snapshot = new ActorPlanStatus(
                _self.Value ?? string.Empty,
                goalId ?? string.Empty,
                planSummary ?? string.Empty,
                steps,
                currentStep ?? string.Empty,
                state ?? string.Empty,
                DateTime.UtcNow);
            lock (_planStatusGate)
            {
                _planStatus = snapshot;
            }
        }

        private static string[] BuildPlanStepDescriptions(Plan plan)
        {
            if (plan?.Steps == null || plan.Steps.Count == 0)
            {
                return Array.Empty<string>();
            }

            var result = new string[plan.Steps.Count];
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                result[i] = FormatStepForStatus(plan.Steps[i]);
            }

            return result;
        }

        private static string FormatStepForStatus(PlanStep step)
        {
            if (step == null)
            {
                return "<none>";
            }

            string activity = step.ActivityName ?? "<unknown>";
            string target = FormatThingId(step.Target);
            if (string.IsNullOrWhiteSpace(target) || string.Equals(target, "<none>", StringComparison.Ordinal))
            {
                return activity;
            }

            return string.Concat(activity, " -> ", target);
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

        private static string BuildExecutionContextString(Guid planId, string goalId, string activity, long snapshotVersion, string worldTime, string worldDay)
        {
            string planText = planId == Guid.Empty ? "<none>" : planId.ToString();
            string goalText = string.IsNullOrWhiteSpace(goalId) ? "<none>" : goalId;
            string activityText = string.IsNullOrWhiteSpace(activity) ? "<unknown>" : activity;
            string wt = string.IsNullOrWhiteSpace(worldTime) ? "<unknown>" : worldTime;
            string wd = string.IsNullOrWhiteSpace(worldDay) ? "<unknown>" : worldDay;
            return $"plan={planText} goal={goalText} activity={activityText} snapshot_version={snapshotVersion} world_time={wt} world_day={wd}";
        }

        private void RefreshWorldTime(ref string worldTime, ref string worldDay)
        {
            try
            {
                var snapshot = _world?.Snap();
                var time = snapshot?.Time;
                worldTime = FormatWorldTime(time);
                worldDay = FormatWorldDay(time);
            }
            catch
            {
                worldTime = "<unknown>";
                worldDay = "<unknown>";
            }
        }

        private static string DescribeStep(PlanStep step)
        {
            if (step == null)
            {
                return "activity=<none>";
            }

            string actor = FormatThingId(step.Actor);
            string target = FormatThingId(step.Target);
            return $"activity={step.ActivityName ?? "<unknown>"} actor={actor} target={target}";
        }

        private static string DescribeReservations(IReadOnlyList<Reservation> reservations)
        {
            if (reservations == null || reservations.Count == 0)
            {
                return "none";
            }

            var parts = new string[reservations.Count];
            for (int i = 0; i < reservations.Count; i++)
            {
                var r = reservations[i];
                string thing = FormatThingId(r.Thing);
                parts[i] = $"{thing}:{r.Mode}:{r.Priority}";
            }

            return string.Join(",", parts);
        }

        private static string FormatThingId(ThingId thing)
        {
            var value = thing.Value;
            return string.IsNullOrEmpty(value) ? "<none>" : value;
        }
    }

    public sealed class ActorPlanStatus
    {
        private readonly string[] _steps;

        public ActorPlanStatus(string actorId, string goalId, string planSummary, IReadOnlyList<string> steps, string currentStep, string state, DateTime updatedUtc)
        {
            ActorId = actorId ?? string.Empty;
            GoalId = goalId ?? string.Empty;
            PlanSummary = planSummary ?? string.Empty;
            CurrentStep = currentStep ?? string.Empty;
            State = state ?? string.Empty;
            UpdatedUtc = updatedUtc;
            if (steps == null || steps.Count == 0)
            {
                _steps = Array.Empty<string>();
            }
            else
            {
                _steps = new string[steps.Count];
                for (int i = 0; i < steps.Count; i++)
                {
                    _steps[i] = steps[i] ?? string.Empty;
                }
            }
        }

        public string ActorId { get; }
        public string GoalId { get; }
        public string PlanSummary { get; }
        public string CurrentStep { get; }
        public string State { get; }
        public DateTime UpdatedUtc { get; }
        public IReadOnlyList<string> Steps => _steps;
    }

    public sealed class PerActorLogger : IDisposable
    {
        private readonly object _gate = new object();
        private const long MaxLogBytes = 75L * 1024L * 1024L;
        private readonly RollingLogWriter _writer;
        private readonly bool _enabled;
        private int _commitCount, _conflictCount;
        private readonly System.Collections.Generic.Dictionary<string,int> _activityCommits = new System.Collections.Generic.Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.Dictionary<string,int> _tickActivityCommits = new System.Collections.Generic.Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.Dictionary<string,int> _reservationFailures = new System.Collections.Generic.Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.Dictionary<string,int> _tickReservationFailures = new System.Collections.Generic.Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        private double _totalWaitMs;
        private int _goalSwitchCount;
        private readonly System.Collections.Generic.Dictionary<string,int> _goalSwitches = new System.Collections.Generic.Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.Dictionary<string,int> _goalSelections = new System.Collections.Generic.Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.Dictionary<string,int> _tickGoalSelections = new System.Collections.Generic.Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.Dictionary<string,int> _goalSatisfactions = new System.Collections.Generic.Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.Dictionary<string,int> _tickGoalSatisfactions = new System.Collections.Generic.Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.Dictionary<string,System.Collections.Generic.List<double>> _goalDurations = new System.Collections.Generic.Dictionary<string,System.Collections.Generic.List<double>>(StringComparer.OrdinalIgnoreCase);
        private double _pathDistanceSum;
        private int _pathSampleCount;
        private int _pathBlockedCount;
        private int _pathDetourCount;

        public PerActorLogger(string filePath, bool enabled = true)
        {
            _enabled = enabled;
            if (!_enabled)
            {
                _writer = null;
                return;
            }

            _writer = new RollingLogWriter(filePath, MaxLogBytes);
        }

        public void Write(string line)
        {
            if (!_enabled)
            {
                return;
            }

            lock (_gate)
            {
                _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|{line}");
            }
        }
        public void IncCommit(string activity)
        {
            if (!_enabled)
            {
                return;
            }
            lock (_gate)
            {
                _commitCount++;
                if (!string.IsNullOrEmpty(activity))
                {
                    Increment(_activityCommits, activity);
                    Increment(_tickActivityCommits, activity);
                }
            }
        }
        public void IncConflict(string activity)
        {
            if (!_enabled)
            {
                return;
            }

            lock (_gate)
            {
                _conflictCount++;
            }
        }
        public void AddWait(double waitedMs)
        {
            if (!_enabled)
            {
                return;
            }

            lock (_gate)
            {
                _totalWaitMs += waitedMs;
            }
        }
        public void IncReservationFailure(string activity)
        {
            if (!_enabled)
            {
                return;
            }
            lock (_gate)
            {
                if (string.IsNullOrEmpty(activity)) activity = "<unknown>";
                Increment(_reservationFailures, activity);
                Increment(_tickReservationFailures, activity);
            }
        }
        public void IncGoalSwitch(string goalId)
        {
            if (!_enabled)
            {
                return;
            }
            lock (_gate)
            {
                _goalSwitchCount++;
                if (!string.IsNullOrEmpty(goalId))
                {
                    int value;
                    _goalSwitches.TryGetValue(goalId, out value);
                    _goalSwitches[goalId] = value + 1;
                }
            }
        }
        public void IncGoalSelected(string goalId)
        {
            if (!_enabled)
            {
                return;
            }
            lock (_gate)
            {
                if (string.IsNullOrEmpty(goalId)) goalId = "<none>";
                Increment(_goalSelections, goalId);
                Increment(_tickGoalSelections, goalId);
            }
        }
        public void IncGoalSatisfied(string goalId)
        {
            if (!_enabled)
            {
                return;
            }
            if (string.IsNullOrEmpty(goalId)) return;
            lock (_gate)
            {
                Increment(_goalSatisfactions, goalId);
                Increment(_tickGoalSatisfactions, goalId);
            }
        }
        public void RecordGoalDuration(string goalId, double durationMs)
        {
            if (!_enabled)
            {
                return;
            }
            if (string.IsNullOrEmpty(goalId) || durationMs <= 0) return;
            lock (_gate)
            {
                if (!_goalDurations.TryGetValue(goalId, out var list))
                {
                    list = new System.Collections.Generic.List<double>();
                    _goalDurations[goalId] = list;
                }
                list.Add(durationMs);
            }
        }
        public void RecordPathSample(double distance, bool blocked, bool detour)
        {
            if (!_enabled)
            {
                return;
            }
            lock (_gate)
            {
                _pathSampleCount++;
                if (distance > 0) _pathDistanceSum += distance;
                if (blocked) _pathBlockedCount++;
                if (detour) _pathDetourCount++;
            }
        }

        public void FlushTick()
        {
            if (!_enabled)
            {
                return;
            }
            lock (_gate)
            {
                if (_tickActivityCommits.Count == 0 && _tickReservationFailures.Count == 0 && _tickGoalSelections.Count == 0 && _tickGoalSatisfactions.Count == 0)
                {
                    return;
                }

                var parts = new System.Collections.Generic.List<string>();
                if (_tickActivityCommits.Count > 0) parts.Add("commits=" + FormatMap(_tickActivityCommits));
                if (_tickReservationFailures.Count > 0) parts.Add("reservation_failed=" + FormatMap(_tickReservationFailures));
                if (_tickGoalSelections.Count > 0) parts.Add("goal_selected=" + FormatMap(_tickGoalSelections));
                if (_tickGoalSatisfactions.Count > 0) parts.Add("goal_satisfied=" + FormatMap(_tickGoalSatisfactions));

                _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|TICK {string.Join(" ", parts)}");

                _tickActivityCommits.Clear();
                _tickReservationFailures.Clear();
                _tickGoalSelections.Clear();
                _tickGoalSatisfactions.Clear();
            }
        }

        public void LogCurrencyChange(ThingId actor, double delta, double newBalance, string source, string executionContext)
        {
            if (!_enabled)
            {
                return;
            }
            lock (_gate)
            {
                string id = actor.Value ?? "<unknown>";
                string ctx = string.IsNullOrWhiteSpace(executionContext) ? string.Empty : " " + executionContext;
                _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|CURRENCY change actor={id} delta={delta.ToString("0.###", CultureInfo.InvariantCulture)} balance={newBalance.ToString("0.###", CultureInfo.InvariantCulture)} source={source ?? "<none>"}{ctx}");
            }
        }

        public void LogShopTransaction(ThingId shop, ThingId actor, string itemId, int quantity, double totalPrice, ShopTransactionKind kind, string executionContext)
        {
            if (!_enabled)
            {
                return;
            }
            lock (_gate)
            {
                string shopId = shop.Value ?? "<unknown>";
                string actorId = actor.Value ?? "<unknown>";
                string mode = kind == ShopTransactionKind.Sale ? "sell" : "buy";
                string ctx = string.IsNullOrWhiteSpace(executionContext) ? string.Empty : " " + executionContext;
                _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|SHOP transaction mode={mode} actor={actorId} shop={shopId} item={itemId ?? "<unknown>"} quantity={quantity} total={totalPrice.ToString("0.###", CultureInfo.InvariantCulture)}{ctx}");
            }
        }

        public void LogEffectSummary(Guid planId, string activity, in EffectBatch batch, long snapshotVersion, string worldTime, string worldDay)
        {
            if (!_enabled)
            {
                return;
            }
            string act = string.IsNullOrWhiteSpace(activity) ? "<unknown>" : activity;
            string wt = string.IsNullOrWhiteSpace(worldTime) ? "<unknown>" : worldTime;
            string wd = string.IsNullOrWhiteSpace(worldDay) ? "<unknown>" : worldDay;
            lock (_gate)
            {
                string summary =
                    $"plan={planId} activity={act} snapshot_version={snapshotVersion} base_version={batch.BaseVersion} world_time={wt} world_day={wd} " +
                    $"reads={Count(batch.Reads)} writes={Count(batch.Writes)} facts={Count(batch.FactDeltas)} spawns={Count(batch.Spawns)} despawns={Count(batch.Despawns)} " +
                    $"plan_cooldowns={Count(batch.PlanCooldowns)} inventory_ops={Count(batch.InventoryOps)} currency_ops={Count(batch.CurrencyOps)} shop_txns={Count(batch.ShopTransactions)} " +
                    $"relationship_ops={Count(batch.RelationshipOps)} crop_ops={Count(batch.CropOps)} animal_ops={Count(batch.AnimalOps)} mining_ops={Count(batch.MiningOps)} fishing_ops={Count(batch.FishingOps)} foraging_ops={Count(batch.ForagingOps)} quest_ops={Count(batch.QuestOps)}";
                _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|EFFECT summary {summary}");

                foreach (var read in batch.Reads ?? Array.Empty<ReadSetEntry>())
                {
                    string detail =
                        $"plan={planId} activity={act} kind=read thing={FormatThingIdForLog(read.Thing)} attr={read.ExpectAttribute ?? "<none>"} expect={FormatNullableDouble(read.ExpectValue)} world_time={wt} world_day={wd}";
                    _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|EFFECT detail {detail}");
                }

                foreach (var write in batch.Writes ?? Array.Empty<WriteSetEntry>())
                {
                    string detail =
                        $"plan={planId} activity={act} kind=write thing={FormatThingIdForLog(write.Thing)} attr={write.Attribute ?? "<none>"} value={FormatDouble(write.Value)} world_time={wt} world_day={wd}";
                    _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|EFFECT detail {detail}");
                }

                foreach (var fact in batch.FactDeltas ?? Array.Empty<FactDelta>())
                {
                    string change = fact.Add ? "add" : "remove";
                    string detail =
                        $"plan={planId} activity={act} kind=fact predicate={fact.Pred ?? "<none>"} subject={FormatThingIdForLog(fact.A)} object={FormatThingIdForLog(fact.B)} change={change} world_time={wt} world_day={wd}";
                    _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|EFFECT detail {detail}");
                }

                foreach (var spawn in batch.Spawns ?? Array.Empty<ThingSpawnRequest>())
                {
                    string detail =
                        $"plan={planId} activity={act} kind=spawn thing={FormatThingIdForLog(spawn.Id)} type={spawn.Type ?? "<none>"} pos={spawn.Position} tags={FormatTags(spawn.Tags)} attrs={FormatAttributes(spawn.Attributes)} world_time={wt} world_day={wd}";
                    _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|EFFECT detail {detail}");
                }

                foreach (var despawn in batch.Despawns ?? Array.Empty<ThingId>())
                {
                    string detail =
                        $"plan={planId} activity={act} kind=despawn thing={FormatThingIdForLog(despawn)} world_time={wt} world_day={wd}";
                    _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|EFFECT detail {detail}");
                }

                foreach (var cooldown in batch.PlanCooldowns ?? Array.Empty<PlanCooldownRequest>())
                {
                    string detail =
                        $"plan={planId} activity={act} kind=plan_cooldown scope={FormatThingIdForLog(cooldown.Scope)} seconds={FormatDouble(cooldown.Seconds)} use_duration={cooldown.UseStepDuration} world_time={wt} world_day={wd}";
                    _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|EFFECT detail {detail}");
                }

                foreach (var op in batch.InventoryOps ?? Array.Empty<InventoryDelta>())
                {
                    string mode = op.Remove ? "remove" : "add";
                    string detail =
                        $"plan={planId} activity={act} kind=inventory owner={FormatThingIdForLog(op.Owner)} item={op.ItemId ?? "<unknown>"} quantity={op.Quantity} mode={mode} world_time={wt} world_day={wd}";
                    _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|EFFECT detail {detail}");
                }

                foreach (var delta in batch.CurrencyOps ?? Array.Empty<CurrencyDelta>())
                {
                    string detail =
                        $"plan={planId} activity={act} kind=currency owner={FormatThingIdForLog(delta.Owner)} amount={FormatDouble(delta.Amount)} world_time={wt} world_day={wd}";
                    _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|EFFECT detail {detail}");
                }

                foreach (var txn in batch.ShopTransactions ?? Array.Empty<ShopTransaction>())
                {
                    string mode = txn.Kind == ShopTransactionKind.Sale ? "sell" : "buy";
                    string detail =
                        $"plan={planId} activity={act} kind=shop mode={mode} shop={FormatThingIdForLog(txn.Shop)} actor={FormatThingIdForLog(txn.Actor)} item={txn.ItemId ?? "<unknown>"} quantity={txn.Quantity} world_time={wt} world_day={wd}";
                    _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|EFFECT detail {detail}");
                }

                foreach (var rel in batch.RelationshipOps ?? Array.Empty<RelationshipDelta>())
                {
                    string detail =
                        $"plan={planId} activity={act} kind=relationship from={FormatThingIdForLog(rel.From)} to={FormatThingIdForLog(rel.To)} relationship={rel.RelationshipId ?? "<none>"} item={rel.ItemId ?? "<none>"} delta={FormatNullableDouble(rel.ExplicitDelta)} world_time={wt} world_day={wd}";
                    _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|EFFECT detail {detail}");
                }

                foreach (var crop in batch.CropOps ?? Array.Empty<CropOperation>())
                {
                    string detail =
                        $"plan={planId} activity={act} kind=crop op={crop.Kind} plot={FormatThingIdForLog(crop.Plot)} actor={FormatThingIdForLog(crop.Actor)} crop_id={crop.CropId ?? "<none>"} seed={crop.SeedItemId ?? "<none>"} quantity={crop.SeedQuantity} world_time={wt} world_day={wd}";
                    _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|EFFECT detail {detail}");
                }

                foreach (var animal in batch.AnimalOps ?? Array.Empty<AnimalOperation>())
                {
                    string detail =
                        $"plan={planId} activity={act} kind=animal op={animal.Kind} animal={FormatThingIdForLog(animal.Animal)} actor={FormatThingIdForLog(animal.Actor)} item={animal.ItemId ?? "<none>"} quantity={animal.Quantity} world_time={wt} world_day={wd}";
                    _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|EFFECT detail {detail}");
                }

                foreach (var mining in batch.MiningOps ?? Array.Empty<MiningOperation>())
                {
                    string detail =
                        $"plan={planId} activity={act} kind=mining op={mining.Kind} node={FormatThingIdForLog(mining.Node)} actor={FormatThingIdForLog(mining.Actor)} tool_item={mining.ToolItemId ?? "<none>"} tool_tier={mining.ToolTier} world_time={wt} world_day={wd}";
                    _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|EFFECT detail {detail}");
                }

                foreach (var fishing in batch.FishingOps ?? Array.Empty<FishingOperation>())
                {
                    string detail =
                        $"plan={planId} activity={act} kind=fishing op={fishing.Kind} spot={FormatThingIdForLog(fishing.Spot)} actor={FormatThingIdForLog(fishing.Actor)} bait_item={fishing.BaitItemId ?? "<none>"} bait_quantity={fishing.BaitQuantity} world_time={wt} world_day={wd}";
                    _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|EFFECT detail {detail}");
                }

                foreach (var foraging in batch.ForagingOps ?? Array.Empty<ForagingOperation>())
                {
                    string detail =
                        $"plan={planId} activity={act} kind=foraging op={foraging.Kind} spot={FormatThingIdForLog(foraging.Spot)} actor={FormatThingIdForLog(foraging.Actor)} world_time={wt} world_day={wd}";
                    _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|EFFECT detail {detail}");
                }

                foreach (var quest in batch.QuestOps ?? Array.Empty<QuestOperation>())
                {
                    string detail =
                        $"plan={planId} activity={act} kind=quest op={quest.Kind} actor={FormatThingIdForLog(quest.Actor)} quest_id={quest.QuestId ?? "<none>"} objective_id={quest.ObjectiveId ?? "<none>"} amount={quest.Amount} grant_rewards={(quest.GrantRewards ? "true" : "false")} world_time={wt} world_day={wd}";
                    _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|EFFECT detail {detail}");
                }
            }
        }

        public void Dispose()
        {
            if (!_enabled)
            {
                return;
            }
            lock (_gate)
            {
                var parts = new System.Collections.Generic.List<string>();
                foreach (var kv in _activityCommits) parts.Add(kv.Key + ":" + kv.Value);
                var perAct = string.Join(",", parts);
                var perResFail = FormatMap(_reservationFailures);
                var perGoalSelect = FormatMap(_goalSelections);
                var perGoalSatisfied = FormatMap(_goalSatisfactions);
                _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|SUMMARY commits={_commitCount} conflicts={_conflictCount} wait_ms={_totalWaitMs:0} goal_switches={_goalSwitchCount} per_activity={perAct} reservation_failures={perResFail} goal_selected={perGoalSelect} goal_satisfied={perGoalSatisfied}");
                var po = new
                {
                    commits = _commitCount,
                    conflicts = _conflictCount,
                    wait_ms = _totalWaitMs,
                    per_activity = _activityCommits,
                    reservation_failures = _reservationFailures,
                    goal_switches = _goalSwitchCount,
                    goal_switch_map = _goalSwitches,
                    goal_selected = _goalSelections,
                    goal_satisfied = _goalSatisfactions,
                    goal_durations = _goalDurations,
                    path = new { samples = _pathSampleCount, total_distance = _pathDistanceSum, blocked = _pathBlockedCount, detours = _pathDetourCount }
                };
                var json = System.Text.Json.JsonSerializer.Serialize(po, new System.Text.Json.JsonSerializerOptions{WriteIndented=false});
                _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|SUMMARYJSON {json}");
                _writer.Dispose();
            }
        }

        private static void Increment(System.Collections.Generic.Dictionary<string,int> map, string key)
        {
            if (map.TryGetValue(key, out var existing))
            {
                map[key] = existing + 1;
            }
            else
            {
                map[key] = 1;
            }
        }

        private static string FormatMap(System.Collections.Generic.Dictionary<string,int> map)
        {
            if (map == null || map.Count == 0) return "<none>";
            var list = new System.Collections.Generic.List<string>(map.Count);
            foreach (var kv in map)
            {
                list.Add(kv.Key + ":" + kv.Value);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(",", list);
        }

        private static int Count<T>(T[] items)
        {
            return items?.Length ?? 0;
        }

        private static string FormatThingIdForLog(ThingId thing)
        {
            return string.IsNullOrEmpty(thing.Value) ? "<none>" : thing.Value;
        }

        private static string FormatDouble(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string FormatNullableDouble(double? value)
        {
            return value.HasValue ? FormatDouble(value.Value) : "<none>";
        }

        private static string FormatTags(string[] tags)
        {
            if (tags == null || tags.Length == 0)
            {
                return "<none>";
            }

            return string.Join(",", tags);
        }

        private static string FormatAttributes(ThingAttributeValue[] attributes)
        {
            if (attributes == null || attributes.Length == 0)
            {
                return "<none>";
            }

            var parts = new string[attributes.Length];
            for (int i = 0; i < attributes.Length; i++)
            {
                var attr = attributes[i];
                parts[i] = $"{attr.Name ?? "<none>"}:{FormatDouble(attr.Value)}";
            }

            return string.Join(",", parts);
        }
    }
}
