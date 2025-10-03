using System;
using System.Collections.Generic;
using UnityEngine;

namespace DataDrivenGoap
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
    /// Immutable description of an item defined in the map.
    /// </summary>
    public sealed class GoapItem
    {
        public GoapItem(string id, string name, Vector2Int tile, Vector3 worldPosition)
        {
            Id = string.IsNullOrEmpty(id) ? name ?? string.Empty : id;
            Name = string.IsNullOrEmpty(name) ? Id : name;
            Tile = tile;
            WorldPosition = worldPosition;
        }

        public string Id { get; }

        public string Name { get; }

        public Vector2Int Tile { get; }

        public Vector3 WorldPosition { get; }
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
        public static Simulation Create(
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
            var pawnCount = pawnDefinitions.pawns.Length;
            var defaultSpeed = Mathf.Max(0.01f, pawnDefinitions.defaultSpeed);
            var defaultHeightOffset = pawnDefinitions.defaultHeightOffset;

            var config = new SimulationConfig(
                mapSize,
                pawnCount,
                tileSpacing,
                new Vector2(minElevation, maxElevation),
                defaultSpeed,
                defaultHeightOffset,
                randomSeed);

            var random = new System.Random(config.RandomSeed);
            var map = BuildMap(config, mapDefinition);
            var pawns = BuildPawns(map, config, pawnDefinitions);
            var items = BuildItems(map, itemDefinitions);
            return new Simulation(config, map, pawns, items, random);
        }

        private static GoapMap BuildMap(SimulationConfig config, MapDefinitionDto mapDefinition)
        {
            var tiles = new List<MapTile>(config.MapSize.x * config.MapSize.y);
            var halfWidth = (config.MapSize.x - 1) * 0.5f;
            var halfHeight = (config.MapSize.y - 1) * 0.5f;
            var minElevation = config.ElevationRange.x;
            var maxElevation = config.ElevationRange.y;
            var elevationRange = Mathf.Approximately(minElevation, maxElevation)
                ? 0f
                : maxElevation - minElevation;

            var definedTiles = new HashSet<Vector2Int>();
            foreach (var tileDefinition in mapDefinition.tiles)
            {
                if (tileDefinition == null)
                {
                    continue;
                }

                var coordinates = tileDefinition.coordinates.ToVector2Int();
                var elevation = tileDefinition.elevation;
                var traversalCost = Mathf.Max(0f, tileDefinition.traversalCost);
                var normalizedElevation = elevationRange <= Mathf.Epsilon
                    ? 0f
                    : Mathf.Clamp01((elevation - minElevation) / elevationRange);
                var worldCenter = new Vector3(
                    (coordinates.x - halfWidth) * config.TileSpacing,
                    0f,
                    (coordinates.y - halfHeight) * config.TileSpacing);

                tiles.Add(new MapTile(coordinates, elevation, normalizedElevation, traversalCost, worldCenter));
                definedTiles.Add(coordinates);
            }

            for (var y = 0; y < config.MapSize.y; y++)
            {
                for (var x = 0; x < config.MapSize.x; x++)
                {
                    var coordinates = new Vector2Int(x, y);
                    if (definedTiles.Contains(coordinates))
                    {
                        continue;
                    }

                    var worldCenter = new Vector3(
                        (coordinates.x - halfWidth) * config.TileSpacing,
                        0f,
                        (coordinates.y - halfHeight) * config.TileSpacing);
                    tiles.Add(new MapTile(coordinates, minElevation, 0f, 1f, worldCenter));
                }
            }

            return new GoapMap(config.MapSize, tiles);
        }

        private static List<PawnInternal> BuildPawns(GoapMap map, SimulationConfig config, PawnDefinitionsDto pawnDefinitions)
        {
            var pawns = new List<PawnInternal>(pawnDefinitions.pawns.Length);
            for (var i = 0; i < pawnDefinitions.pawns.Length; i++)
            {
                var definition = pawnDefinitions.pawns[i];
                if (definition == null)
                {
                    continue;
                }

                var id = definition.id >= 0 ? definition.id : i;
                var name = string.IsNullOrWhiteSpace(definition.name) ? $"Pawn {i + 1}" : definition.name;
                var color = ParseColor(definition.color, i);
                var speed = definition.speed > 0f ? definition.speed : config.PawnSpeed;
                var heightOffset = definition.heightOffset >= 0f ? definition.heightOffset : config.PawnHeightOffset;
                var spawnTile = definition.spawnTile.ToVector2Int();

                var pawn = new PawnInternal(id, name, color, speed, heightOffset);
                var tile = map.GetTile(spawnTile);
                pawn.TeleportTo(tile);
                pawn.TargetTile = spawnTile;
                pawns.Add(pawn);
            }

            return pawns;
        }

        private static List<GoapItem> BuildItems(GoapMap map, ItemDefinitionsDto itemDefinitions)
        {
            var items = new List<GoapItem>(itemDefinitions.items.Length);
            foreach (var definition in itemDefinitions.items)
            {
                if (definition == null)
                {
                    continue;
                }

                var coordinates = definition.tile.ToVector2Int();
                var tile = map.GetTile(coordinates);
                items.Add(new GoapItem(definition.id, definition.name, coordinates, tile.WorldSurfacePosition));
            }

            return items;
        }

        private static Color ParseColor(string value, int index)
        {
            if (!string.IsNullOrWhiteSpace(value) && ColorUtility.TryParseHtmlString(value, out var parsed))
            {
                return parsed;
            }

            var hue = Mathf.Repeat(index * 0.3125f, 1f);
            return Color.HSVToRGB(hue, 0.8f, 1f);
        }
    }

    /// <summary>
    /// Main runtime simulation loop.
    /// </summary>
    public sealed class Simulation
    {
        private readonly SimulationConfig _config;
        private readonly GoapMap _map;
        private readonly List<PawnInternal> _pawns;
        private readonly List<GoapItem> _items;
        private readonly System.Random _random;

        public Simulation(
            SimulationConfig config,
            GoapMap map,
            List<PawnInternal> pawns,
            IReadOnlyList<GoapItem> items,
            System.Random random)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _map = map ?? throw new ArgumentNullException(nameof(map));
            _pawns = pawns ?? throw new ArgumentNullException(nameof(pawns));
            _items = items != null ? new List<GoapItem>(items) : new List<GoapItem>();
            _random = random ?? throw new ArgumentNullException(nameof(random));
        }

        public event Action<MapTile> TileGenerated;

        public event Action<PawnSnapshot> PawnSpawned;

        public event Action<PawnSnapshot> PawnUpdated;

        public SimulationConfig Config => _config;

        public GoapMap Map => _map;

        public IReadOnlyList<GoapItem> Items => _items;

        public void Start()
        {
            foreach (var tile in _map.Tiles)
            {
                TileGenerated?.Invoke(tile);
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
}
