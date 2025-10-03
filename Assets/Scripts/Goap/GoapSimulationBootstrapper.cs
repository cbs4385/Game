using System;
using System.Collections.Generic;
using DataDrivenGoap;
using UnityEngine;

/// <summary>
/// Unity hook that bootstraps the DataDrivenGoap simulation and renders a simple tile map with animated pawns.
/// </summary>
public sealed class GoapSimulationBootstrapper : MonoBehaviour
{
    [Header("Data Sources")]
    [SerializeField] private TextAsset mapDefinitionAsset;
    [SerializeField] private TextAsset pawnDefinitionAsset;
    [SerializeField] private TextAsset itemDefinitionAsset;

    [Header("Simulation Setup")]
    [SerializeField] private int randomSeed = 1337;

    [Header("Visual Styling")]
    [SerializeField] private float tileScaleFactor = 0.9f;
    [SerializeField] private Color lowElevationColor = new Color(0.16f, 0.42f, 0.23f);
    [SerializeField] private Color midElevationColor = new Color(0.88f, 0.79f, 0.29f);
    [SerializeField] private Color highElevationColor = Color.white;
    [SerializeField] private float pawnVisualScale = 0.6f;

    private readonly Dictionary<Vector2Int, GameObject> _tiles = new();
    private readonly Dictionary<int, GameObject> _pawns = new();
    private readonly Dictionary<int, PawnSnapshot> _pawnSnapshots = new();
    private Simulation _simulation;
    private SimulationConfig _config;
    private Transform _mapRoot;
    private Transform _pawnRoot;
    private Sprite _tileSprite;
    private Sprite _pawnSprite;
    private Texture2D _pawnTexture;

    public event Action<Simulation> SimulationInitialized;

    public Simulation Simulation => _simulation;

    public SimulationConfig Config => _config;

    public IReadOnlyDictionary<Vector2Int, GameObject> TileObjects => _tiles;

    public IReadOnlyDictionary<int, GameObject> PawnObjects => _pawns;

    public IReadOnlyDictionary<int, PawnSnapshot> CurrentPawnSnapshots => _pawnSnapshots;

    private void Awake()
    {
        _mapRoot = new GameObject("Generated Map").transform;
        _mapRoot.SetParent(transform, false);
        _pawnRoot = new GameObject("Pawns").transform;
        _pawnRoot.SetParent(transform, false);

        _tileSprite = CreateSquareSprite();
        _pawnSprite = CreatePawnSprite();
    }

    private void Start()
    {
        if (_simulation != null)
        {
            _simulation.TileGenerated -= HandleTileGenerated;
            _simulation.PawnSpawned -= HandlePawnSpawned;
            _simulation.PawnUpdated -= HandlePawnUpdated;
        }

        ResetSceneState();
        if (mapDefinitionAsset == null)
        {
            Debug.LogError("Cannot start GOAP simulation without a map definition asset.");
            return;
        }

        if (pawnDefinitionAsset == null)
        {
            Debug.LogError("Cannot start GOAP simulation without a pawn definition asset.");
            return;
        }

        try
        {
            var mapDefinition = DataDrivenGoapJsonLoader.LoadMapDefinition(mapDefinitionAsset);
            var pawnDefinitions = DataDrivenGoapJsonLoader.LoadPawnDefinitions(pawnDefinitionAsset);
            var itemDefinitions = itemDefinitionAsset != null
                ? DataDrivenGoapJsonLoader.LoadItemDefinitions(itemDefinitionAsset)
                : ItemDefinitionsDto.Empty;

            _simulation = SimulationFactory.Create(mapDefinition, pawnDefinitions, itemDefinitions, randomSeed);
            _config = _simulation.Config;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to create GOAP simulation: {ex.Message}");
            Debug.LogException(ex);
            return;
        }

        _simulation.TileGenerated += HandleTileGenerated;
        _simulation.PawnSpawned += HandlePawnSpawned;
        _simulation.PawnUpdated += HandlePawnUpdated;
        _simulation.Start();

        SimulationInitialized?.Invoke(_simulation);

        Debug.Log(
            $"GOAP simulation started with world {_config.MapSize.x}x{_config.MapSize.y}, {_config.PawnCount} pawns, {_simulation.Items.Count} items, tile spacing {_config.TileSpacing:F2}, elevation range {_config.ElevationRange.x:F2}-{_config.ElevationRange.y:F2}, pawn speed {_config.PawnSpeed:F2}, height offset {_config.PawnHeightOffset:F2} (seed {_config.RandomSeed}).");

        ConfigureCamera();
    }

