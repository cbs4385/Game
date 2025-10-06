using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

[CreateAssetMenu(menuName = "UI/Inventory Panel Settings", fileName = "InventoryPanelSettings")]
public sealed class InventoryPanelSettingsAsset : ScriptableObject
{
    [Header("Scaling")]
    [SerializeField] private PanelScaleMode scaleMode = PanelScaleMode.ScaleWithScreenSize;
    [SerializeField] private Vector2Int referenceResolution = new Vector2Int(1920, 1080);
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

        AssignRequired(instance, ScaleModeMemberName, scaleMode);
        AssignRequired(instance, ReferenceResolutionMemberName, referenceResolution);
        AssignRequired(instance, ReferenceDpiMemberName, referenceDpi);
        AssignRequired(instance, MatchMemberName, match);
        AssignRequired(instance, ScreenMatchModeMemberName, screenMatchMode);
        AssignRequired(instance, ScaleMemberName, scale);
        TryAssignOptional(instance, TargetTextureMemberName, targetTexture);
        AssignRequired(instance, ClearDepthStencilMemberName, clearDepthStencil);
        AssignPanelClearFlags(instance, panelClearFlags);
        TryAssignOptional(instance, TextSettingsMemberName, textSettings);
        AssignRequired(instance, TargetDisplayMemberName, targetDisplay);
        TryAssignOptional(instance, DrawToCamerasMemberName, drawToCameras);
        TryAssignOptional(instance, ViewportMemberName, viewport);
        TryAssignOptional(instance, VsyncMemberName, vsync);
        TryAssignOptional(instance, TargetWidthMemberName, targetWidth);
        TryAssignOptional(instance, TargetHeightMemberName, targetHeight);
        AssignRequired(instance, SortingOrderMemberName, sortingOrder);
        AssignRenderingMode(instance, renderingMode);
        TryAssignOptional(instance, VsyncCountMemberName, vsyncCount);
        TryAssignOptional(instance, RuntimeShaderMemberName, runtimeShader);
        TryAssignOptional(instance, RuntimeWorldSpacePanelSettingsMemberName, runtimeWorldSpacePanelSettings);
        TryAssignOptional(instance, AntiAliasingMemberName, antiAliasing);
        TryAssignOptional(instance, PixelsPerUnitMemberName, pixelsPerUnit);

        TryAssignOptional(instance, MaxQueuedFramesMemberName, maxQueuedFrames);
        TryAssignOptional(instance, WorldSpaceLayerMemberName, worldSpaceLayer);
        TryAssignTargetLayerMask(instance, targetLayerMask);

