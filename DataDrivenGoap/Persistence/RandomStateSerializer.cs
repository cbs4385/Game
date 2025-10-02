using System;
using System.Reflection;

namespace DataDrivenGoap.Persistence
{
    internal static class RandomStateSerializer
    {
        private const string SeedArrayField = "SeedArray";
        private const string InextField = "inext";
        private const string InextpField = "inextp";

        public static RandomState Capture(Random rng)
        {
            if (rng == null)
                return null;

            var seedArray = (int[])GetField(rng, SeedArrayField)?.GetValue(rng);
            if (seedArray == null)
                return null;
            var inext = (int?)GetField(rng, InextField)?.GetValue(rng);
            var inextp = (int?)GetField(rng, InextpField)?.GetValue(rng);
            if (!inext.HasValue || !inextp.HasValue)
                return null;

            return new RandomState
            {
                seedArray = (int[])seedArray.Clone(),
                inext = inext.Value,
                inextp = inextp.Value
            };
        }

        public static void Apply(Random rng, RandomState state)
        {
            if (rng == null || state == null)
                return;
            var seedArrayField = GetField(rng, SeedArrayField);
            var inextField = GetField(rng, InextField);
            var inextpField = GetField(rng, InextpField);
            if (seedArrayField == null || inextField == null || inextpField == null)
                return;
            var current = (int[])seedArrayField.GetValue(rng);
            if (current == null || current.Length != state.seedArray?.Length)
                return;
            Array.Copy(state.seedArray, current, current.Length);
            inextField.SetValue(rng, state.inext);
            inextpField.SetValue(rng, state.inextp);
        }

        private static FieldInfo GetField(Random rng, string name)
        {
            return rng?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        }
    }
}
