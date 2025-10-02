
using System;
using System.Collections.Generic;
using DataDrivenGoap.Items;

namespace DataDrivenGoap.Core
{
    public struct ThingId : IEquatable<ThingId>
    {
        public string Value { get; }
        public ThingId(string value) { Value = value ?? throw new ArgumentNullException(nameof(value)); }
        public override string ToString() => Value;
        public bool Equals(ThingId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is ThingId o && Equals(o);
        public override int GetHashCode() => Value != null ? Value.GetHashCode() : 0;
        public static implicit operator ThingId(string s) => new ThingId(s);
    }

    public struct GridPos : IEquatable<GridPos>
    {
        public int X { get; }
        public int Y { get; }
        public GridPos(int x, int y) { X = x; Y = y; }
        public override string ToString() => $"({X},{Y})";
        public bool Equals(GridPos other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is GridPos o && Equals(o);
        public override int GetHashCode() => (X * 397) ^ Y;
        public static int Manhattan(GridPos a, GridPos b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
    }

    public sealed class ThingView
    {
        public ThingId Id { get; }
        public string Type { get; }
        public IReadOnlyCollection<string> Tags { get; }
        public GridPos Position { get; }
        public IReadOnlyDictionary<string, double> Attributes { get; }
        public BuildingInfo Building { get; }
        public ThingView(ThingId id, string type, IReadOnlyCollection<string> tags, GridPos pos, IReadOnlyDictionary<string,double> attrs, BuildingInfo building = null)
        { Id = id; Type = type; Tags = tags; Position = pos; Attributes = attrs; Building = building; }
        public double AttrOrDefault(string key, double defValue) => (Attributes != null && Attributes.TryGetValue(key, out var v)) ? v : defValue;
    }

    public struct Fact : IEquatable<Fact>
    {
        public string Pred; public ThingId A; public ThingId B;
        public Fact(string pred, ThingId a, ThingId b) { Pred = pred; A = a; B = b; }
        public override string ToString() => $"{Pred}({A},{B})";
        public bool Equals(Fact other) => Pred == other.Pred && A.Equals(other.A) && B.Equals(other.B);
        public override bool Equals(object obj) => obj is Fact f && Equals(f);
        public override int GetHashCode() => HashCode.Combine(Pred, A, B);
    }

    public interface IWorldSnapshot
    {
        long Version { get; }
        ThingView GetThing(ThingId id);
        IEnumerable<ThingView> AllThings();
        IEnumerable<ThingView> QueryByTag(string tag);
        bool HasFact(string pred, ThingId a, ThingId b);
        int Distance(ThingId a, ThingId b);
        WorldTimeSnapshot Time { get; }

        // Tilemap/Pathfinding
        int Width { get; }
        int Height { get; }
        bool IsWalkable(int x, int y);
        bool IsWalkable(GridPos p);
        bool TryFindNextStep(GridPos from, GridPos to, out GridPos next);
    }

    public enum CommitResult { Committed, Conflict }

    public interface IWorld
    {
        IWorldSnapshot Snap();
        CommitResult TryCommit(in Effects.EffectBatch batch);
    }

    public enum ReservationMode { Hard, Soft }

    public struct Reservation
    {
        public ThingId Thing; public ReservationMode Mode; public int Priority;
        public Reservation(ThingId t, ReservationMode m, int p) { Thing = t; Mode = m; Priority = p; }
    }

    public interface IReservationService
    {
        bool TryAcquireAll(IReadOnlyList<Reservation> reservations, Guid planId, ThingId actorId);
        void ReleaseAll(IReadOnlyList<Reservation> reservations, Guid planId, ThingId actorId);
    }

    public interface IReservationQuery
    {
        bool HasActiveReservation(ThingId thing, ThingId requester);
    }

    public interface IInventoryQuery
    {
        bool Has(ThingId owner, string predicate);
        int Count(ThingId owner, string predicate);
        IReadOnlyList<Items.InventoryStackView> Snapshot(ThingId owner);
        double GetCurrency(ThingId owner);
    }

    public interface ICraftingQuery
    {
        bool TryGetRecipe(string recipeId, out RecipeDefinition recipe);
        bool HasIngredients(ThingId owner, RecipeDefinition recipe, int count);
        double GetCraftDuration(string recipeId);
    }

    public readonly struct CropTileStateSnapshot
    {
        public bool Exists { get; }
        public bool Tilled { get; }
        public string CropId { get; }
        public int Stage { get; }
        public bool Watered { get; }
        public int DaysInStage { get; }
        public ThingId PlantedBy { get; }
        public int RegrowCounter { get; }
        public bool ReadyToHarvest { get; }

        public CropTileStateSnapshot(bool exists, bool tilled, string cropId, int stage, bool watered, int daysInStage, ThingId plantedBy, int regrowCounter, bool readyToHarvest)
        {
            Exists = exists;
            Tilled = tilled;
            CropId = cropId ?? string.Empty;
            Stage = stage;
            Watered = watered;
            DaysInStage = daysInStage;
            PlantedBy = plantedBy;
            RegrowCounter = regrowCounter;
            ReadyToHarvest = readyToHarvest;
        }
    }

    public interface ICropQuery
    {
        bool TryGet(ThingId plot, out CropTileStateSnapshot state);
        int CountReadyCrops();
    }

    public readonly struct AnimalStateSnapshot
    {
        public bool Exists { get; }
        public string Species { get; }
        public double Hunger { get; }
        public double Happiness { get; }
        public bool HasProduce { get; }
        public double ProduceReadyInHours { get; }
        public double HoursSinceFed { get; }
        public double HoursSinceBrushed { get; }
        public bool NeedsBrush { get; }

        public AnimalStateSnapshot(
            bool exists,
            string species,
            double hunger,
            double happiness,
            bool hasProduce,
            double produceReadyInHours,
            double hoursSinceFed,
            double hoursSinceBrushed,
            bool needsBrush)
        {
            Exists = exists;
            Species = species ?? string.Empty;
            Hunger = hunger;
            Happiness = happiness;
            HasProduce = hasProduce;
            ProduceReadyInHours = produceReadyInHours;
            HoursSinceFed = hoursSinceFed;
            HoursSinceBrushed = hoursSinceBrushed;
            NeedsBrush = needsBrush;
        }
    }

    public readonly struct AnimalSummarySnapshot
    {
        public int TotalAnimals { get; }
        public int HungryCount { get; }
        public int ProduceReadyCount { get; }
        public int NeedsBrushCount { get; }

        public AnimalSummarySnapshot(int totalAnimals, int hungryCount, int produceReadyCount, int needsBrushCount)
        {
            TotalAnimals = totalAnimals;
            HungryCount = hungryCount;
            ProduceReadyCount = produceReadyCount;
            NeedsBrushCount = needsBrushCount;
        }
    }

    public interface IAnimalQuery
    {
        bool TryGet(ThingId animal, out AnimalStateSnapshot state);
        AnimalSummarySnapshot SnapshotSummary();
    }

    public readonly struct FishingSpotSnapshot
    {
        public bool Exists { get; }
        public bool IsShallow { get; }
        public bool HasCatch { get; }
        public string CatchId { get; }
        public string ItemId { get; }

        public FishingSpotSnapshot(bool exists, bool isShallow, bool hasCatch, string catchId, string itemId)
        {
            Exists = exists;
            IsShallow = isShallow;
            HasCatch = hasCatch;
            CatchId = catchId ?? string.Empty;
            ItemId = itemId ?? string.Empty;
        }
    }

    public interface IFishingQuery
    {
        bool TryGet(ThingId spot, out FishingSpotSnapshot state);
        int CountAvailableSpots();
    }

    public readonly struct ForageSpotSnapshot
    {
        public bool Exists { get; }
        public bool IsForest { get; }
        public bool IsCoastal { get; }
        public bool HasResource { get; }
        public string ResourceId { get; }
        public string ItemId { get; }

        public ForageSpotSnapshot(bool exists, bool isForest, bool isCoastal, bool hasResource, string resourceId, string itemId)
        {
            Exists = exists;
            IsForest = isForest;
            IsCoastal = isCoastal;
            HasResource = hasResource;
            ResourceId = resourceId ?? string.Empty;
            ItemId = itemId ?? string.Empty;
        }
    }

    public interface IForagingQuery
    {
        bool TryGet(ThingId spot, out ForageSpotSnapshot state);
        int CountAvailableSpots();
    }

    public readonly struct MiningNodeSnapshot
    {
        public bool Exists { get; }
        public string LayerId { get; }
        public bool HasOre { get; }
        public string OreId { get; }
        public string ItemId { get; }
        public string RequiredToolId { get; }
        public int RequiredToolTier { get; }

        public MiningNodeSnapshot(bool exists, string layerId, bool hasOre, string oreId, string itemId, string requiredToolId, int requiredToolTier)
        {
            Exists = exists;
            LayerId = layerId ?? string.Empty;
            HasOre = hasOre;
            OreId = oreId ?? string.Empty;
            ItemId = itemId ?? string.Empty;
            RequiredToolId = requiredToolId ?? string.Empty;
            RequiredToolTier = Math.Max(0, requiredToolTier);
        }
    }

    public interface IMiningQuery
    {
        bool TryGet(ThingId node, out MiningNodeSnapshot state);
        int CountAvailableNodes();
    }

    public enum QuestStatus
    {
        Locked,
        Available,
        Active,
        ReadyToTurnIn,
        Completed
    }

    public readonly struct QuestObjectiveProgress
    {
        public bool Exists { get; }
        public string QuestId { get; }
        public string ObjectiveId { get; }
        public int Progress { get; }
        public int Required { get; }
        public bool Completed { get; }
        public bool IsCurrent { get; }

        public QuestObjectiveProgress(bool exists, string questId, string objectiveId, int progress, int required, bool completed, bool isCurrent)
        {
            Exists = exists;
            QuestId = questId ?? string.Empty;
            ObjectiveId = objectiveId ?? string.Empty;
            Progress = Math.Max(0, progress);
            Required = Math.Max(0, required);
            Completed = completed;
            IsCurrent = isCurrent;
        }
    }

    public interface IQuestQuery
    {
        QuestStatus GetStatus(ThingId actor, string questId);
        QuestObjectiveProgress GetObjectiveProgress(ThingId actor, string questId, string objectiveId);
        bool IsQuestAvailable(ThingId actor, string questId);
        bool IsQuestActive(ThingId actor, string questId);
        bool IsQuestReadyToTurnIn(ThingId actor, string questId);
        bool IsQuestCompleted(ThingId actor, string questId);
        bool IsObjectiveActive(ThingId actor, string questId, string objectiveId);
    }

    public interface ISkillProgression
    {
        double GetSkillLevel(ThingId actor, string skillId);
        double GetSkillExperience(ThingId actor, string skillId);
        void AddExperience(ThingId actor, string skillId, double amount);
    }

    public readonly struct WeatherSnapshot
    {
        public string Id { get; }
        public string DisplayName { get; }
        public bool AutoWaterCrops { get; }
        public bool GrowthPaused { get; }
        public bool CancelOutdoorShifts { get; }
        public double PathCostMultiplier { get; }
        public double GustStrength { get; }

        public WeatherSnapshot(
            string id,
            string displayName,
            bool autoWaterCrops,
            bool growthPaused,
            bool cancelOutdoorShifts,
            double pathCostMultiplier,
            double gustStrength)
        {
            Id = id ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            AutoWaterCrops = autoWaterCrops;
            GrowthPaused = growthPaused;
            CancelOutdoorShifts = cancelOutdoorShifts;
            PathCostMultiplier = double.IsFinite(pathCostMultiplier) ? pathCostMultiplier : 1.0;
            GustStrength = double.IsFinite(gustStrength) ? gustStrength : 0.0;
        }
    }

    public interface IWeatherQuery
    {
        WeatherSnapshot CurrentWeather { get; }
        bool IsWeather(string weatherId);
    }

    public enum ExecProgress { Completed, NeedsMoreTime, Failed, Interrupted }

    public sealed class Goal { public string Id { get; } public Goal(string id) { Id = id; } }

    public sealed class PlanStep
    {
        public string ActivityName { get; }
        public ThingId Actor;
        public ThingId Target;
        public IReadOnlyList<Reservation> Reservations;
        public Func<IWorldSnapshot, bool> Preconditions;
        public Func<IWorldSnapshot, Effects.EffectBatch> BuildEffects;
        public Func<IWorldSnapshot, double> DurationSeconds;
        public PlanStep(string activityName) { ActivityName = activityName; }
    }

    public sealed class Plan
    {
        private readonly List<PlanStep> _steps = new List<PlanStep>();
        public IReadOnlyList<PlanStep> Steps => _steps;
        public string GoalId { get; internal set; }
        public void Add(PlanStep s) { _steps.Add(s); }
        public bool IsEmpty => _steps.Count == 0;
        public PlanStep NextStepWhosePreconditionsHold(IWorldSnapshot snap)
        {
            for (int i = 0; i < _steps.Count; i++)
            {
                var s = _steps[i];
                if (s.Preconditions == null || s.Preconditions(snap))
                { _steps.RemoveAt(i); return s; }
            }
            return null;
        }
    }

    public sealed class ExecutionContext
    {
        public IWorldSnapshot Snapshot { get; }
        public ThingId Self { get; }
        public Random Rng { get; }
        public ExecutionContext(IWorldSnapshot snap, ThingId self, Random rng) { Snapshot = snap; Self = self; Rng = rng; }
    }

    public interface IExecutor
    {
        ExecProgress Run(PlanStep step, ExecutionContext ctx, out Effects.EffectBatch effects);
    }
    public interface IExecutorRegistry { IExecutor Resolve(string activityName); }

    public interface IPlanner
    {
        Plan Plan(IWorldSnapshot snap, ThingId self, Goal goal, double priorityJitter = 0.0, Random rng = null);
    }
}
