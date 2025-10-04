
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DataDrivenGoap.Config
{
    public sealed class ReservationConfig
    {
        public string thing { get; set; }
        public string mode { get; set; }
        public int priority { get; set; }
    }

    public sealed class EffectConfig
    {
        public string type { get; set; }
        public string target { get; set; }
        public string who { get; set; }
        public string attr { get; set; }
        public string name { get; set; }
        public string op { get; set; }
        public JsonElement value { get; set; }
        public JsonElement defaultValue { get; set; }
        public bool clamp01 { get; set; }
        public bool? clamp { get; set; }
        public string pred { get; set; }
        public string a { get; set; }
        public string b { get; set; }
        public string condition { get; set; }
        public string when { get; set; }
        public string towards { get; set; }
        public double? stopWithin { get; set; }
    }

    public sealed class ItemPriceConfig
    {
        public double? buy { get; set; }
        public double? sell { get; set; }
    }

    public sealed class ItemEffectConfig
    {
        public string type { get; set; }
        public string value { get; set; }
    }

    public sealed class ItemConfig
    {
        public string id { get; set; }
        public string displayName { get; set; }
        public string[] tags { get; set; }
        public int stackSize { get; set; }
        public int quality { get; set; }
        public ItemPriceConfig price { get; set; }
        public ItemEffectConfig[] effects { get; set; }
        public string[] tools { get; set; }
        public string[] gifts { get; set; }
        public string spriteSlug { get; set; }
    }

    public sealed class RecipeConfig
    {
        public string id { get; set; }
        public Dictionary<string, int> inputs { get; set; }
        public Dictionary<string, int> outputs { get; set; }
        public string[] stations { get; set; }
        public double time { get; set; }
        public RecipeSkillGateConfig[] gates { get; set; }
    }

    public sealed class RecipeSkillGateConfig
    {
        public string skill { get; set; }
        public int level { get; set; }
    }

    public sealed class InventoryItemConfig
    {
        public string id { get; set; }
        public int quantity { get; set; }
        public int? quality { get; set; }
    }

    public sealed class InventoryConfig
    {
        public int slots { get; set; }
        public int stackSize { get; set; }
        public InventoryItemConfig[] start { get; set; }
    }

    public sealed class ContainerConfig
    {
        public InventoryConfig inventory { get; set; }
    }

    public sealed class ShopStockConfig
    {
        public string item { get; set; }
        public int quantity { get; set; }
        public string restock { get; set; }
        public double? price { get; set; }
    }

    public sealed class ShopConfig
    {
        public ShopStockConfig[] stock { get; set; }
        public string restockEvery { get; set; }
        public double? restockHour { get; set; }
        public double? markup { get; set; }
        public double? markdown { get; set; }
        public InventoryConfig inventory { get; set; }
    }

    public sealed class CropYieldConfig
    {
        public string item { get; set; }
        public int min { get; set; }
        public int max { get; set; }
    }

    public sealed class CropConfig
    {
        public string id { get; set; }
        public string[] seasons { get; set; }
        public int[] growthStages { get; set; }
        public int? regrowthDays { get; set; }
        public bool hydrationNeeded { get; set; }
        public bool witheredOnSeasonChange { get; set; }
        public string seedItemId { get; set; }
        public CropYieldConfig yield { get; set; }
        public string skill { get; set; }
        public double skillXp { get; set; }
    }

    public sealed class AnimalProduceConfig
    {
        public string item { get; set; }
        public int quantity { get; set; }
        public double intervalHours { get; set; }
        public double? happinessThreshold { get; set; }
        public double? hungerThreshold { get; set; }
    }

    public sealed class AnimalConfig
    {
        public string id { get; set; }
        public string species { get; set; }
        public string feedItemId { get; set; }
        public int feedQuantity { get; set; }
        public double hungerHours { get; set; }
        public double happinessHalfLifeHours { get; set; }
        public double feedHappiness { get; set; }
        public double brushHappiness { get; set; }
        public double brushCooldownHours { get; set; }
        public AnimalProduceConfig[] produce { get; set; }
    }

    public sealed class ActionConfig
    {
        public string id { get; set; }
        public string activity { get; set; }
        public string duration { get; set; }
        public string cost { get; set; }
        public string[] pre { get; set; }
        public ReservationConfig[] reservations { get; set; }
        public EffectConfig[] effects { get; set; }
    }

    public sealed class TargetSelectorConfig
    {
        public string type { get; set; }
        public string tag { get; set; }
        public bool excludeSelf { get; set; }
        public string where { get; set; }
    }

    public sealed class GoalActionConfig
    {
        public string id { get; set; }
        public bool moveToTarget { get; set; }
        public TargetSelectorConfig target { get; set; }
    }

    public sealed class GoalConfig
    {
        public string id { get; set; }
        public string priority { get; set; }
        public string[] satisfiedWhen { get; set; }
        public GoalActionConfig[] actions { get; set; }
    }

    public sealed class AttributeInitConfig
    {
        public double? value { get; set; }
        public double? min { get; set; }
        public double? max { get; set; }
    }

    public sealed class ActorSeedConfig
    {
        public int count { get; set; }
        public string type { get; set; }
        public string[] tags { get; set; }
        public Dictionary<string, AttributeInitConfig> attributes { get; set; }
        public ActorRoleConfig role { get; set; }
        public InventoryConfig inventory { get; set; }
        public double? currency { get; set; }
    }

    public sealed class ActorRoleConfig
    {
        public string id { get; set; }
        public RoleBlockConfig[] blocks { get; set; }
    }

    public sealed class RoleBlockConfig
    {
        public string start { get; set; }
        public string end { get; set; }
        public string @goto { get; set; }
        public string task { get; set; }
        public string[] days { get; set; }
        public string[] seasons { get; set; }
    }

    public sealed class ScheduleRoleConfig
    {
        public string id { get; set; }
        public RoleBlockConfig[] blocks { get; set; }
    }

    public sealed class SchedulePawnConfig
    {
        public string pawn { get; set; }
        public string role { get; set; }
        public RoleBlockConfig[] blocks { get; set; }
    }

    public sealed class ScheduleDatabaseConfig
    {
        public ScheduleRoleConfig[] roles { get; set; }
        public SchedulePawnConfig[] pawns { get; set; }
    }

    public sealed class CalendarEventWhenConfig
    {
        public string[] seasons { get; set; }
        public int[] daysOfSeason { get; set; }
        public int? startDayOfSeason { get; set; }
        public int? endDayOfSeason { get; set; }
        public string[] weekdays { get; set; }
        public double? startHour { get; set; }
        public double? endHour { get; set; }
        public string[] weather { get; set; }
    }

    public sealed class CalendarEventFactConfig
    {
        public string pred { get; set; }
        public string a { get; set; }
        public string b { get; set; }
    }

    public sealed class CalendarEventAttributeConfig
    {
        public string thing { get; set; }
        public string attribute { get; set; }
        public double value { get; set; }
    }

    public sealed class CalendarEventSpawnAttributeConfig
    {
        public string name { get; set; }
        public double value { get; set; }
    }

    public sealed class CalendarEventSpawnConfig
    {
        public string id { get; set; }
        public string type { get; set; }
        public string[] tags { get; set; }
        public int x { get; set; }
        public int y { get; set; }
        public CalendarEventSpawnAttributeConfig[] attributes { get; set; }
    }

    public sealed class CalendarEventScheduleOverrideConfig
    {
        public string role { get; set; }
        public string[] actors { get; set; }
        public string gotoTag { get; set; }
        public string task { get; set; }
    }

    public sealed class CalendarEventEffectConfig
    {
        public string type { get; set; }
        public string who { get; set; }
        public string target { get; set; }
        public double? amount { get; set; }
        public double? seconds { get; set; }
        public bool useStepDuration { get; set; }
        public string relationship { get; set; }
        public string item { get; set; }
    }

    public sealed class CalendarEventDoConfig
    {
        public CalendarEventFactConfig[] facts { get; set; }
        public CalendarEventAttributeConfig[] attributes { get; set; }
        public CalendarEventSpawnConfig[] spawns { get; set; }
        public string[] despawn { get; set; }
        public CalendarEventScheduleOverrideConfig[] schedule { get; set; }
    }

    public sealed class CalendarEventConfig
    {
        public string id { get; set; }
        public string label { get; set; }
        public CalendarEventWhenConfig when { get; set; }
        public CalendarEventDoConfig @do { get; set; }
        public CalendarEventEffectConfig[] effects { get; set; }
    }

    public sealed class CalendarEventDatabaseConfig
    {
        public CalendarEventConfig[] events { get; set; }
    }

    public sealed class DialogueConditionConfig
    {
        public string type { get; set; }
        public string relationship { get; set; }
        public double? min { get; set; }
        public string eventId { get; set; }
        public string weather { get; set; }
        public double? startHour { get; set; }
        public double? endHour { get; set; }
        public string fact { get; set; }
        public string factSubject { get; set; }
        public string factObject { get; set; }
        public string item { get; set; }
        public int? quantity { get; set; }
    }

    public sealed class DialogueOutcomeConfig
    {
        public string type { get; set; }
        public string relationship { get; set; }
        public double? amount { get; set; }
        public string fact { get; set; }
        public string factSubject { get; set; }
        public string factObject { get; set; }
        public string eventId { get; set; }
        public string item { get; set; }
        public int? quantity { get; set; }
    }

    public sealed class DialogueNodeConfig
    {
        public string id { get; set; }
        public string text { get; set; }
        public DialogueConditionConfig[] conditions { get; set; }
        public DialogueOutcomeConfig[] outcomes { get; set; }
        public double? cooldownSeconds { get; set; }
    }

    public sealed class DialogueDatabaseConfig
    {
        public DialogueNodeConfig[] nodes { get; set; }
    }

    public sealed class QuestObjectiveConfig
    {
        public string id { get; set; }
        public string description { get; set; }
        public int requiredCount { get; set; }
    }

    public sealed class QuestRewardItemConfig
    {
        public string itemId { get; set; }
        public int quantity { get; set; }
    }

    public sealed class QuestRewardConfig
    {
        public QuestRewardItemConfig[] items { get; set; }
        public double? currency { get; set; }
        public string description { get; set; }
    }

    public sealed class QuestConfig
    {
        public string id { get; set; }
        public string title { get; set; }
        public string description { get; set; }
        public string giver { get; set; }
        public string[] prerequisites { get; set; }
        public QuestObjectiveConfig[] objectives { get; set; }
        public QuestRewardConfig reward { get; set; }
        public bool autoAccept { get; set; }
    }

    public sealed class QuestDatabaseConfig
    {
        public QuestConfig[] quests { get; set; }
    }

    public sealed class BuildingAreaConfig
    {
        public int? x { get; set; }
        public int? y { get; set; }
        public int? width { get; set; }
        public int? height { get; set; }
    }

    public sealed class ServicePointConfig
    {
        public int? x { get; set; }
        public int? y { get; set; }
    }

    public sealed class BuildingOpenHoursConfig
    {
        public string[] days { get; set; }
        public string[] seasons { get; set; }
        public string open { get; set; }
        public string close { get; set; }
    }

    public sealed class BuildingConfig
    {
        public BuildingAreaConfig area { get; set; }
        public bool? open { get; set; }
        public int? capacity { get; set; }
        public ServicePointConfig[] service_points { get; set; }
        public BuildingOpenHoursConfig[] openHours { get; set; }
        public ShopConfig shop { get; set; }
    }

    public sealed class ThingSpawnConfig
    {
        public string id { get; set; }
        public string type { get; set; }
        public string[] tags { get; set; }
        public int? x { get; set; }
        public int? y { get; set; }
        public Dictionary<string, double> attributes { get; set; }
        public BuildingConfig building { get; set; }
        public ContainerConfig container { get; set; }
        public double? currency { get; set; }
    }

    public sealed class MapTileConfig
    {
        public bool walkable { get; set; }
        public bool farmland { get; set; }
        public bool water { get; set; }
        public bool shallowWater { get; set; }
        public bool forest { get; set; }
        public bool coastal { get; set; }
    }

    public sealed class FishingCatchConfig
    {
        public string id { get; set; }
        public string itemId { get; set; }
        public int? minQuantity { get; set; }
        public int? maxQuantity { get; set; }
        public string[] seasons { get; set; }
        public string[] weather { get; set; }
        public bool shallowOnly { get; set; }
        public bool deepOnly { get; set; }
        public double? weight { get; set; }
        public string baitItemId { get; set; }
        public int? castsPerSpot { get; set; }
        public string skill { get; set; }
        public double? skillXp { get; set; }
    }

    public sealed class FishingSystemConfig
    {
        public bool enabled { get; set; }
        public double respawnHours { get; set; }
        public int maxActiveSpots { get; set; }
        public double spotsPer100Tiles { get; set; }
        public FishingCatchConfig[] catches { get; set; }
    }

    public sealed class ForagingResourceConfig
    {
        public string id { get; set; }
        public string itemId { get; set; }
        public int? minQuantity { get; set; }
        public int? maxQuantity { get; set; }
        public string[] seasons { get; set; }
        public string[] weather { get; set; }
        public string[] biomes { get; set; }
        public double? weight { get; set; }
        public int? gathersPerSpot { get; set; }
        public string skill { get; set; }
        public double? skillXp { get; set; }
    }

    public sealed class ForagingSystemConfig
    {
        public bool enabled { get; set; }
        public double respawnHours { get; set; }
        public int maxActiveSpots { get; set; }
        public double forestSpotsPer100Tiles { get; set; }
        public double coastSpotsPer100Tiles { get; set; }
        public ForagingResourceConfig[] resources { get; set; }
    }

    public sealed class MiningOreConfig
    {
        public string id { get; set; }
        public string itemId { get; set; }
        public int? minQuantity { get; set; }
        public int? maxQuantity { get; set; }
        public string[] seasons { get; set; }
        public string[] weather { get; set; }
        public string[] biomes { get; set; }
        public double? weight { get; set; }
        public int? hitsPerNode { get; set; }
        public string requiredToolId { get; set; }
        public int? requiredToolTier { get; set; }
        public string skill { get; set; }
        public double? skillXp { get; set; }
    }

    public sealed class MiningLayerConfig
    {
        public string id { get; set; }
        public string[] oreIds { get; set; }
        public string[] biomes { get; set; }
    }

    public sealed class MiningNodeConfig
    {
        public string id { get; set; }
        public string type { get; set; }
        public string[] tags { get; set; }
        public int? x { get; set; }
        public int? y { get; set; }
        public string layer { get; set; }
        public string[] biomes { get; set; }
    }

    public sealed class MiningSystemConfig
    {
        public bool enabled { get; set; }
        public double respawnHours { get; set; }
        public int maxActiveNodes { get; set; }
        public MiningOreConfig[] ores { get; set; }
        public MiningLayerConfig[] layers { get; set; }
        public MiningNodeConfig[] nodes { get; set; }
    }

    public sealed class MapServicePointConfig
    {
        public double? x { get; set; }
        public double? y { get; set; }
    }

    public sealed class MapBuildingPrototypeConfig
    {
        public string idPrefix { get; set; }
        public string type { get; set; }
        public string[] tags { get; set; }
        public Dictionary<string, double> attributes { get; set; }
        public BuildingConfig building { get; set; }
        public MapServicePointConfig[] servicePoints { get; set; }
    }

    public sealed class WorldMapConfig
    {
        public string image { get; set; }
        public string key { get; set; }
        public string annotations { get; set; }
        public string data { get; set; }
        public int tileSize { get; set; }
        public Dictionary<string, MapTileConfig> tiles { get; set; }
        public Dictionary<string, MapBuildingPrototypeConfig> buildingPrototypes { get; set; }
    }

    public sealed class VillageConfig
    {
        public VillageMapData map { get; set; }
        public VillagePawnDataset pawns { get; set; }
        public Dictionary<string, VillageLocation> locations { get; set; }
        public SocialInteractionConfig social { get; set; }
    }

    public sealed class VillageMapData
    {
        public Dictionary<string, string> key { get; set; }
        public VillageMapAnnotations annotations { get; set; }
    }

    public sealed class VillageMapAnnotations
    {
        public VillageBuildingAnnotation[] buildings { get; set; }
    }

    public sealed class VillageBuildingAnnotation
    {
        public string name { get; set; }
        public double[] bbox { get; set; }
        public string location { get; set; }
    }

    public sealed class VillagePawnDataset
    {
        public VillagePawn[] pawns { get; set; }
    }

    public sealed class VillagePawn
    {
        public string id { get; set; }
        public string name { get; set; }
        public string role { get; set; }
        public VillagePawnLocation home { get; set; }
        public VillagePawnLocation workplace { get; set; }
        public InventoryConfig inventory { get; set; }
        public double? currency { get; set; }
    }

    public sealed class VillagePawnLocation
    {
        public string id { get; set; }
        public string name { get; set; }
        public string type { get; set; }
        public string location { get; set; }
        public double[] bbox { get; set; }
        public double[] center { get; set; }
    }

    public sealed class VillageLocation
    {
        public string id { get; set; }
        public string name { get; set; }
        public string type { get; set; }
        public double[] bbox { get; set; }
        public double[] center { get; set; }
    }

    public sealed class FactSeedConfig
    {
        public string pred { get; set; }
        public string a { get; set; }
        public string b { get; set; }
    }

    public sealed class WorldConfig
    {
        public int width { get; set; }
        public int height { get; set; }
        public double blockedChance { get; set; }
        public int shards { get; set; }
        public int rngSeed { get; set; }
        public ThingSpawnConfig[] things { get; set; }
        public FactSeedConfig[] facts { get; set; }
        public WorldMapConfig map { get; set; }
    }

    public sealed class SimulationConfig
    {
        public double durationGameDays { get; set; }
        public int actorHostSeed { get; set; }
        public double priorityJitter { get; set; }
        public bool? worldLoggingEnabled { get; set; }
    }

    public sealed class TimeConfig
    {
        public double dayLengthSeconds { get; set; }
        public int worldHoursPerDay { get; set; }
        public int minutesPerHour { get; set; }
        public int secondsPerMinute { get; set; }
        public int daysPerMonth { get; set; }
        public int seasonLengthDays { get; set; }
        public string[] seasons { get; set; }
        public int startYear { get; set; }
        public int startDayOfYear { get; set; }
        public double? startTimeOfDayHours { get; set; }
    }

    public sealed class NeedConfig
    {
        public string id { get; set; }
        public string attribute { get; set; }
        public double changePerTrigger { get; set; }
        public double triggersPerDay { get; set; }
        public bool clamp01 { get; set; }
        public double? minValue { get; set; }
        public double? maxValue { get; set; }
        public double? defaultValue { get; set; }
        public string[] targetTags { get; set; }
    }

    public sealed class NeedSystemConfig
    {
        public bool enabled { get; set; }
        public NeedConfig[] needs { get; set; }
    }

    public sealed class WeatherTransitionConfig
    {
        public string to { get; set; }
        public double weight { get; set; }
    }

    public sealed class WeatherStateConfig
    {
        public string id { get; set; }
        public string displayName { get; set; }
        public bool autoWaterCrops { get; set; }
        public bool growthPause { get; set; }
        public bool cancelOutdoorShifts { get; set; }
        public double pathCostMultiplier { get; set; }
        public double weight { get; set; }
        public Dictionary<string, double> seasonWeights { get; set; }
        public WeatherTransitionConfig[] transitions { get; set; }
        public double? gustStrengthMin { get; set; }
        public double? gustStrengthMax { get; set; }
    }

    public sealed class WeatherSystemConfig
    {
        public bool enabled { get; set; }
        public string defaultState { get; set; }
        public double dawnHour { get; set; }
        public double gustIntervalHours { get; set; }
        public WeatherStateConfig[] states { get; set; }
    }

    public sealed class RelationshipTypeConfig
    {
        public string id { get; set; }
        public bool symmetric { get; set; }
        public double minValue { get; set; }
        public double maxValue { get; set; }
        public double defaultValue { get; set; }
        public double? decayPerDay { get; set; }
        public string description { get; set; }
    }

    public sealed class RelationshipSeedConfig
    {
        public string from { get; set; }
        public string to { get; set; }
        public string type { get; set; }
        public double value { get; set; }
        public string notes { get; set; }
    }

    public sealed class SocialInteractionConfig
    {
        public bool enabled { get; set; }
        public RelationshipTypeConfig[] relationshipTypes { get; set; }
        public RelationshipSeedConfig[] seeds { get; set; }
    }

    public sealed class ObserverConfig
    {
        public string cameraPawn { get; set; }
        public bool showOnlySelectedPawn { get; set; }
    }

    public sealed class DemoConfig
    {
        public WorldConfig world { get; set; }
        public ActorSeedConfig actors { get; set; }
        public SimulationConfig simulation { get; set; }
        public TimeConfig time { get; set; }
        public NeedSystemConfig needs { get; set; }
        public SocialInteractionConfig social { get; set; }
        public ItemDatabaseConfig items { get; set; }
        public FarmingDatabaseConfig farming { get; set; }
        public LivestockDatabaseConfig livestock { get; set; }
        public WeatherSystemConfig weather { get; set; }
        public FishingSystemConfig fishing { get; set; }
        public ForagingSystemConfig foraging { get; set; }
        public MiningSystemConfig mining { get; set; }
        public FileReferenceConfig schedules { get; set; }
        public FileReferenceConfig events { get; set; }
        public FileReferenceConfig dialogue { get; set; }
        public FileReferenceConfig quests { get; set; }
        public PersistenceConfig persistence { get; set; }
        public ObserverConfig observer { get; set; }
    }

    public sealed class ItemDatabaseConfig
    {
        public string catalog { get; set; }
        public string recipes { get; set; }
    }

    public sealed class FarmingDatabaseConfig
    {
        public string crops { get; set; }
    }

    public sealed class LivestockDatabaseConfig
    {
        public string animals { get; set; }
    }

    public sealed class FileReferenceConfig
    {
        public string path { get; set; }
    }

    public sealed class PersistenceConfig
    {
        public string directory { get; set; }
        public string saveSlot { get; set; }
        public double autosaveMinutes { get; set; }
    }

    public static class ConfigLoader
    {
        public static List<ActionConfig> LoadActions(string path)
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<List<ActionConfig>>(fs, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidDataException($"Action config at '{path}' could not be deserialized.");
        }

        public static List<GoalConfig> LoadGoals(string path)
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<List<GoalConfig>>(fs, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidDataException($"Goal config at '{path}' could not be deserialized.");
        }

        public static List<ItemConfig> LoadItems(string path)
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<List<ItemConfig>>(fs, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException($"Item config at '{path}' could not be deserialized.");
        }

        public static List<RecipeConfig> LoadRecipes(string path)
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<List<RecipeConfig>>(fs, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException($"Recipe config at '{path}' could not be deserialized.");
        }

        public static DemoConfig LoadDemoConfig(string path)
        {
            using var fs = File.OpenRead(path);
            var config = JsonSerializer.Deserialize<DemoConfig>(fs, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                         ?? throw new InvalidDataException($"Demo config at '{path}' could not be deserialized.");

            ValidateDemoConfig(config, path);
            return config;
        }

        public static VillageConfig LoadVillageConfig(string path)
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<VillageConfig>(fs, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidDataException($"Village config at '{path}' could not be deserialized.");
        }

        public static List<CropConfig> LoadCrops(string path)
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<List<CropConfig>>(fs, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException($"Crop config at '{path}' could not be deserialized.");
        }

        public static List<AnimalConfig> LoadAnimals(string path)
        {
            using var fs = File.OpenRead(path);
            var configs = JsonSerializer.Deserialize<List<AnimalConfig>>(fs, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException($"Animal config at '{path}' could not be deserialized.");

            ValidateAnimalConfigs(configs, path);
            return configs;
        }

        private static void ValidateAnimalConfigs(IReadOnlyList<AnimalConfig> configs, string sourcePath)
        {
            if (configs is null)
            {
                throw new InvalidDataException($"Animal config at '{sourcePath}' is empty.");
            }

            for (int i = 0; i < configs.Count; i++)
            {
                var config = configs[i];
                if (config is null)
                {
                    throw new InvalidDataException($"Animal config at '{sourcePath}' contains a null entry at index {i}.");
                }

                if (string.IsNullOrWhiteSpace(config.id))
                {
                    throw new InvalidDataException($"Animal config at '{sourcePath}' entry {i} is missing 'id'.");
                }

                string animalId = config.id.Trim();

                if (config.feedQuantity <= 0)
                {
                    throw new InvalidDataException($"Animal '{animalId}' in '{sourcePath}' must specify a positive 'feedQuantity'.");
                }

                if (!double.IsFinite(config.hungerHours) || config.hungerHours <= 0.0)
                {
                    throw new InvalidDataException($"Animal '{animalId}' in '{sourcePath}' must specify a positive 'hungerHours'.");
                }

                if (!double.IsFinite(config.happinessHalfLifeHours) || config.happinessHalfLifeHours <= 0.0)
                {
                    throw new InvalidDataException($"Animal '{animalId}' in '{sourcePath}' must specify a positive 'happinessHalfLifeHours'.");
                }

                if (!double.IsFinite(config.feedHappiness) || config.feedHappiness < 0.0 || config.feedHappiness > 1.0)
                {
                    throw new InvalidDataException($"Animal '{animalId}' in '{sourcePath}' must specify 'feedHappiness' between 0 and 1.");
                }

                if (!double.IsFinite(config.brushHappiness) || config.brushHappiness < 0.0 || config.brushHappiness > 1.0)
                {
                    throw new InvalidDataException($"Animal '{animalId}' in '{sourcePath}' must specify 'brushHappiness' between 0 and 1.");
                }

                if (!double.IsFinite(config.brushCooldownHours) || config.brushCooldownHours <= 0.0)
                {
                    throw new InvalidDataException($"Animal '{animalId}' in '{sourcePath}' must specify a positive 'brushCooldownHours'.");
                }

                var produce = config.produce;
                if (produce == null)
                {
                    continue;
                }

                for (int j = 0; j < produce.Length; j++)
                {
                    var entry = produce[j];
                    if (entry is null)
                    {
                        throw new InvalidDataException($"Animal '{animalId}' in '{sourcePath}' has a null produce entry at index {j}.");
                    }

                    if (string.IsNullOrWhiteSpace(entry.item))
                    {
                        throw new InvalidDataException($"Animal '{animalId}' in '{sourcePath}' produce entry {j} is missing 'item'.");
                    }

                    if (entry.quantity <= 0)
                    {
                        throw new InvalidDataException($"Animal '{animalId}' in '{sourcePath}' produce '{entry.item}' must specify a positive 'quantity'.");
                    }

                    if (!double.IsFinite(entry.intervalHours) || entry.intervalHours <= 0.0)
                    {
                        throw new InvalidDataException($"Animal '{animalId}' in '{sourcePath}' produce '{entry.item}' must specify a positive 'intervalHours'.");
                    }

                    if (entry.happinessThreshold is null || !double.IsFinite(entry.happinessThreshold.Value) || entry.happinessThreshold.Value < 0.0 || entry.happinessThreshold.Value > 1.0)
                    {
                        throw new InvalidDataException($"Animal '{animalId}' in '{sourcePath}' produce '{entry.item}' must specify 'happinessThreshold' between 0 and 1.");
                    }

                    if (entry.hungerThreshold is null || !double.IsFinite(entry.hungerThreshold.Value) || entry.hungerThreshold.Value < 0.0 || entry.hungerThreshold.Value > 1.0)
                    {
                        throw new InvalidDataException($"Animal '{animalId}' in '{sourcePath}' produce '{entry.item}' must specify 'hungerThreshold' between 0 and 1.");
                    }
                }
            }
        }

        public static ScheduleDatabaseConfig LoadSchedules(string path)
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<ScheduleDatabaseConfig>(fs, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidDataException($"Schedule config at '{path}' could not be deserialized.");
        }

        public static CalendarEventDatabaseConfig LoadCalendarEvents(string path)
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<CalendarEventDatabaseConfig>(fs, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidDataException($"Calendar event config at '{path}' could not be deserialized.");
        }

        public static DialogueDatabaseConfig LoadDialogue(string path)
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<DialogueDatabaseConfig>(fs, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidDataException($"Dialogue config at '{path}' could not be deserialized.");
        }

        public static QuestDatabaseConfig LoadQuests(string path)
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<QuestDatabaseConfig>(fs, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidDataException($"Quest config at '{path}' could not be deserialized.");
        }

        private static void ValidateDemoConfig(DemoConfig config, string sourcePath)
        {
            if (config.world is null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'world' section.");
            }

            if (config.actors is null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'actors' section.");
            }

            if (config.simulation is null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'simulation' section.");
            }

            if (config.time is null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'time' section.");
            }

            if (config.needs is null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'needs' section.");
            }

            if (config.social is null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'social' section.");
            }

            if (config.items is null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'items' section.");
            }

            if (config.farming is null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'farming' section.");
            }

            if (config.livestock is null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'livestock' section.");
            }

            if (config.weather is null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'weather' section.");
            }

            if (config.mining is null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'mining' section.");
            }

            if (config.schedules is null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'schedules' reference.");
            }

            if (config.events is null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'events' reference.");
            }

            if (config.dialogue is null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'dialogue' reference.");
            }

            if (config.persistence is null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'persistence' section.");
            }

            ValidateWorldConfig(config.world, sourcePath);
            ValidateSimulationConfig(config.simulation, sourcePath);
            ValidateTimeConfig(config.time, sourcePath);
            ValidateNeedSystemConfig(config.needs, sourcePath);
            ValidateItemDatabaseConfig(config.items, sourcePath);
            ValidateFarmingDatabaseConfig(config.farming, sourcePath);
            ValidateLivestockDatabaseConfig(config.livestock, sourcePath);
            ValidateWeatherConfig(config.weather, sourcePath);
            ValidateMiningConfig(config.mining, sourcePath);
            ValidateFileReferenceConfig(config.schedules, sourcePath, "schedules");
            ValidateFileReferenceConfig(config.events, sourcePath, "events");
            ValidateFileReferenceConfig(config.dialogue, sourcePath, "dialogue");
            ValidatePersistenceConfig(config.persistence, sourcePath);
            ValidateActorSeedConfig(config.actors, sourcePath);
            ValidateSocialConfig(config.social, sourcePath);
        }

        private static void ValidateWorldConfig(WorldConfig config, string sourcePath)
        {
            if (config.width <= 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' has an invalid 'world.width' value of {config.width}. It must be greater than zero.");
            }

            if (config.height <= 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' has an invalid 'world.height' value of {config.height}. It must be greater than zero.");
            }

            if (config.blockedChance < 0.0 || config.blockedChance > 1.0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' has an invalid 'world.blockedChance' value of {config.blockedChance}. It must be between 0 and 1.");
            }

            if (config.shards <= 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' has an invalid 'world.shards' value of {config.shards}. It must be greater than zero.");
            }

            if (config.things == null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'world.things' collection.");
            }

            if (config.facts == null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'world.facts' collection.");
            }

            if (config.map is null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'world.map' section.");
            }
        }

        private static void ValidateSimulationConfig(SimulationConfig config, string sourcePath)
        {
            if (double.IsNaN(config.durationGameDays) || double.IsInfinity(config.durationGameDays) || config.durationGameDays <= 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' has an invalid 'simulation.durationGameDays' value of {config.durationGameDays}. It must be a finite number greater than zero.");
            }

            if (config.priorityJitter < 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' has an invalid 'simulation.priorityJitter' value of {config.priorityJitter}. It cannot be negative.");
            }
        }

        private static void ValidateTimeConfig(TimeConfig config, string sourcePath)
        {
            if (config.dayLengthSeconds <= 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' has an invalid 'time.dayLengthSeconds' value of {config.dayLengthSeconds}. It must be greater than zero.");
            }

            if (config.worldHoursPerDay <= 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' has an invalid 'time.worldHoursPerDay' value of {config.worldHoursPerDay}. It must be greater than zero.");
            }

            if (config.minutesPerHour <= 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' has an invalid 'time.minutesPerHour' value of {config.minutesPerHour}. It must be greater than zero.");
            }

            if (config.secondsPerMinute <= 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' has an invalid 'time.secondsPerMinute' value of {config.secondsPerMinute}. It must be greater than zero.");
            }

            if (config.daysPerMonth <= 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' has an invalid 'time.daysPerMonth' value of {config.daysPerMonth}. It must be greater than zero.");
            }

            if (config.seasonLengthDays <= 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' has an invalid 'time.seasonLengthDays' value of {config.seasonLengthDays}. It must be greater than zero.");
            }

            if (config.seasons == null || config.seasons.Length == 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' must define at least one entry in 'time.seasons'.");
            }

            if (config.startYear <= 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' has an invalid 'time.startYear' value of {config.startYear}. It must be greater than zero.");
            }

            if (config.startDayOfYear <= 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' has an invalid 'time.startDayOfYear' value of {config.startDayOfYear}. It must be greater than zero.");
            }

            if (config.startTimeOfDayHours is { } startTime && startTime < 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' has an invalid 'time.startTimeOfDayHours' value of {startTime}. It cannot be negative.");
            }
        }

        private static void ValidateNeedSystemConfig(NeedSystemConfig config, string sourcePath)
        {
            if (config.needs == null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'needs.needs' collection.");
            }
        }

        private static void ValidateItemDatabaseConfig(ItemDatabaseConfig config, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(config.catalog))
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' must provide a non-empty 'items.catalog' value.");
            }

            if (string.IsNullOrWhiteSpace(config.recipes))
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' must provide a non-empty 'items.recipes' value.");
            }
        }

        private static void ValidateFarmingDatabaseConfig(FarmingDatabaseConfig config, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(config.crops))
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' must provide a non-empty 'farming.crops' value.");
            }
        }

        private static void ValidateLivestockDatabaseConfig(LivestockDatabaseConfig config, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(config.animals))
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' must provide a non-empty 'livestock.animals' value.");
            }
        }

        private static void ValidateWeatherConfig(WeatherSystemConfig config, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(config.defaultState))
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' must provide a non-empty 'weather.defaultState' value.");
            }

            if (config.states == null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'weather.states' collection.");
            }
        }

        private static void ValidateMiningConfig(MiningSystemConfig config, string sourcePath)
        {
            if (config.ores == null || config.ores.Length == 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' must define at least one entry in 'mining.ores'.");
            }

            for (int i = 0; i < config.ores.Length; i++)
            {
                var ore = config.ores[i];
                if (ore == null)
                    throw new InvalidDataException($"Demo config '{sourcePath}' has a null entry in 'mining.ores' at index {i}.");
                if (string.IsNullOrWhiteSpace(ore.id))
                    throw new InvalidDataException($"Demo config '{sourcePath}' mining ore at index {i} must define an id.");
                if (string.IsNullOrWhiteSpace(ore.itemId))
                    throw new InvalidDataException($"Demo config '{sourcePath}' mining ore '{ore.id}' must define an itemId.");
            }

            if (config.layers == null || config.layers.Length == 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' must define at least one entry in 'mining.layers'.");
            }

            for (int i = 0; i < config.layers.Length; i++)
            {
                var layer = config.layers[i];
                if (layer == null)
                    throw new InvalidDataException($"Demo config '{sourcePath}' has a null entry in 'mining.layers' at index {i}.");
                if (string.IsNullOrWhiteSpace(layer.id))
                    throw new InvalidDataException($"Demo config '{sourcePath}' mining layer at index {i} must define an id.");
                if (layer.oreIds == null || layer.oreIds.Length == 0)
                    throw new InvalidDataException($"Demo config '{sourcePath}' mining layer '{layer.id}' must list oreIds.");
            }

            if (config.nodes != null)
            {
                for (int i = 0; i < config.nodes.Length; i++)
                {
                    var node = config.nodes[i];
                    if (node == null)
                        throw new InvalidDataException($"Demo config '{sourcePath}' has a null entry in 'mining.nodes' at index {i}.");
                    if (string.IsNullOrWhiteSpace(node.id))
                        throw new InvalidDataException($"Demo config '{sourcePath}' mining node at index {i} must define an id.");
                    if (!node.x.HasValue || !node.y.HasValue)
                        throw new InvalidDataException($"Demo config '{sourcePath}' mining node '{node.id}' must define x and y coordinates.");
                    if (string.IsNullOrWhiteSpace(node.layer))
                        throw new InvalidDataException($"Demo config '{sourcePath}' mining node '{node.id}' must reference a layer id.");
                }
            }
        }

        private static void ValidateFileReferenceConfig(FileReferenceConfig config, string sourcePath, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(config.path))
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' must provide a non-empty '{propertyName}.path' value.");
            }
        }

        private static void ValidatePersistenceConfig(PersistenceConfig config, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(config.directory))
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' must provide a non-empty 'persistence.directory' value.");
            }

            if (string.IsNullOrWhiteSpace(config.saveSlot))
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' must provide a non-empty 'persistence.saveSlot' value.");
            }

            if (config.autosaveMinutes <= 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' has an invalid 'persistence.autosaveMinutes' value of {config.autosaveMinutes}. It must be greater than zero.");
            }
        }

        private static void ValidateActorSeedConfig(ActorSeedConfig config, string sourcePath)
        {
            if (config.count <= 0)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' has an invalid 'actors.count' value of {config.count}. It must be greater than zero.");
            }
        }

        private static void ValidateSocialConfig(SocialInteractionConfig config, string sourcePath)
        {
            if (config.relationshipTypes == null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'social.relationshipTypes' collection.");
            }

            if (config.seeds == null)
            {
                throw new InvalidDataException($"Demo config '{sourcePath}' is missing the 'social.seeds' collection.");
            }
        }
    }
}
