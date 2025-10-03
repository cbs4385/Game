
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;
using DataDrivenGoap.Persistence;

namespace DataDrivenGoap.World
{
    internal sealed class ShardState
    {
        public readonly int Index;
        public long Version;
        public ImmutableDictionary<ThingId, ThingRecord> Things;
        public ImmutableHashSet<Fact> Facts;
        public readonly object Gate = new object();
        public ShardState(int index, long version, ImmutableDictionary<ThingId, ThingRecord> things, ImmutableHashSet<Fact> facts)
        { Index = index; Version = version; Things = things; Facts = facts; }
    }

    internal sealed class ThingRecord
    {
        public readonly ThingId Id;
        public readonly string Type;
        public readonly ImmutableHashSet<string> Tags;
        public readonly GridPos Pos;
        public readonly ImmutableDictionary<string,double> Attrs;
        public readonly BuildingInfo Building;
        public ThingRecord(ThingId id, string type, ImmutableHashSet<string> tags, GridPos pos, ImmutableDictionary<string,double> attrs, BuildingInfo building)
        { Id=id; Type=type; Tags=tags; Pos=pos; Attrs=attrs; Building=building; }
        public ThingRecord WithPos(GridPos p) => new ThingRecord(Id, Type, Tags, p, Attrs, Building);
        public ThingRecord WithAttr(string key, double val)
        {
            var attrs = Attrs.SetItem(key, val);
            var building = Building;
            if (building != null && string.Equals(key, "open", StringComparison.OrdinalIgnoreCase))
            {
                building = building.WithOpenFlag(val > 0.5);
            }
            return new ThingRecord(Id, Type, Tags, Pos, attrs, building);
        }
    }

    internal sealed class Snapshot : IWorldSnapshot
    {
        private readonly ShardState[] _shards;
        private readonly System.Collections.Generic.Dictionary<ThingId, ThingView> _cache = new System.Collections.Generic.Dictionary<ThingId, ThingView>();
        private readonly bool[,] _walkable;
        private readonly int _w, _h;
        public long Version { get; }

        private readonly WorldTimeSnapshot _time;

        public WorldTimeSnapshot Time => _time;

        public Snapshot(ShardState[] shards, long globalVersion, bool[,] walkable, int w, int h, WorldTimeSnapshot time)
        {
            _shards = shards; Version = globalVersion; _walkable = walkable; _w = w; _h = h; _time = time;
        }

        private static int HashIdx(ThingId id, int mod) => (id.GetHashCode() & 0x7fffffff) % mod;
        private ShardState ShardOf(ThingId id) => _shards[HashIdx(id, _shards.Length)];

        public ThingView GetThing(ThingId id)
        {
            ThingView tv;
            if (_cache.TryGetValue(id, out tv)) return tv;
            var shard = ShardOf(id);
            ThingRecord tr;
            if (!shard.Things.TryGetValue(id, out tr)) return null;
            tv = new ThingView(tr.Id, tr.Type, tr.Tags, tr.Pos, tr.Attrs, tr.Building);
            _cache[id] = tv;
            return tv;
        }

        public IEnumerable<ThingView> AllThings()
        {
            foreach (var sh in _shards)
                foreach (var kv in sh.Things)
                    yield return GetThing(kv.Key);
        }

        public IEnumerable<ThingView> QueryByTag(string tag)
        {
            foreach (var sh in _shards)
                foreach (var kv in sh.Things)
                    if (kv.Value.Tags.Contains(tag))
                        yield return GetThing(kv.Key);
        }

        public bool HasFact(string pred, ThingId a, ThingId b)
        {
            var sh = ShardOf(a);
            return sh.Facts.Contains(new Fact(pred, a, b));
        }

        public int Distance(ThingId a, ThingId b)
        {
            var ta = GetThing(a); var tb = GetThing(b);
            if (ta == null || tb == null) return int.MaxValue/4;
            return GridPos.Manhattan(ta.Position, tb.Position);
        }

