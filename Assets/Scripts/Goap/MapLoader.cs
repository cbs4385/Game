using System;
using System.Collections.Generic;
using UnityEngine;

namespace DataDrivenGoap
{
    /// <summary>
    /// Utility responsible for loading map configuration data without relying on
    /// third-party packages that are not available inside the Unity runtime.
    /// </summary>
    public static class MapLoader
    {
        private const byte DefaultColorTolerance = 2;

        /// <summary>
        /// Attempts to load a <see cref="WorldMapConfig"/> instance from the provided JSON asset.
        /// </summary>
        /// <param name="asset">JSON asset that contains a serialized <see cref="WorldMapConfig"/> payload.</param>
        /// <param name="config">The parsed configuration on success.</param>
        /// <param name="errorMessage">Human readable error information when the operation fails.</param>
        /// <returns>True when the configuration could be parsed, otherwise false.</returns>
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

        /// <summary>
        /// Attempts to load a <see cref="WorldMapConfig"/> instance from the provided JSON payload.
        /// </summary>
        /// <param name="json">JSON payload that describes the world map.</param>
        /// <param name="config">The parsed configuration on success.</param>
        /// <param name="errorMessage">Human readable error information when the operation fails.</param>
        /// <returns>True when the configuration could be parsed, otherwise false.</returns>
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
                var wrapper = JsonUtility.FromJson<WorldMapDefinitionWrapper>(json);
                if (wrapper == null)
                {
                    config = WorldMapConfig.CreateEmpty();
                    errorMessage = "Failed to deserialize the world map definition.";
                    return false;
                }

                config = wrapper.world ?? WorldMapConfig.CreateEmpty();
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

        /// <summary>
        /// Checks whether two colors are approximately equal by comparing their channels against a tolerance.
        /// </summary>
        public static bool ApproximatelyEqual(Color32 lhs, Color32 rhs, byte tolerance = DefaultColorTolerance)
        {
            return Math.Abs(lhs.r - rhs.r) <= tolerance
                && Math.Abs(lhs.g - rhs.g) <= tolerance
                && Math.Abs(lhs.b - rhs.b) <= tolerance
                && Math.Abs(lhs.a - rhs.a) <= tolerance;
        }

        [Serializable]
        private sealed class WorldMapDefinitionWrapper
        {
            public WorldMapConfig world;
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

        public void ApplyDefaults()
        {
            id ??= string.Empty;
            prototypeId ??= string.Empty;
        }
    }

    [Serializable]
    public sealed class MapBuildingPrototypeConfig
    {
        public string id;
        public string displayName;
        public string spriteId;
        public Color32 color;
        public bool allowRotation = true;

        public void ApplyDefaults()
        {
            id ??= string.Empty;
            displayName ??= id;
            spriteId ??= string.Empty;
        }
    }

    [Serializable]
    public sealed class VillageLocation
    {
        public string id;
        public string displayName;
        public GridPos center;
        public int radius;

        public void ApplyDefaults()
        {
            id ??= string.Empty;
            displayName ??= id;
            radius = Math.Max(0, radius);
        }
    }

    [Serializable]
    public sealed class VillageBuildingAnnotation
    {
        public string villageId;
        public string buildingId;
        public GridPos position;

        public void ApplyDefaults()
        {
            villageId ??= string.Empty;
            buildingId ??= string.Empty;
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

        public void ApplyDefaults()
        {
            id ??= string.Empty;
            displayName ??= id;
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
        }
    }

    [Serializable]
    public sealed class WorldMapConfig
    {
        public GridPos mapSize = new GridPos(32, 32);
        public float tileSpacing = 1f;
        public VillageConfig[] villages = Array.Empty<VillageConfig>();
        public MapBuildingPrototypeConfig[] buildingPrototypes = Array.Empty<MapBuildingPrototypeConfig>();

        public static WorldMapConfig CreateEmpty()
        {
            return new WorldMapConfig();
        }

        public void ApplyDefaults()
        {
            tileSpacing = Mathf.Max(0.01f, tileSpacing);

            villages ??= Array.Empty<VillageConfig>();
            foreach (var village in villages)
            {
                village?.ApplyDefaults();
            }

            buildingPrototypes ??= Array.Empty<MapBuildingPrototypeConfig>();
            foreach (var prototype in buildingPrototypes)
            {
                prototype?.ApplyDefaults();
            }
        }
    }
}
