// Assets/CameraZoom.cs
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal; // URP PixelPerfectCamera

public sealed class CameraScrollZoom2D : MonoBehaviour
{
    [Header("Smooth zoom (no step quantization)")]
    [SerializeField] float minOrthoSize = 2f;
    [SerializeField] float maxOrthoSize = 20f;
    [SerializeField] float zoomSpeed = 5f;

    [Header("If PixelPerfectCamera exists, use stepped zoom")]
    [SerializeField] bool usePixelPerfectSteps = true;
    [SerializeField] int minStep = 1;   // 1x
    [SerializeField] int maxStep = 8;   // 8x

    Camera _cam;
    PixelPerfectCamera _ppc;
    float _baseSize;   // ortho size at 1x (pixel-perfect)
    int _step = 1;

    void Awake()
    {
        _cam = GetComponent<Camera>();
        _ppc = GetComponent<PixelPerfectCamera>();

        if (_ppc != null)
        {
            // Base ortho size that shows refResolutionY pixels at assetsPPU
            // 2 * orthoSize (world units) == visible vertical world units
            _baseSize = _ppc.refResolutionY / (2f * _ppc.assetsPPU);
            // Initialize step from current ortho size if it’s already set
            if (_cam.orthographicSize > 0f)
            {
                _step = Mathf.Max(1, Mathf.RoundToInt(_baseSize / _cam.orthographicSize));
            }
            ApplyStepZoom();
        }
    }

    void Update()
    {
        if (Mouse.current == null) return;
        float wheel = Mouse.current.scroll.ReadValue().y;
        if (Mathf.Approximately(wheel, 0f)) return;

        // Use discrete, pixel-perfect steps if the component is present
        if (_ppc != null && usePixelPerfectSteps)
        {
            int delta = wheel > 0f ? +1 : -1; // up = zoom in
            _step = Mathf.Clamp(_step + delta, minStep, maxStep);
            ApplyStepZoom();
            return;
        }

        // Otherwise do smooth zoom by changing orthographic size (or FOV)
        if (_cam.orthographic)
        {
            float size = _cam.orthographicSize;
            size -= wheel * (zoomSpeed * 0.01f); // normalize mouse delta (~±120)
            _cam.orthographicSize = Mathf.Clamp(size, minOrthoSize, maxOrthoSize);
        }
        else
        {
            float fov = _cam.fieldOfView;
            fov -= wheel * (zoomSpeed * 0.1f);
            _cam.fieldOfView = Mathf.Clamp(fov, 10f, 80f);
        }
    }

    void ApplyStepZoom()
    {
        if (_baseSize <= 0f) return;
        _cam.orthographicSize = Mathf.Clamp(_baseSize / _step, minOrthoSize, maxOrthoSize);
    }
}
