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

    private void Awake()
    {
        _mapRoot = new GameObject("Generated Map").transform;
        _mapRoot.SetParent(transform, false);
        _pawnRoot = new GameObject("Pawns").transform;
        _pawnRoot.SetParent(transform, false);
    }

    private void Start()
    {
        _config = new SimulationConfig(mapSize, pawnCount, tileSpacing, elevationRange, pawnSpeed, pawnHeightOffset, randomSeed);
        _simulation = SimulationFactory.Create(_config);
        _simulation.TileGenerated += HandleTileGenerated;
        _simulation.PawnSpawned += HandlePawnSpawned;
        _simulation.PawnUpdated += HandlePawnUpdated;
        _simulation.Start();
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
    }

    private void HandleTileGenerated(MapTile tile)
    {
        if (_tiles.ContainsKey(tile.Coordinates))
        {
            return;
        }

        var tileObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        tileObject.name = $"Tile ({tile.Coordinates.x},{tile.Coordinates.y})";
        tileObject.transform.SetParent(_mapRoot, false);

        var position = tile.WorldCenter;
        position.y = tile.Elevation * 0.5f;
        tileObject.transform.position = position;

        var scale = new Vector3(_config.TileSpacing * tileScaleFactor, Mathf.Max(tile.Elevation, 0.1f), _config.TileSpacing * tileScaleFactor);
        tileObject.transform.localScale = scale;

        if (tileObject.TryGetComponent(out Collider collider))
        {
            collider.enabled = false;
        }

        if (tileObject.TryGetComponent(out Renderer renderer))
        {
            renderer.material.color = EvaluateElevationColor(tile.NormalizedElevation);
        }

        _tiles[tile.Coordinates] = tileObject;
    }

    private void HandlePawnSpawned(PawnSnapshot pawn)
    {
        if (_pawns.ContainsKey(pawn.Id))
        {
            return;
        }

        var pawnObject = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        pawnObject.name = pawn.Name;
        pawnObject.transform.SetParent(_pawnRoot, false);
        pawnObject.transform.localScale = Vector3.one * pawnVisualScale;

        if (pawnObject.TryGetComponent(out Collider collider))
        {
            collider.enabled = false;
        }

        if (pawnObject.TryGetComponent(out Renderer renderer))
        {
            renderer.material.color = pawn.Color;
        }

        pawnObject.transform.position = pawn.WorldPosition;
        _pawns[pawn.Id] = pawnObject;
    }

    private void HandlePawnUpdated(PawnSnapshot pawn)
    {
        if (_pawns.TryGetValue(pawn.Id, out var pawnObject))
        {
            pawnObject.transform.position = pawn.WorldPosition;
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
}
