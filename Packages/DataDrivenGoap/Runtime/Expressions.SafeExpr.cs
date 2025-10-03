using System;
using System.Collections.Generic;
using System.Globalization;
using DataDrivenGoap.Core;
using DataDrivenGoap.Items;

namespace DataDrivenGoap.Expressions
{
    public sealed class EvalContext
    {
        public IWorldSnapshot Snapshot { get; }
        public Dictionary<string, object> Vars { get; }
        public IReservationQuery ReservationQuery { get; }
        public IInventoryQuery InventoryQuery { get; }
        public ICropQuery CropQuery { get; }
        public IAnimalQuery AnimalQuery { get; }
        public IFishingQuery FishingQuery { get; }
        public IForagingQuery ForagingQuery { get; }
        public IMiningQuery MiningQuery { get; }
        public IQuestQuery QuestQuery { get; }
        public IWeatherQuery WeatherQuery { get; }
        public ICraftingQuery CraftingQuery { get; }
        public ISkillProgression SkillProgression { get; }
        public EvalContext(IWorldSnapshot snap, IReservationQuery reservationQuery = null, IInventoryQuery inventoryQuery = null, ICropQuery cropQuery = null, IAnimalQuery animalQuery = null, IFishingQuery fishingQuery = null, IMiningQuery miningQuery = null, IForagingQuery foragingQuery = null, IQuestQuery questQuery = null, IWeatherQuery weatherQuery = null, ICraftingQuery craftingQuery = null, ISkillProgression skillProgression = null)
        {
            Snapshot = snap ?? throw new ArgumentNullException(nameof(snap));
            Vars = new Dictionary<string, object>(StringComparer.Ordinal);
            ReservationQuery = reservationQuery;
            InventoryQuery = inventoryQuery;
            CropQuery = cropQuery;
            AnimalQuery = animalQuery;
            FishingQuery = fishingQuery;
            MiningQuery = miningQuery;
            ForagingQuery = foragingQuery;
            QuestQuery = questQuery;
            WeatherQuery = weatherQuery;
            CraftingQuery = craftingQuery;
            SkillProgression = skillProgression;
        }
    }

    /// <summary>
    /// Tiny safe expression evaluator used for GOAP configs.
    /// Supports: numbers, + - * /, !, && ||, comparisons, parentheses,
    /// variables like $self/$target (as values in EvalContext.Vars),
    /// and functions: distance($a,$b), attr($thing,"key"), clamp(v,lo,hi), min(a,b), max(a,b).
    /// </summary>
    public sealed class SafeExpr
    {
        private readonly List<Token> _tokens;
        private SafeExpr(List<Token> tokens) { _tokens = tokens; }

        public static SafeExpr Compile(string expr)
        {
            var tz = new Tokenizer(expr ?? "0");
            return new SafeExpr(tz.Tokenize());
        }

        public double EvalNumber(EvalContext ctx) { int i = 0; return ParseOr(ref i, ctx); }
        public bool EvalBool(EvalContext ctx) => Math.Abs(EvalNumber(ctx)) > 1e-12;

        // or -> and -> cmp -> add -> mul -> unary -> primary
        private double ParseOr(ref int i, EvalContext ctx)
        {
            double a = ParseAnd(ref i, ctx);
            while (Match(ref i, TokenKind.Logic, "||"))
            {
                double b = ParseAnd(ref i, ctx);
                a = (AsBool(a) || AsBool(b)) ? 1.0 : 0.0;
            }
            return a;
        }

        private double ParseAnd(ref int i, EvalContext ctx)
        {
            double a = ParseCmp(ref i, ctx);
            while (Match(ref i, TokenKind.Logic, "&&"))
            {
                double b = ParseCmp(ref i, ctx);
                a = (AsBool(a) && AsBool(b)) ? 1.0 : 0.0;
            }
            return a;
        }

        private double ParseCmp(ref int i, EvalContext ctx)
        {
            double a = ParseAdd(ref i, ctx);
            if (Peek(i).Kind == TokenKind.Cmp)
            {
                string op = Peek(i).Text; i++;
                double b = ParseAdd(ref i, ctx);
                switch (op)
                {
                    case "==": return Math.Abs(a - b) < 1e-9 ? 1.0 : 0.0;
                    case "!=": return Math.Abs(a - b) >= 1e-9 ? 1.0 : 0.0;
                    case ">": return a > b ? 1.0 : 0.0;
                    case "<": return a < b ? 1.0 : 0.0;
                    case ">=": return a >= b ? 1.0 : 0.0;
                    case "<=": return a <= b ? 1.0 : 0.0;
                }
            }
            return a;
        }

