using System;
using System.Collections.Generic;
using DataDrivenGoap.Core;

namespace DataDrivenGoap.Persistence
{
    public sealed class SnapshotManifest
    {
        public int version { get; set; }
        public DateTime savedAtUtc { get; set; }
        public long tick { get; set; }
        public Dictionary<string, string> chunks { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class ClockState
    {
        public double totalWorldSeconds { get; set; }
        public double totalWorldDays { get; set; }
        public double timeScale { get; set; }
        public double secondsPerDay { get; set; }
        public double secondsIntoDay { get; set; }
        public int dayOfYear { get; set; }
        public int dayOfMonth { get; set; }
        public int month { get; set; }
        public int seasonIndex { get; set; }
        public string seasonName { get; set; }
        public int year { get; set; }
        public int daysPerMonth { get; set; }
        public int seasonLengthDays { get; set; }
        public int daysPerYear { get; set; }
    }

    public sealed class WorldStateChunk
    {
        public long version { get; set; }
        public int width { get; set; }
        public int height { get; set; }
        public bool[][] walkable { get; set; }
        public List<ThingState> things { get; set; } = new List<ThingState>();
        public List<FactState> facts { get; set; } = new List<FactState>();
        public List<ReservationState> reservations { get; set; } = new List<ReservationState>();
    }

    public sealed class ThingState
    {
        public string id { get; set; }
        public string type { get; set; }
        public string[] tags { get; set; }
        public int x { get; set; }
        public int y { get; set; }
        public Dictionary<string, double> attributes { get; set; }
        public BuildingState building { get; set; }
    }

    public sealed class BuildingState
    {
        public RectState area { get; set; }
        public bool isOpen { get; set; }
        public int capacity { get; set; }
        public List<GridPosState> servicePoints { get; set; }
        public List<BuildingOpenHoursState> openHours { get; set; }
    }

    public sealed class RectState
    {
        public int minX { get; set; }
        public int minY { get; set; }
        public int maxX { get; set; }
        public int maxY { get; set; }
    }

    public sealed class GridPosState
    {
        public int x { get; set; }
        public int y { get; set; }
    }

    public sealed class BuildingOpenHoursState
    {
        public int[] daysOfWeek { get; set; }
        public string[] seasons { get; set; }
        public double startHour { get; set; }
        public double endHour { get; set; }
    }

    public sealed class FactState
    {
        public string pred { get; set; }
        public string a { get; set; }
        public string b { get; set; }
    }

    public sealed class ReservationState
    {
        public string thing { get; set; }
        public string owner { get; set; }
        public string mode { get; set; }
        public int priority { get; set; }
        public Guid planId { get; set; }
        public DateTime createdUtc { get; set; }
    }

    public sealed class InventorySystemState
    {
        public List<InventoryOwnerState> owners { get; set; } = new List<InventoryOwnerState>();
    }

    public sealed class InventoryOwnerState
    {
        public string ownerId { get; set; }
        public int slots { get; set; }
        public int stackSize { get; set; }
        public List<InventoryStackState> stacks { get; set; } = new List<InventoryStackState>();
        public double currency { get; set; }
    }

    public sealed class InventoryStackState
    {
        public string itemId { get; set; }
        public int quantity { get; set; }
        public int quality { get; set; }
    }

    public sealed class ShopSystemState
    {
        public List<ShopState> shops { get; set; } = new List<ShopState>();
    }

    public sealed class ShopState
    {
        public string ownerId { get; set; }
        public double markup { get; set; }
        public double markdown { get; set; }
        public int lastRestockDay { get; set; }
        public double restockHour { get; set; }
        public List<ShopStockState> stock { get; set; } = new List<ShopStockState>();
    }

    public sealed class ShopStockState
    {
        public string itemId { get; set; }
        public int quantity { get; set; }
        public int maxQuantity { get; set; }
        public string restockRule { get; set; }
        public double? priceOverride { get; set; }
    }

    public sealed class CropSystemState
    {
        public List<CropTileState> tiles { get; set; } = new List<CropTileState>();
        public double lastProcessedDay { get; set; }
        public string lastSeason { get; set; }
        public bool pendingAutoWater { get; set; }
        public bool pendingGrowthPause { get; set; }
        public RandomState rng { get; set; }
    }

    public sealed class CropTileState
    {
        public string plotId { get; set; }
        public int x { get; set; }
        public int y { get; set; }
        public bool tilled { get; set; }
        public string cropId { get; set; }
        public int stage { get; set; }
        public bool watered { get; set; }
        public int daysInStage { get; set; }
        public string plantedBy { get; set; }
        public int regrowCounter { get; set; }
        public bool readyToHarvest { get; set; }
        public int unwateredDays { get; set; }
    }

    public sealed class AnimalSystemState
    {
        public List<AnimalStateData> animals { get; set; } = new List<AnimalStateData>();
        public double lastWorldHours { get; set; }
        public bool growthPaused { get; set; }
    }

    public sealed class AnimalStateData
    {
        public string id { get; set; }
        public double hunger { get; set; }
        public double happiness { get; set; }
        public double lastFedHours { get; set; }
        public double lastBrushedHours { get; set; }
        public List<AnimalProduceStateData> produce { get; set; } = new List<AnimalProduceStateData>();
    }

    public sealed class AnimalProduceStateData
    {
        public double timerHours { get; set; }
        public bool ready { get; set; }
    }

    public sealed class SkillProgressionState
    {
        public List<ActorSkillProgressState> actors { get; set; } = new List<ActorSkillProgressState>();
    }

    public sealed class ActorSkillProgressState
    {
        public string actorId { get; set; }
        public List<SkillProgressState> skills { get; set; } = new List<SkillProgressState>();
    }

    public sealed class SkillProgressState
    {
        public string skillId { get; set; }
        public double xp { get; set; }
        public int level { get; set; }
    }

    public sealed class QuestSystemState
    {
        public List<QuestActorState> actors { get; set; } = new List<QuestActorState>();
    }

    public sealed class QuestActorState
    {
        public string actorId { get; set; }
        public List<QuestStateData> quests { get; set; } = new List<QuestStateData>();
    }

    public sealed class QuestStateData
    {
        public string questId { get; set; }
        public string status { get; set; }
        public int objectiveIndex { get; set; }
        public int progress { get; set; }
        public bool rewardsClaimed { get; set; }
    }

    public sealed class WeatherSystemState
    {
        public string currentStateId { get; set; }
        public WeatherSnapshotData currentSnapshot { get; set; }
        public double currentDayIndex { get; set; }
        public int lastGustHour { get; set; }
        public RandomState rng { get; set; }
    }

    public sealed class WeatherSnapshotData
    {
        public string id { get; set; }
        public string displayName { get; set; }
        public bool autoWaterCrops { get; set; }
        public bool growthPaused { get; set; }
        public bool cancelOutdoorShifts { get; set; }
        public double pathCostMultiplier { get; set; }
        public double gustStrength { get; set; }
    }

    public sealed class CalendarSystemState
    {
        public List<ActiveCalendarEventState> activeEvents { get; set; } = new List<ActiveCalendarEventState>();
    }

    public sealed class ActiveCalendarEventState
    {
        public string eventId { get; set; }
        public Dictionary<string, double?> attributeRestore { get; set; } = new Dictionary<string, double?>();
        public List<string> spawnedThings { get; set; } = new List<string>();
        public List<ScheduleOverrideState> overrides { get; set; } = new List<ScheduleOverrideState>();
    }

    public sealed class ScheduleOverrideState
    {
        public string roleId { get; set; }
        public List<string> actors { get; set; } = new List<string>();
        public string gotoTag { get; set; }
        public string taskId { get; set; }
    }

    public sealed class ActorHostState
    {
        public string actorId { get; set; }
        public Dictionary<string, double> planCooldownSecondsRemaining { get; set; } = new Dictionary<string, double>(StringComparer.Ordinal);
        public Dictionary<string, double> reservationCooldownSecondsRemaining { get; set; } = new Dictionary<string, double>(StringComparer.Ordinal);
        public RandomState rng { get; set; }
    }

    public sealed class ActorHostCollectionState
    {
        public List<ActorHostState> actors { get; set; } = new List<ActorHostState>();
    }

    public sealed class RandomState
    {
        public int inext { get; set; }
        public int inextp { get; set; }
        public int[] seedArray { get; set; }
    }
}
