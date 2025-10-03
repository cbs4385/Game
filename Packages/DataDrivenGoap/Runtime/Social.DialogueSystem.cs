using System;
using System.Collections.Generic;
using System.Linq;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;
using DataDrivenGoap.Items;
using DataDrivenGoap.Simulation;

namespace DataDrivenGoap.Social
{
    public sealed class DialogueNode
    {
        public string Id { get; }
        public string Text { get; }
        internal DialogueConditionConfig[] Conditions { get; }
        internal DialogueOutcomeConfig[] Outcomes { get; }
        public double? CooldownSeconds { get; }

        public DialogueNode(DialogueNodeConfig cfg)
        {
            if (cfg == null)
                throw new ArgumentNullException(nameof(cfg));
            if (string.IsNullOrWhiteSpace(cfg.id))
                throw new InvalidOperationException("Dialogue nodes must specify a non-empty id.");
            if (string.IsNullOrWhiteSpace(cfg.text))
                throw new InvalidOperationException($"Dialogue node '{cfg.id}' must specify dialogue text.");
            if (cfg.conditions == null)
                throw new InvalidOperationException($"Dialogue node '{cfg.id}' must include a conditions array, even if empty.");
            if (cfg.outcomes == null)
                throw new InvalidOperationException($"Dialogue node '{cfg.id}' must include an outcomes array, even if empty.");

            Id = cfg.id;
            Text = cfg.text;
            Conditions = cfg.conditions;
            Outcomes = cfg.outcomes;
            double? cooldown = cfg.cooldownSeconds;
            if (cooldown.HasValue)
            {
                double value = cooldown.Value;
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                    cooldown = null;
            }
            CooldownSeconds = cooldown;
        }
    }

    public sealed class DialogueSystem
    {
        private readonly List<DialogueNode> _nodes;
        private readonly SocialRelationshipSystem _relationships;
        private readonly InventorySystem _inventory;
        private readonly ICalendarEventQuery _events;
        private readonly IWeatherQuery _weather;
        private readonly IWorld _world;

        public DialogueSystem(
            IEnumerable<DialogueNodeConfig> configs,
            SocialRelationshipSystem relationships,
            InventorySystem inventory,
            ICalendarEventQuery events,
            IWeatherQuery weather,
            IWorld world = null)
        {
            _nodes = new List<DialogueNode>();
            if (configs != null)
            {
                foreach (var cfg in configs)
                {
                    if (cfg == null)
                        throw new InvalidOperationException("Dialogue configuration entries cannot be null.");
                    _nodes.Add(new DialogueNode(cfg));
                }
            }
            _relationships = relationships;
            _inventory = inventory;
            _events = events;
            _weather = weather;
            _world = world;
        }

        public IReadOnlyList<DialogueNode> Nodes => _nodes;

        public IEnumerable<DialogueNode> GetAvailableDialogue(IWorldSnapshot snapshot, ThingId speaker, ThingId listener)
        {
            if (snapshot == null)
                return Array.Empty<DialogueNode>();

            var available = new List<DialogueNode>();
            foreach (var node in _nodes)
            {
                if (node == null)
                    continue;
                if (IsOnCooldown(snapshot, speaker, listener, node))
                    continue;
                if (node.Conditions == null || node.Conditions.Length == 0 || node.Conditions.All(c => EvaluateCondition(snapshot, speaker, listener, c)))
                    available.Add(node);
            }
            return available;
        }

        public DialogueExecutionResult ExecuteNode(
            IWorldSnapshot snapshot,
            ThingId speaker,
            ThingId listener,
            DialogueNode node,
            double? overrideCooldownSeconds = null)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            if (node == null)
                throw new ArgumentNullException(nameof(node));

            bool onCooldown = IsOnCooldown(snapshot, speaker, listener, node);
            if (onCooldown)
            {
                return DialogueExecutionResult.OnCooldown(node);
            }

            bool conditionsMet = node.Conditions == null
                || node.Conditions.Length == 0
                || node.Conditions.All(c => EvaluateCondition(snapshot, speaker, listener, c));
            if (!conditionsMet)
            {
                return DialogueExecutionResult.ConditionsNotMet(node);
            }

