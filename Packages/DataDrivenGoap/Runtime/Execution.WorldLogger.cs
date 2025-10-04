using System;
using System.Globalization;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;

namespace DataDrivenGoap.Execution
{
    public sealed class WorldLogger : IDisposable
    {
        private const long MaxLogBytes = 75L * 1024L * 1024L;

        private readonly object _gate = new object();
        private readonly RollingLogWriter _writer;
        private readonly bool _enabled;

        public WorldLogger(string filePath, bool enabled = true)
        {
            _enabled = enabled;

            if (!_enabled)
            {
                _writer = null;
                return;
            }

            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("World log file path must be provided", nameof(filePath));

            _writer = new RollingLogWriter(filePath, MaxLogBytes);
        }

        public void LogInfo(string message)
        {
            Write("INFO", message ?? string.Empty);
        }

        public void LogActorLifecycle(ThingId actor, string state)
        {
            string actorId = actor.Value ?? "<unknown>";
            string details = $"state={state ?? "<unknown>"} actor={actorId}";
            Write("ACTOR", details);
        }

        public void LogPlanSelection(ThingId actor, string goalId, string summary, int stepCount, long snapshotVersion, string worldTime, string worldDay)
        {
            string actorId = actor.Value ?? "<unknown>";
            string goal = goalId ?? "<none>";
            string detail =
                $"actor={actorId} goal={goal} steps={stepCount} summary={summary ?? "<none>"} snapshot_version={snapshotVersion} world_time={worldTime ?? "<unknown>"} world_day={worldDay ?? "<unknown>"}";
            Write("PLAN", detail);
        }

        public void LogStep(string status, ThingId actor, Guid planId, string description)
        {
            string actorId = actor.Value ?? "<unknown>";
            string details = $"status={status ?? "<none>"} actor={actorId} plan={planId} {description ?? string.Empty}".TrimEnd();
            Write("STEP", details);
        }

