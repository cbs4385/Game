using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Globalization;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;

namespace DataDrivenGoap.Demo
namespace DataDrivenGoap
{
    public sealed class MapThingSeed
    /// <summary>
    /// Utility responsible for loading map configuration data without relying on
    /// third-party packages that are not available inside the Unity runtime.
    /// </summary>
    public static class MapLoader
    {
        public string Id { get; }
        public string Type { get; }
        public IReadOnlyList<string> Tags { get; }
        public GridPos Position { get; }
        public IReadOnlyDictionary<string, double> Attributes { get; }
        public BuildingConfig Building { get; }
        public RectInt? Area { get; }
        public IReadOnlyList<GridPos> ServicePoints { get; }
        private const byte DefaultColorTolerance = 2;

        public MapThingSeed(
            string id,
            string type,
            IEnumerable<string> tags,
            GridPos position,
            IDictionary<string, double> attributes,
            BuildingConfig building,
            RectInt? area,
            IEnumerable<GridPos> servicePoints)
        /// <summary>
        /// Attempts to load a <see cref="WorldMapConfig"/> instance from the provided JSON asset.
        /// </summary>
        /// <param name="asset">JSON asset that contains a serialized <see cref="WorldMapConfig"/> payload.</param>
        /// <param name="config">The parsed configuration on success.</param>
        /// <param name="errorMessage">Human readable error information when the operation fails.</param>
        /// <returns>True when the configuration could be parsed, otherwise false.</returns>
        public static bool TryLoadWorldMap(TextAsset asset, out WorldMapConfig config, out string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Id is required", nameof(id));
            Id = id;
            Type = type ?? string.Empty;
            Tags = (tags ?? Array.Empty<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Position = position;
            Attributes = new Dictionary<string, double>(attributes ?? new Dictionary<string, double>(), StringComparer.OrdinalIgnoreCase);
            Building = CloneBuildingConfig(building);
            Area = area;
            ServicePoints = (servicePoints ?? Array.Empty<GridPos>()).ToArray();
            if (asset == null)
            {
                config = WorldMapConfig.CreateEmpty();
                errorMessage = "The JSON asset is null.";
                return false;
            }

        private static BuildingConfig CloneBuildingConfig(BuildingConfig cfg)
        {
            if (cfg == null)
                return null;
            return new BuildingConfig
            {
                area = cfg.area == null
                    ? null
                    : new BuildingAreaConfig { x = cfg.area.x, y = cfg.area.y, width = cfg.area.width, height = cfg.area.height },
                open = cfg.open,
                capacity = cfg.capacity,
                service_points = cfg.service_points == null
                    ? Array.Empty<ServicePointConfig>()
                    : cfg.service_points.Select(sp => sp == null ? null : new ServicePointConfig { x = sp.x, y = sp.y }).ToArray(),
                openHours = cfg.openHours == null
                    ? Array.Empty<BuildingOpenHoursConfig>()
                    : cfg.openHours.Select(oh => oh == null ? null : new BuildingOpenHoursConfig
                    {
                        days = oh.days == null ? Array.Empty<string>() : oh.days.ToArray(),
                        seasons = oh.seasons == null ? Array.Empty<string>() : oh.seasons.ToArray(),
                        open = oh.open,
                        close = oh.close
                    }).ToArray(),
                shop = cfg.shop
            };
            return TryLoadWorldMap(asset.text, out config, out errorMessage);
        }
    }

    public sealed class MapLoaderResult
        /// <summary>
        /// Attempts to load a <see cref="WorldMapConfig"/> instance from the provided JSON payload.
        /// </summary>
        /// <param name="json">JSON payload that describes the world map.</param>
        /// <param name="config">The parsed configuration on success.</param>
        /// <param name="errorMessage">Human readable error information when the operation fails.</param>
        /// <returns>True when the configuration could be parsed, otherwise false.</returns>
        public static bool TryLoadWorldMap(string json, out WorldMapConfig config, out string errorMessage)
        {
        private readonly bool[,] _walkable;

        public int Width { get; }
        public int Height { get; }
        public IReadOnlyList<MapThingSeed> Buildings { get; }
        public IReadOnlyList<GridPos> FarmlandTiles { get; }
        public IReadOnlyList<GridPos> WaterTiles { get; }
        public IReadOnlyList<GridPos> ShallowWaterTiles { get; }
        public IReadOnlyList<GridPos> ForestTiles { get; }
        public IReadOnlyList<GridPos> CoastalTiles { get; }
        public int TileScale { get; }

        public MapLoaderResult(
            int width,
            int height,
            int tileScale,
            bool[,] walkable,
            IEnumerable<MapThingSeed> buildings,
            IEnumerable<GridPos> farmlandTiles,
            IEnumerable<GridPos> waterTiles,
            IEnumerable<GridPos> shallowWaterTiles,
            IEnumerable<GridPos> forestTiles,
            IEnumerable<GridPos> coastalTiles)
            if (string.IsNullOrWhiteSpace(json))
            {
            if (walkable == null)
                throw new ArgumentNullException(nameof(walkable));
            if (walkable.GetLength(0) != width || walkable.GetLength(1) != height)
                throw new ArgumentException("Walkable grid dimensions must match the provided width/height", nameof(walkable));
            if (tileScale <= 0)
                throw new ArgumentOutOfRangeException(nameof(tileScale), "Tile scale must be greater than zero.");
            Width = width;
            Height = height;
            TileScale = tileScale;
            _walkable = (bool[,])walkable.Clone();
            Buildings = (buildings ?? Array.Empty<MapThingSeed>()).ToArray();
            FarmlandTiles = (farmlandTiles ?? Array.Empty<GridPos>()).ToArray();
            WaterTiles = (waterTiles ?? Array.Empty<GridPos>()).ToArray();
            ShallowWaterTiles = (shallowWaterTiles ?? Array.Empty<GridPos>()).ToArray();
            ForestTiles = (forestTiles ?? Array.Empty<GridPos>()).ToArray();
            CoastalTiles = (coastalTiles ?? Array.Empty<GridPos>()).ToArray();
        }

        public bool IsWalkable(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height)
                config = WorldMapConfig.CreateEmpty();
                errorMessage = "The JSON payload is empty.";
                return false;
            return _walkable[x, y];
            }

        public bool[,] CloneWalkable() => (bool[,])_walkable.Clone();
    }

