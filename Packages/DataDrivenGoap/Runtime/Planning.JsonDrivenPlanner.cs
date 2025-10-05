using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;
using DataDrivenGoap.Expressions;
using DataDrivenGoap.Simulation;
using DataDrivenGoap.Social;
using DataDrivenGoap.Items;

namespace DataDrivenGoap.Planning
{
    public sealed class JsonDrivenPlanner : IPlanner
    {
        private sealed class ActionModel
        {
            public string Id { get; }
            public ActionConfig Config { get; }
            public SafeExpr DurationExpr { get; }
            public SafeExpr CostExpr { get; }
            public SafeExpr[] Preconditions { get; }
            public EffectOp[] Effects { get; }

            public ActionModel(ActionConfig config)
            {
                Config = config ?? throw new ArgumentNullException(nameof(config));
                if (string.IsNullOrWhiteSpace(config.id))
                    throw new ArgumentException("Action id must be provided.", nameof(config));

                Id = config.id;
                if (!string.IsNullOrWhiteSpace(config.duration))
                    DurationExpr = SafeExpr.Compile(config.duration);
                if (!string.IsNullOrWhiteSpace(config.cost))
                    CostExpr = SafeExpr.Compile(config.cost);
                Preconditions = (config.pre ?? Array.Empty<string>())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(SafeExpr.Compile)
                    .ToArray();
                Effects = CompileEffects(config.effects);
            }
        }

        private abstract class EffectOp
        {
            protected SafeExpr Condition { get; }

            protected EffectOp(SafeExpr condition)
            {
                Condition = condition;
            }

            protected bool ShouldApply(EvalContext ctx)
            {
                if (Condition == null) return true;
                try { return Condition.EvalBool(ctx); }
                catch { return false; }
            }

            public abstract void Apply(
                IWorldSnapshot snap,
                EvalContext ctx,
                ThingId self,
                ThingId target,
                List<WriteSetEntry> writes,
                List<FactDelta> facts,
                List<ThingSpawnRequest> spawns,
                List<PlanCooldownRequest> planCooldowns,
                List<ThingId> despawns,
                List<InventoryDelta> inventoryOps,
                List<CurrencyDelta> currencyOps,
                List<ShopTransaction> shopTransactions,
                List<RelationshipDelta> relationshipOps,
                List<CropOperation> cropOps,
                List<AnimalOperation> animalOps,
                List<MiningOperation> miningOps,
                List<FishingOperation> fishingOps,
                List<ForagingOperation> foragingOps,
                List<QuestOperation> questOps,
                HashSet<ThingId> reads);
        }

        private readonly struct TargetRef
        {
            private readonly string _text;

            public TargetRef(string text)
            {
                _text = text;
            }

            public bool TryResolve(ThingId self, ThingId target, out ThingId resolved)
            {
                var txt = _text;
                if (string.IsNullOrWhiteSpace(txt) || string.Equals(txt, "$self", StringComparison.OrdinalIgnoreCase))
                {
                    resolved = self;
                    return true;
                }

                if (string.Equals(txt, "$target", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(target.Value))
                    {
                        resolved = default;
                        return false;
                    }
                    resolved = target;
                    return true;
                }

                resolved = new ThingId(txt);
                return true;
            }

            public ThingId ResolveOrDefault(ThingId self, ThingId target, ThingId fallback)
            {
                return TryResolve(self, target, out var resolved) ? resolved : fallback;
            }
        }

        private sealed class WriteAttrEffect : EffectOp
        {
            private enum Operation { Add, Set }

            private readonly TargetRef _target;
            private readonly string _attribute;
            private readonly Operation _op;
            private readonly SafeExpr _valueExpr;
            private readonly SafeExpr _defaultExpr;
            private readonly bool _clamp01;

            public WriteAttrEffect(EffectConfig cfg, SafeExpr condition)
                : base(condition)
            {
                string targetText = !string.IsNullOrWhiteSpace(cfg?.target) ? cfg.target : cfg?.who;
                string attrText = !string.IsNullOrWhiteSpace(cfg?.attr) ? cfg.attr : cfg?.name;
                _target = new TargetRef(targetText);
                _attribute = attrText ?? string.Empty;
                _op = ParseOperation(cfg);
                JsonElement valueElement = cfg?.value ?? default;
                JsonElement defaultElement = cfg?.defaultValue ?? default;
                _valueExpr = CompileExpr(valueElement);
                _defaultExpr = CompileExpr(defaultElement);
                bool clamp = cfg?.clamp01 ?? true;
                if (cfg?.clamp.HasValue ?? false)
                    clamp = cfg.clamp.Value;
                if (!string.IsNullOrEmpty(_attribute) && _attribute.StartsWith("@", StringComparison.Ordinal))
                    clamp = false;
                _clamp01 = clamp;
            }

            public override void Apply(IWorldSnapshot snap, EvalContext ctx, ThingId self, ThingId target, List<WriteSetEntry> writes, List<FactDelta> facts, List<ThingSpawnRequest> spawns, List<PlanCooldownRequest> planCooldowns, List<ThingId> despawns, List<InventoryDelta> inventoryOps, List<CurrencyDelta> currencyOps, List<ShopTransaction> shopTransactions, List<RelationshipDelta> relationshipOps, List<CropOperation> cropOps, List<AnimalOperation> animalOps, List<MiningOperation> miningOps, List<FishingOperation> fishingOps, List<ForagingOperation> foragingOps, List<QuestOperation> questOps, HashSet<ThingId> reads)
            {
                if (snap == null || ctx == null) return;
                if (string.IsNullOrEmpty(_attribute)) return;
                if (!ShouldApply(ctx)) return;
                if (!_target.TryResolve(self, target, out var resolved)) return;

                reads?.Add(resolved);

                double defaultVal = EvaluateDefault(ctx);
                double current = defaultVal;
                var view = snap.GetThing(resolved);
                if (view != null)
                    current = view.AttrOrDefault(_attribute, defaultVal);

                double value = EvaluateNumber(_valueExpr, ctx, 0.0);
                double result = _op == Operation.Add ? current + value : value;
                if (_clamp01)
                    result = Clamp01(result);
                if (double.IsNaN(result) || double.IsInfinity(result))
                    result = current;

                writes?.Add(new WriteSetEntry(resolved, _attribute, result));
            }

            private double EvaluateDefault(EvalContext ctx)
            {
                double fallback = 0.0;
                if (!string.IsNullOrEmpty(_attribute) && string.Equals(_attribute, "health", StringComparison.OrdinalIgnoreCase))
                    fallback = 1.0;
                var expr = _defaultExpr;
                if (expr == null) return fallback;
                try
                {
                    var v = expr.EvalNumber(ctx);
                    if (double.IsNaN(v) || double.IsInfinity(v)) return fallback;
                    return v;
                }
                catch
                {
                    return fallback;
                }
            }

            private static Operation ParseOperation(EffectConfig cfg)
            {
                var op = cfg?.op;
                if (!string.IsNullOrWhiteSpace(op))
                {
                    op = op.Trim();
                    if (op.Equals("set", StringComparison.OrdinalIgnoreCase) || op.Equals("attr_set", StringComparison.OrdinalIgnoreCase) || op.Equals("set_attr", StringComparison.OrdinalIgnoreCase))
                        return Operation.Set;
                    if (op.Equals("attr_add", StringComparison.OrdinalIgnoreCase) || op.Equals("add_attr", StringComparison.OrdinalIgnoreCase) || op.Equals("add", StringComparison.OrdinalIgnoreCase))
                        return Operation.Add;
                }
                return Operation.Add;
            }
        }

        private sealed class WriteFactEffect : EffectOp
        {
            private readonly string _predicate;
            private readonly TargetRef _a;
            private readonly string _bText;
            private readonly bool _defaultAdd;
            private readonly SafeExpr _valueExpr;

            public WriteFactEffect(EffectConfig cfg, SafeExpr condition)
                : base(condition)
            {
                _predicate = cfg?.pred;
                _a = new TargetRef(cfg?.a);
                _bText = cfg?.b;
                bool add = true;
                var op = cfg?.op;
                if (!string.IsNullOrWhiteSpace(op))
                {
                    op = op.Trim();
                    if (op.Equals("remove", StringComparison.OrdinalIgnoreCase) || op.Equals("fact_remove", StringComparison.OrdinalIgnoreCase))
                        add = false;
                }
                _defaultAdd = add;
                JsonElement valueElement = cfg?.value ?? default;
                _valueExpr = CompileExpr(valueElement);
            }

            public override void Apply(IWorldSnapshot snap, EvalContext ctx, ThingId self, ThingId target, List<WriteSetEntry> writes, List<FactDelta> facts, List<ThingSpawnRequest> spawns, List<PlanCooldownRequest> planCooldowns, List<ThingId> despawns, List<InventoryDelta> inventoryOps, List<CurrencyDelta> currencyOps, List<ShopTransaction> shopTransactions, List<RelationshipDelta> relationshipOps, List<CropOperation> cropOps, List<AnimalOperation> animalOps, List<MiningOperation> miningOps, List<FishingOperation> fishingOps, List<ForagingOperation> foragingOps, List<QuestOperation> questOps, HashSet<ThingId> reads)
            {
                if (snap == null || ctx == null) return;
                if (string.IsNullOrWhiteSpace(_predicate)) return;
                if (!ShouldApply(ctx)) return;
                if (!_a.TryResolve(self, target, out var resolvedA)) return;

                ThingId resolvedB;
                if (string.IsNullOrWhiteSpace(_bText))
                    resolvedB = new ThingId(string.Empty);
                else
                {
                    var bref = new TargetRef(_bText);
                    if (!bref.TryResolve(self, target, out resolvedB))
                        resolvedB = new ThingId(string.Empty);
                }

                bool add = _defaultAdd;
                if (_valueExpr != null)
                {
                    try
                    {
                        add = Math.Abs(_valueExpr.EvalNumber(ctx)) > 1e-9;
                    }
                    catch { }
                }

                reads?.Add(resolvedA);
                if (!string.IsNullOrEmpty(resolvedB.Value)) reads?.Add(resolvedB);
                facts?.Add(new FactDelta { Pred = _predicate, A = resolvedA, B = resolvedB, Add = add });
            }
        }

        private sealed class SpawnEffect : EffectOp
        {
            private readonly TargetRef _anchor;
            private readonly string _type;
            private readonly string _idPrefix;
            private readonly string[] _tags;
            private readonly ThingAttributeValue[] _attributes;

            public SpawnEffect(EffectConfig cfg, SafeExpr condition)
                : base(condition)
            {
                string targetText = !string.IsNullOrWhiteSpace(cfg?.target) ? cfg.target : cfg?.who;
                _anchor = new TargetRef(targetText);

                string type = cfg?.name;
                if (string.IsNullOrWhiteSpace(type))
                    type = cfg?.attr;

                string idPrefix = null;
                var tags = new List<string>();
                var attrs = new List<ThingAttributeValue>();

                JsonElement valueElement = cfg?.value ?? default;
                if (valueElement.ValueKind == JsonValueKind.Object)
                {
                    if (string.IsNullOrWhiteSpace(type) && valueElement.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
                        type = typeProp.GetString();
                    if (valueElement.TryGetProperty("idPrefix", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                        idPrefix = idProp.GetString();
                    if (valueElement.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in tagsProp.EnumerateArray())
                        {
                            if (t.ValueKind != JsonValueKind.String) continue;
                            var tag = t.GetString();
                            if (!string.IsNullOrWhiteSpace(tag))
                                tags.Add(tag);
                        }
                    }
                    if (valueElement.TryGetProperty("attributes", out var attrProp) && attrProp.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in attrProp.EnumerateObject())
                        {
                            double val = 0.0;
                            if (property.Value.ValueKind == JsonValueKind.Number)
                                val = property.Value.GetDouble();
                            attrs.Add(new ThingAttributeValue(property.Name, val));
                        }
                    }
                }

                if (tags.Count == 0)
                    tags.Add("item");

                _type = string.IsNullOrWhiteSpace(type) ? "item" : type;
                _idPrefix = string.IsNullOrWhiteSpace(idPrefix) ? _type : idPrefix;
                _tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                _attributes = attrs.ToArray();
            }

            public override void Apply(IWorldSnapshot snap, EvalContext ctx, ThingId self, ThingId target, List<WriteSetEntry> writes, List<FactDelta> facts, List<ThingSpawnRequest> spawns, List<PlanCooldownRequest> planCooldowns, List<ThingId> despawns, List<InventoryDelta> inventoryOps, List<CurrencyDelta> currencyOps, List<ShopTransaction> shopTransactions, List<RelationshipDelta> relationshipOps, List<CropOperation> cropOps, List<AnimalOperation> animalOps, List<MiningOperation> miningOps, List<FishingOperation> fishingOps, List<ForagingOperation> foragingOps, List<QuestOperation> questOps, HashSet<ThingId> reads)
            {
                if (snap == null || ctx == null) return;
                if (!ShouldApply(ctx)) return;
                if (spawns == null) return;
                if (!_anchor.TryResolve(self, target, out var anchorId)) return;

                var anchor = snap.GetThing(anchorId);
                if (anchor == null) return;
                reads?.Add(anchorId);

                string prefix = string.IsNullOrWhiteSpace(_idPrefix) ? _type : _idPrefix;
                if (string.IsNullOrWhiteSpace(prefix))
                    prefix = "thing";

                var newId = new ThingId($"{prefix}-{Guid.NewGuid():N}");
                var request = new ThingSpawnRequest
                {
                    Id = newId,
                    Type = _type,
                    Tags = _tags,
                    Attributes = _attributes,
                    Position = anchor.Position
                };
                spawns.Add(request);
            }
        }

