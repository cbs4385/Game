using System;
using System.Collections;
using System.Collections.Generic;

namespace System.Collections.Immutable
{
    public sealed class ImmutableDictionary<TKey, TValue> : IReadOnlyDictionary<TKey, TValue>
    {
        private readonly Dictionary<TKey, TValue> _dictionary;
        private readonly IEqualityComparer<TKey> _comparer;

        internal ImmutableDictionary(Dictionary<TKey, TValue> dictionary, IEqualityComparer<TKey> comparer)
        {
            _comparer = comparer ?? EqualityComparer<TKey>.Default;
            _dictionary = dictionary ?? new Dictionary<TKey, TValue>(0, _comparer);
        }

        public static ImmutableDictionary<TKey, TValue> Empty { get; } = new ImmutableDictionary<TKey, TValue>(new Dictionary<TKey, TValue>(0, EqualityComparer<TKey>.Default), EqualityComparer<TKey>.Default);

        public ImmutableDictionary<TKey, TValue> SetItem(TKey key, TValue value)
        {
            var dict = new Dictionary<TKey, TValue>(_dictionary, _comparer);
            dict[key] = value;
            return new ImmutableDictionary<TKey, TValue>(dict, _comparer);
        }

        public ImmutableDictionary<TKey, TValue> Remove(TKey key)
        {
            if (!_dictionary.ContainsKey(key))
                return this;
            var dict = new Dictionary<TKey, TValue>(_dictionary, _comparer);
            dict.Remove(key);
            return new ImmutableDictionary<TKey, TValue>(dict, _comparer);
        }

        public bool ContainsKey(TKey key) => _dictionary.ContainsKey(key);
        public bool TryGetValue(TKey key, out TValue value) => _dictionary.TryGetValue(key, out value);
        public TValue this[TKey key] => _dictionary[key];
        public IEnumerable<TKey> Keys => _dictionary.Keys;
        public IEnumerable<TValue> Values => _dictionary.Values;
        public int Count => _dictionary.Count;

        public Builder ToBuilder() => new Builder(this);

        internal Dictionary<TKey, TValue> ToMutable() => new Dictionary<TKey, TValue>(_dictionary, _comparer);

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _dictionary.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public sealed class Builder : IDictionary<TKey, TValue>
        {
            private readonly Dictionary<TKey, TValue> _dict;
            private readonly IEqualityComparer<TKey> _comparer;

            internal Builder(ImmutableDictionary<TKey, TValue> source)
            {
                _comparer = source?._comparer ?? EqualityComparer<TKey>.Default;
                _dict = source?.ToMutable() ?? new Dictionary<TKey, TValue>(0, _comparer);
            }

            public Builder(IEqualityComparer<TKey> comparer = null)
            {
                _comparer = comparer ?? EqualityComparer<TKey>.Default;
                _dict = new Dictionary<TKey, TValue>(0, _comparer);
            }

            public TValue this[TKey key]
            {
                get => _dict[key];
                set => _dict[key] = value;
            }

            public ICollection<TKey> Keys => _dict.Keys;
            public ICollection<TValue> Values => _dict.Values;
            public int Count => _dict.Count;
            public bool IsReadOnly => false;

            public void Add(TKey key, TValue value) => _dict.Add(key, value);
            public bool ContainsKey(TKey key) => _dict.ContainsKey(key);
            public bool Remove(TKey key) => _dict.Remove(key);
            public bool TryGetValue(TKey key, out TValue value) => _dict.TryGetValue(key, out value);
            public void Add(KeyValuePair<TKey, TValue> item) => _dict.Add(item.Key, item.Value);
            public void Clear() => _dict.Clear();
            public bool Contains(KeyValuePair<TKey, TValue> item) => _dict.TryGetValue(item.Key, out var value) && EqualityComparer<TValue>.Default.Equals(value, item.Value);
            public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => ((ICollection<KeyValuePair<TKey, TValue>>)_dict).CopyTo(array, arrayIndex);
            public bool Remove(KeyValuePair<TKey, TValue> item)
            {
                if (Contains(item))
                    return _dict.Remove(item.Key);
                return false;
            }
            public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _dict.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public ImmutableDictionary<TKey, TValue> ToImmutable() => new ImmutableDictionary<TKey, TValue>(new Dictionary<TKey, TValue>(_dict, _comparer), _comparer);
        }
    }

