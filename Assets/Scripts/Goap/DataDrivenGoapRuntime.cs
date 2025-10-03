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
        public static Simulation Create(SimulationConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            var random = new System.Random(config.RandomSeed);
            var map = MapGenerator.Generate(config, random);
            var pawns = PawnFactory.Create(map, config, random);
            return new Simulation(config, map, pawns, random);
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
        private readonly System.Random _random;

        public Simulation(SimulationConfig config, GoapMap map, List<PawnInternal> pawns, System.Random random)
        {
            _config = config;
            _map = map;
            _pawns = pawns;
            _random = random;
        }

        public event Action<MapTile> TileGenerated;

        public event Action<PawnSnapshot> PawnSpawned;

        public event Action<PawnSnapshot> PawnUpdated;

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
    }

    internal static class MapGenerator
    {
        public static GoapMap Generate(SimulationConfig config, System.Random random)
        {
            var tiles = new List<MapTile>(config.MapSize.x * config.MapSize.y);
            var halfWidth = (config.MapSize.x - 1) * 0.5f;
            var halfHeight = (config.MapSize.y - 1) * 0.5f;

            for (var y = 0; y < config.MapSize.y; y++)
            {
                for (var x = 0; x < config.MapSize.x; x++)
                {
                    var sample = (float)random.NextDouble();
                    var elevation = Mathf.Lerp(config.ElevationRange.x, config.ElevationRange.y, sample);
                    var normalized = Mathf.InverseLerp(config.ElevationRange.x, config.ElevationRange.y, elevation);
                    var traversalCost = Mathf.Lerp(1f, 5f, (float)random.NextDouble());
                    var worldCenter = new Vector3((x - halfWidth) * config.TileSpacing, 0f, (y - halfHeight) * config.TileSpacing);
                    tiles.Add(new MapTile(new Vector2Int(x, y), elevation, normalized, traversalCost, worldCenter));
                }
            }

            return new GoapMap(config.MapSize, tiles);
        }
    }

    internal static class PawnFactory
    {
        public static List<PawnInternal> Create(GoapMap map, SimulationConfig config, System.Random random)
        {
            var pawns = new List<PawnInternal>(config.PawnCount);
            for (var i = 0; i < config.PawnCount; i++)
            {
                var spawnCoordinates = new Vector2Int(random.Next(0, map.Size.x), random.Next(0, map.Size.y));
                var color = Color.HSVToRGB((float)random.NextDouble(), 0.8f, 1f);
                var pawn = new PawnInternal(i, $"Pawn {i + 1}", color, config.PawnSpeed, config.PawnHeightOffset);
                var spawnTile = map.GetTile(spawnCoordinates);
                pawn.TeleportTo(spawnTile);
                pawn.TargetTile = spawnCoordinates;
                pawns.Add(pawn);
            }

            return pawns;
        }
    }

    /// <summary>
    /// Internal mutable pawn state used to drive the simulation.
    /// </summary>
    internal sealed class PawnInternal
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