        private sealed class ConsumeNearbyItemEffect : EffectOp
        {
            private readonly TargetRef _anchor;
            private readonly string _tag;
            private readonly string _consumedAttr;
            private readonly double _consumedValue;
            private readonly double _consumedThreshold;
            private readonly string _heldAttr;
            private readonly bool _requireHeldClear;
            private readonly double _heldThreshold;
            private readonly double? _heldValue;
            private readonly double _maxDistance;

            public ConsumeNearbyItemEffect(EffectConfig cfg, SafeExpr condition)
                : base(condition)
            {
                string targetText = !string.IsNullOrWhiteSpace(cfg?.target) ? cfg.target : cfg?.who;
                _anchor = new TargetRef(targetText);

                string tag = "food";
                string consumedAttr = "consumed";
                double consumedValue = 1.0;
                double consumedThreshold = 0.5;
                string heldAttr = "held";
                bool requireHeldClear = true;
                double heldThreshold = 0.5;
                double? heldValue = 0.0;
                double maxDistance = 1.0;

                JsonElement valueElement = cfg?.value ?? default;
                if (valueElement.ValueKind == JsonValueKind.String)
                {
                    tag = valueElement.GetString();
                }
                else if (valueElement.ValueKind == JsonValueKind.Number)
                {
                    maxDistance = Math.Max(0.0, valueElement.GetDouble());
                }
                else if (valueElement.ValueKind == JsonValueKind.Object)
                {
                    if (valueElement.TryGetProperty("tag", out var tagProp) && tagProp.ValueKind == JsonValueKind.String)
                        tag = tagProp.GetString();
                    if (valueElement.TryGetProperty("maxDistance", out var distProp) && distProp.ValueKind == JsonValueKind.Number)
                        maxDistance = Math.Max(0.0, distProp.GetDouble());
                    if (valueElement.TryGetProperty("radius", out var radiusProp) && radiusProp.ValueKind == JsonValueKind.Number)
                        maxDistance = Math.Max(maxDistance, radiusProp.GetDouble());
                    if (valueElement.TryGetProperty("consumedAttr", out var consumedAttrProp) && consumedAttrProp.ValueKind == JsonValueKind.String)
                        consumedAttr = consumedAttrProp.GetString();
                    if (valueElement.TryGetProperty("consumedAttribute", out var consumedAttributeProp) && consumedAttributeProp.ValueKind == JsonValueKind.String)
                        consumedAttr = consumedAttributeProp.GetString();
                    if (valueElement.TryGetProperty("consumedValue", out var consumedValueProp) && consumedValueProp.ValueKind == JsonValueKind.Number)
                        consumedValue = consumedValueProp.GetDouble();
                    if (valueElement.TryGetProperty("consumedThreshold", out var consumedThresholdProp) && consumedThresholdProp.ValueKind == JsonValueKind.Number)
                        consumedThreshold = consumedThresholdProp.GetDouble();
                    if (valueElement.TryGetProperty("heldAttr", out var heldAttrProp) && heldAttrProp.ValueKind == JsonValueKind.String)
                        heldAttr = heldAttrProp.GetString();
                    if (valueElement.TryGetProperty("heldAttribute", out var heldAttributeProp) && heldAttributeProp.ValueKind == JsonValueKind.String)
                        heldAttr = heldAttributeProp.GetString();
                    if (valueElement.TryGetProperty("heldValue", out var heldValueProp) && heldValueProp.ValueKind == JsonValueKind.Number)
                        heldValue = heldValueProp.GetDouble();
                    if (valueElement.TryGetProperty("setHeldValue", out var setHeldValueProp) && setHeldValueProp.ValueKind == JsonValueKind.Number)
                        heldValue = setHeldValueProp.GetDouble();
                    if (valueElement.TryGetProperty("heldThreshold", out var heldThresholdProp) && heldThresholdProp.ValueKind == JsonValueKind.Number)
                        heldThreshold = heldThresholdProp.GetDouble();
                    if (valueElement.TryGetProperty("requireHeldClear", out var requireHeldProp) && requireHeldProp.ValueKind == JsonValueKind.True)
                        requireHeldClear = true;
                    if (valueElement.TryGetProperty("requireHeldClear", out var requireHeldClearProp) && requireHeldClearProp.ValueKind == JsonValueKind.False)
                        requireHeldClear = false;
                    if (valueElement.TryGetProperty("requireHeld", out var requireHeldAnyProp))
                    {
                        if (requireHeldAnyProp.ValueKind == JsonValueKind.True)
                            requireHeldClear = true;
                        else if (requireHeldAnyProp.ValueKind == JsonValueKind.False)
                            requireHeldClear = false;
                    }
                }

                _tag = string.IsNullOrWhiteSpace(tag) ? "food" : tag;
                _consumedAttr = string.IsNullOrWhiteSpace(consumedAttr) ? "consumed" : consumedAttr;
                _consumedValue = consumedValue;
                _consumedThreshold = consumedThreshold;
                _heldAttr = heldAttr;
                _requireHeldClear = requireHeldClear;
                _heldThreshold = heldThreshold;
                _heldValue = heldValue;
                _maxDistance = maxDistance <= 0.0 ? 1.0 : maxDistance;
            }

            public override void Apply(IWorldSnapshot snap, EvalContext ctx, ThingId self, ThingId target, List<WriteSetEntry> writes, List<FactDelta> facts, List<ThingSpawnRequest> spawns, List<PlanCooldownRequest> planCooldowns, List<ThingId> despawns, List<InventoryDelta> inventoryOps, List<CurrencyDelta> currencyOps, List<ShopTransaction> shopTransactions, List<RelationshipDelta> relationshipOps, List<CropOperation> cropOps, List<AnimalOperation> animalOps, List<MiningOperation> miningOps, List<FishingOperation> fishingOps, List<ForagingOperation> foragingOps, List<QuestOperation> questOps, HashSet<ThingId> reads)
            {
                if (snap == null || ctx == null) return;
                if (!ShouldApply(ctx)) return;
                if (!_anchor.TryResolve(self, target, out var anchorId)) return;

                var anchor = snap.GetThing(anchorId);
                if (anchor == null) return;
                reads?.Add(anchorId);

                ThingView best = null;
                int bestDist = int.MaxValue;

                foreach (var candidate in snap.QueryByTag(_tag))
                {
                    if (candidate == null) continue;
                    int dist = DataDrivenGoap.Core.GridPos.Manhattan(candidate.Position, anchor.Position);
                    if (dist > _maxDistance + 1e-6) continue;

                    double consumedValue = candidate.AttrOrDefault(_consumedAttr, 0.0);
                    if (consumedValue > _consumedThreshold) continue;

                    if (_requireHeldClear && !string.IsNullOrWhiteSpace(_heldAttr))
                    {
                        double heldValue = candidate.AttrOrDefault(_heldAttr, 0.0);
                        if (heldValue > _heldThreshold) continue;
                    }

                    if (dist < bestDist)
                    {
                        best = candidate;
                        bestDist = dist;
                    }
                }

                if (best == null)
                    return;

                reads?.Add(best.Id);
                writes?.Add(new WriteSetEntry(best.Id, _consumedAttr, _consumedValue));
                if (_heldValue.HasValue && !string.IsNullOrWhiteSpace(_heldAttr))
                    writes?.Add(new WriteSetEntry(best.Id, _heldAttr, _heldValue.Value));
            }
        }

        private sealed class DespawnEffect : EffectOp
        {
            private readonly TargetRef _target;

            public DespawnEffect(EffectConfig cfg, SafeExpr condition)
                : base(condition)
            {
                string targetText = !string.IsNullOrWhiteSpace(cfg?.target) ? cfg.target : cfg?.who;
                if (string.IsNullOrWhiteSpace(targetText))
                    targetText = "$target";
                _target = new TargetRef(targetText);
            }

            public override void Apply(IWorldSnapshot snap, EvalContext ctx, ThingId self, ThingId target, List<WriteSetEntry> writes, List<FactDelta> facts, List<ThingSpawnRequest> spawns, List<PlanCooldownRequest> planCooldowns, List<ThingId> despawns, List<InventoryDelta> inventoryOps, List<CurrencyDelta> currencyOps, List<ShopTransaction> shopTransactions, List<RelationshipDelta> relationshipOps, List<CropOperation> cropOps, List<AnimalOperation> animalOps, List<MiningOperation> miningOps, List<FishingOperation> fishingOps, List<ForagingOperation> foragingOps, List<QuestOperation> questOps, HashSet<ThingId> reads)
            {
                if (snap == null || ctx == null) return;
                if (despawns == null) return;
                if (!ShouldApply(ctx)) return;
                if (!_target.TryResolve(self, target, out var resolved)) return;
                if (string.IsNullOrEmpty(resolved.Value)) return;

                reads?.Add(resolved);
                if (!despawns.Contains(resolved))
                    despawns.Add(resolved);
            }
        }

        private sealed class MoveStepEffect : EffectOp
        {
            private readonly TargetRef _mover;
            private readonly TargetRef _towards;
            private readonly double _stopWithin;

            public MoveStepEffect(EffectConfig cfg, SafeExpr condition)
                : base(condition)
            {
                string moverText = !string.IsNullOrWhiteSpace(cfg?.target) ? cfg.target : cfg?.who;
                _mover = new TargetRef(moverText);
                _towards = new TargetRef(string.IsNullOrWhiteSpace(cfg?.towards) ? "$target" : cfg.towards);
                _stopWithin = cfg?.stopWithin ?? 1.0;
                if (_stopWithin < 0) _stopWithin = 0;
            }

            public override void Apply(IWorldSnapshot snap, EvalContext ctx, ThingId self, ThingId target, List<WriteSetEntry> writes, List<FactDelta> facts, List<ThingSpawnRequest> spawns, List<PlanCooldownRequest> planCooldowns, List<ThingId> despawns, List<InventoryDelta> inventoryOps, List<CurrencyDelta> currencyOps, List<ShopTransaction> shopTransactions, List<RelationshipDelta> relationshipOps, List<CropOperation> cropOps, List<AnimalOperation> animalOps, List<MiningOperation> miningOps, List<FishingOperation> fishingOps, List<ForagingOperation> foragingOps, List<QuestOperation> questOps, HashSet<ThingId> reads)
            {
                if (snap == null || ctx == null) return;
                if (!ShouldApply(ctx)) return;
                if (!_mover.TryResolve(self, target, out var mover)) return;
                if (!_towards.TryResolve(self, target, out var dest)) return;

                var moverView = snap.GetThing(mover);
                var destView = snap.GetThing(dest);
                if (moverView == null || destView == null) return;

                
                // Resolve a usable destination: if the target is a building, prefer the nearest service point.
                var toPos = destView.Position;
                var building = destView.Building;
                if (building != null && building.ServicePoints != null && building.ServicePoints.Count > 0)
                {
                    int best = int.MaxValue;
                    var bestPos = toPos;
                    foreach (var sp in building.ServicePoints)
                    {
                        int d = DataDrivenGoap.Core.GridPos.Manhattan(moverView.Position, sp);
                        if (d < best) { best = d; bestPos = sp; }
                    }
                    toPos = bestPos;
                }

                int dist = DataDrivenGoap.Core.GridPos.Manhattan(moverView.Position, toPos);
                if (dist <= _stopWithin) return;

                if (snap.TryFindNextStep(moverView.Position, toPos, out var next))
                {

                    reads?.Add(mover);
                    reads?.Add(dest);
                    writes?.Add(new WriteSetEntry(mover, "@move.x", next.X));
                    writes?.Add(new WriteSetEntry(mover, "@move.y", next.Y));
                }
            }
        }

