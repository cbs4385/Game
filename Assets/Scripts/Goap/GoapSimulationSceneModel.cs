using System.Collections.Generic;
using DataDrivenGoap.Unity;
using UnityEngine;

/// <summary>
/// Captures the core DataDrivenGoap runtime types so that other systems (such as UI)
/// can observe the simulation from the scene hierarchy.
/// </summary>
public sealed class GoapSimulationSceneModel : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private GoapSimulationBootstrapper bootstrapper;

    private readonly List<MapTile> _mapTiles = new();
    private readonly List<PawnSnapshot> _pawnSnapshots = new();

    public SimulationConfig Config { get; private set; }

    public Simulation Simulation { get; private set; }

    public IReadOnlyList<MapTile> MapTiles => _mapTiles;

    public IReadOnlyList<PawnSnapshot> PawnSnapshots => _pawnSnapshots;

    private void Reset()
    {
        bootstrapper = GetComponent<GoapSimulationBootstrapper>();
    }

    private void Awake()
    {
        if (bootstrapper == null)
        {
            bootstrapper = GetComponent<GoapSimulationBootstrapper>();
        }
    }

    private void OnEnable()
    {
        if (bootstrapper == null)
        {
            return;
        }

        bootstrapper.SimulationInitialized += HandleSimulationInitialized;

        if (bootstrapper.Simulation != null)
        {
            HandleSimulationInitialized(bootstrapper.Simulation);
        }
    }

    private void OnDisable()
    {
        if (bootstrapper != null)
        {
            bootstrapper.SimulationInitialized -= HandleSimulationInitialized;
        }

        if (Simulation != null)
        {
            UnsubscribeFromSimulation(Simulation);
            Simulation = null;
        }
    }

    public bool TryGetMapTile(Vector2Int coordinates, out MapTile tile)
    {
        for (var i = 0; i < _mapTiles.Count; i++)
        {
            if (_mapTiles[i].Coordinates == coordinates)
            {
                tile = _mapTiles[i];
                return true;
            }
        }

        tile = null;
        return false;
    }

    public bool TryGetPawnSnapshot(int id, out PawnSnapshot snapshot)
    {
        for (var i = 0; i < _pawnSnapshots.Count; i++)
        {
            if (_pawnSnapshots[i].Id == id)
            {
                snapshot = _pawnSnapshots[i];
                return true;
            }
        }

        snapshot = default;
        return false;
    }

    private void HandleSimulationInitialized(Simulation simulation)
    {
        if (simulation == null)
        {
            return;
        }

        if (Simulation != null)
        {
            UnsubscribeFromSimulation(Simulation);
        }

        Simulation = simulation;
        Config = simulation.Config;

        _mapTiles.Clear();
        foreach (var tile in simulation.Map.Tiles)
        {
            _mapTiles.Add(tile);
        }

        _pawnSnapshots.Clear();
        foreach (var pawn in simulation.GetPawnSnapshots())
        {
            _pawnSnapshots.Add(pawn);
        }

        SubscribeToSimulation(simulation);
    }

    private void SubscribeToSimulation(Simulation simulation)
    {
        simulation.TileGenerated += HandleTileGenerated;
        simulation.PawnSpawned += HandlePawnChanged;
        simulation.PawnUpdated += HandlePawnChanged;
    }

    private void UnsubscribeFromSimulation(Simulation simulation)
    {
        simulation.TileGenerated -= HandleTileGenerated;
        simulation.PawnSpawned -= HandlePawnChanged;
        simulation.PawnUpdated -= HandlePawnChanged;
    }

    private void HandleTileGenerated(MapTile tile)
    {
        for (var i = 0; i < _mapTiles.Count; i++)
        {
            if (_mapTiles[i].Coordinates == tile.Coordinates)
            {
                _mapTiles[i] = tile;
                return;
            }
        }

        _mapTiles.Add(tile);
    }

    private void HandlePawnChanged(PawnSnapshot pawn)
    {
        for (var i = 0; i < _pawnSnapshots.Count; i++)
        {
            if (_pawnSnapshots[i].Id == pawn.Id)
            {
                _pawnSnapshots[i] = pawn;
                return;
            }
        }

        _pawnSnapshots.Add(pawn);
    }
}
