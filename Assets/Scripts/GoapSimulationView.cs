using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.Execution;
using DataDrivenGoap.Items;
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
    [SerializeField] private Vector2 thingHoverScreenOffset = new Vector2(16f, -16f);
    [SerializeField] private Vector2 thingHoverBackgroundPadding = new Vector2(12f, 6f);
    [SerializeField] private Color thingHoverTextColor = Color.white;
    [SerializeField] private Color thingHoverBackgroundColor = new Color(0f, 0f, 0f, 0.75f);
    [SerializeField, Min(1)] private int thingHoverFontSize = 14;
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
    private readonly GUIContent _thingHoverGuiContent = new GUIContent();
    private readonly GUIContent _selectedThingGuiContent = new GUIContent();
    
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
    private GUIStyle _selectedPawnPlanButtonStyle;
    private ThingId? _selectedPawnId;
    private readonly Dictionary<ThingId, VillagePawn> _pawnDefinitions = new Dictionary<ThingId, VillagePawn>();
    private readonly HashSet<ThingId> _manualPawnIds = new HashSet<ThingId>();
    private bool _manualPlanAutoEvaluationEnabled = true;
    private readonly StringBuilder _selectedPawnPanelBuilder = new StringBuilder();
    private readonly List<(string Label, double? Value)> _selectedPawnNeeds = new List<(string Label, double? Value)>();
    private string[] _selectedPawnPlanStepLines = Array.Empty<string>();
    private ActorPlanStatus _selectedPawnPlanStatus;
    private long _selectedPawnPlanSnapshotVersion;
    private readonly List<PlanActionOption> _selectedPawnPlanOptions = new List<PlanActionOption>();
    private readonly List<PlanActionOption> _selectedPawnAllActionablePlanOptions = new List<PlanActionOption>();
    private readonly List<PlanActionOption> _selectedPawnActionablePlanOptions = new List<PlanActionOption>();
    private int? _selectedPlanOptionIndex;
    private string _selectedPlanOptionLabel = string.Empty;
    private string[] _needAttributeNames = Array.Empty<string>();
    private string _selectedPawnPanelTextBeforePlanSteps = string.Empty;
    private string _selectedPawnPanelTextAfterPlanSteps = string.Empty;
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
    private GUIStyle _thingHoverStyle;
    private string _hoveredThingLabel = string.Empty;
    private Vector2 _hoveredThingScreenPosition = Vector2.zero;
    private ThingId? _selectedThingId;
    private ThingPlanParticipation[] _selectedThingParticipation = Array.Empty<ThingPlanParticipation>();
    private string[] _selectedThingPlanLines = Array.Empty<string>();
    private string _selectedThingHeader = string.Empty;
    private GridPos? _selectedThingGridPosition;
    private readonly StringBuilder _selectedThingPanelBuilder = new StringBuilder();
    private InventoryStackView[] _selectedThingInventoryStacks = Array.Empty<InventoryStackView>();
    private string[] _selectedThingInventoryLines = Array.Empty<string>();
    private string _selectedThingInventoryHeader = string.Empty;
    private int? _selectedThingInventorySelectionIndex;
    private string _selectedThingInventorySelectionLabel = string.Empty;

    private void Awake()
    {
        EnsureBootstrapperReference();
        EnsureObserverCamera();
    }

    private void RenderThingInventoryPanel(
        Rect pawnPanelRect,
        Rect thingPlanRect,
        bool hasPawnPanel,
        bool hasThingPlanPanel)
    {
        if (string.IsNullOrEmpty(_selectedThingInventoryHeader))
        {
            return;
        }

        EnsureSelectedPawnPanelStyle();
        EnsureSelectedPawnPlanButtonStyle();

        float width = Mathf.Max(16f, selectedPawnPanelWidth);
        float horizontalPosition = hasPawnPanel ? pawnPanelRect.x : selectedPawnPanelOffset.x;
        float verticalSpacing = Mathf.Max(8f, selectedPawnPanelPadding.y);
        float verticalPosition;
        if (hasThingPlanPanel)
        {
            verticalPosition = thingPlanRect.yMax + verticalSpacing;
        }
        else if (hasPawnPanel)
        {
            verticalPosition = pawnPanelRect.yMax + verticalSpacing;
        }
        else
        {
            verticalPosition = selectedPawnPanelOffset.y;
        }

        var content = _selectedThingGuiContent;
        content.text = _selectedThingInventoryHeader;
        float headerHeight = _selectedPawnPanelStyle.CalcHeight(content, width);

        var inventoryLines = _selectedThingInventoryLines ?? Array.Empty<string>();
        int lineCount = inventoryLines.Length;
        float[] lineHeights = lineCount > 0 ? new float[lineCount] : Array.Empty<float>();

        if (lineCount > 0)
        {
            for (int i = 0; i < lineCount; i++)
            {
                content.text = inventoryLines[i] ?? string.Empty;
                lineHeights[i] = _selectedPawnPlanButtonStyle.CalcHeight(content, width);
            }
        }

        float totalHeight = headerHeight;
        if (lineHeights.Length > 0)
        {
            for (int i = 0; i < lineHeights.Length; i++)
            {
                totalHeight += lineHeights[i];
            }
        }

        if (totalHeight <= 0f)
        {
            return;
        }

        var panelRect = new Rect(horizontalPosition, verticalPosition, width, Mathf.Max(0f, totalHeight));

        if (selectedPawnPanelBackgroundColor.a > 0f && Texture2D.whiteTexture != null)
        {
            float padX = Mathf.Max(0f, selectedPawnPanelPadding.x);
            float padY = Mathf.Max(0f, selectedPawnPanelPadding.y);
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

        float currentY = verticalPosition;

        if (headerHeight > 0f)
        {
            content.text = _selectedThingInventoryHeader;
            var headerRect = new Rect(horizontalPosition, currentY, width, headerHeight);
            GUI.Label(headerRect, content, _selectedPawnPanelStyle);
            currentY += headerHeight;
        }

        if (lineCount == 0)
        {
            return;
        }

        for (int i = 0; i < lineCount; i++)
        {
            float lineHeight = lineHeights[i];
            var buttonRect = new Rect(horizontalPosition, currentY, width, lineHeight);
            var previousEnabled = GUI.enabled;
            var previousBackground = GUI.backgroundColor;

            bool interactable = i < _selectedThingInventoryStacks.Length;
            bool isSelected = _selectedThingInventorySelectionIndex.HasValue &&
                _selectedThingInventorySelectionIndex.Value == i;
            GUI.enabled = interactable;

            if (isSelected)
            {
                GUI.backgroundColor = Color.Lerp(Color.white, Color.yellow, 0.5f);
            }

            string label = inventoryLines[i] ?? string.Empty;
            if (GUI.Button(buttonRect, label, _selectedPawnPlanButtonStyle) && interactable)
            {
                HandleInventoryItemInvoked(i);
            }

            GUI.backgroundColor = previousBackground;
            GUI.enabled = previousEnabled;
            currentY += lineHeight;
        }
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
            ClearThingHover(clearThingSelection: true);
            return;
        }

        UpdatePawnDiagnosticsLabel();

        var snapshot = _world.Snap();
        EnsureThingVisuals(snapshot);
        if (_selectedThingId.HasValue)
        {
            var existingThing = snapshot.GetThing(_selectedThingId.Value);
            if (existingThing == null || _pawnDefinitions.ContainsKey(_selectedThingId.Value))
            {
                ClearSelectedThingPlan();
            }
        }

        UpdateThingHover(snapshot);
        if (TryGetClickGrid(snapshot, out var clickGrid))
        {
            var clickedPawnId = DetectClickedPawn(snapshot, clickGrid);
            if (clickedPawnId.HasValue)
            {
                if (!_selectedPawnId.HasValue || !_selectedPawnId.Value.Equals(clickedPawnId.Value))
                {
                    SetSelectedPawnId(clickedPawnId, suppressImmediateVisibilityUpdate: false);
                }

                ClearSelectedThingPlan();
            }
            else
            {
                var clickedThingId = DetectClickedThing(snapshot, clickGrid);
                if (clickedThingId.HasValue)
                {
                    var clickedThing = snapshot.GetThing(clickedThingId.Value);
                    if (clickedThing == null)
                    {
                        throw new InvalidOperationException(
                            $"World snapshot no longer contains thing '{clickedThingId.Value.Value}'.");
                    }

                    UpdateSelectedThingPlan(clickedThing);
                }
                else
                {
                    ClearSelectedThingPlan();
                }
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
        ClearSelectedThingPlan();

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
        _manualPlanAutoEvaluationEnabled = args.ManualPlanAutoEvaluationEnabled;
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

    private void UpdateThingHover(IWorldSnapshot snapshot)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (!TryReadPointerScreenPosition(out var screen))
        {
            ClearThingHover();
            return;
        }
        if (!TryProjectScreenToGrid(snapshot, screen, out var gridPos))
        {
            ClearThingHover();
            return;
        }

        ThingView hoveredThing = null;
        foreach (var thing in snapshot.AllThings())
        {
            if (thing == null)
            {
                throw new InvalidOperationException("World snapshot returned a null thing entry while computing hover tooltip.");
            }

            if (_pawnDefinitions.ContainsKey(thing.Id))
            {
                continue;
            }

            if (thing.Position.Equals(gridPos))
            {
                hoveredThing = thing;
                break;
            }
        }

        if (hoveredThing == null)
        {
            ClearThingHover();
            return;
        }

        var label = FormatThingHoverLabel(hoveredThing);
        _hoveredThingLabel = label;
        _thingHoverGuiContent.text = label;
        _hoveredThingScreenPosition = screen;
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
        const float standardScrollStep = 120f;

        var mouse = Mouse.current;
        if (mouse != null)
        {
            var scroll = mouse.scroll.ReadValue().y;
            if (float.IsNaN(scroll) || float.IsInfinity(scroll))
            {
                throw new InvalidOperationException("Mouse scroll produced a non-finite value.");
            }

            if (Mathf.Approximately(scroll, 0f))
            {
                return 0f;
            }

            if (Mathf.Abs(scroll) >= standardScrollStep)
            {
                return scroll / standardScrollStep;
            }

            return scroll;
        }

        if (!Input.mousePresent)
        {
            return 0f;
        }

        var legacyScroll = Input.mouseScrollDelta.y;
        if (float.IsNaN(legacyScroll) || float.IsInfinity(legacyScroll))
        {
            throw new InvalidOperationException("Mouse scroll produced a non-finite value.");
        }

        if (Mathf.Approximately(legacyScroll, 0f))
        {
            return 0f;
        }

        if (Mathf.Abs(legacyScroll) >= standardScrollStep)
        {
            return legacyScroll / standardScrollStep;
        }

        return legacyScroll;
    }

    private void OnGUI()
    {
        bool hasClock = !string.IsNullOrEmpty(_clockLabel);
        bool hasPawnUpdate = !string.IsNullOrEmpty(_pawnUpdateLabel);

        if (hasClock || hasPawnUpdate)
        {
            RenderClockLabel(hasClock, hasPawnUpdate);
        }

        var hasPawnPanel = RenderSelectedPawnPanel(out var pawnPanelRect);
        var hasThingPlanPanel = RenderThingPlanPanel(pawnPanelRect, hasPawnPanel, out var thingPlanRect);
        RenderThingInventoryPanel(pawnPanelRect, thingPlanRect, hasPawnPanel, hasThingPlanPanel);
        RenderThingHover();
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
        bool hasContent =
            !string.IsNullOrEmpty(_selectedPawnPanelTextBeforePlanSteps) ||
            !string.IsNullOrEmpty(_selectedPawnPanelTextAfterPlanSteps);

        if (!hasContent)
        {
            panelRect = default;
            return false;
        }

        EnsureSelectedPawnPanelStyle();
        EnsureSelectedPawnPlanButtonStyle();

        float width = Mathf.Max(16f, selectedPawnPanelWidth);
        float x = selectedPawnPanelOffset.x;
        float y = selectedPawnPanelOffset.y;
        var content = _selectedPawnGuiContent;

        float beforeHeight = 0f;
        if (!string.IsNullOrEmpty(_selectedPawnPanelTextBeforePlanSteps))
        {
            content.text = _selectedPawnPanelTextBeforePlanSteps;
            beforeHeight = _selectedPawnPanelStyle.CalcHeight(content, width);
        }

        float stepsHeight = 0f;
        float[] stepLineHeights = null;
        if (_selectedPawnPlanStepLines.Length > 0)
        {
            int stepCount = Mathf.Min(_selectedPawnPlanStepLines.Length, _selectedPawnActionablePlanOptions.Count);
            if (stepCount > 0)
            {
                stepLineHeights = new float[stepCount];
                for (int i = 0; i < stepCount; i++)
                {
                    content.text = _selectedPawnPlanStepLines[i];
                    var height = _selectedPawnPlanButtonStyle.CalcHeight(content, width);
                    stepLineHeights[i] = height;
                    stepsHeight += height;
                }
            }
        }

        float afterHeight = 0f;
        if (!string.IsNullOrEmpty(_selectedPawnPanelTextAfterPlanSteps))
        {
            content.text = _selectedPawnPanelTextAfterPlanSteps;
            afterHeight = _selectedPawnPanelStyle.CalcHeight(content, width);
        }

        float totalHeight = beforeHeight + stepsHeight + afterHeight;

        if (totalHeight <= 0f)
        {
            panelRect = default;
            return false;
        }

        panelRect = new Rect(x, y, width, Mathf.Max(0f, totalHeight));

        if (selectedPawnPanelBackgroundColor.a > 0f && Texture2D.whiteTexture != null)
        {
            float padX = Mathf.Max(0f, selectedPawnPanelPadding.x);
            float padY = Mathf.Max(0f, selectedPawnPanelPadding.y);
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

        float currentY = y;
        if (beforeHeight > 0f)
        {
            content.text = _selectedPawnPanelTextBeforePlanSteps;
            var beforeRect = new Rect(x, currentY, width, beforeHeight);
            GUI.Label(beforeRect, content, _selectedPawnPanelStyle);
            currentY += beforeHeight;
        }

        if (stepLineHeights != null)
        {
            for (int i = 0; i < stepLineHeights.Length; i++)
            {
                if (i >= _selectedPawnActionablePlanOptions.Count)
                {
                    break;
                }

                var option = _selectedPawnActionablePlanOptions[i];
                var lineHeight = stepLineHeights[i];
                var buttonRect = new Rect(x, currentY, width, lineHeight);
                var previousEnabled = GUI.enabled;
                var previousBackground = GUI.backgroundColor;

                bool interactable = option.IsActionable && option.TargetId.HasValue && option.TargetPosition.HasValue;
                GUI.enabled = interactable;

                var displayLabel = FormatPlanOptionDisplay(option, i);
                bool isSelected = !string.IsNullOrEmpty(_selectedPlanOptionLabel) &&
                    string.Equals(_selectedPlanOptionLabel, displayLabel, StringComparison.Ordinal);
                if (isSelected)
                {
                    GUI.backgroundColor = Color.Lerp(Color.white, Color.cyan, 0.5f);
                }

                if (GUI.Button(buttonRect, _selectedPawnPlanStepLines[i], _selectedPawnPlanButtonStyle))
                {
                    HandlePlanStepButtonClicked(option);
                }

                GUI.backgroundColor = previousBackground;
                GUI.enabled = previousEnabled;
                currentY += lineHeight;
            }
        }

        if (afterHeight > 0f)
        {
            content.text = _selectedPawnPanelTextAfterPlanSteps;
            var afterRect = new Rect(x, currentY, width, afterHeight);
            GUI.Label(afterRect, content, _selectedPawnPanelStyle);
        }

        return true;
    }

    private void RenderThingHover()
    {
        if (string.IsNullOrEmpty(_hoveredThingLabel))
        {
            return;
        }

        EnsureThingHoverStyle();

        var content = _thingHoverGuiContent;
        content.text = _hoveredThingLabel;
        var contentSize = _thingHoverStyle.CalcSize(content);
        var offset = thingHoverScreenOffset;
        var screenPoint = _hoveredThingScreenPosition + offset;
        var guiPoint = new Vector2(screenPoint.x, Screen.height - screenPoint.y);
        var labelRect = new Rect(guiPoint, contentSize);

        if (thingHoverBackgroundColor.a > 0f && Texture2D.whiteTexture != null)
        {
            var padding = thingHoverBackgroundPadding;
            var backgroundRect = new Rect(
                labelRect.x - padding.x * 0.5f,
                labelRect.y - padding.y * 0.5f,
                labelRect.width + padding.x,
                labelRect.height + padding.y);

            var previousColor = GUI.color;
            GUI.color = thingHoverBackgroundColor;
            GUI.DrawTexture(backgroundRect, Texture2D.whiteTexture);
            GUI.color = previousColor;
        }

        GUI.Label(labelRect, content, _thingHoverStyle);
    }

    private void EnsureThingHoverStyle()
    {
        var desiredFontSize = Mathf.Max(1, thingHoverFontSize);
        if (_thingHoverStyle == null)
        {
            _thingHoverStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperLeft,
                wordWrap = false,
                richText = false,
                padding = new RectOffset(0, 0, 0, 0)
            };
        }

        if (_thingHoverStyle.fontSize != desiredFontSize)
        {
            _thingHoverStyle.fontSize = desiredFontSize;
        }

        _thingHoverStyle.normal.textColor = thingHoverTextColor;
    }

    private void ClearThingHover(bool clearThingSelection = false)
    {
        _hoveredThingLabel = string.Empty;
        _thingHoverGuiContent.text = string.Empty;
        _hoveredThingScreenPosition = Vector2.zero;
        if (clearThingSelection)
        {
            ClearSelectedThingPlan();
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

    private void EnsureSelectedPawnPlanButtonStyle()
    {
        EnsureSelectedPawnPanelStyle();

        if (_selectedPawnPlanButtonStyle == null)
        {
            _selectedPawnPlanButtonStyle = new GUIStyle(GUI.skin.button)
            {
                alignment = _selectedPawnPanelStyle.alignment,
                wordWrap = true,
                richText = _selectedPawnPanelStyle.richText,
                padding = new RectOffset(
                    _selectedPawnPanelStyle.padding.left,
                    _selectedPawnPanelStyle.padding.right,
                    _selectedPawnPanelStyle.padding.top,
                    _selectedPawnPanelStyle.padding.bottom),
                margin = new RectOffset(0, 0, 0, 0)
            };
        }

        if (_selectedPawnPlanButtonStyle.fontSize != _selectedPawnPanelStyle.fontSize)
        {
            _selectedPawnPlanButtonStyle.fontSize = _selectedPawnPanelStyle.fontSize;
        }

        _selectedPawnPlanButtonStyle.normal.textColor = _selectedPawnPanelStyle.normal.textColor;
        _selectedPawnPlanButtonStyle.hover.textColor = _selectedPawnPanelStyle.hover.textColor;
        _selectedPawnPlanButtonStyle.active.textColor = _selectedPawnPanelStyle.active.textColor;
        _selectedPawnPlanButtonStyle.focused.textColor = _selectedPawnPanelStyle.focused.textColor;
    }

    private void UpdateSelectedThingPlan(ThingView thing)
    {
        if (thing == null)
        {
            ClearSelectedThingPlan();
            return;
        }

        if (bootstrapper == null)
        {
            throw new InvalidOperationException("Thing selection requires an attached GoapSimulationBootstrapper instance.");
        }

        PopulateSelectedThingInventory(thing);

        var participation = bootstrapper.GetThingPlanParticipation(thing.Id, thing.Tags ?? Array.Empty<string>());
        ThingPlanParticipation[] entries;
        if (participation.Count == 0)
        {
            entries = Array.Empty<ThingPlanParticipation>();
        }
        else
        {
            entries = new ThingPlanParticipation[participation.Count];
            for (int i = 0; i < participation.Count; i++)
            {
                entries[i] = participation[i];
            }
        }

        List<PlanActionOption> manualOptions = null;
        if (_selectedPawnPlanOptions.Count > 0)
        {
            for (int i = 0; i < _selectedPawnPlanOptions.Count; i++)
            {
                var option = _selectedPawnPlanOptions[i];
                if (!option.TargetId.HasValue)
                {
                    continue;
                }

                if (!NullableThingIdEquals(option.TargetId, thing.Id))
                {
                    continue;
                }

                manualOptions ??= new List<PlanActionOption>();
                manualOptions.Add(option);
            }
        }

        bool usingManualOptions = manualOptions != null && manualOptions.Count > 0;
        if (usingManualOptions)
        {
            entries = new ThingPlanParticipation[manualOptions.Count];
            for (int i = 0; i < manualOptions.Count; i++)
            {
                var option = manualOptions[i];
                entries[i] = new ThingPlanParticipation(
                    option.GoalId,
                    option.RawLabel,
                    option.ActivityId,
                    option.IsActionable);
            }
        }

        string[] formatted;
        if (entries.Length == 0)
        {
            formatted = new[] { "<none>" };
        }
        else if (usingManualOptions)
        {
            formatted = new string[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                formatted[i] = FormatPlanOptionDisplay(manualOptions[i], i);
            }
        }
        else
        {
            formatted = new string[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                var activityLabel = string.IsNullOrEmpty(entry.Activity) ? "<none>" : entry.Activity;
                var moveLabel = entry.MoveToTarget ? "Yes" : "No";
                formatted[i] = string.Format(
                    CultureInfo.InvariantCulture,
                    "Goal: {0} — Action: {1} (Activity {2}, MoveToTarget: {3})",
                    entry.GoalId,
                    entry.ActionId,
                    activityLabel,
                    moveLabel);
            }
        }

        var identifier = thing.Id.Value ?? string.Empty;
        var formattedLabel = FormatThingHoverLabel(thing);
        _selectedThingHeader = string.Format(
            CultureInfo.InvariantCulture,
            "{0} (ID: {1})",
            formattedLabel,
            identifier);
        _selectedThingId = thing.Id;
        _selectedThingParticipation = entries;
        _selectedThingPlanLines = formatted;
        _selectedThingGridPosition = thing.Position;

        RefreshActionablePlanOptionsForSelection();
        SyncPlanSelectionToThing();
    }

    private void ClearSelectedThingPlan()
    {
        _selectedThingId = null;
        _selectedThingParticipation = Array.Empty<ThingPlanParticipation>();
        _selectedThingPlanLines = Array.Empty<string>();
        _selectedThingHeader = string.Empty;
        _selectedThingGridPosition = null;
        ClearSelectedThingInventory();
        _selectedThingPanelBuilder.Clear();
        _selectedThingGuiContent.text = string.Empty;
        _selectedPlanOptionIndex = null;
        _selectedPlanOptionLabel = string.Empty;
        RefreshActionablePlanOptionsForSelection();
    }

    private void PopulateSelectedThingInventory(ThingView thing)
    {
        ClearSelectedThingInventory();

        if (thing == null)
        {
            return;
        }

        if (bootstrapper == null)
        {
            throw new InvalidOperationException("Inventory inspection requires an attached GoapSimulationBootstrapper instance.");
        }

        if (!bootstrapper.TryGetInventoryContents(thing.Id, out var stacks))
        {
            return;
        }

        if (stacks == null)
        {
            throw new InvalidOperationException(
                $"Inventory snapshot provider returned null for thing '{thing.Id.Value ?? string.Empty}'.");
        }

        int count = stacks.Count;
        if (count <= 0)
        {
            _selectedThingInventoryStacks = Array.Empty<InventoryStackView>();
            _selectedThingInventoryLines = new[] { "<empty>" };
            _selectedThingInventoryHeader = "Contents (empty)";
            _selectedThingInventorySelectionIndex = null;
            _selectedThingInventorySelectionLabel = string.Empty;
            return;
        }

        var stackArray = new InventoryStackView[count];
        var lineArray = new string[count];
        var ownerId = thing.Id.Value ?? string.Empty;

        for (int i = 0; i < count; i++)
        {
            var stack = stacks[i];
            if (stack.Item == null)
            {
                throw new InvalidOperationException(
                    $"Inventory stack {i} for thing '{ownerId}' is missing an item definition.");
            }

            if (stack.Quantity <= 0)
            {
                throw new InvalidOperationException(
                    $"Inventory stack {i} for thing '{ownerId}' reported non-positive quantity {stack.Quantity}.");
            }

            if (string.IsNullOrWhiteSpace(stack.Item.Id))
            {
                throw new InvalidOperationException(
                    $"Inventory stack {i} for thing '{ownerId}' references an item with no identifier.");
            }

            stackArray[i] = stack;

            string label = string.Format(
                CultureInfo.InvariantCulture,
                "{0} x{1}",
                stack.Item.Id,
                stack.Quantity);

            if (stack.Quality > 0)
            {
                label = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} (Quality {1})",
                    label,
                    stack.Quality);
            }

            lineArray[i] = label;
        }

        _selectedThingInventoryStacks = stackArray;
        _selectedThingInventoryLines = lineArray;
        _selectedThingInventoryHeader = string.Format(
            CultureInfo.InvariantCulture,
            "Contents ({0})",
            count);
        _selectedThingInventorySelectionIndex = null;
        _selectedThingInventorySelectionLabel = string.Empty;
    }

    private void ClearSelectedThingInventory()
    {
        _selectedThingInventoryStacks = Array.Empty<InventoryStackView>();
        _selectedThingInventoryLines = Array.Empty<string>();
        _selectedThingInventoryHeader = string.Empty;
        _selectedThingInventorySelectionIndex = null;
        _selectedThingInventorySelectionLabel = string.Empty;
    }

    private void HandleInventoryItemInvoked(int index)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (_selectedThingInventoryStacks == null)
        {
            throw new InvalidOperationException("Inventory selection cannot proceed before inventory data is populated.");
        }

        if (index >= _selectedThingInventoryStacks.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                $"Inventory selection index {index} is outside the available stack range {_selectedThingInventoryStacks.Length}.");
        }

        if (_selectedThingInventoryLines == null || index >= _selectedThingInventoryLines.Length)
        {
            throw new InvalidOperationException("Inventory selection lines were not initialized for the current selection.");
        }

        _selectedThingInventorySelectionIndex = index;
        _selectedThingInventorySelectionLabel = _selectedThingInventoryLines[index] ?? string.Empty;
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

    private bool RenderThingPlanPanel(Rect pawnPanelRect, bool hasPawnPanel, out Rect planPanelRect)
    {
        planPanelRect = default;
        if (_selectedThingId == null || string.IsNullOrEmpty(_selectedThingHeader))
        {
            return false;
        }

        EnsureSelectedPawnPanelStyle();
        EnsureSelectedPawnPlanButtonStyle();

        float width = Mathf.Max(16f, selectedPawnPanelWidth);
        float horizontalPosition = hasPawnPanel ? pawnPanelRect.x : selectedPawnPanelOffset.x;
        float verticalSpacing = Mathf.Max(8f, selectedPawnPanelPadding.y);
        float verticalPosition = hasPawnPanel
            ? pawnPanelRect.yMax + verticalSpacing
            : selectedPawnPanelOffset.y;

        int entryCount = _selectedThingParticipation.Length;
        var participationLabel = entryCount == 1
            ? "Plan Participation (1 entry):"
            : string.Format(CultureInfo.InvariantCulture, "Plan Participation ({0} entries):", entryCount);

        var content = _selectedThingGuiContent;
        content.text = _selectedThingHeader;
        float headerHeight = _selectedPawnPanelStyle.CalcHeight(content, width);

        content.text = participationLabel;
        float participationHeight = _selectedPawnPanelStyle.CalcHeight(content, width);

        float[] lineHeights = Array.Empty<float>();
        if (_selectedThingPlanLines.Length > 0)
        {
            lineHeights = new float[_selectedThingPlanLines.Length];
            for (int i = 0; i < _selectedThingPlanLines.Length; i++)
            {
                content.text = _selectedThingPlanLines[i];
                lineHeights[i] = _selectedPawnPlanButtonStyle.CalcHeight(content, width);
            }
        }

        float totalHeight = headerHeight + participationHeight;
        if (lineHeights.Length > 0)
        {
            for (int i = 0; i < lineHeights.Length; i++)
            {
                totalHeight += lineHeights[i];
            }
        }

        if (totalHeight <= 0f)
        {
            return false;
        }

        var panelRect = new Rect(horizontalPosition, verticalPosition, width, Mathf.Max(0f, totalHeight));
        planPanelRect = panelRect;

        if (selectedPawnPanelBackgroundColor.a > 0f && Texture2D.whiteTexture != null)
        {
            float padX = Mathf.Max(0f, selectedPawnPanelPadding.x);
            float padY = Mathf.Max(0f, selectedPawnPanelPadding.y);
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

        float currentY = verticalPosition;

        if (headerHeight > 0f)
        {
            content.text = _selectedThingHeader;
            var headerRect = new Rect(horizontalPosition, currentY, width, headerHeight);
            GUI.Label(headerRect, content, _selectedPawnPanelStyle);
            currentY += headerHeight;
        }

        if (participationHeight > 0f)
        {
            content.text = participationLabel;
            var participationRect = new Rect(horizontalPosition, currentY, width, participationHeight);
            GUI.Label(participationRect, content, _selectedPawnPanelStyle);
            currentY += participationHeight;
        }

        if (_selectedThingPlanLines.Length == 0)
        {
            return true;
        }

        bool allowManual = _selectedPawnId.HasValue && _manualPawnIds.Contains(_selectedPawnId.Value);
        for (int i = 0; i < _selectedThingPlanLines.Length; i++)
        {
            float lineHeight = lineHeights[i];
            var buttonRect = new Rect(horizontalPosition, currentY, width, lineHeight);
            var previousBackground = GUI.backgroundColor;
            var previousEnabled = GUI.enabled;

            string lineText = i < _selectedThingPlanLines.Length ? _selectedThingPlanLines[i] : string.Empty;
            PlanActionOption matchedOption = i < _selectedThingParticipation.Length
                ? FindMatchingPlanOption(_selectedThingParticipation[i], lineText)
                : null;
            bool isSelected = _selectedPlanOptionIndex.HasValue && _selectedPlanOptionIndex.Value == i;
            if (isSelected)
            {
                GUI.backgroundColor = Color.Lerp(Color.white, Color.cyan, 0.5f);
            }

            bool isActionable = allowManual && matchedOption != null && matchedOption.IsActionable;
            GUI.enabled = isActionable;

            if (GUI.Button(buttonRect, _selectedThingPlanLines[i], _selectedPawnPlanButtonStyle) && matchedOption != null)
            {
                HandlePlanOptionInvoked(i);
            }

            GUI.enabled = previousEnabled;
            GUI.backgroundColor = previousBackground;
            currentY += lineHeight;
        }

        return true;
    }

    private PlanActionOption FindMatchingPlanOption(ThingPlanParticipation participation, string fallbackLabel)
    {
        if (_selectedThingId == null)
        {
            return null;
        }

        PlanActionOption targetMatch = null;
        for (int i = 0; i < _selectedPawnPlanOptions.Count; i++)
        {
            var option = _selectedPawnPlanOptions[i];
            if (!option.TargetId.HasValue)
            {
                continue;
            }

            if (!NullableThingIdEquals(option.TargetId, _selectedThingId))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(participation.GoalId) &&
                !string.Equals(option.GoalId, participation.GoalId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(option.ActivityId, participation.Activity, StringComparison.OrdinalIgnoreCase))
            {
                return option;
            }

            targetMatch ??= option;
        }

        if (targetMatch != null)
        {
            return targetMatch;
        }

        return CreateFallbackPlanOption(participation, fallbackLabel);
    }

    private void SyncPlanSelectionToThing()
    {
        if (_selectedThingId == null || _selectedThingParticipation.Length == 0)
        {
            _selectedPlanOptionIndex = null;
            _selectedPlanOptionLabel = string.Empty;
            return;
        }

        if (_selectedPlanOptionIndex.HasValue)
        {
            int index = _selectedPlanOptionIndex.Value;
            if (index >= 0 && index < _selectedThingParticipation.Length)
            {
                var participation = _selectedThingParticipation[index];
                var fallbackLabel = index < _selectedThingPlanLines.Length ? _selectedThingPlanLines[index] : string.Empty;
                var currentMatch = FindMatchingPlanOption(participation, fallbackLabel);
                if (currentMatch != null)
                {
                    _selectedPlanOptionLabel = index < _selectedThingPlanLines.Length
                        ? _selectedThingPlanLines[index]
                        : FormatPlanOptionDisplay(currentMatch, index);
                    return;
                }
            }

            _selectedPlanOptionIndex = null;
            _selectedPlanOptionLabel = string.Empty;
        }

        for (int i = 0; i < _selectedThingParticipation.Length; i++)
        {
            var participation = _selectedThingParticipation[i];
            var fallbackLabel = i < _selectedThingPlanLines.Length ? _selectedThingPlanLines[i] : string.Empty;
            var matchedOption = FindMatchingPlanOption(participation, fallbackLabel);
            if (matchedOption == null)
            {
                continue;
            }

            _selectedPlanOptionIndex = i;
            _selectedPlanOptionLabel = i < _selectedThingPlanLines.Length
                ? _selectedThingPlanLines[i]
                : FormatPlanOptionDisplay(matchedOption, i);
            return;
        }

        _selectedPlanOptionIndex = null;
        _selectedPlanOptionLabel = string.Empty;
    }

    private void PopulateSelectedPawnPlan(ThingId selectedId, IWorldSnapshot snapshot)
    {
        _selectedPawnPlanSnapshotVersion = snapshot?.Version ?? -1;
        _selectedPawnPlanOptions.Clear();
        _selectedPawnAllActionablePlanOptions.Clear();
        _selectedPawnActionablePlanOptions.Clear();
        _selectedPawnPlanStatus = null;

        if (bootstrapper != null && bootstrapper.TryGetActorPlanStatus(selectedId, out var status) && status != null)
        {
            _selectedPawnPlanStatus = status;
            var resolvedGoalId = status.GoalId;
            _selectedPawnPlanGoal = string.IsNullOrWhiteSpace(resolvedGoalId) ? string.Empty : resolvedGoalId.Trim();
            _selectedPawnPlanState = HumanizeIdentifier(status.State);
            _selectedPawnPlanCurrentStep = status.CurrentStep ?? string.Empty;
            _selectedPawnPlanUpdatedUtc = status.UpdatedUtc;
            if (status.Steps != null)
            {
                for (int i = 0; i < status.Steps.Count; i++)
                {
                    var step = status.Steps[i];
                    if (string.IsNullOrWhiteSpace(step))
                    {
                        continue;
                    }

                    var trimmed = step.Trim();
                    var option = BuildPlanActionOption(trimmed, snapshot, i, _selectedPawnPlanGoal);
                    _selectedPawnPlanOptions.Add(option);
                    if (option.IsActionable && option.TargetId.HasValue && option.TargetPosition.HasValue)
                    {
                        _selectedPawnAllActionablePlanOptions.Add(option);
                    }
                }
            }
        }
        else
        {
            _selectedPawnPlanGoal = string.Empty;
            _selectedPawnPlanState = string.Empty;
            _selectedPawnPlanCurrentStep = string.Empty;
            _selectedPawnPlanUpdatedUtc = default;
            _selectedPawnPlanOptions.Clear();
            _selectedPawnAllActionablePlanOptions.Clear();
            _selectedPawnActionablePlanOptions.Clear();
        }

        RefreshActionablePlanOptionsForSelection();
        SyncPlanSelectionToThing();
    }

    private void RefreshActionablePlanOptionsForSelection()
    {
        _selectedPawnActionablePlanOptions.Clear();

        if (_selectedPawnAllActionablePlanOptions.Count == 0)
        {
            return;
        }

        if (!_selectedThingId.HasValue)
        {
            return;
        }

        var selectedThingId = _selectedThingId.Value;
        for (int i = 0; i < _selectedPawnAllActionablePlanOptions.Count; i++)
        {
            var option = _selectedPawnAllActionablePlanOptions[i];
            if (!option.TargetId.HasValue || !option.TargetPosition.HasValue)
            {
                continue;
            }

            if (NullableThingIdEquals(option.TargetId, selectedThingId))
            {
                _selectedPawnActionablePlanOptions.Add(option);
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
        bool selectedPawnManual = _manualPawnIds.Contains(selectedId);
        if (selectedPawnManual && !_manualPlanAutoEvaluationEnabled)
        {
            builder.AppendLine("  Automatic plan evaluation disabled.");
            builder.AppendLine("  Select a plan step to command this pawn manually.");
            hasPlanContent = true;
        }
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

        if (_selectedPawnAllActionablePlanOptions.Count > 0)
        {
            builder.AppendLine("  Steps:");
            hasPlanContent = true;
            if (_selectedPawnActionablePlanOptions.Count == 0)
            {
                builder.AppendLine("    Select a plan target to view manual steps.");
            }
        }

        string beforeStepsText = builder.ToString();

        if (_selectedPawnActionablePlanOptions.Count > 0)
        {
            var stepLines = new string[_selectedPawnActionablePlanOptions.Count];
            for (int i = 0; i < _selectedPawnActionablePlanOptions.Count; i++)
            {
                var option = _selectedPawnActionablePlanOptions[i];
                var displayLabel = FormatPlanOptionDisplay(option, i);
                stepLines[i] = string.Concat("    ", displayLabel);
            }

            _selectedPawnPlanStepLines = stepLines;
        }
        else
        {
            _selectedPawnPlanStepLines = Array.Empty<string>();
        }

        builder.Clear();

        if (_selectedPawnPlanUpdatedUtc != default)
        {
            builder.Append("  Updated: ")
                .Append(_selectedPawnPlanUpdatedUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
                .Append("Z")
                .AppendLine();
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

        var afterStepsText = builder.ToString();
        builder.Clear();

        _selectedPawnPanelTextBeforePlanSteps = beforeStepsText;
        _selectedPawnPanelTextAfterPlanSteps = afterStepsText;
        var combined = string.Concat(beforeStepsText, afterStepsText);
        _selectedPawnPanelText = combined.TrimEnd('\r', '\n');
        return _selectedPawnPanelText;
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
        _selectedPawnPlanStepLines = Array.Empty<string>();
        _selectedPawnPlanStatus = null;
        _selectedPawnPlanOptions.Clear();
        _selectedPawnAllActionablePlanOptions.Clear();
        _selectedPawnActionablePlanOptions.Clear();
        _selectedPlanOptionIndex = null;
        _selectedPlanOptionLabel = string.Empty;
        _selectedPawnPanelTextBeforePlanSteps = string.Empty;
        _selectedPawnPanelTextAfterPlanSteps = string.Empty;
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
        if (trimmed.Length == 0)
        {
            return false;
        }

        var normalizedForRootCheck = NormalizeManifestPathForRootCheck(trimmed);

        if (Path.IsPathRooted(normalizedForRootCheck))
        {
            if (File.Exists(normalizedForRootCheck))
            {
                absolutePath = Path.GetFullPath(normalizedForRootCheck);
                return true;
            }

            var root = Path.GetPathRoot(normalizedForRootCheck);
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            var remainder = normalizedForRootCheck.Substring(root.Length);
            return TryResolveRelativePathCaseInsensitive(root, remainder, out absolutePath);
        }

        var normalizedRelative = normalizedForRootCheck.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
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

    private static string NormalizeManifestPathForRootCheck(string trimmed)
    {
        if (string.IsNullOrEmpty(trimmed))
        {
            return trimmed;
        }

        if (IsUncPath(trimmed) || IsDriveQualifiedPath(trimmed))
        {
            return trimmed;
        }

        if (trimmed.Length > 0 && (trimmed[0] == '/' || trimmed[0] == '\\'))
        {
            if (trimmed.Length == 1)
            {
                return string.Empty;
            }

            if (trimmed[1] != '/' && trimmed[1] != '\\')
            {
                return trimmed.Substring(1);
            }
        }

        return trimmed;
    }

    private static bool IsUncPath(string path)
    {
        return path.Length >= 2 &&
               ((path[0] == '/' && path[1] == '/') || (path[0] == '\\' && path[1] == '\\'));
    }

    private static bool IsDriveQualifiedPath(string path)
    {
        return path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]);
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
        ClearThingHover(clearThingSelection: true);
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
        ClearThingHover(clearThingSelection: true);
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
        _selectedPawnPlanButtonStyle = null;
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
            ClearSelectedThingPlan();
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

    private ThingId? DetectClickedPawn(IWorldSnapshot snapshot, GridPos gridPos)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
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

    private ThingId? DetectClickedThing(IWorldSnapshot snapshot, GridPos gridPos)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        foreach (var thing in snapshot.AllThings())
        {
            if (thing == null)
            {
                throw new InvalidOperationException("World snapshot returned a null thing entry while detecting clicked thing.");
            }

            if (_pawnDefinitions.ContainsKey(thing.Id))
            {
                continue;
            }

            if (!thing.Position.Equals(gridPos))
            {
                continue;
            }

            var confirmed = snapshot.GetThing(thing.Id);
            if (confirmed == null)
            {
                throw new InvalidOperationException($"World snapshot no longer contains thing '{thing.Id.Value}'.");
            }

            return thing.Id;
        }

        return null;
    }

    private string FormatThingHoverLabel(ThingView thing)
    {
        if (thing == null)
        {
            throw new ArgumentNullException(nameof(thing));
        }

        if (thing.Type == null)
        {
            throw new InvalidOperationException($"Thing '{thing.Id.Value}' type is missing; cannot build hover label.");
        }

        var trimmed = thing.Type.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException($"Thing '{thing.Id.Value}' type is empty; cannot build hover label.");
        }

        var normalized = trimmed.Replace('_', ' ');
        var lower = normalized.ToLowerInvariant();
        var textInfo = CultureInfo.InvariantCulture.TextInfo;
        return textInfo.ToTitleCase(lower);
    }

    private bool TryProjectScreenToGrid(IWorldSnapshot snapshot, Vector2 screen, out GridPos gridPos)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (!float.IsFinite(screen.x) || !float.IsFinite(screen.y))
        {
            throw new InvalidOperationException("Mouse position produced a non-finite value.");
        }

        EnsureObserverCamera();

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
            gridPos = default;
            return false;
        }

        gridPos = new GridPos(gridX, gridY);
        return true;
    }

    private bool TryGetClickGrid(IWorldSnapshot snapshot, out GridPos gridPos)
    {
        gridPos = default;
        if (!TryReadPointerClick(out var screen))
        {
            return false;
        }

        return TryProjectScreenToGrid(snapshot, screen, out gridPos);
    }

    private bool TryReadPointerScreenPosition(out Vector2 screenPosition)
    {
        var mouse = Mouse.current;
        if (mouse != null)
        {
            var value = mouse.position.ReadValue();
            if (!float.IsFinite(value.x) || !float.IsFinite(value.y))
            {
                throw new InvalidOperationException("Mouse position produced a non-finite value.");
            }

            screenPosition = value;
            return true;
        }

        if (!Input.mousePresent)
        {
            screenPosition = default;
            return false;
        }

        var legacyPosition = Input.mousePosition;
        if (!float.IsFinite(legacyPosition.x) || !float.IsFinite(legacyPosition.y))
        {
            throw new InvalidOperationException("Mouse position produced a non-finite value.");
        }

        screenPosition = new Vector2(legacyPosition.x, legacyPosition.y);
        return true;
    }

    private bool TryReadPointerClick(out Vector2 screenPosition)
    {
        var mouse = Mouse.current;
        if (mouse != null)
        {
            if (!mouse.leftButton.wasPressedThisFrame)
            {
                screenPosition = default;
                return false;
            }

            var value = mouse.position.ReadValue();
            if (!float.IsFinite(value.x) || !float.IsFinite(value.y))
            {
                throw new InvalidOperationException("Mouse position produced a non-finite value.");
            }

            screenPosition = value;
            return true;
        }

        if (!Input.mousePresent || !Input.GetMouseButtonDown(0))
        {
            screenPosition = default;
            return false;
        }

        var legacyPosition = Input.mousePosition;
        if (!float.IsFinite(legacyPosition.x) || !float.IsFinite(legacyPosition.y))
        {
            throw new InvalidOperationException("Mouse position produced a non-finite value.");
        }

        screenPosition = new Vector2(legacyPosition.x, legacyPosition.y);
        return true;
    }

    private PlanActionOption BuildPlanActionOption(
        string stepLabel,
        IWorldSnapshot snapshot,
        int stepIndex,
        string planGoalId)
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
        if (string.IsNullOrWhiteSpace(planGoalId))
        {
            throw new InvalidOperationException(
                $"Plan step '{trimmed}' cannot be constructed because its goal identifier is missing.");
        }

        var normalizedGoalId = planGoalId.Trim();
        if (normalizedGoalId.Length == 0)
        {
            throw new InvalidOperationException(
                $"Plan step '{trimmed}' cannot be constructed because its goal identifier is empty after trimming.");
        }

        string formattedLabel = string.Format(CultureInfo.InvariantCulture, "{0}. {1}", stepIndex + 1, trimmed);
        string activityId = ExtractPlanActivityIdentifier(trimmed);
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

        return new PlanActionOption(
            formattedLabel,
            trimmed,
            activityId,
            targetId,
            targetPosition,
            stepIndex,
            actionable,
            normalizedGoalId);
    }

    private static string ExtractPlanActivityIdentifier(string stepLabel)
    {
        if (string.IsNullOrWhiteSpace(stepLabel))
        {
            throw new ArgumentException("Plan step label must be provided to extract an activity identifier.", nameof(stepLabel));
        }

        var separator = stepLabel.IndexOf("->", StringComparison.Ordinal);
        string candidate = separator >= 0 ? stepLabel.Substring(0, separator) : stepLabel;
        candidate = candidate.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            throw new InvalidOperationException($"Plan step '{stepLabel}' does not include a valid activity identifier.");
        }

        return candidate;
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

    private void HandlePlanOptionInvoked(int participationIndex)
    {
        if (_selectedThingId == null)
        {
            throw new InvalidOperationException("No thing is currently selected for manual plan execution.");
        }

        if (participationIndex < 0 || participationIndex >= _selectedThingParticipation.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(participationIndex));
        }

        var participation = _selectedThingParticipation[participationIndex];
        var fallbackLabel = participationIndex < _selectedThingPlanLines.Length
            ? _selectedThingPlanLines[participationIndex]
            : string.Empty;
        var option = FindMatchingPlanOption(participation, fallbackLabel);
        if (option == null)
        {
            throw new InvalidOperationException(
                $"No matching plan option is available for participation entry '{participation}'.");
        }

        if (!option.IsActionable || !option.TargetId.HasValue || !option.TargetPosition.HasValue)
        {
            throw new InvalidOperationException(
                $"Plan option '{option.Label}' does not resolve to a valid actionable target.");
        }

        if (string.IsNullOrWhiteSpace(option.GoalId))
        {
            throw new InvalidOperationException(
                $"Plan option '{option.Label}' is missing the associated goal identifier required for manual execution.");
        }

        var participationGoalId = participation.GoalId?.Trim();
        if (string.IsNullOrEmpty(participationGoalId))
        {
            throw new InvalidOperationException(
                $"Participation entry '{participation}' is missing the associated goal identifier required for manual execution.");
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

        playerPawnController.RequestManualInteract(
            option.TargetId.Value,
            option.TargetPosition.Value,
            option.StepIndex,
            _selectedPawnPlanSnapshotVersion,
            option.ActivityId,
            participationGoalId);
        _selectedPlanOptionIndex = participationIndex;
        _selectedPlanOptionLabel = participationIndex < _selectedThingPlanLines.Length
            ? _selectedThingPlanLines[participationIndex]
            : FormatPlanOptionDisplay(option, participationIndex);
    }

    private static string FormatPlanOptionDisplay(PlanActionOption option, int displayIndex)
    {
        if (option == null)
        {
            throw new ArgumentNullException(nameof(option));
        }

        if (displayIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(displayIndex));
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}. {1}",
            displayIndex + 1,
            option.RawLabel);
    }

    private static string StripPlanDisplayIndex(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        var trimmed = label.Trim();
        int dotIndex = trimmed.IndexOf('.');
        if (dotIndex <= 0)
        {
            return trimmed;
        }

        for (int i = 0; i < dotIndex; i++)
        {
            if (!char.IsDigit(trimmed[i]))
            {
                return trimmed;
            }
        }

        var remainder = trimmed.Substring(dotIndex + 1).TrimStart();
        return remainder.Length > 0 ? remainder : trimmed;
    }

    private void HandlePlanStepButtonClicked(PlanActionOption option)
    {
        if (option == null)
        {
            throw new ArgumentNullException(nameof(option));
        }

        if (_world == null)
        {
            throw new InvalidOperationException("Manual plan step selection requires an active world snapshot provider.");
        }

        if (!option.TargetId.HasValue || !option.TargetPosition.HasValue)
        {
            throw new InvalidOperationException(
                $"Plan step '{option.Label}' does not define a valid target for manual execution.");
        }

        var snapshot = _world.Snap();
        var targetThing = snapshot.GetThing(option.TargetId.Value);
        if (targetThing == null)
        {
            throw new InvalidOperationException(
                $"Manual plan step '{option.Label}' references target '{option.TargetId.Value.Value ?? "<unknown>"}' " +
                "that is not present in the current world snapshot.");
        }

        UpdateSelectedThingPlan(targetThing);

        int participationIndex = -1;
        for (int i = 0; i < _selectedThingParticipation.Length; i++)
        {
            var participation = _selectedThingParticipation[i];
            var fallbackLabel = i < _selectedThingPlanLines.Length ? _selectedThingPlanLines[i] : string.Empty;
            var matchedOption = FindMatchingPlanOption(participation, fallbackLabel);
            if (matchedOption != null && ReferenceEquals(matchedOption, option))
            {
                participationIndex = i;
                break;
            }
        }

        if (participationIndex < 0)
        {
            throw new InvalidOperationException(
                $"Manual plan step '{option.Label}' did not map to a selectable plan option for target '{option.TargetId.Value.Value ?? "<unknown>"}'.");
        }

        HandlePlanOptionInvoked(participationIndex);
    }

    private PlanActionOption CreateFallbackPlanOption(ThingPlanParticipation participation, string fallbackLabel)
    {
        if (_selectedThingId == null || !_selectedThingGridPosition.HasValue)
        {
            return null;
        }

        var goalId = participation.GoalId;
        var activityId = participation.Activity;
        if (string.IsNullOrWhiteSpace(goalId) || string.IsNullOrWhiteSpace(activityId))
        {
            return null;
        }

        string label = string.IsNullOrWhiteSpace(fallbackLabel)
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0} (Manual) -> {1}",
                activityId,
                _selectedThingId.Value.Value ?? string.Empty)
            : fallbackLabel.Trim();

        if (label.Length == 0)
        {
            label = activityId;
        }

        var rawLabel = StripPlanDisplayIndex(label);
        if (string.IsNullOrEmpty(rawLabel))
        {
            rawLabel = label;
        }

        return new PlanActionOption(
            label,
            rawLabel,
            activityId,
            _selectedThingId.Value,
            _selectedThingGridPosition.Value,
            0,
            true,
            goalId);
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
        public PlanActionOption(
            string label,
            string rawLabel,
            string activityId,
            ThingId? targetId,
            GridPos? targetPosition,
            int stepIndex,
            bool isActionable,
            string goalId)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                throw new ArgumentException("Plan option label must be provided.", nameof(label));
            }

            if (string.IsNullOrWhiteSpace(rawLabel))
            {
                throw new ArgumentException("Plan option raw label must be provided.", nameof(rawLabel));
            }

            if (string.IsNullOrWhiteSpace(activityId))
            {
                throw new ArgumentException("Plan option activity identifier must be provided.", nameof(activityId));
            }

            if (string.IsNullOrWhiteSpace(goalId))
            {
                throw new ArgumentException("Plan option goal identifier must be provided.", nameof(goalId));
            }

            Label = label;
            RawLabel = rawLabel;
            ActivityId = activityId;
            TargetId = targetId;
            TargetPosition = targetPosition;
            StepIndex = stepIndex;
            IsActionable = isActionable;
            GoalId = goalId.Trim();
            if (GoalId.Length == 0)
            {
                throw new ArgumentException(
                    "Plan option goal identifier must contain at least one non-whitespace character.", nameof(goalId));
            }
        }

        public string Label { get; }
        public string RawLabel { get; }
        public string ActivityId { get; }
        public ThingId? TargetId { get; }
        public GridPos? TargetPosition { get; }
        public int StepIndex { get; }
        public bool IsActionable { get; }
        public string GoalId { get; }
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
