using System;
using System.Linq;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;
using DataDrivenGoap.World;
using UnityEngine;
using UnityEngine.InputSystem;

[DisallowMultipleComponent]
public sealed class PlayerPawnController : MonoBehaviour
{
    [SerializeField] private GoapSimulationBootstrapper bootstrapper;
    [SerializeField, Min(0.01f)] private float moveIntervalSeconds = 0.2f;

    private IWorld _world;
    private ThingId? _playerPawnId;
    private float _moveCooldown;

    private void Awake()
    {
        EnsureBootstrapperReference();
    }

    private void OnEnable()
    {
        EnsureBootstrapperReference();
        bootstrapper.Bootstrapped += HandleBootstrapped;
        if (bootstrapper.HasBootstrapped)
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

        _world = null;
        _playerPawnId = null;
        _moveCooldown = 0f;
    }

    private void Update()
    {
        if (_world == null || !_playerPawnId.HasValue)
        {
            return;
        }

        if (moveIntervalSeconds <= 0f)
        {
            throw new InvalidOperationException("PlayerPawnController requires 'moveIntervalSeconds' to be greater than zero.");
        }

        if (_moveCooldown > 0f)
        {
            _moveCooldown -= Time.deltaTime;
        }

        var direction = ReadMovementInput();
        if (direction == Vector2Int.zero)
        {
            return;
        }

        if (_moveCooldown > 0f)
        {
            return;
        }

        ExecuteMove(direction);
    }

    private void HandleBootstrapped(object sender, GoapSimulationBootstrapper.SimulationReadyEventArgs args)
    {
        if (args == null)
        {
            throw new ArgumentNullException(nameof(args));
        }

        if (args.World == null)
        {
            throw new InvalidOperationException("Bootstrapper emitted a null world instance for the player controller.");
        }

        if (!args.PlayerPawnId.HasValue)
        {
            throw new InvalidOperationException("Bootstrapper did not designate a player pawn id for manual control.");
        }

        if (args.ManualPawnIds == null || !args.ManualPawnIds.Contains(args.PlayerPawnId.Value))
        {
            throw new InvalidOperationException($"Player pawn '{args.PlayerPawnId.Value.Value}' was not flagged for manual control in the bootstrap configuration.");
        }

        _world = args.World;
        _playerPawnId = args.PlayerPawnId.Value;
        _moveCooldown = 0f;
    }

    private Vector2Int ReadMovementInput()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null)
        {
            return Vector2Int.zero;
        }

        var direction = Vector2Int.zero;
        if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed)
        {
            direction += Vector2Int.up;
        }

        if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed)
        {
            direction += Vector2Int.down;
        }

        if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
        {
            direction += Vector2Int.left;
        }

        if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
        {
            direction += Vector2Int.right;
        }

        if (direction.x != 0 && direction.y != 0)
        {
            if (Mathf.Abs(direction.x) > Mathf.Abs(direction.y))
            {
                direction.y = 0;
            }
            else
            {
                direction.x = 0;
            }
        }

        return direction;
    }

    private void ExecuteMove(Vector2Int direction)
    {
        var snapshot = _world.Snap();
        var playerId = _playerPawnId.Value;
        var playerThing = snapshot.GetThing(playerId);
        if (playerThing == null)
        {
            throw new InvalidOperationException($"World snapshot no longer contains the player pawn '{playerId.Value}'.");
        }

        var target = new GridPos(playerThing.Position.X + direction.x, playerThing.Position.Y + direction.y);
        if (!snapshot.IsWalkable(target.X, target.Y))
        {
            return;
        }

        var batch = new EffectBatch
        {
            BaseVersion = snapshot.Version,
            Reads = new[] { new ReadSetEntry(playerId, null, null) },
            Writes = new[]
            {
                new WriteSetEntry(playerId, "@move.x", target.X),
                new WriteSetEntry(playerId, "@move.y", target.Y)
            },
            FactDeltas = Array.Empty<FactDelta>(),
            Spawns = Array.Empty<ThingSpawnRequest>(),
            PlanCooldowns = Array.Empty<PlanCooldownRequest>(),
            Despawns = Array.Empty<ThingId>(),
            InventoryOps = Array.Empty<InventoryDelta>(),
            CurrencyOps = Array.Empty<CurrencyDelta>(),
            ShopTransactions = Array.Empty<ShopTransaction>(),
            RelationshipOps = Array.Empty<RelationshipDelta>(),
            CropOps = Array.Empty<CropOperation>(),
            AnimalOps = Array.Empty<AnimalOperation>(),
            MiningOps = Array.Empty<MiningOperation>(),
            FishingOps = Array.Empty<FishingOperation>(),
            ForagingOps = Array.Empty<ForagingOperation>(),
            QuestOps = Array.Empty<QuestOperation>()
        };

        var result = _world.TryCommit(batch);
        if (result == CommitResult.Conflict)
        {
            throw new InvalidOperationException($"Player movement commit conflicted while moving to ({target.X}, {target.Y}).");
        }

        _moveCooldown = moveIntervalSeconds;
    }

    private void EnsureBootstrapperReference()
    {
        if (bootstrapper == null)
        {
            bootstrapper = FindFirstObjectByType<GoapSimulationBootstrapper>();
        }

        if (bootstrapper == null)
        {
            throw new InvalidOperationException("PlayerPawnController could not locate a GoapSimulationBootstrapper in the scene.");
        }
    }
}