        public int Width => _w;
        public int Height => _h;
        public bool IsWalkable(int x, int y) => x>=0 && y>=0 && x<_w && y<_h && _walkable[x,y];
        public bool IsWalkable(GridPos p) => IsWalkable(p.X, p.Y);
        public bool TryFindNextStep(GridPos from, GridPos to, out GridPos next)
            => DataDrivenGoap.Pathfinding.AStar4.TryFindNextStep(this, from, to, out next);
    }

    public sealed class ShardedWorld : IWorld
    {
        private readonly ShardState[] _shards;
        private long _globalVersion;
        private readonly int _width, _height;
        private readonly bool[,] _walkable;

        private static int HashIdx(ThingId id, int mod) => (id.GetHashCode() & 0x7fffffff) % mod;

        private readonly WorldClock _clock;

        public ShardedWorld(
            int width,
            int height,
            double blockedChance,
            int shardCount,
            int rngSeed,
            IEnumerable<(ThingId id, string type, IEnumerable<string> tags, GridPos pos, IDictionary<string,double> attrs, BuildingInfo building)> seedThings,
            IEnumerable<Fact> seedFacts,
            WorldClock clock,
            bool[,] walkableOverride = null)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (shardCount <= 0)
                throw new ArgumentException("Shard count must be greater than zero", nameof(shardCount));
            _shards = new ShardState[shardCount];
            for (int i=0;i<shardCount;i++)
                _shards[i] = new ShardState(i, 1, ImmutableDictionary<ThingId, ThingRecord>.Empty, ImmutableHashSet<Fact>.Empty);

            if (width <= 0) throw new ArgumentException("World width must be greater than zero", nameof(width));
            if (height <= 0) throw new ArgumentException("World height must be greater than zero", nameof(height));
            _width = width;
            _height = height;

            if (double.IsNaN(blockedChance) || double.IsInfinity(blockedChance) || blockedChance < 0 || blockedChance > 1)
                throw new ArgumentException("blockedChance must be within [0,1]", nameof(blockedChance));

            var rng = new Random(rngSeed);
            if (walkableOverride != null)
            {
                if (walkableOverride.GetLength(0) != _width || walkableOverride.GetLength(1) != _height)
                    throw new ArgumentException("walkableOverride dimensions must match world width/height", nameof(walkableOverride));
                bool anyWalkable = false;
                for (int x = 0; x < _width && !anyWalkable; x++)
                    for (int y = 0; y < _height && !anyWalkable; y++)
                        if (walkableOverride[x, y])
                            anyWalkable = true;
                if (!anyWalkable)
                    throw new ArgumentException("walkableOverride must contain at least one walkable tile", nameof(walkableOverride));
                _walkable = (bool[,])walkableOverride.Clone();
            }
            else
            {
                _walkable = new bool[_width, _height];
                for (int x = 0; x < _width; x++)
                {
                    for (int y = 0; y < _height; y++)
                    {
                        _walkable[x, y] = true;
                    }
                }
            }

            if (seedThings == null)
                throw new ArgumentNullException(nameof(seedThings));

            foreach (var t in seedThings)
            {
                if (t.tags == null)
                    throw new ArgumentException("Seed thing tags must not be null", nameof(seedThings));
                if (t.attrs == null)
                    throw new ArgumentException("Seed thing attributes must not be null", nameof(seedThings));

                int x = Math.Max(0, Math.Min(_width-1, t.pos.X));
                int y = Math.Max(0, Math.Min(_height-1, t.pos.Y));
                if (!_walkable[x,y])
                {
                    for (int tries=0; tries<200; tries++)
                    {
                        int rx = rng.Next(0,_width), ry = rng.Next(0,_height);
                        if (_walkable[rx,ry]) { x=rx; y=ry; break; }
                    }
                }
                int idx = HashIdx(t.id, _shards.Length);
                var sh = _shards[idx];
                var attrs = new Dictionary<string, double>(t.attrs, StringComparer.OrdinalIgnoreCase);
                bool openFlag = t.building?.IsOpenFlag ?? true;
                if (attrs.TryGetValue("open", out var openAttr))
                    openFlag = openAttr > 0.5;
                else
                    attrs["open"] = openFlag ? 1.0 : 0.0;

                var building = t.building?.WithOpenFlag(openFlag);

                var tr = new ThingRecord(
                    t.id, t.type,
                    t.tags.ToImmutableHashSet(),
                    new GridPos(x,y),
                    attrs.ToImmutableDictionary(),
                    building
                );
                sh.Things = sh.Things.Add(t.id, tr);
            }

