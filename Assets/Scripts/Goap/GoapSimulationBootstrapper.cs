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

    [Header("Map Loader Integration")]
    [SerializeField] private MapLoaderSettings mapLoaderSettings = new();

    [Header("Simulation Setup")]
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
            UnsubscribeFromSimulationEvents();
        }

        ResetSceneState();

        if (!TryLoadMapDefinition(out var mapDefinition))
        {
            return;
        }

        var simulation = CreateSimulation(mapDefinition);
        if (simulation == null)
        {
            return;
        }

        InitializeSimulation(simulation);
    }

    private Simulation CreateSimulation(MapDefinitionDto mapDefinition)
    {
        if (pawnDefinitionAsset == null)
        {
            Debug.LogError("Cannot start GOAP simulation without a pawn definition asset.");
            return null;
        }

        try
        {
            var pawnDefinitions = DataDrivenGoapJsonLoader.LoadPawnDefinitions(pawnDefinitionAsset);
            var itemDefinitions = itemDefinitionAsset != null
                ? DataDrivenGoapJsonLoader.LoadItemDefinitions(itemDefinitionAsset)
                : ItemDefinitionsDto.Empty;

            return SimulationFactory.Create(mapDefinition, pawnDefinitions, itemDefinitions, randomSeed);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to create GOAP simulation: {ex.Message}");
            Debug.LogException(ex);
            return null;
        }
    }

    private void InitializeSimulation(Simulation simulation)
    {
        _simulation = simulation;
        _config = _simulation.Config;

        BuildItemSpriteLookup();
        SubscribeToSimulationEvents();
        _simulation.Start();

        SimulationInitialized?.Invoke(_simulation);

        Debug.Log(
            $"GOAP simulation started with world {_config.MapSize.x}x{_config.MapSize.y}, {_config.PawnCount} pawns, {_itemSnapshots.Count} items, tile spacing {_config.TileSpacing:F2}, elevation range {_config.ElevationRange.x:F2}-{_config.ElevationRange.y:F2}, pawn speed {_config.PawnSpeed:F2}, height offset {_config.PawnHeightOffset:F2} (seed {_config.RandomSeed}).");

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
            UnsubscribeFromSimulationEvents();
            SimulationInitialized = null;
            _simulation = null;
            _config = null;
        }
    }

    private bool TryLoadMapDefinition(out MapDefinitionDto mapDefinition)
    {
        if (mapDefinitionAsset != null)
        {
            try
            {
                mapDefinition = DataDrivenGoapJsonLoader.LoadMapDefinition(mapDefinitionAsset);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to load GOAP map definition asset '{mapDefinitionAsset.name}': {ex.Message}");
                Debug.LogException(ex);
            }
        }

        if (TryLoadMapDefinitionFromMapLoader(out mapDefinition))
        {
            return true;
        }

        mapDefinition = null;
        Debug.LogError(
            "Cannot start GOAP simulation without a map definition asset or a configured map loader source. " +
            "Configure a proper simulation-provided map definition; automatic fallback generation is no longer supported.");
        return false;
    }

    private bool TryLoadMapDefinitionFromMapLoader(out MapDefinitionDto mapDefinition)
    {
        mapDefinition = null;

        if (mapLoaderSettings == null || !mapLoaderSettings.IsConfigured)
        {
            return false;
        }

        if (!MapLoader.TryLoadWorldMap(mapLoaderSettings.worldSettingsAsset, out var worldMapConfig, out var errorMessage))
        {
            Debug.LogError($"Failed to load world map configuration from '{mapLoaderSettings.worldSettingsAsset.name}': {errorMessage}");
            return false;
        }

        if (mapLoaderSettings.mapTexture == null)
        {
            Debug.LogError("Map loader configuration is missing the map texture asset.");
            return false;
        }

        if (mapLoaderSettings.villageDataAsset == null)
        {
            Debug.LogError("Map loader configuration is missing the village data asset.");
            return false;
        }

        VillageConfig villageConfig;
        try
        {
            villageConfig = JsonUtilities.Deserialize<VillageConfig>(mapLoaderSettings.villageDataAsset.text) ?? new VillageConfig();
            villageConfig.ApplyDefaults();
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to parse village data asset '{mapLoaderSettings.villageDataAsset.name}': {ex.Message}");
            Debug.LogException(ex);
            return false;
        }

        try
        {
            var mapResult = MapLoader.Load(mapLoaderSettings.mapTexture, worldMapConfig, villageConfig);
            mapDefinition = ConvertToMapDefinition(mapResult);
            mapDefinition.ApplyDefaults();
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to convert loaded map data into a GOAP map definition: {ex.Message}");
            Debug.LogException(ex);
            return false;
        }
    }

    private static MapDefinitionDto ConvertToMapDefinition(MapLoaderResult mapResult)
    {
        if (mapResult == null)
        {
            throw new ArgumentNullException(nameof(mapResult));
        }

        var farmland = BuildLookup(mapResult.FarmlandTiles);
        var water = BuildLookup(mapResult.WaterTiles);
        var shallowWater = BuildLookup(mapResult.ShallowWaterTiles);
        var forest = BuildLookup(mapResult.ForestTiles);
        var coastal = BuildLookup(mapResult.CoastalTiles);

        var tiles = new List<MapTileDefinitionDto>(mapResult.Width * mapResult.Height);
        for (var y = 0; y < mapResult.Height; y++)
        {
            for (var x = 0; x < mapResult.Width; x++)
            {
                var coordinates = new Vector2Int(x, y);
                var walkable = mapResult.IsWalkable(x, y);
                var elevation = EvaluateElevation(coordinates, walkable, farmland, water, shallowWater, forest, coastal);
                var traversal = EvaluateTraversalCost(coordinates, walkable, farmland, water, shallowWater, forest, coastal);

                tiles.Add(new MapTileDefinitionDto
                {
                    coordinates = new SerializableVector2Int(x, y),
                    elevation = elevation,
                    traversalCost = traversal
                });
            }
        }

        return new MapDefinitionDto
        {
            size = new SerializableVector2Int(mapResult.Width, mapResult.Height),
            tileSpacing = 1f,
            minElevation = 0f,
            maxElevation = 1f,
            tiles = tiles.ToArray()
        };
    }

    private static HashSet<Vector2Int> BuildLookup(IReadOnlyList<GridPos> positions)
    {
        var set = new HashSet<Vector2Int>();
        if (positions == null)
        {
            return set;
        }

        for (var i = 0; i < positions.Count; i++)
        {
            var pos = positions[i];
            set.Add(new Vector2Int(pos.x, pos.y));
        }

        return set;
    }

    private static float EvaluateElevation(
        Vector2Int coordinates,
        bool isWalkable,
        HashSet<Vector2Int> farmland,
        HashSet<Vector2Int> water,
        HashSet<Vector2Int> shallowWater,
        HashSet<Vector2Int> forest,
        HashSet<Vector2Int> coastal)
    {
        if (water.Contains(coordinates))
        {
            return 1f;
        }

        if (shallowWater.Contains(coordinates))
        {
            return 0.85f;
        }

        if (coastal.Contains(coordinates))
        {
            return 0.75f;
        }

        if (forest.Contains(coordinates))
        {
            return 0.65f;
        }

        if (farmland.Contains(coordinates))
        {
            return 0.45f;
        }

        if (!isWalkable)
        {
            return 0.55f;
        }

        return 0.35f;
    }

    private static float EvaluateTraversalCost(
        Vector2Int coordinates,
        bool isWalkable,
        HashSet<Vector2Int> farmland,
        HashSet<Vector2Int> water,
        HashSet<Vector2Int> shallowWater,
        HashSet<Vector2Int> forest,
        HashSet<Vector2Int> coastal)
    {
        if (water.Contains(coordinates))
        {
            return 10f;
        }

        if (shallowWater.Contains(coordinates))
        {
            return 7f;
        }

        if (!isWalkable)
        {
            return 6f;
        }

        if (forest.Contains(coordinates))
        {
            return 3.5f;
        }

        if (coastal.Contains(coordinates))
        {
            return 2.5f;
        }

        if (farmland.Contains(coordinates))
        {
            return 1.25f;
        }

        return 1f;
    }

    [Serializable]
    private sealed class MapLoaderSettings
    {
        public TextAsset worldSettingsAsset;
        public TextAsset villageDataAsset;
        public Texture2D mapTexture;

        public bool IsConfigured => worldSettingsAsset != null && villageDataAsset != null && mapTexture != null;
    }

    private void SubscribeToSimulationEvents()
    {
        if (_simulation == null)
        {
            return;
        }

        _simulation.TileGenerated += HandleTileGenerated;
        _simulation.PawnSpawned += HandlePawnSpawned;
        _simulation.PawnUpdated += HandlePawnUpdated;
        _simulation.ItemSpawned += HandleItemSpawned;
    }

    private void UnsubscribeFromSimulationEvents()
    {
        if (_simulation == null)
        {
            return;
        }

        _simulation.TileGenerated -= HandleTileGenerated;
        _simulation.PawnSpawned -= HandlePawnSpawned;
        _simulation.PawnUpdated -= HandlePawnUpdated;
        _simulation.ItemSpawned -= HandleItemSpawned;
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

