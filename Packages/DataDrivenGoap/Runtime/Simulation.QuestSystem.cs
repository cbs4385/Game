using System;
using System.Collections.Generic;
using System.Linq;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;
using DataDrivenGoap.Persistence;

namespace DataDrivenGoap.Simulation
{
    public readonly struct QuestOperationResult
    {
        public bool Success { get; }
        public bool StateChanged { get; }
        public bool RewardGranted { get; }
        public QuestStatus Status { get; }
        public string Message { get; }
        public string ObjectiveId { get; }
        public int ObjectiveProgress { get; }
        public int ObjectiveRequired { get; }
        public IReadOnlyList<InventoryDelta> InventoryChanges { get; }
        public IReadOnlyList<CurrencyDelta> CurrencyChanges { get; }

        public QuestOperationResult(
            bool success,
            bool stateChanged,
            bool rewardGranted,
            QuestStatus status,
            string message,
            string objectiveId,
            int objectiveProgress,
            int objectiveRequired,
            IReadOnlyList<InventoryDelta> inventoryChanges,
            IReadOnlyList<CurrencyDelta> currencyChanges)
        {
            Success = success;
            StateChanged = stateChanged;
            RewardGranted = rewardGranted;
            Status = status;
            Message = message ?? string.Empty;
            ObjectiveId = objectiveId ?? string.Empty;
            ObjectiveProgress = Math.Max(0, objectiveProgress);
            ObjectiveRequired = Math.Max(0, objectiveRequired);
            InventoryChanges = inventoryChanges ?? Array.Empty<InventoryDelta>();
            CurrencyChanges = currencyChanges ?? Array.Empty<CurrencyDelta>();
        }

        public static QuestOperationResult Failed => new QuestOperationResult(false, false, false, QuestStatus.Locked, string.Empty, string.Empty, 0, 0, Array.Empty<InventoryDelta>(), Array.Empty<CurrencyDelta>());
    }

    public sealed class QuestSystem : IQuestQuery
    {
        private sealed class QuestObjectiveDefinition
        {
            public string Id { get; }
            public int Required { get; }

            public QuestObjectiveDefinition(string id, int required)
            {
                Id = id ?? string.Empty;
                Required = Math.Max(1, required);
            }
        }

        private sealed class QuestRewardDefinition
        {
            public QuestRewardItemConfig[] Items { get; }
            public double Currency { get; }

            public QuestRewardDefinition(QuestRewardConfig config)
            {
                if (config?.items != null && config.items.Length > 0)
                {
                    Items = config.items
                        .Where(i => i != null && !string.IsNullOrWhiteSpace(i.itemId) && i.quantity > 0)
                        .Select(i => new QuestRewardItemConfig
                        {
                            itemId = i.itemId.Trim(),
                            quantity = Math.Max(1, i.quantity)
                        })
                        .ToArray();
                }
                else
                {
                    Items = Array.Empty<QuestRewardItemConfig>();
                }

                Currency = config?.currency ?? 0.0;
            }
        }

        private sealed class QuestDefinition
        {
            public string Id { get; }
            public string Title { get; }
            public string Description { get; }
            public string Giver { get; }
            public string[] Prerequisites { get; }
            public QuestObjectiveDefinition[] Objectives { get; }
            public QuestRewardDefinition Reward { get; }
            public bool AutoAccept { get; }

            public QuestDefinition(QuestConfig config)
            {
                if (config == null)
                    throw new ArgumentNullException(nameof(config));
                if (string.IsNullOrWhiteSpace(config.id))
                    throw new ArgumentException("Quest config must specify an id", nameof(config));

                Id = config.id.Trim();
                Title = config.title ?? string.Empty;
                Description = config.description ?? string.Empty;
                Giver = config.giver ?? string.Empty;
                Prerequisites = config.prerequisites?.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray() ?? Array.Empty<string>();
                Objectives = (config.objectives ?? Array.Empty<QuestObjectiveConfig>())
                    .Where(o => o != null)
                    .Select(o => new QuestObjectiveDefinition(o.id ?? string.Empty, o.requiredCount <= 0 ? 1 : o.requiredCount))
                    .ToArray();
                if (Objectives.Length == 0)
                    Objectives = new[] { new QuestObjectiveDefinition("default", 1) };
                Reward = new QuestRewardDefinition(config.reward);
                AutoAccept = config.autoAccept;
            }
        }

        private sealed class QuestProgress
        {
            public QuestStatus Status;
            public int ObjectiveIndex;
            public int Progress;
            public bool RewardsClaimed;
        }