            var relationshipChanges = new List<DialogueExecutionResult.RelationshipChange>();
            var itemGrants = new List<DialogueExecutionResult.ItemGrant>();
            var factChanges = new List<DialogueExecutionResult.FactChange>();
            var triggeredEvents = new List<string>();

            foreach (var outcome in node.Outcomes ?? Array.Empty<DialogueOutcomeConfig>())
            {
                if (outcome == null)
                    continue;

                var type = outcome.type?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(type))
                    continue;

                switch (type)
                {
                    case "relationship":
                    case "relationship_adjust":
                    case "relationship_change":
                    case "relationship_delta":
                    {
                        if (_relationships == null || string.IsNullOrWhiteSpace(outcome.relationship))
                            break;
                        double amount = outcome.amount ?? 0.0;
                        if (double.IsNaN(amount) || double.IsInfinity(amount) || Math.Abs(amount) < 1e-9)
                            break;
                        var from = ResolveOrDefault(outcome.factSubject, speaker, speaker, listener);
                        var to = ResolveOrDefault(outcome.factObject, listener, speaker, listener);
                        double before = double.NaN;
                        try
                        {
                            before = _relationships.GetRelationship(snapshot, from, to, outcome.relationship.Trim());
                        }
                        catch
                        {
                            before = double.NaN;
                        }
                        double after = _relationships.AdjustRelationship(from, to, outcome.relationship.Trim(), amount);
                        double delta = double.IsNaN(before) ? amount : after - before;
                        relationshipChanges.Add(new DialogueExecutionResult.RelationshipChange(from, to, outcome.relationship.Trim(), delta, after));
                        break;
                    }
                    case "relationship_set":
                    case "relationship_assign":
                    {
                        if (_relationships == null || string.IsNullOrWhiteSpace(outcome.relationship) || !outcome.amount.HasValue)
                            break;
                        double target = outcome.amount.Value;
                        if (double.IsNaN(target) || double.IsInfinity(target))
                            break;
                        var from = ResolveOrDefault(outcome.factSubject, speaker, speaker, listener);
                        var to = ResolveOrDefault(outcome.factObject, listener, speaker, listener);
                        double before = double.NaN;
                        try
                        {
                            before = _relationships.GetRelationship(snapshot, from, to, outcome.relationship.Trim());
                        }
                        catch
                        {
                            before = double.NaN;
                        }
                        double after = _relationships.SetRelationship(from, to, outcome.relationship.Trim(), target);
                        double delta = double.IsNaN(before) ? target : after - before;
                        relationshipChanges.Add(new DialogueExecutionResult.RelationshipChange(from, to, outcome.relationship.Trim(), delta, after));
                        break;
                    }
                    case "inventory_add":
                    case "give_item":
                    case "item_reward":
                    {
                        if (_inventory == null || string.IsNullOrWhiteSpace(outcome.item))
                            break;
                        int quantity = Math.Max(1, outcome.quantity ?? 1);
                        var recipient = ResolveOrDefault(outcome.factSubject, listener, speaker, listener);
                        if (!IsValidThing(recipient))
                            break;
                        int granted = _inventory.AddItem(recipient, outcome.item.Trim(), quantity);
                        if (granted > 0)
                            itemGrants.Add(new DialogueExecutionResult.ItemGrant(recipient, outcome.item.Trim(), granted));
                        break;
                    }
                    case "set_fact":
                    case "add_fact":
                    case "fact_add":
                    case "fact_set":
                    {
                        var predicate = outcome.fact?.Trim();
                        if (string.IsNullOrWhiteSpace(predicate))
                            break;
                        var subject = ResolveOrDefault(outcome.factSubject, speaker, speaker, listener);
                        var obj = ResolveOrDefault(outcome.factObject, listener, speaker, listener);
                        if (!IsValidThing(subject) || !IsValidThing(obj))
                            break;
                        if (TryApplyFact(predicate, subject, obj, true))
                            factChanges.Add(new DialogueExecutionResult.FactChange(predicate, subject, obj, true));
                        break;
                    }
                    case "clear_fact":
                    case "remove_fact":
                    case "unset_fact":
                    {
                        var predicate = outcome.fact?.Trim();
                        if (string.IsNullOrWhiteSpace(predicate))
                            break;
                        var subject = ResolveOrDefault(outcome.factSubject, speaker, speaker, listener);
                        var obj = ResolveOrDefault(outcome.factObject, listener, speaker, listener);
                        if (!IsValidThing(subject) || !IsValidThing(obj))
                            break;
                        if (TryApplyFact(predicate, subject, obj, false))
                            factChanges.Add(new DialogueExecutionResult.FactChange(predicate, subject, obj, false));
                        break;
                    }
                    case "trigger_event":
                    case "start_event":
                    case "event":
                    {
                        if (_events == null)
                            break;
                        string eventId = outcome.eventId ?? outcome.fact ?? outcome.item;
                        eventId = eventId?.Trim();
                        if (string.IsNullOrWhiteSpace(eventId))
                            break;
                        if (_events.TryStartEvent(eventId))
                            triggeredEvents.Add(eventId);
                        break;
                    }
                }
            }