        public void LogEffectSummary(ThingId actor, Guid planId, string activity, in EffectBatch batch, long snapshotVersion, string worldTime, string worldDay)
        {
            string actorId = actor.Value ?? "<unknown>";
            string act = string.IsNullOrWhiteSpace(activity) ? "<unknown>" : activity;
            string wt = string.IsNullOrWhiteSpace(worldTime) ? "<unknown>" : worldTime;
            string wd = string.IsNullOrWhiteSpace(worldDay) ? "<unknown>" : worldDay;
            string summary =
                $"actor={actorId} plan={planId} activity={act} snapshot_version={snapshotVersion} base_version={batch.BaseVersion} world_time={wt} world_day={wd} " +
                $"reads={Count(batch.Reads)} writes={Count(batch.Writes)} facts={Count(batch.FactDeltas)} spawns={Count(batch.Spawns)} despawns={Count(batch.Despawns)} " +
                $"plan_cooldowns={Count(batch.PlanCooldowns)} inventory_ops={Count(batch.InventoryOps)} currency_ops={Count(batch.CurrencyOps)} shop_txns={Count(batch.ShopTransactions)} " +
                $"relationship_ops={Count(batch.RelationshipOps)} crop_ops={Count(batch.CropOps)} animal_ops={Count(batch.AnimalOps)} mining_ops={Count(batch.MiningOps)} fishing_ops={Count(batch.FishingOps)} foraging_ops={Count(batch.ForagingOps)} quest_ops={Count(batch.QuestOps)}";
            Write("EFFECT", summary);

            foreach (var read in batch.Reads ?? Array.Empty<ReadSetEntry>())
            {
                string detail =
                    $"actor={actorId} plan={planId} activity={act} kind=read thing={FormatThingId(read.Thing)} attr={read.ExpectAttribute ?? "<none>"} expect={FormatNullableDouble(read.ExpectValue)} world_time={wt} world_day={wd}";
                Write("EFFECT_DETAIL", detail);
            }

            foreach (var write in batch.Writes ?? Array.Empty<WriteSetEntry>())
            {
                string detail =
                    $"actor={actorId} plan={planId} activity={act} kind=write thing={FormatThingId(write.Thing)} attr={write.Attribute ?? "<none>"} value={FormatDouble(write.Value)} world_time={wt} world_day={wd}";
                Write("EFFECT_DETAIL", detail);
            }

            foreach (var fact in batch.FactDeltas ?? Array.Empty<FactDelta>())
            {
                string change = fact.Add ? "add" : "remove";
                string detail =
                    $"actor={actorId} plan={planId} activity={act} kind=fact predicate={fact.Pred ?? "<none>"} subject={FormatThingId(fact.A)} object={FormatThingId(fact.B)} change={change} world_time={wt} world_day={wd}";
                Write("EFFECT_DETAIL", detail);
            }

            foreach (var spawn in batch.Spawns ?? Array.Empty<ThingSpawnRequest>())
            {
                string detail =
                    $"actor={actorId} plan={planId} activity={act} kind=spawn thing={FormatThingId(spawn.Id)} type={spawn.Type ?? "<none>"} pos={spawn.Position} tags={FormatTags(spawn.Tags)} attrs={FormatAttributes(spawn.Attributes)} world_time={wt} world_day={wd}";
                Write("EFFECT_DETAIL", detail);
            }

            foreach (var despawn in batch.Despawns ?? Array.Empty<ThingId>())
            {
                Write("EFFECT_DETAIL", $"actor={actorId} plan={planId} activity={act} kind=despawn thing={FormatThingId(despawn)} world_time={wt} world_day={wd}");
            }

            foreach (var cooldown in batch.PlanCooldowns ?? Array.Empty<PlanCooldownRequest>())
            {
                string detail =
                    $"actor={actorId} plan={planId} activity={act} kind=plan_cooldown scope={FormatThingId(cooldown.Scope)} seconds={FormatDouble(cooldown.Seconds)} use_duration={cooldown.UseStepDuration} world_time={wt} world_day={wd}";
                Write("EFFECT_DETAIL", detail);
            }

            foreach (var op in batch.InventoryOps ?? Array.Empty<InventoryDelta>())
            {
                string mode = op.Remove ? "remove" : "add";
                string detail =
                    $"actor={actorId} plan={planId} activity={act} kind=inventory owner={FormatThingId(op.Owner)} item={op.ItemId ?? "<unknown>"} quantity={op.Quantity} mode={mode} world_time={wt} world_day={wd}";
                Write("EFFECT_DETAIL", detail);
            }

            foreach (var delta in batch.CurrencyOps ?? Array.Empty<CurrencyDelta>())
            {
                string detail =
                    $"actor={actorId} plan={planId} activity={act} kind=currency owner={FormatThingId(delta.Owner)} amount={FormatDouble(delta.Amount)} world_time={wt} world_day={wd}";
                Write("EFFECT_DETAIL", detail);
            }

            foreach (var txn in batch.ShopTransactions ?? Array.Empty<ShopTransaction>())
            {
                string mode = txn.Kind == ShopTransactionKind.Sale ? "sell" : "buy";
                string detail =
                    $"actor={actorId} plan={planId} activity={act} kind=shop mode={mode} shop={FormatThingId(txn.Shop)} txn_actor={FormatThingId(txn.Actor)} item={txn.ItemId ?? "<unknown>"} quantity={txn.Quantity} world_time={wt} world_day={wd}";
                Write("EFFECT_DETAIL", detail);
            }

            foreach (var rel in batch.RelationshipOps ?? Array.Empty<RelationshipDelta>())
            {
                string detail =
                    $"actor={actorId} plan={planId} activity={act} kind=relationship from={FormatThingId(rel.From)} to={FormatThingId(rel.To)} relationship={rel.RelationshipId ?? "<none>"} item={rel.ItemId ?? "<none>"} delta={FormatNullableDouble(rel.ExplicitDelta)} world_time={wt} world_day={wd}";
                Write("EFFECT_DETAIL", detail);
            }

            foreach (var crop in batch.CropOps ?? Array.Empty<CropOperation>())
            {
                string detail =
                    $"actor={actorId} plan={planId} activity={act} kind=crop op={crop.Kind} plot={FormatThingId(crop.Plot)} actor_ref={FormatThingId(crop.Actor)} crop_id={crop.CropId ?? "<none>"} seed={crop.SeedItemId ?? "<none>"} quantity={crop.SeedQuantity} world_time={wt} world_day={wd}";
                Write("EFFECT_DETAIL", detail);
            }

            foreach (var animal in batch.AnimalOps ?? Array.Empty<AnimalOperation>())
            {
                string detail =
                    $"actor={actorId} plan={planId} activity={act} kind=animal op={animal.Kind} target={FormatThingId(animal.Animal)} actor_ref={FormatThingId(animal.Actor)} item={animal.ItemId ?? "<none>"} quantity={animal.Quantity} world_time={wt} world_day={wd}";
                Write("EFFECT_DETAIL", detail);
            }

            foreach (var mining in batch.MiningOps ?? Array.Empty<MiningOperation>())
            {
                string detail =
                    $"actor={actorId} plan={planId} activity={act} kind=mining node={FormatThingId(mining.Node)} actor_ref={FormatThingId(mining.Actor)} tool={mining.ToolItemId ?? "<none>"} tier={mining.ToolTier} world_time={wt} world_day={wd}";
                Write("EFFECT_DETAIL", detail);
            }

            foreach (var fishing in batch.FishingOps ?? Array.Empty<FishingOperation>())
            {
                string detail =
                    $"actor={actorId} plan={planId} activity={act} kind=fishing spot={FormatThingId(fishing.Spot)} actor_ref={FormatThingId(fishing.Actor)} bait={fishing.BaitItemId ?? "<none>"} quantity={fishing.BaitQuantity} world_time={wt} world_day={wd}";
                Write("EFFECT_DETAIL", detail);
            }

            foreach (var foraging in batch.ForagingOps ?? Array.Empty<ForagingOperation>())
            {
                string detail =
                    $"actor={actorId} plan={planId} activity={act} kind=foraging spot={FormatThingId(foraging.Spot)} actor_ref={FormatThingId(foraging.Actor)} world_time={wt} world_day={wd}";
                Write("EFFECT_DETAIL", detail);
            }

            foreach (var quest in batch.QuestOps ?? Array.Empty<QuestOperation>())
            {
                string detail =
                    $"actor={actorId} plan={planId} activity={act} kind=quest quest_id={quest.QuestId ?? "<unknown>"} actor_ref={FormatThingId(quest.Actor)} op_kind={quest.Kind} objective={quest.ObjectiveId ?? "<none>"} amount={quest.Amount} grant_rewards={quest.GrantRewards} world_time={wt} world_day={wd}";
                Write("EFFECT_DETAIL", detail);
            }
        }