        private sealed class PlanCooldownEffect : EffectOp
        {
            private readonly TargetRef _scope;
            private readonly SafeExpr _secondsExpr;
            private readonly bool _useDuration;

            public PlanCooldownEffect(EffectConfig cfg, SafeExpr condition)
                : base(condition)
            {
                string scopeText = !string.IsNullOrWhiteSpace(cfg?.target) ? cfg.target : cfg?.who;
                if (string.IsNullOrWhiteSpace(scopeText))
                    scopeText = "$self";
                _scope = new TargetRef(scopeText);

                JsonElement valueElement = cfg?.value ?? default;
                _secondsExpr = CompileExpr(valueElement);
                bool useDuration = false;
                if (valueElement.ValueKind == JsonValueKind.Undefined || valueElement.ValueKind == JsonValueKind.Null)
                    useDuration = true;
                if (_secondsExpr == null)
                    useDuration = true;
                _useDuration = useDuration;
            }

            public override void Apply(IWorldSnapshot snap, EvalContext ctx, ThingId self, ThingId target, List<WriteSetEntry> writes, List<FactDelta> facts, List<ThingSpawnRequest> spawns, List<PlanCooldownRequest> planCooldowns, List<ThingId> despawns, List<InventoryDelta> inventoryOps, List<CurrencyDelta> currencyOps, List<ShopTransaction> shopTransactions, List<RelationshipDelta> relationshipOps, List<CropOperation> cropOps, List<AnimalOperation> animalOps, List<MiningOperation> miningOps, List<FishingOperation> fishingOps, List<ForagingOperation> foragingOps, List<QuestOperation> questOps, HashSet<ThingId> reads)
            {
                if (snap == null || ctx == null) return;
                if (planCooldowns == null) return;
                if (!ShouldApply(ctx)) return;

                var resolved = _scope.ResolveOrDefault(self, target, self);
                if (!string.IsNullOrEmpty(resolved.Value))
                    reads?.Add(resolved);

                double seconds = 0.0;
                if (_secondsExpr != null)
                {
                    seconds = Math.Max(0.0, EvaluateNumber(_secondsExpr, ctx, 0.0));
                }

                planCooldowns.Add(new PlanCooldownRequest(resolved, seconds, _useDuration));
            }
        }

        private sealed class InventoryEffect : EffectOp
        {
            private readonly TargetRef _owner;
            private readonly string _itemId;
            private readonly SafeExpr _quantityExpr;
            private readonly bool _remove;

            public InventoryEffect(EffectConfig cfg, SafeExpr condition, bool remove)
                : base(condition)
            {
                string ownerText = !string.IsNullOrWhiteSpace(cfg?.who) ? cfg.who : cfg?.target;
                if (string.IsNullOrWhiteSpace(ownerText))
                    ownerText = "$self";
                _owner = new TargetRef(ownerText);
                _itemId = (cfg?.name ?? cfg?.attr ?? string.Empty)?.Trim() ?? string.Empty;
                _remove = remove;

                JsonElement valueElement = cfg?.value ?? default;
                SafeExpr expr = null;
                if (valueElement.ValueKind == JsonValueKind.Object)
                {
                    if (valueElement.TryGetProperty("quantity", out var qtyProp))
                        expr = CompileExpr(qtyProp);
                }
                if (expr == null && valueElement.ValueKind != JsonValueKind.Object)
                    expr = CompileExpr(valueElement);
                _quantityExpr = expr ?? SafeExpr.Compile("1");
            }

            public override void Apply(IWorldSnapshot snap, EvalContext ctx, ThingId self, ThingId target, List<WriteSetEntry> writes, List<FactDelta> facts, List<ThingSpawnRequest> spawns, List<PlanCooldownRequest> planCooldowns, List<ThingId> despawns, List<InventoryDelta> inventoryOps, List<CurrencyDelta> currencyOps, List<ShopTransaction> shopTransactions, List<RelationshipDelta> relationshipOps, List<CropOperation> cropOps, List<AnimalOperation> animalOps, List<MiningOperation> miningOps, List<FishingOperation> fishingOps, List<ForagingOperation> foragingOps, List<QuestOperation> questOps, HashSet<ThingId> reads)
            {
                if (snap == null || ctx == null) return;
                if (inventoryOps == null) return;
                if (!ShouldApply(ctx)) return;
                if (!_owner.TryResolve(self, target, out var owner)) return;
                if (string.IsNullOrWhiteSpace(_itemId)) return;

                double qtyValue = EvaluateNumber(_quantityExpr, ctx, 1.0);
                int quantity = (int)Math.Round(Math.Abs(qtyValue));
                if (quantity <= 0)
                    return;

                reads?.Add(owner);
                inventoryOps.Add(new InventoryDelta(owner, _itemId, quantity, _remove));
            }
        }

        private sealed class CraftingEffect : EffectOp
        {
            private readonly TargetRef _owner;
            private readonly string _recipeId;
            private readonly SafeExpr _countExpr;
            private readonly string _stationHint;
            private readonly bool _useTargetStation;

            public CraftingEffect(EffectConfig cfg, SafeExpr condition)
                : base(condition)
            {
                string ownerText = !string.IsNullOrWhiteSpace(cfg?.who) ? cfg.who : "$self";
                _owner = new TargetRef(ownerText);
                _recipeId = (cfg?.name ?? cfg?.attr ?? string.Empty)?.Trim() ?? string.Empty;

                JsonElement valueElement = cfg?.value ?? default;
                SafeExpr countExpr = null;
                string stationHint = null;
                bool useTarget = true;

                if (valueElement.ValueKind == JsonValueKind.Object)
                {
                    if (valueElement.TryGetProperty("count", out var countProp))
                        countExpr = CompileExpr(countProp);
                    else if (valueElement.TryGetProperty("quantity", out var qtyProp))
                        countExpr = CompileExpr(qtyProp);

                    if (valueElement.TryGetProperty("station", out var stationProp) && stationProp.ValueKind == JsonValueKind.String)
                        stationHint = stationProp.GetString();

                    if (valueElement.TryGetProperty("useTargetStation", out var targetProp))
                    {
                        if (targetProp.ValueKind == JsonValueKind.False)
                            useTarget = false;
                        else if (targetProp.ValueKind == JsonValueKind.True)
                            useTarget = true;
                    }
                }
                else if (valueElement.ValueKind == JsonValueKind.String || valueElement.ValueKind == JsonValueKind.Number)
                {
                    countExpr = CompileExpr(valueElement);
                }

                _countExpr = countExpr ?? SafeExpr.Compile("1");
                _stationHint = string.IsNullOrWhiteSpace(stationHint) ? null : stationHint.Trim();
                _useTargetStation = useTarget;
            }

            public override void Apply(IWorldSnapshot snap, EvalContext ctx, ThingId self, ThingId target, List<WriteSetEntry> writes, List<FactDelta> facts, List<ThingSpawnRequest> spawns, List<PlanCooldownRequest> planCooldowns, List<ThingId> despawns, List<InventoryDelta> inventoryOps, List<CurrencyDelta> currencyOps, List<ShopTransaction> shopTransactions, List<RelationshipDelta> relationshipOps, List<CropOperation> cropOps, List<AnimalOperation> animalOps, List<MiningOperation> miningOps, List<FishingOperation> fishingOps, List<ForagingOperation> foragingOps, List<QuestOperation> questOps, HashSet<ThingId> reads)
            {
                if (snap == null || ctx == null) return;
                if (inventoryOps == null) return;
                if (!ShouldApply(ctx)) return;
                if (string.IsNullOrWhiteSpace(_recipeId)) return;
                if (!_owner.TryResolve(self, target, out var owner)) return;

                var crafting = ctx.CraftingQuery;
                if (crafting == null) return;
                if (!crafting.TryGetRecipe(_recipeId, out var recipe) || recipe == null)
                    return;

                double countValue = EvaluateNumber(_countExpr, ctx, 1.0);
                int count = (int)Math.Round(Math.Abs(countValue));
                if (count <= 0)
                    return;

                var ownerThing = snap.GetThing(owner);
                if (!CraftingUtility.MeetsSkillGates(recipe, ownerThing, ctx?.SkillProgression))
                    return;

                ThingView stationView = null;
                if (_useTargetStation && !string.IsNullOrEmpty(target.Value))
                    stationView = snap.GetThing(target);

                if (!CraftingUtility.MatchesStation(recipe, stationView, _stationHint))
                    return;

                if (!crafting.HasIngredients(owner, recipe, count))
                    return;

                reads?.Add(owner);
                if (stationView != null)
                    reads?.Add(target);

                foreach (var input in recipe.Inputs)
                {
                    int required = CraftingUtility.SafeMultiply(input.Value, count);
                    if (required <= 0)
                        continue;
                    inventoryOps.Add(new InventoryDelta(owner, input.Key, required, remove: true));
                }

                foreach (var output in recipe.Outputs)
                {
                    int amount = CraftingUtility.SafeMultiply(output.Value, count);
                    if (amount <= 0)
                        continue;
                    inventoryOps.Add(new InventoryDelta(owner, output.Key, amount, remove: false));
                }
            }
        }

        private sealed class CurrencyEffect : EffectOp
        {
            private readonly TargetRef _owner;
            private readonly SafeExpr _amountExpr;

            public CurrencyEffect(EffectConfig cfg, SafeExpr condition)
                : base(condition)
            {
                string ownerText = !string.IsNullOrWhiteSpace(cfg?.who) ? cfg.who : cfg?.target;
                if (string.IsNullOrWhiteSpace(ownerText))
                    ownerText = "$self";
                _owner = new TargetRef(ownerText);
                _amountExpr = CompileExpr(cfg?.value ?? default) ?? SafeExpr.Compile("0");
            }

            public override void Apply(IWorldSnapshot snap, EvalContext ctx, ThingId self, ThingId target, List<WriteSetEntry> writes, List<FactDelta> facts, List<ThingSpawnRequest> spawns, List<PlanCooldownRequest> planCooldowns, List<ThingId> despawns, List<InventoryDelta> inventoryOps, List<CurrencyDelta> currencyOps, List<ShopTransaction> shopTransactions, List<RelationshipDelta> relationshipOps, List<CropOperation> cropOps, List<AnimalOperation> animalOps, List<MiningOperation> miningOps, List<FishingOperation> fishingOps, List<ForagingOperation> foragingOps, List<QuestOperation> questOps, HashSet<ThingId> reads)
            {
                if (snap == null || ctx == null) return;
                if (currencyOps == null) return;
                if (!ShouldApply(ctx)) return;
                if (!_owner.TryResolve(self, target, out var owner)) return;

                double amount = EvaluateNumber(_amountExpr, ctx, 0.0);
                if (Math.Abs(amount) < 1e-6)
                    return;

                reads?.Add(owner);
                currencyOps.Add(new CurrencyDelta(owner, amount));
            }
        }

        private sealed class SkillXpEffect : EffectOp
        {
            private readonly TargetRef _target;
            private readonly string _skillId;
            private readonly SafeExpr _amountExpr;

