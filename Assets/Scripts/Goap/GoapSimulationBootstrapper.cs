using System;
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

    [Header("Visual Assets")]
    [SerializeField] private Sprite tileSprite;
    [SerializeField] private Sprite pawnSprite;
    [SerializeField] private Sprite defaultItemSprite;
    [SerializeField] private ItemSpriteMapping[] itemSpriteMappings;

    private readonly Dictionary<Vector2Int, GameObject> _tiles = new();
    private readonly Dictionary<int, GameObject> _pawns = new();
    private readonly Dictionary<int, GameObject> _items = new();
    private readonly Dictionary<int, PawnSnapshot> _pawnSnapshots = new();
    private readonly Dictionary<int, ItemSnapshot> _itemSnapshots = new();
    private readonly Dictionary<string, Sprite> _itemSpritesById = new(StringComparer.OrdinalIgnoreCase);
    private Simulation _simulation;
    private SimulationConfig _config;
    private Transform _mapRoot;
    private Transform _pawnRoot;
    private Transform _itemRoot;

    public event Action<Simulation> SimulationInitialized;

    public Simulation Simulation => _simulation;

    public SimulationConfig Config => _config;

    public IReadOnlyDictionary<Vector2Int, GameObject> TileObjects => _tiles;

    public IReadOnlyDictionary<int, GameObject> PawnObjects => _pawns;

    public IReadOnlyDictionary<int, PawnSnapshot> CurrentPawnSnapshots => _pawnSnapshots;

    public IReadOnlyDictionary<int, GameObject> ItemObjects => _items;

    public IReadOnlyDictionary<int, ItemSnapshot> CurrentItemSnapshots => _itemSnapshots;

    private void Awake()
    {
        _mapRoot = new GameObject("Generated Map").transform;
        _mapRoot.SetParent(transform, false);
        _pawnRoot = new GameObject("Pawns").transform;
        _pawnRoot.SetParent(transform, false);
        _itemRoot = new GameObject("Items").transform;
        _itemRoot.SetParent(transform, false);
    }

    private void Start()
    {
        if (_simulation != null)
        {
            _simulation.TileGenerated -= HandleTileGenerated;
            _simulation.PawnSpawned -= HandlePawnSpawned;
            _simulation.PawnUpdated -= HandlePawnUpdated;
            _simulation.ItemSpawned -= HandleItemSpawned;
        }

        ResetSceneState();

        _config = new SimulationConfig(mapSize, pawnCount, tileSpacing, elevationRange, pawnSpeed, pawnHeightOffset, randomSeed);
        _simulation = SimulationFactory.Create(_config);
        BuildItemSpriteLookup();
        _simulation.TileGenerated += HandleTileGenerated;
        _simulation.PawnSpawned += HandlePawnSpawned;
        _simulation.PawnUpdated += HandlePawnUpdated;
        _simulation.ItemSpawned += HandleItemSpawned;
        _simulation.Start();

        SimulationInitialized?.Invoke(_simulation);

        Debug.Log(
            $"GOAP simulation started with world {mapSize.x}x{mapSize.y}, {pawnCount} pawns, tile spacing {tileSpacing:F2}, elevation range {elevationRange.x:F2}-{elevationRange.y:F2}, pawn speed {pawnSpeed:F2}, height offset {pawnHeightOffset:F2} (seed {randomSeed}).");

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
            _simulation.ItemSpawned -= HandleItemSpawned;
            SimulationInitialized = null;
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
        renderer.sprite = tileSprite;
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
        renderer.sprite = pawnSprite;
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

    private void HandleItemSpawned(ItemSnapshot item)
    {
        if (_items.TryGetValue(item.Id, out var existingItem))
        {
            if (existingItem != null)
            {
                Destroy(existingItem);
            }

            _items.Remove(item.Id);
            _itemSnapshots.Remove(item.Id);
        }

        var itemObject = new GameObject();
        itemObject.name = GetItemDisplayName(item);
        itemObject.transform.SetParent(_itemRoot, false);
        itemObject.transform.position = ProjectTo2D(item.WorldPosition);
        var scale = Mathf.Max(0.1f, pawnVisualScale * 0.75f);
        itemObject.transform.localScale = Vector3.one * scale;

        var renderer = itemObject.AddComponent<SpriteRenderer>();
        renderer.sprite = ResolveItemSprite(item);
        renderer.sortingOrder = 2;

        _items[item.Id] = itemObject;
        _itemSnapshots[item.Id] = item;

        Debug.Log($"Item spawned: {itemObject.name} (ID {item.Id}) at tile {item.Tile}, world position {item.WorldPosition}.");
    }

    private void BuildItemSpriteLookup()
    {
        _itemSpritesById.Clear();

        if (itemSpriteMappings != null)
        {
            foreach (var mapping in itemSpriteMappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.spriteId) || mapping.sprite == null)
                {
                    continue;
                }

                _itemSpritesById[mapping.spriteId] = mapping.sprite;
            }
        }

        if (_simulation?.ItemDefinitions == null)
        {
            return;
        }

        foreach (var definition in _simulation.ItemDefinitions)
        {
            if (string.IsNullOrEmpty(definition.SpriteId) || _itemSpritesById.ContainsKey(definition.SpriteId))
            {
                continue;
            }

            if (defaultItemSprite != null)
            {
                _itemSpritesById[definition.SpriteId] = defaultItemSprite;
            }
        }
    }

    private Sprite ResolveItemSprite(ItemSnapshot item)
    {
        if (_simulation != null && _simulation.TryGetItemDefinition(item.DefinitionId, out var definition))
        {
            if (!string.IsNullOrEmpty(definition.SpriteId) &&
                _itemSpritesById.TryGetValue(definition.SpriteId, out var sprite) &&
                sprite != null)
            {
                return sprite;
            }
        }

        return defaultItemSprite;
    }

    private string GetItemDisplayName(ItemSnapshot item)
    {
        if (_simulation != null && _simulation.TryGetItemDefinition(item.DefinitionId, out var definition))
        {
            if (!string.IsNullOrEmpty(definition.DisplayName))
            {
                return $"{definition.DisplayName} ({item.Id})";
            }

            if (!string.IsNullOrEmpty(definition.Id))
            {
                return $"{definition.Id} ({item.Id})";
            }
        }

        return $"Item {item.Id}";
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

        foreach (var itemObject in _items.Values)
        {
            if (itemObject != null)
            {
                Destroy(itemObject);
            }
        }

        _tiles.Clear();
        _pawns.Clear();
        _items.Clear();
        _pawnSnapshots.Clear();
        _itemSnapshots.Clear();
        _itemSpritesById.Clear();

        ClearTransformChildren(_mapRoot);
        ClearTransformChildren(_pawnRoot);
        ClearTransformChildren(_itemRoot);
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

    [Serializable]
    private struct ItemSpriteMapping
    {
        public string spriteId;
        public Sprite sprite;
    }
}

