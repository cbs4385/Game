using System;
using UnityEngine;

namespace DataDrivenGoap
{
    /// <summary>
    /// Utility responsible for converting JSON payloads into strongly typed GOAP definitions.
    /// </summary>
    public static class DataDrivenGoapJsonLoader
    {
        public static MapDefinitionDto LoadMapDefinition(TextAsset asset)
        {
            if (asset == null)
            {
                throw new ArgumentNullException(nameof(asset));
            }

            var definition = JsonUtility.FromJson<MapDefinitionDto>(asset.text);
            if (definition == null)
            {
                throw new InvalidOperationException("Failed to deserialize the map definition payload.");
            }

            definition.ApplyDefaults();
            return definition;
        }

        public static PawnDefinitionsDto LoadPawnDefinitions(TextAsset asset)
        {
            if (asset == null)
            {
                throw new ArgumentNullException(nameof(asset));
            }

            var definition = JsonUtility.FromJson<PawnDefinitionsDto>(asset.text);
            if (definition == null)
            {
                throw new InvalidOperationException("Failed to deserialize the pawn definition payload.");
            }

            definition.ApplyDefaults();
            return definition;
        }

        public static ItemDefinitionsDto LoadItemDefinitions(TextAsset asset)
        {
            if (asset == null)
            {
                throw new ArgumentNullException(nameof(asset));
            }

            if (string.IsNullOrWhiteSpace(asset.text))
            {
                return ItemDefinitionsDto.Empty;
            }

            var definition = JsonUtility.FromJson<ItemDefinitionsDto>(asset.text);
            if (definition == null)
            {
                throw new InvalidOperationException("Failed to deserialize the item definition payload.");
            }

            definition.ApplyDefaults();
            return definition;
        }
    }

    [Serializable]
    public sealed class MapDefinitionDto
    {
        public SerializableVector2Int size = new SerializableVector2Int(1, 1);
        public float tileSpacing = 1f;
        public float minElevation = 0f;
        public float maxElevation = 1f;
        public MapTileDefinitionDto[] tiles = Array.Empty<MapTileDefinitionDto>();

        public void ApplyDefaults()
        {
            tiles ??= Array.Empty<MapTileDefinitionDto>();
            foreach (var tile in tiles)
            {
                tile?.ApplyDefaults();
            }
        }
    }

    [Serializable]
    public sealed class MapTileDefinitionDto
    {
        public SerializableVector2Int coordinates = new SerializableVector2Int();
        public float elevation = 0f;
        public float traversalCost = 1f;

        public void ApplyDefaults()
        {
            traversalCost = Mathf.Max(0f, traversalCost);
        }
    }

    [Serializable]
    public sealed class PawnDefinitionsDto
    {
        public float defaultSpeed = 2f;
        public float defaultHeightOffset = 0.75f;
        public PawnDefinitionDto[] pawns = Array.Empty<PawnDefinitionDto>();

        public void ApplyDefaults()
        {
            pawns ??= Array.Empty<PawnDefinitionDto>();
            foreach (var pawn in pawns)
            {
                pawn?.ApplyDefaults();
            }

            defaultSpeed = Mathf.Max(0.01f, defaultSpeed);
        }
    }

    [Serializable]
    public sealed class PawnDefinitionDto
    {
        public int id = -1;
        public string name;
        public string color = "#FFFFFFFF";
        public SerializableVector2Int spawnTile = new SerializableVector2Int();
        public float speed = -1f;
        public float heightOffset = -1f;

        public void ApplyDefaults()
        {
            if (string.IsNullOrWhiteSpace(color))
            {
                color = "#FFFFFFFF";
            }
        }
    }

    [Serializable]
    public sealed class ItemDefinitionsDto
    {
        public ItemDefinitionDto[] items = Array.Empty<ItemDefinitionDto>();

        public static ItemDefinitionsDto Empty { get; } = new ItemDefinitionsDto
        {
            items = Array.Empty<ItemDefinitionDto>()
        };

        public void ApplyDefaults()
        {
            items ??= Array.Empty<ItemDefinitionDto>();
            foreach (var item in items)
            {
                item?.ApplyDefaults();
            }
        }
    }

    [Serializable]
    public sealed class ItemDefinitionDto
    {
        public string id;
        public string name;
        public SerializableVector2Int tile = new SerializableVector2Int();

        public void ApplyDefaults()
        {
            // No-op, placeholder for future validation hooks.
        }
    }

    [Serializable]
    public struct SerializableVector2Int
    {
        public int x;
        public int y;

        public SerializableVector2Int(int x, int y)
        {
            this.x = x;
            this.y = y;
        }

        public Vector2Int ToVector2Int() => new Vector2Int(x, y);
    }
}
