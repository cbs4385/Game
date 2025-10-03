using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;
using DataDrivenGoap.World;

namespace DataDrivenGoap.Simulation
{
    public sealed class NeedScheduler : IDisposable
    {
        private sealed class NeedDefinition
        {
            public NeedConfig Config { get; }
            public string Attribute { get; }
            public double ChangePerTrigger { get; }
            public double ChangePerSecond { get; }
            public double IntervalSeconds { get; }
            public bool Clamp01 { get; }
            public double? MinValue { get; }
            public double? MaxValue { get; }
            public double? DefaultValue { get; }
            public string[] TargetTags { get; }

            public NeedDefinition(NeedConfig config, double secondsPerDay)
            {
                Config = config ?? throw new ArgumentNullException(nameof(config));

                if (string.IsNullOrWhiteSpace(config.attribute))
                    throw new ArgumentException("Need configuration must specify an attribute", nameof(config));
                Attribute = config.attribute.Trim();

                if (double.IsNaN(config.changePerTrigger) || double.IsInfinity(config.changePerTrigger))
                    throw new ArgumentException("Need changePerTrigger must be a finite number", nameof(config));
                ChangePerTrigger = config.changePerTrigger;

                Clamp01 = config.clamp01;
                MinValue = config.minValue;
                MaxValue = config.maxValue;
                DefaultValue = config.defaultValue;

                if (!Clamp01)
                {
                    if (!MinValue.HasValue || !MaxValue.HasValue)
                        throw new ArgumentException("Non-clamped needs must define minValue and maxValue", nameof(config));
                    if (double.IsNaN(MinValue.Value) || double.IsInfinity(MinValue.Value))
                        throw new ArgumentException("Need minValue must be a finite number", nameof(config));
                    if (double.IsNaN(MaxValue.Value) || double.IsInfinity(MaxValue.Value))
                        throw new ArgumentException("Need maxValue must be a finite number", nameof(config));
                    if (MinValue.Value > MaxValue.Value)
                        throw new ArgumentException("Need minValue must be less than or equal to maxValue", nameof(config));
                    if (DefaultValue.HasValue && (DefaultValue.Value < MinValue.Value || DefaultValue.Value > MaxValue.Value))
                        throw new ArgumentException("Need defaultValue must be within the configured range", nameof(config));
                }
                else if (DefaultValue.HasValue && (DefaultValue.Value < 0.0 || DefaultValue.Value > 1.0))
                {
                    throw new ArgumentException("Clamped needs must have defaultValue within [0,1]", nameof(config));
                }

                if (config.targetTags == null)
                    throw new ArgumentException("Need configuration must include targetTags (may be empty)", nameof(config));

                TargetTags = config.targetTags
                    .Select(tag => tag ?? throw new ArgumentException("Need targetTags cannot contain null entries", nameof(config)))
                    .Select(tag => tag.Trim())
                    .ToArray();

                if (TargetTags.Any(string.IsNullOrWhiteSpace))
                    throw new ArgumentException("Need targetTags cannot contain blank entries", nameof(config));

                if (double.IsNaN(config.triggersPerDay) || double.IsInfinity(config.triggersPerDay) || config.triggersPerDay <= 0)
                    throw new ArgumentException("Need triggersPerDay must be a positive finite number", nameof(config));

                if (double.IsNaN(secondsPerDay) || double.IsInfinity(secondsPerDay) || secondsPerDay <= 0)
                    throw new ArgumentOutOfRangeException(nameof(secondsPerDay));

                IntervalSeconds = secondsPerDay / config.triggersPerDay;
                if (!double.IsFinite(IntervalSeconds) || IntervalSeconds <= 0)
                {
                    throw new ArgumentException("Need triggersPerDay produced an invalid interval", nameof(config));
                }

                ChangePerSecond = ChangePerTrigger / IntervalSeconds;
                if (!double.IsFinite(ChangePerSecond))
                {
                    throw new ArgumentException("Need changePerTrigger produced an invalid change rate", nameof(config));
                }
            }
        }

        private readonly IWorld _world;
        private readonly WorldClock _clock;
        private readonly List<NeedDefinition> _needs;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Thread _thread;

        public bool HasNeeds => _needs.Count > 0;

        public NeedScheduler(IWorld world, WorldClock clock, NeedSystemConfig config)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (config == null) throw new ArgumentNullException(nameof(config));

            _needs = new List<NeedDefinition>();
            if (!config.enabled)
            {
                return;
            }

            double secondsPerDay = _clock.SecondsPerDay;
            if (config.needs == null)
                throw new ArgumentException("Need system configuration must include a needs collection", nameof(config));

            foreach (var need in config.needs)
            {
                if (need == null)
                    throw new ArgumentException("Need entries cannot be null", nameof(config));

                var def = new NeedDefinition(need, secondsPerDay);
                _needs.Add(def);
            }

