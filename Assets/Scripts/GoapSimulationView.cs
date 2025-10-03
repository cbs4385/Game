using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.World;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class GoapSimulationView : MonoBehaviour
{
    [SerializeField] private GoapSimulationBootstrapper bootstrapper;
    [SerializeField] private Transform pawnContainer;
    [SerializeField] private int mapSortingOrder = -100;
    [SerializeField] private int pawnSortingOrder = 0;

    private readonly Dictionary<ThingId, PawnVisual> _pawnVisuals = new Dictionary<ThingId, PawnVisual>();
    private readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> _textureCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _pawnSpritePaths = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    private ShardedWorld _world;
    private IReadOnlyList<(ThingId Id, VillagePawn Pawn)> _actors;
    private string _datasetRoot;
    private GameObject _mapObject;
    private Sprite _mapSprite;
    private Texture2D _mapTexture;
    private Transform _pawnRoot;

    private void Awake()
    {
        EnsureBootstrapperReference();
    }

    private void OnEnable()
    {
        EnsureBootstrapperReference();
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
    }

    private void HandleBootstrapped(object sender, GoapSimulationBootstrapper.SimulationReadyEventArgs args)
    {
        if (args == null)
        {
            throw new ArgumentNullException(nameof(args));
        }

        if (_world != null)
        {
            DisposeVisuals();
        }

        _world = args.World ?? throw new InvalidOperationException("Bootstrapper emitted a null world instance.");
        _actors = args.ActorDefinitions ?? throw new InvalidOperationException("Bootstrapper emitted null actor definitions.");
        _datasetRoot = args.DatasetRoot ?? throw new InvalidOperationException("Bootstrapper emitted a null dataset root path.");

        EnsurePawnContainer();
        LoadSpriteManifest(Path.Combine(_datasetRoot, "sprites_manifest.json"));

        var mapPath = Path.Combine(_datasetRoot, "village_map_1000x1000.png");
        var snapshot = _world.Snap();
        LoadMap(mapPath, snapshot.Width, snapshot.Height);
        CreatePawnVisuals(snapshot);
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

    private void UpdatePawnPosition(PawnVisual visual, GridPos position)
    {
        var translated = new Vector3(position.X + 0.5f, position.Y + 0.5f, 0f);
        visual.Root.localPosition = translated;
    }

    private void LoadMap(string mapPath, int expectedWidth, int expectedHeight)
    {
        if (string.IsNullOrWhiteSpace(mapPath))
        {
            throw new ArgumentException("Map path must be provided.", nameof(mapPath));
        }

        var absolutePath = Path.GetFullPath(mapPath);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"World map image '{absolutePath}' could not be found.", absolutePath);
        }

        _mapTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        var data = File.ReadAllBytes(absolutePath);
        if (!_mapTexture.LoadImage(data, false))
        {
            Destroy(_mapTexture);
            _mapTexture = null;
            throw new InvalidDataException($"World map image '{absolutePath}' is not a valid RGBA texture.");
        }

        if (_mapTexture.width != expectedWidth || _mapTexture.height != expectedHeight)
        {
            throw new InvalidDataException($"World map image '{absolutePath}' dimensions {_mapTexture.width}x{_mapTexture.height} do not match world {expectedWidth}x{expectedHeight}.");
        }

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
        using var stream = File.OpenRead(absolutePath);
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Sprite manifest '{absolutePath}' must contain an object at the root.");
        }

        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Sprite manifest entry '{entry.Name}' must be an object.");
            }

            if (!entry.Value.TryGetProperty("sprites", out var spritesElement) || spritesElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Sprite manifest entry '{entry.Name}' must contain a 'sprites' object.");
            }

            var spritePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var spriteProperty in spritesElement.EnumerateObject())
            {
                var spritePath = spriteProperty.Value.GetString();
                if (string.IsNullOrWhiteSpace(spritePath))
                {
                    throw new InvalidDataException($"Sprite manifest entry '{entry.Name}' has an empty path for orientation '{spriteProperty.Name}'.");
                }

                spritePaths[spriteProperty.Name] = spritePath.Trim();
            }

            if (spritePaths.Count == 0)
            {
                throw new InvalidDataException($"Sprite manifest entry '{entry.Name}' does not contain any sprite paths.");
            }

            _pawnSpritePaths[entry.Name] = spritePaths;
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

    private void EnsureBootstrapperReference()
    {
        if (bootstrapper == null)
        {
            bootstrapper = FindObjectOfType<GoapSimulationBootstrapper>();
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
            Destroy(_mapTexture);
            _mapTexture = null;
        }

        _pawnSpritePaths.Clear();
        _actors = null;
        _world = null;
        _datasetRoot = null;
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
}
