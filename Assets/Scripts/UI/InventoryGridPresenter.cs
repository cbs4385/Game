using System;
using System.Collections.Generic;
using System.Globalization;
using DataDrivenGoap.Core;
using DataDrivenGoap.Items;
using UnityEngine;
using UnityEngine.UIElements;

[RequireComponent(typeof(UIDocument))]
public sealed class InventoryGridPresenter : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private GoapSimulationBootstrapper bootstrapper;
    [SerializeField] private GoapSimulationView simulationView;
    [SerializeField] private PanelSettings panelSettings;

    [Header("Layout")]
    [SerializeField, Min(1)] private int columns = 4;
    [SerializeField, Min(16)] private int slotSize = 40;
    [SerializeField] private Vector2 panelOffset = new Vector2(16f, 280f);

    [Header("Behaviour")]
    [SerializeField, Min(0.05f)] private float refreshInterval = 0.25f;
    [SerializeField, Min(1)] private int fallbackCapacity = 12;

    private UIDocument _document;
    private VisualElement _root;
    private VisualElement _panel;
    private Label _titleLabel;
    private VisualElement _grid;
    private Label _emptyLabel;

    private ThingId? _selectedThing;
    private string _selectedHeader = "Inventory";
    private float _nextRefreshAt;
    private readonly List<SlotElements> _slotPool = new List<SlotElements>(32);
    private bool _selectionDirty;
    private bool _initialized;
    private bool _dependenciesConfigured;

    public void ConfigureDependencies(
        GoapSimulationBootstrapper requiredBootstrapper,
        GoapSimulationView requiredSimulationView,
        PanelSettings overridePanelSettings = null)
    {
        if (requiredBootstrapper == null)
        {
            throw new ArgumentNullException(nameof(requiredBootstrapper));
        }

        if (requiredSimulationView == null)
        {
            throw new ArgumentNullException(nameof(requiredSimulationView));
        }

        bootstrapper = requiredBootstrapper;
        simulationView = requiredSimulationView;
        _dependenciesConfigured = true;

        if (overridePanelSettings != null)
        {
            panelSettings = overridePanelSettings;
            if (_document != null)
            {
                _document.panelSettings = overridePanelSettings;
            }
        }

        if (isActiveAndEnabled)
        {
            InitializeIfReady();
        }
    }

    private void Awake()
    {
        _document = GetComponent<UIDocument>();
        if (_document == null)
        {
            throw new InvalidOperationException("InventoryGridPresenter requires a UIDocument component.");
        }
    }

    private void OnEnable()
    {
        InitializeIfReady();
    }

    private void OnDisable()
    {
        _initialized = false;
    }

    private void Start()
    {
        InitializeIfReady();
        if (!_initialized)
        {
            string reason = _dependenciesConfigured
                ? "InventoryGridPresenter failed to initialize even though dependencies were configured before Start."
                : "InventoryGridPresenter could not initialize because required dependencies were not configured before Start.";
            Environment.FailFast(reason, new InvalidOperationException(reason));
        }
    }

    private void InitializeIfReady()
    {
        if (_initialized)
        {
            return;
        }

        if (_document == null)
        {
            throw new InvalidOperationException("InventoryGridPresenter requires a UIDocument component.");
        }

        if (bootstrapper == null || simulationView == null)
        {
            return;
        }

        if (panelSettings != null)
        {
            _document.panelSettings = panelSettings;
        }

        _root = _document.rootVisualElement;
        if (_root == null)
        {
            throw new InvalidOperationException("InventoryGridPresenter could not access the root visual element.");
        }

        _root.style.position = Position.Absolute;
        _root.style.left = panelOffset.x;
        _root.style.top = panelOffset.y;

        EnsureLayoutElements();

        SetVisible(false);
        if (_emptyLabel != null)
        {
            _emptyLabel.style.display = DisplayStyle.None;
        }

        _grid?.Clear();
        _selectionDirty = true;
        ApplySelectionToUi();
        _initialized = true;
    }

    private void Update()
    {
        if (_grid == null || _panel == null || _titleLabel == null || _emptyLabel == null)
        {
            return;
        }

        if (_selectionDirty)
        {
            ApplySelectionToUi();
        }

        if (!_selectedThing.HasValue)
        {
            return;
        }

        if (Time.unscaledTime < _nextRefreshAt)
        {
            return;
        }

        Refresh();
        _nextRefreshAt = Time.unscaledTime + refreshInterval;
    }

    public void SetSelection(ThingId? thingId, string header)
    {
        _selectedThing = thingId;
        _selectedHeader = string.IsNullOrWhiteSpace(header) ? "Inventory" : header.Trim();
        _selectionDirty = true;
        ApplySelectionToUi();
    }

    private void Refresh()
    {
        if (_grid == null || _panel == null || _titleLabel == null || _emptyLabel == null)
        {
            return;
        }

        if (!_selectedThing.HasValue)
        {
            return;
        }

        var ownerId = _selectedThing.Value;
        bool hasSlots = bootstrapper.TryGetInventorySlots(ownerId, out var slotSnapshot);
        if (slotSnapshot == null)
        {
            throw new InvalidOperationException($"Inventory slot snapshot provider returned null for owner '{ownerId.Value ?? string.Empty}'.");
        }

        IReadOnlyList<InventoryStackView> slots;
        if (hasSlots)
        {
            slots = slotSnapshot;
        }
        else
        {
            if (!bootstrapper.TryGetInventoryContents(ownerId, out var stacks) || stacks == null)
            {
                _grid.Clear();
                _emptyLabel.style.display = DisplayStyle.Flex;
                return;
            }

            int desiredCapacity = Math.Max(fallbackCapacity, stacks.Count);
            var padded = new InventoryStackView[desiredCapacity];
            for (int i = 0; i < stacks.Count; i++)
            {
                padded[i] = stacks[i];
            }

            for (int i = stacks.Count; i < desiredCapacity; i++)
            {
                padded[i] = new InventoryStackView(null, 0, 0);
            }

            slots = padded;
        }

        int capacity = slots.Count;
        if (capacity <= 0)
        {
            _grid.Clear();
            _emptyLabel.style.display = DisplayStyle.Flex;
            return;
        }

        _emptyLabel.style.display = DisplayStyle.None;
        float totalWidth = columns * slotSize + Mathf.Max(0, columns - 1) * 4f;
        _grid.style.width = totalWidth;

        EnsureSlotPool(capacity);

        for (int i = 0; i < capacity; i++)
        {
            var elements = _slotPool[i];
            if (elements.Root.parent != _grid)
            {
                _grid.Add(elements.Root);
            }

            elements.Root.style.width = slotSize;
            elements.Root.style.height = slotSize;

            var stack = slots[i];
            bool hasItem = stack.Item != null && stack.Quantity > 0;
            elements.Root.EnableInClassList("inv-slot--empty", !hasItem);
            elements.Root.style.opacity = hasItem ? 1f : 0.35f;

            if (hasItem)
            {
                var sprite = ResolveItemSprite(stack);
                elements.Root.style.backgroundImage = sprite != null ? new StyleBackground(sprite) : default;
                elements.Quantity.text = stack.Quantity.ToString(CultureInfo.InvariantCulture);
                elements.Root.tooltip = BuildTooltip(stack);
            }
            else
            {
                elements.Root.style.backgroundImage = default;
                elements.Quantity.text = string.Empty;
                elements.Root.tooltip = "Empty";
            }
        }

        for (int i = capacity; i < _slotPool.Count; i++)
        {
            var elements = _slotPool[i];
            if (elements.Root.parent == _grid)
            {
                _grid.Remove(elements.Root);
            }
        }
    }

    private void ApplySelectionToUi()
    {
        if (_panel == null || _titleLabel == null || _grid == null || _emptyLabel == null)
        {
            return;
        }

        _selectionDirty = false;
        _titleLabel.text = _selectedHeader;

        if (!_selectedThing.HasValue)
        {
            SetVisible(false);
            _grid.Clear();
            _emptyLabel.style.display = DisplayStyle.None;
            return;
        }

        SetVisible(true);
        Refresh();
        _nextRefreshAt = Time.unscaledTime + refreshInterval;
    }

    private void SetVisible(bool visible)
    {
        if (_panel != null)
        {
            _panel.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }

    private void EnsureSlotPool(int capacity)
    {
        while (_slotPool.Count < capacity)
        {
            int slotIndex = _slotPool.Count;
            var root = new VisualElement();
            root.AddToClassList("inv-slot");

            ApplySlotStyle(root);

            var quantityLabel = new Label { name = "Quantity" };
            quantityLabel.AddToClassList("inv-qty");
            ApplyQuantityLabelStyle(quantityLabel);
            quantityLabel.pickingMode = PickingMode.Ignore;
            root.Add(quantityLabel);

            root.RegisterCallback<ClickEvent>(_ => OnSlotClicked(slotIndex));

            _slotPool.Add(new SlotElements(root, quantityLabel, slotIndex));
        }
    }

    private void EnsureLayoutElements()
    {
        _panel = _root.Q<VisualElement>("InventoryPanel");
        _titleLabel = _root.Q<Label>("Title");
        _grid = _root.Q<VisualElement>("Grid");
        _emptyLabel = _root.Q<Label>("EmptyMessage");

        if (_panel != null && _titleLabel != null && _grid != null && _emptyLabel != null)
        {
            return;
        }

        _root.Clear();

        _panel = new VisualElement { name = "InventoryPanel" };
        _panel.AddToClassList("inv-panel");
        ApplyPanelStyle(_panel);
        _root.Add(_panel);

        _titleLabel = new Label("Inventory") { name = "Title" };
        _titleLabel.AddToClassList("inv-title");
        ApplyTitleStyle(_titleLabel);
        _panel.Add(_titleLabel);

        _grid = new VisualElement { name = "Grid" };
        _grid.AddToClassList("inv-grid");
        ApplyGridStyle(_grid);
        _panel.Add(_grid);

        _emptyLabel = new Label("No inventory") { name = "EmptyMessage" };
        _emptyLabel.AddToClassList("inv-empty");
        ApplyEmptyLabelStyle(_emptyLabel);
        _panel.Add(_emptyLabel);
    }

    private static void ApplyPanelStyle(VisualElement panel)
    {
        panel.style.paddingLeft = 8f;
        panel.style.paddingRight = 8f;
        panel.style.paddingTop = 8f;
        panel.style.paddingBottom = 8f;
        panel.style.backgroundColor = new StyleColor(new Color(0f, 0f, 0f, 0.75f));
        var borderColor = new StyleColor(new Color(1f, 1f, 1f, 0.1f));
        panel.style.borderTopWidth = 1f;
        panel.style.borderRightWidth = 1f;
        panel.style.borderBottomWidth = 1f;
        panel.style.borderLeftWidth = 1f;
        panel.style.borderTopColor = borderColor;
        panel.style.borderRightColor = borderColor;
        panel.style.borderBottomColor = borderColor;
        panel.style.borderLeftColor = borderColor;
        panel.style.borderTopLeftRadius = 8f;
        panel.style.borderTopRightRadius = 8f;
        panel.style.borderBottomLeftRadius = 8f;
        panel.style.borderBottomRightRadius = 8f;
        panel.style.flexDirection = FlexDirection.Column;
    }

    private static void ApplyTitleStyle(Label title)
    {
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.fontSize = 14f;
        title.style.color = Color.white;
        title.style.marginBottom = 6f;
    }

    private static void ApplyGridStyle(VisualElement grid)
    {
        grid.style.flexDirection = FlexDirection.Row;
        grid.style.flexWrap = Wrap.Wrap;
        grid.style.marginBottom = 6f;
    }

    private static void ApplyEmptyLabelStyle(Label emptyLabel)
    {
        emptyLabel.style.color = new StyleColor(new Color(1f, 1f, 1f, 0.7f));
        emptyLabel.style.fontSize = 12f;
        emptyLabel.style.marginTop = 6f;
    }

    private static void ApplySlotStyle(VisualElement slot)
    {
        slot.style.borderTopWidth = 1f;
        slot.style.borderRightWidth = 1f;
        slot.style.borderBottomWidth = 1f;
        slot.style.borderLeftWidth = 1f;
        var borderColor = new StyleColor(new Color(1f, 1f, 1f, 0.1f));
        slot.style.borderTopColor = borderColor;
        slot.style.borderRightColor = borderColor;
        slot.style.borderBottomColor = borderColor;
        slot.style.borderLeftColor = borderColor;
        slot.style.backgroundColor = new StyleColor(new Color(1f, 1f, 1f, 0.04f));
        slot.style.borderTopLeftRadius = 6f;
        slot.style.borderTopRightRadius = 6f;
        slot.style.borderBottomLeftRadius = 6f;
        slot.style.borderBottomRightRadius = 6f;
        slot.style.backgroundImage = default;
        slot.style.flexShrink = 0f;
        slot.style.flexGrow = 0f;
        slot.style.position = Position.Relative;
        slot.style.marginRight = 4f;
        slot.style.marginBottom = 4f;
    }

    private static void ApplyQuantityLabelStyle(Label quantityLabel)
    {
        quantityLabel.style.position = Position.Absolute;
        quantityLabel.style.bottom = 2f;
        quantityLabel.style.right = 4f;
        quantityLabel.style.fontSize = 11f;
        quantityLabel.style.color = Color.white;
        quantityLabel.style.unityTextOutlineWidth = 0.4f;
        quantityLabel.style.unityTextOutlineColor = new Color(0f, 0f, 0f, 0.8f);
        quantityLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        quantityLabel.pickingMode = PickingMode.Ignore;
    }

    private void OnSlotClicked(int index)
    {
        // Reserved for future interactions (drag-and-drop, context menus, etc.).
    }

    private static string BuildTooltip(InventoryStackView stack)
    {
        if (stack.Item == null)
        {
            return "Empty";
        }

        var name = stack.Item.Id ?? "<unknown item>";
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}\nQty: {1}",
            name,
            stack.Quantity);
    }

    private Sprite ResolveItemSprite(InventoryStackView stack)
    {
        var slug = stack.Item?.SpriteSlug;
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        return simulationView.LoadSpriteAsset(slug);
    }

    private sealed class SlotElements
    {
        public SlotElements(VisualElement root, Label quantity, int index)
        {
            Root = root ?? throw new ArgumentNullException(nameof(root));
            Quantity = quantity ?? throw new ArgumentNullException(nameof(quantity));
            Index = index;
        }

        public VisualElement Root { get; }
        public Label Quantity { get; }
        public int Index { get; }
    }
}