    private void Update()
    {
        _simulation?.Update(Time.deltaTime);
    }

    private void OnDestroy()
    {
        if (_simulation != null)
        {
            _simulation.TileGenerated -= HandleTileGenerated;
            _simulation.PawnSpawned -= HandlePawnSpawned;
            _simulation.PawnUpdated -= HandlePawnUpdated;
            SimulationInitialized = null;
        }

        if (_pawnTexture != null)
        {
            Destroy(_pawnTexture);
            _pawnTexture = null;
        }

        if (_pawnSprite != null)
        {
            Destroy(_pawnSprite);
            _pawnSprite = null;
        }

        if (_tileSprite != null)
        {
            Destroy(_tileSprite);
            _tileSprite = null;
        }
    }

    private void HandleTileGenerated(MapTile tile)
    {
        if (_tiles.TryGetValue(tile.Coordinates, out var existingTile))
        {
            if (existingTile != null)
            {
                Destroy(existingTile);
            }

            _tiles.Remove(tile.Coordinates);
        }

        var tileObject = new GameObject();
        tileObject.name = $"Tile ({tile.Coordinates.x},{tile.Coordinates.y})";
        tileObject.transform.SetParent(_mapRoot, false);

        tileObject.transform.position = ProjectTo2D(tile.WorldCenter);
        tileObject.transform.localScale = new Vector3(_config.TileSpacing * tileScaleFactor, _config.TileSpacing * tileScaleFactor, 1f);

        var renderer = tileObject.AddComponent<SpriteRenderer>();
        renderer.sprite = _tileSprite;
        renderer.sortingOrder = 0;
        renderer.color = EvaluateElevationColor(tile.NormalizedElevation);

        _tiles[tile.Coordinates] = tileObject;

        Debug.Log(
            $"Tile generated at {tile.Coordinates} | elevation {tile.Elevation:F2} (normalized {tile.NormalizedElevation:F2}), traversal cost {tile.TraversalCost:F2}, world center {tile.WorldCenter}.");
    }

    private void HandlePawnSpawned(PawnSnapshot pawn)
    {
        if (_pawns.TryGetValue(pawn.Id, out var existingPawn))
        {
            if (existingPawn != null)
            {
                Destroy(existingPawn);
            }

            _pawns.Remove(pawn.Id);
            _pawnSnapshots.Remove(pawn.Id);
        }

        var pawnObject = new GameObject();
        pawnObject.name = pawn.Name;
        pawnObject.transform.SetParent(_pawnRoot, false);
        pawnObject.transform.localScale = Vector3.one * pawnVisualScale;

        var renderer = pawnObject.AddComponent<SpriteRenderer>();
        renderer.sprite = _pawnSprite;
        renderer.sortingOrder = 1;
        renderer.color = pawn.Color;

        pawnObject.transform.position = ProjectTo2D(pawn.WorldPosition);
        _pawns[pawn.Id] = pawnObject;
        _pawnSnapshots[pawn.Id] = pawn;

        Debug.Log($"Pawn spawned: {pawn.Name} (ID {pawn.Id}) at tile {pawn.Tile} targeting {pawn.TargetTile}, world position {pawn.WorldPosition}.");
    }