        private double ParseAdd(ref int i, EvalContext ctx)
        {
            double a = ParseMul(ref i, ctx);
            while (true)
            {
                if (Match(ref i, TokenKind.Op, "+")) a += ParseMul(ref i, ctx);
                else if (Match(ref i, TokenKind.Op, "-")) a -= ParseMul(ref i, ctx);
                else break;
            }
            return a;
        }

        private double ParseMul(ref int i, EvalContext ctx)
        {
            double a = ParseUnary(ref i, ctx);
            while (true)
            {
                if (Match(ref i, TokenKind.Op, "*")) a *= ParseUnary(ref i, ctx);
                else if (Match(ref i, TokenKind.Op, "/"))
                {
                    double b = ParseUnary(ref i, ctx);
                    a = Math.Abs(b) < 1e-12 ? 0.0 : a / b;
                }
                else break;
            }
            return a;
        }

        private double ParseUnary(ref int i, EvalContext ctx)
        {
            if (Match(ref i, TokenKind.Op, "+")) return +ParseUnary(ref i, ctx);
            if (Match(ref i, TokenKind.Op, "-")) return -ParseUnary(ref i, ctx);
            if (Match(ref i, TokenKind.Op, "!")) return AsBool(ParseUnary(ref i, ctx)) ? 0.0 : 1.0;
            return ParsePrimary(ref i, ctx);
        }

        private double ParsePrimary(ref int i, EvalContext ctx)
        {
            var t = Peek(i);
            if (t.Kind == TokenKind.Number) { i++; return t.Number; }
            if (t.Kind == TokenKind.String) { i++; return 0.0; }
            if (t.Kind == TokenKind.DollarVar) { i++; return 0.0; } // marker only; actual values passed as args

            // boolean literals true/false
            if (t.Kind == TokenKind.Ident)
            {
                if (string.Equals(t.Text, "true", StringComparison.OrdinalIgnoreCase)) { i++; return 1.0; }
                if (string.Equals(t.Text, "false", StringComparison.OrdinalIgnoreCase)) { i++; return 0.0; }
            }

            // function call: ident '(' args? ')'
            if (t.Kind == TokenKind.Ident && Peek(i + 1).Kind == TokenKind.LParen)
            {
                string name = t.Text; i += 2; // skip ident + '('
                var args = new List<object>();
                if (Peek(i).Kind != TokenKind.RParen)
                {
                    while (true)
                    {
                        args.Add(ParseArg(ref i, ctx));
                        if (Match(ref i, TokenKind.Comma, ",")) continue;
                        break;
                    }
                }
                Expect(ref i, TokenKind.RParen, ")");
                return EvalFunction(name, args.ToArray(), ctx);
            }

            if (Match(ref i, TokenKind.LParen, "("))
            {
                double v = ParseOr(ref i, ctx);
                Expect(ref i, TokenKind.RParen, ")");
                return v;
            }

            throw new InvalidOperationException("Unexpected token: " + t.Text);
        }

        private object ParseArg(ref int i, EvalContext ctx)
        {
            var t = Peek(i);
            if (t.Kind == TokenKind.String) { i++; return t.Text; }
            if (t.Kind == TokenKind.DollarVar) { i++; return ResolveVar(t.Text, ctx); }
            double v = ParseOr(ref i, ctx);
            return v;
        }

        private static bool AsBool(double d) => Math.Abs(d) > 1e-12;

        private object ResolveVar(string name, EvalContext ctx)
        {
            if (ctx == null) return 0.0;
            return ctx.Vars.TryGetValue(name, out var v) ? v : 0.0;
        }

        private static void ParseQuestArgs(object[] args, EvalContext ctx, out ThingId actor, out string questId, out string objectiveId)
        {
            actor = AsThingId("$self", ctx);
            questId = string.Empty;
            objectiveId = string.Empty;

            if (args == null || args.Length == 0)
                return;

            if (args.Length == 1)
            {
                questId = AsString(args[0]);
                return;
            }

            actor = AsThingId(args[0], ctx);
            questId = AsString(args[1]);

            if (args.Length >= 3)
                objectiveId = AsString(args[2]);
        }