            public SkillXpEffect(EffectConfig cfg, SafeExpr condition)
                : base(condition)
            {
                string targetText = !string.IsNullOrWhiteSpace(cfg?.who) ? cfg.who : cfg?.target;
                if (string.IsNullOrWhiteSpace(targetText))
                    targetText = "$self";
                _target = new TargetRef(targetText);

                string skillId = !string.IsNullOrWhiteSpace(cfg?.name) ? cfg.name : cfg?.attr;
                _skillId = string.IsNullOrWhiteSpace(skillId) ? null : skillId.Trim();
                _amountExpr = CompileExpr(cfg?.value ?? default) ?? SafeExpr.Compile("0");
            }

            public override void Apply(IWorldSnapshot snap, EvalContext ctx, ThingId self, ThingId target, List<WriteSetEntry> writes, List<FactDelta> facts, List<ThingSpawnRequest> spawns, List<PlanCooldownRequest> planCooldowns, List<ThingId> despawns, List<InventoryDelta> inventoryOps, List<CurrencyDelta> currencyOps, List<ShopTransaction> shopTransactions, List<RelationshipDelta> relationshipOps, List<CropOperation> cropOps, List<AnimalOperation> animalOps, List<MiningOperation> miningOps, List<FishingOperation> fishingOps, List<ForagingOperation> foragingOps, List<QuestOperation> questOps, HashSet<ThingId> reads)
            {
                if (!ShouldApply(ctx))
                    return;
                if (ctx?.SkillProgression == null)
                    return;
                if (string.IsNullOrWhiteSpace(_skillId))
                    return;

                var recipient = _target.ResolveOrDefault(self, target, self);
                if (string.IsNullOrWhiteSpace(recipient.Value))
                    return;

                double amount = EvaluateNumber(_amountExpr, ctx, 0.0);
                if (Math.Abs(amount) < 1e-9)
                    return;

                ctx.SkillProgression.AddExperience(recipient, _skillId, amount);
            }
        }

        private sealed class ShopTransactionEffect : EffectOp
        {
            private readonly TargetRef _shop;
            private readonly TargetRef _actor;
            private readonly string _itemId;
            private readonly ShopTransactionKind _kind;
            private readonly SafeExpr _quantityExpr;

            public ShopTransactionEffect(EffectConfig cfg, SafeExpr condition)
                : base(condition)
            {
                string shopText = !string.IsNullOrWhiteSpace(cfg?.target) ? cfg.target : "$target";
                string actorText = !string.IsNullOrWhiteSpace(cfg?.who) ? cfg.who : "$self";
                _shop = new TargetRef(shopText);
                _actor = new TargetRef(actorText);

                string itemId = (cfg?.name ?? cfg?.attr ?? string.Empty)?.Trim() ?? string.Empty;
                string mode = cfg?.op;
                SafeExpr quantityExpr = null;

                JsonElement valueElement = cfg?.value ?? default;
                if (valueElement.ValueKind == JsonValueKind.Object)
                {
                    if (valueElement.TryGetProperty("item", out var itemProp) && itemProp.ValueKind == JsonValueKind.String)
                        itemId = itemProp.GetString();
                    if (valueElement.TryGetProperty("mode", out var modeProp) && modeProp.ValueKind == JsonValueKind.String)
                        mode = modeProp.GetString();
                    if (valueElement.TryGetProperty("quantity", out var qtyProp))
                        quantityExpr = CompileExpr(qtyProp);
                }
                else if (valueElement.ValueKind != JsonValueKind.Undefined && valueElement.ValueKind != JsonValueKind.Null)
                {
                    quantityExpr = CompileExpr(valueElement);
                }

                if (string.IsNullOrWhiteSpace(mode))
                    mode = "buy";
                mode = mode.Trim().ToLowerInvariant();
                _kind = mode == "sell" || mode == "sale" ? ShopTransactionKind.Sale : ShopTransactionKind.Purchase;

                _itemId = itemId?.Trim() ?? string.Empty;
                _quantityExpr = quantityExpr ?? SafeExpr.Compile("1");
            }

            public override void Apply(IWorldSnapshot snap, EvalContext ctx, ThingId self, ThingId target, List<WriteSetEntry> writes, List<FactDelta> facts, List<ThingSpawnRequest> spawns, List<PlanCooldownRequest> planCooldowns, List<ThingId> despawns, List<InventoryDelta> inventoryOps, List<CurrencyDelta> currencyOps, List<ShopTransaction> shopTransactions, List<RelationshipDelta> relationshipOps, List<CropOperation> cropOps, List<AnimalOperation> animalOps, List<MiningOperation> miningOps, List<FishingOperation> fishingOps, List<ForagingOperation> foragingOps, List<QuestOperation> questOps, HashSet<ThingId> reads)
            {
                if (snap == null || ctx == null) return;
                if (shopTransactions == null) return;
                if (!ShouldApply(ctx)) return;
                if (string.IsNullOrWhiteSpace(_itemId)) return;
                if (!_shop.TryResolve(self, target, out var shop)) return;
                if (!_actor.TryResolve(self, target, out var actor)) return;

                double qtyValue = EvaluateNumber(_quantityExpr, ctx, 1.0);
                int quantity = (int)Math.Round(Math.Abs(qtyValue));
                if (quantity <= 0)
                    return;

                reads?.Add(shop);
                reads?.Add(actor);
                shopTransactions.Add(new ShopTransaction(shop, actor, _itemId, quantity, _kind));
            }
        }

        private sealed class RelationshipEffect : EffectOp
        {
            private readonly TargetRef _from;
            private readonly TargetRef _to;
            private readonly string _relationshipId;
            private readonly string _itemId;
            private readonly SafeExpr _deltaExpr;

            public RelationshipEffect(EffectConfig cfg, SafeExpr condition)
                : base(condition)
            {
                string fromText = !string.IsNullOrWhiteSpace(cfg?.who) ? cfg.who : "$self";
                string toText = !string.IsNullOrWhiteSpace(cfg?.target) ? cfg.target : "$target";
                _from = new TargetRef(fromText);
                _to = new TargetRef(toText);
                _relationshipId = (cfg?.name ?? cfg?.attr ?? "friendship")?.Trim() ?? "friendship";

                string itemId = string.Empty;
                JsonElement valueElement = cfg?.value ?? default;
                SafeExpr deltaExpr = null;
                if (valueElement.ValueKind == JsonValueKind.Object)
                {
                    if (valueElement.TryGetProperty("item", out var itemProp) && itemProp.ValueKind == JsonValueKind.String)
                        itemId = itemProp.GetString();
                    if (valueElement.TryGetProperty("delta", out var deltaProp))
                        deltaExpr = CompileExpr(deltaProp);
                }
                else if (valueElement.ValueKind == JsonValueKind.String || valueElement.ValueKind == JsonValueKind.Number)
                {
                    deltaExpr = CompileExpr(valueElement);
                }

                if (string.IsNullOrWhiteSpace(itemId))
                    itemId = (cfg?.b ?? string.Empty)?.Trim();

                _itemId = itemId?.Trim() ?? string.Empty;
                _deltaExpr = deltaExpr;
            }

            public override void Apply(IWorldSnapshot snap, EvalContext ctx, ThingId self, ThingId target, List<WriteSetEntry> writes, List<FactDelta> facts, List<ThingSpawnRequest> spawns, List<PlanCooldownRequest> planCooldowns, List<ThingId> despawns, List<InventoryDelta> inventoryOps, List<CurrencyDelta> currencyOps, List<ShopTransaction> shopTransactions, List<RelationshipDelta> relationshipOps, List<CropOperation> cropOps, List<AnimalOperation> animalOps, List<MiningOperation> miningOps, List<FishingOperation> fishingOps, List<ForagingOperation> foragingOps, List<QuestOperation> questOps, HashSet<ThingId> reads)
            {
                if (snap == null || ctx == null) return;
                if (relationshipOps == null) return;
                if (!ShouldApply(ctx)) return;
                if (!_from.TryResolve(self, target, out var from)) return;
                if (!_to.TryResolve(self, target, out var to)) return;

                double? explicitDelta = null;
                if (_deltaExpr != null)
                    explicitDelta = EvaluateNumber(_deltaExpr, ctx, 0.0);

                reads?.Add(from);
                reads?.Add(to);
                relationshipOps.Add(new RelationshipDelta(from, to, _relationshipId, _itemId, explicitDelta));
            }
        }

        private sealed class AnimalEffect : EffectOp
        {
            private readonly TargetRef _target;
            private readonly AnimalOperationKind _kind;
            private readonly string _itemId;
            private readonly int _quantity;
            private readonly bool _valid;

            public AnimalEffect(EffectConfig cfg, SafeExpr condition)
                : base(condition)
            {
                string targetText = !string.IsNullOrWhiteSpace(cfg?.target) ? cfg.target : cfg?.who;
                _target = new TargetRef(targetText);
                _kind = ParseKind(cfg, out _valid);
                (_itemId, _quantity) = ParseValue(cfg?.value);
            }

            private static AnimalOperationKind ParseKind(EffectConfig cfg, out bool valid)
            {
                string opText = cfg?.op ?? cfg?.type ?? string.Empty;
                opText = opText?.Trim()?.ToLowerInvariant() ?? string.Empty;
                valid = true;
                switch (opText)
                {
                    case "animal_feed":
                    case "feed_animal":
                    case "feed_livestock":
                    case "livestock_feed":
                        return AnimalOperationKind.Feed;
                    case "animal_brush":
                    case "brush_animal":
                    case "groom_animal":
                        return AnimalOperationKind.Brush;
                    case "animal_collect":
                    case "collect_produce":
                    case "collect_eggs":
                    case "collect_milk":
                    case "harvest_animal":
                        return AnimalOperationKind.Collect;
                    default:
                        valid = false;
                        return AnimalOperationKind.Feed;
                }
            }