            double appliedCooldown = ApplyCooldown(speaker, listener, node, overrideCooldownSeconds);
            return new DialogueExecutionResult(
                node,
                true,
                false,
                relationshipChanges.ToArray(),
                itemGrants.ToArray(),
                factChanges.ToArray(),
                triggeredEvents.ToArray(),
                appliedCooldown);
        }

        private static ThingId ResolveOrDefault(string token, ThingId fallback, ThingId speaker, ThingId listener)
        {
            var resolved = ResolveParticipant(token, speaker, listener);
            return IsValidThing(resolved) ? resolved : fallback;
        }

        private static bool IsValidThing(ThingId id)
        {
            return !string.IsNullOrEmpty(id.Value);
        }

        private bool IsOnCooldown(IWorldSnapshot snapshot, ThingId speaker, ThingId listener, DialogueNode node)
        {
            if (snapshot == null || node == null || !IsValidThing(speaker))
                return false;

            string key = BuildCooldownAttributeKey(node, listener);
            if (string.IsNullOrEmpty(key))
                return false;

            var speakerThing = snapshot.GetThing(speaker);
            if (speakerThing?.Attributes == null)
                return false;

            if (!speakerThing.Attributes.TryGetValue(key, out var expires))
                return false;

            if (double.IsNaN(expires) || double.IsInfinity(expires))
                return false;

            double now = snapshot.Time?.TotalWorldSeconds ?? 0.0;
            return expires > now + 1e-6;
        }

        private double ApplyCooldown(ThingId speaker, ThingId listener, DialogueNode node, double? overrideCooldownSeconds)
        {
            if (_world == null || node == null || !IsValidThing(speaker))
                return 0.0;

            double seconds = DetermineCooldownSeconds(node, overrideCooldownSeconds);
            if (seconds <= 1e-6)
                return 0.0;

            string key = BuildCooldownAttributeKey(node, listener);
            if (string.IsNullOrEmpty(key))
                return 0.0;

            for (int attempt = 0; attempt < 8; attempt++)
            {
                var snap = _world.Snap();
                var speakerThing = snap.GetThing(speaker);
                double? prior = null;
                if (speakerThing?.Attributes != null && speakerThing.Attributes.TryGetValue(key, out var existing))
                    prior = existing;

                double now = snap.Time?.TotalWorldSeconds ?? 0.0;
                double target = now + seconds;
                if (prior.HasValue && Math.Abs(prior.Value - target) < 1e-6)
                    return seconds;

                var writes = new[] { new WriteSetEntry(speaker, key, target) };
                var reads = prior.HasValue ? new[] { new ReadSetEntry(speaker, key, prior.Value) } : Array.Empty<ReadSetEntry>();
                var batch = new EffectBatch
                {
                    BaseVersion = snap.Version,
                    Reads = reads,
                    Writes = writes,
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
                    FishingOps = Array.Empty<FishingOperation>(),
                    ForagingOps = Array.Empty<ForagingOperation>()
                };

                if (_world.TryCommit(batch) == CommitResult.Committed)
                    return seconds;
            }

            return 0.0;
        }

        private static double DetermineCooldownSeconds(DialogueNode node, double? overrideCooldownSeconds)
        {
            if (overrideCooldownSeconds.HasValue)
            {
                double value = overrideCooldownSeconds.Value;
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
                    throw new InvalidOperationException("Override cooldown must be a positive, finite value.");
                return value;
            }

            if (node?.CooldownSeconds.HasValue == true)
            {
                double value = node.CooldownSeconds.Value;
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
                    throw new InvalidOperationException($"Dialogue node '{node.Id}' has an invalid cooldown value.");
                return value;
            }

            throw new InvalidOperationException($"Dialogue node '{node?.Id ?? "<unknown>"}' must define a cooldown or provide an override.");
        }

        private static string BuildCooldownAttributeKey(DialogueNode node, ThingId listener)
        {
            string nodeId = node?.Id ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nodeId))
                return string.Empty;

            string listenerId = listener.Value ?? string.Empty;
            return string.IsNullOrWhiteSpace(listenerId)
                ? $"dialogue.cooldown.{nodeId}"
                : $"dialogue.cooldown.{nodeId}.{listenerId}";
        }

        private bool TryApplyFact(string predicate, ThingId subject, ThingId obj, bool add)
        {
            if (_world == null || string.IsNullOrWhiteSpace(predicate))
                return false;

            var snap = _world.Snap();
            var delta = new FactDelta { Pred = predicate.Trim(), A = subject, B = obj, Add = add };
            var batch = new EffectBatch
            {
                BaseVersion = snap.Version,
                FactDeltas = new[] { delta },
                Reads = Array.Empty<ReadSetEntry>(),
                Writes = Array.Empty<WriteSetEntry>(),
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

            return _world.TryCommit(batch) == CommitResult.Committed;
        }

        private bool EvaluateCondition(IWorldSnapshot snapshot, ThingId speaker, ThingId listener, DialogueConditionConfig condition)
        {
            if (condition == null)
                return true;

            var type = condition.type?.Trim().ToLowerInvariant();
            switch (type)
            {
                case null:
                case "":
                    return true;
                case "relationship_min":
                case "relationship_at_least":
                    if (_relationships == null || string.IsNullOrWhiteSpace(condition.relationship))
                        return false;
                    double minValue = condition.min ?? 0.0;
                    double value = _relationships.GetRelationship(snapshot, speaker, listener, condition.relationship.Trim());
                    return value >= minValue;
                case "event_active":
                    if (_events == null || string.IsNullOrWhiteSpace(condition.eventId))
                        return false;
                    return _events.IsEventActive(condition.eventId.Trim());
                case "weather_is":
                    if (_weather == null || string.IsNullOrWhiteSpace(condition.weather))
                        return false;
                    return _weather.IsWeather(condition.weather.Trim());
                case "time_between":
                    if (snapshot.Time == null)
                        return false;
                    double hour = snapshot.Time.TimeOfDay.TotalHours;
                    double start = condition.startHour ?? 0.0;
                    double end = condition.endHour ?? 24.0;
                    return CalendarEventSystem_IsWithin(hour, start, end);
                case "has_fact":
                    if (string.IsNullOrWhiteSpace(condition.fact))
                        return false;
                    var a = ResolveParticipant(condition.factSubject, speaker, listener);
                    var b = ResolveParticipant(condition.factObject, speaker, listener);
                    return snapshot.HasFact(condition.fact.Trim(), a, b);
                case "inventory_has":
                    if (_inventory == null || string.IsNullOrWhiteSpace(condition.item))
                        return false;
                    int quantity = Math.Max(1, condition.quantity ?? 1);
                    return _inventory.Count(speaker, condition.item.Trim()) >= quantity;
                default:
                    return false;
            }
        }

        private static ThingId ResolveParticipant(string token, ThingId speaker, ThingId listener)
        {
            if (string.IsNullOrWhiteSpace(token))
                return default;
            var normalized = token.Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "$self":
                case "speaker":
                    return speaker;
                case "$listener":
                case "$other":
                case "listener":
                    return listener;
                case "$world":
                    return new ThingId("$world");
                default:
                    return new ThingId(token.Trim());
            }
        }

        private static bool CalendarEventSystem_IsWithin(double hour, double start, double end)
        {
            double Clamp(double value)
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                    return 0.0;
                if (value < 0.0)
                    return 0.0;
                if (value > 24.0)
                    return 24.0;
                return value;
            }

            hour = Clamp(hour);
            start = Clamp(start);
            end = Clamp(end);
            if (Math.Abs(end - start) < 1e-6)
                return true;
            if (start <= end)
                return hour >= start && hour < end;
            return hour >= start || hour < end;
        }
    }

    public sealed class DialogueExecutionResult
    {
        private static readonly RelationshipChange[] EmptyRelationships = Array.Empty<RelationshipChange>();
        private static readonly ItemGrant[] EmptyItems = Array.Empty<ItemGrant>();
        private static readonly FactChange[] EmptyFacts = Array.Empty<FactChange>();

        internal DialogueExecutionResult(
            DialogueNode node,
            bool conditionsSatisfied,
            bool wasOnCooldown,
            IReadOnlyList<RelationshipChange> relationshipChanges,
            IReadOnlyList<ItemGrant> itemGrants,
            IReadOnlyList<FactChange> factChanges,
            IReadOnlyList<string> triggeredEvents,
            double cooldownSeconds)
        {
            Node = node;
            ConditionsSatisfied = conditionsSatisfied;
            WasOnCooldown = wasOnCooldown;
            RelationshipChanges = relationshipChanges ?? EmptyRelationships;
            ItemGrants = itemGrants ?? EmptyItems;
            FactChanges = factChanges ?? EmptyFacts;
            TriggeredEvents = triggeredEvents ?? Array.Empty<string>();
            CooldownSeconds = cooldownSeconds;
        }

        public DialogueNode Node { get; }
        public bool ConditionsSatisfied { get; }
        public bool WasOnCooldown { get; }
        public IReadOnlyList<RelationshipChange> RelationshipChanges { get; }
        public IReadOnlyList<ItemGrant> ItemGrants { get; }
        public IReadOnlyList<FactChange> FactChanges { get; }
        public IReadOnlyList<string> TriggeredEvents { get; }
        public double CooldownSeconds { get; }
        public bool AnyChanges => RelationshipChanges.Count > 0 || ItemGrants.Count > 0 || FactChanges.Count > 0 || TriggeredEvents.Count > 0;

        internal static DialogueExecutionResult OnCooldown(DialogueNode node)
        {
            return new DialogueExecutionResult(node, false, true, EmptyRelationships, EmptyItems, EmptyFacts, Array.Empty<string>(), 0.0);
        }

        internal static DialogueExecutionResult ConditionsNotMet(DialogueNode node)
        {
            return new DialogueExecutionResult(node, false, false, EmptyRelationships, EmptyItems, EmptyFacts, Array.Empty<string>(), 0.0);
        }

        public readonly struct RelationshipChange
        {
            public RelationshipChange(ThingId from, ThingId to, string relationshipId, double delta, double newValue)
            {
                From = from;
                To = to;
                RelationshipId = relationshipId ?? string.Empty;
                Delta = delta;
                NewValue = newValue;
            }

            public ThingId From { get; }
            public ThingId To { get; }
            public string RelationshipId { get; }
            public double Delta { get; }
            public double NewValue { get; }
        }

        public readonly struct ItemGrant
        {
            public ItemGrant(ThingId recipient, string itemId, int quantity)
            {
                Recipient = recipient;
                ItemId = itemId ?? string.Empty;
                Quantity = quantity;
            }

            public ThingId Recipient { get; }
            public string ItemId { get; }
            public int Quantity { get; }
        }

        public readonly struct FactChange
        {
            public FactChange(string predicate, ThingId subject, ThingId obj, bool added)
            {
                Predicate = predicate ?? string.Empty;
                Subject = subject;
                Object = obj;
                Added = added;
            }

            public string Predicate { get; }
            public ThingId Subject { get; }
            public ThingId Object { get; }
            public bool Added { get; }
        }
    }
}