        private readonly Dictionary<string, QuestDefinition> _definitions = new Dictionary<string, QuestDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ThingId, Dictionary<string, QuestProgress>> _actorQuests = new Dictionary<ThingId, Dictionary<string, QuestProgress>>();
        private readonly object _gate = new object();

        public QuestSystem(IEnumerable<QuestConfig> quests)
        {
            if (quests == null)
                return;

            foreach (var cfg in quests)
            {
                if (cfg == null)
                    continue;
                var def = new QuestDefinition(cfg);
                _definitions[def.Id] = def;
            }
        }

        public QuestOperationResult Apply(QuestOperation operation)
        {
            if (string.IsNullOrWhiteSpace(operation.QuestId))
                return QuestOperationResult.Failed;

            var questId = operation.QuestId.Trim();
            if (!_definitions.TryGetValue(questId, out var def))
                return QuestOperationResult.Failed;

            if (string.IsNullOrWhiteSpace(operation.Actor.Value))
                return QuestOperationResult.Failed;

            lock (_gate)
            {
                var actor = operation.Actor;
                var state = GetOrCreateState(actor, questId, createIfMissing: operation.Kind == QuestOperationKind.Accept);

                switch (operation.Kind)
                {
                    case QuestOperationKind.Accept:
                        return ApplyAccept(def, actor, questId, state);
                    case QuestOperationKind.Progress:
                        return ApplyProgress(def, actor, questId, state, Math.Max(1, operation.Amount));
                    case QuestOperationKind.ClaimRewards:
                        return ApplyClaim(def, actor, questId, state, operation.GrantRewards);
                    default:
                        return QuestOperationResult.Failed;
                }
            }
        }

        private QuestOperationResult ApplyAccept(QuestDefinition def, ThingId actor, string questId, QuestProgress state)
        {
            if (!PrerequisitesMet(actor, def))
                return QuestOperationResult.Failed;

            var currentStatus = ComputeStatus(actor, def, state);
            if (state != null && (state.Status == QuestStatus.Active || state.Status == QuestStatus.ReadyToTurnIn || state.Status == QuestStatus.Completed))
            {
                return new QuestOperationResult(false, false, false, currentStatus, "already_accept", string.Empty, 0, 0, Array.Empty<InventoryDelta>(), Array.Empty<CurrencyDelta>());
            }

            state = GetOrCreateState(actor, questId, createIfMissing: true);
            state.Status = QuestStatus.Active;
            state.ObjectiveIndex = 0;
            state.Progress = 0;
            state.RewardsClaimed = false;

            var firstObjective = def.Objectives[0];
            return new QuestOperationResult(true, true, false, QuestStatus.Active, "accepted", firstObjective.Id, 0, firstObjective.Required, Array.Empty<InventoryDelta>(), Array.Empty<CurrencyDelta>());
        }

        private QuestOperationResult ApplyProgress(QuestDefinition def, ThingId actor, string questId, QuestProgress state, int amount)
        {
            if (state == null || state.Status != QuestStatus.Active)
                return QuestOperationResult.Failed;

            int index = Math.Clamp(state.ObjectiveIndex, 0, def.Objectives.Length - 1);
            var objective = def.Objectives[index];

            int before = state.Progress;
            state.Progress = Math.Clamp(state.Progress + amount, 0, objective.Required);
            bool objectiveCompleted = state.Progress >= objective.Required;
            bool stateChanged = state.Progress != before;

            if (objectiveCompleted)
            {
                state.ObjectiveIndex++;
                if (state.ObjectiveIndex >= def.Objectives.Length)
                {
                    state.Status = QuestStatus.ReadyToTurnIn;
                    state.Progress = objective.Required;
                }
                else
                {
                    state.Progress = 0;
                }
            }

            var status = ComputeStatus(actor, def, state);
            var required = objective.Required;
            var progress = Math.Min(state.Progress, required);
            if (status == QuestStatus.ReadyToTurnIn)
            {
                progress = required;
            }

            return new QuestOperationResult(true, stateChanged || objectiveCompleted, false, status, objectiveCompleted ? "objective_complete" : "progress", objective.Id, progress, required, Array.Empty<InventoryDelta>(), Array.Empty<CurrencyDelta>());
        }