            private static (string itemId, int quantity) ParseValue(JsonElement? value)
            {
                if (!value.HasValue)
                    return (null, 0);

                var element = value.Value;
                if (element.ValueKind == JsonValueKind.String)
                    return (element.GetString(), 0);

                if (element.ValueKind == JsonValueKind.Object)
                {
                    string itemId = null;
                    int quantity = 0;
                    if (element.TryGetProperty("item", out var itemProp) && itemProp.ValueKind == JsonValueKind.String)
                        itemId = itemProp.GetString();
                    if (element.TryGetProperty("itemId", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                    {
                        if (itemId == null)
                            itemId = idProp.GetString();
                    }
                    if (element.TryGetProperty("quantity", out var qtyProp) && qtyProp.ValueKind == JsonValueKind.Number)
                    {
                        try { quantity = qtyProp.GetInt32(); }
                        catch { quantity = (int)Math.Round(qtyProp.GetDouble()); }
                        if (quantity < 0) quantity = 0;
                    }
                    return (itemId, quantity);
                }

                return (null, 0);
            }

            public override void Apply(IWorldSnapshot snap, EvalContext ctx, ThingId self, ThingId target, List<WriteSetEntry> writes, List<FactDelta> facts, List<ThingSpawnRequest> spawns, List<PlanCooldownRequest> planCooldowns, List<ThingId> despawns, List<InventoryDelta> inventoryOps, List<CurrencyDelta> currencyOps, List<ShopTransaction> shopTransactions, List<RelationshipDelta> relationshipOps, List<CropOperation> cropOps, List<AnimalOperation> animalOps, List<MiningOperation> miningOps, List<FishingOperation> fishingOps, List<ForagingOperation> foragingOps, List<QuestOperation> questOps, HashSet<ThingId> reads)
            {
                if (!_valid || animalOps == null)
                    return;
                if (snap == null || ctx == null)
                    return;
                if (!ShouldApply(ctx))
                    return;
                if (!_target.TryResolve(self, target, out var resolved))
                    return;

                var op = new AnimalOperation
                {
                    Kind = _kind,
                    Animal = resolved,
                    Actor = self,
                    ItemId = _itemId,
                    Quantity = _quantity
                };
                animalOps.Add(op);
                reads?.Add(resolved);
            }
        }

        private sealed class FishingEffect : EffectOp
        {
            private readonly TargetRef _target;
            private readonly string _baitItemId;
            private readonly int _baitQuantity;
            private readonly bool _valid;

            public FishingEffect(EffectConfig cfg, SafeExpr condition)
                : base(condition)
            {
                string targetText = !string.IsNullOrWhiteSpace(cfg?.target) ? cfg.target : cfg?.who;
                _target = new TargetRef(targetText);
                (_baitItemId, _baitQuantity, _valid) = ParseValue(cfg?.value);
            }

            private static (string baitItemId, int quantity, bool valid) ParseValue(JsonElement? value)
            {
                string bait = null;
                int quantity = 1;
                bool valid = true;

                if (!value.HasValue)
                    return (bait, quantity, valid);

                var element = value.Value;
                if (element.ValueKind == JsonValueKind.String)
                {
                    bait = element.GetString();
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    if (element.TryGetProperty("bait", out var baitProp) && baitProp.ValueKind == JsonValueKind.String)
                        bait = baitProp.GetString();
                    else if (element.TryGetProperty("item", out var itemProp) && itemProp.ValueKind == JsonValueKind.String)
                        bait = itemProp.GetString();
                    if (element.TryGetProperty("quantity", out var qtyProp) && qtyProp.ValueKind == JsonValueKind.Number)
                    {
                        try { quantity = qtyProp.GetInt32(); }
                        catch { quantity = (int)Math.Round(qtyProp.GetDouble()); }
                        if (quantity <= 0)
                            quantity = 1;
                    }
                }
                else if (element.ValueKind != JsonValueKind.Null && element.ValueKind != JsonValueKind.Undefined)
                {
                    valid = false;
                }

                if (quantity <= 0)
                    quantity = 1;

                return (string.IsNullOrWhiteSpace(bait) ? null : bait.Trim(), quantity, valid);
            }

            public override void Apply(IWorldSnapshot snap, EvalContext ctx, ThingId self, ThingId target, List<WriteSetEntry> writes, List<FactDelta> facts, List<ThingSpawnRequest> spawns, List<PlanCooldownRequest> planCooldowns, List<ThingId> despawns, List<InventoryDelta> inventoryOps, List<CurrencyDelta> currencyOps, List<ShopTransaction> shopTransactions, List<RelationshipDelta> relationshipOps, List<CropOperation> cropOps, List<AnimalOperation> animalOps, List<MiningOperation> miningOps, List<FishingOperation> fishingOps, List<ForagingOperation> foragingOps, List<QuestOperation> questOps, HashSet<ThingId> reads)
            {
                if (!_valid || fishingOps == null)
                    return;
                if (snap == null || ctx == null)
                    return;
                if (!ShouldApply(ctx))
                    return;
                if (!_target.TryResolve(self, target, out var resolved))
                    return;

                var op = new FishingOperation
                {
                    Kind = FishingOperationKind.Cast,
                    Spot = resolved,
                    Actor = self,
                    BaitItemId = _baitItemId,
                    BaitQuantity = _baitItemId == null ? 0 : Math.Max(1, _baitQuantity)
                };

                fishingOps.Add(op);
                reads?.Add(resolved);
            }
        }

        private sealed class MiningEffect : EffectOp
        {
            private readonly TargetRef _target;
            private readonly string _toolItemId;
            private readonly string _toolFlag;
            private readonly int _toolTier;
            private readonly bool _tierFromTool;
            private readonly bool _valid;

            public MiningEffect(EffectConfig cfg, SafeExpr condition)
                : base(condition)
            {
                string targetText = !string.IsNullOrWhiteSpace(cfg?.target) ? cfg.target : cfg?.who;
                _target = new TargetRef(targetText);
                (_toolItemId, _toolFlag, _toolTier, _tierFromTool, _valid) = ParseValue(cfg?.value);
            }

            private static (string toolItemId, string toolFlag, int tier, bool tierFromTool, bool valid) ParseValue(JsonElement? value)
            {
                string tool = null;
                string flag = null;
                int tier = 0;
                bool tierFromTool = false;
                bool valid = true;

                if (!value.HasValue)
                    return (tool, flag, tier, tierFromTool, valid);

                var element = value.Value;
                if (element.ValueKind == JsonValueKind.String)
                {
                    tool = element.GetString();
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    if (element.TryGetProperty("tool", out var toolProp) && toolProp.ValueKind == JsonValueKind.String)
                        tool = toolProp.GetString();
                    else if (element.TryGetProperty("item", out var itemProp) && itemProp.ValueKind == JsonValueKind.String)
                        tool = itemProp.GetString();

                    if (element.TryGetProperty("toolFlag", out var flagProp) && flagProp.ValueKind == JsonValueKind.String)
                        flag = flagProp.GetString();

                    if (element.TryGetProperty("tier", out var tierProp) && tierProp.ValueKind == JsonValueKind.Number)
                    {
                        try { tier = tierProp.GetInt32(); }
                        catch { tier = (int)Math.Round(tierProp.GetDouble()); }
                        if (tier < 0)
                            tier = 0;
                    }

                    if (element.TryGetProperty("tierFromTool", out var tierFromProp))
                    {
                        tierFromTool = tierFromProp.ValueKind == JsonValueKind.True;
                        if (!tierFromTool && tierFromProp.ValueKind == JsonValueKind.Number)
                            tierFromTool = Math.Abs(tierFromProp.GetDouble()) > 0.5;
                    }
                }
                else if (element.ValueKind != JsonValueKind.Null && element.ValueKind != JsonValueKind.Undefined)
                {
                    valid = false;
                }

                return (
                    string.IsNullOrWhiteSpace(tool) ? null : tool.Trim(),
                    string.IsNullOrWhiteSpace(flag) ? null : flag.Trim(),
                    tier,
                    tierFromTool,
                    valid);
            }

            public override void Apply(IWorldSnapshot snap, EvalContext ctx, ThingId self, ThingId target, List<WriteSetEntry> writes, List<FactDelta> facts, List<ThingSpawnRequest> spawns, List<PlanCooldownRequest> planCooldowns, List<ThingId> despawns, List<InventoryDelta> inventoryOps, List<CurrencyDelta> currencyOps, List<ShopTransaction> shopTransactions, List<RelationshipDelta> relationshipOps, List<CropOperation> cropOps, List<AnimalOperation> animalOps, List<MiningOperation> miningOps, List<FishingOperation> fishingOps, List<ForagingOperation> foragingOps, List<QuestOperation> questOps, HashSet<ThingId> reads)
            {
                if (!_valid || miningOps == null)
                    return;
                if (snap == null || ctx == null)
                    return;
                if (!ShouldApply(ctx))
                    return;
                if (!_target.TryResolve(self, target, out var resolved))
                    return;

                var (toolId, tier) = ResolveTool(ctx, self);

                var op = new MiningOperation
                {
                    Kind = MiningOperationKind.Extract,
                    Node = resolved,
                    Actor = self,
                    ToolItemId = toolId,
                    ToolTier = Math.Max(0, tier)
                };

                miningOps.Add(op);
                reads?.Add(resolved);
            }

            private (string toolId, int tier) ResolveTool(EvalContext ctx, ThingId actor)
            {
                string chosenId = _toolItemId;
                int tier = _tierFromTool ? -1 : Math.Max(0, _toolTier);

                var inventory = ctx?.InventoryQuery?.Snapshot(actor);
                if (inventory != null)
                {
                    string bestId = null;
                    int bestTier = -1;

                    foreach (var stack in inventory)
                    {
                        var item = stack.Item;
                        if (item == null)
                            continue;
                        if (!string.IsNullOrWhiteSpace(_toolItemId) && !string.Equals(item.Id, _toolItemId, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!string.IsNullOrWhiteSpace(_toolFlag) && !item.HasToolFlag(_toolFlag))
                            continue;

                        int candidateTier = _tierFromTool ? ExtractTier(item, _toolFlag) : Math.Max(0, _toolTier);
                        if (candidateTier > bestTier)
                        {
                            bestTier = candidateTier;
                            bestId = item.Id;
                        }
                    }

                    if (bestId != null)
                    {
                        chosenId = bestId;
                        tier = bestTier < 0 ? 0 : bestTier;
                    }
                }

                if (string.IsNullOrWhiteSpace(chosenId))
                    chosenId = _toolItemId;
                if (tier < 0)
                    tier = Math.Max(0, _toolTier);

                return (chosenId, tier);
            }

            private static int ExtractTier(DataDrivenGoap.Items.ItemDefinition item, string requiredFlag)
            {
                if (item == null)
                    return 0;

                int best = -1;
                foreach (var flag in item.ToolFlags ?? Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(flag))
                        continue;

                    string text = flag.Trim();
                    string suffix;
                    if (!string.IsNullOrWhiteSpace(requiredFlag))
                    {
                        if (!text.StartsWith(requiredFlag, StringComparison.OrdinalIgnoreCase))
                            continue;
                        suffix = text.Substring(requiredFlag.Length);
                        if (!suffix.StartsWith("_tier", StringComparison.OrdinalIgnoreCase))
                            continue;
                        suffix = suffix.Substring(5);
                    }
                    else
                    {
                        int idx = text.IndexOf("_tier", StringComparison.OrdinalIgnoreCase);
                        if (idx < 0)
                            continue;
                        suffix = text.Substring(idx + 5);
                    }

                    if (int.TryParse(suffix, out int parsed) && parsed > best)
                        best = parsed;
                }

                return best < 0 ? 0 : best;
            }
        }

        private sealed class ForagingEffect : EffectOp
        {
            private readonly TargetRef _target;
            private readonly bool _valid;

            public ForagingEffect(EffectConfig cfg, SafeExpr condition)
                : base(condition)
            {
                string targetText = !string.IsNullOrWhiteSpace(cfg?.target) ? cfg.target : cfg?.who;
                _target = new TargetRef(targetText);
                _valid = true;
            }

            public override void Apply(IWorldSnapshot snap, EvalContext ctx, ThingId self, ThingId target, List<WriteSetEntry> writes, List<FactDelta> facts, List<ThingSpawnRequest> spawns, List<PlanCooldownRequest> planCooldowns, List<ThingId> despawns, List<InventoryDelta> inventoryOps, List<CurrencyDelta> currencyOps, List<ShopTransaction> shopTransactions, List<RelationshipDelta> relationshipOps, List<CropOperation> cropOps, List<AnimalOperation> animalOps, List<MiningOperation> miningOps, List<FishingOperation> fishingOps, List<ForagingOperation> foragingOps, List<QuestOperation> questOps, HashSet<ThingId> reads)
            {
                if (!_valid || foragingOps == null)
                    return;
                if (snap == null || ctx == null)
                    return;
                if (!ShouldApply(ctx))
                    return;
                if (!_target.TryResolve(self, target, out var resolved))
                    return;

                var op = new ForagingOperation
                {
                    Kind = ForagingOperationKind.Harvest,
                    Spot = resolved,
                    Actor = self
                };

                foragingOps.Add(op);
                reads?.Add(resolved);
            }
        }

        private sealed class QuestEffect : EffectOp
        {
            private readonly TargetRef _actor;
            private readonly string _questId;
            private readonly QuestOperationKind _kind;
            private readonly string _objectiveId;
            private readonly SafeExpr _amountExpr;
            private readonly bool _grantRewards;

            public QuestEffect(EffectConfig cfg, SafeExpr condition)
                : base(condition)
            {
                string actorText = !string.IsNullOrWhiteSpace(cfg?.who) ? cfg.who : "$self";
                _actor = new TargetRef(actorText);
                _questId = (cfg?.name ?? cfg?.attr ?? string.Empty)?.Trim() ?? string.Empty;

                string opText = cfg?.op ?? cfg?.type;
                string normalized = opText?.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(normalized))
                    normalized = "quest_progress";

                switch (normalized)
                {
                    case "quest_accept":
                    case "quest-start":
                    case "quest_start":
                        _kind = QuestOperationKind.Accept;
                        break;
                    case "quest_claim":
                    case "quest_complete":
                    case "quest_finish":
                        _kind = QuestOperationKind.ClaimRewards;
                        break;
                    default:
                        _kind = QuestOperationKind.Progress;
                        break;
                }

                string objectiveId = null;
                bool grantRewards = false;
                SafeExpr amountExpr = null;
                JsonElement valueElement = cfg?.value ?? default;

                if (valueElement.ValueKind == JsonValueKind.Object)
                {
                    if (valueElement.TryGetProperty("objective", out var objProp) && objProp.ValueKind == JsonValueKind.String)
                        objectiveId = objProp.GetString();
                    if (valueElement.TryGetProperty("amount", out var amountProp))
                        amountExpr = CompileExpr(amountProp);
                    if (valueElement.TryGetProperty("grantRewards", out var grantProp))
                        grantRewards = grantProp.ValueKind == JsonValueKind.True;
                }
                else if (valueElement.ValueKind == JsonValueKind.String)
                {
                    if (_kind == QuestOperationKind.Progress)
                        objectiveId = valueElement.GetString();
                }
                else if (valueElement.ValueKind == JsonValueKind.Number && _kind == QuestOperationKind.Progress)
                {
                    amountExpr = CompileExpr(valueElement);
                }
                else if (valueElement.ValueKind == JsonValueKind.True && _kind == QuestOperationKind.ClaimRewards)
                {
                    grantRewards = true;
                }

                if (string.IsNullOrWhiteSpace(objectiveId) && !string.IsNullOrWhiteSpace(cfg?.target))
                    objectiveId = cfg.target.Trim();

                _objectiveId = objectiveId ?? string.Empty;
                _amountExpr = amountExpr ?? SafeExpr.Compile("1");
                _grantRewards = grantRewards || _kind == QuestOperationKind.ClaimRewards;
            }

            public override void Apply(
                IWorldSnapshot snap,
                EvalContext ctx,
                ThingId self,
                ThingId target,
                List<WriteSetEntry> writes,
                List<FactDelta> facts,
                List<ThingSpawnRequest> spawns,
                List<PlanCooldownRequest> planCooldowns,
                List<ThingId> despawns,
                List<InventoryDelta> inventoryOps,
                List<CurrencyDelta> currencyOps,
                List<ShopTransaction> shopTransactions,
                List<RelationshipDelta> relationshipOps,
                List<CropOperation> cropOps,
                List<AnimalOperation> animalOps,
                List<MiningOperation> miningOps,
                List<FishingOperation> fishingOps,
                List<ForagingOperation> foragingOps,
                List<QuestOperation> questOps,
                HashSet<ThingId> reads)
            {
                if (!ShouldApply(ctx))
                    return;
                if (questOps == null)
                    return;
                if (string.IsNullOrWhiteSpace(_questId))
                    return;

                if (!_actor.TryResolve(self, target, out var actor) || string.IsNullOrWhiteSpace(actor.Value))
                    actor = self;

                int amount = 1;
                if (_kind == QuestOperationKind.Progress)
                {
                    amount = (int)Math.Round(Math.Max(1.0, EvaluateNumber(_amountExpr, ctx, 1.0)));
                }

                var op = new QuestOperation
                {
                    Kind = _kind,
                    Actor = actor,
                    QuestId = _questId,
                    ObjectiveId = _objectiveId,
                    Amount = amount,
                    GrantRewards = _grantRewards
                };

                questOps.Add(op);
                if (!string.IsNullOrEmpty(actor.Value))
                    reads?.Add(actor);
            }
        }

