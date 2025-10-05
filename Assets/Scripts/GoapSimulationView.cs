using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.Execution;
using DataDrivenGoap.World;
using UnityEngine;

using UnityEngine.InputSystem;

[DisallowMultipleComponent]
public sealed class GoapSimulationView : MonoBehaviour
{
    [SerializeField] private GoapSimulationBootstrapper bootstrapper;
    [SerializeField] private Transform pawnContainer;
    [SerializeField] private Camera observerCamera;
    [SerializeField] private PlayerPawnController playerPawnController;
    [SerializeField] private int mapSortingOrder = -100;
    [SerializeField] private int pawnSortingOrder = 0;
    [SerializeField] private int thingSortingOrder = -50;
    [SerializeField, Min(0.01f)] private float thingMarkerScale = 0.6f;
    [SerializeField, Range(0f, 1f)] private float thingMarkerAlpha = 0.9f;
    [SerializeField] private bool showThingMarkers = true;
    [SerializeField] private Vector2 clockScreenOffset = new Vector2(16f, 16f);
    [SerializeField] private Vector2 clockBackgroundPadding = new Vector2(12f, 8f);
    [SerializeField] private Color clockTextColor = Color.white;
    [SerializeField] private Color clockBackgroundColor = new Color(0f, 0f, 0f, 0.65f);
    [SerializeField, Min(1)] private int clockFontSize = 18;
    [SerializeField] private string clockLabelTemplate = "Year {0}, Day {1:D3} — {2:hh\\:mm\\:ss}";
    [SerializeField] private Vector2 selectedPawnPanelOffset = new Vector2(16f, 80f);
    [SerializeField, Min(16f)] private float selectedPawnPanelWidth = 320f;
    [SerializeField] private Vector2 selectedPawnPanelPadding = new Vector2(12f, 12f);
    [SerializeField] private Color selectedPawnPanelTextColor = Color.white;
    [SerializeField] private Color selectedPawnPanelBackgroundColor = new Color(0f, 0f, 0f, 0.75f);
    [SerializeField, Min(1)] private int selectedPawnPanelFontSize = 14;
    [SerializeField] private string selectedPawnPanelNeedsHeader = "Needs";
    [SerializeField] private string selectedPawnPanelPlanHeader = "Plan";
    [SerializeField, Min(0.01f)] private float minOrthographicSize = 2.5f;
    [SerializeField, Min(0.01f)] private float maxOrthographicSize = 40f;
    [SerializeField, Min(0.01f)] private float orthographicZoomStep = 1.5f;
    [SerializeField, Range(1f, 179f)] private float minPerspectiveFieldOfView = 25f;
    [SerializeField, Range(1f, 179f)] private float maxPerspectiveFieldOfView = 80f;
    [SerializeField, Min(0.01f)] private float perspectiveZoomStep = 10f;

    private static readonly Color BuildingTintColor = new Color(0.75f, 0.24f, 0.24f, 1f);
    private const float BuildingTintBlend = 0.65f;

