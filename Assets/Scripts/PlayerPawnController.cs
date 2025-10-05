using System;
using System.Linq;
using DataDrivenGoap.Core;
using DataDrivenGoap.Effects;
using DataDrivenGoap.World;
using UnityEngine;
using UnityEngine.InputSystem;

[DisallowMultipleComponent]
[RequireComponent(typeof(PlayerInput))]
public sealed class PlayerPawnController : MonoBehaviour
{
    [SerializeField] private GoapSimulationBootstrapper bootstrapper;
    [SerializeField, Min(0.01f)] private float moveIntervalSeconds = 0.2f;
    [SerializeField] private InputActionReference moveAction;
    [SerializeField] private InputActionReference interactAction;
    [SerializeField] private InputActionAsset playerControlsAsset;
    [SerializeField] private PlayerInput playerInput;
    [SerializeField] private string defaultActionMap = "Gameplay";
    [SerializeField] private string moveActionName = "Move";
    [SerializeField] private string interactActionName = "Interact";
    [SerializeField, Range(0f, 0.99f)] private float movementDeadZone = 0.4f;

    private IWorld _world;
    private ThingId? _playerPawnId;
    private float _moveCooldown;
    private InputAction _resolvedMoveAction;
    private InputAction _resolvedInteractAction;
    private Vector2Int _lastMoveDirection;
    private long _interactionSequence;
    private ThingId? _lastInteractionFactTarget;

    public ThingId? ControlledPawnId => _playerPawnId;

    private void Awake()
    {
        EnsureBootstrapperReference();
        EnsurePlayerInputReference();
    }

    private void OnEnable()
    {
        EnsureBootstrapperReference();
        EnsurePlayerInputReference();
        BindInputActions();
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

        UnbindInputActions();
        _world = null;
        _playerPawnId = null;
        _moveCooldown = 0f;
        _lastMoveDirection = Vector2Int.zero;
        _interactionSequence = 0L;
        _lastInteractionFactTarget = null;
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

        _lastMoveDirection = direction;

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
        _lastInteractionFactTarget = null;
    }