    public static class MapLoader
            try
            {
        public static MapLoaderResult Load(string baseDirectory, WorldMapConfig mapConfig, VillageConfig village = null)
                var wrapper = JsonUtility.FromJson<WorldMapDefinitionWrapper>(json);
                if (wrapper == null)
                {
            if (mapConfig == null)
                throw new ArgumentNullException(nameof(mapConfig));
            if (string.IsNullOrWhiteSpace(baseDirectory))
                baseDirectory = Environment.CurrentDirectory;

            string imagePath = ResolvePath(baseDirectory, mapConfig.image, nameof(mapConfig.image));

            VillageConfig inlineVillage = village;
            if (inlineVillage == null && !string.IsNullOrWhiteSpace(mapConfig.data))
            {
                string dataPath = ResolvePath(baseDirectory, mapConfig.data, nameof(mapConfig.data));
                inlineVillage = ConfigLoader.LoadVillageConfig(dataPath);
                    config = WorldMapConfig.CreateEmpty();
                    errorMessage = "Failed to deserialize the world map definition.";
                    return false;
                }

            VillageMapData inlineMapData = inlineVillage?.map;
            var locationLookup = BuildLocationLookup(inlineVillage?.locations);

            int tileScale = mapConfig.tileSize;
            if (tileScale < 1)
                throw new InvalidOperationException("Map configuration tileSize must be at least 1.");

            Dictionary<Rgba32, string> tileNameByColor;
            if (inlineMapData?.key != null && inlineMapData.key.Count > 0)
            {
                tileNameByColor = LoadTileKey(inlineMapData.key);
                config = wrapper.world ?? WorldMapConfig.CreateEmpty();
                config.ApplyDefaults();
                errorMessage = null;
                return true;
            }
            else
            catch (Exception ex)
            {
                string keyPath = ResolvePath(baseDirectory, mapConfig.key, nameof(mapConfig.key));
                tileNameByColor = LoadTileKey(keyPath);
                config = WorldMapConfig.CreateEmpty();
                errorMessage = ex.Message;
                return false;
            }
            if (tileNameByColor.Count == 0)
                throw new InvalidOperationException("Tile key does not define any entries.");

            using var image = Image.Load<Rgba32>(imagePath);

            if (image.Width % tileScale != 0 || image.Height % tileScale != 0)
                throw new InvalidOperationException($"Image dimensions {image.Width}x{image.Height} are not divisible by tile size {tileScale}.");

            int width = image.Width / tileScale;
            int height = image.Height / tileScale;

            var tileConfig = new Dictionary<string, MapTileConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in mapConfig.tiles ?? new Dictionary<string, MapTileConfig>())
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null)
                    continue;
                tileConfig[kv.Key.Trim()] = kv.Value;
        }

