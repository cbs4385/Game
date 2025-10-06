using System;
using UnityEngine;
using UnityEngine.UIElements;

[DisallowMultipleComponent]
[RequireComponent(typeof(UIDocument))]
public sealed class InventoryUIDocumentAuthoring : MonoBehaviour
{
    [SerializeField] private PanelSettings panelSettings;
    [SerializeField] private VisualTreeAsset visualTree;

    private void Awake()
    {
        var document = GetComponent<UIDocument>();
        if (document == null)
        {
            throw new InvalidOperationException("InventoryUIDocumentAuthoring requires a UIDocument component.");
        }

        if (panelSettings == null)
        {
            throw new InvalidOperationException("InventoryUIDocumentAuthoring requires a PanelSettings asset reference.");
        }

        if (visualTree == null)
        {
            throw new InvalidOperationException("InventoryUIDocumentAuthoring requires a VisualTreeAsset reference.");
        }

        document.panelSettings = panelSettings;
        document.visualTreeAsset = visualTree;
    }
}