    private static readonly IReadOnlyDictionary<string, string> ThingIconManifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["altar"] = "/Sprites/Activities/activity_interact.png",
        ["bakery_display"] = "/Sprites/Activities/activity_bakebread.png",
        ["bed"] = "/Sprites/Activities/activity_rest.png",
        ["bookcase"] = "/Sprites/Activities/activity_leisure.png",
        ["bookshelf"] = "/Sprites/Activities/activity_leisure.png",
        ["cabinet"] = "/Sprites/Activities/activity_work.png",
        ["chair"] = "/Sprites/Activities/activity_leisure.png",
        ["chalkboard"] = "/Sprites/Activities/activity_work.png",
        ["counter"] = "/Sprites/Activities/activity_work.png",
        ["desk"] = "/Sprites/Activities/activity_work.png",
        ["display_counter"] = "/Sprites/Activities/activity_work.png",
        ["lectern"] = "/Sprites/Activities/activity_interact.png",
        ["meeting_table"] = "/Sprites/Activities/activity_chat.png",
        ["oven"] = "/Sprites/Activities/cooking.png",
        ["pantry"] = "/Sprites/Activities/activity_eat.png",
        ["pew"] = "/Sprites/Activities/activity_rest.png",
        ["prep_table"] = "/Sprites/Activities/cooking.png",
        ["reading_table"] = "/Sprites/Activities/activity_leisure.png",
        ["register"] = "/Sprites/Activities/activity_work.png",
        ["service_bell"] = "/Sprites/Activities/activity_work.png",
        ["shrine"] = "/Sprites/Activities/activity_quest.png",
        ["stool"] = "/Sprites/Activities/activity_leisure.png",
        ["storage_crate"] = "/Sprites/Activities/activity_work.png",
        ["store_shelf"] = "/Sprites/Activities/activity_work.png",
        ["stove"] = "/Sprites/Activities/cooking.png",
        ["table"] = "/Sprites/Activities/activity_chat.png",
        ["teacher_desk"] = "/Sprites/Activities/activity_work.png",
        ["wardrobe"] = "/Sprites/Activities/activity_work.png",
        ["washbasin"] = "/Sprites/Activities/activity_wait.png",
        ["weapon"] = "/Sprites/Activities/activity_attack.png"
    };

    private readonly Dictionary<ThingId, PawnVisual> _pawnVisuals = new Dictionary<ThingId, PawnVisual>();
    private readonly Dictionary<ThingId, ThingVisual> _thingVisuals = new Dictionary<ThingId, ThingVisual>();
    private readonly Dictionary<ThingId, GridPos> _pawnPreviousGridPositions = new Dictionary<ThingId, GridPos>();
    private readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> _textureCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Sprite> _thingSpriteCache = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> _thingTextureCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _pawnSpritePaths = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    private readonly GUIContent _clockGuiContent = new GUIContent();
    private readonly GUIContent _pawnUpdateGuiContent = new GUIContent();
    private readonly GUIContent _selectedPawnGuiContent = new GUIContent();

    private ShardedWorld _world;
    private IReadOnlyList<(ThingId Id, VillagePawn Pawn)> _actors;
    private IReadOnlyDictionary<ThingId, ActorHostDiagnostics> _actorDiagnostics;
    private string _datasetRoot;
    private GameObject _mapObject;
    private Sprite _mapSprite;
    private Texture2D _mapTexture;
    private bool _ownsMapTexture;
    private GoapSimulationBootstrapper.SimulationReadyEventArgs.TileClassificationSnapshot _tileClassification;
    private Transform _pawnRoot;
    private Transform _thingRoot;
    private WorldClock _clock;
    private string _clockLabel = string.Empty;
    private string _pawnUpdateLabel = string.Empty;
    private GUIStyle _clockStyle;
    private GUIStyle _selectedPawnPanelStyle;
    private ThingId? _selectedPawnId;
    private readonly Dictionary<ThingId, VillagePawn> _pawnDefinitions = new Dictionary<ThingId, VillagePawn>();
    private readonly HashSet<ThingId> _manualPawnIds = new HashSet<ThingId>();
    private readonly StringBuilder _selectedPawnPanelBuilder = new StringBuilder();
    private readonly List<(string Label, double? Value)> _selectedPawnNeeds = new List<(string Label, double? Value)>();
    private string[] _selectedPawnPlanSteps = Array.Empty<string>();
    private ActorPlanStatus _selectedPawnPlanStatus;
    private readonly List<PlanActionOption> _selectedPawnPlanOptions = new List<PlanActionOption>();
    private int? _selectedPlanOptionIndex;
    private string _selectedPlanOptionLabel = string.Empty;
    private string[] _needAttributeNames = Array.Empty<string>();
    private string _selectedPawnPanelText = string.Empty;
    private string _selectedPawnName = string.Empty;
    private string _selectedPawnRole = string.Empty;
    private GridPos? _selectedPawnGridPosition;
    private string _selectedPawnPlanGoal = string.Empty;
    private string _selectedPawnPlanState = string.Empty;
    private string _selectedPawnPlanCurrentStep = string.Empty;
    private DateTime _selectedPawnPlanUpdatedUtc;
    private float _targetOrthographicSize;
    private float _targetPerspectiveFieldOfView;
    private bool _zoomInitialized;
    private UnityEngine.Rendering.Universal.PixelPerfectCamera _pixelPerfectCamera;
    private bool _showOnlySelectedPawn;
    private bool _selectedPawnVisibilityDirty;
    private readonly HashSet<ThingId> _thingUpdateScratch = new HashSet<ThingId>();
    private readonly List<ThingId> _thingRemovalScratch = new List<ThingId>();

    private void Awake()
    {
        EnsureBootstrapperReference();
        EnsureObserverCamera();
    }

    private void OnEnable()
    {
        EnsureBootstrapperReference();
        EnsureObserverCamera();
        bootstrapper.Bootstrapped += HandleBootstrapped;
        if (bootstrapper.HasBootstrapped && _world == null)
        {
            HandleBootstrapped(bootstrapper, bootstrapper.LatestBootstrap);
        }
    }

    private void OnDisable()
    {
        if (bootstrapper != null)
        {
            bootstrapper.Bootstrapped -= HandleBootstrapped;
        }
    }

    private void OnDestroy()
    {
        if (bootstrapper != null)
        {
            bootstrapper.Bootstrapped -= HandleBootstrapped;
        }
        DisposeVisuals();
    }

    private void Update()
    {
        UpdateClockDisplay();

        if (_world == null)
        {
            ClearPawnUpdateLabel();
            ClearSelectedPawnInfo();
            return;
        }

        UpdatePawnDiagnosticsLabel();

        var snapshot = _world.Snap();
        EnsureThingVisuals(snapshot);
        var clickedPawnId = DetectClickedPawn(snapshot);
        if (clickedPawnId.HasValue)
        {
            if (!_selectedPawnId.HasValue || !_selectedPawnId.Value.Equals(clickedPawnId.Value))
            {
                SetSelectedPawnId(clickedPawnId, suppressImmediateVisibilityUpdate: false);
            }
        }
        ThingView selectedThing = null;
        foreach (var entry in _pawnVisuals)
        {
            var thing = snapshot.GetThing(entry.Key);
            if (thing == null)
            {
                throw new InvalidOperationException($"World snapshot no longer contains actor '{entry.Key.Value}'.");
            }

            if (!_pawnPreviousGridPositions.TryGetValue(entry.Key, out var previousGridPosition))
            {
                throw new InvalidOperationException(
                    $"Previous grid position for pawn '{entry.Key.Value}' is missing; direction tracking cannot continue.");
            }

            var currentGridPosition = thing.Position;
            var orientationKey = DetermineOrientationKey(previousGridPosition, currentGridPosition);
            if (orientationKey != null)
            {
                var spritePath = ResolveOrientationSpritePath(entry.Value, orientationKey);
                var sprite = LoadSpriteAsset(spritePath);
                if (sprite == null)
                {
                    throw new InvalidOperationException(
                        $"Sprite asset loader returned null for pawn '{entry.Value.PawnId}' orientation '{orientationKey}'.");
                }

                entry.Value.Renderer.sprite = sprite;
            }

            UpdatePawnPosition(entry.Value, currentGridPosition);
            _pawnPreviousGridPositions[entry.Key] = currentGridPosition;

            if (_selectedPawnId != null && entry.Key.Equals(_selectedPawnId.Value))
            {
                selectedThing = thing;
            }
        }

        UpdateObserverCamera(snapshot);
        UpdateSelectedPawnInfo(selectedThing, snapshot);
        TryApplySelectedPawnVisibility();
    }

    private void HandleBootstrapped(object sender, GoapSimulationBootstrapper.SimulationReadyEventArgs args)
    {
        if (args == null)
        {
            throw new ArgumentNullException(nameof(args));
        }

        EnsureObserverCamera();
        EnsurePlayerPawnController();

        if (_world != null)
        {
            DisposeVisuals();
        }

        _pawnPreviousGridPositions.Clear();

        _world = args.World ?? throw new InvalidOperationException("Bootstrapper emitted a null world instance.");
        _actors = args.ActorDefinitions ?? throw new InvalidOperationException("Bootstrapper emitted null actor definitions.");
        _actorDiagnostics = args.ActorDiagnostics ?? throw new InvalidOperationException("Bootstrapper emitted null actor diagnostics.");
        _manualPawnIds.Clear();
        if (args.ManualPawnIds != null)
        {
            foreach (var manualId in args.ManualPawnIds)
            {
                _manualPawnIds.Add(manualId);
            }
        }
        _datasetRoot = args.DatasetRoot ?? throw new InvalidOperationException("Bootstrapper emitted a null dataset root path.");
        _clock = args.Clock ?? throw new InvalidOperationException("Bootstrapper emitted a null world clock instance.");
        _tileClassification = args.TileClassification ?? throw new InvalidOperationException("Bootstrapper emitted a null tile classification snapshot.");
        _showOnlySelectedPawn = args.ShowOnlySelectedPawn;
        var parsedSelectedPawnId = ParseSelectedPawnId(args.CameraPawnId);
        if (parsedSelectedPawnId == null)
        {
            throw new InvalidOperationException(
                "Demo configuration must define observer.cameraPawn so the observer camera can track a pawn.");
        }

        SetSelectedPawnId(parsedSelectedPawnId, suppressImmediateVisibilityUpdate: true);

        _pawnDefinitions.Clear();
        foreach (var actor in _actors)
        {
            if (actor.Pawn != null)
            {
                _pawnDefinitions[actor.Id] = actor.Pawn;
            }
        }

        var needNames = bootstrapper?.NeedAttributeNames;
        if (needNames != null && needNames.Count > 0)
        {
            var copy = new string[needNames.Count];
            for (int i = 0; i < needNames.Count; i++)
            {
                copy[i] = needNames[i];
            }
            _needAttributeNames = copy;
        }
        else
        {
            _needAttributeNames = Array.Empty<string>();
        }

        EnsurePawnContainer();
        if (showThingMarkers)
        {
            EnsureThingRoot();
        }
        else
        {
            ClearThingVisuals();
        }
        LoadSpriteManifest(Path.Combine(_datasetRoot, "sprites_manifest.json"));

        var snapshot = _world.Snap();
        LoadMap(args.MapTexture, _tileClassification, snapshot.Width, snapshot.Height);
        EnsureThingVisuals(snapshot);
        CreatePawnVisuals(snapshot);
        ValidateSelectedPawnPresence();
        TryApplySelectedPawnVisibility();
        UpdateObserverCamera(snapshot);
        UpdateClockDisplay();
        UpdatePawnDiagnosticsLabel();
        var selectedThing = _selectedPawnId != null ? snapshot.GetThing(_selectedPawnId.Value) : null;
        UpdateSelectedPawnInfo(selectedThing, snapshot);
    }

    private void UpdateClockDisplay()
    {
        if (_clock == null)
        {
            _clockLabel = string.Empty;
            _clockGuiContent.text = string.Empty;
            return;
        }

        if (string.IsNullOrWhiteSpace(clockLabelTemplate))
        {
            throw new InvalidOperationException("Clock label template must be a non-empty string.");
        }

        var snapshot = _clock.Snapshot();
        var formatted = string.Format(
            CultureInfo.InvariantCulture,
            clockLabelTemplate,
            snapshot.Year,
            snapshot.DayOfYear,
            snapshot.TimeOfDay);
        _clockLabel = formatted;
        _clockGuiContent.text = formatted;
    }

    private void ClearPawnUpdateLabel()
    {
        _pawnUpdateLabel = string.Empty;
        _pawnUpdateGuiContent.text = string.Empty;
    }

    private void UpdatePawnDiagnosticsLabel()
    {
        if (_selectedPawnId == null || _actorDiagnostics == null)
        {
            ClearPawnUpdateLabel();
            return;
        }

        var selectedId = _selectedPawnId.Value;
        if (_manualPawnIds.Contains(selectedId))
        {
            _pawnUpdateLabel = "Manual control";
            _pawnUpdateGuiContent.text = _pawnUpdateLabel;
            return;
        }

        if (!_actorDiagnostics.TryGetValue(selectedId, out var diagnostics) || diagnostics == null)
        {
            throw new InvalidOperationException($"Diagnostics for pawn '{selectedId.Value}' are missing.");
        }

        var updateCount = diagnostics.UpdateCount;
        var deltaSeconds = diagnostics.LastUpdateDeltaSeconds;
        var lastUpdateUtc = diagnostics.LastUpdateUtc;

        string label;
        if (updateCount < 2 || !double.IsFinite(deltaSeconds) || deltaSeconds < 0d)
        {
            label = "Δt: collecting…";
        }
        else
        {
            label = string.Format(CultureInfo.InvariantCulture, "Δt: {0:0.00}s", deltaSeconds);
        }

        if (lastUpdateUtc != DateTime.MinValue)
        {
            label += string.Format(CultureInfo.InvariantCulture, " @ {0:HH:mm:ss} UTC", lastUpdateUtc);
        }

        _pawnUpdateLabel = label;
        _pawnUpdateGuiContent.text = label;
    }

    private void EnsureThingVisuals(IWorldSnapshot snapshot)
    {
        if (!showThingMarkers)
        {
            if (_thingVisuals.Count > 0)
            {
                ClearThingVisuals();
            }

            _thingUpdateScratch.Clear();
            _thingRemovalScratch.Clear();
            return;
        }

        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        EnsureThingRoot();

        _thingUpdateScratch.Clear();

        foreach (var thing in snapshot.AllThings())
        {
            if (thing == null)
            {
                throw new InvalidOperationException("World snapshot returned a null thing entry while synchronizing visuals.");
            }

            if (_pawnDefinitions.ContainsKey(thing.Id))
            {
                continue;
            }

            _thingUpdateScratch.Add(thing.Id);

            if (!_thingVisuals.TryGetValue(thing.Id, out var visual))
            {
                visual = CreateThingVisual(thing);
                _thingVisuals[thing.Id] = visual;
            }

            var scale = Mathf.Max(thingMarkerScale, 0.01f);
            visual.Root.localScale = new Vector3(scale, scale, 1f);
            UpdateThingPosition(visual, thing.Position);
        }

        if (_thingVisuals.Count > _thingUpdateScratch.Count)
        {
            _thingRemovalScratch.Clear();
            foreach (var entry in _thingVisuals)
            {
                if (!_thingUpdateScratch.Contains(entry.Key))
                {
                    _thingRemovalScratch.Add(entry.Key);
                }
            }

            for (int i = 0; i < _thingRemovalScratch.Count; i++)
            {
                var id = _thingRemovalScratch[i];
                if (_thingVisuals.TryGetValue(id, out var visual) && visual?.Root != null)
                {
                    Destroy(visual.Root.gameObject);
                }

                _thingVisuals.Remove(id);
            }

            _thingRemovalScratch.Clear();
        }

        _thingUpdateScratch.Clear();
    }

    private void CreatePawnVisuals(IWorldSnapshot snapshot)
    {
        foreach (var actor in _actors)
        {
            if (actor.Pawn == null)
            {
                throw new InvalidDataException($"Actor '{actor.Id.Value}' is missing pawn metadata in the dataset.");
            }

            var pawnId = actor.Pawn.id?.Trim();
            if (string.IsNullOrWhiteSpace(pawnId))
            {
                throw new InvalidDataException($"Actor '{actor.Id.Value}' has an empty pawn id.");
            }

            if (_pawnVisuals.ContainsKey(actor.Id))
            {
                throw new InvalidOperationException($"Duplicate actor id '{actor.Id.Value}' detected while constructing visuals.");
            }

            var thing = snapshot.GetThing(actor.Id);
            if (thing == null)
            {
                throw new InvalidOperationException($"World snapshot does not contain actor '{actor.Id.Value}' during initialization.");
            }

            if (!_pawnSpritePaths.TryGetValue(pawnId, out var spritePaths))
            {
                throw new InvalidDataException($"Sprite manifest is missing an entry for pawn '{pawnId}'.");
            }

            if (!spritePaths.TryGetValue("south", out var defaultSpritePath))
            {
                throw new InvalidDataException($"Sprite manifest entry for pawn '{pawnId}' does not define a 'south' orientation sprite.");
            }

            var sprite = LoadSpriteAsset(defaultSpritePath);
            var pawnObject = new GameObject($"Pawn_{pawnId}");
            pawnObject.transform.SetParent(_pawnRoot, false);
            var renderer = pawnObject.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.sortingOrder = pawnSortingOrder;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var visual = new PawnVisual(pawnObject.transform, renderer, spritePaths, pawnId);
            _pawnVisuals.Add(actor.Id, visual);
            UpdatePawnPosition(visual, thing.Position);
            _pawnPreviousGridPositions[actor.Id] = thing.Position;
        }
    }

    private void ValidateSelectedPawnPresence()
    {
        if (_selectedPawnId == null)
        {
            return;
        }

        var selectedId = _selectedPawnId.Value;
        if (!_pawnVisuals.ContainsKey(selectedId))
        {
            throw new InvalidOperationException($"Observer requested camera pawn '{selectedId.Value}' but no matching pawn was initialized.");
        }
    }

    private void UpdatePawnPosition(PawnVisual visual, GridPos position)
    {
        var translated = new Vector3(position.X + 0.5f, position.Y + 0.5f, 0f);
        visual.Root.localPosition = translated;
    }

    private void UpdateThingPosition(ThingVisual visual, GridPos position)
    {
        if (visual == null)
        {
            throw new ArgumentNullException(nameof(visual));
        }

        var translated = new Vector3(position.X + 0.5f, position.Y + 0.5f, 0f);
        visual.Root.localPosition = translated;
    }

    private ThingVisual CreateThingVisual(ThingView thing)
    {
        if (thing == null)
        {
            throw new ArgumentNullException(nameof(thing));
        }

        if (_thingRoot == null)
        {
            throw new InvalidOperationException("Thing root must be initialized before creating thing visuals.");
        }

        var type = thing.Type?.Trim();
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new InvalidDataException($"Thing '{thing.Id.Value}' is missing a type definition in the world snapshot.");
        }

        var sprite = GetOrCreateThingSprite(type);
        var go = new GameObject($"Thing_{thing.Id.Value}");
        go.transform.SetParent(_thingRoot, false);
        var scale = Mathf.Max(thingMarkerScale, 0.01f);
        go.transform.localScale = new Vector3(scale, scale, 1f);
        var renderer = go.AddComponent<SpriteRenderer>();
        renderer.sprite = sprite;
        renderer.sortingOrder = thingSortingOrder;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return new ThingVisual(go.transform, renderer, type);
    }

    private void UpdateObserverCamera(IWorldSnapshot snapshot)
    {
        if (_selectedPawnId == null)
        {
            return;
        }

        EnsureObserverCamera();

        if (observerCamera == null)
        {
            throw new InvalidOperationException("Observer camera reference has not been configured for GoapSimulationView.");
        }

        var selectedId = _selectedPawnId.Value;
        var thing = snapshot.GetThing(selectedId);
        if (thing == null)
        {
            throw new InvalidOperationException($"World snapshot no longer contains the selected pawn '{selectedId.Value}'.");
        }

        if (!_pawnVisuals.TryGetValue(selectedId, out var visual))
        {
            throw new InvalidOperationException($"Visual representation for selected pawn '{selectedId.Value}' is missing.");
        }

        ApplyCameraZoom();

        var cameraTransform = observerCamera.transform;
        var currentZ = cameraTransform.position.z;
        var pawnWorldPosition = visual.Root.position;
        var target = new Vector3(pawnWorldPosition.x, pawnWorldPosition.y, currentZ);
        cameraTransform.position = target;
    }

    private void ApplyCameraZoom()
    {
        EnsureObserverCamera();

        var scrollDelta = ReadMouseScrollDelta();
        if (Mathf.Approximately(scrollDelta, 0f))
        {
            return;
        }

        if (observerCamera.orthographic)
        {
            var minSize = Mathf.Max(0.01f, minOrthographicSize);
            var maxSize = Mathf.Max(minSize, maxOrthographicSize);
            var step = Mathf.Max(0.01f, orthographicZoomStep);
            _targetOrthographicSize = Mathf.Clamp(
                _targetOrthographicSize - scrollDelta * step,
                minSize,
                maxSize);
            observerCamera.orthographicSize = _targetOrthographicSize;
            ApplyPixelPerfectZoom(_targetOrthographicSize);
        }
        else
        {
            var minFov = Mathf.Clamp(minPerspectiveFieldOfView, 1f, 179f);
            var maxFov = Mathf.Clamp(Mathf.Max(minFov, maxPerspectiveFieldOfView), 1f, 179f);
            var step = Mathf.Max(0.01f, perspectiveZoomStep);
            _targetPerspectiveFieldOfView = Mathf.Clamp(
                _targetPerspectiveFieldOfView - scrollDelta * step,
                minFov,
                maxFov);
            observerCamera.fieldOfView = _targetPerspectiveFieldOfView;
        }
    }

    private void InitializeCameraZoomTargets()
    {
        if (observerCamera == null)
        {
            throw new InvalidOperationException("Observer camera reference has not been configured for GoapSimulationView.");
        }

        if (observerCamera.orthographic)
        {
            var minSize = Mathf.Max(0.01f, minOrthographicSize);
            var maxSize = Mathf.Max(minSize, maxOrthographicSize);
            _targetOrthographicSize = Mathf.Clamp(observerCamera.orthographicSize, minSize, maxSize);
            observerCamera.orthographicSize = _targetOrthographicSize;
            ApplyPixelPerfectZoom(_targetOrthographicSize);
        }
        else
        {
            var minFov = Mathf.Clamp(minPerspectiveFieldOfView, 1f, 179f);
            var maxFov = Mathf.Clamp(Mathf.Max(minFov, maxPerspectiveFieldOfView), 1f, 179f);
            _targetPerspectiveFieldOfView = Mathf.Clamp(observerCamera.fieldOfView, minFov, maxFov);
            observerCamera.fieldOfView = _targetPerspectiveFieldOfView;
        }

        _zoomInitialized = true;
    }

    private void ApplyPixelPerfectZoom(float orthographicSize)
    {
        if (_pixelPerfectCamera == null)
        {
            return;
        }

        if (orthographicSize <= 0f)
        {
            throw new InvalidOperationException("Orthographic size must be positive to compute pixel-perfect zoom parameters.");
        }

        if (_pixelPerfectCamera.refResolutionY <= 0)
        {
            throw new InvalidOperationException("PixelPerfectCamera.refResolutionY must be positive to compute assets PPU.");
        }

        var denominator = 2f * orthographicSize;
        var referenceResolutionY = (float)_pixelPerfectCamera.refResolutionY;
        var assetsPerUnit = Mathf.Max(1, Mathf.RoundToInt(referenceResolutionY / denominator));
        _pixelPerfectCamera.assetsPPU = assetsPerUnit;
    }

    private static float ReadMouseScrollDelta()
    {
        var mouse = Mouse.current;
        if (mouse == null)
        {
            return 0f;
        }

        var scroll = mouse.scroll.ReadValue().y;
        if (float.IsNaN(scroll) || float.IsInfinity(scroll))
        {
            throw new InvalidOperationException("Mouse scroll produced a non-finite value.");
        }

        if (Mathf.Approximately(scroll, 0f))
        {
            return 0f;
        }

        const float standardScrollStep = 120f;
        if (Mathf.Abs(scroll) >= standardScrollStep)
        {
            return scroll / standardScrollStep;
        }

        return scroll;
    }

    private void OnGUI()
    {
        bool hasClock = !string.IsNullOrEmpty(_clockLabel);
        bool hasPawnUpdate = !string.IsNullOrEmpty(_pawnUpdateLabel);

        if (hasClock || hasPawnUpdate)
        {
            RenderClockLabel(hasClock, hasPawnUpdate);
        }

        if (RenderSelectedPawnPanel(out var panelRect))
        {
            RenderPlanControls(panelRect);
        }
    }

    private void RenderClockLabel(bool hasClock, bool hasPawnUpdate)
    {
        if (!hasClock && !hasPawnUpdate)
        {
            return;
        }

        EnsureClockStyle();

        var x = clockScreenOffset.x;
        var y = clockScreenOffset.y;
        var clockSize = hasClock ? _clockStyle.CalcSize(_clockGuiContent) : Vector2.zero;
        var pawnSize = hasPawnUpdate ? _clockStyle.CalcSize(_pawnUpdateGuiContent) : Vector2.zero;
        const float lineSpacing = 4f;
        float spacing = (hasClock && hasPawnUpdate) ? lineSpacing : 0f;
        float width = Mathf.Max(clockSize.x, pawnSize.x);
        float height = clockSize.y + pawnSize.y + spacing;

        if (width <= 0f && height <= 0f)
        {
            return;
        }

        var labelRect = new Rect(x, y, Mathf.Max(0f, width), Mathf.Max(0f, height));
        var paddingX = Mathf.Max(0f, clockBackgroundPadding.x);
        var paddingY = Mathf.Max(0f, clockBackgroundPadding.y);
        if (clockBackgroundColor.a > 0f && Texture2D.whiteTexture != null)
        {
            var halfPadX = paddingX * 0.5f;
            var halfPadY = paddingY * 0.5f;
            var backgroundRect = new Rect(
                labelRect.x - halfPadX,
                labelRect.y - halfPadY,
                labelRect.width + paddingX,
                labelRect.height + paddingY);

            var previousColor = GUI.color;
            GUI.color = clockBackgroundColor;
            GUI.DrawTexture(backgroundRect, Texture2D.whiteTexture);
            GUI.color = previousColor;
        }

        float currentY = y;
        if (hasClock)
        {
            var clockRect = new Rect(x, currentY, labelRect.width, clockSize.y);
            GUI.Label(clockRect, _clockGuiContent, _clockStyle);
            currentY += clockSize.y + spacing;
        }

        if (hasPawnUpdate)
        {
            var pawnRect = new Rect(x, currentY, labelRect.width, pawnSize.y);
            GUI.Label(pawnRect, _pawnUpdateGuiContent, _clockStyle);
        }
    }

    private void EnsureClockStyle()
    {
        var desiredFontSize = Mathf.Max(1, clockFontSize);
        if (_clockStyle == null)
        {
            _clockStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperLeft,
                wordWrap = false,
                richText = false,
                padding = new RectOffset(0, 0, 0, 0)
            };
        }

        if (_clockStyle.fontSize != desiredFontSize)
        {
            _clockStyle.fontSize = desiredFontSize;
        }

        _clockStyle.normal.textColor = clockTextColor;
    }

    private bool RenderSelectedPawnPanel(out Rect panelRect)
    {
        if (string.IsNullOrEmpty(_selectedPawnPanelText))
        {
            panelRect = default;
            return false;
        }

        EnsureSelectedPawnPanelStyle();

        var width = Mathf.Max(16f, selectedPawnPanelWidth);
        var content = _selectedPawnGuiContent;
        content.text = _selectedPawnPanelText;
        var height = _selectedPawnPanelStyle.CalcHeight(content, width);
        panelRect = new Rect(selectedPawnPanelOffset.x, selectedPawnPanelOffset.y, width, Mathf.Max(0f, height));

        if (selectedPawnPanelBackgroundColor.a > 0f && Texture2D.whiteTexture != null)
        {
            var padX = Mathf.Max(0f, selectedPawnPanelPadding.x);
            var padY = Mathf.Max(0f, selectedPawnPanelPadding.y);
            var backgroundRect = new Rect(
                panelRect.x - padX * 0.5f,
                panelRect.y - padY * 0.5f,
                panelRect.width + padX,
                panelRect.height + padY);
            var previous = GUI.color;
            GUI.color = selectedPawnPanelBackgroundColor;
            GUI.DrawTexture(backgroundRect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        GUI.Label(panelRect, content, _selectedPawnPanelStyle);
        return true;
    }

    private void RenderPlanControls(Rect panelRect)
    {
        if (_selectedPawnId == null)
        {
            return;
        }

        var selectedId = _selectedPawnId.Value;
        if (!_manualPawnIds.Contains(selectedId))
        {
            return;
        }

        if (_selectedPawnPlanOptions.Count == 0)
        {
            return;
        }

        float startY = panelRect.yMax + Mathf.Max(8f, selectedPawnPanelPadding.y);
        float width = panelRect.width;
        const float buttonHeight = 26f;
        const float spacing = 4f;

        for (int i = 0; i < _selectedPawnPlanOptions.Count; i++)
        {
            var option = _selectedPawnPlanOptions[i];
            var buttonRect = new Rect(panelRect.x, startY + i * (buttonHeight + spacing), width, buttonHeight);
            var previousColor = GUI.backgroundColor;
            var previousEnabled = GUI.enabled;

            bool isSelected = _selectedPlanOptionIndex.HasValue && option.StepIndex == _selectedPlanOptionIndex.Value;
            if (isSelected)
            {
                GUI.backgroundColor = Color.Lerp(Color.white, Color.cyan, 0.5f);
            }

            GUI.enabled = option.IsActionable;
            if (GUI.Button(buttonRect, option.Label))
            {
                HandlePlanOptionInvoked(option);
            }

            GUI.enabled = previousEnabled;
            GUI.backgroundColor = previousColor;
        }
    }

    private void EnsureSelectedPawnPanelStyle()
    {
        var desiredFontSize = Mathf.Max(1, selectedPawnPanelFontSize);
        if (_selectedPawnPanelStyle == null)
        {
            _selectedPawnPanelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperLeft,
                wordWrap = true,
                richText = false,
                padding = new RectOffset(0, 0, 0, 0)
            };
        }

        if (_selectedPawnPanelStyle.fontSize != desiredFontSize)
        {
            _selectedPawnPanelStyle.fontSize = desiredFontSize;
        }

        _selectedPawnPanelStyle.normal.textColor = selectedPawnPanelTextColor;
    }

    private void UpdateSelectedPawnInfo(ThingView selectedThing, IWorldSnapshot snapshot)
    {
        if (_selectedPawnId == null)
        {
            ClearSelectedPawnInfo();
            return;
        }

        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var selectedId = _selectedPawnId.Value;
        if (!_pawnDefinitions.TryGetValue(selectedId, out var pawn) || pawn == null)
        {
            _selectedPawnName = selectedId.Value ?? string.Empty;
            _selectedPawnRole = string.Empty;
        }
        else
        {
            var rawName = pawn.name?.Trim();
            _selectedPawnName = string.IsNullOrEmpty(rawName) ? (selectedId.Value ?? string.Empty) : rawName;
            _selectedPawnRole = pawn.role?.Trim() ?? string.Empty;
        }

        _selectedPawnGridPosition = selectedThing?.Position;

        PopulateSelectedPawnNeeds(selectedThing);
        PopulateSelectedPawnPlan(selectedId, snapshot);
        _selectedPawnPanelText = ComposeSelectedPawnPanelText(selectedId);
    }

    private void PopulateSelectedPawnNeeds(ThingView selectedThing)
    {
        _selectedPawnNeeds.Clear();
        if (selectedThing?.Attributes == null)
        {
            return;
        }

        if (_needAttributeNames.Length > 0)
        {
            foreach (var attributeName in _needAttributeNames)
            {
                if (string.IsNullOrWhiteSpace(attributeName))
                {
                    continue;
                }

                if (selectedThing.Attributes.TryGetValue(attributeName, out var value))
                {
                    _selectedPawnNeeds.Add((attributeName, value));
                }
                else
                {
                    _selectedPawnNeeds.Add((attributeName, null));
                }
            }
        }
        else
        {
            foreach (var entry in selectedThing.Attributes.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                _selectedPawnNeeds.Add((entry.Key, entry.Value));
            }
        }
    }

    private void PopulateSelectedPawnPlan(ThingId selectedId, IWorldSnapshot snapshot)
    {
        _selectedPawnPlanOptions.Clear();
        _selectedPawnPlanStatus = null;

        if (bootstrapper != null && bootstrapper.TryGetActorPlanStatus(selectedId, out var status) && status != null)
        {
            _selectedPawnPlanStatus = status;
            _selectedPawnPlanGoal = status.GoalId ?? string.Empty;
            _selectedPawnPlanState = HumanizeIdentifier(status.State);
            _selectedPawnPlanCurrentStep = status.CurrentStep ?? string.Empty;
            _selectedPawnPlanUpdatedUtc = status.UpdatedUtc;
            if (status.Steps != null)
            {
                var steps = new List<string>(status.Steps.Count);
                for (int i = 0; i < status.Steps.Count; i++)
                {
                    var step = status.Steps[i];
                    if (string.IsNullOrWhiteSpace(step))
                    {
                        continue;
                    }

                    var trimmed = step.Trim();
                    steps.Add(trimmed);
                    var option = BuildPlanActionOption(trimmed, snapshot, i);
                    _selectedPawnPlanOptions.Add(option);
                }

                _selectedPawnPlanSteps = steps.ToArray();
            }
            else
            {
                _selectedPawnPlanSteps = Array.Empty<string>();
            }
        }
        else
        {
            _selectedPawnPlanGoal = string.Empty;
            _selectedPawnPlanState = string.Empty;
            _selectedPawnPlanCurrentStep = string.Empty;
            _selectedPawnPlanUpdatedUtc = default;
            _selectedPawnPlanSteps = Array.Empty<string>();
        }

        if (_selectedPlanOptionIndex.HasValue)
        {
            bool stillPresent = _selectedPawnPlanOptions.Any(option => option.StepIndex == _selectedPlanOptionIndex.Value);
            if (!stillPresent)
            {
                _selectedPlanOptionIndex = null;
                _selectedPlanOptionLabel = string.Empty;
            }
        }
    }

    private string ComposeSelectedPawnPanelText(ThingId selectedId)
    {
        var builder = _selectedPawnPanelBuilder;
        builder.Clear();

        var displayId = selectedId.Value ?? string.Empty;
        var displayName = string.IsNullOrEmpty(_selectedPawnName) ? displayId : _selectedPawnName;
        builder.Append(displayName);
        if (!string.IsNullOrEmpty(displayId) && !string.Equals(displayName, displayId, StringComparison.Ordinal))
        {
            builder.Append(" (").Append(displayId).Append(')');
        }
        builder.AppendLine();

        if (!string.IsNullOrEmpty(_selectedPawnRole))
        {
            builder.Append("Role: ").Append(HumanizeIdentifier(_selectedPawnRole)).AppendLine();
        }

        if (_selectedPawnGridPosition.HasValue)
        {
            var pos = _selectedPawnGridPosition.Value;
            builder.Append("Position: (").Append(pos.X).Append(", ").Append(pos.Y).Append(')').AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine(string.IsNullOrWhiteSpace(selectedPawnPanelNeedsHeader) ? "Needs" : selectedPawnPanelNeedsHeader);
        if (_selectedPawnNeeds.Count == 0)
        {
            builder.AppendLine("  <none>");
        }
        else
        {
            foreach (var entry in _selectedPawnNeeds)
            {
                var label = HumanizeIdentifier(entry.Label);
                builder.Append("  ").Append(string.IsNullOrEmpty(label) ? entry.Label : label).Append(": ")
                    .Append(FormatNeedValue(entry.Value)).AppendLine();
            }
        }

        builder.AppendLine();
        builder.AppendLine(string.IsNullOrWhiteSpace(selectedPawnPanelPlanHeader) ? "Plan" : selectedPawnPanelPlanHeader);
        bool hasPlanContent = false;
        if (!string.IsNullOrEmpty(_selectedPawnPlanState))
        {
            builder.Append("  State: ").Append(_selectedPawnPlanState).AppendLine();
            hasPlanContent = true;
        }
        if (!string.IsNullOrEmpty(_selectedPawnPlanGoal))
        {
            builder.Append("  Goal: ").Append(_selectedPawnPlanGoal).AppendLine();
            hasPlanContent = true;
        }
        if (!string.IsNullOrEmpty(_selectedPawnPlanCurrentStep))
        {
            builder.Append("  Current: ").Append(_selectedPawnPlanCurrentStep).AppendLine();
            hasPlanContent = true;
        }
        if (_selectedPawnPlanSteps.Length > 0)
        {
            builder.AppendLine("  Steps:");
            for (int i = 0; i < _selectedPawnPlanSteps.Length; i++)
            {
                builder.Append("    ").Append(i + 1).Append(". ").Append(_selectedPawnPlanSteps[i]).AppendLine();
            }
            hasPlanContent = true;
        }
        if (_selectedPawnPlanUpdatedUtc != default)
        {
            builder.Append("  Updated: ").Append(_selectedPawnPlanUpdatedUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append("Z").AppendLine();
            hasPlanContent = true;
        }
        if (!hasPlanContent)
        {
            builder.AppendLine("  <none>");
        }

        if (!string.IsNullOrEmpty(_selectedPlanOptionLabel))
        {
            builder.AppendLine();
            builder.Append("  Manual Selection: ").Append(_selectedPlanOptionLabel).AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private void ClearSelectedPawnInfo()
    {
        _selectedPawnName = string.Empty;
        _selectedPawnRole = string.Empty;
        _selectedPawnGridPosition = null;
        _selectedPawnPlanGoal = string.Empty;
        _selectedPawnPlanState = string.Empty;
        _selectedPawnPlanCurrentStep = string.Empty;
        _selectedPawnPlanUpdatedUtc = default;
        _selectedPawnNeeds.Clear();
        _selectedPawnPlanSteps = Array.Empty<string>();
        _selectedPawnPlanStatus = null;
        _selectedPawnPlanOptions.Clear();
        _selectedPlanOptionIndex = null;
        _selectedPlanOptionLabel = string.Empty;
        _selectedPawnPanelText = string.Empty;
        _selectedPawnPanelBuilder.Clear();
    }

    private static string HumanizeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace('_', ' ').Replace('-', ' ').Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized);
    }

    private static string FormatNeedValue(double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value))
        {
            return "—";
        }

        return value.Value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void LoadMap(
        Texture2D baseMapTexture,
        GoapSimulationBootstrapper.SimulationReadyEventArgs.TileClassificationSnapshot tileClassification,
        int expectedWidth,
        int expectedHeight)
    {
        if (baseMapTexture == null)
        {
            throw new ArgumentNullException(nameof(baseMapTexture));
        }

        if (tileClassification == null)
        {
            throw new ArgumentNullException(nameof(tileClassification));
        }

        if (baseMapTexture.width != expectedWidth || baseMapTexture.height != expectedHeight)
        {
            throw new InvalidDataException($"Preloaded world map texture dimensions {baseMapTexture.width}x{baseMapTexture.height} do not match world {expectedWidth}x{expectedHeight}.");
        }

        if (tileClassification.Width != expectedWidth || tileClassification.Height != expectedHeight)
        {
            throw new InvalidDataException($"Tile classification dimensions {tileClassification.Width}x{tileClassification.Height} do not match world {expectedWidth}x{expectedHeight}.");
        }

        var basePixels = baseMapTexture.GetPixels32();
        if (basePixels.Length != expectedWidth * expectedHeight)
        {
            throw new InvalidDataException("Base world map texture pixel count does not match expected grid size.");
        }

        var outputPixels = new Color32[basePixels.Length];
        var walkable = tileClassification.Walkable;
        for (int y = 0; y < expectedHeight; y++)
        {
            for (int x = 0; x < expectedWidth; x++)
            {
                int pixelIndex = y * expectedWidth + x;
                var baseColor = basePixels[pixelIndex];
                outputPixels[pixelIndex] = walkable[x, y] ? baseColor : BlendBuildingTint(baseColor);
            }
        }

        var generatedTexture = new Texture2D(expectedWidth, expectedHeight, TextureFormat.RGBA32, false, true);
        generatedTexture.SetPixels32(outputPixels);
        generatedTexture.filterMode = FilterMode.Point;
        generatedTexture.wrapMode = TextureWrapMode.Clamp;
        generatedTexture.Apply(false, false);

        _mapTexture = generatedTexture;
        _ownsMapTexture = true;

        _mapSprite = Sprite.Create(_mapTexture, new Rect(0f, 0f, _mapTexture.width, _mapTexture.height), new Vector2(0f, 0f), 1f);
        _mapSprite.name = "GoapWorldMap";

        _mapObject = new GameObject("World Map");
        _mapObject.transform.SetParent(transform, false);
        var renderer = _mapObject.AddComponent<SpriteRenderer>();
        renderer.sprite = _mapSprite;
        renderer.sortingOrder = mapSortingOrder;
        renderer.drawMode = SpriteDrawMode.Simple;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    private static Color32 BlendBuildingTint(Color32 baseColor)
    {
        var baseLinear = (Color)baseColor;
        var blended = Color.Lerp(baseLinear, BuildingTintColor, Mathf.Clamp01(BuildingTintBlend));
        blended.a = baseLinear.a;
        return (Color32)blended;
    }

    private void LoadSpriteManifest(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException("Manifest path must be provided.", nameof(manifestPath));
        }

        var absolutePath = Path.GetFullPath(manifestPath);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"Sprite manifest '{absolutePath}' does not exist.", absolutePath);
        }

        _pawnSpritePaths.Clear();
        var json = File.ReadAllText(absolutePath);
        var manifest = StrictJson.ParseObject(json, absolutePath);

        foreach (var entry in manifest)
        {
            if (entry.Value is not Dictionary<string, object> entryObject)
            {
                throw new InvalidDataException($"Sprite manifest entry '{entry.Key}' must be an object.");
            }

            if (!entryObject.TryGetValue("sprites", out var spritesValue) || spritesValue is not Dictionary<string, object> spritesObject)
            {
                throw new InvalidDataException($"Sprite manifest entry '{entry.Key}' must contain a 'sprites' object.");
            }

            var spritePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var spriteProperty in spritesObject)
            {
                if (spriteProperty.Value is not string spritePath || string.IsNullOrWhiteSpace(spritePath))
                {
                    throw new InvalidDataException($"Sprite manifest entry '{entry.Key}' has an empty path for orientation '{spriteProperty.Key}'.");
                }

                spritePaths[spriteProperty.Key] = spritePath.Trim();
            }

            if (spritePaths.Count == 0)
            {
                throw new InvalidDataException($"Sprite manifest entry '{entry.Key}' does not contain any sprite paths.");
            }

            _pawnSpritePaths[entry.Key] = spritePaths;
        }
    }

    private Sprite LoadSpriteAsset(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException("Sprite path must be provided.", nameof(manifestPath));
        }

        var absolutePath = ResolveSpriteAbsolutePath(manifestPath);
        if (_spriteCache.TryGetValue(absolutePath, out var cached))
        {
            return cached;
        }

        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"Sprite asset '{absolutePath}' referenced by manifest is missing.", absolutePath);
        }

        var data = File.ReadAllBytes(absolutePath);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!texture.LoadImage(data, false))
        {
            Destroy(texture);
            throw new InvalidDataException($"Sprite asset '{absolutePath}' could not be decoded as a valid image.");
        }

        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;

        var pixelsPerUnit = Mathf.Max(texture.width, texture.height);
        if (pixelsPerUnit <= 0f)
        {
            Destroy(texture);
            throw new InvalidDataException($"Sprite asset '{absolutePath}' has invalid dimensions {texture.width}x{texture.height}.");
        }

        var sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), pixelsPerUnit);
        sprite.name = Path.GetFileNameWithoutExtension(absolutePath);

        _textureCache[absolutePath] = texture;
        _spriteCache[absolutePath] = sprite;
        return sprite;
    }

    private Sprite GetOrCreateThingSprite(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("Thing type must be provided.", nameof(type));
        }

        var normalized = type.Trim();
        if (_thingSpriteCache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        if (TryLoadThingSprite(normalized, out var loadedSprite))
        {
            _thingSpriteCache[normalized] = loadedSprite;
            return loadedSprite;
        }

        var color = DeriveThingColor(normalized);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
        var pixel = (Color32)color;
        texture.SetPixels32(new[] { pixel, pixel, pixel, pixel });
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.Apply(false, false);

        var sprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            new Vector2(0.5f, 0.5f),
            texture.width);
        sprite.name = $"ThingType_{normalized}";

        _thingSpriteCache[normalized] = sprite;
        _thingTextureCache[normalized] = texture;
        return sprite;
    }

    private bool TryLoadThingSprite(string normalizedType, out Sprite sprite)
    {
        sprite = null;
        if (string.IsNullOrWhiteSpace(normalizedType))
        {
            return false;
        }

        if (!ThingIconManifest.TryGetValue(normalizedType, out var manifestPath))
        {
            return false;
        }

        sprite = LoadSpriteAsset(manifestPath);
        return sprite != null;
    }

    private Color DeriveThingColor(string normalizedType)
    {
        if (string.IsNullOrWhiteSpace(normalizedType))
        {
            throw new ArgumentException("Thing type must be provided for color derivation.", nameof(normalizedType));
        }

        unchecked
        {
            uint hash = 2166136261u;
            for (int i = 0; i < normalizedType.Length; i++)
            {
                var ch = char.ToLowerInvariant(normalizedType[i]);
                hash ^= ch;
                hash *= 16777619u;
            }

            float hue = (hash & 0xFFFFu) / 65535f;
            float saturation = 0.45f + ((hash >> 16) & 0xFFu) / 255f * 0.35f;
            float value = 0.65f + ((hash >> 24) & 0xFFu) / 255f * 0.3f;

            var color = Color.HSVToRGB(hue, Mathf.Clamp01(saturation), Mathf.Clamp01(value));
            color.a = Mathf.Clamp01(thingMarkerAlpha);
            return color;
        }
    }

    private string ResolveSpriteAbsolutePath(string manifestPath)
    {
        if (!TryResolveSpriteAbsolutePath(manifestPath, out var absolutePath))
        {
            throw new FileNotFoundException($"Sprite asset '{manifestPath}' could not be resolved relative to known search paths.", manifestPath);
        }

        return absolutePath;
    }

    private bool TryResolveSpriteAbsolutePath(string manifestPath, out string absolutePath)
    {
        absolutePath = null;
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return false;
        }

        var trimmed = manifestPath.Trim();

        if (Path.IsPathRooted(trimmed))
        {
            if (File.Exists(trimmed))
            {
                absolutePath = Path.GetFullPath(trimmed);
                return true;
            }

            var root = Path.GetPathRoot(trimmed);
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            var remainder = trimmed.Substring(root.Length);
            return TryResolveRelativePathCaseInsensitive(root, remainder, out absolutePath);
        }

        if (trimmed.StartsWith("/", StringComparison.Ordinal) || trimmed.StartsWith("\\", StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(1);
        }

        var normalizedRelative = trimmed.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        foreach (var root in EnumerateSpriteSearchRoots())
        {
            if (TryResolveRelativePathCaseInsensitive(root, normalizedRelative, out var resolved))
            {
                absolutePath = resolved;
                return true;
            }
        }

        return false;
    }

    private IEnumerable<string> EnumerateSpriteSearchRoots()
    {
        if (!string.IsNullOrEmpty(_datasetRoot))
        {
            yield return _datasetRoot;
            var parent = Directory.GetParent(_datasetRoot);
            if (parent != null)
            {
                yield return parent.FullName;
            }
        }

        if (!string.IsNullOrEmpty(Application.dataPath))
        {
            yield return Application.dataPath;
        }
    }

    private static bool TryResolveRelativePathCaseInsensitive(string baseDirectory, string relativePath, out string resolvedPath)
    {
        resolvedPath = null;
        if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var segments = relativePath.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        var current = Path.GetFullPath(baseDirectory);

        for (int i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                var parent = Directory.GetParent(current);
                if (parent == null)
                {
                    return false;
                }

                current = parent.FullName;
                continue;
            }

            bool last = i == segments.Length - 1;
            if (last)
            {
                var candidateFile = Path.Combine(current, segment);
                if (File.Exists(candidateFile))
                {
                    resolvedPath = Path.GetFullPath(candidateFile);
                    return true;
                }

                var match = Directory.EnumerateFileSystemEntries(current)
                    .FirstOrDefault(entry => string.Equals(Path.GetFileName(entry), segment, StringComparison.OrdinalIgnoreCase));
                if (match != null && File.Exists(match))
                {
                    resolvedPath = Path.GetFullPath(match);
                    return true;
                }

                return false;
            }

            var candidateDirectory = Path.Combine(current, segment);
            if (Directory.Exists(candidateDirectory))
            {
                current = Path.GetFullPath(candidateDirectory);
                continue;
            }

            var directoryMatch = Directory.EnumerateDirectories(current)
                .FirstOrDefault(dir => string.Equals(Path.GetFileName(dir), segment, StringComparison.OrdinalIgnoreCase));
            if (directoryMatch == null)
            {
                return false;
            }

            current = Path.GetFullPath(directoryMatch);
        }

        return false;
    }

    private void EnsurePawnContainer()
    {
        if (pawnContainer == null)
        {
            pawnContainer = transform;
        }

        if (_pawnRoot == null)
        {
            var pawnRootObject = new GameObject("Pawns");
            pawnRootObject.transform.SetParent(pawnContainer, false);
            _pawnRoot = pawnRootObject.transform;
        }
    }

    private void EnsureThingRoot()
    {
        if (_thingRoot != null)
        {
            return;
        }

        var parent = pawnContainer == null ? transform : pawnContainer;
        if (parent == null)
        {
            throw new InvalidOperationException("GoapSimulationView requires a valid transform to parent thing markers.");
        }

        var thingRootObject = new GameObject("Things");
        thingRootObject.transform.SetParent(parent, false);
        _thingRoot = thingRootObject.transform;
    }

    private void ClearThingVisuals()
    {
        foreach (var visual in _thingVisuals.Values)
        {
            if (visual?.Root != null)
            {
                Destroy(visual.Root.gameObject);
            }
        }

        _thingVisuals.Clear();

        if (_thingRoot != null)
        {
            Destroy(_thingRoot.gameObject);
            _thingRoot = null;
        }

        _thingUpdateScratch.Clear();
        _thingRemovalScratch.Clear();
    }

    private void EnsureObserverCamera()
    {
        var resolvedCamera = observerCamera;
        if (resolvedCamera == null)
        {
            resolvedCamera = GetComponent<Camera>();
        }

        if (resolvedCamera == null)
        {
            resolvedCamera = GetComponentInChildren<Camera>(true);
        }

        if (resolvedCamera == null)
        {
            resolvedCamera = Camera.main;
        }

        if (resolvedCamera == null)
        {
            resolvedCamera = FindFirstObjectByType<Camera>();
        }

        if (resolvedCamera == null)
        {
            throw new InvalidOperationException("GoapSimulationView requires a Camera reference to center on the selected pawn.");
        }

        if (!ReferenceEquals(observerCamera, resolvedCamera))
        {
            observerCamera = resolvedCamera;
            _zoomInitialized = false;
        }

        _pixelPerfectCamera = observerCamera.GetComponent<UnityEngine.Rendering.Universal.PixelPerfectCamera>();

        if (!_zoomInitialized)
        {
            InitializeCameraZoomTargets();
        }
    }

    private void EnsureBootstrapperReference()
    {
        if (bootstrapper == null)
        {
            bootstrapper = FindFirstObjectByType<GoapSimulationBootstrapper>();
        }

        if (bootstrapper == null)
        {
            throw new InvalidOperationException("GoapSimulationView could not locate a GoapSimulationBootstrapper in the scene.");
        }
    }

    private void DisposeVisuals()
    {
        ClearThingVisuals();
        foreach (var visual in _pawnVisuals.Values)
        {
            if (visual?.Root != null)
            {
                Destroy(visual.Root.gameObject);
            }
        }

        _pawnVisuals.Clear();
        _pawnPreviousGridPositions.Clear();

        if (_pawnRoot != null)
        {
            Destroy(_pawnRoot.gameObject);
            _pawnRoot = null;
        }

        if (_mapObject != null)
        {
            Destroy(_mapObject);
            _mapObject = null;
        }

        foreach (var sprite in _spriteCache.Values)
        {
            if (sprite != null)
            {
                Destroy(sprite);
            }
        }
        _spriteCache.Clear();

        foreach (var sprite in _thingSpriteCache.Values)
        {
            if (sprite != null)
            {
                Destroy(sprite);
            }
        }
        _thingSpriteCache.Clear();

        foreach (var texture in _textureCache.Values)
        {
            if (texture != null)
            {
                Destroy(texture);
            }
        }
        _textureCache.Clear();

        foreach (var texture in _thingTextureCache.Values)
        {
            if (texture != null)
            {
                Destroy(texture);
            }
        }
        _thingTextureCache.Clear();

        if (_mapSprite != null)
        {
            Destroy(_mapSprite);
            _mapSprite = null;
        }

        if (_mapTexture != null)
        {
            if (_ownsMapTexture)
            {
                Destroy(_mapTexture);
            }

            _mapTexture = null;
        }

        _ownsMapTexture = false;

        _pawnSpritePaths.Clear();
        _actors = null;
        _world = null;
        _datasetRoot = null;
        _clock = null;
        _clockLabel = string.Empty;
        _clockGuiContent.text = string.Empty;
        _clockStyle = null;
        SetSelectedPawnId(null, suppressImmediateVisibilityUpdate: true);
        _showOnlySelectedPawn = false;
        _selectedPawnVisibilityDirty = false;
        _actorDiagnostics = null;
        ClearPawnUpdateLabel();
        _pawnDefinitions.Clear();
        _needAttributeNames = Array.Empty<string>();
        _selectedPawnPanelStyle = null;
        _selectedPawnGuiContent.text = string.Empty;
        ClearSelectedPawnInfo();
        _manualPawnIds.Clear();
        _tileClassification = null;
    }

    private static ThingId? ParseSelectedPawnId(string rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId))
        {
            return null;
        }

        return new ThingId(rawId.Trim());
    }

    private void SetSelectedPawnId(ThingId? selectedPawnId, bool suppressImmediateVisibilityUpdate)
    {
        if (!suppressImmediateVisibilityUpdate && _pawnVisuals.Count == 0)
        {
            throw new InvalidOperationException(
                "Pawn visuals must be initialized before updating the selected pawn.");
        }

        var previous = _selectedPawnId;
        _selectedPawnId = selectedPawnId;
        if (!NullableThingIdEquals(previous, selectedPawnId))
        {
            _selectedPlanOptionIndex = null;
            _selectedPlanOptionLabel = string.Empty;
        }
        _selectedPawnVisibilityDirty = true;

        if (!suppressImmediateVisibilityUpdate)
        {
            TryApplySelectedPawnVisibility();
        }
    }

    private void EnsurePlayerPawnController()
    {
        if (playerPawnController == null)
        {
            playerPawnController = FindFirstObjectByType<PlayerPawnController>();
        }

        if (playerPawnController == null)
        {
            throw new InvalidOperationException("GoapSimulationView could not locate a PlayerPawnController instance.");
        }
    }

    private void TryApplySelectedPawnVisibility()
    {
        if (!_selectedPawnVisibilityDirty)
        {
            return;
        }

        if (_pawnVisuals.Count == 0)
        {
            return;
        }

        ApplyPawnVisibilityRule();
        _selectedPawnVisibilityDirty = false;
    }

    private void ApplyPawnVisibilityRule()
    {
        if (!_showOnlySelectedPawn)
        {
            foreach (var entry in _pawnVisuals)
            {
                var visual = entry.Value ?? throw new InvalidOperationException(
                    $"Pawn visual entry for '{entry.Key.Value}' is missing.");
                var renderer = visual.Renderer ?? throw new InvalidOperationException(
                    $"Pawn '{visual.PawnId}' visual is missing its SpriteRenderer component.");
                renderer.enabled = true;
            }

            return;
        }

        if (!_selectedPawnId.HasValue)
        {
            throw new InvalidOperationException(
                "Observer configuration requested hiding all but the selected pawn, but no pawn is currently selected.");
        }

        var selectedId = _selectedPawnId.Value;
        if (!_pawnVisuals.TryGetValue(selectedId, out var selectedVisual) || selectedVisual == null)
        {
            throw new InvalidOperationException(
                $"Observer selected pawn '{selectedId.Value}' was not instantiated in the scene.");
        }

        if (selectedVisual.Renderer == null)
        {
            throw new InvalidOperationException(
                $"Pawn '{selectedVisual.PawnId}' visual is missing its SpriteRenderer component.");
        }

        foreach (var entry in _pawnVisuals)
        {
            var visual = entry.Value ?? throw new InvalidOperationException(
                $"Pawn visual entry for '{entry.Key.Value}' is missing.");
            var renderer = visual.Renderer ?? throw new InvalidOperationException(
                $"Pawn '{visual.PawnId}' visual is missing its SpriteRenderer component.");
            renderer.enabled = entry.Key.Equals(selectedId);
        }
    }

    private static string ResolveOrientationSpritePath(PawnVisual visual, string orientationKey)
    {
        if (visual == null)
        {
            throw new ArgumentNullException(nameof(visual));
        }

        if (string.IsNullOrWhiteSpace(orientationKey))
        {
            throw new ArgumentException("Orientation key must be provided.", nameof(orientationKey));
        }

        if (!visual.SpritePaths.TryGetValue(orientationKey, out var manifestPath) || string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException(
                $"Sprite manifest entry for pawn '{visual.PawnId}' does not define an orientation '{orientationKey}'.");
        }

        return manifestPath;
    }

    private static string DetermineOrientationKey(GridPos previous, GridPos current)
    {
        var deltaX = current.X - previous.X;
        var deltaY = current.Y - previous.Y;

        if (deltaX > 0)
        {
            return "east";
        }

        if (deltaX < 0)
        {
            return "west";
        }

        if (deltaY > 0)
        {
            return "north";
        }

        if (deltaY < 0)
        {
            return "south";
        }

        return null;
    }

    private ThingId? DetectClickedPawn(IWorldSnapshot snapshot)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (!TryGetClickGrid(snapshot, out var gridPos))
        {
            return null;
        }

        foreach (var entry in _pawnVisuals)
        {
            var thing = snapshot.GetThing(entry.Key);
            if (thing == null)
            {
                throw new InvalidOperationException($"World snapshot no longer contains actor '{entry.Key.Value}'.");
            }

            if (thing.Position.Equals(gridPos))
            {
                return entry.Key;
            }
        }

        return null;
    }

    private bool TryGetClickGrid(IWorldSnapshot snapshot, out GridPos gridPos)
    {
        gridPos = default;
        var mouse = Mouse.current;
        if (mouse == null)
        {
            return false;
        }

        if (!mouse.leftButton.wasPressedThisFrame)
        {
            return false;
        }

        EnsureObserverCamera();

        var screen = mouse.position.ReadValue();
        var ray = observerCamera.ScreenPointToRay(new Vector3(screen.x, screen.y, 0f));
        var plane = new Plane(Vector3.forward, Vector3.zero);
        if (!plane.Raycast(ray, out var distance))
        {
            throw new InvalidOperationException("Observer camera ray failed to intersect the world plane during selection.");
        }

        var worldPoint = ray.GetPoint(distance);
        if (!float.IsFinite(worldPoint.x) || !float.IsFinite(worldPoint.y))
        {
            throw new InvalidOperationException("Mouse click projected to a non-finite world coordinate.");
        }

        var gridX = Mathf.FloorToInt(worldPoint.x);
        var gridY = Mathf.FloorToInt(worldPoint.y);
        if (gridX < 0 || gridY < 0 || gridX >= snapshot.Width || gridY >= snapshot.Height)
        {
            return false;
        }

        gridPos = new GridPos(gridX, gridY);
        return true;
    }

    private PlanActionOption BuildPlanActionOption(string stepLabel, IWorldSnapshot snapshot, int stepIndex)
    {
        if (string.IsNullOrWhiteSpace(stepLabel))
        {
            throw new ArgumentException("Plan step label must be provided.", nameof(stepLabel));
        }

        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var trimmed = stepLabel.Trim();
        string formattedLabel = string.Format(CultureInfo.InvariantCulture, "{0}. {1}", stepIndex + 1, trimmed);
        var targetIdRaw = ExtractPlanTargetIdentifier(trimmed);
        ThingId? targetId = null;
        GridPos? targetPosition = null;
        bool actionable = false;

        if (!string.IsNullOrEmpty(targetIdRaw))
        {
            var resolvedId = new ThingId(targetIdRaw);
            var targetThing = snapshot.GetThing(resolvedId);
            if (targetThing == null)
            {
                throw new InvalidOperationException(
                    $"Plan step '{trimmed}' references target '{targetIdRaw}' that is not present in the world snapshot.");
            }

            targetId = resolvedId;
            targetPosition = targetThing.Position;
            actionable = true;
        }

        return new PlanActionOption(formattedLabel, targetId, targetPosition, stepIndex, actionable);
    }

    private static string ExtractPlanTargetIdentifier(string stepLabel)
    {
        if (string.IsNullOrWhiteSpace(stepLabel))
        {
            return string.Empty;
        }

        var markerIndex = stepLabel.LastIndexOf("->", StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return string.Empty;
        }

        var candidate = stepLabel.Substring(markerIndex + 2).Trim();
        if (string.IsNullOrEmpty(candidate) || string.Equals(candidate, "<none>", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return candidate;
    }

    private void HandlePlanOptionInvoked(PlanActionOption option)
    {
        if (option == null)
        {
            throw new ArgumentNullException(nameof(option));
        }

        if (!option.IsActionable || !option.TargetId.HasValue || !option.TargetPosition.HasValue)
        {
            throw new InvalidOperationException(
                $"Plan option '{option.Label}' does not resolve to a valid actionable target.");
        }

        EnsurePlayerPawnController();

        if (_selectedPawnId == null)
        {
            throw new InvalidOperationException("No pawn is currently selected for manual plan execution.");
        }

        var selectedId = _selectedPawnId.Value;
        if (!playerPawnController.ControlledPawnId.HasValue ||
            !playerPawnController.ControlledPawnId.Value.Equals(selectedId))
        {
            throw new InvalidOperationException(
                $"Selected pawn '{selectedId.Value}' is not controlled by the player pawn controller.");
        }

        playerPawnController.RequestManualInteract(option.TargetId.Value, option.TargetPosition.Value, option.StepIndex);
        _selectedPlanOptionIndex = option.StepIndex;
        _selectedPlanOptionLabel = option.Label;
    }

    private static bool NullableThingIdEquals(ThingId? left, ThingId? right)
    {
        if (!left.HasValue && !right.HasValue)
        {
            return true;
        }

        if (left.HasValue != right.HasValue)
        {
            return false;
        }

        return left.HasValue && left.Value.Equals(right.Value);
    }

    private sealed class PlanActionOption
    {
        public PlanActionOption(string label, ThingId? targetId, GridPos? targetPosition, int stepIndex, bool isActionable)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                throw new ArgumentException("Plan option label must be provided.", nameof(label));
            }

            Label = label;
            TargetId = targetId;
            TargetPosition = targetPosition;
            StepIndex = stepIndex;
            IsActionable = isActionable;
        }

        public string Label { get; }
        public ThingId? TargetId { get; }
        public GridPos? TargetPosition { get; }
        public int StepIndex { get; }
        public bool IsActionable { get; }
    }

    private sealed class ThingVisual
    {
        public ThingVisual(Transform root, SpriteRenderer renderer, string thingType)
        {
            Root = root ?? throw new ArgumentNullException(nameof(root));
            Renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            if (string.IsNullOrWhiteSpace(thingType))
            {
                throw new ArgumentException("Thing type must be provided for a thing visual.", nameof(thingType));
            }

            ThingType = thingType;
        }

        public Transform Root { get; }
        public SpriteRenderer Renderer { get; }
        public string ThingType { get; }
    }

    private sealed class PawnVisual
    {
        public PawnVisual(
            Transform root,
            SpriteRenderer renderer,
            IReadOnlyDictionary<string, string> spritePaths,
            string pawnId)
        {
            Root = root ?? throw new ArgumentNullException(nameof(root));
            Renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            SpritePaths = spritePaths ?? throw new ArgumentNullException(nameof(spritePaths));
            if (string.IsNullOrWhiteSpace(pawnId))
            {
                throw new ArgumentException("Pawn id must be provided for a pawn visual.", nameof(pawnId));
            }

            PawnId = pawnId;
        }

        public Transform Root { get; }
        public SpriteRenderer Renderer { get; }
        public IReadOnlyDictionary<string, string> SpritePaths { get; }
        public string PawnId { get; }
    }

    private static class StrictJson
    {
        public static Dictionary<string, object> ParseObject(string json, string sourceDescription)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            var reader = new Reader(json, sourceDescription);
            var value = reader.ReadValue();
            if (value is not Dictionary<string, object> result)
            {
                throw new InvalidDataException($"JSON '{sourceDescription}' must contain an object at the root.");
            }

            reader.SkipWhitespace();
            if (!reader.EndOfDocument)
            {
                throw new InvalidDataException($"JSON '{sourceDescription}' contains trailing content after the root object.");
            }

            return result;
        }

        private ref struct Reader
        {
            private readonly string _json;
            private readonly string _sourceDescription;
            private int _index;

            public Reader(string json, string sourceDescription)
            {
                _json = json;
                _sourceDescription = sourceDescription;
                _index = 0;
            }

            public bool EndOfDocument => _index >= _json.Length;

            public object ReadValue()
            {
                SkipWhitespace();
                if (EndOfDocument)
                {
                    throw CreateException("Unexpected end of JSON content.");
                }

                var c = _json[_index];
                switch (c)
                {
                    case '{':
                        return ReadObject();
                    case '[':
                        return ReadArray();
                    case '"':
                        return ReadString();
                    case 't':
                        return ReadLiteral("true", true);
                    case 'f':
                        return ReadLiteral("false", false);
                    case 'n':
                        return ReadLiteral("null", null);
                    case '-':
                    case '0':
                    case '1':
                    case '2':
                    case '3':
                    case '4':
                    case '5':
                    case '6':
                    case '7':
                    case '8':
                    case '9':
                        return ReadNumber();
                    default:
                        throw CreateException($"Unexpected character '{c}' while parsing JSON value.");
                }
            }

            public void SkipWhitespace()
            {
                while (!EndOfDocument)
                {
                    var c = _json[_index];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                    {
                        _index++;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            private Dictionary<string, object> ReadObject()
            {
                Expect('{');
                SkipWhitespace();

                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                if (TryConsume('}'))
                {
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    var key = ReadString();
                    SkipWhitespace();
                    Expect(':');
                    var value = ReadValue();
                    if (result.ContainsKey(key))
                    {
                        throw CreateException($"Duplicate key '{key}' detected in JSON object.");
                    }
                    result[key] = value;
                    SkipWhitespace();
                    if (TryConsume('}'))
                    {
                        break;
                    }

                    Expect(',');
                }

                return result;
            }

            private List<object> ReadArray()
            {
                Expect('[');
                SkipWhitespace();
                var result = new List<object>();
                if (TryConsume(']'))
                {
                    return result;
                }

                while (true)
                {
                    var value = ReadValue();
                    result.Add(value);
                    SkipWhitespace();
                    if (TryConsume(']'))
                    {
                        break;
                    }

                    Expect(',');
                }

                return result;
            }

            private string ReadString()
            {
                Expect('"');
                var builder = new StringBuilder();

                while (true)
                {
                    if (EndOfDocument)
                    {
                        throw CreateException("Unterminated string literal in JSON content.");
                    }

                    var c = _json[_index++];
                    if (c == '"')
                    {
                        break;
                    }

                    if (c == '\\')
                    {
                        if (EndOfDocument)
                        {
                            throw CreateException("Unterminated escape sequence in JSON string.");
                        }

                        builder.Append(ReadEscapedCharacter());
                    }
                    else
                    {
                        builder.Append(c);
                    }
                }

                return builder.ToString();
            }

            private char ReadEscapedCharacter()
            {
                var escape = _json[_index++];
                return escape switch
                {
                    '"' => '"',
                    '\\' => '\\',
                    '/' => '/',
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'u' => ReadUnicodeEscape(),
                    _ => throw CreateException($"Unsupported escape sequence '\\{escape}' in JSON string."),
                };
            }

            private char ReadUnicodeEscape()
            {
                if (_index + 4 > _json.Length)
                {
                    throw CreateException("Incomplete unicode escape sequence in JSON string.");
                }

                var code = 0;
                for (var i = 0; i < 4; i++)
                {
                    var digit = _json[_index++];
                    code <<= 4;
                    if (digit >= '0' && digit <= '9')
                    {
                        code += digit - '0';
                    }
                    else if (digit >= 'a' && digit <= 'f')
                    {
                        code += 10 + digit - 'a';
                    }
                    else if (digit >= 'A' && digit <= 'F')
                    {
                        code += 10 + digit - 'A';
                    }
                    else
                    {
                        throw CreateException("Invalid character in unicode escape sequence.");
                    }
                }

                return (char)code;
            }

            private object ReadNumber()
            {
                var start = _index;
                if (_json[_index] == '-')
                {
                    _index++;
                }

                while (!EndOfDocument && char.IsDigit(_json[_index]))
                {
                    _index++;
                }

                if (!EndOfDocument && _json[_index] == '.')
                {
                    _index++;
                    if (EndOfDocument || !char.IsDigit(_json[_index]))
                    {
                        throw CreateException("Invalid JSON number format.");
                    }

                    while (!EndOfDocument && char.IsDigit(_json[_index]))
                    {
                        _index++;
                    }
                }

                if (!EndOfDocument && (_json[_index] == 'e' || _json[_index] == 'E'))
                {
                    _index++;
                    if (!EndOfDocument && (_json[_index] == '+' || _json[_index] == '-'))
                    {
                        _index++;
                    }

                    if (EndOfDocument || !char.IsDigit(_json[_index]))
                    {
                        throw CreateException("Invalid JSON number exponent.");
                    }

                    while (!EndOfDocument && char.IsDigit(_json[_index]))
                    {
                        _index++;
                    }
                }

                var span = _json.Substring(start, _index - start);
                if (span.IndexOf('.') >= 0 || span.IndexOf('e') >= 0 || span.IndexOf('E') >= 0)
                {
                    if (double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
                    {
                        return doubleValue;
                    }
                }
                else
                {
                    if (long.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                    {
                        return longValue;
                    }
                }

                throw CreateException($"Invalid JSON number '{span}'.");
            }

            private object ReadLiteral(string literal, object value)
            {
                for (var i = 0; i < literal.Length; i++)
                {
                    if (EndOfDocument || _json[_index++] != literal[i])
                    {
                        throw CreateException($"Invalid literal while parsing JSON. Expected '{literal}'.");
                    }
                }

                return value;
            }

            private void Expect(char expected)
            {
                if (EndOfDocument || _json[_index] != expected)
                {
                    throw CreateException($"Expected character '{expected}'.");
                }

                _index++;
            }

            private bool TryConsume(char expected)
            {
                if (!EndOfDocument && _json[_index] == expected)
                {
                    _index++;
                    return true;
                }

                return false;
            }

            private InvalidDataException CreateException(string message)
            {
                return new InvalidDataException($"{message} (while parsing '{_sourceDescription}').");
            }
        }
    }
}