        return instance;
    }

    private const string ScaleModeMemberName = nameof(PanelSettings.scaleMode);
    private const string ReferenceResolutionMemberName = "referenceResolution";
    private const string ReferenceDpiMemberName = nameof(PanelSettings.referenceDpi);
    private const string MatchMemberName = nameof(PanelSettings.match);
    private const string ScreenMatchModeMemberName = nameof(PanelSettings.screenMatchMode);
    private const string ScaleMemberName = nameof(PanelSettings.scale);
    private const string TargetTextureMemberName = nameof(PanelSettings.targetTexture);
    private const string ClearDepthStencilMemberName = nameof(PanelSettings.clearDepthStencil);
    private const string PanelClearFlagsMemberName = "panelClearFlags";
    private const string ClearFlagsMemberName = "clearFlags";
    private const string ClearSettingsMemberName = "clearSettings";
    private const string TextSettingsMemberName = nameof(PanelSettings.textSettings);
    private const string TargetDisplayMemberName = nameof(PanelSettings.targetDisplay);
    private const string DrawToCamerasMemberName = "drawToCameras";
    private const string ViewportMemberName = "viewport";
    private const string VsyncMemberName = "vsync";
    private const string TargetWidthMemberName = "targetWidth";
    private const string TargetHeightMemberName = "targetHeight";
    private const string SortingOrderMemberName = nameof(PanelSettings.sortingOrder);
    private static readonly string[] RenderingModeMemberNames =
    {
        "renderingMode",
        "m_RenderingMode"
    };

    private static readonly string[] RuntimePanelCreationSettingsMemberNames =
    {
        "runtimePanelCreationSettings",
        "m_RuntimePanelCreationSettings"
    };

    private static readonly string[] RuntimePanelSettingsMemberNames =
    {
        "runtimePanelSettings",
        "m_RuntimePanelSettings"
    };
    private const string VsyncCountMemberName = "vsyncCount";
    private const string RuntimeShaderMemberName = "runtimeShader";
    private const string RuntimeWorldSpacePanelSettingsMemberName = "runtimeWorldSpacePanelSettings";
    private const string AntiAliasingMemberName = "antiAliasing";
    private const string PixelsPerUnitMemberName = "pixelsPerUnit";
    private const string MaxQueuedFramesMemberName = "maxQueuedFrames";
    private const string WorldSpaceLayerMemberName = "worldSpaceLayer";

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

    private static void AssignRenderingMode(PanelSettings target, RuntimePanelRenderingModeOption option)
    {
        var enumName = GetRuntimePanelRenderingModeName(option);
        var numericValue = Convert.ToInt64(option);

        if (TryAssignRenderingModeByName(target, enumName) ||
            TryAssignRenderingModeByNumericValue(target, numericValue))
        {
            return;
        }

        if (TryAssignRenderingModeInAnyContainerByName(target, enumName) ||
            TryAssignRenderingModeInAnyContainerByNumericValue(target, numericValue))
        {
            return;
        }

        if (TryAssignRenderingModeBySemanticMatchOnObject(target, enumName, numericValue) ||
            TryAssignRenderingModeInAllContainersBySemanticMatch(target, enumName, numericValue))
        {
            return;
        }

        throw new MissingMemberException(target.GetType().FullName, RenderingModeMemberNames[0]);
    }

    private static bool TryAssignRenderingModeByName(PanelSettings target, string enumName)
    {
        if (string.IsNullOrEmpty(enumName))
        {
            return false;
        }

        return TryAssignEnumValueOnObject(target, RenderingModeMemberNames, enumName);
    }

    private static bool TryAssignRenderingModeByNumericValue(PanelSettings target, long numericValue)
    {
        return TryAssignEnumValueOnObject(target, RenderingModeMemberNames, numericValue);
    }

    private static void AssignEnumValue(PanelSettings target, string memberName, string enumName)
    {
        if (!TryAssignEnumValue(target, memberName, enumName))
        {
            throw new MissingMemberException(target.GetType().FullName, memberName);
        }
    }

    private static bool TryAssignRenderingModeInAnyContainerByName(PanelSettings target, string enumName)
    {
        foreach (var containerName in RuntimePanelCreationSettingsMemberNames)
        {
            if (TryAssignRenderingModeInContainerByName(target, containerName, enumName))
            {
                return true;
            }
        }

        foreach (var containerName in RuntimePanelSettingsMemberNames)
        {
            if (TryAssignRenderingModeInContainerByName(target, containerName, enumName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryAssignRenderingModeInAnyContainerByNumericValue(PanelSettings target, long numericValue)
    {
        foreach (var containerName in RuntimePanelCreationSettingsMemberNames)
        {
            if (TryAssignRenderingModeInContainerByNumericValue(target, containerName, numericValue))
            {
                return true;
            }
        }

        foreach (var containerName in RuntimePanelSettingsMemberNames)
        {
            if (TryAssignRenderingModeInContainerByNumericValue(target, containerName, numericValue))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryAssignRenderingModeBySemanticMatchOnObject(object target, string enumName, long numericValue)
    {
        if (target == null)
        {
            return false;
        }

        var type = target.GetType();
        var members = type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        foreach (var member in members)
        {
            var canWrite = member switch
            {
                PropertyInfo property when property.PropertyType.IsEnum => property.CanWrite,
                FieldInfo field when field.FieldType.IsEnum => true,
                _ => false
            };

            if (!canWrite || !IndicatesRenderingMode(member.Name))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(enumName) && TryAssignEnumValueOnObject(target, member.Name, enumName))
            {
                return true;
            }

            if (TryAssignEnumValueOnObject(target, member.Name, numericValue))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryAssignRenderingModeInAllContainersBySemanticMatch(PanelSettings target, string enumName, long numericValue)
    {
        var type = target.GetType();

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!property.CanRead)
            {
                continue;
            }

            var container = property.GetValue(target) ?? CreateContainerInstance(property.PropertyType);
            if (TryAssignRenderingModeBySemanticMatchOnObject(container, enumName, numericValue))
            {
                if (property.CanWrite)
                {
                    property.SetValue(target, container);
                    return true;
                }

                if (container != null && !property.PropertyType.IsValueType)
                {
                    return true;
                }

                if (TryInvokeContainerSetter(target, property.Name, property.PropertyType, container))
                {
                    return true;
                }
            }
        }

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.FieldType.IsEnum)
            {
                continue;
            }

            var container = field.GetValue(target) ?? CreateContainerInstance(field.FieldType);
            if (TryAssignRenderingModeBySemanticMatchOnObject(container, enumName, numericValue))
            {
                field.SetValue(target, container);
                return true;
            }
        }

        return false;
    }

    private static bool TryAssignRenderingModeInContainerByName(PanelSettings target, string containerMemberName, string enumName)
    {
        if (string.IsNullOrEmpty(containerMemberName) || string.IsNullOrEmpty(enumName))
        {
            return false;
        }

        if (TryAssignRenderingModeOnObjectByName(target, containerMemberName, enumName))
        {
            return true;
        }

        return TryAssignRenderingModeThroughAccessorsByName(target, containerMemberName, enumName);
    }

    private static bool TryAssignRenderingModeInContainerByNumericValue(
        PanelSettings target,
        string containerMemberName,
        long numericValue)
    {
        if (string.IsNullOrEmpty(containerMemberName))
        {
            return false;
        }

        if (TryAssignRenderingModeOnObjectByNumericValue(target, containerMemberName, numericValue))
        {
            return true;
        }

        return TryAssignRenderingModeThroughAccessorsByNumericValue(target, containerMemberName, numericValue);
    }

    private static bool TryAssignRenderingModeOnObjectByName(object target, string containerMemberName, string enumName)
    {
        if (target == null || string.IsNullOrEmpty(containerMemberName) || string.IsNullOrEmpty(enumName))
        {
            return false;
        }

        var type = target.GetType();

        var property = type.GetProperty(containerMemberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null)
        {
            var canRead = property.CanRead;
            var container = canRead ? property.GetValue(target) : null;
            if (container == null)
            {
                container = CreateContainerInstance(property.PropertyType);
            }

            if (TryAssignEnumValueOnObject(container, RenderingModeMemberNames, enumName))
            {
                if (property.CanWrite)
                {
                    property.SetValue(target, container);
                    return true;
                }

                if (container != null && !property.PropertyType.IsValueType)
                {
                    return true;
                }

                if (TryInvokeContainerSetter(target, containerMemberName, property.PropertyType, container))
                {
                    return true;
                }
            }
        }

        var field = type.GetField(containerMemberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
            var container = field.GetValue(target) ?? CreateContainerInstance(field.FieldType);
            if (TryAssignEnumValueOnObject(container, RenderingModeMemberNames, enumName))
            {
                field.SetValue(target, container);
                return true;
            }
        }

        return false;
    }

    private static bool TryAssignRenderingModeOnObjectByNumericValue(object target, string containerMemberName, long numericValue)
    {
        if (target == null || string.IsNullOrEmpty(containerMemberName))
        {
            return false;
        }

        var type = target.GetType();

        var property = type.GetProperty(containerMemberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null)
        {
            var canRead = property.CanRead;
            var container = canRead ? property.GetValue(target) : null;
            if (container == null)
            {
                container = CreateContainerInstance(property.PropertyType);
            }

            if (TryAssignEnumValueOnObject(container, RenderingModeMemberNames, numericValue))
            {
                if (property.CanWrite)
                {
                    property.SetValue(target, container);
                    return true;
                }

                if (container != null && !property.PropertyType.IsValueType)
                {
                    return true;
                }

                if (TryInvokeContainerSetter(target, containerMemberName, property.PropertyType, container))
                {
                    return true;
                }
            }
        }

        var field = type.GetField(containerMemberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
            var container = field.GetValue(target) ?? CreateContainerInstance(field.FieldType);
            if (TryAssignEnumValueOnObject(container, RenderingModeMemberNames, numericValue))
            {
                field.SetValue(target, container);
                return true;
            }
        }

        return false;
    }

    private static bool TryAssignRenderingModeOnObjectByNumericValue(object target, string containerMemberName, long numericValue)
    {
        if (target == null || string.IsNullOrEmpty(containerMemberName))
        {
            return false;
        }

        var type = target.GetType();

        var property = type.GetProperty(containerMemberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null)
        {
            var canRead = property.CanRead;
            var container = canRead ? property.GetValue(target) : null;
            if (container == null)
            {
                container = CreateContainerInstance(property.PropertyType);
            }

            if (TryAssignEnumValueOnObject(container, RenderingModeMemberName, numericValue))
            {
                if (property.CanWrite)
                {
                    property.SetValue(target, container);
                    return true;
                }

                if (container != null && !property.PropertyType.IsValueType)
                {
                    return true;
                }

                if (TryInvokeContainerSetter(target, containerMemberName, property.PropertyType, container))
                {
                    return true;
                }
            }
        }

        var field = type.GetField(containerMemberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
            var container = field.GetValue(target) ?? CreateContainerInstance(field.FieldType);
            if (TryAssignEnumValueOnObject(container, RenderingModeMemberName, numericValue))
            {
                field.SetValue(target, container);
                return true;
            }
        }

        return false;
    }

    private static bool TryAssignEnumValue(PanelSettings target, string memberName, string enumName)
    {
        if (string.IsNullOrEmpty(enumName))
        {
            return false;
        }

        var type = target.GetType();
        var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.CanWrite)
        {
            var value = CreateEnumValue(property.PropertyType, enumName, memberName);
            property.SetValue(target, value);
            return true;
        }

        var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
            var value = CreateEnumValue(field.FieldType, enumName, memberName);
            field.SetValue(target, value);
            return true;
        }

        return false;
    }

    private static bool TryAssignEnumValue(PanelSettings target, string memberName, long numericValue)
    {
        var type = target.GetType();
        var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.CanWrite && property.PropertyType.IsEnum)
        {
            var value = CreateEnumValue(property.PropertyType, numericValue, memberName);
            property.SetValue(target, value);
            return true;
        }

        var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && field.FieldType.IsEnum)
        {
            var value = CreateEnumValue(field.FieldType, numericValue, memberName);
            field.SetValue(target, value);
            return true;
        }

        return false;
    }

    private static void AssignPanelClearFlags(PanelSettings target, PanelClearFlagsOption option)
    {
        var enumName = GetPanelClearFlagsName(option);
        if (TryAssignEnumValue(target, PanelClearFlagsMemberName, enumName))
        {
            return;
        }

        if (TryAssignEnumValue(target, ClearFlagsMemberName, enumName))
        {
            return;
        }

        if (TryAssignCompositePanelClearFlags(target, enumName))
        {
            return;
        }
    }

    private static bool TryAssignCompositePanelClearFlags(PanelSettings target, string enumName)
    {
        var type = target.GetType();

        var property = type.GetProperty(ClearSettingsMemberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.CanRead && property.CanWrite)
        {
            var settings = property.GetValue(target);
            if (TryAssignPanelClearFlagsOnObject(settings, enumName, out var updated))
            {
                property.SetValue(target, updated);
                return true;
            }
        }

        var field = type.GetField(ClearSettingsMemberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
            var settings = field.GetValue(target);
            if (TryAssignPanelClearFlagsOnObject(settings, enumName, out var updated))
            {
                field.SetValue(target, updated);
                return true;
            }
        }

        return false;
    }

    private static bool TryAssignPanelClearFlagsOnObject(object target, string enumName, out object updated)
    {
        updated = target;
        if (target == null)
        {
            return false;
        }

        if (TryAssignEnumValueOnObject(target, PanelClearFlagsMemberName, enumName) ||
            TryAssignEnumValueOnObject(target, ClearFlagsMemberName, enumName))
        {
            updated = target;
            return true;
        }

        return false;
    }

    private static bool TryAssignRenderingModeThroughAccessorsByName(object target, string containerMemberName, string enumName)
    {
        if (target == null || string.IsNullOrEmpty(containerMemberName) || string.IsNullOrEmpty(enumName))
        {
            return false;
        }

        var type = target.GetType();
        var pascalName = ToPascalCase(containerMemberName);
        if (string.IsNullOrEmpty(pascalName))
        {
            return false;
        }

        var getter = type.GetMethod($"Get{pascalName}", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (getter == null)
        {
            return false;
        }

        var container = getter.Invoke(target, null);
        if (!TryAssignEnumValueOnObject(container, RenderingModeMemberNames, enumName))
        {
            return false;
        }

        return TryInvokeContainerSetter(target, containerMemberName, getter.ReturnType, container);
    }

    private static bool TryAssignRenderingModeThroughAccessorsByNumericValue(object target, string containerMemberName, long numericValue)
    {
        if (target == null || string.IsNullOrEmpty(containerMemberName))
        {
            return false;
        }

        var type = target.GetType();
        var pascalName = ToPascalCase(containerMemberName);
        if (string.IsNullOrEmpty(pascalName))
        {
            return false;
        }

        var getter = type.GetMethod($"Get{pascalName}", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (getter == null)
        {
            return false;
        }

        var container = getter.Invoke(target, null);
        if (!TryAssignEnumValueOnObject(container, RenderingModeMemberNames, numericValue))
        {
            return false;
        }

        return TryInvokeContainerSetter(target, containerMemberName, getter.ReturnType, container);
    }

    private static bool TryAssignRenderingModeThroughAccessorsByNumericValue(object target, string containerMemberName, long numericValue)
    {
        if (target == null || string.IsNullOrEmpty(containerMemberName))
        {
            return false;
        }

        var type = target.GetType();
        var pascalName = ToPascalCase(containerMemberName);
        if (string.IsNullOrEmpty(pascalName))
        {
            return false;
        }

        var getter = type.GetMethod($"Get{pascalName}", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (getter == null)
        {
            return false;
        }

        var container = getter.Invoke(target, null);
        if (!TryAssignEnumValueOnObject(container, RenderingModeMemberName, numericValue))
        {
            return false;
        }

        return TryInvokeContainerSetter(target, containerMemberName, getter.ReturnType, container);
    }

    private static bool TryInvokeContainerSetter(object target, string containerMemberName, Type containerType, object container)
    {
        if (target == null || containerType == null)
        {
            return false;
        }

        var targetType = target.GetType();

        var pascalName = ToPascalCase(containerMemberName);
        if (string.IsNullOrEmpty(pascalName))
        {
            return false;
        }

        var candidateNames = new[]
        {
            $"Set{pascalName}",
            $"Assign{pascalName}",
            $"Apply{pascalName}",
            $"Update{pascalName}"
        };

        foreach (var methodName in candidateNames)
        {
            var method = targetType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { containerType }, null);
            if (method != null)
            {
                method.Invoke(target, new[] { container });
                return true;
            }
        }

        return false;
    }

    private static string ToPascalCase(string memberName)
    {
        if (string.IsNullOrEmpty(memberName))
        {
            return string.Empty;
        }

        if (memberName.Length == 1)
        {
            return memberName.ToUpperInvariant();
        }

        return char.ToUpperInvariant(memberName[0]) + memberName.Substring(1);
    }

    private static object CreateContainerInstance(Type containerType)
    {
        if (containerType == null)
        {
            return null;
        }

        if (containerType.IsValueType)
        {
            return Activator.CreateInstance(containerType);
        }

        var constructor = containerType.GetConstructor(Type.EmptyTypes);
        return constructor != null ? constructor.Invoke(Array.Empty<object>()) : null;
    }

    private static bool IndicatesRenderingMode(string memberName)
    {
        if (string.IsNullOrEmpty(memberName))
        {
            return false;
        }

        var lowered = memberName.ToLowerInvariant();
        if (lowered.Contains("renderingmode") || lowered.Contains("renderermode"))
        {
            return true;
        }

        return lowered.Contains("render") && lowered.Contains("mode");
    }

    private static bool TryAssignEnumValueOnObject(object target, string[] memberNames, string enumName)
    {
        if (target == null || memberNames == null)
        {
            return false;
        }

        foreach (var memberName in memberNames)
        {
            if (TryAssignEnumValueOnObject(target, memberName, enumName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryAssignEnumValueOnObject(object target, string memberName, string enumName)
    {
        if (target == null)
        {
            return false;
        }

        var type = target.GetType();

        var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.CanWrite && property.PropertyType.IsEnum)
        {
            var value = CreateEnumValue(property.PropertyType, enumName, memberName);
            property.SetValue(target, value);
            return true;
        }

        var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && field.FieldType.IsEnum)
        {
            var value = CreateEnumValue(field.FieldType, enumName, memberName);
            field.SetValue(target, value);
            return true;
        }

        return false;
    }

    private static bool TryAssignEnumValueOnObject(object target, string[] memberNames, long numericValue)
    {
        if (target == null || memberNames == null)
        {
            return false;
        }

        foreach (var memberName in memberNames)
        {
            if (TryAssignEnumValueOnObject(target, memberName, numericValue))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryAssignEnumValueOnObject(object target, string memberName, long numericValue)
    {
        if (target == null)
        {
            return false;
        }

        var type = target.GetType();

        var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.CanWrite && property.PropertyType.IsEnum)
        {
            var value = CreateEnumValue(property.PropertyType, numericValue, memberName);
            property.SetValue(target, value);
            return true;
        }

        var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && field.FieldType.IsEnum)
        {
            var value = CreateEnumValue(field.FieldType, numericValue, memberName);
            field.SetValue(target, value);
            return true;
        }

        return false;
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

    private static object CreateEnumValue(Type enumType, long numericValue, string memberName)
    {
        if (!enumType.IsEnum)
        {
            throw new InvalidOperationException($"Member '{memberName}' on '{enumType.FullName}' is not an enum.");
        }

        var value = Enum.ToObject(enumType, numericValue);
        if (!Enum.IsDefined(enumType, value))
        {
            throw new InvalidOperationException($"Value '{numericValue}' is not defined for enum '{enumType.FullName}'.");
        }

        return value;
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