        public void LogInventoryChange(ThingId owner, string itemId, int quantity, string source, string executionContext)
        {
            string actorId = owner.Value ?? "<unknown>";
            string direction = quantity >= 0 ? "add" : "remove";
            string ctx = string.IsNullOrWhiteSpace(executionContext) ? string.Empty : " " + executionContext;
            string details = $"actor={actorId} item={itemId ?? "<unknown>"} qty={quantity} mode={direction} source={source ?? "<none>"}{ctx}";
            Write("INVENTORY", details);
        }

        public void LogCurrencyChange(ThingId owner, double delta, double newBalance, string source, string executionContext)
        {
            string actorId = owner.Value ?? "<unknown>";
            string details =
                $"actor={actorId} delta={delta.ToString("0.###", CultureInfo.InvariantCulture)} balance={newBalance.ToString("0.###", CultureInfo.InvariantCulture)} source={source ?? "<none>"}";
            if (!string.IsNullOrWhiteSpace(executionContext))
            {
                details += " " + executionContext;
            }
            Write("CURRENCY", details);
        }

        public void LogShopTransaction(ThingId shop, ThingId actor, string itemId, int quantity, double totalPrice, ShopTransactionKind kind, string executionContext)
        {
            string shopId = shop.Value ?? "<unknown>";
            string actorId = actor.Value ?? "<unknown>";
            string mode = kind == ShopTransactionKind.Sale ? "sell" : "buy";
            string details =
                $"mode={mode} actor={actorId} shop={shopId} item={itemId ?? "<unknown>"} quantity={quantity} total={totalPrice.ToString("0.###", CultureInfo.InvariantCulture)}";
            if (!string.IsNullOrWhiteSpace(executionContext))
            {
                details += " " + executionContext;
            }
            Write("SHOP", details);
        }

        public void LogCustom(string category, ThingId actor, string message, string executionContext = null)
        {
            string cat = string.IsNullOrWhiteSpace(category) ? "CUSTOM" : category.Trim().ToUpperInvariant();
            string actorId = actor.Value ?? "<unknown>";
            string details = $"actor={actorId} {message ?? string.Empty}".TrimEnd();
            if (!string.IsNullOrWhiteSpace(executionContext))
            {
                details = string.IsNullOrWhiteSpace(details)
                    ? executionContext.Trim()
                    : details + " " + executionContext;
            }
            Write(cat, details);
        }

        public void LogQuestEvent(ThingId actor, string questId, string status, string objectiveId, int progress, int required, string message, string executionContext = null)
        {
            string actorId = actor.Value ?? "<unknown>";
            string quest = string.IsNullOrWhiteSpace(questId) ? "<unknown>" : questId;
            string stat = string.IsNullOrWhiteSpace(status) ? "<unknown>" : status;
            string objective = string.IsNullOrWhiteSpace(objectiveId) ? "<none>" : objectiveId;
            string msg = string.IsNullOrWhiteSpace(message) ? "<none>" : message;
            string details = $"actor={actorId} quest={quest} status={stat} objective={objective} progress={progress}/{Math.Max(1, required)} message={msg}";
            if (!string.IsNullOrWhiteSpace(executionContext))
                details += " " + executionContext;
            Write("QUEST", details);
        }

        private void Write(string category, string message)
        {
            if (!_enabled)
            {
                return;
            }

            lock (_gate)
            {
                _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}|{category ?? "LOG"} {message ?? string.Empty}");
            }
        }

        private static string FormatThingId(ThingId thing)
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

        private static int Count<T>(T[] items)
        {
            return items?.Length ?? 0;
        }

        public void Dispose()
        {
            if (!_enabled)
            {
                return;
            }

            lock (_gate)
            {
                _writer?.Dispose();
            }
        }
    }
}