    private Vector2Int ReadMovementInput()
    {
        if (_resolvedMoveAction == null)
        {
            throw new InvalidOperationException("PlayerPawnController requires a move action to be bound before reading input.");
        }

        var raw = _resolvedMoveAction.ReadValue<Vector2>();
        if (float.IsNaN(raw.x) || float.IsNaN(raw.y) || float.IsInfinity(raw.x) || float.IsInfinity(raw.y))
        {
            throw new InvalidOperationException("Move action produced a non-finite vector value.");
        }

        var deadZone = Mathf.Clamp(movementDeadZone, 0f, 0.99f);
        var direction = Vector2Int.zero;
        var absX = Mathf.Abs(raw.x);
        var absY = Mathf.Abs(raw.y);
        if (absX >= deadZone)
        {
            direction.x = raw.x > 0f ? 1 : -1;
        }

        if (absY >= deadZone)
        {
            direction.y = raw.y > 0f ? 1 : -1;
        }

        if (direction.x != 0 && direction.y != 0)
        {
            if (absX > absY)
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

    private void ExecuteInteract()
    {
        var snapshot = _world.Snap();
        var playerId = _playerPawnId.Value;
        var playerThing = snapshot.GetThing(playerId);
        if (playerThing == null)
        {
            throw new InvalidOperationException($"World snapshot no longer contains the player pawn '{playerId.Value}'.");
        }

        var targetPos = ResolveInteractionTarget(snapshot, playerThing);
        var targetThing = FindInteractableThing(snapshot, playerId, targetPos);
        var targetId = targetThing != null ? targetThing.Id : default;
        ExecuteManualInteract(snapshot, targetId, targetPos, planStepIndex: null);
    }

    public void RequestManualInteract(ThingId targetId, GridPos targetPos, int? planStepIndex = null)
    {
        if (_world == null)
        {
            throw new InvalidOperationException("PlayerPawnController cannot process manual interactions before the world is available.");
        }

        if (!_playerPawnId.HasValue)
        {
            throw new InvalidOperationException("PlayerPawnController has not been assigned a controlled pawn id.");
        }

        var snapshot = _world.Snap();
        ExecuteManualInteract(snapshot, targetId, targetPos, planStepIndex);
    }

    private void ExecuteManualInteract(IWorldSnapshot snapshot, ThingId targetId, GridPos targetPos, int? planStepIndex)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (!_playerPawnId.HasValue)
        {
            throw new InvalidOperationException("PlayerPawnController cannot execute manual interaction without a controlled pawn.");
        }

        var playerId = _playerPawnId.Value;
        var playerThing = snapshot.GetThing(playerId);
        if (playerThing == null)
        {
            throw new InvalidOperationException($"World snapshot no longer contains the player pawn '{playerId.Value}'.");
        }

        if (targetPos.X < 0 || targetPos.X >= snapshot.Width || targetPos.Y < 0 || targetPos.Y >= snapshot.Height)
        {
            throw new InvalidOperationException($"Manual interaction target position ({targetPos.X}, {targetPos.Y}) is outside the world bounds {snapshot.Width}x{snapshot.Height}.");
        }

        bool hasTarget = !string.IsNullOrWhiteSpace(targetId.Value);
        ThingView targetThing = null;
        if (hasTarget)
        {
            targetThing = snapshot.GetThing(targetId);
            if (targetThing == null)
            {
                throw new InvalidOperationException(
                    $"Manual interaction target '{targetId.Value}' does not exist in the current world snapshot.");
            }

            if (!targetThing.Position.Equals(targetPos))
            {
                throw new InvalidOperationException(
                    $"Manual interaction target '{targetId.Value}' is at ({targetThing.Position.X}, {targetThing.Position.Y}) rather than the requested position ({targetPos.X}, {targetPos.Y}).");
            }

            if (targetThing.Id.Equals(playerId))
            {
                throw new InvalidOperationException("Manual interactions cannot target the player pawn itself.");
            }
        }

        if (planStepIndex.HasValue && planStepIndex.Value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(planStepIndex), "Plan step index must be non-negative when supplied.");
        }

        var writeCount = planStepIndex.HasValue ? 5 : 4;
        var writes = new WriteSetEntry[writeCount];
        writes[0] = new WriteSetEntry(playerId, "@manual.interact.seq", (double)++_interactionSequence);
        writes[1] = new WriteSetEntry(playerId, "@manual.interact.x", targetPos.X);
        writes[2] = new WriteSetEntry(playerId, "@manual.interact.y", targetPos.Y);
        writes[3] = new WriteSetEntry(playerId, "@manual.interact.hasTarget", hasTarget ? 1d : 0d);
        if (planStepIndex.HasValue)
        {
            writes[4] = new WriteSetEntry(playerId, "@manual.interact.planStep", planStepIndex.Value);
        }

        var reads = hasTarget
            ? new[]
            {
                new ReadSetEntry(playerId, null, null),
                new ReadSetEntry(targetThing.Id, null, null)
            }
            : new[] { new ReadSetEntry(playerId, null, null) };

        FactDelta[] factDeltas;
        if (_lastInteractionFactTarget.HasValue && hasTarget)
        {
            var previous = _lastInteractionFactTarget.Value;
            if (previous.Equals(targetThing.Id))
            {
                factDeltas = new[]
                {
                    new FactDelta
                    {
                        Pred = "manual_interact_target",
                        A = playerId,
                        B = targetThing.Id,
                        Add = true
                    }
                };
            }
            else
            {
                factDeltas = new[]
                {
                    new FactDelta
                    {
                        Pred = "manual_interact_target",
                        A = playerId,
                        B = previous,
                        Add = false
                    },
                    new FactDelta
                    {
                        Pred = "manual_interact_target",
                        A = playerId,
                        B = targetThing.Id,
                        Add = true
                    }
                };
            }
        }
        else if (_lastInteractionFactTarget.HasValue)
        {
            factDeltas = new[]
            {
                new FactDelta
                {
                    Pred = "manual_interact_target",
                    A = playerId,
                    B = _lastInteractionFactTarget.Value,
                    Add = false
                }
            };
        }
        else if (hasTarget)
        {
            factDeltas = new[]
            {
                new FactDelta
                {
                    Pred = "manual_interact_target",
                    A = playerId,
                    B = targetThing.Id,
                    Add = true
                }
            };
        }
        else
        {
            factDeltas = Array.Empty<FactDelta>();
        }

        var batch = new EffectBatch
        {
            BaseVersion = snapshot.Version,
            Reads = reads,
            Writes = writes,
            FactDeltas = factDeltas,
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
            throw new InvalidOperationException(
                $"Player interaction commit conflicted while interacting at ({targetPos.X}, {targetPos.Y}).");
        }

        _lastInteractionFactTarget = hasTarget ? targetThing?.Id : (ThingId?)null;
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

    private void BindInputActions()
    {
        _resolvedMoveAction = ResolveAction(moveAction, nameof(moveAction), moveActionName, nameof(moveActionName));
        _resolvedMoveAction.Enable();

        _resolvedInteractAction = ResolveAction(interactAction, nameof(interactAction), interactActionName, nameof(interactActionName));
        _resolvedInteractAction.performed += HandleInteractPerformed;
        _resolvedInteractAction.Enable();
    }

    private void UnbindInputActions()
    {
        if (_resolvedInteractAction != null)
        {
            _resolvedInteractAction.performed -= HandleInteractPerformed;
            if (_resolvedInteractAction.enabled)
            {
                _resolvedInteractAction.Disable();
            }

            _resolvedInteractAction = null;
        }

        if (_resolvedMoveAction != null)
        {
            if (_resolvedMoveAction.enabled)
            {
                _resolvedMoveAction.Disable();
            }

            _resolvedMoveAction = null;
        }
    }

    private void HandleInteractPerformed(InputAction.CallbackContext context)
    {
        if (context.phase != InputActionPhase.Performed)
        {
            return;
        }

        if (_world == null || !_playerPawnId.HasValue)
        {
            return;
        }

        ExecuteInteract();
    }

    private InputAction ResolveAction(InputActionReference reference, string referenceFieldName, string actionName, string actionNameField)
    {
        if (reference == null)
        {
            return ResolveActionFromPlayerInput(referenceFieldName, actionName, actionNameField);
        }

        var action = reference.action;
        if (action == null)
        {
            throw new InvalidOperationException($"InputActionReference '{referenceFieldName}' does not resolve to a valid InputAction instance.");
        }

        return action;
    }

    private InputAction ResolveActionFromPlayerInput(string referenceFieldName, string actionName, string actionNameField)
    {
        EnsurePlayerInputReference();

        if (playerInput == null)
        {
            throw new InvalidOperationException($"PlayerPawnController requires a PlayerInput reference when '{referenceFieldName}' is not assigned.");
        }

        if (string.IsNullOrWhiteSpace(actionName))
        {
            throw new InvalidOperationException($"PlayerPawnController requires '{actionNameField}' to be specified when '{referenceFieldName}' is not assigned.");
        }

        var actions = playerInput.actions;
        if (actions == null)
        {
            throw new InvalidOperationException("PlayerPawnController requires the PlayerInput to have an actions asset assigned.");
        }

        var action = actions.FindAction(actionName, false);
        if (action == null)
        {
            throw new InvalidOperationException($"PlayerInput actions asset does not define an action named '{actionName}'.");
        }

        return action;
    }

    private void EnsurePlayerInputReference()
    {
        if (playerInput == null)
        {
            playerInput = GetComponent<PlayerInput>();
        }

        if (playerInput == null)
        {
            throw new InvalidOperationException("PlayerPawnController requires a PlayerInput component on the same GameObject.");
        }

        EnsurePlayerInputConfigured(playerInput);
    }

    private void EnsurePlayerInputConfigured(PlayerInput input)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (playerControlsAsset != null)
        {
            if (input.actions != playerControlsAsset)
            {
                input.actions = playerControlsAsset;
            }
        }

        if (input.actions == null)
        {
            throw new InvalidOperationException("PlayerInput must reference an InputActionAsset before the player controller can operate.");
        }

        if (!string.IsNullOrWhiteSpace(defaultActionMap))
        {
            var actionMap = input.actions.FindActionMap(defaultActionMap, false);
            if (actionMap == null)
            {
                throw new InvalidOperationException($"PlayerInput actions asset does not contain an action map named '{defaultActionMap}'.");
            }

            input.defaultActionMap = defaultActionMap;
        }
    }

    private GridPos ResolveInteractionTarget(IWorldSnapshot snapshot, ThingView playerThing)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (playerThing == null)
        {
            throw new ArgumentNullException(nameof(playerThing));
        }

        var basePos = playerThing.Position;
        if (_lastMoveDirection == Vector2Int.zero)
        {
            return basePos;
        }

        var candidateX = basePos.X + _lastMoveDirection.x;
        var candidateY = basePos.Y + _lastMoveDirection.y;

        if (candidateX < 0 || candidateX >= snapshot.Width || candidateY < 0 || candidateY >= snapshot.Height)
        {
            return basePos;
        }

        return new GridPos(candidateX, candidateY);
    }

    private static ThingView FindInteractableThing(IWorldSnapshot snapshot, ThingId playerId, GridPos targetPos)
    {
        foreach (var thing in snapshot.AllThings())
        {
            if (thing == null || thing.Id.Equals(playerId))
            {
                continue;
            }

            if (thing.Position.Equals(targetPos))
            {
                return thing;
            }
        }

        return null;
    }
}
