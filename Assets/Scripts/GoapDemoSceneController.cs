using System;
using System.Collections.Generic;
using System.Linq;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.World;
using UnityEngine;

/// <summary>
/// Simple scene bootstrapper that builds a DataDrivenGOAP world on startup and
/// instantiates Unity primitives to visualize the generated map and pawns.
/// </summary>
public class GoapDemoSceneController : MonoBehaviour
{
    [Header("World Configuration")]
    [SerializeField] private int width = 18;
    [SerializeField] private int height = 12;
    [SerializeField] private int shardCount = 4;
    [SerializeField] private int pawnCount = 6;
    [SerializeField] private int worldSeed = 12345;
    [SerializeField] private float tileSize = 1.25f;

    [Header("Visual Styling")]
    [SerializeField] private Color walkableColor = new Color(0.55f, 0.78f, 0.55f);
    [SerializeField] private Color blockedColor = new Color(0.18f, 0.2f, 0.23f);
    [SerializeField] private Color[] pawnPalette =
    {
        new Color(0.87f, 0.32f, 0.32f),
        new Color(0.91f, 0.7f, 0.2f),
        new Color(0.3f, 0.67f, 0.91f),
        new Color(0.67f, 0.36f, 0.91f),
        new Color(0.28f, 0.8f, 0.62f)
    };
    [SerializeField] private Vector3 cameraOffset = new Vector3(0f, 10f, -8f);
    [SerializeField] private Vector3 cameraEulerAngles = new Vector3(55f, 0f, 0f);

    private ShardedWorld _world;
    private WorldClock _clock;
    private Transform _tileRoot;
    private Transform _pawnRoot;
    private readonly Dictionary<ThingId, GameObject> _pawnVisuals = new Dictionary<ThingId, GameObject>();

    private void Start()
    {
        GenerateWorld();
        BuildSceneFromWorld();
    }

    private void GenerateWorld()
    {
        if (width <= 0 || height <= 0)
        {
            Debug.LogWarning("World dimensions must be positive.");
            width = Math.Max(1, width);
            height = Math.Max(1, height);
        }

        var timeConfig = new TimeConfig
        {
            dayLengthSeconds = 120f,
            worldHoursPerDay = 24,
            minutesPerHour = 60,
            secondsPerMinute = 60,
            daysPerMonth = 30,
            seasonLengthDays = 30,
            seasons = new[] { "spring", "summer", "autumn", "winter" },
            startYear = 1,
            startDayOfYear = 1,
            startTimeOfDayHours = 9f
        };

        _clock = new WorldClock(timeConfig);

        var walkable = GenerateWalkableMask(width, height, worldSeed);
        var pawnPositions = ChoosePawnPositions(walkable, pawnCount, worldSeed);

        var seedThings = new List<SeedThing>();
        var random = new System.Random(worldSeed);

        for (int i = 0; i < pawnPositions.Count; i++)
        {
            var id = new ThingId($"pawn_{i + 1:00}");
            var tags = new[] { "pawn", "villager" };
            var attributes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["speed"] = 1.0 + (i * 0.1),
                ["energy"] = 0.5 + (random.NextDouble() * 0.5),
                ["colorIndex"] = i
            };
            seedThings.Add(new SeedThing(id, "villager", tags, pawnPositions[i], attributes, null));
        }

        var seedFacts = new List<Fact>();

