using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

[CreateAssetMenu(menuName = "UI/Inventory Panel Settings", fileName = "InventoryPanelSettings")]
public sealed class InventoryPanelSettingsAsset : ScriptableObject
{
    [Header("Scaling")]
    [SerializeField] private PanelScaleMode scaleMode = PanelScaleMode.ScaleWithScreenSize;
    [SerializeField] private Vector2 referenceResolution = new Vector2(1920f, 1080f);
    [SerializeField, Min(1f)] private float referenceDpi = 96f;
    [SerializeField, Range(0f, 1f)] private float match = 0f;
    [SerializeField] private PanelScreenMatchMode screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
    [SerializeField, Min(0.01f)] private float scale = 1f;

    [Header("Rendering")]
    [SerializeField] private RenderTexture targetTexture;
    [SerializeField] private bool clearDepthStencil = true;
    [SerializeField] private int maxQueuedFrames = 8;
    [SerializeField] private PanelClearFlagsOption panelClearFlags = PanelClearFlagsOption.DepthStencil;
    [SerializeField] private PanelTextSettings textSettings;
    [SerializeField, Min(0)] private int targetDisplay = 0;
    [SerializeField] private bool drawToCameras = true;
    [SerializeField] private Rect viewport = new Rect(0f, 0f, 1f, 1f);
    [SerializeField] private bool vsync = true;
    [SerializeField, Min(0f)] private float targetWidth;
    [SerializeField, Min(0f)] private float targetHeight;
    [SerializeField] private int worldSpaceLayer;
    [SerializeField] private int sortingOrder;
    [SerializeField] private LayerMask targetLayerMask = ~0;
    [SerializeField] private RuntimePanelRenderingModeOption renderingMode = RuntimePanelRenderingModeOption.Camera;
    [SerializeField, Min(0)] private int vsyncCount = 1;
    [SerializeField] private Shader runtimeShader;
    [SerializeField] private PanelSettings runtimeWorldSpacePanelSettings;
    [SerializeField, Min(0)] private int antiAliasing = 4;
    [SerializeField, Min(0.01f)] private float pixelsPerUnit = 100f;

    public PanelSettings CreateRuntimePanelSettings()
    {
        var instance = ScriptableObject.CreateInstance<PanelSettings>();
        instance.hideFlags = HideFlags.HideAndDontSave;
        instance.name = $"{name}_Runtime";

        instance.scaleMode = scaleMode;
        AssignRequired(instance, nameof(referenceResolution), referenceResolution);
        instance.referenceDpi = referenceDpi;
        instance.match = match;
        instance.screenMatchMode = screenMatchMode;
        instance.scale = scale;
        instance.targetTexture = targetTexture;
        instance.clearDepthStencil = clearDepthStencil;
        AssignEnumValue(instance, "panelClearFlags", GetPanelClearFlagsName(panelClearFlags));
        instance.textSettings = textSettings;
        instance.targetDisplay = targetDisplay;
        TryAssignOptional(instance, nameof(drawToCameras), drawToCameras);
        TryAssignOptional(instance, nameof(viewport), viewport);
        TryAssignOptional(instance, nameof(vsync), vsync);
        TryAssignOptional(instance, nameof(targetWidth), targetWidth);
        TryAssignOptional(instance, nameof(targetHeight), targetHeight);
        instance.sortingOrder = sortingOrder;
        AssignEnumValue(instance, "renderingMode", GetRuntimePanelRenderingModeName(renderingMode));
        TryAssignOptional(instance, nameof(vsyncCount), vsyncCount);
        TryAssignOptional(instance, nameof(runtimeShader), runtimeShader);
        TryAssignOptional(instance, nameof(runtimeWorldSpacePanelSettings), runtimeWorldSpacePanelSettings);
        TryAssignOptional(instance, nameof(antiAliasing), antiAliasing);
        TryAssignOptional(instance, nameof(pixelsPerUnit), pixelsPerUnit);

        TryAssignOptional(instance, nameof(maxQueuedFrames), maxQueuedFrames);
        TryAssignOptional(instance, nameof(worldSpaceLayer), worldSpaceLayer);
        TryAssignTargetLayerMask(instance, targetLayerMask);

        return instance;
    }

    [Flags]
    private enum PanelClearFlagsOption
    {
        None = 0,
        Color = 1,
        Depth = 2,
        Stencil = 4,
        DepthStencil = Depth | Stencil,
        All = Color | Depth | Stencil
    }

    private enum RuntimePanelRenderingModeOption
    {
        Disabled = 0,
        Camera = 1,
        WorldSpace = 2
    }

    private static string GetPanelClearFlagsName(PanelClearFlagsOption option)
    {
        return option switch
        {
            PanelClearFlagsOption.None => "None",
            PanelClearFlagsOption.Color => "Color",
            PanelClearFlagsOption.Depth => "Depth",
            PanelClearFlagsOption.Stencil => "Stencil",
            PanelClearFlagsOption.DepthStencil => "DepthStencil",
            PanelClearFlagsOption.All => "All",
            _ => throw new ArgumentOutOfRangeException(nameof(option), option, null)
        };
    }

    private static string GetRuntimePanelRenderingModeName(RuntimePanelRenderingModeOption option)
    {
        return option switch
        {
            RuntimePanelRenderingModeOption.Disabled => "Disabled",
            RuntimePanelRenderingModeOption.Camera => "Camera",
            RuntimePanelRenderingModeOption.WorldSpace => "WorldSpace",
            _ => throw new ArgumentOutOfRangeException(nameof(option), option, null)
        };
    }

    private static void AssignRequired<T>(PanelSettings target, string memberName, T value)
    {
        if (!TryAssignOptional(target, memberName, value))
        {
            throw new MissingMemberException(target.GetType().FullName, memberName);
        }
    }

