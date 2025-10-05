using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DataDrivenGoap.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class ManualPlanExecutionTests
{
    private const double IngredientTolerance = 1e-6;

    [UnityTest]
    public IEnumerator ManualCollectIngredientStepTransfersIngredient()
    {
        var bootstrapperObject = new GameObject("GoapSimulationBootstrapper_Test");
        var bootstrapper = bootstrapperObject.AddComponent<GoapSimulationBootstrapper>();

        yield return null;
        yield return new WaitUntil(() => bootstrapper.HasBootstrapped);

        var args = bootstrapper.LatestBootstrap;
        Assert.IsNotNull(args, "Bootstrapper did not publish ready event arguments.");
        Assert.IsNotNull(args.World, "World instance should be available after bootstrap.");
        Assert.IsTrue(args.PlayerPawnId.HasValue, "Player pawn id should be defined in bootstrap data.");

        var world = args.World;
        var playerId = args.PlayerPawnId.Value;

        Dictionary<ThingId, double> beforeIngredients = null;
        Dictionary<ThingId, double> afterIngredients = null;
        int executedIndex = -1;

        for (int stepIndex = 0; stepIndex < 6 && executedIndex < 0; stepIndex++)
        {
            var snapshot = world.Snap();
            var baseline = snapshot.AllThings().ToDictionary(t => t.Id, t => t.AttrOrDefault("ingredients", 0.0));
            try
            {
                bootstrapper.ExecuteManualPlanStep(playerId, stepIndex, null, null, snapshot.Version);
                beforeIngredients = baseline;
                var postSnapshot = world.Snap();
                afterIngredients = postSnapshot.AllThings().ToDictionary(t => t.Id, t => t.AttrOrDefault("ingredients", 0.0));
                executedIndex = stepIndex;
            }
            catch (InvalidOperationException)
            {
                // Try the next plan index; no world mutations should have occurred on failure.
            }

            if (executedIndex < 0)
            {
                yield return null;
            }
        }

        Assert.That(executedIndex, Is.GreaterThanOrEqualTo(0), "No manual plan step executed successfully for the player pawn.");
        Assert.IsNotNull(beforeIngredients, "Baseline ingredient map should be captured before manual execution.");
        Assert.IsNotNull(afterIngredients, "Post-execution ingredient map should be captured after manual execution.");
        Assert.IsTrue(beforeIngredients.ContainsKey(playerId), "Baseline ingredient map must contain the player pawn entry.");
        Assert.IsTrue(afterIngredients.ContainsKey(playerId), "Post-execution ingredient map must contain the player pawn entry.");

        var playerBefore = beforeIngredients[playerId];
        var playerAfter = afterIngredients[playerId];
        Assert.That(playerAfter, Is.EqualTo(playerBefore + 1.0).Within(IngredientTolerance),
            "Player pawn should gain exactly one ingredient after manual execution.");

        var losses = new List<(ThingId Id, double Delta)>();
        foreach (var kv in beforeIngredients)
        {
            if (kv.Key.Equals(playerId))
            {
                continue;
            }

            if (!afterIngredients.TryGetValue(kv.Key, out var afterValue))
            {
                continue;
            }

            double change = kv.Value - afterValue;
            if (change > IngredientTolerance)
            {
                losses.Add((kv.Key, change));
            }
        }

        Assert.That(losses.Count, Is.EqualTo(1), "Exactly one ingredient source should lose ingredients.");
        Assert.That(losses[0].Delta, Is.EqualTo(1.0).Within(IngredientTolerance),
            "Ingredient source should lose exactly one ingredient when the manual action executes.");

        UnityEngine.Object.Destroy(bootstrapperObject);
        yield return null;
    }
}