            if (_needs.Count == 0)
                throw new ArgumentException("Need system is enabled but no needs were configured", nameof(config));
        }

        public void Start()
        {
            if (!HasNeeds) return;
            if (_thread != null) return;
            _thread = new Thread(RunLoop) { IsBackground = true, Name = "NeedScheduler" };
            _thread.Start();
        }

        public void Stop()
        {
            if (_thread == null) return;
            _cts.Cancel();
            try
            {
                _thread.Join();
            }
            catch (ThreadStateException)
            {
            }
            _thread = null;
        }

        private void RunLoop()
        {
            var token = _cts.Token;
            var initialTime = _clock.Snapshot();
            double nowSeconds = initialTime.TotalWorldSeconds;
            var lastUpdate = new Dictionary<NeedDefinition, double>();
            var pendingDeltas = new Dictionary<NeedDefinition, double>();
            foreach (var need in _needs)
            {
                lastUpdate[need] = nowSeconds;
                pendingDeltas[need] = 0.0;
            }

            while (!token.IsCancellationRequested)
            {
                var time = _clock.Snapshot();
                nowSeconds = time.TotalWorldSeconds;

                foreach (var need in _needs)
                {
                    double elapsed = nowSeconds - lastUpdate[need];
                    if (elapsed <= 1e-6)
                        continue;

                    lastUpdate[need] = nowSeconds;

                    if (Math.Abs(need.ChangePerSecond) <= double.Epsilon)
                        continue;

                    double delta = need.ChangePerSecond * elapsed;
                    if (double.IsNaN(delta) || double.IsInfinity(delta) || Math.Abs(delta) < 1e-9)
                        continue;

                    pendingDeltas[need] += delta;
                    double totalDelta = pendingDeltas[need];
                    if (Math.Abs(totalDelta) < 1e-6)
                        continue;

                    var result = ApplyNeed(need, totalDelta);

                    if (result == CommitResult.Committed)
                    {
                        pendingDeltas[need] = 0.0;
                    }
                    else
                    {
                        Console.WriteLine(
                            $"[NeedScheduler] commit conflict for need '{need.Config?.id ?? need.Attribute}' attribute '{need.Attribute}' " +
                            $"delta={totalDelta:0.###}; will retry next tick.");
                    }
                }

                if (token.WaitHandle.WaitOne(100))
                    break;
            }
        }

        private CommitResult ApplyNeed(NeedDefinition need, double delta)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var snap = _world.Snap();
                var writes = new List<WriteSetEntry>();
                var reads = new List<ReadSetEntry>();

                foreach (var thing in snap.AllThings())
                {
                    if (!MatchesTarget(thing, need))
                        continue;

                    double currentValue = 0.0;
                    bool hasAttr = thing.Attributes != null && thing.Attributes.TryGetValue(need.Attribute, out currentValue);
                    if (!hasAttr)
                    {
                        if (!need.DefaultValue.HasValue)
                        {
                            var message =
                                $"Thing '{thing.Id}' is missing attribute '{need.Attribute}' and no default was provided.";
                            throw new InvalidOperationException(message);
                        }

                        currentValue = need.DefaultValue.Value;
                    }

                    double nextValue = currentValue + delta;
                    if (need.Clamp01)
                    {
                        nextValue = Math.Clamp(nextValue, 0.0, 1.0);
                    }
                    else
                    {
                        if (need.MinValue.HasValue)
                            nextValue = Math.Max(nextValue, need.MinValue.Value);
                        if (need.MaxValue.HasValue)
                            nextValue = Math.Min(nextValue, need.MaxValue.Value);
                    }

                    if (Math.Abs(nextValue - currentValue) < 1e-9)
                        continue;

                    reads.Add(new ReadSetEntry(thing.Id, need.Attribute, currentValue));
                    writes.Add(new WriteSetEntry(thing.Id, need.Attribute, nextValue));
                }

                if (writes.Count == 0)
                    return CommitResult.Committed;

                var batch = new EffectBatch
                {
                    BaseVersion = snap.Version,
                    Reads = reads.ToArray(),
                    Writes = writes.ToArray(),
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
                    ForagingOps = Array.Empty<ForagingOperation>()
                };

                var result = _world.TryCommit(batch);
                if (result == CommitResult.Committed)
                    return CommitResult.Committed;

                if (attempt < 2)
                    Thread.Sleep(1);
            }

            return CommitResult.Conflict;
        }

        private static bool MatchesTarget(ThingView thing, NeedDefinition need)
        {
            if (thing == null)
                return false;

            if (need.TargetTags.Length == 0)
                return true;

            foreach (var tag in need.TargetTags)
            {
                if (thing.Tags != null && thing.Tags.Contains(tag))
                    return true;
            }

            return false;
        }

        public void Dispose()
        {
            Stop();
            _cts.Dispose();
        }
    }
}