        private QuestOperationResult ApplyClaim(QuestDefinition def, ThingId actor, string questId, QuestProgress state, bool grantRewards)
        {
            if (state == null)
                return QuestOperationResult.Failed;

            if (state.Status != QuestStatus.ReadyToTurnIn && state.Status != QuestStatus.Completed)
                return QuestOperationResult.Failed;

            if (state.Status == QuestStatus.Completed && state.RewardsClaimed)
            {
                return new QuestOperationResult(false, false, false, QuestStatus.Completed, "already_completed", string.Empty, 0, 0, Array.Empty<InventoryDelta>(), Array.Empty<CurrencyDelta>());
            }

            var inventoryRewards = new List<InventoryDelta>();
            var currencyRewards = new List<CurrencyDelta>();
            bool rewardGranted = false;

            if (grantRewards)
            {
                foreach (var item in def.Reward.Items ?? Array.Empty<QuestRewardItemConfig>())
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.itemId) || item.quantity <= 0)
                        continue;
                    inventoryRewards.Add(new InventoryDelta(actor, item.itemId, item.quantity, remove: false));
                }

                if (def.Reward.Currency != 0.0)
                {
                    currencyRewards.Add(new CurrencyDelta(actor, def.Reward.Currency));
                }

                if (inventoryRewards.Count > 0 || currencyRewards.Count > 0)
                    rewardGranted = true;
            }

            var finalObjective = def.Objectives[def.Objectives.Length - 1];
            state.Status = QuestStatus.Completed;
            state.Progress = finalObjective.Required;
            state.ObjectiveIndex = def.Objectives.Length;
            state.RewardsClaimed = true;

            var status = ComputeStatus(actor, def, state);
            return new QuestOperationResult(true, true, rewardGranted, status, "completed", finalObjective.Id, finalObjective.Required, finalObjective.Required, inventoryRewards, currencyRewards);
        }

        private QuestProgress GetOrCreateState(ThingId actor, string questId, bool createIfMissing)
        {
            if (!_actorQuests.TryGetValue(actor, out var quests))
            {
                if (!createIfMissing)
                    return null;
                quests = new Dictionary<string, QuestProgress>(StringComparer.OrdinalIgnoreCase);
                _actorQuests[actor] = quests;
            }

            if (!quests.TryGetValue(questId, out var state) && createIfMissing)
            {
                state = new QuestProgress
                {
                    Status = QuestStatus.Available,
                    ObjectiveIndex = 0,
                    Progress = 0,
                    RewardsClaimed = false
                };
                quests[questId] = state;
            }

            return state;
        }

        private QuestProgress TryGetState(ThingId actor, string questId)
        {
            if (_actorQuests.TryGetValue(actor, out var quests) && quests.TryGetValue(questId, out var state))
                return state;
            return null;
        }

        private bool PrerequisitesMet(ThingId actor, QuestDefinition quest)
        {
            if (quest.Prerequisites == null || quest.Prerequisites.Length == 0)
                return true;

            foreach (var prereq in quest.Prerequisites)
            {
                if (string.IsNullOrWhiteSpace(prereq))
                    continue;
                if (!IsQuestCompleted(actor, prereq.Trim()))
                    return false;
            }
            return true;
        }

        private QuestStatus ComputeStatus(ThingId actor, QuestDefinition quest, QuestProgress state)
        {
            if (state == null)
                return PrerequisitesMet(actor, quest) ? QuestStatus.Available : QuestStatus.Locked;

            if (state.Status == QuestStatus.Completed)
                return QuestStatus.Completed;
            if (state.Status == QuestStatus.ReadyToTurnIn)
                return QuestStatus.ReadyToTurnIn;
            if (state.Status == QuestStatus.Active)
                return QuestStatus.Active;

            return PrerequisitesMet(actor, quest) ? QuestStatus.Available : QuestStatus.Locked;
        }

        public QuestStatus GetStatus(ThingId actor, string questId)
        {
            if (string.IsNullOrWhiteSpace(questId))
                return QuestStatus.Locked;
            questId = questId.Trim();

            lock (_gate)
            {
                if (!_definitions.TryGetValue(questId, out var def))
                    return QuestStatus.Locked;
                var state = TryGetState(actor, questId);
                return ComputeStatus(actor, def, state);
            }
        }

        public QuestObjectiveProgress GetObjectiveProgress(ThingId actor, string questId, string objectiveId)
        {
            if (string.IsNullOrWhiteSpace(questId))
                return default;

            questId = questId.Trim();
            lock (_gate)
            {
                if (!_definitions.TryGetValue(questId, out var def))
                    return default;

                var state = TryGetState(actor, questId);
                var status = ComputeStatus(actor, def, state);

                int index = -1;
                if (!string.IsNullOrWhiteSpace(objectiveId))
                {
                    var targetId = objectiveId.Trim();
                    index = Array.FindIndex(def.Objectives, o => string.Equals(o.Id, targetId, StringComparison.OrdinalIgnoreCase));
                    if (index < 0)
                        return default;
                }
                else
                {
                    index = Math.Clamp(state?.ObjectiveIndex ?? 0, 0, def.Objectives.Length - 1);
                }

                var objective = def.Objectives[index];
                bool completed = false;
                bool isCurrent = false;
                int progress = 0;

                if (state != null)
                {
                    if (state.Status == QuestStatus.Completed)
                    {
                        completed = true;
                        progress = objective.Required;
                    }
                    else if (state.Status == QuestStatus.ReadyToTurnIn)
                    {
                        if (index >= def.Objectives.Length - 1)
                        {
                            completed = true;
                            progress = objective.Required;
                        }
                        else if (index < def.Objectives.Length - 1)
                        {
                            completed = index < state.ObjectiveIndex;
                            progress = completed ? objective.Required : 0;
                        }
                    }
                    else if (index < state.ObjectiveIndex)
                    {
                        completed = true;
                        progress = objective.Required;
                    }
                    else if (index == state.ObjectiveIndex)
                    {
                        progress = Math.Clamp(state.Progress, 0, objective.Required);
                        completed = progress >= objective.Required;
                        isCurrent = state.Status == QuestStatus.Active;
                    }
                }
                else
                {
                    completed = status == QuestStatus.Completed;
                    progress = completed ? objective.Required : 0;
                    isCurrent = status == QuestStatus.Active && index == 0;
                }

                return new QuestObjectiveProgress(true, questId, objective.Id, progress, objective.Required, completed, isCurrent);
            }
        }

        public bool IsQuestAvailable(ThingId actor, string questId)
        {
            return GetStatus(actor, questId) == QuestStatus.Available;
        }

        public bool IsQuestActive(ThingId actor, string questId)
        {
            return GetStatus(actor, questId) == QuestStatus.Active;
        }

        public bool IsQuestReadyToTurnIn(ThingId actor, string questId)
        {
            return GetStatus(actor, questId) == QuestStatus.ReadyToTurnIn;
        }

        public bool IsQuestCompleted(ThingId actor, string questId)
        {
            return GetStatus(actor, questId) == QuestStatus.Completed;
        }

        public bool IsObjectiveActive(ThingId actor, string questId, string objectiveId)
        {
            if (string.IsNullOrWhiteSpace(objectiveId))
                return false;
            var progress = GetObjectiveProgress(actor, questId, objectiveId);
            return progress.Exists && progress.IsCurrent;
        }

        public QuestSystemState CaptureState()
        {
            var state = new QuestSystemState();
            lock (_gate)
            {
                foreach (var actorEntry in _actorQuests)
                {
                    if (string.IsNullOrWhiteSpace(actorEntry.Key.Value))
                        continue;

                    var actorState = new QuestActorState
                    {
                        actorId = actorEntry.Key.Value
                    };

                    foreach (var questEntry in actorEntry.Value)
                    {
                        if (string.IsNullOrWhiteSpace(questEntry.Key) || questEntry.Value == null)
                            continue;

                        actorState.quests.Add(new QuestStateData
                        {
                            questId = questEntry.Key,
                            status = questEntry.Value.Status.ToString(),
                            objectiveIndex = questEntry.Value.ObjectiveIndex,
                            progress = questEntry.Value.Progress,
                            rewardsClaimed = questEntry.Value.RewardsClaimed
                        });
                    }

                    if (actorState.quests.Count > 0)
                        state.actors.Add(actorState);
                }
            }
            return state;
        }

        public void ApplyState(QuestSystemState state)
        {
            lock (_gate)
            {
                _actorQuests.Clear();
                if (state?.actors == null)
                    return;

                foreach (var actorState in state.actors)
                {
                    if (actorState == null || string.IsNullOrWhiteSpace(actorState.actorId))
                        continue;

                    var actorId = new ThingId(actorState.actorId.Trim());
                    var questMap = new Dictionary<string, QuestProgress>(StringComparer.OrdinalIgnoreCase);

                    if (actorState.quests == null)
                        continue;

                    foreach (var quest in actorState.quests)
                    {
                        if (quest == null || string.IsNullOrWhiteSpace(quest.questId))
                            continue;

                        if (!Enum.TryParse<QuestStatus>(quest.status, true, out var status))
                            status = QuestStatus.Active;

                        questMap[quest.questId.Trim()] = new QuestProgress
                        {
                            Status = status,
                            ObjectiveIndex = Math.Max(0, quest.objectiveIndex),
                            Progress = Math.Max(0, quest.progress),
                            RewardsClaimed = quest.rewardsClaimed
                        };
                    }

                    if (questMap.Count > 0)
                        _actorQuests[actorId] = questMap;
                }
            }
        }
    }
}
