using System.Collections.Generic;

namespace DataDrivenGoap.Compatibility
{
    internal static class HashCodeCompat
    {
        public static int Combine<T1, T2, T3>(T1 value1, T2 value2, T3 value3)
        {
            unchecked
            {
                var hash = 17;
                hash = hash * 31 + GetHashCode(value1);
                hash = hash * 31 + GetHashCode(value2);
                hash = hash * 31 + GetHashCode(value3);
                return hash;
            }
        }

        public static int Combine<T1, T2, T3, T4>(T1 value1, T2 value2, T3 value3, T4 value4)
        {
            unchecked
            {
                var hash = 17;
                hash = hash * 31 + GetHashCode(value1);
                hash = hash * 31 + GetHashCode(value2);
                hash = hash * 31 + GetHashCode(value3);
                hash = hash * 31 + GetHashCode(value4);
                return hash;
            }
        }

        private static int GetHashCode<T>(T value)
        {
            return value == null ? 0 : EqualityComparer<T>.Default.GetHashCode(value);
        }
    }
}