        _world = new ShardedWorld(
            width,
            height,
            blockedChance: 0.0,
            shardCount: Math.Max(1, shardCount),
            rngSeed: worldSeed,
            seedThings: seedThings,
            seedFacts: seedFacts,
            clock: _clock,
            walkableOverride: walkable);
    }

    private void BuildSceneFromWorld()
    {
        if (_world == null)
            return;

        var snapshot = _world.Snap();
        ClearPreviousVisuals();
        BuildTiles(snapshot);
        BuildPawns(snapshot);
        FocusCamera(snapshot);
    }

    private void ClearPreviousVisuals()
    {
        if (_tileRoot != null)
        {
            DestroyImmediate(_tileRoot.gameObject);
        }
        if (_pawnRoot != null)
        {
            DestroyImmediate(_pawnRoot.gameObject);
        }
        _pawnVisuals.Clear();

        _tileRoot = new GameObject("Tiles").transform;
        _tileRoot.SetParent(transform, false);
        _pawnRoot = new GameObject("Pawns").transform;
        _pawnRoot.SetParent(transform, false);
    }

    private void BuildTiles(IWorldSnapshot snapshot)
    {
        for (int x = 0; x < snapshot.Width; x++)
        {
            for (int y = 0; y < snapshot.Height; y++)
            {
                var tileObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
                tileObject.name = $"Tile_{x}_{y}";
                tileObject.transform.SetParent(_tileRoot, false);
                tileObject.transform.localScale = new Vector3(tileSize, tileSize, tileSize);
                tileObject.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                tileObject.transform.position = GridToWorld(new GridPos(x, y));

                var renderer = tileObject.GetComponent<Renderer>();
                var color = snapshot.IsWalkable(x, y) ? walkableColor : blockedColor;
                renderer.sharedMaterial = new Material(renderer.sharedMaterial);
                renderer.sharedMaterial.color = color;

                var collider = tileObject.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider);
                }
            }
        }
    }

    private void BuildPawns(IWorldSnapshot snapshot)
    {
        foreach (var pawn in snapshot.QueryByTag("pawn"))
        {
            var pawnObject = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            pawnObject.name = pawn.Id.Value;
            pawnObject.transform.SetParent(_pawnRoot, false);
            pawnObject.transform.position = GridToWorld(pawn.Position) + new Vector3(0f, 0.5f * tileSize, 0f);
            pawnObject.transform.localScale = new Vector3(tileSize * 0.5f, tileSize * 0.75f, tileSize * 0.5f);

            var renderer = pawnObject.GetComponent<Renderer>();
            var color = ResolvePawnColor(pawn);
            renderer.sharedMaterial = new Material(renderer.sharedMaterial);
            renderer.sharedMaterial.color = color;

            var collider = pawnObject.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            _pawnVisuals[pawn.Id] = pawnObject;
        }
    }

    private void FocusCamera(IWorldSnapshot snapshot)
    {
        var camera = Camera.main;
        if (camera == null)
            return;

        var center = GridToWorld(new GridPos(snapshot.Width - 1, snapshot.Height - 1)) * 0.5f;
        camera.transform.position = center + cameraOffset;
        camera.transform.rotation = Quaternion.Euler(cameraEulerAngles);
    }

    private Vector3 GridToWorld(GridPos pos)
    {
        return new Vector3(pos.X * tileSize, 0f, pos.Y * tileSize);
    }

    private static bool[,] GenerateWalkableMask(int width, int height, int seed)
    {
        var mask = new bool[width, height];
        var random = new System.Random(seed);
        int centerX = width / 2;
        int centerY = height / 2;
        int radius = Math.Max(2, Math.Min(width, height) / 4);

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                var distance = Math.Abs(x - centerX) + Math.Abs(y - centerY);
                var walkable = distance > radius || (x % 3 == 0 && y % 2 == 0);
                if (!walkable && random.NextDouble() > 0.85)
                {
                    walkable = true;
                }
                mask[x, y] = walkable;
            }
        }

        // Ensure the border is always walkable so that actors have space to move.
        for (int x = 0; x < width; x++)
        {
            mask[x, 0] = true;
            mask[x, height - 1] = true;
        }
        for (int y = 0; y < height; y++)
        {
            mask[0, y] = true;
            mask[width - 1, y] = true;
        }

        return mask;
    }

    private static List<GridPos> ChoosePawnPositions(bool[,] walkable, int count, int seed)
    {
        int width = walkable.GetLength(0);
        int height = walkable.GetLength(1);
        var positions = new List<GridPos>();
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                if (walkable[x, y])
                {
                    positions.Add(new GridPos(x, y));
                }
            }
        }

        var random = new System.Random(seed ^ 0x5f3759df);
        for (int i = positions.Count - 1; i > 0; i--)
        {
            int swapIndex = random.Next(i + 1);
            (positions[i], positions[swapIndex]) = (positions[swapIndex], positions[i]);
        }

        if (positions.Count > count)
        {
            positions = positions.Take(count).ToList();
        }

        return positions;
    }

    private Color ResolvePawnColor(ThingView pawn)
    {
        if (pawnPalette == null || pawnPalette.Length == 0)
        {
            return Color.white;
        }

        var index = (int)Mathf.Repeat((float)pawn.AttrOrDefault("colorIndex", 0), pawnPalette.Length);
        return pawnPalette[index];
    }
}