        private sealed class CropEffect : EffectOp
        {
            private readonly TargetRef _target;
            private readonly CropOperationKind _kind;
            private readonly string _cropId;
            private readonly string _seedItemId;
            private readonly int _seedQuantity;
            private readonly bool _valid;

            public CropEffect(EffectConfig cfg, SafeExpr condition)
                : base(condition)
            {
                string targetText = !string.IsNullOrWhiteSpace(cfg?.target) ? cfg.target : cfg?.who;
                _target = new TargetRef(targetText);
                _kind = ParseKind(cfg, out _valid);
                (_cropId, _seedItemId, _seedQuantity) = ParseValue(cfg?.value, _kind);
            }

            private static CropOperationKind ParseKind(EffectConfig cfg, out bool valid)
            {
                string opText = cfg?.op ?? cfg?.type ?? string.Empty;
                opText = opText?.Trim()?.ToLowerInvariant() ?? string.Empty;
                valid = true;
                if (opText == "till" || opText == "till_soil")
                    return CropOperationKind.Till;
                if (opText == "plant" || opText == "plant_seed" || opText == "planting")
                    return CropOperationKind.Plant;
                if (opText == "water" || opText == "water_crop")
                    return CropOperationKind.Water;
                if (opText == "harvest" || opText == "harvest_crop")
                    return CropOperationKind.Harvest;

                valid = false;
                return CropOperationKind.Water;
            }

            private static (string cropId, string seedItemId, int seedQuantity) ParseValue(JsonElement? value, CropOperationKind kind)
            {
                string cropId = null;
                string seedItemId = null;
                int seedQuantity = 1;

                if (kind == CropOperationKind.Plant && value.HasValue)
                {
                    var element = value.Value;
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        cropId = element.GetString();
                    }
                    else if (element.ValueKind == JsonValueKind.Object)
                    {
                        if (element.TryGetProperty("crop", out var cropProp) && cropProp.ValueKind == JsonValueKind.String)
                            cropId = cropProp.GetString();
                        if (element.TryGetProperty("seed", out var seedProp) && seedProp.ValueKind == JsonValueKind.String)
                            seedItemId = seedProp.GetString();
                        if (element.TryGetProperty("seedItem", out var seedItemProp) && seedItemProp.ValueKind == JsonValueKind.String)
                        {
                            if (seedItemId == null)
                                seedItemId = seedItemProp.GetString();
                        }
                        if (element.TryGetProperty("seedItemId", out var seedItemIdProp) && seedItemIdProp.ValueKind == JsonValueKind.String)
                        {
                            if (seedItemId == null)
                                seedItemId = seedItemIdProp.GetString();
                        }
                        if (element.TryGetProperty("quantity", out var qtyProp) && qtyProp.ValueKind == JsonValueKind.Number)
                            seedQuantity = Math.Max(1, qtyProp.GetInt32());
                        if (element.TryGetProperty("seedQuantity", out var seedQtyProp) && seedQtyProp.ValueKind == JsonValueKind.Number)
                            seedQuantity = Math.Max(1, seedQtyProp.GetInt32());
                    }
                }
                else if (value.HasValue && value.Value.ValueKind == JsonValueKind.String)
                {
                    cropId = value.Value.GetString();
                }

                return (cropId, seedItemId, seedQuantity <= 0 ? 1 : seedQuantity);
            }

            public override void Apply(IWorldSnapshot snap, EvalContext ctx, ThingId self, ThingId target, List<WriteSetEntry> writes, List<FactDelta> facts, List<ThingSpawnRequest> spawns, List<PlanCooldownRequest> planCooldowns, List<ThingId> despawns, List<InventoryDelta> inventoryOps, List<CurrencyDelta> currencyOps, List<ShopTransaction> shopTransactions, List<RelationshipDelta> relationshipOps, List<CropOperation> cropOps, List<AnimalOperation> animalOps, List<MiningOperation> miningOps, List<FishingOperation> fishingOps, List<ForagingOperation> foragingOps, List<QuestOperation> questOps, HashSet<ThingId> reads)
            {
                if (!_valid || cropOps == null)
                    return;
                if (snap == null || ctx == null)
                    return;
                if (!ShouldApply(ctx))
                    return;
                if (!_target.TryResolve(self, target, out var resolved))
                    return;

                var op = new CropOperation
                {
                    Kind = _kind,
                    Plot = resolved,
                    Actor = self,
                    CropId = _cropId,
                    SeedItemId = _seedItemId,
                    SeedQuantity = _seedQuantity <= 0 ? 1 : _seedQuantity
                };
                cropOps.Add(op);
                reads?.Add(resolved);
            }
        }

        private static EffectOp[] CompileEffects(EffectConfig[] configs)
        {
            if (configs == null || configs.Length == 0)
                return Array.Empty<EffectOp>();

            var list = new List<EffectOp>();
            foreach (var cfg in configs)
            {
                if (cfg == null)
                    continue;

                var effectType = InferEffectType(cfg);
                if (string.IsNullOrWhiteSpace(effectType))
                    continue;

                string conditionText = !string.IsNullOrWhiteSpace(cfg.condition) ? cfg.condition : cfg.when;
                var condition = string.IsNullOrWhiteSpace(conditionText) ? null : SafeExpr.Compile(conditionText);
                switch (effectType)
                {
                    case "writeattr":
                        list.Add(new WriteAttrEffect(cfg, condition));
                        break;
                    case "writefact":
                        list.Add(new WriteFactEffect(cfg, condition));
                        break;
                    case "movestep":
                    case "move_step":
                        list.Add(new MoveStepEffect(cfg, condition));
                        break;
                    case "spawn":
                        list.Add(new SpawnEffect(cfg, condition));
                        break;
                    case "consume_nearby":
                    case "consume_nearby_item":
                    case "consume_nearby_tag":
                        list.Add(new ConsumeNearbyItemEffect(cfg, condition));
                        break;
                    case "despawn":
                        list.Add(new DespawnEffect(cfg, condition));
                        break;
                    case "plan_cooldown":
                        list.Add(new PlanCooldownEffect(cfg, condition));
                        break;
                    case "inventory":
                    case "inventory_add":
                    case "inventory_remove":
                        bool remove = string.Equals(effectType, "inventory_remove", StringComparison.OrdinalIgnoreCase);
                        if (!remove && cfg?.op != null)
                        {
                            var op = cfg.op.Trim().ToLowerInvariant();
                            if (op == "remove" || op == "take" || op == "inventory_remove")
                                remove = true;
                        }
                        list.Add(new InventoryEffect(cfg, condition, remove));
                        break;
                    case "craft":
                        list.Add(new CraftingEffect(cfg, condition));
                        break;
                    case "currency":
                        list.Add(new CurrencyEffect(cfg, condition));
                        break;
                    case "shop_transaction":
                    case "shop":
                        list.Add(new ShopTransactionEffect(cfg, condition));
                        break;
                    case "relationship":
                    case "relationship_adjust":
                        list.Add(new RelationshipEffect(cfg, condition));
                        break;
                    case "animal":
                        list.Add(new AnimalEffect(cfg, condition));
                        break;
                    case "fish":
                        list.Add(new FishingEffect(cfg, condition));
                        break;
                    case "mine":
                    case "mining":
                        list.Add(new MiningEffect(cfg, condition));
                        break;
                    case "forage":
                    case "foraging":
                        list.Add(new ForagingEffect(cfg, condition));
                        break;
                    case "quest":
                        list.Add(new QuestEffect(cfg, condition));
                        break;
                    case "crop":
                        list.Add(new CropEffect(cfg, condition));
                        break;
                    case "skill_xp":
                    case "skillxp":
                    case "skill":
                        list.Add(new SkillXpEffect(cfg, condition));
                        break;
                }
            }
            return list.ToArray();
        }

        private static string InferEffectType(EffectConfig cfg)
        {
            if (cfg == null) return null;
            if (!string.IsNullOrWhiteSpace(cfg.type))
            {
                var type = cfg.type.Trim().ToLowerInvariant();
                if (type == "quest" || type == "quests" || type == "quest_effect")
                    return "quest";
                if (type == "plan-cooldown" || type == "plan cooldown")
                    return "plan_cooldown";
                if (type == "despawn")
                    return "despawn";
                if (type == "consume-nearby" || type == "consume nearby")
                    return "consume_nearby";
                if (type == "crop" || type == "farming")
                    return "crop";
                if (type == "animal" || type == "livestock")
                    return "animal";
                if (type == "fish" || type == "fishing")
                    return "fish";
                if (type == "mine" || type == "mining")
                    return "mine";
                if (type == "craft" || type == "crafting" || type == "craft_recipe")
                    return "craft";
                return type;
            }

            var op = cfg.op;
            if (string.IsNullOrWhiteSpace(op))
                return null;

            op = op.Trim().ToLowerInvariant();
            if (op.StartsWith("attr_", StringComparison.Ordinal) || op == "add" || op == "set")
                return "writeattr";
            if (op.StartsWith("fact_", StringComparison.Ordinal))
                return "writefact";
            if (op.StartsWith("quest", StringComparison.Ordinal))
                return "quest";
            if (op == "movestep" || op == "move_step" || op == "move")
                return "movestep";
            if (op == "spawn" || op == "spawnthing" || op == "spawn_item")
                return "spawn";
            if (op == "despawn" || op == "despawnthing" || op == "despawn_item" || op == "remove")
                return "despawn";
            if (op == "plan_cooldown" || op == "plan-cooldown" || op == "plan cooldown")
                return "plan_cooldown";
            if (op == "till" || op == "till_soil" || op == "plant" || op == "plant_seed" || op == "water" || op == "water_crop" || op == "harvest" || op == "harvest_crop")
                return "crop";
            if (op == "animal_feed" || op == "feed_animal" || op == "feed_livestock" || op == "livestock_feed" ||
                op == "animal_brush" || op == "brush_animal" || op == "groom_animal" ||
                op == "animal_collect" || op == "collect_produce" || op == "collect_eggs" || op == "collect_milk" || op == "harvest_animal")
                return "animal";
            if (op == "fish" || op == "cast" || op == "go_fish" || op == "fishing")
                return "fish";
            if (op == "craft" || op == "craft_recipe" || op == "crafting")
                return "craft";
            if (op == "skill" || op == "skill_xp" || op == "skillxp" || op == "award_skill")
                return "skill_xp";
            return null;
        }