            if (seedFacts == null)
                throw new ArgumentNullException(nameof(seedFacts));

            foreach (var f in seedFacts)
            {
                if (string.IsNullOrWhiteSpace(f.Pred) || string.IsNullOrWhiteSpace(f.A.Value))
                    throw new ArgumentException("Seed facts must define predicate and subject", nameof(seedFacts));
                int idx = HashIdx(f.A, _shards.Length);
                var sh = _shards[idx];
                sh.Facts = sh.Facts.Add(f);
            }

            _globalVersion = 1;
        }

        public IWorldSnapshot Snap()
        {
            var copy = new ShardState[_shards.Length];
            for (int i=0;i<_shards.Length;i++) copy[i] = _shards[i];
            var time = _clock.Snapshot();
            return new Snapshot(copy, Interlocked.Read(ref _globalVersion), _walkable, _width, _height, time);
        }

        public CommitResult TryCommit(in EffectBatch batch)
        {
            var touched = new SortedSet<int>();
            foreach (var w in batch.Writes ?? Array.Empty<WriteSetEntry>()) touched.Add(HashIdx(w.Thing, _shards.Length));
            foreach (var r in batch.Reads ?? Array.Empty<ReadSetEntry>()) touched.Add(HashIdx(r.Thing, _shards.Length));
            foreach (var fd in batch.FactDeltas ?? Array.Empty<FactDelta>()) touched.Add(HashIdx(fd.A, _shards.Length));
            foreach (var sp in batch.Spawns ?? Array.Empty<ThingSpawnRequest>()) touched.Add(HashIdx(sp.Id, _shards.Length));
            foreach (var ds in batch.Despawns ?? Array.Empty<ThingId>()) touched.Add(HashIdx(ds, _shards.Length));

            var locked = new System.Collections.Generic.List<int>();
            try
            {
                foreach (var idx in touched) { Monitor.Enter(_shards[idx].Gate); locked.Add(idx); }

                foreach (var r in batch.Reads ?? Array.Empty<ReadSetEntry>())
                {
                    var sh = _shards[HashIdx(r.Thing, _shards.Length)];
                    ThingRecord tr; if (!sh.Things.TryGetValue(r.Thing, out tr)) return CommitResult.Conflict;
                    if (r.ExpectAttribute != null && r.ExpectValue.HasValue)
                    {
                        double v; if (!tr.Attrs.TryGetValue(r.ExpectAttribute, out v)) v = 0.0;
                        if (Math.Abs(v - r.ExpectValue.Value) > 1e-9) return CommitResult.Conflict;
                    }
                }

                var builders = new System.Collections.Generic.Dictionary<int,(ImmutableDictionary<ThingId,ThingRecord>.Builder tb, ImmutableHashSet<Fact>.Builder fb)>();
                foreach (var idx in touched) builders[idx] = (_shards[idx].Things.ToBuilder(), _shards[idx].Facts.ToBuilder());

                foreach (var sp in batch.Spawns ?? Array.Empty<ThingSpawnRequest>())
                {
                    if (string.IsNullOrWhiteSpace(sp.Id.Value)) return CommitResult.Conflict;
                    int idx = HashIdx(sp.Id, _shards.Length);
                    var b = builders[idx];
                    if (b.tb.ContainsKey(sp.Id)) return CommitResult.Conflict;

                    int x = Math.Max(0, Math.Min(_width - 1, sp.Position.X));
                    int y = Math.Max(0, Math.Min(_height - 1, sp.Position.Y));
                    var pos = new GridPos(x, y);

                    var tagSet = (sp.Tags ?? Array.Empty<string>())
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Select(t => t.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

                    var attrBuilder = ImmutableDictionary.CreateBuilder<string, double>(StringComparer.OrdinalIgnoreCase);
                    foreach (var av in sp.Attributes ?? Array.Empty<ThingAttributeValue>())
                    {
                        if (string.IsNullOrWhiteSpace(av.Name))
                            continue;
                        attrBuilder[av.Name] = av.Value;
                    }

                    var attrMap = attrBuilder.ToImmutable();
                    var record = new ThingRecord(sp.Id, sp.Type ?? string.Empty, tagSet, pos, attrMap, null);
                    b.tb[sp.Id] = record; builders[idx] = b;
                }

                foreach (var w in batch.Writes ?? Array.Empty<WriteSetEntry>())
                {
                    int idx = HashIdx(w.Thing, _shards.Length);
                    var b = builders[idx];
                    ThingRecord tr; if (!b.tb.TryGetValue(w.Thing, out tr)) return CommitResult.Conflict;
                    if (w.Attribute == "@move.x") tr = tr.WithPos(new GridPos((int)w.Value, tr.Pos.Y));
                    else if (w.Attribute == "@move.y") tr = tr.WithPos(new GridPos(tr.Pos.X, (int)w.Value));
                    else tr = tr.WithAttr(w.Attribute, w.Value);
                    b.tb[w.Thing] = tr; builders[idx] = b;
                }

                foreach (var fd in batch.FactDeltas ?? Array.Empty<FactDelta>())
                {
                    int idx = HashIdx(fd.A, _shards.Length);
                    var b = builders[idx];
                    if (fd.Add) b.fb.Add(new Fact(fd.Pred, fd.A, fd.B));
                    else b.fb.Remove(new Fact(fd.Pred, fd.A, fd.B));
                    builders[idx] = b;
                }

                foreach (var ds in batch.Despawns ?? Array.Empty<ThingId>())
                {
                    if (string.IsNullOrWhiteSpace(ds.Value)) return CommitResult.Conflict;
                    int idx = HashIdx(ds, _shards.Length);
                    var b = builders[idx];
                    if (!b.tb.Remove(ds)) return CommitResult.Conflict;
                    RemoveFactsFor(ds, b.fb);
                    builders[idx] = b;
                }

                foreach (var shardIdx in builders.Keys.ToArray())
                {
                    var builder = builders[shardIdx];
                    var consumed = CollectConsumedItems(builder.tb);
                    if (consumed.Count == 0)
                    {
                        builders[shardIdx] = builder;
                        continue;
                    }

                    foreach (var id in consumed)
                    {
                        builder.tb.Remove(id);
                        RemoveFactsFor(id, builder.fb);
                    }

                    builders[shardIdx] = builder;
                }

                foreach (var kv in builders)
                {
                    int idx = kv.Key;
                    _shards[idx].Things = kv.Value.tb.ToImmutable();
                    _shards[idx].Facts = kv.Value.fb.ToImmutable();
                    _shards[idx].Version++;
                }
                Interlocked.Increment(ref _globalVersion);
                return CommitResult.Committed;
            }
            finally
            {
                for (int i = locked.Count-1; i>=0; i--) Monitor.Exit(_shards[locked[i]].Gate);
            }
        }

        public WorldStateChunk CaptureState()
        {
            var chunk = new WorldStateChunk
            {
                version = _globalVersion,
                width = _width,
                height = _height,
                walkable = SerializeWalkable()
            };

            var factSet = new HashSet<Fact>();
            foreach (var shard in _shards)
            {
                foreach (var kv in shard.Things)
                    chunk.things.Add(ToThingState(kv.Value));

                foreach (var fact in shard.Facts)
                {
                    if (factSet.Add(fact))
                    {
                        chunk.facts.Add(new FactState
                        {
                            pred = fact.Pred,
                            a = fact.A.Value,
                            b = fact.B.Value
                        });
                    }
                }
            }

            return chunk;
        }

        public void ApplyState(WorldStateChunk state)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (state.width != _width || state.height != _height)
                throw new InvalidOperationException("World dimensions do not match snapshot");

            var walkable = DeserializeWalkable(state.walkable);
            if (walkable.GetLength(0) != _width || walkable.GetLength(1) != _height)
                throw new InvalidOperationException("Walkable grid size mismatch");

            var newThingMaps = new Dictionary<int, Dictionary<ThingId, ThingRecord>>();
            var newFactSets = new Dictionary<int, HashSet<Fact>>();
            for (int i = 0; i < _shards.Length; i++)
            {
                newThingMaps[i] = new Dictionary<ThingId, ThingRecord>();
                newFactSets[i] = new HashSet<Fact>();
            }

            if (state.things != null)
            {
                foreach (var thing in state.things)
                {
                    if (thing == null || string.IsNullOrWhiteSpace(thing.id))
                        continue;
                    var id = new ThingId(thing.id.Trim());
                    int shardIdx = HashIdx(id, _shards.Length);
                    var record = FromThingState(id, thing);
                    newThingMaps[shardIdx][id] = record;
                }
            }

            if (state.facts != null)
            {
                foreach (var factState in state.facts)
                {
                    if (factState == null || string.IsNullOrWhiteSpace(factState.pred) || string.IsNullOrWhiteSpace(factState.a))
                        continue;
                    var fact = new Fact(factState.pred.Trim(), new ThingId(factState.a.Trim()),
                        string.IsNullOrWhiteSpace(factState.b) ? default : new ThingId(factState.b.Trim()));
                    int shardIdx = HashIdx(fact.A, _shards.Length);
                    newFactSets[shardIdx].Add(fact);
                }
            }

            for (int i = 0; i < _shards.Length; i++)
            {
                var shard = _shards[i];
                lock (shard.Gate)
                {
                    shard.Version = state.version;
                    shard.Things = newThingMaps[i].ToImmutableDictionary();
                    shard.Facts = newFactSets[i].ToImmutableHashSet();
                }
            }

            _globalVersion = state.version;
            for (int x = 0; x < _width; x++)
                for (int y = 0; y < _height; y++)
                    _walkable[x, y] = walkable[x, y];
        }

