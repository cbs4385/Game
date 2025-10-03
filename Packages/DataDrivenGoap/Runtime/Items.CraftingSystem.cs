using System;
using System.Linq;
using DataDrivenGoap.Core;

namespace DataDrivenGoap.Items
{
    public static class CraftingUtility
    {
        public static int SafeMultiply(int value, int multiplier)
        {
            if (value <= 0 || multiplier <= 0)
                return 0;
            long total = (long)value * multiplier;
            if (total > int.MaxValue)
                return int.MaxValue;
            return (int)total;
        }

        public static bool MatchesStation(RecipeDefinition recipe, ThingView stationThing, string stationHint)
        {
            if (recipe == null)
                return false;

            var stations = recipe.Stations;
            if (stations == null || stations.Count == 0)
                return true;

            if (!string.IsNullOrWhiteSpace(stationHint))
            {
                foreach (var station in stations)
                {
                    if (string.Equals(station, stationHint, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            if (stationThing == null)
                return false;

            foreach (var station in stations)
            {
                if (!string.IsNullOrWhiteSpace(stationThing.Id.Value) &&
                    string.Equals(stationThing.Id.Value, station, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrWhiteSpace(stationThing.Type) &&
                    string.Equals(stationThing.Type, station, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (stationThing.Tags != null && stationThing.Tags.Any(t => string.Equals(t, station, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            return false;
        }

        public static bool MeetsSkillGates(RecipeDefinition recipe, ThingView crafter, ISkillProgression skillProgression = null)
        {
            if (recipe == null)
                return false;
            if (recipe.Gates == null || recipe.Gates.Count == 0)
                return true;
            if (crafter == null)
                return false;

            foreach (var gate in recipe.Gates)
            {
                if (string.IsNullOrWhiteSpace(gate.Skill))
                    continue;
                double value = 0.0;
                if (skillProgression != null && crafter != null && !string.IsNullOrWhiteSpace(crafter.Id.Value))
                {
                    value = skillProgression.GetSkillLevel(crafter.Id, gate.Skill);
                }
                else if (crafter != null)
                {
                    value = crafter.AttrOrDefault(gate.Skill, 0.0);
                }
                if (value + 1e-6 < gate.Level)
                    return false;
            }

            return true;
        }
    }

    public sealed class CraftingSystem : ICraftingQuery
    {
        private readonly ItemCatalog _catalog;
        private readonly InventorySystem _inventory;

        public CraftingSystem(ItemCatalog catalog, InventorySystem inventory)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        }

        public bool TryGetRecipe(string recipeId, out RecipeDefinition recipe)
        {
            return _catalog.TryGetRecipe(recipeId, out recipe);
        }

        public bool HasIngredients(ThingId owner, RecipeDefinition recipe, int count)
        {
            if (recipe == null || count <= 0)
                return false;
            if (string.IsNullOrEmpty(owner.Value))
                return false;

            foreach (var kvp in recipe.Inputs)
            {
                int required = CraftingUtility.SafeMultiply(kvp.Value, count);
                if (required <= 0)
                    continue;
                int available = _inventory.Count(owner, kvp.Key);
                if (available < required)
                    return false;
            }

            return true;
        }

        public double GetCraftDuration(string recipeId)
        {
            if (!_catalog.TryGetRecipe(recipeId, out var recipe) || recipe == null)
                return 0.0;
            return recipe.TimeSeconds;
        }
    }
}
