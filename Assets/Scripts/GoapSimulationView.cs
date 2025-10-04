using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.World;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class GoapSimulationView : MonoBehaviour
{
    [SerializeField] private GoapSimulationBootstrapper bootstrapper;
    [SerializeField] private Transform pawnContainer;
    [SerializeField] private Camera observerCamera;
    [SerializeField] private int mapSortingOrder = -100;
    [SerializeField] private int pawnSortingOrder = 0;
    [SerializeField] private Vector2 clockScreenOffset = new Vector2(16f, 16f);
    [SerializeField] private Vector2 clockBackgroundPadding = new Vector2(12f, 8f);
    [SerializeField] private Color clockTextColor = Color.white;
    [SerializeField] private Color clockBackgroundColor = new Color(0f, 0f, 0f, 0.65f);
    [SerializeField, Min(1)] private int clockFontSize = 18;
    [SerializeField] private string clockLabelTemplate = "Year {0}, Day {1:D3} — {2:hh\\:mm\\:ss}";

    private readonly Dictionary<ThingId, PawnVisual> _pawnVisuals = new Dictionary<ThingId, PawnVisual>();
    private readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> _textureCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _pawnSpritePaths = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    private readonly GUIContent _clockGuiContent = new GUIContent();

    private ShardedWorld _world;
    private IReadOnlyList<(ThingId Id, VillagePawn Pawn)> _actors;
    private string _datasetRoot;
    private GameObject _mapObject;
    private Sprite _mapSprite;
    private Texture2D _mapTexture;
    private bool _ownsMapTexture;
    private Transform _pawnRoot;
    private WorldClock _clock;
    private string _clockLabel = string.Empty;
    private GUIStyle _clockStyle;
    private ThingId? _selectedPawnId;

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
            return;
        }

        var snapshot = _world.Snap();
        foreach (var entry in _pawnVisuals)
        {
            var thing = snapshot.GetThing(entry.Key);
            if (thing == null)
            {
                throw new InvalidOperationException($"World snapshot no longer contains actor '{entry.Key.Value}'.");
            }

            UpdatePawnPosition(entry.Value, thing.Position);
        }

        UpdateObserverCamera(snapshot);
    }

    private void HandleBootstrapped(object sender, GoapSimulationBootstrapper.SimulationReadyEventArgs args)
    {
        if (args == null)
        {
            throw new ArgumentNullException(nameof(args));
        }

        EnsureObserverCamera();

        if (_world != null)
        {
            DisposeVisuals();
        }

        _world = args.World ?? throw new InvalidOperationException("Bootstrapper emitted a null world instance.");
        _actors = args.ActorDefinitions ?? throw new InvalidOperationException("Bootstrapper emitted null actor definitions.");
        _datasetRoot = args.DatasetRoot ?? throw new InvalidOperationException("Bootstrapper emitted a null dataset root path.");
        _clock = args.Clock ?? throw new InvalidOperationException("Bootstrapper emitted a null world clock instance.");
        _selectedPawnId = ParseSelectedPawnId(args.CameraPawnId);

        EnsurePawnContainer();
        LoadSpriteManifest(Path.Combine(_datasetRoot, "sprites_manifest.json"));

        var snapshot = _world.Snap();
        LoadMap(args.MapTexture, snapshot.Width, snapshot.Height);
        CreatePawnVisuals(snapshot);
        ValidateSelectedPawnPresence();
        UpdateObserverCamera(snapshot);
        UpdateClockDisplay();
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

            var visual = new PawnVisual(pawnObject.transform, renderer, spritePaths);
            _pawnVisuals.Add(actor.Id, visual);
            UpdatePawnPosition(visual, thing.Position);
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

    private void UpdateObserverCamera(IWorldSnapshot snapshot)
    {
        if (_selectedPawnId == null)
        {
            return;
        }

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

        var cameraTransform = observerCamera.transform;
        var currentZ = cameraTransform.position.z;
        var pawnWorldPosition = visual.Root.position;
        var target = new Vector3(pawnWorldPosition.x, pawnWorldPosition.y, currentZ);
        cameraTransform.position = target;
    }

    private void OnGUI()
    {
        if (string.IsNullOrEmpty(_clockLabel))
        {
            return;
        }

        EnsureClockStyle();

        var content = _clockGuiContent;
        var labelSize = _clockStyle.CalcSize(content);
        var x = clockScreenOffset.x;
        var y = clockScreenOffset.y;
        var labelRect = new Rect(x, y, Mathf.Max(0f, labelSize.x), Mathf.Max(0f, labelSize.y));

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

        GUI.Label(labelRect, content, _clockStyle);
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

    private void LoadMap(Texture2D mapTexture, int expectedWidth, int expectedHeight)
    {
        if (mapTexture == null)
        {
            throw new ArgumentNullException(nameof(mapTexture));
        }

        if (mapTexture.width != expectedWidth || mapTexture.height != expectedHeight)
        {
            throw new InvalidDataException($"Preloaded world map texture dimensions {mapTexture.width}x{mapTexture.height} do not match world {expectedWidth}x{expectedHeight}.");
        }

        _mapTexture = mapTexture;
        _ownsMapTexture = false;

        _mapTexture.filterMode = FilterMode.Point;
        _mapTexture.wrapMode = TextureWrapMode.Clamp;

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

    private static string ResolveSpriteAbsolutePath(string manifestPath)
    {
        var trimmed = manifestPath.Trim();
        string candidate;
        if (Path.IsPathRooted(trimmed))
        {
            candidate = trimmed;
        }
        else
        {
            if (trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(1);
            }

            trimmed = trimmed.Replace('/', Path.DirectorySeparatorChar);
            candidate = Path.Combine(Application.dataPath, trimmed);
        }

        return Path.GetFullPath(candidate);
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

    private void EnsureObserverCamera()
    {
        if (observerCamera == null)
        {
            observerCamera = Camera.main;
        }

        if (observerCamera == null)
        {
            throw new InvalidOperationException("GoapSimulationView requires a Camera reference to center on the selected pawn.");
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
        foreach (var visual in _pawnVisuals.Values)
        {
            if (visual?.Root != null)
            {
                Destroy(visual.Root.gameObject);
            }
        }

        _pawnVisuals.Clear();

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

        foreach (var texture in _textureCache.Values)
        {
            if (texture != null)
            {
                Destroy(texture);
            }
        }
        _textureCache.Clear();

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
        _selectedPawnId = null;
    }

    private static ThingId? ParseSelectedPawnId(string rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId))
        {
            return null;
        }

        return new ThingId(rawId.Trim());
    }

    private sealed class PawnVisual
    {
        public PawnVisual(Transform root, SpriteRenderer renderer, IReadOnlyDictionary<string, string> spritePaths)
        {
            Root = root ?? throw new ArgumentNullException(nameof(root));
            Renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            SpritePaths = spritePaths ?? throw new ArgumentNullException(nameof(spritePaths));
        }

        public Transform Root { get; }
        public SpriteRenderer Renderer { get; }
        public IReadOnlyDictionary<string, string> SpritePaths { get; }
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
