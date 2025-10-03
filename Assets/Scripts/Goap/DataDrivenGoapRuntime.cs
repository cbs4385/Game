using System;
using System.Collections.Generic;
using UnityEngine;

namespace DataDrivenGoap.Unity
{
    /// <summary>
    /// Configuration payload used to bootstrap the GOAP simulation.
    /// </summary>
    public sealed class SimulationConfig
    {
        public SimulationConfig(
            Vector2Int mapSize,
            int pawnCount,
            float tileSpacing,
            Vector2 elevationRange,
            float pawnSpeed,
            float pawnHeightOffset,
            int randomSeed)
        {
            if (mapSize.x <= 0 || mapSize.y <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(mapSize), "The map must be at least 1x1 tile.");
            }

            if (pawnCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pawnCount));
            }

            if (tileSpacing <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(tileSpacing));
            }

            if (pawnSpeed <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(pawnSpeed));
            }

            MapSize = mapSize;
            PawnCount = pawnCount;
            TileSpacing = tileSpacing;
            ElevationRange = new Vector2(Mathf.Min(elevationRange.x, elevationRange.y), Mathf.Max(elevationRange.x, elevationRange.y));
            PawnSpeed = pawnSpeed;
            PawnHeightOffset = pawnHeightOffset;
            RandomSeed = randomSeed == 0 ? Environment.TickCount : randomSeed;
        }

        public Vector2Int MapSize { get; }

        public int PawnCount { get; }

        public float TileSpacing { get; }

        public Vector2 ElevationRange { get; }

        public float PawnSpeed { get; }

        public float PawnHeightOffset { get; }

        public int RandomSeed { get; }
    }

    /// <summary>
    /// Immutable description of a generated map tile.
    /// </summary>
    public sealed class MapTile
    {
        public MapTile(Vector2Int coordinates, float elevation, float normalizedElevation, float traversalCost, Vector3 worldCenter)
        {
            Coordinates = coordinates;
            Elevation = elevation;
            NormalizedElevation = normalizedElevation;
            TraversalCost = traversalCost;
            WorldCenter = worldCenter;
        }

        public Vector2Int Coordinates { get; }

        public float Elevation { get; }

        public float NormalizedElevation { get; }

        public float TraversalCost { get; }

        public Vector3 WorldCenter { get; }

        public Vector3 WorldSurfacePosition => new Vector3(WorldCenter.x, Elevation, WorldCenter.z);
    }

    /// <summary>
    /// Collection of generated map tiles.
    /// </summary>
    public sealed class GoapMap
    {
        private readonly Dictionary<Vector2Int, MapTile> _tiles;

        public GoapMap(Vector2Int size, IReadOnlyList<MapTile> tiles)
        {
            Size = size;
            _tiles = new Dictionary<Vector2Int, MapTile>(tiles.Count);
            foreach (var tile in tiles)
            {
                _tiles[tile.Coordinates] = tile;
            }
        }

        public Vector2Int Size { get; }

        public IEnumerable<MapTile> Tiles => _tiles.Values;

        public MapTile GetTile(Vector2Int coordinates)
        {
            if (!_tiles.TryGetValue(coordinates, out var tile))
            {
                throw new KeyNotFoundException($"The tile {coordinates} could not be found.");
            }

            return tile;
        }

        public MapTile GetTile(int x, int y) => GetTile(new Vector2Int(x, y));
    }

    /// <summary>
    /// Immutable description of an item that can appear in the simulation.
    /// </summary>
    public sealed class UnityItemDefinition
    {
        public UnityItemDefinition(string id, string displayName, string spriteId)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("An item definition must have a valid identifier.", nameof(id));
            }

            Id = id;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName;
            SpriteId = spriteId ?? string.Empty;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string SpriteId { get; }
    }

    /// <summary>
    /// Holds data-driven content used by the simulation (items, etc.).
    /// </summary>
    public sealed class GoapContent
    {
        private readonly Dictionary<string, UnityItemDefinition> _itemLookup;

        public GoapContent(IReadOnlyList<UnityItemDefinition> itemDefinitions)
        {
            ItemDefinitions = itemDefinitions ?? Array.Empty<UnityItemDefinition>();
            _itemLookup = new Dictionary<string, UnityItemDefinition>(ItemDefinitions.Count, StringComparer.OrdinalIgnoreCase);

            foreach (var definition in ItemDefinitions)
            {
                if (definition == null || string.IsNullOrEmpty(definition.Id) || _itemLookup.ContainsKey(definition.Id))
                {
                    continue;
                }

                _itemLookup[definition.Id] = definition;
            }
        }

        public IReadOnlyList<UnityItemDefinition> ItemDefinitions { get; }

        public bool TryGetItemDefinition(string id, out UnityItemDefinition definition)
        {
            if (string.IsNullOrEmpty(id))
            {
                definition = null;
                return false;
            }

            return _itemLookup.TryGetValue(id, out definition);
        }
    }

    /// <summary>
    /// Immutable snapshot of an item in the simulation.
    /// </summary>
    public readonly struct ItemSnapshot
    {
        public ItemSnapshot(int id, string definitionId, Vector3 worldPosition, Vector2Int tile)
        {
            Id = id;
            DefinitionId = definitionId;
            WorldPosition = worldPosition;
            Tile = tile;
        }

        public int Id { get; }

        public string DefinitionId { get; }

        public Vector3 WorldPosition { get; }

        public Vector2Int Tile { get; }
    }

    /// <summary>
    /// Immutable snapshot of a pawn's state that can be consumed by presentation code without mutating the simulation.
    /// </summary>
    public readonly struct PawnSnapshot
    {
        public PawnSnapshot(int id, string name, Color color, Vector3 worldPosition, Vector2Int tile, Vector2Int targetTile)
        {
            Id = id;
            Name = name;
            Color = color;
            WorldPosition = worldPosition;
            Tile = tile;
            TargetTile = targetTile;
        }

        public int Id { get; }

        public string Name { get; }

        public Color Color { get; }

        public Vector3 WorldPosition { get; }

        public Vector2Int Tile { get; }

        public Vector2Int TargetTile { get; }
    }

    /// <summary>
    /// Entry point to create the simulation using data-driven defaults.
    /// </summary>
    public static class SimulationFactory
    {
        public static UnitySimulation Create(
            MapDefinitionDto mapDefinition,
            PawnDefinitionsDto pawnDefinitions,
            ItemDefinitionsDto itemDefinitions,
            int randomSeed)
        {
            if (mapDefinition == null)
            {
                throw new ArgumentNullException(nameof(mapDefinition));
            }

            if (pawnDefinitions == null)
            {
                throw new ArgumentNullException(nameof(pawnDefinitions));
            }

            mapDefinition.ApplyDefaults();
            pawnDefinitions.ApplyDefaults();
            itemDefinitions ??= ItemDefinitionsDto.Empty;
            itemDefinitions.ApplyDefaults();

            var mapSize = mapDefinition.size.ToVector2Int();
            if (mapSize.x <= 0 || mapSize.y <= 0)
            {
                throw new ArgumentException("The map definition must specify a positive size.", nameof(mapDefinition));
            }

            var tileSpacing = Mathf.Max(0.01f, mapDefinition.tileSpacing);
            var minElevation = Mathf.Min(mapDefinition.minElevation, mapDefinition.maxElevation);
            var maxElevation = Mathf.Max(mapDefinition.minElevation, mapDefinition.maxElevation);
            var defaultSpeed = Mathf.Max(0.01f, pawnDefinitions.defaultSpeed);
            var defaultHeightOffset = pawnDefinitions.defaultHeightOffset;

            var initialConfig = new SimulationConfig(
                mapSize,
                pawnDefinitions.pawns.Length,
                tileSpacing,
                new Vector2(minElevation, maxElevation),
                defaultSpeed,
                defaultHeightOffset,
                randomSeed);

            var random = new System.Random(initialConfig.RandomSeed);
            var content = GoapContentLoader.Load();
            var map = MapGenerator.Generate(mapDefinition, initialConfig, random);
            var pawns = PawnFactory.Create(map, initialConfig, pawnDefinitions, random);
            var config = pawns.Count == initialConfig.PawnCount
                ? initialConfig
                : new SimulationConfig(
                    mapSize,
                    pawns.Count,
                    tileSpacing,
                    new Vector2(minElevation, maxElevation),
                    defaultSpeed,
                    defaultHeightOffset,
                    initialConfig.RandomSeed);
            var items = ItemFactory.Create(map, config, content, random);
            return new UnitySimulation(config, map, pawns, items, content, random);
        }
    }

    /// <summary>
    /// Main runtime simulation loop.
    /// </summary>
    public sealed class UnitySimulation
    {
        private readonly SimulationConfig _config;
        private readonly GoapMap _map;
        private readonly List<PawnInternal> _pawns;
        private readonly List<ItemInternal> _items;
        private readonly Dictionary<int, ItemInternal> _itemsById;
        private readonly GoapContent _content;
        private readonly System.Random _random;

        public UnitySimulation(
            SimulationConfig config,
            GoapMap map,
            List<PawnInternal> pawns,
            List<ItemInternal> items,
            GoapContent content,
            System.Random random)
        {
            _config = config;
            _map = map;
            _pawns = pawns;
            _items = items ?? new List<ItemInternal>();
            _content = content ?? new GoapContent(Array.Empty<UnityItemDefinition>());
            _itemsById = new Dictionary<int, ItemInternal>(_items.Count);
            foreach (var item in _items)
            {
                if (item != null)
                {
                    _itemsById[item.Id] = item;
                }
            }
            _random = random;
        }

        public event Action<MapTile> TileGenerated;

        public event Action<PawnSnapshot> PawnSpawned;

        public event Action<PawnSnapshot> PawnUpdated;

        public event Action<ItemSnapshot> ItemSpawned;

        public SimulationConfig Config => _config;

        public GoapMap Map => _map;

        public GoapContent Content => _content;

        public IReadOnlyList<UnityItemDefinition> ItemDefinitions => _content.ItemDefinitions;

        public void Start()
        {
            foreach (var tile in _map.Tiles)
            {
                TileGenerated?.Invoke(tile);
            }

            foreach (var item in _items)
            {
                ItemSpawned?.Invoke(item.CreateSnapshot());
            }

            foreach (var pawn in _pawns)
            {
                PawnSpawned?.Invoke(pawn.CreateSnapshot());
            }
        }

        public void Update(float deltaTime)
        {
            if (deltaTime <= 0f)
            {
                return;
            }

            foreach (var pawn in _pawns)
            {
                UpdatePawn(pawn, deltaTime);
            }
        }

        private void UpdatePawn(PawnInternal pawn, float deltaTime)
        {
            var targetTile = _map.GetTile(pawn.TargetTile);
            var targetPosition = targetTile.WorldSurfacePosition + Vector3.up * _config.PawnHeightOffset;
            var currentPosition = pawn.WorldPosition;
            var toTarget = targetPosition - currentPosition;
            var maxStep = pawn.Speed * deltaTime;

            if (toTarget.sqrMagnitude <= maxStep * maxStep)
            {
                pawn.WorldPosition = targetPosition;
                pawn.CurrentTile = targetTile.Coordinates;
                PawnUpdated?.Invoke(pawn.CreateSnapshot());
                ChooseNextTarget(pawn);
                PawnUpdated?.Invoke(pawn.CreateSnapshot());
            }
            else
            {
                pawn.WorldPosition = currentPosition + toTarget.normalized * maxStep;
                PawnUpdated?.Invoke(pawn.CreateSnapshot());
            }
        }

        private void ChooseNextTarget(PawnInternal pawn)
        {
            if (_map.Size.x == 1 && _map.Size.y == 1)
            {
                pawn.TargetTile = pawn.CurrentTile;
                return;
            }

            Vector2Int candidate;
            do
            {
                candidate = new Vector2Int(_random.Next(0, _map.Size.x), _random.Next(0, _map.Size.y));
            }
            while (candidate == pawn.CurrentTile);

            pawn.TargetTile = candidate;
        }

        public IEnumerable<PawnSnapshot> GetPawnSnapshots()
        {
            foreach (var pawn in _pawns)
            {
                yield return pawn.CreateSnapshot();
            }
        }

        public IEnumerable<ItemSnapshot> GetItemSnapshots()
        {
            foreach (var item in _items)
            {
                yield return item.CreateSnapshot();
            }
        }

        public bool TryGetPawnSnapshot(int id, out PawnSnapshot snapshot)
        {
            foreach (var pawn in _pawns)
            {
                if (pawn.Id == id)
                {
                    snapshot = pawn.CreateSnapshot();
                    return true;
                }
            }

            snapshot = default;
            return false;
        }

        public bool TryGetItemSnapshot(int id, out ItemSnapshot snapshot)
        {
            if (_itemsById.TryGetValue(id, out var item))
            {
                snapshot = item.CreateSnapshot();
                return true;
            }

            snapshot = default;
            return false;
        }

        public bool TryGetItemDefinition(string id, out UnityItemDefinition definition)
        {
            if (_content == null)
            {
                definition = null;
                return false;
            }

            return _content.TryGetItemDefinition(id, out definition);
        }
    }

    internal static class GoapContentLoader
    {
        private const string ResourcePath = "DataDrivenGoap/GoapContent";

        public static GoapContent Load()
        {
            var asset = Resources.Load<TextAsset>(ResourcePath);
            if (asset == null || string.IsNullOrWhiteSpace(asset.text))
            {
                return new GoapContent(Array.Empty<UnityItemDefinition>());
            }

            try
            {
                var payload = JsonUtility.FromJson<GoapContentPayload>(asset.text);
                if (payload?.items == null || payload.items.Length == 0)
                {
                    return new GoapContent(Array.Empty<UnityItemDefinition>());
                }

                var definitions = new List<UnityItemDefinition>(payload.items.Length);
                foreach (var item in payload.items)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.id))
                    {
                        continue;
                    }

                    var displayName = string.IsNullOrWhiteSpace(item.displayName) ? item.id : item.displayName;
                    var spriteId = item.spriteId ?? string.Empty;
                    definitions.Add(new UnityItemDefinition(item.id, displayName, spriteId));
                }

                return new GoapContent(definitions);
            }
            catch (Exception exception)
            {
                Debug.LogError($"Failed to parse GOAP content at Resources/{ResourcePath}: {exception}");
                return new GoapContent(Array.Empty<UnityItemDefinition>());
            }
        }

        [Serializable]
        private sealed class GoapContentPayload
        {
            public ItemDefinitionPayload[] items;
        }

        [Serializable]
        private sealed class ItemDefinitionPayload
        {
            public string id;
            public string displayName;
            public string spriteId;
        }
    }

    internal static class MapGenerator
    {
        public static GoapMap Generate(MapDefinitionDto mapDefinition, SimulationConfig config, System.Random random)
        {
            var tiles = new List<MapTile>(config.MapSize.x * config.MapSize.y);
            var halfWidth = (config.MapSize.x - 1) * 0.5f;
            var halfHeight = (config.MapSize.y - 1) * 0.5f;

            var definitionLookup = new Dictionary<Vector2Int, MapTileDefinitionDto>(config.MapSize.x * config.MapSize.y);
            if (mapDefinition?.tiles != null)
            {
                foreach (var tile in mapDefinition.tiles)
                {
                    if (tile == null)
                    {
                        continue;
                    }

                    var coordinates = tile.coordinates.ToVector2Int();
                    definitionLookup[coordinates] = tile;
                }
            }

            for (var y = 0; y < config.MapSize.y; y++)
            {
                for (var x = 0; x < config.MapSize.x; x++)
                {
                    var coordinates = new Vector2Int(x, y);
                    float elevation;
                    float traversalCost;

                    if (definitionLookup.TryGetValue(coordinates, out var definition))
                    {
                        elevation = Mathf.Clamp(definition.elevation, config.ElevationRange.x, config.ElevationRange.y);
                        traversalCost = Mathf.Max(0f, definition.traversalCost);
                    }
                    else
                    {
                        var sample = (float)random.NextDouble();
                        elevation = Mathf.Lerp(config.ElevationRange.x, config.ElevationRange.y, sample);
                        traversalCost = Mathf.Lerp(1f, 5f, (float)random.NextDouble());
                    }

                    var normalized = Mathf.Approximately(config.ElevationRange.x, config.ElevationRange.y)
                        ? 0f
                        : Mathf.InverseLerp(config.ElevationRange.x, config.ElevationRange.y, elevation);
                    var worldCenter = new Vector3((x - halfWidth) * config.TileSpacing, 0f, (y - halfHeight) * config.TileSpacing);
                    tiles.Add(new MapTile(coordinates, elevation, normalized, traversalCost, worldCenter));
                }
            }

            return new GoapMap(config.MapSize, tiles);
        }
    }

    internal static class ItemFactory
    {
        public static List<ItemInternal> Create(GoapMap map, SimulationConfig config, GoapContent content, System.Random random)
        {
            var items = new List<ItemInternal>();
            if (map == null || content == null)
            {
                return items;
            }

            var definitions = content.ItemDefinitions;
            if (definitions.Count == 0)
            {
                return items;
            }

            var tiles = new List<MapTile>(map.Tiles);
            if (tiles.Count == 0)
            {
                return items;
            }

            var targetCount = Mathf.Clamp(tiles.Count / 4, 1, tiles.Count);
            var heightOffset = Mathf.Max(0.05f, config.PawnHeightOffset * 0.3f);

            for (var i = 0; i < targetCount; i++)
            {
                var definition = definitions[random.Next(0, definitions.Count)];
                var tileIndex = random.Next(0, tiles.Count);
                var tile = tiles[tileIndex];
                tiles.RemoveAt(tileIndex);
                var worldPosition = tile.WorldSurfacePosition + Vector3.up * heightOffset;
                items.Add(new ItemInternal(i, definition, tile.Coordinates, worldPosition));

                if (tiles.Count == 0)
                {
                    break;
                }
            }

            return items;
        }
    }

    internal static class PawnFactory
    {
        public static List<PawnInternal> Create(
            GoapMap map,
            SimulationConfig config,
            PawnDefinitionsDto pawnDefinitions,
            System.Random random)
        {
            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (pawnDefinitions == null)
            {
                throw new ArgumentNullException(nameof(pawnDefinitions));
            }

            var definitions = pawnDefinitions.pawns ?? Array.Empty<PawnDefinitionDto>();
            var pawns = new List<PawnInternal>(definitions.Length);
            if (definitions.Length == 0)
            {
                return pawns;
            }

            var mapSize = map.Size;
            var usedIds = new HashSet<int>();

            for (var i = 0; i < definitions.Length; i++)
            {
                var definition = definitions[i];
                if (definition == null)
                {
                    throw new InvalidOperationException($"Pawn definition at index {i} is null.");
                }

                var id = definition.id;
                if (id < 0)
                {
                    throw new InvalidOperationException($"Pawn definition at index {i} must specify a non-negative id.");
                }

                if (!usedIds.Add(id))
                {
                    throw new InvalidOperationException($"Duplicate pawn id '{id}' detected.");
                }

                var spawnCoordinates = definition.spawnTile.ToVector2Int();
                if (spawnCoordinates.x < 0 || spawnCoordinates.x >= mapSize.x ||
                    spawnCoordinates.y < 0 || spawnCoordinates.y >= mapSize.y)
                {
                    throw new InvalidOperationException(
                        $"Pawn '{id}' spawn tile {spawnCoordinates} is outside the map bounds {mapSize}.");
                }

                var colorString = definition.color?.Trim();
                if (!ColorUtility.TryParseHtmlString(colorString, out var color))
                {
                    throw new InvalidOperationException(
                        $"Pawn '{id}' has an invalid color value '{definition.color}'.");
                }

                var speed = config.PawnSpeed;
                if (definition.speed > 0f)
                {
                    speed = definition.speed;
                }
                else if (definition.speed == 0f)
                {
                    throw new InvalidOperationException($"Pawn '{id}' speed override must be positive.");
                }
                else if (definition.speed < 0f && !Mathf.Approximately(definition.speed, -1f))
                {
                    throw new InvalidOperationException($"Pawn '{id}' speed override must be positive.");
                }

                var heightOffset = config.PawnHeightOffset;
                if (definition.heightOffset >= 0f)
                {
                    heightOffset = definition.heightOffset;
                }
                else if (!Mathf.Approximately(definition.heightOffset, -1f))
                {
                    throw new InvalidOperationException(
                        $"Pawn '{id}' height offset override must be non-negative.");
                }

                var tile = map.GetTile(spawnCoordinates);
                var name = string.IsNullOrWhiteSpace(definition.name) ? $"Pawn {id}" : definition.name.Trim();
                var pawn = new PawnInternal(id, name, color, speed, heightOffset);
                pawn.TeleportTo(tile);
                pawn.TargetTile = spawnCoordinates;
                pawns.Add(pawn);
            }

            return pawns;
        }
    }

    /// <summary>
    /// Internal mutable pawn state used to drive the simulation.
    /// </summary>
    public sealed class PawnInternal
    {
        public PawnInternal(int id, string name, Color color, float speed, float heightOffset)
        {
            Id = id;
            Name = name;
            Color = color;
            Speed = speed;
            HeightOffset = heightOffset;
        }

        public int Id { get; }

        public string Name { get; }

        public Color Color { get; }

        public float Speed { get; }

        public float HeightOffset { get; }

        public Vector2Int CurrentTile { get; set; }

        public Vector2Int TargetTile { get; set; }

        public Vector3 WorldPosition { get; set; }

        public void TeleportTo(MapTile tile)
        {
            CurrentTile = tile.Coordinates;
            WorldPosition = tile.WorldSurfacePosition + Vector3.up * HeightOffset;
        }

        public PawnSnapshot CreateSnapshot()
        {
            return new PawnSnapshot(Id, Name, Color, WorldPosition, CurrentTile, TargetTile);
        }
    }

    /// <summary>
    /// Internal mutable item state used to drive the simulation.
    /// </summary>
    public sealed class ItemInternal
    {
        public ItemInternal(int id, UnityItemDefinition definition, Vector2Int tile, Vector3 worldPosition)
        {
            Id = id;
            Definition = definition;
            Tile = tile;
            WorldPosition = worldPosition;
        }

        public int Id { get; }

        public UnityItemDefinition Definition { get; }

        public Vector2Int Tile { get; }

        public Vector3 WorldPosition { get; }

        public ItemSnapshot CreateSnapshot()
        {
            return new ItemSnapshot(Id, Definition?.Id, WorldPosition, Tile);
        }
    }
}
