using System;
using System.Collections.Generic;
using System.Threading;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;
using DataDrivenGoap.Persistence;

namespace DataDrivenGoap.Simulation
{
    public sealed class SkillProgressionSystem : ISkillProgression
    {
        private sealed class SkillEntry
        {
            public double Xp;
            public int Level;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<ThingId, Dictionary<string, SkillEntry>> _skills =
            new Dictionary<ThingId, Dictionary<string, SkillEntry>>();
        private IWorld _world;

        public SkillProgressionSystem(IWorld world = null)
        {
            Volatile.Write(ref _world, world);
        }

        public void AttachWorld(IWorld world)
        {
            Volatile.Write(ref _world, world);
            if (world == null)
                return;

            List<(ThingId actor, string skill, int level)> pending;
            lock (_gate)
            {
                pending = new List<(ThingId, string, int)>();
                foreach (var actorEntry in _skills)
                {
                    if (string.IsNullOrWhiteSpace(actorEntry.Key.Value))
                        continue;
                    foreach (var skillEntry in actorEntry.Value)
                    {
                        if (string.IsNullOrWhiteSpace(skillEntry.Key) || skillEntry.Value == null)
                            continue;
                        pending.Add((actorEntry.Key, skillEntry.Key, skillEntry.Value.Level));
                    }
                }
            }

            foreach (var update in pending)
                UpdateWorldAttribute(update.actor, update.skill, update.level);
        }

        public double GetSkillLevel(ThingId actor, string skillId)
        {
            if (string.IsNullOrWhiteSpace(actor.Value) || string.IsNullOrWhiteSpace(skillId))
                return 0.0;

            skillId = skillId.Trim();

            lock (_gate)
            {
                if (TryGetEntry(actor, skillId, createIfMissing: false, out var entry))
                    return entry.Level;

                double initialLevel = ResolveInitialLevel(actor, skillId);
                if (initialLevel <= 0.0)
                    return 0.0;

                entry = new SkillEntry
                {
                    Level = (int)Math.Max(0, Math.Floor(initialLevel + 1e-6)),
                    Xp = TotalXpForLevel((int)Math.Max(0, Math.Floor(initialLevel + 1e-6)))
                };

                GetOrCreateSkillMap(actor)[skillId] = entry;
                return entry.Level;
            }
        }

        public double GetSkillExperience(ThingId actor, string skillId)
        {
            if (string.IsNullOrWhiteSpace(actor.Value) || string.IsNullOrWhiteSpace(skillId))
                return 0.0;

            skillId = skillId.Trim();

            lock (_gate)
            {
                if (TryGetEntry(actor, skillId, createIfMissing: false, out var entry))
                    return entry.Xp;

                double initialLevel = ResolveInitialLevel(actor, skillId);
                if (initialLevel <= 0.0)
                    return 0.0;

                entry = new SkillEntry
                {
                    Level = (int)Math.Max(0, Math.Floor(initialLevel + 1e-6)),
                    Xp = TotalXpForLevel((int)Math.Max(0, Math.Floor(initialLevel + 1e-6)))
                };

                GetOrCreateSkillMap(actor)[skillId] = entry;
                return entry.Xp;
            }
        }

        public void AddExperience(ThingId actor, string skillId, double amount)
        {
            if (string.IsNullOrWhiteSpace(actor.Value))
                return;
            if (string.IsNullOrWhiteSpace(skillId))
                return;
            if (double.IsNaN(amount) || double.IsInfinity(amount) || Math.Abs(amount) < 1e-9)
                return;

            skillId = skillId.Trim();
            int? updatedLevel = null;

            lock (_gate)
            {
                if (!TryGetEntry(actor, skillId, createIfMissing: true, out var entry))
                    return;

                entry.Xp = Math.Max(0.0, entry.Xp + amount);
                int newLevel = CalculateLevel(entry.Xp);
                if (newLevel != entry.Level)
                {
                    entry.Level = newLevel;
                    updatedLevel = newLevel;
                }
            }

            if (updatedLevel.HasValue)
                UpdateWorldAttribute(actor, skillId, updatedLevel.Value);
        }

        public SkillProgressionState CaptureState()
        {
            var state = new SkillProgressionState();

            lock (_gate)
            {
                foreach (var actorEntry in _skills)
                {
                    if (string.IsNullOrWhiteSpace(actorEntry.Key.Value))
                        continue;

                    var actorState = new ActorSkillProgressState
                    {
                        actorId = actorEntry.Key.Value
                    };

                    foreach (var skillEntry in actorEntry.Value)
                    {
                        if (string.IsNullOrWhiteSpace(skillEntry.Key) || skillEntry.Value == null)
                            continue;

                        actorState.skills.Add(new SkillProgressState
                        {
                            skillId = skillEntry.Key,
                            xp = skillEntry.Value.Xp,
                            level = skillEntry.Value.Level
                        });
                    }

                    if (actorState.skills.Count > 0)
                        state.actors.Add(actorState);
                }
            }

            return state;
        }