        private static SafeExpr CompileExpr(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
                return null;
            if (element.ValueKind == JsonValueKind.String)
            {
                var text = element.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : SafeExpr.Compile(text);
            }
            return SafeExpr.Compile(element.GetRawText());
        }

        private static double EvaluateNumber(SafeExpr expr, EvalContext ctx, double fallback)
        {
            if (expr == null) return fallback;
            try
            {
                var v = expr.EvalNumber(ctx);
                if (double.IsNaN(v) || double.IsInfinity(v)) return fallback;
                return v;
            }
            catch
            {
                return fallback;
            }
        }

        private sealed class TargetSelectorModel
        {
            public string Type { get; set; }
            public string Tag { get; set; }
            public bool ExcludeSelf { get; set; }
            public SafeExpr Filter { get; set; }
        }

        private sealed class GoalActionModel
        {
            public ActionModel Action { get; set; }
            public TargetSelectorModel Target { get; set; }
            public bool MoveToTarget { get; set; }
        }

        private sealed class GoalModel
        {
            public GoalConfig Config { get; }
            public SafeExpr PriorityExpr { get; }
            public SafeExpr[] SatisfiedExprs { get; }
            public GoalActionModel[] Actions { get; }

            public GoalModel(GoalConfig config, GoalActionModel[] actions)
            {
                Config = config ?? throw new ArgumentNullException(nameof(config));
                if (string.IsNullOrWhiteSpace(config.id))
                    throw new ArgumentException("Goal id must be provided.", nameof(config));

                if (!string.IsNullOrWhiteSpace(config.priority))
                    PriorityExpr = SafeExpr.Compile(config.priority);
                SatisfiedExprs = (config.satisfiedWhen ?? Array.Empty<string>())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(SafeExpr.Compile)
                    .ToArray();
                Actions = actions ?? Array.Empty<GoalActionModel>();
            }
        }

        private readonly Dictionary<string, ActionModel> _actions;
        private readonly List<GoalModel> _goals;
        private readonly ActionModel _moveAction;
        private readonly ActionModel _waitAction;
        private readonly IReservationQuery _reservationQuery;
        private readonly IInventoryQuery _inventoryQuery;
        private readonly ICropQuery _cropQuery;
        private readonly IAnimalQuery _animalQuery;
        private readonly IFishingQuery _fishingQuery;
        private readonly IMiningQuery _miningQuery;
        private readonly RoleScheduleService _scheduleService;
        private readonly IForagingQuery _foragingQuery;
        private readonly IQuestQuery _questQuery;
        private readonly IWeatherQuery _weatherQuery;
        private readonly ICraftingQuery _craftingQuery;
        private readonly ISkillProgression _skillProgression;

        public JsonDrivenPlanner(IEnumerable<ActionConfig> actions, IEnumerable<GoalConfig> goals, IReservationQuery reservationQuery = null, RoleScheduleService scheduleService = null, IInventoryQuery inventoryQuery = null, ICropQuery cropQuery = null, IAnimalQuery animalQuery = null, IFishingQuery fishingQuery = null, IMiningQuery miningQuery = null, IForagingQuery foragingQuery = null, IQuestQuery questQuery = null, IWeatherQuery weatherQuery = null, ICraftingQuery craftingQuery = null, ISkillProgression skillProgression = null)
        {
            if (actions == null) throw new ArgumentNullException(nameof(actions));
            if (goals == null) throw new ArgumentNullException(nameof(goals));

            _actions = new Dictionary<string, ActionModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var cfg in actions)
            {
                if (cfg == null) continue;
                var model = new ActionModel(cfg);
                _actions[model.Id] = model;
            }
            if (_actions.Count == 0)
                throw new InvalidOperationException("actions.json must define at least one action.");

            _goals = new List<GoalModel>();
            foreach (var goalCfg in goals)
            {
                if (goalCfg == null) continue;
                var goalActions = BuildGoalActions(goalCfg.actions);
                var model = new GoalModel(goalCfg, goalActions);
                _goals.Add(model);
            }
            if (_goals.Count == 0)
                throw new InvalidOperationException("goals.json must define at least one goal.");

            _moveAction = _actions.TryGetValue("move_to", out var mv) ? mv : null;
            _waitAction = _actions.TryGetValue("wait", out var wait) ? wait : null;
            _reservationQuery = reservationQuery;
            _inventoryQuery = inventoryQuery;
            _cropQuery = cropQuery;
            _animalQuery = animalQuery;
            _fishingQuery = fishingQuery;
            _miningQuery = miningQuery;
            _foragingQuery = foragingQuery;
            _scheduleService = scheduleService;
            _questQuery = questQuery;
            _weatherQuery = weatherQuery;
            _craftingQuery = craftingQuery;
            _skillProgression = skillProgression;
        }

        public Plan Plan(IWorldSnapshot snap, ThingId self, Goal goal, double priorityJitter = 0.0, Random rng = null)
        {
            if (snap == null) throw new ArgumentNullException(nameof(snap));
            var me = snap.GetThing(self);
            if (me == null) return new Plan();

            if (goal != null && !string.IsNullOrWhiteSpace(goal.Id))
            {
                var requested = _goals.FirstOrDefault(g => string.Equals(g.Config.id, goal.Id, StringComparison.OrdinalIgnoreCase));
                if (requested != null)
                {
                    if (IsGoalSatisfied(requested, snap, self)) return new Plan();
                    var planForRequested = BuildPlanForGoal(requested, snap, self);
                    return planForRequested ?? new Plan();
                }
            }

            Plan schedulePlan = null;
            double schedulePriority = 0.0;
            if (_scheduleService != null)
            {
                schedulePlan = TryBuildSchedulePlan(snap, self, out schedulePriority);
                if (schedulePlan != null && schedulePlan.IsEmpty)
                {
                    schedulePlan = null;
                    schedulePriority = 0.0;
                }
            }

            var orderedGoals = new List<(GoalModel goal, double priority)>();
            foreach (var g in _goals)
            {
                if (IsGoalSatisfied(g, snap, self)) continue;
                double priority = EvaluatePriority(g, snap, self);
                if (priority > 0.0)
                {
                    if (priorityJitter > 0.0 && rng != null)
                    {
                        priority += rng.NextDouble() * priorityJitter;
                    }
                    orderedGoals.Add((g, priority));
                }
            }

            if (orderedGoals.Count == 0)
                return new Plan();

            orderedGoals.Sort((a, b) => b.priority.CompareTo(a.priority));
            foreach (var candidate in orderedGoals)
            {
                if (schedulePlan != null && schedulePriority >= candidate.priority)
                    return schedulePlan;

                var plan = BuildPlanForGoal(candidate.goal, snap, self);
                if (plan != null && !plan.IsEmpty)
                    return plan;
            }

            if (schedulePlan != null)
                return schedulePlan;

            return new Plan();
        }

        private Plan TryBuildSchedulePlan(IWorldSnapshot snap, ThingId self, out double priority)
        {
            priority = 0.0;
            if (_scheduleService == null)
                return null;
            if (!_scheduleService.TryEvaluate(snap, self, out var evaluation))
                return null;

            var block = evaluation.ActiveBlock ?? evaluation.UpcomingBlock;
            if (block == null)
                return null;

            if (string.IsNullOrEmpty(evaluation.EffectiveGotoTag) && string.IsNullOrEmpty(block.GotoTag))
                return null;

            if (string.IsNullOrEmpty(evaluation.TargetId.Value))
                return null;

            var selfThing = snap.GetThing(self);
            if (selfThing == null)
                return null;

            if (!evaluation.HasActiveBlock)
            {
                if (double.IsPositiveInfinity(evaluation.MinutesUntilStart) || evaluation.MinutesUntilStart > 120.0)
                    return null;
                priority = Math.Max(0.0, 2.5 - (evaluation.MinutesUntilStart / 60.0));
                if (priority <= 0.0)
                    return null;
            }
            else
            {
                priority = 4.0 + Math.Min(2.0, Math.Max(0.0, evaluation.MinutesIntoBlock / 20.0));
            }

            double health = selfThing.AttrOrDefault("health", 1.0);
            if (health < 0.35)
                priority *= 0.5;

            double hunger = selfThing.AttrOrDefault("hunger", 0.0);
            if (hunger > 0.85)
                priority *= 0.6;

            var plan = new Plan { GoalId = "follow_schedule" };

            if (_moveAction != null)
            {
                int distance = snap.Distance(self, evaluation.TargetId);
                if (distance > 1)
                {
                    var moveStep = CreatePlanStep(_moveAction, self, evaluation.TargetId);
                    if (moveStep != null)
                        plan.Add(moveStep);
                }
            }

            ActionModel taskAction = null;
            string taskId = !string.IsNullOrWhiteSpace(evaluation.EffectiveTask) ? evaluation.EffectiveTask : block.Task;
            if (!string.IsNullOrWhiteSpace(taskId) && _actions.TryGetValue(taskId, out var configured))
            {
                taskAction = configured;
            }
            else if (_waitAction != null)
            {
                taskAction = _waitAction;
            }

            if (taskAction != null)
            {
                ThingId target = taskAction == _waitAction ? self : evaluation.TargetId;
                var step = CreatePlanStep(taskAction, self, target);
                if (step != null)
                    plan.Add(step);
            }

            if (plan.IsEmpty)
                return null;

            return plan;
        }

        private GoalActionModel[] BuildGoalActions(GoalActionConfig[] configs)
        {
            if (configs == null || configs.Length == 0)
                return Array.Empty<GoalActionModel>();

            var list = new List<GoalActionModel>();
            foreach (var cfg in configs)
            {
                if (cfg == null || string.IsNullOrWhiteSpace(cfg.id))
                    continue;

                if (!_actions.TryGetValue(cfg.id, out var action))
                    throw new InvalidOperationException($"Goal references unknown action '{cfg.id}'.");

                var selector = BuildTargetSelector(cfg.target);
                list.Add(new GoalActionModel { Action = action, Target = selector, MoveToTarget = cfg.moveToTarget });
            }
            return list.ToArray();
        }

        private TargetSelectorModel BuildTargetSelector(TargetSelectorConfig cfg)
        {
            if (cfg == null) return null;
            return new TargetSelectorModel
            {
                Type = string.IsNullOrWhiteSpace(cfg.type) ? "self" : cfg.type,
                Tag = cfg.tag,
                ExcludeSelf = cfg.excludeSelf,
                Filter = string.IsNullOrWhiteSpace(cfg.where) ? null : SafeExpr.Compile(cfg.where)
            };
        }

        private bool IsGoalSatisfied(GoalModel goal, IWorldSnapshot snap, ThingId self)
        {
            if (goal.SatisfiedExprs.Length == 0) return false;
            var ctx = new EvalContext(snap, _reservationQuery, _inventoryQuery, _cropQuery, _animalQuery, _fishingQuery, _miningQuery, _foragingQuery, _questQuery, _weatherQuery, _craftingQuery, _skillProgression);
            ctx.Vars["$self"] = self;
            ThingId? satisfiedTarget = null;
            var targetSource = goal.Actions?.FirstOrDefault(a => a?.Target != null);
            if (targetSource?.Target != null)
                satisfiedTarget = ResolveTarget(targetSource.Target, snap, self);
            ctx.Vars["$target"] = satisfiedTarget ?? new ThingId(string.Empty);
            foreach (var expr in goal.SatisfiedExprs)
            {
                if (!expr.EvalBool(ctx))
                    return false;
            }
            return true;
        }

