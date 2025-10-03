using System.Collections.Generic;
using UnityEngine;
using DataDrivenGoap.Config;
using DataDrivenGoap.Core;
using DataDrivenGoap.Planning;
using DataDrivenGoap.World;

public class GoapDemoBootstrap : MonoBehaviour
{
    [SerializeField]
    private bool runOnStart = true;

    private ShardedWorld _world;
    private JsonDrivenPlanner _planner;
    private ThingId _actorId = new ThingId("villager");

    private void Awake()
    {
        InitializeSimulation();
    }

    private void Start()
    {
        if (runOnStart)
        {
            RunDemoPlan();
        }
    }

    [ContextMenu("Run Demo Plan")]
    public void RunDemoPlan()
    {
        if (_world == null || _planner == null)
        {
            InitializeSimulation();
        }

        var snapshot = _world.Snap();
        var plan = _planner.Plan(snapshot, _actorId, new Goal("rest_and_refuel"));

        if (plan == null || plan.IsEmpty)
        {
            Debug.LogWarning("GOAP demo could not find a plan for the villager.");
            return;
        }

        for (int stepIndex = 0; stepIndex < plan.Steps.Count; stepIndex++)
        {
            var step = plan.Steps[stepIndex];
            Debug.Log($"Executing step {stepIndex + 1}: {step.ActivityName}");

            var effects = step.BuildEffects(snapshot);
            var result = _world.TryCommit(effects);
            Debug.Log($"Commit result: {result}");

            snapshot = _world.Snap();
        }

        var finalSnapshot = _world.Snap();
        var actor = finalSnapshot.GetThing(_actorId);
        double energy = actor?.AttrOrDefault("energy", 0.0) ?? 0.0;
        Debug.Log($"Villager energy after plan: {energy:0.00}");
    }

    private void InitializeSimulation()
    {
        var timeConfig = new TimeConfig
        {
            dayLengthSeconds = 60,
            worldHoursPerDay = 24,
            minutesPerHour = 60,
            secondsPerMinute = 60,
            daysPerMonth = 30,
            seasonLengthDays = 30,
            seasons = new[] { "Spring", "Summer", "Autumn", "Winter" },
            startYear = 1,
            startDayOfYear = 1,
            startTimeOfDayHours = 6
        };

        var clock = new WorldClock(timeConfig);

        var seedThings = new List<(ThingId id, string type, IEnumerable<string> tags, GridPos pos, IDictionary<string, double> attrs, BuildingInfo building)>
        {
            (_actorId, "villager", new[] { "actor" }, new GridPos(1, 1), new Dictionary<string, double> { { "energy", 0.25 } }, null)
        };

        _world = new ShardedWorld(
            width: 4,
            height: 4,
            blockedChance: 0.0,
            shardCount: 1,
            rngSeed: 42,
            seedThings: seedThings,
            seedFacts: new Fact[0],
            clock: clock
        );

        var restAction = new ActionConfig
        {
            id = "rest",
            activity = "rest",
            duration = "1",
            pre = new[] { "attr($self,\"energy\") < 0.99" },
            effects = new[]
            {
                new EffectConfig
                {
                    type = "writeattr",
                    target = "$self",
                    attr = "energy",
                    op = "set",
                    value = "1"
                }
            }
        };

        var goal = new GoalConfig
        {
            id = "rest_and_refuel",
            priority = "1",
            satisfiedWhen = new[] { "attr($self,\"energy\") >= 0.99" },
            actions = new[]
            {
                new GoalActionConfig { id = "rest" }
            }
        };

        _planner = new JsonDrivenPlanner(
            new[] { restAction },
            new[] { goal }
        );
    }
}
