
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace DataDrivenGoap
{
    /// <summary>
    /// Immutable description of an object that should be spawned on the generated map.
    /// </summary>
    public sealed class MapThingSeed
    {
        public MapThingSeed(
            string id,
            string type,
            IEnumerable<string> tags,
            GridPos position,
            IDictionary<string, double> attributes,
            BuildingConfig building,
            RectInt? area,
            IEnumerable<GridPos> servicePoints)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Id is required", nameof(id));
            }

            Id = id.Trim();
            Type = type ?? string.Empty;
            Tags = (tags ?? Array.Empty<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Position = position;
            Attributes = new Dictionary<string, double>(attributes ?? new Dictionary<string, double>(), StringComparer.OrdinalIgnoreCase);
            Building = building == null ? null : BuildingConfig.Clone(building);
            Area = area;
            ServicePoints = (servicePoints ?? Array.Empty<GridPos>()).ToArray();
        }

        public string Id { get; }

        public string Type { get; }

        public IReadOnlyList<string> Tags { get; }

        public GridPos Position { get; }

        public IReadOnlyDictionary<string, double> Attributes { get; }

        public BuildingConfig Building { get; }

        public RectInt? Area { get; }

        public IReadOnlyList<GridPos> ServicePoints { get; }
    }

    /// <summary>
    /// Result of loading a map from a texture.
    /// </summary>
    public sealed class MapLoaderResult
    {
        private readonly bool[,] _walkable;

        public MapLoaderResult(
            int width,
            int height,
            int tileScale,
            bool[,] walkable,
            IEnumerable<MapThingSeed> buildings,
            IEnumerable<GridPos> farmland,
            IEnumerable<GridPos> water,
            IEnumerable<GridPos> shallowWater,
            IEnumerable<GridPos> forest,
            IEnumerable<GridPos> coastal)
        {
            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            if (height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }

            if (tileScale <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tileScale));
            }

            if (walkable == null)
            {
                throw new ArgumentNullException(nameof(walkable));
            }

            if (walkable.GetLength(0) != width || walkable.GetLength(1) != height)
            {
                throw new ArgumentException("Walkable grid dimensions must match the provided width/height", nameof(walkable));
            }

            Width = width;
            Height = height;
            TileScale = tileScale;
            _walkable = (bool[,])walkable.Clone();
            Buildings = (buildings ?? Array.Empty<MapThingSeed>()).ToArray();
            FarmlandTiles = (farmland ?? Array.Empty<GridPos>()).ToArray();
            WaterTiles = (water ?? Array.Empty<GridPos>()).ToArray();
            ShallowWaterTiles = (shallowWater ?? Array.Empty<GridPos>()).ToArray();
            ForestTiles = (forest ?? Array.Empty<GridPos>()).ToArray();
            CoastalTiles = (coastal ?? Array.Empty<GridPos>()).ToArray();
        }

        public int Width { get; }

        public int Height { get; }

        public int TileScale { get; }

        public IReadOnlyList<MapThingSeed> Buildings { get; }

        public IReadOnlyList<GridPos> FarmlandTiles { get; }

        public IReadOnlyList<GridPos> WaterTiles { get; }

        public IReadOnlyList<GridPos> ShallowWaterTiles { get; }

        public IReadOnlyList<GridPos> ForestTiles { get; }

        public IReadOnlyList<GridPos> CoastalTiles { get; }

        public bool IsWalkable(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height)
            {
                return false;
            }

            return _walkable[x, y];
        }

        public bool[,] CloneWalkable() => (bool[,])_walkable.Clone();
    }

    /// <summary>
    /// Utility responsible for loading map configuration data from JSON and texture files.
    /// </summary>
    public static class MapLoader
    {
        private const byte DefaultColorTolerance = 2;

        public static bool TryLoadWorldMap(TextAsset asset, out WorldMapConfig config, out string errorMessage)
        {
            if (asset == null)
            {
                config = WorldMapConfig.CreateEmpty();
                errorMessage = "The JSON asset is null.";
                return false;
            }

            return TryLoadWorldMap(asset.text, out config, out errorMessage);
        }

        public static bool TryLoadWorldMap(string json, out WorldMapConfig config, out string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                config = WorldMapConfig.CreateEmpty();
                errorMessage = "The JSON payload is empty.";
                return false;
            }

            try
            {
                var wrapper = JsonUtilities.Deserialize<WorldMapDefinitionWrapper>(json);
                if (wrapper?.world == null)
                {
                    config = WorldMapConfig.CreateEmpty();
                    errorMessage = "Failed to deserialize the world map definition.";
                    return false;
                }

                config = wrapper.world;
                config.ApplyDefaults();
                errorMessage = null;
                return true;
            }
            catch (Exception ex)
            {
                config = WorldMapConfig.CreateEmpty();
                errorMessage = ex.Message;
                return false;
            }
        }

        public static MapLoaderResult Load(string baseDirectory, WorldMapConfig mapConfig, VillageConfig village = null)
        {
            if (mapConfig == null)
            {
                throw new ArgumentNullException(nameof(mapConfig));
            }

            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                baseDirectory = Environment.CurrentDirectory;
            }

            mapConfig.ApplyDefaults();

            string imagePath = ResolvePath(baseDirectory, mapConfig.image, nameof(mapConfig.image));

            VillageConfig inlineVillage = village;
            if (inlineVillage == null && !string.IsNullOrWhiteSpace(mapConfig.data))
            {
                string dataPath = ResolvePath(baseDirectory, mapConfig.data, nameof(mapConfig.data));
                inlineVillage = LoadVillageConfig(dataPath);
            }

            VillageMapData inlineMapData = inlineVillage?.map;
            var locationLookup = BuildLocationLookup(inlineVillage?.locations);

            int tileScale = Math.Max(1, mapConfig.tileSize);

            Dictionary<Color32Key, string> tileNameByColor;
            if (inlineMapData?.key != null && inlineMapData.key.Count > 0)
            {
                tileNameByColor = LoadTileKey(inlineMapData.key);
            }
            else
            {
                string keyPath = ResolvePath(baseDirectory, mapConfig.key, nameof(mapConfig.key));
                tileNameByColor = LoadTileKey(keyPath);
            }

            if (tileNameByColor.Count == 0)
            {
                throw new InvalidOperationException("Tile key does not define any entries.");
            }

            var texture = LoadTexture(imagePath);
            try
            {
                if (texture.width % tileScale != 0 || texture.height % tileScale != 0)
                {
                    throw new InvalidOperationException($"Image dimensions {texture.width}x{texture.height} are not divisible by tile size {tileScale}.");
                }

                int width = texture.width / tileScale;
                int height = texture.height / tileScale;

                var pixels = texture.GetPixels32();

                var tileConfig = new Dictionary<string, MapTileConfig>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in mapConfig.tiles ?? new Dictionary<string, MapTileConfig>())
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null)
                    {
                        continue;
                    }

                    tileConfig[kvp.Key.Trim()] = kvp.Value;
                }

                var walkable = new bool[width, height];
                var farmlandTiles = new List<GridPos>();
                var waterTiles = new List<GridPos>();
                var shallowWaterTiles = new List<GridPos>();
                var forestTiles = new List<GridPos>();
                var coastalTiles = new List<GridPos>();
                bool anyWalkable = false;

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        var pixel = GetPixel(pixels, texture.width, x * tileScale, y * tileScale);
                        if (tileScale > 1)
                        {
                            for (int oy = 0; oy < tileScale; oy++)
                            {
                                for (int ox = 0; ox < tileScale; ox++)
                                {
                                    var sample = GetPixel(pixels, texture.width, (x * tileScale) + ox, (y * tileScale) + oy);
                                    if (!ApproximatelyEqual(sample, pixel, 0))
                                    {
                                        throw new InvalidOperationException($"Tile at ({x},{y}) contains multiple colors and cannot be processed with the configured tile size.");
                                    }
                                }
                            }
                        }

                        var pixelKey = new Color32Key(pixel);
                        if (!tileNameByColor.TryGetValue(pixelKey, out var tileName))
                        {
                            throw new InvalidOperationException($"Color {ColorToString(pixel)} at {x},{y} not found in tile key.");
                        }

                        MapTileConfig cfg = null;
                        tileConfig.TryGetValue(tileName, out cfg);

                        bool isWalkable = cfg?.walkable ?? false;
                        bool isFarmland = cfg?.farmland ?? false;
                        bool isWater = cfg?.water ?? false;
                        bool isShallow = cfg?.shallowWater ?? false;
                        bool isForest = cfg?.forest ?? false;
                        bool isCoastal = cfg?.coastal ?? false;

                        walkable[x, y] = isWalkable;
                        if (isWalkable)
                        {
                            anyWalkable = true;
                        }

                        if (isFarmland)
                        {
                            farmlandTiles.Add(new GridPos(x, y));
                        }

                        if (isWater)
                        {
                            waterTiles.Add(new GridPos(x, y));
                        }

                        if (isShallow)
                        {
                            shallowWaterTiles.Add(new GridPos(x, y));
                        }

                        if (isForest)
                        {
                            forestTiles.Add(new GridPos(x, y));
                        }

                        if (isCoastal)
                        {
                            coastalTiles.Add(new GridPos(x, y));
                        }
                    }
                }

                if (!anyWalkable)
                {
                    throw new InvalidOperationException("Loaded map does not contain any walkable tiles.");
                }

                var buildingPrototypes = new Dictionary<string, MapBuildingPrototypeConfig>(StringComparer.OrdinalIgnoreCase);
                if (mapConfig.buildingPrototypes != null)
                {
                    foreach (var kvp in mapConfig.buildingPrototypes)
                    {
                        if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null)
                        {
                            continue;
                        }

                        kvp.Value.ApplyDefaults();
                        buildingPrototypes[kvp.Key.Trim()] = kvp.Value;
                    }
                }

                IReadOnlyList<VillageBuildingAnnotation> annotations;
                if (inlineMapData?.annotations?.buildings != null && inlineMapData.annotations.buildings.Length > 0)
                {
                    annotations = inlineMapData.annotations.buildings;
                }
                else if (!string.IsNullOrWhiteSpace(mapConfig.annotations))
                {
                    string annotationsPath = ResolvePath(baseDirectory, mapConfig.annotations, nameof(mapConfig.annotations));
                    annotations = LoadAnnotations(annotationsPath);
                }
                else
                {
                    annotations = Array.Empty<VillageBuildingAnnotation>();
                }

                var buildings = LoadBuildings(
                    annotations,
                    buildingPrototypes,
                    tileScale,
                    width,
                    height,
                    locationLookup).ToArray();

                return new MapLoaderResult(
                    width,
                    height,
                    tileScale,
                    walkable,
                    buildings,
                    farmlandTiles,
                    waterTiles,
                    shallowWaterTiles,
                    forestTiles,
                    coastalTiles);
            }
            finally
            {
                DestroyTexture(texture);
            }
        }

        public static IReadOnlyList<GridPos> CollectTilesMatchingColor(
            Texture2D texture,
            Color32 targetColor,
            byte tolerance = DefaultColorTolerance)
        {
            if (texture == null)
            {
                throw new ArgumentNullException(nameof(texture));
            }

            var result = new List<GridPos>();
            var pixels = texture.GetPixels32();

            for (var y = 0; y < texture.height; y++)
            {
                for (var x = 0; x < texture.width; x++)
                {
                    var pixel = pixels[y * texture.width + x];
                    if (ApproximatelyEqual(pixel, targetColor, tolerance))
                    {
                        result.Add(new GridPos(x, y));
                    }
                }
            }

            return result;
        }

        public static bool ApproximatelyEqual(Color32 lhs, Color32 rhs, byte tolerance = DefaultColorTolerance)
        {
            return Math.Abs(lhs.r - rhs.r) <= tolerance
                && Math.Abs(lhs.g - rhs.g) <= tolerance
                && Math.Abs(lhs.b - rhs.b) <= tolerance
                && Math.Abs(lhs.a - rhs.a) <= tolerance;
        }

        private static Texture2D LoadTexture(string path)
        {
            var data = File.ReadAllBytes(path);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(texture, data, false))
            {
                DestroyTexture(texture);
                throw new InvalidOperationException($"Failed to load image '{path}'.");
            }

            return texture;
        }

        private static Color32 GetPixel(Color32[] pixels, int width, int x, int y)
        {
            return pixels[(y * width) + x];
        }

        private static string ColorToString(Color32 color)
        {
            return $"#{ColorUtility.ToHtmlStringRGBA(color)}";
        }

        private static void DestroyTexture(Texture2D texture)
        {
            if (texture == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(texture);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private readonly struct Color32Key : IEquatable<Color32Key>
        {
            public readonly byte R;
            public readonly byte G;
            public readonly byte B;
            public readonly byte A;

            public Color32Key(Color32 color)
            {
                R = color.r;
                G = color.g;
                B = color.b;
                A = color.a;
            }

            public Color32Key(byte r, byte g, byte b, byte a)
            {
                R = r;
                G = g;
                B = b;
                A = a;
            }

            public bool Equals(Color32Key other)
            {
                return R == other.R && G == other.G && B == other.B && A == other.A;
            }

            public override bool Equals(object obj)
            {
                return obj is Color32Key other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(R, G, B, A);
            }

            public override string ToString()
            {
                return $"#{R:X2}{G:X2}{B:X2}{A:X2}";
            }
        }

        private static string ResolvePath(string baseDirectory, string relativePath, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new InvalidOperationException($"Map configuration must include a value for '{propertyName}'.");
            }

            var path = Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(baseDirectory, relativePath);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Unable to locate map resource for '{propertyName}'", path);
            }

            return path;
        }

        private static Dictionary<Color32Key, string> LoadTileKey(string keyPath)
        {
            var json = File.ReadAllText(keyPath);
            var entries = JsonUtilities.ParseStringDictionary(json);
            return LoadTileKey(entries);
        }

        private static Dictionary<Color32Key, string> LoadTileKey(IDictionary<string, string> entries)
        {
            var map = new Dictionary<Color32Key, string>();
            if (entries == null)
            {
                return map;
            }

            foreach (var kv in entries)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
                {
                    continue;
                }

                var color = ParseColor(kv.Value.Trim());
                map[color] = kv.Key.Trim();
            }

            return map;
        }

        private static IReadOnlyDictionary<string, VillageLocation> BuildLocationLookup(IDictionary<string, VillageLocation> locations)
        {
            if (locations == null || locations.Count == 0)
            {
                return new Dictionary<string, VillageLocation>(StringComparer.OrdinalIgnoreCase);
            }

            var map = new Dictionary<string, VillageLocation>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in locations)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null)
                {
                    continue;
                }

                var normalized = kv.Key.Trim();
                var clone = VillageLocation.Clone(kv.Value, normalized);
                map[normalized] = clone;

                if (!string.IsNullOrWhiteSpace(clone.id))
                {
                    map[clone.id.Trim()] = clone;
                }

                if (!string.IsNullOrWhiteSpace(clone.name) && !map.ContainsKey(clone.name.Trim()))
                {
                    map[clone.name.Trim()] = clone;
                }
            }

            return map;
        }

        private static IEnumerable<MapThingSeed> LoadBuildings(
            IEnumerable<VillageBuildingAnnotation> annotations,
            IDictionary<string, MapBuildingPrototypeConfig> prototypes,
            int tileScale,
            int width,
            int height,
            IReadOnlyDictionary<string, VillageLocation> locations)
        {
            if (annotations == null)
            {
                return Array.Empty<MapThingSeed>();
            }

            var result = new List<MapThingSeed>();
            var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var annotation in annotations)
            {
                if (annotation == null || string.IsNullOrWhiteSpace(annotation.name))
                {
                    continue;
                }

                if (!prototypes.TryGetValue(annotation.name.Trim(), out var prototype))
                {
                    continue;
                }

                var bbox = ResolveBoundingBox(annotation, locations);
                if (bbox == null || bbox.Length != 4)
                {
                    continue;
                }

                var area = NormalizeBoundingBox(bbox, tileScale, width, height);
                if (area.width <= 0 || area.height <= 0)
                {
                    continue;
                }

                var idPrefix = string.IsNullOrWhiteSpace(prototype.idPrefix) ? prototype.type ?? annotation.name : prototype.idPrefix.Trim();
                counters.TryGetValue(idPrefix, out var index);
                index++;
                counters[idPrefix] = index;
                var id = $"{idPrefix}-{index:00}";

                var tags = new List<string>();
                if (prototype.tags != null)
                {
                    tags.AddRange(prototype.tags.Where(t => !string.IsNullOrWhiteSpace(t)));
                }
                if (annotation.tags != null)
                {
                    tags.AddRange(annotation.tags.Where(t => !string.IsNullOrWhiteSpace(t)));
                }

                var attributes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                if (annotation.attributes != null)
                {
                    foreach (var kv in annotation.attributes)
                    {
                        attributes[kv.Key] = kv.Value;
                    }
                }

                var buildingConfig = prototype.building == null ? null : BuildingConfig.Clone(prototype.building);
                if (buildingConfig != null)
                {
                    buildingConfig.id = id;
                    buildingConfig.prototypeId = annotation.name?.Trim() ?? string.Empty;
                    buildingConfig.position = new GridPos(area.x + area.width / 2, area.y + area.height / 2);
                    buildingConfig.rotation = annotation.rotation;
                    buildingConfig.area = new BuildingAreaConfig
                    {
                        x = area.x,
                        y = area.y,
                        width = area.width,
                        height = area.height
                    };
                }

                var servicePoints = BuildServicePoints(prototype.servicePoints, area);

                var seed = new MapThingSeed(
                    id,
                    prototype.type ?? annotation.name,
                    tags,
                    new GridPos(area.x + area.width / 2, area.y + area.height / 2),
                    attributes,
                    buildingConfig,
                    area,
                    servicePoints);

                result.Add(seed);
            }

            return result;
        }

        private static double[] ResolveBoundingBox(VillageBuildingAnnotation annotation, IReadOnlyDictionary<string, VillageLocation> locations)
        {
            if (annotation?.bbox != null && annotation.bbox.Length == 4)
            {
                return annotation.bbox;
            }

            if (!string.IsNullOrWhiteSpace(annotation?.location) && locations != null && locations.TryGetValue(annotation.location.Trim(), out var loc))
            {
                return loc?.bbox;
            }

            return null;
        }

        private static RectInt NormalizeBoundingBox(double[] bbox, int tileScale, int width, int height)
        {
            int minX = Mathf.Clamp(Mathf.RoundToInt((float)bbox[0] / tileScale), 0, width);
            int minY = Mathf.Clamp(Mathf.RoundToInt((float)bbox[1] / tileScale), 0, height);
            int maxX = Mathf.Clamp(Mathf.RoundToInt((float)bbox[2] / tileScale), 0, width - 1);
            int maxY = Mathf.Clamp(Mathf.RoundToInt((float)bbox[3] / tileScale), 0, height - 1);

            int rectWidth = Math.Max(0, (maxX - minX) + 1);
            int rectHeight = Math.Max(0, (maxY - minY) + 1);
            return new RectInt(minX, minY, rectWidth, rectHeight);
        }

        private static IReadOnlyList<GridPos> BuildServicePoints(PrototypeServicePointConfig[] configs, RectInt area)
        {
            if (configs == null || configs.Length == 0 || area.width <= 0 || area.height <= 0)
            {
                return Array.Empty<GridPos>();
            }

            var points = new List<GridPos>();
            foreach (var cfg in configs)
            {
                if (cfg == null)
                {
                    continue;
                }

                float normX = Mathf.Clamp01(cfg.x);
                float normY = Mathf.Clamp01(cfg.y);
                int px = area.x + Mathf.RoundToInt(normX * Math.Max(0, area.width - 1));
                int py = area.y + Mathf.RoundToInt(normY * Math.Max(0, area.height - 1));
                points.Add(new GridPos(px, py));
            }

            return points;
        }

        private static IReadOnlyList<VillageBuildingAnnotation> LoadAnnotations(string path)
        {
            var json = File.ReadAllText(path);
            var wrapper = JsonUtilities.Deserialize<AnnotationWrapper>(json) ?? new AnnotationWrapper();
            var result = new List<VillageBuildingAnnotation>();
            foreach (var annotation in wrapper.buildings ?? Array.Empty<VillageBuildingAnnotation>())
            {
                if (annotation == null)
                {
                    continue;
                }

                try
                {
                    annotation.ApplyDefaults();
                    result.Add(annotation);
                }
                catch
                {
                    // Ignore malformed entries.
                }
            }

            return result;
        }

        private static VillageConfig LoadVillageConfig(string path)
        {
            using var stream = File.OpenRead(path);
            var cfg = JsonUtilities.Deserialize<VillageConfig>(stream) ?? new VillageConfig();
            cfg.ApplyDefaults();
            return cfg;
        }

        private static Color32Key ParseColor(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                throw new InvalidOperationException("Color must be provided as a non-empty hex string.");
            }

            hex = hex.Trim();
            if (hex.StartsWith("#", StringComparison.Ordinal))
            {
                hex = hex[1..];
            }

            if (hex.Length != 6)
            {
                throw new InvalidOperationException($"Color '{hex}' must be a 6-character hex code.");
            }

            if (!int.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
                !int.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
                !int.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                throw new InvalidOperationException($"Color '{hex}' contains invalid hex characters.");
            }

            return new Color32Key((byte)r, (byte)g, (byte)b, 255);
        }

        private sealed class WorldMapDefinitionWrapper
        {
            public WorldMapConfig world { get; set; }
        }

        [Serializable]
        private sealed class AnnotationWrapper
        {
            public VillageBuildingAnnotation[] buildings = Array.Empty<VillageBuildingAnnotation>();
        }
    }

    [Serializable]
    public struct GridPos
    {
        public int x;
        public int y;

        public GridPos(int x, int y)
        {
            this.x = x;
            this.y = y;
        }

        public int X => x;

        public int Y => y;

        public Vector2Int ToVector2Int() => new Vector2Int(x, y);

        public override string ToString() => $"({x}, {y})";
    }

    [Serializable]
    public sealed class BuildingConfig
    {
        public string id;
        public string prototypeId;
        public GridPos position;
        public float rotation;
        public BuildingAreaConfig area;
        public bool open;
        public int capacity;
        public ServicePointConfig[] service_points = Array.Empty<ServicePointConfig>();
        public BuildingOpenHoursConfig[] openHours = Array.Empty<BuildingOpenHoursConfig>();
        public ShopConfig shop;

        public void ApplyDefaults()
        {
            id ??= string.Empty;
            prototypeId ??= string.Empty;
            service_points ??= Array.Empty<ServicePointConfig>();
            openHours ??= Array.Empty<BuildingOpenHoursConfig>();
            shop?.ApplyDefaults();
        }

        public static BuildingConfig Clone(BuildingConfig source)
        {
            if (source == null)
            {
                return null;
            }

            return new BuildingConfig
            {
                id = source.id,
                prototypeId = source.prototypeId,
                position = source.position,
                rotation = source.rotation,
                area = source.area == null ? null : new BuildingAreaConfig
                {
                    x = source.area.x,
                    y = source.area.y,
                    width = source.area.width,
                    height = source.area.height
                },
                open = source.open,
                capacity = source.capacity,
                service_points = source.service_points?.Select(sp => sp == null ? null : new ServicePointConfig { x = sp.x, y = sp.y }).ToArray() ?? Array.Empty<ServicePointConfig>(),
                openHours = source.openHours?.Select(oh => oh == null ? null : new BuildingOpenHoursConfig
                {
                    days = oh.days?.ToArray() ?? Array.Empty<string>(),
                    seasons = oh.seasons?.ToArray() ?? Array.Empty<string>(),
                    open = oh.open,
                    close = oh.close
                }).ToArray() ?? Array.Empty<BuildingOpenHoursConfig>(),
                shop = ShopConfig.Clone(source.shop)
            };
        }
    }

    [Serializable]
    public sealed class BuildingAreaConfig
    {
        public int x;
        public int y;
        public int width;
        public int height;
    }

    [Serializable]
    public sealed class ServicePointConfig
    {
        public int x;
        public int y;
    }

    [Serializable]
    public sealed class BuildingOpenHoursConfig
    {
        public string[] days = Array.Empty<string>();
        public string[] seasons = Array.Empty<string>();
        public float open;
        public float close;
    }

    [Serializable]
    public sealed class ShopConfig
    {
        public ShopItemConfig[] items = Array.Empty<ShopItemConfig>();
        public string restockEvery;
        public float restockHour;
        public float markup = 1f;
        public float markdown = 1f;
        public ShopInventoryConfig inventory = new ShopInventoryConfig();

        public void ApplyDefaults()
        {
            items ??= Array.Empty<ShopItemConfig>();
            inventory ??= new ShopInventoryConfig();
            inventory.ApplyDefaults();
        }

        public static ShopConfig Clone(ShopConfig source)
        {
            if (source == null)
            {
                return null;
            }

            return new ShopConfig
            {
                restockEvery = source.restockEvery,
                restockHour = source.restockHour,
                markup = source.markup,
                markdown = source.markdown,
                inventory = source.inventory == null
                    ? new ShopInventoryConfig()
                    : new ShopInventoryConfig
                    {
                        slots = source.inventory.slots,
                        stackSize = source.inventory.stackSize
                    },
                items = source.items?.Select(i => i == null ? null : new ShopItemConfig
                {
                    item = i.item,
                    quantity = i.quantity,
                    restock = i.restock
                }).ToArray() ?? Array.Empty<ShopItemConfig>()
            };
        }
    }

    [Serializable]
    public sealed class ShopItemConfig
    {
        public string item;
        public int quantity;
        public string restock;
    }

    [Serializable]
    public sealed class ShopInventoryConfig
    {
        public int slots = 1;
        public int stackSize = 1;

        public void ApplyDefaults()
        {
            slots = Math.Max(1, slots);
            stackSize = Math.Max(1, stackSize);
        }
    }

    [Serializable]
    public sealed class MapBuildingPrototypeConfig
    {
        public string idPrefix;
        public string type;
        public string[] tags = Array.Empty<string>();
        public BuildingConfig building;
        public PrototypeServicePointConfig[] servicePoints = Array.Empty<PrototypeServicePointConfig>();

        public void ApplyDefaults()
        {
            tags ??= Array.Empty<string>();
            servicePoints ??= Array.Empty<PrototypeServicePointConfig>();
            building?.ApplyDefaults();
        }
    }

    [Serializable]
    public sealed class PrototypeServicePointConfig
    {
        public float x;
        public float y;
    }

    [Serializable]
    public sealed class VillageLocation
    {
        public string id;
        public string name;
        public string type;
        public double[] bbox;
        public double[] center;
        public int radius;

        public void ApplyDefaults()
        {
            id ??= string.Empty;
            name ??= string.Empty;
            type ??= string.Empty;
            radius = Math.Max(0, radius);
        }

        public static VillageLocation Clone(VillageLocation src, string fallbackId)
        {
            if (src == null)
            {
                throw new InvalidOperationException($"Village location '{fallbackId}' must not be null.");
            }

            if (string.IsNullOrWhiteSpace(src.id))
            {
                throw new InvalidOperationException($"Village location '{fallbackId}' must specify an 'id'.");
            }

            if (src.bbox == null || src.bbox.Length != 4)
            {
                throw new InvalidOperationException($"Village location '{src.id.Trim()}' must specify a 'bbox' with four coordinates.");
            }

            if (src.center == null || src.center.Length < 2)
            {
                throw new InvalidOperationException($"Village location '{src.id.Trim()}' must specify a 'center' with two coordinates.");
            }

            return new VillageLocation
            {
                id = src.id,
                name = src.name,
                type = src.type,
                bbox = src.bbox.ToArray(),
                center = src.center.ToArray(),
                radius = src.radius
            };
        }
    }

    [Serializable]
    public sealed class VillageBuildingAnnotation
    {
        public string name;
        public string location;
        public double[] bbox;
        public float rotation;
        public string[] tags = Array.Empty<string>();
        public Dictionary<string, double> attributes = new Dictionary<string, double>();

        public void ApplyDefaults()
        {
            name ??= string.Empty;
            tags ??= Array.Empty<string>();
            attributes ??= new Dictionary<string, double>();
        }
    }

    [Serializable]
    public sealed class MapServicePointConfig
    {
        public string id;
        public string displayName;
        public string serviceType;
        public GridPos position;

        public void ApplyDefaults()
        {
            id ??= string.Empty;
            displayName ??= id;
            serviceType ??= string.Empty;
        }
    }

    [Serializable]
    public sealed class VillageConfig
    {
        public string id;
        public string displayName;
        public VillageLocation location = new VillageLocation();
        public BuildingConfig[] buildings = Array.Empty<BuildingConfig>();
        public MapServicePointConfig[] servicePoints = Array.Empty<MapServicePointConfig>();
        public VillageBuildingAnnotation[] annotations = Array.Empty<VillageBuildingAnnotation>();
        public VillageMapData map = new VillageMapData();
        public Dictionary<string, VillageLocation> locations = new Dictionary<string, VillageLocation>(StringComparer.OrdinalIgnoreCase);

        public void ApplyDefaults()
        {
            id ??= string.Empty;
            displayName ??= string.IsNullOrWhiteSpace(displayName) ? id : displayName;
            location ??= new VillageLocation();
            location.ApplyDefaults();

            buildings ??= Array.Empty<BuildingConfig>();
            foreach (var building in buildings)
            {
                building?.ApplyDefaults();
            }

            servicePoints ??= Array.Empty<MapServicePointConfig>();
            foreach (var servicePoint in servicePoints)
            {
                servicePoint?.ApplyDefaults();
            }

            annotations ??= Array.Empty<VillageBuildingAnnotation>();
            foreach (var annotation in annotations)
            {
                annotation?.ApplyDefaults();
            }

            map ??= new VillageMapData();
            map.ApplyDefaults();

            locations ??= new Dictionary<string, VillageLocation>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in locations.ToArray())
            {
                if (kv.Value == null)
                {
                    locations.Remove(kv.Key);
                    continue;
                }

                kv.Value.ApplyDefaults();
            }
        }
    }

    [Serializable]
    public sealed class VillageMapData
    {
        public Dictionary<string, string> key = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public VillageMapAnnotations annotations = new VillageMapAnnotations();

        public void ApplyDefaults()
        {
            key ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            annotations ??= new VillageMapAnnotations();
            annotations.ApplyDefaults();
        }
    }

    [Serializable]
    public sealed class VillageMapAnnotations
    {
        public VillageBuildingAnnotation[] buildings = Array.Empty<VillageBuildingAnnotation>();

        public void ApplyDefaults()
        {
            buildings ??= Array.Empty<VillageBuildingAnnotation>();
            foreach (var annotation in buildings)
            {
                annotation?.ApplyDefaults();
            }
        }
    }

    [Serializable]
    public sealed class WorldMapConfig
    {
        public string image;
        public string key;
        public string data;
        public string annotations;
        public int tileSize = 1;
        public Dictionary<string, MapTileConfig> tiles = new Dictionary<string, MapTileConfig>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, MapBuildingPrototypeConfig> buildingPrototypes = new Dictionary<string, MapBuildingPrototypeConfig>(StringComparer.OrdinalIgnoreCase);

        public static WorldMapConfig CreateEmpty() => new WorldMapConfig();

        public void ApplyDefaults()
        {
            tileSize = Math.Max(1, tileSize);
            tiles ??= new Dictionary<string, MapTileConfig>(StringComparer.OrdinalIgnoreCase);
            buildingPrototypes ??= new Dictionary<string, MapBuildingPrototypeConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in buildingPrototypes.ToArray())
            {
                kv.Value?.ApplyDefaults();
            }
        }
    }

    [Serializable]
    public sealed class MapTileConfig
    {
        public bool walkable;
        public bool farmland;
        public bool water;
        public bool shallowWater;
        public bool forest;
        public bool coastal;
    }
}