    private static bool TryAssignOptional<T>(PanelSettings target, string memberName, T value)
    {
        var type = target.GetType();
        var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.CanWrite && TryConvertValue(value, property.PropertyType, out var convertedPropertyValue))
        {
            property.SetValue(target, convertedPropertyValue);
            return true;
        }

        var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && TryConvertValue(value, field.FieldType, out var convertedFieldValue))
        {
            field.SetValue(target, convertedFieldValue);
            return true;
        }

        return false;
    }

    private static bool TryConvertValue<T>(T value, Type targetType, out object converted)
    {
        if (value == null)
        {
            if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null)
            {
                converted = null;
                return true;
            }

            converted = null;
            return false;
        }

        var valueType = value.GetType();
        if (targetType.IsAssignableFrom(valueType))
        {
            converted = value;
            return true;
        }

        if (targetType.IsEnum)
        {
            if (value is string enumName)
            {
                converted = Enum.Parse(targetType, enumName, false);
                return true;
            }

            if (IsNumericType(valueType))
            {
                converted = Enum.ToObject(targetType, value);
                return true;
            }
        }

        if (IsNumericType(valueType) && IsNumericType(targetType))
        {
            converted = Convert.ChangeType(value, targetType);
            return true;
        }

        if (targetType == typeof(Vector2Int) && value is Vector2 vectorValue)
        {
            converted = Vector2Int.RoundToInt(vectorValue);
            return true;
        }

        if (targetType == typeof(Vector2) && value is Vector2Int vectorIntValue)
        {
            converted = (Vector2)vectorIntValue;
            return true;
        }

        if (targetType == typeof(RectInt) && value is Rect rectValue)
        {
            converted = new RectInt(
                Mathf.RoundToInt(rectValue.xMin),
                Mathf.RoundToInt(rectValue.yMin),
                Mathf.RoundToInt(rectValue.width),
                Mathf.RoundToInt(rectValue.height));
            return true;
        }

        if (targetType == typeof(Rect) && value is RectInt rectIntValue)
        {
            converted = new Rect(rectIntValue.x, rectIntValue.y, rectIntValue.width, rectIntValue.height);
            return true;
        }

        converted = null;
        return false;
    }

    private static bool IsNumericType(Type type)
    {
        switch (Type.GetTypeCode(type))
        {
            case TypeCode.Byte:
            case TypeCode.SByte:
            case TypeCode.UInt16:
            case TypeCode.UInt32:
            case TypeCode.UInt64:
            case TypeCode.Int16:
            case TypeCode.Int32:
            case TypeCode.Int64:
            case TypeCode.Decimal:
            case TypeCode.Double:
            case TypeCode.Single:
                return true;
            default:
                return false;
        }
    }

    private static void AssignEnumValue(PanelSettings target, string memberName, string enumName)
    {
        if (string.IsNullOrEmpty(enumName))
        {
            return;
        }

        var type = target.GetType();
        var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.CanWrite)
        {
            var value = CreateEnumValue(property.PropertyType, enumName, memberName);
            property.SetValue(target, value);
            return;
        }

        var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
            var value = CreateEnumValue(field.FieldType, enumName, memberName);
            field.SetValue(target, value);
            return;
        }

        throw new MissingMemberException(type.FullName, memberName);
    }

    private static object CreateEnumValue(Type enumType, string enumName, string memberName)
    {
        if (!enumType.IsEnum)
        {
            throw new InvalidOperationException($"Member '{memberName}' on '{enumType.FullName}' is not an enum.");
        }

        try
        {
            return Enum.Parse(enumType, enumName, false);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"Value '{enumName}' is not defined for enum '{enumType.FullName}'.", exception);
        }
    }

    private static void AssignEnumValue(PanelSettings target, string memberName, string enumName)
    {
        if (string.IsNullOrEmpty(enumName))
        {
            return;
        }

        var type = target.GetType();
        var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.CanWrite)
        {
            var value = CreateEnumValue(property.PropertyType, enumName, memberName);
            property.SetValue(target, value);
            return;
        }

        var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
            var value = CreateEnumValue(field.FieldType, enumName, memberName);
            field.SetValue(target, value);
            return;
        }

        throw new MissingMemberException(type.FullName, memberName);
    }

    private static object CreateEnumValue(Type enumType, string enumName, string memberName)
    {
        if (!enumType.IsEnum)
        {
            throw new InvalidOperationException($"Member '{memberName}' on '{enumType.FullName}' is not an enum.");
        }

        try
        {
            return Enum.Parse(enumType, enumName, false);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"Value '{enumName}' is not defined for enum '{enumType.FullName}'.", exception);
        }
    }

    private static void TryAssignTargetLayerMask(PanelSettings target, LayerMask mask)
    {
        var type = target.GetType();
        var property = type.GetProperty("targetLayerMask", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.CanWrite)
        {
            if (property.PropertyType == typeof(LayerMask))
            {
                property.SetValue(target, mask);
                return;
            }

            if (property.PropertyType == typeof(int))
            {
                property.SetValue(target, mask.value);
                return;
            }

            if (property.PropertyType == typeof(uint))
            {
                property.SetValue(target, Convert.ToUInt32(mask.value));
                return;
            }
        }

        var field = type.GetField("targetLayerMask", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
            if (field.FieldType == typeof(LayerMask))
            {
                field.SetValue(target, mask);
            }
            else if (field.FieldType == typeof(int))
            {
                field.SetValue(target, mask.value);
            }
            else if (field.FieldType == typeof(uint))
            {
                field.SetValue(target, Convert.ToUInt32(mask.value));
            }
        }
    }
}