            var walkable = new bool[width, height];
            var farmlandTiles = new List<GridPos>();
            var waterTiles = new List<GridPos>();
            var shallowWaterTiles = new List<GridPos>();
            var forestTiles = new List<GridPos>();
            var coastalTiles = new List<GridPos>();
            bool anyWalkable = false;
            for (int y = 0; y < height; y++)
        /// <summary>
        /// Enumerates all coordinates inside <paramref name="texture"/> that approximately match
        /// <paramref name="targetColor"/>.
        /// </summary>
        /// <param name="texture">Texture that contains encoded map data.</param>
        /// <param name="targetColor">Color that should be matched.</param>
        /// <param name="tolerance">Color tolerance (per channel) that is allowed while matching.</param>
        /// <returns>List of all coordinates that matched the supplied color.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="texture"/> is null.</exception>
        public static IReadOnlyList<GridPos> CollectTilesMatchingColor(
            Texture2D texture,
            Color32 targetColor,
            byte tolerance = DefaultColorTolerance)
        {
                for (int x = 0; x < width; x++)
            if (texture == null)
            {
                    var pixel = image[x * tileScale, y * tileScale];
                    if (tileScale > 1)
                    {
                        for (int oy = 0; oy < tileScale; oy++)
                        {
                            for (int ox = 0; ox < tileScale; ox++)
                            {
                                var sample = image[(x * tileScale) + ox, (y * tileScale) + oy];
                                if (sample != pixel)
                                    throw new InvalidOperationException($"Tile at ({x},{y}) contains multiple colors and cannot be processed with the configured tile size.");
                throw new ArgumentNullException(nameof(texture));
            }
                        }
                    }
                    if (!tileNameByColor.TryGetValue(pixel, out var tileName))
                        throw new InvalidOperationException($"Color {pixel} at {x},{y} not found in tile key.");
                    bool isWalkable = false;
                    bool isFarmland = false;
                    bool isWater = false;
                    bool isShallow = false;
                    bool isForest = false;
                    bool isCoastal = false;
                    if (tileConfig.TryGetValue(tileName, out var cfg))
                    {
                        isWalkable = cfg?.walkable ?? false;
                        isFarmland = cfg?.farmland ?? false;
                        isWater = cfg?.water ?? false;
                        isShallow = cfg?.shallowWater ?? false;
                        isForest = cfg?.forest ?? false;
                        isCoastal = cfg?.coastal ?? false;
                    }
                    walkable[x, y] = isWalkable;
                    if (isWalkable)
                        anyWalkable = true;
                    if (isFarmland)
                        farmlandTiles.Add(new GridPos(x, y));
                    if (isWater)
                        waterTiles.Add(new GridPos(x, y));
                    if (isShallow)
                        shallowWaterTiles.Add(new GridPos(x, y));
                    if (isForest)
                        forestTiles.Add(new GridPos(x, y));
                    if (isCoastal)
                        coastalTiles.Add(new GridPos(x, y));
                }
            }