    private void HandlePawnUpdated(PawnSnapshot pawn)
    {
        if (_pawns.TryGetValue(pawn.Id, out var pawnObject))
        {
            pawnObject.transform.position = ProjectTo2D(pawn.WorldPosition);
        }

        if (_pawnSnapshots.TryGetValue(pawn.Id, out var previous))
        {
            if (pawn.Tile != previous.Tile)
            {
                Debug.Log(
                    $"Pawn {pawn.Name} reached tile {pawn.Tile} at world position {pawn.WorldPosition}. Target tile remains {pawn.TargetTile}.");
            }

            if (pawn.TargetTile != previous.TargetTile)
            {
                Debug.Log($"Pawn {pawn.Name} now targeting tile {pawn.TargetTile}.");
            }
        }

        _pawnSnapshots[pawn.Id] = pawn;
    }

    private Color EvaluateElevationColor(float normalizedHeight)
    {
        normalizedHeight = Mathf.Clamp01(normalizedHeight);
        if (normalizedHeight <= 0.5f)
        {
            var t = normalizedHeight / 0.5f;
            return Color.Lerp(lowElevationColor, midElevationColor, t);
        }
        else
        {
            var t = (normalizedHeight - 0.5f) / 0.5f;
            return Color.Lerp(midElevationColor, highElevationColor, t);
        }
    }

    private void ConfigureCamera()
    {
        var camera = Camera.main;
        if (camera == null)
        {
            return;
        }

        camera.orthographic = true;

        var margin = _config.TileSpacing;
        var halfWidth = ((_config.MapSize.x - 1) * 0.5f + 0.5f) * _config.TileSpacing;
        var halfHeight = ((_config.MapSize.y - 1) * 0.5f + 0.5f) * _config.TileSpacing;
        var requiredVertical = halfHeight + margin;
        var requiredHorizontal = (halfWidth + margin) / Mathf.Max(camera.aspect, 0.0001f);
        camera.orthographicSize = Mathf.Max(requiredVertical, requiredHorizontal);

        var cameraTransform = camera.transform;
        cameraTransform.position = new Vector3(0f, 0f, -10f);
        cameraTransform.rotation = Quaternion.identity;
    }

    private static Vector3 ProjectTo2D(Vector3 worldPosition)
    {
        return new Vector3(worldPosition.x, worldPosition.z, 0f);
    }

    private static Sprite CreateSquareSprite()
    {
        var texture = Texture2D.whiteTexture;
        return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), texture.width);
    }

    private Sprite CreatePawnSprite()
    {
        const int size = 64;
        _pawnTexture = new Texture2D(size, size, TextureFormat.ARGB32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "PawnSpriteTexture"
        };

        var center = (size - 1) * 0.5f;
        var radius = center;
        var pixels = new Color[size * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - center;
                var dy = y - center;
                var distance = Mathf.Sqrt(dx * dx + dy * dy);
                var alpha = distance <= radius ? 1f : 0f;
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }

        _pawnTexture.SetPixels(pixels);
        _pawnTexture.Apply();

        return Sprite.Create(_pawnTexture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
    }

    private void ResetSceneState()
    {
        foreach (var tileObject in _tiles.Values)
        {
            if (tileObject != null)
            {
                Destroy(tileObject);
            }
        }

        foreach (var pawnObject in _pawns.Values)
        {
            if (pawnObject != null)
            {
                Destroy(pawnObject);
            }
        }

        _tiles.Clear();
        _pawns.Clear();
        _pawnSnapshots.Clear();

        ClearTransformChildren(_mapRoot);
        ClearTransformChildren(_pawnRoot);
    }

    private static void ClearTransformChildren(Transform root)
    {
        if (root == null)
        {
            return;
        }

        for (var i = root.childCount - 1; i >= 0; i--)
        {
            var child = root.GetChild(i);
            if (child != null)
            {
                Destroy(child.gameObject);
            }
        }
    }
}