        private double EvalFunction(string name, object[] args, EvalContext ctx)
        {
            if (string.Equals(name, "time_hours", StringComparison.OrdinalIgnoreCase))
            {
                var time = ctx?.Snapshot?.Time;
                return time?.TimeOfDay.TotalHours ?? 0.0;
            }
            if (string.Equals(name, "day_of_week", StringComparison.OrdinalIgnoreCase))
            {
                var time = ctx?.Snapshot?.Time;
                if (time == null) return 0.0;
                int day = ((time.DayOfYear - 1) % 7 + 7) % 7;
                return day;
            }
            if (string.Equals(name, "is_open", StringComparison.OrdinalIgnoreCase))
            {
                var targetId = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (string.IsNullOrEmpty(targetId.Value)) return 0.0;
                var snapshot = ctx?.Snapshot;
                var thing = snapshot?.GetThing(targetId);
                if (thing == null) return 0.0;
                var building = thing.Building;
                if (building != null)
                    return building.IsOpen(snapshot?.Time) ? 1.0 : 0.0;
                return thing.AttrOrDefault("open", 1.0) > 0.5 ? 1.0 : 0.0;
            }
            if (string.Equals(name, "crop_ready_count", StringComparison.OrdinalIgnoreCase))
            {
                return ctx?.CropQuery?.CountReadyCrops() ?? 0.0;
            }
            if (string.Equals(name, "crop_tilled", StringComparison.OrdinalIgnoreCase))
            {
                var plot = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.CropQuery != null && ctx.CropQuery.TryGet(plot, out var state) && state.Exists)
                    return state.Tilled ? 1.0 : 0.0;
                return 0.0;
            }
            if (string.Equals(name, "crop_has", StringComparison.OrdinalIgnoreCase))
            {
                var plot = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.CropQuery != null && ctx.CropQuery.TryGet(plot, out var state) && state.Exists)
                    return string.IsNullOrEmpty(state.CropId) ? 0.0 : 1.0;
                return 0.0;
            }
            if (string.Equals(name, "crop_empty", StringComparison.OrdinalIgnoreCase))
            {
                var plot = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.CropQuery != null && ctx.CropQuery.TryGet(plot, out var state) && state.Exists)
                    return string.IsNullOrEmpty(state.CropId) ? 1.0 : 0.0;
                return 0.0;
            }
            if (string.Equals(name, "crop_ready", StringComparison.OrdinalIgnoreCase))
            {
                var plot = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.CropQuery != null && ctx.CropQuery.TryGet(plot, out var state) && state.Exists)
                    return state.ReadyToHarvest ? 1.0 : 0.0;
                return 0.0;
            }
            if (string.Equals(name, "crop_watered", StringComparison.OrdinalIgnoreCase))
            {
                var plot = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.CropQuery != null && ctx.CropQuery.TryGet(plot, out var state) && state.Exists)
                    return state.Watered ? 1.0 : 0.0;
                return 0.0;
            }
            if (string.Equals(name, "crop_needs_water", StringComparison.OrdinalIgnoreCase))
            {
                var plot = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.CropQuery != null && ctx.CropQuery.TryGet(plot, out var state) && state.Exists)
                {
                    bool hasCrop = !string.IsNullOrEmpty(state.CropId);
                    return (hasCrop && !state.Watered) ? 1.0 : 0.0;
                }
                return 0.0;
            }
            if (string.Equals(name, "crop_stage", StringComparison.OrdinalIgnoreCase))
            {
                var plot = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.CropQuery != null && ctx.CropQuery.TryGet(plot, out var state) && state.Exists)
                    return state.Stage;
                return 0.0;
            }
            if (string.Equals(name, "crop_regrow_counter", StringComparison.OrdinalIgnoreCase))
            {
                var plot = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.CropQuery != null && ctx.CropQuery.TryGet(plot, out var state) && state.Exists)
                    return state.RegrowCounter;
                return 0.0;
            }

            if (string.Equals(name, "animal_hunger", StringComparison.OrdinalIgnoreCase))
            {
                var animal = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.AnimalQuery != null && ctx.AnimalQuery.TryGet(animal, out var state) && state.Exists)
                    return Math.Clamp(state.Hunger, 0.0, 1.0);
                return 0.0;
            }

            if (string.Equals(name, "animal_hungry", StringComparison.OrdinalIgnoreCase))
            {
                var animal = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.AnimalQuery != null && ctx.AnimalQuery.TryGet(animal, out var state) && state.Exists)
                    return state.Hunger >= 0.5 ? 1.0 : 0.0;
                return 0.0;
            }

            if (string.Equals(name, "animal_happiness", StringComparison.OrdinalIgnoreCase))
            {
                var animal = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.AnimalQuery != null && ctx.AnimalQuery.TryGet(animal, out var state) && state.Exists)
                    return Math.Clamp(state.Happiness, 0.0, 1.0);
                return 0.0;
            }

            if (string.Equals(name, "animal_has_produce", StringComparison.OrdinalIgnoreCase))
            {
                var animal = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.AnimalQuery != null && ctx.AnimalQuery.TryGet(animal, out var state) && state.Exists)
                    return state.HasProduce ? 1.0 : 0.0;
                return 0.0;
            }

            if (string.Equals(name, "animal_produce_ready_in", StringComparison.OrdinalIgnoreCase))
            {
                var animal = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.AnimalQuery != null && ctx.AnimalQuery.TryGet(animal, out var state) && state.Exists)
                    return Math.Max(0.0, state.ProduceReadyInHours);
                return 0.0;
            }

            if (string.Equals(name, "fish_spot_count", StringComparison.OrdinalIgnoreCase))
            {
                return ctx?.FishingQuery?.CountAvailableSpots() ?? 0.0;
            }

            if (string.Equals(name, "fish_available", StringComparison.OrdinalIgnoreCase))
            {
                var spot = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.FishingQuery != null && ctx.FishingQuery.TryGet(spot, out var spotState) && spotState.Exists)
                    return spotState.HasCatch ? 1.0 : 0.0;
                return 0.0;
            }

            if (string.Equals(name, "fish_is_shallow", StringComparison.OrdinalIgnoreCase))
            {
                var spot = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.FishingQuery != null && ctx.FishingQuery.TryGet(spot, out var spotState) && spotState.Exists)
                    return spotState.IsShallow ? 1.0 : 0.0;
                return 0.0;
            }

            if (string.Equals(name, "mine_spot_count", StringComparison.OrdinalIgnoreCase))
            {
                return ctx?.MiningQuery?.CountAvailableNodes() ?? 0.0;
            }

            if (string.Equals(name, "ore_available", StringComparison.OrdinalIgnoreCase))
            {
                var node = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.MiningQuery != null && ctx.MiningQuery.TryGet(node, out var nodeState) && nodeState.Exists)
                    return nodeState.HasOre ? 1.0 : 0.0;
                return 0.0;
            }

            if (string.Equals(name, "forage_spot_count", StringComparison.OrdinalIgnoreCase))
            {
                return ctx?.ForagingQuery?.CountAvailableSpots() ?? 0.0;
            }

            if (string.Equals(name, "forage_available", StringComparison.OrdinalIgnoreCase))
            {
                var spot = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.ForagingQuery != null && ctx.ForagingQuery.TryGet(spot, out var spotState) && spotState.Exists)
                    return spotState.HasResource ? 1.0 : 0.0;
                return 0.0;
            }

            if (string.Equals(name, "forage_is_forest", StringComparison.OrdinalIgnoreCase))
            {
                var spot = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.ForagingQuery != null && ctx.ForagingQuery.TryGet(spot, out var spotState) && spotState.Exists)
                    return spotState.IsForest ? 1.0 : 0.0;
                return 0.0;
            }

            if (string.Equals(name, "forage_is_coastal", StringComparison.OrdinalIgnoreCase))
            {
                var spot = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.ForagingQuery != null && ctx.ForagingQuery.TryGet(spot, out var spotState) && spotState.Exists)
                    return spotState.IsCoastal ? 1.0 : 0.0;
                return 0.0;
            }

            if (string.Equals(name, "quest_status", StringComparison.OrdinalIgnoreCase))
            {
                ParseQuestArgs(args, ctx, out var actor, out var questId, out _);
                if (ctx?.QuestQuery == null || string.IsNullOrWhiteSpace(questId))
                    return 0.0;
                return (double)ctx.QuestQuery.GetStatus(actor, questId);
            }

            if (string.Equals(name, "quest_available", StringComparison.OrdinalIgnoreCase))
            {
                ParseQuestArgs(args, ctx, out var actor, out var questId, out _);
                if (ctx?.QuestQuery == null || string.IsNullOrWhiteSpace(questId))
                    return 0.0;
                return ctx.QuestQuery.IsQuestAvailable(actor, questId) ? 1.0 : 0.0;
            }

            if (string.Equals(name, "quest_active", StringComparison.OrdinalIgnoreCase))
            {
                ParseQuestArgs(args, ctx, out var actor, out var questId, out _);
                if (ctx?.QuestQuery == null || string.IsNullOrWhiteSpace(questId))
                    return 0.0;
                return ctx.QuestQuery.IsQuestActive(actor, questId) ? 1.0 : 0.0;
            }

            if (string.Equals(name, "quest_ready_to_turn_in", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "quest_ready", StringComparison.OrdinalIgnoreCase))
            {
                ParseQuestArgs(args, ctx, out var actor, out var questId, out _);
                if (ctx?.QuestQuery == null || string.IsNullOrWhiteSpace(questId))
                    return 0.0;
                return ctx.QuestQuery.IsQuestReadyToTurnIn(actor, questId) ? 1.0 : 0.0;
            }

            if (string.Equals(name, "quest_completed", StringComparison.OrdinalIgnoreCase))
            {
                ParseQuestArgs(args, ctx, out var actor, out var questId, out _);
                if (ctx?.QuestQuery == null || string.IsNullOrWhiteSpace(questId))
                    return 0.0;
                return ctx.QuestQuery.IsQuestCompleted(actor, questId) ? 1.0 : 0.0;
            }

            if (string.Equals(name, "quest_objective_active", StringComparison.OrdinalIgnoreCase))
            {
                ParseQuestArgs(args, ctx, out var actor, out var questId, out var objectiveId);
                if (ctx?.QuestQuery == null || string.IsNullOrWhiteSpace(questId) || string.IsNullOrWhiteSpace(objectiveId))
                    return 0.0;
                return ctx.QuestQuery.IsObjectiveActive(actor, questId, objectiveId) ? 1.0 : 0.0;
            }

            if (string.Equals(name, "quest_objective_progress", StringComparison.OrdinalIgnoreCase))
            {
                ParseQuestArgs(args, ctx, out var actor, out var questId, out var objectiveId);
                if (ctx?.QuestQuery == null || string.IsNullOrWhiteSpace(questId) || string.IsNullOrWhiteSpace(objectiveId))
                    return 0.0;
                var progress = ctx.QuestQuery.GetObjectiveProgress(actor, questId, objectiveId);
                return progress.Progress;
            }

            if (string.Equals(name, "quest_objective_required", StringComparison.OrdinalIgnoreCase))
            {
                ParseQuestArgs(args, ctx, out var actor, out var questId, out var objectiveId);
                if (ctx?.QuestQuery == null || string.IsNullOrWhiteSpace(questId) || string.IsNullOrWhiteSpace(objectiveId))
                    return 0.0;
                var progress = ctx.QuestQuery.GetObjectiveProgress(actor, questId, objectiveId);
                return progress.Required;
            }

            if (string.Equals(name, "quest_objective_remaining", StringComparison.OrdinalIgnoreCase))
            {
                ParseQuestArgs(args, ctx, out var actor, out var questId, out var objectiveId);
                if (ctx?.QuestQuery == null || string.IsNullOrWhiteSpace(questId) || string.IsNullOrWhiteSpace(objectiveId))
                    return 0.0;
                var progress = ctx.QuestQuery.GetObjectiveProgress(actor, questId, objectiveId);
                return Math.Max(0, progress.Required - progress.Progress);
            }

            if (string.Equals(name, "animal_needs_brush", StringComparison.OrdinalIgnoreCase))
            {
                var animal = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                if (ctx?.AnimalQuery != null && ctx.AnimalQuery.TryGet(animal, out var state) && state.Exists)
                    return state.NeedsBrush ? 1.0 : 0.0;
                return 0.0;
            }

            if (string.Equals(name, "animals_need_feed", StringComparison.OrdinalIgnoreCase))
            {
                var summary = ctx?.AnimalQuery?.SnapshotSummary() ?? default;
                return summary.HungryCount;
            }

            if (string.Equals(name, "animals_have_produce", StringComparison.OrdinalIgnoreCase))
            {
                var summary = ctx?.AnimalQuery?.SnapshotSummary() ?? default;
                return summary.ProduceReadyCount;
            }

            if (string.Equals(name, "animals_need_brush", StringComparison.OrdinalIgnoreCase))
            {
                var summary = ctx?.AnimalQuery?.SnapshotSummary() ?? default;
                return summary.NeedsBrushCount;
            }
            
            if (string.Equals(name, "distance", StringComparison.OrdinalIgnoreCase))
            {
                var a = AsThingId(args.Length > 0 ? args[0] : null, ctx);
                var b = AsThingId(args.Length > 1 ? args[1] : null, ctx);
                var snap = ctx?.Snapshot;
                if (snap == null) return 0.0;
                var av = snap.GetThing(a);
                var bv = snap.GetThing(b);
                if (av == null || bv == null) return 0.0;
                int dist;
                var building = bv.Building;
                if (building != null && building.ServicePoints != null && building.ServicePoints.Count > 0)
                {
                    // Compute distance to the nearest service point rather than the building center.
                    int best = int.MaxValue;
                    foreach (var sp in building.ServicePoints)
                    {
                        int d = DataDrivenGoap.Core.GridPos.Manhattan(av.Position, sp);
                        if (d < best) best = d;
                    }
                    dist = (best == int.MaxValue) ? DataDrivenGoap.Core.GridPos.Manhattan(av.Position, bv.Position) : best;
                }
                else
                {
                    dist = snap.Distance(a, b);
                }
                return (double)dist;
            }

            if (string.Equals(name, "attr", StringComparison.OrdinalIgnoreCase))
            {
                var a = AsThingId(args.Length > 0 ? args[0] : null, ctx);
                var key = Convert.ToString(args.Length > 1 ? args[1] : "", CultureInfo.InvariantCulture);
                var t = ctx.Snapshot.GetThing(a);
                return t == null ? 0.0 : t.AttrOrDefault(key, 0.0);
            }
            if (string.Equals(name, "fact", StringComparison.OrdinalIgnoreCase))
            {
                var pred = Convert.ToString(args.Length > 0 ? args[0] : "", CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(pred)) return 0.0;
                var a = args.Length > 1 ? AsThingId(args[1], ctx) : AsThingId("$self", ctx);
                ThingId b = args.Length > 2 ? AsThingId(args[2], ctx) : new ThingId(string.Empty);
                return ctx.Snapshot.HasFact(pred, a, b) ? 1.0 : 0.0;
            }
            if (string.Equals(name, "has_active_reservation", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "reserved", StringComparison.OrdinalIgnoreCase))
            {
                var query = ctx.ReservationQuery;
                if (query == null) return 0.0;
                var thing = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                var requester = args.Length > 1 ? AsThingId(args[1], ctx) : AsThingId("$self", ctx);
                if (thing.Value == null) return 0.0;
                return query.HasActiveReservation(thing, requester) ? 1.0 : 0.0;
            }
            if (string.Equals(name, "has", StringComparison.OrdinalIgnoreCase))
            {
                var query = ctx.InventoryQuery;
                if (query == null) return 0.0;
                var owner = AsThingId(args.Length > 0 ? args[0] : "$self", ctx);
                string predicate = Convert.ToString(args.Length > 1 ? args[1] : "", CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(predicate)) return 0.0;
                return query.Has(owner, predicate) ? 1.0 : 0.0;
            }
            if (string.Equals(name, "count", StringComparison.OrdinalIgnoreCase))
            {
                var query = ctx.InventoryQuery;
                if (query == null) return 0.0;
                var owner = AsThingId(args.Length > 0 ? args[0] : "$self", ctx);
                string predicate = Convert.ToString(args.Length > 1 ? args[1] : "", CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(predicate)) return 0.0;
                return query.Count(owner, predicate);
            }
            if (string.Equals(name, "recipe_time", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "craft_time", StringComparison.OrdinalIgnoreCase))
            {
                var query = ctx?.CraftingQuery;
                if (query == null) return 0.0;
                string recipeId = Convert.ToString(args.Length > 0 ? args[0] : "", CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(recipeId)) return 0.0;
                return query.GetCraftDuration(recipeId);
            }
            if (string.Equals(name, "can_craft", StringComparison.OrdinalIgnoreCase))
            {
                var query = ctx?.CraftingQuery;
                if (query == null) return 0.0;
                var owner = AsThingId(args.Length > 0 ? args[0] : "$self", ctx);
                string recipeId = Convert.ToString(args.Length > 1 ? args[1] : "", CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(recipeId)) return 0.0;
                if (!query.TryGetRecipe(recipeId, out var recipe) || recipe == null)
                    return 0.0;

                int count = 1;
                if (args.Length > 2)
                {
                    double rawCount = AsDouble(args[2]);
                    count = (int)Math.Round(Math.Abs(rawCount));
                    if (count <= 0)
                        return 0.0;
                }

                ThingId stationThing = default;
                if (ctx != null && ctx.Vars.TryGetValue("$target", out var tv) && tv is ThingId tid)
                    stationThing = tid;
                string stationHint = null;
                if (args.Length > 3)
                {
                    object arg = args[3];
                    if (arg is ThingId explicitThing)
                    {
                        stationThing = explicitThing;
                        stationHint = null;
                    }
                    else if (arg is string str && str.StartsWith("$", StringComparison.Ordinal))
                    {
                        stationThing = AsThingId(str, ctx);
                        stationHint = null;
                    }
                    else
                    {
                        stationHint = Convert.ToString(arg ?? string.Empty, CultureInfo.InvariantCulture);
                    }
                }

                var snap = ctx?.Snapshot;
                var crafter = snap?.GetThing(owner);
                if (!CraftingUtility.MeetsSkillGates(recipe, crafter, ctx?.SkillProgression))
                    return 0.0;

                ThingView stationView = null;
                if (!string.IsNullOrEmpty(stationThing.Value))
                    stationView = snap?.GetThing(stationThing);

                if (!CraftingUtility.MatchesStation(recipe, stationView, stationHint))
                    return 0.0;

                return query.HasIngredients(owner, recipe, count) ? 1.0 : 0.0;
            }
            if (string.Equals(name, "currency", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "wallet", StringComparison.OrdinalIgnoreCase))
            {
                var query = ctx.InventoryQuery;
                if (query == null) return 0.0;
                var owner = AsThingId(args.Length > 0 ? args[0] : "$self", ctx);
                return query.GetCurrency(owner);
            }
            if (string.Equals(name, "weather_is", StringComparison.OrdinalIgnoreCase))
            {
                var query = ctx?.WeatherQuery;
                if (query == null) return 0.0;
                string id = Convert.ToString(args.Length > 0 ? args[0] : "", CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(id)) return 0.0;
                return query.IsWeather(id) ? 1.0 : 0.0;
            }
            if (string.Equals(name, "weather_pathCostMult", StringComparison.OrdinalIgnoreCase))
            {
                var weather = ctx?.WeatherQuery?.CurrentWeather ?? default;
                double value = weather.PathCostMultiplier;
                if (!double.IsFinite(value) || value <= 0) value = 1.0;
                return value;
            }
            if (string.Equals(name, "weather_cancelOutdoorShifts", StringComparison.OrdinalIgnoreCase))
            {
                return (ctx?.WeatherQuery?.CurrentWeather.CancelOutdoorShifts ?? false) ? 1.0 : 0.0;
            }
            if (string.Equals(name, "clamp", StringComparison.OrdinalIgnoreCase))
            {
                var v = AsDouble(args[0]);
                var lo = AsDouble(args[1]);
                var hi = AsDouble(args[2]);
                if (v < lo) v = lo; if (v > hi) v = hi; return v;
            }
            if (string.Equals(name, "min", StringComparison.OrdinalIgnoreCase))
            {
                var a = AsDouble(args[0]);
                var b = AsDouble(args[1]);
                return Math.Min(a, b);
            }
            if (string.Equals(name, "max", StringComparison.OrdinalIgnoreCase))
            {
                var a = AsDouble(args[0]);
                var b = AsDouble(args[1]);
                return Math.Max(a, b);
            }
            if (string.Equals(name, "count_food_available", StringComparison.OrdinalIgnoreCase))
            {
                var anchorId = args.Length > 0 ? AsThingId(args[0], ctx) : AsThingId("$target", ctx);
                var anchorThing = ctx.Snapshot.GetThing(anchorId);
                if (anchorThing == null) return 0.0;

                int count = 0;
                foreach (var food in ctx.Snapshot.QueryByTag("food"))
                {
                    if (food == null) continue;
                    if (food.AttrOrDefault("consumed", 1.0) > 0.5) continue;
                    if (food.AttrOrDefault("held", 0.0) > 0.5) continue;
                    if (DataDrivenGoap.Core.GridPos.Manhattan(food.Position, anchorThing.Position) <= 1)
                        count++;
                }
                return count;
            }
            return 0.0;
        }

        private static ThingId AsThingId(object value, EvalContext ctx)
        {
            if (value is ThingId tid) return tid;
            if (value is string s)
            {
                if (s.StartsWith("$", StringComparison.Ordinal))
                {
                    if (ctx != null && ctx.Vars.TryGetValue(s, out var ov) && ov is ThingId t2) return t2;
                }
                return new ThingId(s);
            }
            return new ThingId(Convert.ToString(value ?? "", CultureInfo.InvariantCulture));
        }

        private static string AsString(object value)
        {
            if (value == null)
                return null;
            if (value is string s)
                return s;
            if (value is ThingId tid)
                return tid.Value;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static double AsDouble(object value)
        {
            if (value == null) return 0.0;

            if (value is ThingId tid)
            {
                return ParseDoubleOrDefault(tid.Value);
            }

            if (value is string s)
            {
                return ParseDoubleOrDefault(s);
            }

            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (InvalidCastException)
            {
                return ParseDoubleOrDefault(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            catch (FormatException)
            {
                return 0.0;
            }
        }

        private static double ParseDoubleOrDefault(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0.0;

            if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var result))
                return result;

            return 0.0;
        }

        // --- token helpers ---
        private enum TokenKind { Number, String, Ident, DollarVar, Op, Cmp, Logic, LParen, RParen, Comma, Eof }
        private sealed class Token
        {
            public TokenKind Kind;
            public string Text;
            public double Number;
            public override string ToString() => $"{Kind}:{Text}";
        }

        private sealed class Tokenizer
        {
            private readonly string _s;
            private int _i;
            private readonly int _n;

            public Tokenizer(string s) { _s = s ?? ""; _n = _s.Length; _i = 0; }

            public List<Token> Tokenize()
            {
                var list = new List<Token>();
                Token t;
                do { t = Next(); list.Add(t); } while (t.Kind != TokenKind.Eof);
                return list;
            }

            private Token Next()
            {
                while (_i < _n && char.IsWhiteSpace(_s[_i])) _i++;
                if (_i >= _n) return new Token { Kind = TokenKind.Eof, Text = "" };

                char c = _s[_i];

                // number
                if (char.IsDigit(c) || (c == '.' && _i + 1 < _n && char.IsDigit(_s[_i + 1])))
                {
                    int j = _i + 1;
                    while (j < _n && (char.IsDigit(_s[j]) || _s[j] == '.')) j++;
                    var text = _s.Substring(_i, j - _i); _i = j;
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        number = 0.0;
                    }
                    return new Token { Kind = TokenKind.Number, Text = text, Number = number };
                }

                // quoted string
                if (c == '"' || c == '\'')
                {
                    char quote = c; _i++; int start = _i;
                    while (_i < _n && _s[_i] != quote) _i++;
                    var text = _s.Substring(start, Math.Max(0, _i - start));
                    if (_i < _n && _s[_i] == quote) _i++;
                    return new Token { Kind = TokenKind.String, Text = text };
                }

                // $var
                if (c == '$')
                {
                    int j = _i + 1;
                    while (j < _n && (char.IsLetterOrDigit(_s[j]) || _s[j] == '_' || _s[j] == '.')) j++;
                    var text = _s.Substring(_i, j - _i); _i = j;
                    return new Token { Kind = TokenKind.DollarVar, Text = text };
                }

                // ident
                if (char.IsLetter(c) || c == '_')
                {
                    int j = _i + 1;
                    while (j < _n && (char.IsLetterOrDigit(_s[j]) || _s[j] == '_')) j++;
                    var text = _s.Substring(_i, j - _i); _i = j;
                    return new Token { Kind = TokenKind.Ident, Text = text };
                }

                // two-char operators
                if (_i + 1 < _n)
                {
                    string two = _s.Substring(_i, 2);
                    if (two == "&&" || two == "||") { _i += 2; return new Token { Kind = TokenKind.Logic, Text = two }; }
                    if (two == "==" || two == "!=" || two == ">=" || two == "<=") { _i += 2; return new Token { Kind = TokenKind.Cmp, Text = two }; }
                }

                // single-char tokens
                if (c == '>' || c == '<') { _i++; return new Token { Kind = TokenKind.Cmp, Text = c.ToString() }; }
                if (c == '+' || c == '-' || c == '*' || c == '/' || c == '!') { _i++; return new Token { Kind = TokenKind.Op, Text = c.ToString() }; }
                if (c == '(') { _i++; return new Token { Kind = TokenKind.LParen, Text = "(" }; }
                if (c == ')') { _i++; return new Token { Kind = TokenKind.RParen, Text = ")" }; }
                if (c == ',') { _i++; return new Token { Kind = TokenKind.Comma, Text = "," }; }

                // unknown char → stop
                _i = _n;
                return new Token { Kind = TokenKind.Eof, Text = "" };
            }
        }

        private Token Peek(int i) => i < _tokens.Count ? _tokens[i] : new Token { Kind = TokenKind.Eof, Text = "" };

        private bool Match(ref int i, TokenKind kind, string text)
        {
            var t = Peek(i);
            if (t.Kind == kind && (text == null || t.Text == text)) { i++; return true; }
            return false;
        }

        private void Expect(ref int i, TokenKind kind, string text)
        {
            if (!Match(ref i, kind, text))
                throw new InvalidOperationException("Expected " + kind + " '" + text + "'");
        }
    }
}
