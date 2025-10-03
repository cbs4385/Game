
using System;
using DataDrivenGoap.Core;

namespace DataDrivenGoap.Effects
{
    public struct ReadSetEntry
    {
        public ThingId Thing; public string ExpectAttribute; public double? ExpectValue;
        public ReadSetEntry(ThingId t, string attr, double? val) { Thing = t; ExpectAttribute = attr; ExpectValue = val; }
    }

    public struct WriteSetEntry
    {
        public ThingId Thing; public string Attribute; public double Value;
        public WriteSetEntry(ThingId t, string attr, double val) { Thing = t; Attribute = attr; Value = val; }
    }

    public struct FactDelta { public string Pred; public ThingId A; public ThingId B; public bool Add; }

    public struct ThingAttributeValue
    {
        public string Name;
        public double Value;
        public ThingAttributeValue(string name, double value) { Name = name; Value = value; }
    }

    public struct ThingSpawnRequest
    {
        public ThingId Id;
        public string Type;
        public string[] Tags;
        public ThingAttributeValue[] Attributes;
        public GridPos Position;
    }

    public struct PlanCooldownRequest
    {
        public ThingId Scope;
        public double Seconds;
        public bool UseStepDuration;
        public PlanCooldownRequest(ThingId scope, double seconds, bool useStepDuration)
        {
            Scope = scope;
            Seconds = seconds;
            UseStepDuration = useStepDuration;
        }
    }

    public struct InventoryDelta
    {
        public ThingId Owner;
        public string ItemId;
        public int Quantity;
        public bool Remove;

        public InventoryDelta(ThingId owner, string itemId, int quantity, bool remove)
        {
            Owner = owner;
            ItemId = itemId;
            Quantity = quantity;
            Remove = remove;
        }
    }

    public struct CurrencyDelta
    {
        public ThingId Owner;
        public double Amount;
        public CurrencyDelta(ThingId owner, double amount)
        {
            Owner = owner;
            Amount = amount;
        }
    }

    public enum ShopTransactionKind
    {
        Purchase,
        Sale
    }

    public struct ShopTransaction
    {
        public ThingId Shop;
        public ThingId Actor;
        public string ItemId;
        public int Quantity;
        public ShopTransactionKind Kind;

        public ShopTransaction(ThingId shop, ThingId actor, string itemId, int quantity, ShopTransactionKind kind)
        {
            Shop = shop;
            Actor = actor;
            ItemId = itemId;
            Quantity = quantity;
            Kind = kind;
        }
    }

    public struct RelationshipDelta
    {
        public ThingId From;
        public ThingId To;
        public string RelationshipId;
        public string ItemId;
        public double? ExplicitDelta;

        public RelationshipDelta(ThingId from, ThingId to, string relationshipId, string itemId, double? explicitDelta)
        {
            From = from;
            To = to;
            RelationshipId = relationshipId;
            ItemId = itemId;
            ExplicitDelta = explicitDelta;
        }
    }

    public enum CropOperationKind
    {
        Till,
        Plant,
        Water,
        Harvest
    }

    public struct CropOperation
    {
        public CropOperationKind Kind;
        public ThingId Plot;
        public ThingId Actor;
        public string CropId;
        public string SeedItemId;
        public int SeedQuantity;
    }

    public enum AnimalOperationKind
    {
        Feed,
        Brush,
        Collect
    }

    public struct AnimalOperation
    {
        public AnimalOperationKind Kind;
        public ThingId Animal;
        public ThingId Actor;
        public string ItemId;
        public int Quantity;
    }

    public enum FishingOperationKind
    {
        Cast
    }

    public struct FishingOperation
    {
        public FishingOperationKind Kind;
        public ThingId Spot;
        public ThingId Actor;
        public string BaitItemId;
        public int BaitQuantity;
    }

    public enum ForagingOperationKind
    {
        Harvest
    }

    public struct ForagingOperation
    {
        public ForagingOperationKind Kind;
        public ThingId Spot;
        public ThingId Actor;
    }

    public enum MiningOperationKind
    {
        Extract
    }

    public struct MiningOperation
    {
        public MiningOperationKind Kind;
        public ThingId Node;
        public ThingId Actor;
        public string ToolItemId;
        public int ToolTier;
    }

    public enum QuestOperationKind
    {
        Accept,
        Progress,
        ClaimRewards
    }

    public struct QuestOperation
    {
        public QuestOperationKind Kind;
        public ThingId Actor;
        public string QuestId;
        public string ObjectiveId;
        public int Amount;
        public bool GrantRewards;
    }

    public struct EffectBatch
    {
        public long BaseVersion;
        public ReadSetEntry[] Reads;
        public WriteSetEntry[] Writes;
        public FactDelta[] FactDeltas;
        public ThingSpawnRequest[] Spawns;
        public PlanCooldownRequest[] PlanCooldowns;
        public ThingId[] Despawns;
        public InventoryDelta[] InventoryOps;
        public CurrencyDelta[] CurrencyOps;
        public ShopTransaction[] ShopTransactions;
        public RelationshipDelta[] RelationshipOps;
        public CropOperation[] CropOps;
        public AnimalOperation[] AnimalOps;
        public MiningOperation[] MiningOps;
        public FishingOperation[] FishingOps;
        public ForagingOperation[] ForagingOps;
        public QuestOperation[] QuestOps;
    }
}
