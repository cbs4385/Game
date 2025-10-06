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

    private void Awake()
    {
        _document = GetComponent<UIDocument>();
        if (_document == null)
        {
            throw new InvalidOperationException("InventoryGridPresenter requires a UIDocument component.");
        }

        if (bootstrapper == null)
        {
            throw new InvalidOperationException("InventoryGridPresenter requires a GoapSimulationBootstrapper reference.");
        }

        if (simulationView == null)
        {
            throw new InvalidOperationException("InventoryGridPresenter requires a GoapSimulationView reference.");
        }

        if (panelSettings != null)
        {
            _document.panelSettings = panelSettings;
        }
    }

    private void OnEnable()
    {
        _root = _document.rootVisualElement;
        if (_root == null)
        {
            throw new InvalidOperationException("InventoryGridPresenter could not access the root visual element.");
        }

        _root.style.position = Position.Absolute;
        _root.style.left = panelOffset.x;
        _root.style.top = panelOffset.y;

        _panel = _root.Q<VisualElement>("InventoryPanel") ?? throw new InvalidOperationException("Inventory panel element was not found in the document.");
        _titleLabel = _root.Q<Label>("Title") ?? throw new InvalidOperationException("Inventory title label was not found in the document.");
        _grid = _root.Q<VisualElement>("Grid") ?? throw new InvalidOperationException("Inventory grid element was not found in the document.");
        _emptyLabel = _root.Q<Label>("EmptyMessage") ?? throw new InvalidOperationException("Inventory empty label was not found in the document.");

        SetVisible(false);
        _emptyLabel.style.display = DisplayStyle.None;
        _grid.Clear();
    }

    private void Update()
    {
        if (_grid == null || _panel == null || _titleLabel == null || _emptyLabel == null)
        {
            return;
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

        if (_titleLabel == null || _panel == null || _grid == null || _emptyLabel == null)
        {
            return;
        }

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

    private void SetVisible(bool visible)
    {
        _panel.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }

    private void EnsureSlotPool(int capacity)
    {
        while (_slotPool.Count < capacity)
        {
            int slotIndex = _slotPool.Count;
            var root = new VisualElement();
            root.AddToClassList("inv-slot");

            var quantityLabel = new Label { name = "Quantity" };
            quantityLabel.AddToClassList("inv-qty");
            quantityLabel.pickingMode = PickingMode.Ignore;
            root.Add(quantityLabel);

            root.RegisterCallback<ClickEvent>(_ => OnSlotClicked(slotIndex));

            _slotPool.Add(new SlotElements(root, quantityLabel, slotIndex));
        }
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