        private bool[][] SerializeWalkable()
        {
            var rows = new bool[_width][];
            for (int x = 0; x < _width; x++)
            {
                var row = new bool[_height];
                for (int y = 0; y < _height; y++)
                    row[y] = _walkable[x, y];
                rows[x] = row;
            }
            return rows;
        }

        private bool[,] DeserializeWalkable(bool[][] data)
        {
            var grid = new bool[_width, _height];
            if (data == null)
                return grid;
            for (int x = 0; x < Math.Min(data.Length, _width); x++)
            {
                var row = data[x];
                if (row == null)
                    continue;
                for (int y = 0; y < Math.Min(row.Length, _height); y++)
                    grid[x, y] = row[y];
            }
            return grid;
        }

        private static ThingState ToThingState(ThingRecord record)
        {
            return new ThingState
            {
                id = record.Id.Value,
                type = record.Type,
                tags = record.Tags?.ToArray() ?? Array.Empty<string>(),
                x = record.Pos.X,
                y = record.Pos.Y,
                attributes = record.Attrs?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, double>(),
                building = ToBuildingState(record.Building)
            };
        }

        private static ThingRecord FromThingState(ThingId id, ThingState state)
        {
            var tags = state.tags != null
                ? state.tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase)
                : ImmutableHashSet<string>.Empty;
            var attrs = state.attributes != null
                ? state.attributes.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase)
                : ImmutableDictionary<string, double>.Empty;
            var building = FromBuildingState(state.building);
            return new ThingRecord(id, state.type ?? string.Empty, tags, new GridPos(state.x, state.y), attrs, building);
        }

        private static BuildingState ToBuildingState(BuildingInfo building)
        {
            if (building == null)
                return null;
            return new BuildingState
            {
                isOpen = building.IsOpenFlag,
                capacity = building.Capacity,
                area = building.Area.HasValue ? new RectState
                {
                    minX = building.Area.Value.MinX,
                    minY = building.Area.Value.MinY,
                    maxX = building.Area.Value.MaxX,
                    maxY = building.Area.Value.MaxY
                } : null,
                servicePoints = building.ServicePoints?.Select(p => new GridPosState { x = p.X, y = p.Y }).ToList(),
                openHours = building.OpenHours?.Select(ToOpenHoursState).ToList()
            };
        }

        private static BuildingInfo FromBuildingState(BuildingState state)
        {
            if (state == null)
                return null;
            RectInt? rect = null;
            if (state.area != null)
                rect = new RectInt(state.area.minX, state.area.minY, state.area.maxX, state.area.maxY);
            var servicePoints = state.servicePoints?.Select(p => new GridPos(p.x, p.y)).ToArray() ?? Array.Empty<GridPos>();
            var openHours = state.openHours?.Select(FromOpenHoursState).ToArray() ?? Array.Empty<BuildingOpenHours>();
            return new BuildingInfo(rect, state.isOpen, state.capacity, servicePoints, openHours);
        }

        private static BuildingOpenHoursState ToOpenHoursState(BuildingOpenHours hours)
        {
            if (hours == null)
                return null;
            return new BuildingOpenHoursState
            {
                daysOfWeek = hours.DaysOfWeek?.ToArray() ?? Array.Empty<int>(),
                seasons = hours.Seasons?.ToArray() ?? Array.Empty<string>(),
                startHour = hours.StartHour,
                endHour = hours.EndHour
            };
        }

        private static BuildingOpenHours FromOpenHoursState(BuildingOpenHoursState state)
        {
            if (state == null)
                return null;
            return new BuildingOpenHours(state.daysOfWeek ?? Array.Empty<int>(), state.seasons ?? Array.Empty<string>(), state.startHour, state.endHour);
        }

        private static void RemoveFactsFor(ThingId id, ImmutableHashSet<Fact>.Builder facts)
        {
            if (facts == null) return;
            var toRemove = new List<Fact>();
            foreach (var fact in facts)
            {
                if (fact.A.Equals(id) || fact.B.Equals(id))
                    toRemove.Add(fact);
            }
            foreach (var fact in toRemove)
                facts.Remove(fact);
        }

        private static List<ThingId> CollectConsumedItems(ImmutableDictionary<ThingId, ThingRecord>.Builder things)
        {
            var list = new List<ThingId>();
            if (things == null)
                return list;
            foreach (var kv in things)
            {
                if (ShouldAutoDespawn(kv.Value))
                    list.Add(kv.Key);
            }
            return list;
        }

        private static bool ShouldAutoDespawn(ThingRecord record)
        {
            if (record == null)
                return false;
            if (record.Tags == null)
                return false;
            bool isItem = record.Tags.Any(t => string.Equals(t, "item", StringComparison.OrdinalIgnoreCase));
            if (!isItem)
                return false;

            foreach (var attr in record.Attrs ?? ImmutableDictionary<string, double>.Empty)
            {
                if (IsConsumedAttribute(attr.Key) && attr.Value > 0.5)
                    return true;
            }
            return false;
        }

        private static bool IsConsumedAttribute(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            if (name.Equals("consumed", StringComparison.OrdinalIgnoreCase))
                return true;
            return name.EndsWith("consumed", StringComparison.OrdinalIgnoreCase);
        }
    }
}
