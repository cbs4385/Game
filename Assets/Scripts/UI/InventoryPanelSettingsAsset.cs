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
    [SerializeField] private PanelSettings.PanelClearFlags panelClearFlags = PanelSettings.PanelClearFlags.DepthStencil;
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
    [SerializeField] private PanelSettings.RuntimePanelRenderingMode renderingMode = PanelSettings.RuntimePanelRenderingMode.Camera;
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
        instance.referenceResolution = referenceResolution;
        instance.referenceDpi = referenceDpi;
        instance.match = match;
        instance.screenMatchMode = screenMatchMode;
        instance.scale = scale;
        instance.targetTexture = targetTexture;
        instance.clearDepthStencil = clearDepthStencil;
        instance.panelClearFlags = panelClearFlags;
        instance.textSettings = textSettings;
        instance.targetDisplay = targetDisplay;
        instance.drawToCameras = drawToCameras;
        instance.viewport = viewport;
        instance.vsync = vsync;
        instance.targetWidth = targetWidth;
        instance.targetHeight = targetHeight;
        instance.sortingOrder = sortingOrder;
        instance.renderingMode = renderingMode;
        instance.vsyncCount = vsyncCount;
        instance.runtimeShader = runtimeShader;
        instance.runtimeWorldSpacePanelSettings = runtimeWorldSpacePanelSettings;
        instance.antiAliasing = antiAliasing;
        instance.pixelsPerUnit = pixelsPerUnit;

        TryAssignOptional(instance, nameof(maxQueuedFrames), maxQueuedFrames);
        TryAssignOptional(instance, nameof(worldSpaceLayer), worldSpaceLayer);
        TryAssignTargetLayerMask(instance, targetLayerMask);

        return instance;
    }

    private static void TryAssignOptional<T>(PanelSettings target, string memberName, T value)
    {
        var type = target.GetType();
        var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.CanWrite && property.PropertyType.IsAssignableFrom(typeof(T)))
        {
            property.SetValue(target, value);
            return;
        }

        var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && field.FieldType.IsAssignableFrom(typeof(T)))
        {
            field.SetValue(target, value);
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