        public void ApplyState(SkillProgressionState state)
        {
            var pendingUpdates = new List<(ThingId actor, string skill, int level)>();

            lock (_gate)
            {
                _skills.Clear();
                if (state?.actors == null)
                    return;

                foreach (var actorState in state.actors)
                {
                    if (actorState == null || string.IsNullOrWhiteSpace(actorState.actorId))
                        continue;

                    var actorId = new ThingId(actorState.actorId.Trim());
                    var skillMap = new Dictionary<string, SkillEntry>(StringComparer.OrdinalIgnoreCase);

                    IEnumerable<SkillProgressState> skills =
                        actorState.skills ?? (IEnumerable<SkillProgressState>)Array.Empty<SkillProgressState>();
                    foreach (var skill in skills)
                    {
                        if (skill == null || string.IsNullOrWhiteSpace(skill.skillId))
                            continue;

                        string skillName = skill.skillId.Trim();
                        double xp = Math.Max(0.0, skill.xp);
                        int level = Math.Max(0, skill.level);
                        int recalculated = CalculateLevel(xp);
                        if (level != recalculated)
                            level = recalculated;

                        skillMap[skillName] = new SkillEntry
                        {
                            Level = level,
                            Xp = Math.Max(xp, TotalXpForLevel(level))
                        };

                        pendingUpdates.Add((actorId, skillName, level));
                    }

                    if (skillMap.Count > 0)
                        _skills[actorId] = skillMap;
                }
            }

            foreach (var update in pendingUpdates)
                UpdateWorldAttribute(update.actor, update.skill, update.level);
        }

        private Dictionary<string, SkillEntry> GetOrCreateSkillMap(ThingId actor)
        {
            if (!_skills.TryGetValue(actor, out var map))
            {
                map = new Dictionary<string, SkillEntry>(StringComparer.OrdinalIgnoreCase);
                _skills[actor] = map;
            }

            return map;
        }

        private bool TryGetEntry(ThingId actor, string skillId, bool createIfMissing, out SkillEntry entry)
        {
            entry = null;
            if (!_skills.TryGetValue(actor, out var map))
            {
                if (!createIfMissing)
                    return false;
                map = new Dictionary<string, SkillEntry>(StringComparer.OrdinalIgnoreCase);
                _skills[actor] = map;
            }

            if (!map.TryGetValue(skillId, out entry))
            {
                if (!createIfMissing)
                    return false;

                double initialLevel = ResolveInitialLevel(actor, skillId);
                int level = (int)Math.Max(0, Math.Floor(initialLevel + 1e-6));
                entry = new SkillEntry
                {
                    Level = level,
                    Xp = TotalXpForLevel(level)
                };
                map[skillId] = entry;
            }

            return true;
        }

        private double ResolveInitialLevel(ThingId actor, string skillId)
        {
            var world = Volatile.Read(ref _world);
            if (world == null)
                return 0.0;

            var snap = world.Snap();
            var thing = snap?.GetThing(actor);
            if (thing?.Attributes == null)
                return 0.0;

            return thing.Attributes.TryGetValue(skillId, out var value) ? value : 0.0;
        }

        private static int CalculateLevel(double xp)
        {
            int level = 0;
            while (xp + 1e-6 >= RequiredXpForNextLevel(level))
            {
                level++;
            }

            return level;
        }

        private static double TotalXpForLevel(int level)
        {
            double xp = 0.0;
            for (int i = 0; i < level; i++)
                xp += RequiredXpForNextLevel(i);
            return xp;
        }

        private static double RequiredXpForNextLevel(int currentLevel)
        {
            int nextLevel = currentLevel + 1;
            return 10.0 * nextLevel * nextLevel;
        }

        private void UpdateWorldAttribute(ThingId actor, string skillId, int level)
        {
            var world = Volatile.Read(ref _world);
            if (world == null)
                return;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                var snapshot = world.Snap();
                var thing = snapshot?.GetThing(actor);
                if (thing == null)
                    return;

                double current = thing.AttrOrDefault(skillId, 0.0);
                if (Math.Abs(current - level) < 1e-6)
                    return;

                var batch = new EffectBatch
                {
                    BaseVersion = snapshot.Version,
                    Reads = new[] { new ReadSetEntry(actor, skillId, current) },
                    Writes = new[] { new WriteSetEntry(actor, skillId, level) },
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

                if (world.TryCommit(batch) == CommitResult.Committed)
                    return;
            }
        }
    }
}