        private double EvaluatePriority(GoalModel goal, IWorldSnapshot snap, ThingId self)
        {
            if (goal.PriorityExpr == null) return 0.0;
            var ctx = new EvalContext(snap, _reservationQuery, _inventoryQuery, _cropQuery, _animalQuery, _fishingQuery, _miningQuery, _foragingQuery, _questQuery, _weatherQuery, _craftingQuery, _skillProgression);
            ctx.Vars["$self"] = self;
            var value = goal.PriorityExpr.EvalNumber(ctx);
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0.0;
            if (value < 0) value = 0.0;
            return value;
        }

        private Plan BuildPlanForGoal(GoalModel goal, IWorldSnapshot snap, ThingId self)
        {
            var plan = new Plan();
            var goalIdAssigned = false;

            foreach (var action in goal.Actions)
            {
                if (action?.Action == null) continue;

                var targetId = ResolveTarget(action.Target, snap, self);
                if (action.Target != null && !targetId.HasValue)
                    continue;

                var mainTarget = targetId ?? self;
                var mainStep = CreatePlanStep(action.Action, self, mainTarget);
                if (mainStep == null)
                    continue;

                if (action.MoveToTarget && targetId.HasValue && _moveAction != null)
                {
                    var moveStep = CreatePlanStep(_moveAction, self, targetId.Value);
                    if (moveStep != null)
                    {
                        plan.Add(moveStep);
                        if (!goalIdAssigned)
                        {
                            plan.GoalId = goal.Config.id;
                            goalIdAssigned = true;
                        }
                    }
                }

                plan.Add(mainStep);
                if (!goalIdAssigned)
                {
                    plan.GoalId = goal.Config.id;
                    goalIdAssigned = true;
                }
            }

            if (!plan.IsEmpty)
                return plan;

            return new Plan();
        }

        private PlanStep CreatePlanStep(ActionModel action, ThingId self, ThingId target)
        {
            if (string.IsNullOrWhiteSpace(action.Config.activity))
                return null;

            var step = new PlanStep(action.Config.activity)
            {
                Actor = self,
                Target = target,
                Reservations = BuildReservations(action, self, target),
                Preconditions = BuildPreconditions(action, self, target),
                DurationSeconds = BuildDuration(action, self, target),
                BuildEffects = BuildEffectsFactory(action, self, target)
            };
            return step;
        }

        private IReadOnlyList<Reservation> BuildReservations(ActionModel action, ThingId self, ThingId target)
        {
            var cfg = action.Config.reservations;
            if (cfg == null || cfg.Length == 0)
                return new[] { new Reservation(self, ReservationMode.Soft, 1) };

            var list = new List<Reservation>();
            foreach (var r in cfg)
            {
                if (r == null || string.IsNullOrWhiteSpace(r.thing))
                    continue;
                var resolved = ResolveThingReference(r.thing, self, target);
                if (!resolved.HasValue)
                    continue;
                var mode = string.Equals(r.mode, "hard", StringComparison.OrdinalIgnoreCase)
                    ? ReservationMode.Hard
                    : ReservationMode.Soft;
                list.Add(new Reservation(resolved.Value, mode, r.priority));
            }
            return list.Count == 0 ? new[] { new Reservation(self, ReservationMode.Soft, 1) } : list.ToArray();
        }

        private ThingId? ResolveThingReference(string text, ThingId self, ThingId target)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (string.Equals(text, "$self", StringComparison.OrdinalIgnoreCase)) return self;
            if (string.Equals(text, "$target", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrEmpty(target.Value) ? (ThingId?)null : target;
            }
            return new ThingId(text);
        }

        private Func<IWorldSnapshot, bool> BuildPreconditions(ActionModel action, ThingId self, ThingId target)
        {
            if (action.Preconditions.Length == 0)
                return null;

            return snap =>
            {
                var ctx = new EvalContext(snap, _reservationQuery, _inventoryQuery, _cropQuery, _animalQuery, _fishingQuery, _miningQuery, _foragingQuery, _questQuery, _weatherQuery, _craftingQuery, _skillProgression);
                ctx.Vars["$self"] = self;
                ctx.Vars["$target"] = target;
                foreach (var pre in action.Preconditions)
                {
                    if (!pre.EvalBool(ctx))
                        return false;
                }
                return true;
            };
        }

        private Func<IWorldSnapshot, double> BuildDuration(ActionModel action, ThingId self, ThingId target)
        {
            if (action.DurationExpr == null)
                return null;

            return snap =>
            {
                var ctx = new EvalContext(snap, _reservationQuery, _inventoryQuery, _cropQuery, _animalQuery, _fishingQuery, _miningQuery, _foragingQuery, _questQuery, _weatherQuery, _craftingQuery, _skillProgression);
                ctx.Vars["$self"] = self;
                ctx.Vars["$target"] = target;
                var value = action.DurationExpr.EvalNumber(ctx);
                if (double.IsNaN(value) || double.IsInfinity(value)) return 0.0;
                if (value < 0) value = 0.0;
                if (value > 120) value = 120;
                return value;
            };
        }

        private Func<IWorldSnapshot, EffectBatch> BuildEffectsFactory(ActionModel action, ThingId self, ThingId target)
        {
            var ops = action.Effects;
            if (ops == null || ops.Length == 0)
            {
                return snap => new EffectBatch
                {
                    BaseVersion = snap.Version,
                    Reads = new[] { new ReadSetEntry(self, null, null) },
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
                    FishingOps = Array.Empty<FishingOperation>(),
                    ForagingOps = Array.Empty<ForagingOperation>(),
                    QuestOps = Array.Empty<QuestOperation>()
                };
            }

            return snap =>
            {
                var ctx = new EvalContext(snap, _reservationQuery, _inventoryQuery, _cropQuery, _animalQuery, _fishingQuery, _miningQuery, _foragingQuery, _questQuery, _weatherQuery, _craftingQuery, _skillProgression);
                ctx.Vars["$self"] = self;
                ctx.Vars["$target"] = target;

                var writes = new List<WriteSetEntry>();
                var facts = new List<FactDelta>();
                var spawns = new List<ThingSpawnRequest>();
                var planCooldowns = new List<PlanCooldownRequest>();
                var despawns = new List<ThingId>();
                var inventoryOps = new List<InventoryDelta>();
                var currencyOps = new List<CurrencyDelta>();
                var shopTransactions = new List<ShopTransaction>();
                var relationshipOps = new List<RelationshipDelta>();
                var cropOps = new List<CropOperation>();
                var animalOps = new List<AnimalOperation>();
                var miningOps = new List<MiningOperation>();
                var fishingOps = new List<FishingOperation>();
                var foragingOps = new List<ForagingOperation>();
                var questOps = new List<QuestOperation>();
                var readIds = new HashSet<ThingId>();
                readIds.Add(self);
                if (!string.IsNullOrEmpty(target.Value)) readIds.Add(target);

                foreach (var op in ops)
                {
                    try
                    {
                        op?.Apply(snap, ctx, self, target, writes, facts, spawns, planCooldowns, despawns, inventoryOps, currencyOps, shopTransactions, relationshipOps, cropOps, animalOps, miningOps, fishingOps, foragingOps, questOps, readIds);
                    }
                    catch
                    {
                        // Ignore malformed effect application so one bad config doesn't crash the planner.
                    }
                }

                var reads = readIds.Select(id => new ReadSetEntry(id, null, null)).ToArray();
                return new EffectBatch
                {
                    BaseVersion = snap.Version,
                    Reads = reads,
                    Writes = writes.ToArray(),
                    FactDeltas = facts.ToArray(),
                    Spawns = spawns.ToArray(),
                    PlanCooldowns = planCooldowns.ToArray(),
                    Despawns = despawns.Distinct().ToArray(),
                    InventoryOps = inventoryOps.ToArray(),
                    CurrencyOps = currencyOps.ToArray(),
                    ShopTransactions = shopTransactions.ToArray(),
                    RelationshipOps = relationshipOps.ToArray(),
                    CropOps = cropOps.ToArray(),
                    AnimalOps = animalOps.ToArray(),
                    MiningOps = miningOps.ToArray(),
                    FishingOps = fishingOps.ToArray(),
                    ForagingOps = foragingOps.ToArray(),
                    QuestOps = questOps.ToArray()
                };
            };
        }

        private ThingId? ResolveTarget(TargetSelectorModel selector, IWorldSnapshot snap, ThingId self)
        {
            if (selector == null)
                return self;

            var type = selector.Type?.Trim();
            if (string.IsNullOrWhiteSpace(type) || string.Equals(type, "self", StringComparison.OrdinalIgnoreCase))
                return self;

            if (string.Equals(type, "nearesttag", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "nearest_tag", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveNearestByTag(selector, snap, self);
            }

            if (string.Equals(type, "randomtag", StringComparison.OrdinalIgnoreCase))
            {
                var candidates = ResolveCandidates(selector, snap, self).ToList();
                if (candidates.Count == 0) return null;
                // deterministic order for repeatability
                return candidates.OrderBy(c => c.Id.Value, StringComparer.Ordinal).First().Id;
            }

            return null;
        }

        private ThingId? ResolveNearestByTag(TargetSelectorModel selector, IWorldSnapshot snap, ThingId self)
        {
            var selfThing = snap.GetThing(self);
            ThingView best = null;
            double bestPreference = double.NegativeInfinity;
            int bestDist = int.MaxValue;

            foreach (var candidate in ResolveCandidates(selector, snap, self))
            {
                double preference = ComputeSocialPreference(selfThing, candidate.Id);
                int dist = snap.Distance(self, candidate.Id);

                if (preference > bestPreference + 1e-6)
                {
                    bestPreference = preference;
                    bestDist = dist;
                    best = candidate;
                    continue;
                }

                if (Math.Abs(preference - bestPreference) < 1e-6 && dist < bestDist)
                {
                    bestDist = dist;
                    best = candidate;
                }
            }

            return best?.Id;
        }

        private static double ComputeSocialPreference(ThingView selfThing, ThingId candidateId)
        {
            if (selfThing?.Attributes == null)
                return 0.0;
            if (string.IsNullOrEmpty(candidateId.Value))
                return 0.0;

            string prefix = SocialRelationshipSystem.AttributePrefix + ".";
            string suffix = "." + candidateId.Value;
            double total = 0.0;
            int count = 0;

            foreach (var kvp in selfThing.Attributes)
            {
                var key = kvp.Key;
                if (string.IsNullOrEmpty(key))
                    continue;
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                double value = kvp.Value;
                if (double.IsNaN(value) || double.IsInfinity(value))
                    continue;

                total += value;
                count++;
            }

            if (count == 0)
                return 0.0;

            return total / count;
        }

        private IEnumerable<ThingView> ResolveCandidates(TargetSelectorModel selector, IWorldSnapshot snap, ThingId self)
        {
            if (selector == null)
                yield break;

            IEnumerable<ThingView> source = Array.Empty<ThingView>();
            if (!string.IsNullOrWhiteSpace(selector.Tag))
            {
                source = snap.QueryByTag(selector.Tag) ?? Array.Empty<ThingView>();
            }

            foreach (var candidate in source)
            {
                if (candidate == null)
                    continue;

                var candidateId = candidate.Id;
                if (selector.ExcludeSelf && candidate.Id.Equals(self))
                    continue;

                if (_reservationQuery != null && _reservationQuery.HasActiveReservation(candidateId, self))
                    continue;

                if (selector.Filter != null)
                {
                    var ctx = new EvalContext(snap, _reservationQuery, _inventoryQuery, _cropQuery, _animalQuery, _fishingQuery, _miningQuery, _foragingQuery, _questQuery, _weatherQuery, _craftingQuery, _skillProgression);
                    ctx.Vars["$self"] = self;
                    ctx.Vars["$target"] = candidateId;
                    if (!selector.Filter.EvalBool(ctx))
                        continue;
                }

                yield return candidate;
            }
        }

        private static double Clamp01(double value)
        {
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }
    }
}