    public sealed class ImmutableHashSet<T> : IReadOnlyCollection<T>
    {
        private readonly HashSet<T> _set;
        private readonly IEqualityComparer<T> _comparer;

        internal ImmutableHashSet(HashSet<T> set, IEqualityComparer<T> comparer)
        {
            _comparer = comparer ?? EqualityComparer<T>.Default;
            _set = set ?? new HashSet<T>(_comparer);
        }

        public static ImmutableHashSet<T> Empty { get; } = new ImmutableHashSet<T>(new HashSet<T>(), EqualityComparer<T>.Default);

        public int Count => _set.Count;
        public bool Contains(T item) => _set.Contains(item);
        public IEnumerator<T> GetEnumerator() => _set.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public ImmutableHashSet<T> Add(T value)
        {
            if (_set.Contains(value))
                return this;
            var set = new HashSet<T>(_set, _comparer) { value };
            return new ImmutableHashSet<T>(set, _comparer);
        }

        public ImmutableHashSet<T> Remove(T value)
        {
            if (!_set.Contains(value))
                return this;
            var set = new HashSet<T>(_set, _comparer);
            set.Remove(value);
            return new ImmutableHashSet<T>(set, _comparer);
        }

        public Builder ToBuilder() => new Builder(this);
        internal HashSet<T> ToMutable() => new HashSet<T>(_set, _comparer);

        public sealed class Builder : ICollection<T>
        {
            private readonly HashSet<T> _set;
            private readonly IEqualityComparer<T> _comparer;

            internal Builder(ImmutableHashSet<T> source)
            {
                _comparer = source?._comparer ?? EqualityComparer<T>.Default;
                _set = source?.ToMutable() ?? new HashSet<T>(_comparer);
            }

            public Builder(IEqualityComparer<T> comparer = null)
            {
                _comparer = comparer ?? EqualityComparer<T>.Default;
                _set = new HashSet<T>(_comparer);
            }

            public int Count => _set.Count;
            public bool IsReadOnly => false;
            public void Add(T item) => _set.Add(item);
            public void Clear() => _set.Clear();
            public bool Contains(T item) => _set.Contains(item);
            public void CopyTo(T[] array, int arrayIndex) => _set.CopyTo(array, arrayIndex);
            public bool Remove(T item) => _set.Remove(item);
            public IEnumerator<T> GetEnumerator() => _set.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public ImmutableHashSet<T> ToImmutable() => new ImmutableHashSet<T>(new HashSet<T>(_set, _comparer), _comparer);
        }
    }

    public static class ImmutableDictionary
    {
        public static ImmutableDictionary<TKey, TValue>.Builder CreateBuilder<TKey, TValue>(IEqualityComparer<TKey> comparer = null)
        {
            return new ImmutableDictionary<TKey, TValue>.Builder(comparer);
        }
    }

    public static class ImmutableHashSet
    {
        public static ImmutableHashSet<T>.Builder CreateBuilder<T>(IEqualityComparer<T> comparer = null)
        {
            return new ImmutableHashSet<T>.Builder(comparer);
        }
    }

    public static class ImmutableExtensions
    {
        public static ImmutableDictionary<TKey, TValue> ToImmutableDictionary<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> source, IEqualityComparer<TKey> comparer = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var dict = comparer != null ? new Dictionary<TKey, TValue>(comparer) : new Dictionary<TKey, TValue>();
            foreach (var kv in source)
                dict[kv.Key] = kv.Value;
            return new ImmutableDictionary<TKey, TValue>(dict, comparer ?? EqualityComparer<TKey>.Default);
        }

        public static ImmutableDictionary<TKey, TValue> ToImmutableDictionary<TKey, TValue>(this IDictionary<TKey, TValue> source, IEqualityComparer<TKey> comparer = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var dict = comparer != null ? new Dictionary<TKey, TValue>(source, comparer) : new Dictionary<TKey, TValue>(source);
            return new ImmutableDictionary<TKey, TValue>(dict, comparer ?? (source is Dictionary<TKey, TValue> d ? d.Comparer : EqualityComparer<TKey>.Default));
        }

        public static ImmutableHashSet<T> ToImmutableHashSet<T>(this IEnumerable<T> source, IEqualityComparer<T> comparer = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var set = comparer != null ? new HashSet<T>(comparer) : new HashSet<T>();
            foreach (var item in source)
                set.Add(item);
            return new ImmutableHashSet<T>(set, comparer ?? EqualityComparer<T>.Default);
        }
    }
}