            if (!anyWalkable)
                throw new InvalidOperationException("Loaded map does not contain any walkable tiles.");
            var result = new List<GridPos>();
            var pixels = texture.GetPixels32();

            var buildingPrototypes = new Dictionary<string, MapBuildingPrototypeConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in mapConfig.buildingPrototypes ?? new Dictionary<string, MapBuildingPrototypeConfig>())
            for (var y = 0; y < texture.height; y++)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null)
                    continue;
                buildingPrototypes[kv.Key.Trim()] = kv.Value;
            }

            IReadOnlyList<MapThingSeed> buildings;
            if (inlineMapData?.annotations?.buildings != null && inlineMapData.annotations.buildings.Length > 0)
                for (var x = 0; x < texture.width; x++)
                {
                buildings = LoadBuildings(inlineMapData.annotations.buildings, buildingPrototypes, tileScale, width, height, locationLookup).ToArray();
            }
            else
                    var pixel = pixels[y * texture.width + x];
                    if (ApproximatelyEqual(pixel, targetColor, tolerance))
                    {
                string annotationsPath = ResolvePath(baseDirectory, mapConfig.annotations, nameof(mapConfig.annotations));
                buildings = LoadBuildings(annotationsPath, buildingPrototypes, tileScale, width, height, locationLookup).ToArray();
                        result.Add(new GridPos(x, y));
                    }

            foreach (var building in buildings)
            {
                if (building?.Area is RectInt area && !area.IsEmpty)
                {
                    var door = FindDoorLocation(area, building.ServicePoints, walkable);
                    if (door.HasValue)
                    {
                        var pos = door.Value;
                        if (pos.X >= 0 && pos.Y >= 0 && pos.X < width && pos.Y < height)
                            walkable[pos.X, pos.Y] = true;
                }
            }
            }

            return new MapLoaderResult(width, height, tileScale, walkable, buildings, farmlandTiles, waterTiles, shallowWaterTiles, forestTiles, coastalTiles);
            return result;
        }

        private static string ResolvePath(string baseDirectory, string relativePath, string propertyName)
        /// <summary>
        /// Checks whether two colors are approximately equal by comparing their channels against a tolerance.
        /// </summary>
        public static bool ApproximatelyEqual(Color32 lhs, Color32 rhs, byte tolerance = DefaultColorTolerance)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new InvalidOperationException($"Map configuration must include a value for '{propertyName}'.");
            var path = Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(baseDirectory, relativePath);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Unable to locate map resource for '{propertyName}'", path);
            return path;
            return Math.Abs(lhs.r - rhs.r) <= tolerance
                && Math.Abs(lhs.g - rhs.g) <= tolerance
                && Math.Abs(lhs.b - rhs.b) <= tolerance
                && Math.Abs(lhs.a - rhs.a) <= tolerance;
        }

        private static Dictionary<Rgba32, string> LoadTileKey(string keyPath)
        [Serializable]
        private sealed class WorldMapDefinitionWrapper
        {
            using var fs = File.OpenRead(keyPath);
            using var doc = JsonDocument.Parse(fs);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var colorText = prop.Value.GetString();
                if (string.IsNullOrWhiteSpace(colorText))
                    continue;
                map[prop.Name] = colorText.Trim();
            public WorldMapConfig world;
        }
            return LoadTileKey(map);
    }

        private static Dictionary<Rgba32, string> LoadTileKey(IDictionary<string, string> entries)
    [Serializable]
    public struct GridPos
    {
            var map = new Dictionary<Rgba32, string>();
            if (entries == null)
                return map;

            foreach (var kv in entries)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
                    continue;
                var color = ParseColor(kv.Value.Trim());
                map[color] = kv.Key.Trim();
            }

            return map;
        }

        private static IReadOnlyDictionary<string, VillageLocation> BuildLocationLookup(IDictionary<string, VillageLocation> locations)
        {
            if (locations == null || locations.Count == 0)
                throw new InvalidOperationException("Village data must define a non-empty set of locations.");

            var map = new Dictionary<string, VillageLocation>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in locations)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                    throw new InvalidOperationException("Village location entries must use non-empty keys.");
                if (kv.Value == null)
                    throw new InvalidOperationException($"Village location '{kv.Key}' is missing its configuration block.");

                var normalized = kv.Key.Trim();
                var clone = CloneLocation(kv.Value, normalized);
                map[normalized] = clone;
        public int x;
        public int y;

                if (!string.IsNullOrWhiteSpace(clone.id))
        public GridPos(int x, int y)
        {
                    var idKey = clone.id.Trim();
                    map[idKey] = clone;
            this.x = x;
            this.y = y;
        }

                if (!string.IsNullOrWhiteSpace(clone.name))
                {
                    var nameKey = clone.name.Trim();
                    if (!map.ContainsKey(nameKey))
                        map[nameKey] = clone;
                }
            }
        public Vector2Int ToVector2Int() => new Vector2Int(x, y);

            return map;
        public override string ToString() => $"({x}, {y})";
    }

        private static VillageLocation CloneLocation(VillageLocation src, string fallbackId)
    [Serializable]
    public sealed class BuildingConfig
    {
            if (src == null)
                throw new InvalidOperationException($"Village location '{fallbackId}' must not be null.");

            if (string.IsNullOrWhiteSpace(src.id))
                throw new InvalidOperationException($"Village location '{fallbackId}' must specify an 'id'.");

            if (src.bbox == null || src.bbox.Length != 4)
                throw new InvalidOperationException($"Village location '{src.id.Trim()}' must specify a 'bbox' with four coordinates.");

            if (src.center == null || src.center.Length < 2)
                throw new InvalidOperationException($"Village location '{src.id.Trim()}' must specify a 'center' with two coordinates.");
        public string id;
        public string prototypeId;
        public GridPos position;
        public float rotation;

            return new VillageLocation
        public void ApplyDefaults()
        {
                id = src.id.Trim(),
                name = string.IsNullOrWhiteSpace(src.name) ? null : src.name.Trim(),
                type = string.IsNullOrWhiteSpace(src.type) ? null : src.type.Trim(),
                bbox = src.bbox.ToArray(),
                center = src.center.ToArray()
            };
            id ??= string.Empty;
            prototypeId ??= string.Empty;
        }
    }

        private static IEnumerable<MapThingSeed> LoadBuildings(
            string annotationsPath,
            IDictionary<string, MapBuildingPrototypeConfig> prototypes,
            int tileScale,
            int width,
            int height,
            IReadOnlyDictionary<string, VillageLocation> locations)
        {
            using var fs = File.OpenRead(annotationsPath);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("buildings", out var buildingsElement))
                throw new InvalidOperationException("Map annotations must include a 'buildings' array.");
            if (buildingsElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Map annotations 'buildings' element must be an array.");

            var annotations = new List<VillageBuildingAnnotation>();
            foreach (var element in buildingsElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Each building annotation must be a JSON object.");
                if (!element.TryGetProperty("name", out var nameProp))
                    throw new InvalidOperationException("Building annotations must define a 'name'.");
                var name = nameProp.GetString();
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidOperationException("Building annotation names must be non-empty.");

                double[] bbox = null;
                if (element.TryGetProperty("bbox", out var bboxProp))
    [Serializable]
    public sealed class MapBuildingPrototypeConfig
    {
                    if (bboxProp.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException($"Building annotation '{name.Trim()}' must provide a 'bbox' array when specified.");
                    bbox = bboxProp.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                }

                string locationRef = null;
                if (element.TryGetProperty("location", out var locationProp))
                    locationRef = locationProp.GetString();
        public string id;
        public string displayName;
        public string spriteId;
        public Color32 color;
        public bool allowRotation = true;

                var annotation = new VillageBuildingAnnotation
        public void ApplyDefaults()
        {
                    name = name.Trim(),
                    bbox = bbox,
                    location = string.IsNullOrWhiteSpace(locationRef) ? null : locationRef.Trim()
                };
                annotations.Add(annotation);
            id ??= string.Empty;
            displayName ??= id;
            spriteId ??= string.Empty;
        }

            return LoadBuildings(annotations, prototypes, tileScale, width, height, locations);
    }

        private static IEnumerable<MapThingSeed> LoadBuildings(
            IEnumerable<VillageBuildingAnnotation> annotations,
            IDictionary<string, MapBuildingPrototypeConfig> prototypes,
            int tileScale,
            int width,
            int height,
            IReadOnlyDictionary<string, VillageLocation> locations)
    [Serializable]
    public sealed class VillageLocation
    {
            if (annotations == null)
                throw new InvalidOperationException("Building annotations must be provided.");

            if (tileScale <= 0)
                throw new InvalidOperationException("Tile scale must be greater than zero when constructing buildings.");
        public string id;
        public string displayName;
        public GridPos center;
        public int radius;

            var result = new List<MapThingSeed>();
            var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var annotation in annotations)
        public void ApplyDefaults()
        {
                if (annotation == null)
                    throw new InvalidOperationException("Building annotations must not contain null entries.");

                string name = annotation.name;
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidOperationException("Building annotations must define a non-empty name.");

                if (!prototypes.TryGetValue(name, out var prototype) || prototype == null)
                    throw new InvalidOperationException($"No building prototype defined for annotation '{name}'.");

                var bbox = ResolveBoundingBox(annotation, locations);
                if (bbox == null || bbox.Length != 4)
                    throw new InvalidOperationException($"Building '{name}' must define a bounding box with four coordinates.");

                double minPx = bbox[0];
                double minPy = bbox[1];
                double maxPx = bbox[2];
                double maxPy = bbox[3];
                if (maxPx < minPx || maxPy < minPy)
                    throw new InvalidOperationException($"Building '{name}' bounding box is invalid.");

                int minX = Math.Clamp((int)Math.Floor(minPx / tileScale), 0, width - 1);
                int minY = Math.Clamp((int)Math.Floor(minPy / tileScale), 0, height - 1);
                int maxX = Math.Clamp((int)Math.Ceiling(maxPx / tileScale) - 1, 0, width - 1);
                int maxY = Math.Clamp((int)Math.Ceiling(maxPy / tileScale) - 1, 0, height - 1);
                if (maxX < minX) maxX = minX;
                if (maxY < minY) maxY = minY;

                var area = new RectInt(minX, minY, maxX, maxY);
                var pos = new GridPos((minX + maxX) / 2, (minY + maxY) / 2);

                var idPrefix = string.IsNullOrWhiteSpace(prototype.idPrefix) ? name : prototype.idPrefix;
                if (!counters.TryGetValue(idPrefix, out var count)) count = 0;
                count++;
                counters[idPrefix] = count;
                string id = $"{idPrefix}-{count}";

                var tags = (prototype.tags ?? Array.Empty<string>())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var attributes = new Dictionary<string, double>(prototype.attributes ?? new Dictionary<string, double>(), StringComparer.OrdinalIgnoreCase);

                var servicePoints = BuildServicePoints(prototype.servicePoints, area);

                var seed = new MapThingSeed(
                    id,
                    prototype.type,
                    tags,
                    pos,
                    attributes,
                    prototype.building,
                    area,
                    servicePoints);
                result.Add(seed);
            id ??= string.Empty;
            displayName ??= id;
            radius = Math.Max(0, radius);
        }

            return result;
    }

        private static double[] ResolveBoundingBox(VillageBuildingAnnotation annotation, IReadOnlyDictionary<string, VillageLocation> locations)
    [Serializable]
    public sealed class VillageBuildingAnnotation
    {
            if (annotation == null)
                throw new InvalidOperationException("Building annotations must not be null when resolving bounding boxes.");
        public string villageId;
        public string buildingId;
        public GridPos position;

            if (annotation.bbox != null)
        public void ApplyDefaults()
        {
                if (annotation.bbox.Length != 4)
                    throw new InvalidOperationException($"Building '{annotation.name}' must define a 'bbox' with four coordinates when provided explicitly.");
                return annotation.bbox.ToArray();
            villageId ??= string.Empty;
            buildingId ??= string.Empty;
        }

            if (string.IsNullOrWhiteSpace(annotation.location))
                throw new InvalidOperationException($"Building '{annotation.name}' must define either a 'bbox' or a 'location' reference.");
            if (locations == null || locations.Count == 0)
                throw new InvalidOperationException($"No village locations are available to resolve building '{annotation.name}'.");
            if (!locations.TryGetValue(annotation.location.Trim(), out var loc) || loc?.bbox == null || loc.bbox.Length != 4)
                throw new InvalidOperationException($"Building '{annotation.name}' references unknown location '{annotation.location}'.");

            return loc.bbox.ToArray();
    }

        private static IReadOnlyList<GridPos> BuildServicePoints(MapServicePointConfig[] configs, RectInt area)
    [Serializable]
    public sealed class MapServicePointConfig
    {
            if (configs == null || configs.Length == 0)
                return Array.Empty<GridPos>();
        public string id;
        public string displayName;
        public string serviceType;
        public GridPos position;

            var points = new List<GridPos>();
            foreach (var cfg in configs)
        public void ApplyDefaults()
        {
                if (cfg == null)
                    continue;
                double rx = cfg.x ?? 0.5;
                double ry = cfg.y ?? 0.5;
                rx = Math.Clamp(rx, 0.0, 1.0);
                ry = Math.Clamp(ry, 0.0, 1.0);
                int x = area.MinX + (int)Math.Round(rx * (area.MaxX - area.MinX));
                int y = area.MinY + (int)Math.Round(ry * (area.MaxY - area.MinY));
                x = Math.Clamp(x, area.MinX, area.MaxX);
                y = Math.Clamp(y, area.MinY, area.MaxY);
                points.Add(new GridPos(x, y));
            id ??= string.Empty;
            displayName ??= id;
            serviceType ??= string.Empty;
        }

            return points;
    }

        private static GridPos? FindDoorLocation(RectInt area, IReadOnlyList<GridPos> servicePoints, bool[,] walkable)
        {
            if (walkable == null)
                return null;

            int width = walkable.GetLength(0);
            int height = walkable.GetLength(1);

            bool IsInsideMap(GridPos p) => p.X >= 0 && p.Y >= 0 && p.X < width && p.Y < height;
            bool IsOnPerimeter(GridPos p)
    [Serializable]
    public sealed class VillageConfig
    {
                if (!area.Contains(p))
                    return false;
                return p.X == area.MinX || p.X == area.MaxX || p.Y == area.MinY || p.Y == area.MaxY;
            }
        public string id;
        public string displayName;
        public VillageLocation location = new VillageLocation();
        public BuildingConfig[] buildings = Array.Empty<BuildingConfig>();
        public MapServicePointConfig[] servicePoints = Array.Empty<MapServicePointConfig>();
        public VillageBuildingAnnotation[] annotations = Array.Empty<VillageBuildingAnnotation>();

            bool HasWalkableNeighborOutside(GridPos p)
            {
                ReadOnlySpan<GridPos> dirs = stackalloc GridPos[4]
        public void ApplyDefaults()
        {
                    new GridPos(1, 0),
                    new GridPos(-1, 0),
                    new GridPos(0, 1),
                    new GridPos(0, -1)
                };
            id ??= string.Empty;
            displayName ??= id;
            location ??= new VillageLocation();
            location.ApplyDefaults();

                foreach (var dir in dirs)
            buildings ??= Array.Empty<BuildingConfig>();
            foreach (var building in buildings)
            {
                    var neighbor = new GridPos(p.X + dir.X, p.Y + dir.Y);
                    if (!IsInsideMap(neighbor))
                        continue;
                    if (area.Contains(neighbor))
                        continue;
                    if (walkable[neighbor.X, neighbor.Y])
                        return true;
                }

                return false;
                building?.ApplyDefaults();
            }

            GridPos? fallback = null;

            if (servicePoints != null)
            {
                foreach (var sp in servicePoints)
            servicePoints ??= Array.Empty<MapServicePointConfig>();
            foreach (var servicePoint in servicePoints)
            {
                    if (!IsOnPerimeter(sp) || !IsInsideMap(sp))
                        continue;
                    if (HasWalkableNeighborOutside(sp))
                        return sp;
                    if (!fallback.HasValue)
                        fallback = sp;
                }
                servicePoint?.ApplyDefaults();
            }

            foreach (var candidate in EnumeratePerimeter(area))
            annotations ??= Array.Empty<VillageBuildingAnnotation>();
            foreach (var annotation in annotations)
            {
                if (!IsInsideMap(candidate))
                    continue;
                if (HasWalkableNeighborOutside(candidate))
                    return candidate;
                if (!fallback.HasValue)
                    fallback = candidate;
                annotation?.ApplyDefaults();
            }
        }

            if (fallback.HasValue)
                return fallback;

            int midX = area.MinX + (area.MaxX - area.MinX) / 2;
            var door = new GridPos(midX, area.MaxY);
            return IsInsideMap(door) ? door : (GridPos?)null;
    }

        private static IEnumerable<GridPos> EnumeratePerimeter(RectInt area)
    [Serializable]
    public sealed class WorldMapConfig
    {
            if (area.IsEmpty)
                yield break;
        public GridPos mapSize = new GridPos(32, 32);
        public float tileSpacing = 1f;
        public VillageConfig[] villages = Array.Empty<VillageConfig>();
        public MapBuildingPrototypeConfig[] buildingPrototypes = Array.Empty<MapBuildingPrototypeConfig>();

            for (int x = area.MinX; x <= area.MaxX; x++)
        public static WorldMapConfig CreateEmpty()
        {
                yield return new GridPos(x, area.MinY);
                if (area.MaxY != area.MinY)
                    yield return new GridPos(x, area.MaxY);
            return new WorldMapConfig();
        }

            if (area.MinX == area.MaxX)
                yield break;

            for (int y = area.MinY + 1; y < area.MaxY; y++)
        public void ApplyDefaults()
        {
                yield return new GridPos(area.MinX, y);
                yield return new GridPos(area.MaxX, y);
            }
        }
            tileSpacing = Mathf.Max(0.01f, tileSpacing);

        private static Rgba32 ParseColor(string hex)
            villages ??= Array.Empty<VillageConfig>();
            foreach (var village in villages)
            {
            if (hex.StartsWith("#", StringComparison.Ordinal))
                hex = hex[1..];
            if (hex.Length != 6)
                throw new InvalidOperationException($"Color '{hex}' must be a 6-character hex code.");
                village?.ApplyDefaults();
            }

            if (!byte.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
                !byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
                !byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            buildingPrototypes ??= Array.Empty<MapBuildingPrototypeConfig>();
            foreach (var prototype in buildingPrototypes)
            {
                throw new InvalidOperationException($"Color '{hex}' contains invalid hex characters.");
                prototype?.ApplyDefaults();
            }

            return new Rgba32(r, g, b, 255);
        }
    }
}
