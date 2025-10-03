using System.Collections.Generic;
using DataDrivenGoap;
using UnityEngine;

/// <summary>
/// Unity hook that bootstraps the DataDrivenGoap simulation and renders a simple tile map with animated pawns.
/// </summary>
public sealed class GoapSimulationBootstrapper : MonoBehaviour
{
    [Header("Simulation Setup")]
    [SerializeField] private Vector2Int mapSize = new Vector2Int(12, 12);
    [SerializeField, Min(0)] private int pawnCount = 6;
    [SerializeField, Min(0.1f)] private float tileSpacing = 1.5f;
    [SerializeField] private Vector2 elevationRange = new Vector2(0.3f, 1.5f);
    [SerializeField, Min(0.1f)] private float pawnSpeed = 2f;
    [SerializeField, Min(0f)] private float pawnHeightOffset = 0.75f;
    [SerializeField] private int randomSeed = 1337;

    [Header("Visual Styling")]
    [SerializeField] private float tileScaleFactor = 0.9f;
    [SerializeField] private Color lowElevationColor = new Color(0.16f, 0.42f, 0.23f);
    [SerializeField] private Color midElevationColor = new Color(0.88f, 0.79f, 0.29f);
    [SerializeField] private Color highElevationColor = Color.white;
    [SerializeField] private float pawnVisualScale = 0.6f;

    private readonly Dictionary<Vector2Int, GameObject> _tiles = new();
    private readonly Dictionary<int, GameObject> _pawns = new();
    private Simulation _simulation;
    private SimulationConfig _config;
    private Transform _mapRoot;
    private Transform _pawnRoot;
    private Sprite _tileSprite;
    private Sprite _pawnSprite;
    private Texture2D _pawnTexture;

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
        _config = new SimulationConfig(mapSize, pawnCount, tileSpacing, elevationRange, pawnSpeed, pawnHeightOffset, randomSeed);
        _simulation = SimulationFactory.Create(_config);
        _simulation.TileGenerated += HandleTileGenerated;
        _simulation.PawnSpawned += HandlePawnSpawned;
        _simulation.PawnUpdated += HandlePawnUpdated;
        _simulation.Start();

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
        if (_tiles.ContainsKey(tile.Coordinates))
        {
            return;
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
    }

    private void HandlePawnSpawned(PawnSnapshot pawn)
    {
        if (_pawns.ContainsKey(pawn.Id))
        {
            return;
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
    }

    private void HandlePawnUpdated(PawnSnapshot pawn)
    {
        if (_pawns.TryGetValue(pawn.Id, out var pawnObject))
        {
            pawnObject.transform.position = ProjectTo2D(pawn.WorldPosition);
        }
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
}
