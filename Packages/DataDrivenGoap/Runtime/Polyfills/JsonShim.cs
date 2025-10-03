using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace System.Text.Json
{
    public enum JsonValueKind
    {
        Undefined,
        Object,
        Array,
        String,
        Number,
        True,
        False,
        Null,
    }

    public abstract class JsonNamingPolicy
    {
        public abstract string ConvertName(string name);
    }

    public sealed class JsonSerializerOptions
    {
        public bool PropertyNameCaseInsensitive { get; set; }
        public bool WriteIndented { get; set; }
        public JsonNamingPolicy PropertyNamingPolicy { get; set; }
    }

    public struct JsonWriterOptions
    {
        public bool Indented { get; set; }
    }

    public sealed class Utf8JsonWriter : IDisposable
    {
        private readonly StreamWriter _writer;

        internal bool Indented { get; }

        public Utf8JsonWriter(Stream utf8Json, JsonWriterOptions options)
        {
            if (utf8Json == null) throw new ArgumentNullException(nameof(utf8Json));
            _writer = new StreamWriter(utf8Json, new UTF8Encoding(false), 1024, leaveOpen: true);
            Indented = options.Indented;
        }

        internal void WriteRaw(string json)
        {
            if (json == null) return;
            _writer.Write(json);
        }

        public void Flush()
        {
            _writer.Flush();
        }

        public void Dispose()
        {
            Flush();
            _writer.Dispose();
        }
    }

    public readonly struct JsonProperty
    {
        public string Name { get; }
        public JsonElement Value { get; }

        internal JsonProperty(string name, JsonElement value)
        {
            Name = name;
            Value = value;
        }
    }

    internal abstract class JsonNode
    {
        public abstract JsonValueKind ValueKind { get; }
    }

    internal sealed class JsonNullNode : JsonNode
    {
        public static readonly JsonNullNode Instance = new JsonNullNode();
        private JsonNullNode() { }
        public override JsonValueKind ValueKind => JsonValueKind.Null;
    }

    internal sealed class JsonStringNode : JsonNode
    {
        public string Value { get; }
        public JsonStringNode(string value) => Value = value ?? string.Empty;
        public override JsonValueKind ValueKind => JsonValueKind.String;
    }

    internal sealed class JsonNumberNode : JsonNode
    {
        public double Value { get; }
        public JsonNumberNode(double value) => Value = value;
        public override JsonValueKind ValueKind => JsonValueKind.Number;
    }

    internal sealed class JsonBooleanNode : JsonNode
    {
        public bool Value { get; }
        public JsonBooleanNode(bool value) => Value = value;
        public override JsonValueKind ValueKind => Value ? JsonValueKind.True : JsonValueKind.False;
    }

    internal sealed class JsonArrayNode : JsonNode
    {
        public List<JsonNode> Items { get; }
        public JsonArrayNode(List<JsonNode> items) => Items = items ?? new List<JsonNode>();
        public override JsonValueKind ValueKind => JsonValueKind.Array;
    }

    internal sealed class JsonObjectNode : JsonNode
    {
        private readonly List<KeyValuePair<string, JsonNode>> _properties = new List<KeyValuePair<string, JsonNode>>();
        private readonly Dictionary<string, JsonNode> _lookup = new Dictionary<string, JsonNode>(StringComparer.Ordinal);

        public override JsonValueKind ValueKind => JsonValueKind.Object;

        public IReadOnlyList<KeyValuePair<string, JsonNode>> Properties => _properties;

        public void Set(string name, JsonNode value)
        {
            if (name == null)
                return;
            if (_lookup.ContainsKey(name))
            {
                for (int i = 0; i < _properties.Count; i++)
                {
                    if (string.Equals(_properties[i].Key, name, StringComparison.Ordinal))
                    {
                        _properties[i] = new KeyValuePair<string, JsonNode>(name, value);
                        break;
                    }
                }
            }
            else
            {
                _properties.Add(new KeyValuePair<string, JsonNode>(name, value));
            }
            _lookup[name] = value;
        }

        public bool TryGet(string name, out JsonNode node)
        {
            return _lookup.TryGetValue(name, out node);
        }
    }

    internal static class SimpleJsonParser
    {
        public static JsonNode Parse(string json)
        {
            if (json == null)
                return JsonNullNode.Instance;
            var parser = new Parser(json);
            var node = parser.ParseValue();
            parser.SkipWhitespace();
            return node ?? JsonNullNode.Instance;
        }

        private sealed class Parser
        {
            private readonly string _text;
            private int _pos;

            public Parser(string text)
            {
                _text = text ?? string.Empty;
                _pos = 0;
            }

            public JsonNode ParseValue()
            {
                SkipWhitespace();
                if (End) return JsonNullNode.Instance;
                char c = _text[_pos];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return new JsonStringNode(ParseString());
                    case 't': return ParseTrue();
                    case 'f': return ParseFalse();
                    case 'n': return ParseNull();
                    default:
                        if (c == '-' || char.IsDigit(c))
                            return new JsonNumberNode(ParseNumber());
                        throw new FormatException($"Unexpected character '{c}' at position {_pos}.");
                }
            }

            public void SkipWhitespace()
            {
                while (!End && char.IsWhiteSpace(_text[_pos]))
                    _pos++;
            }

            private JsonNode ParseObject()
            {
                Expect('{');
                SkipWhitespace();
                var obj = new JsonObjectNode();
                if (TryConsume('}'))
                    return obj;
                while (true)
                {
                    SkipWhitespace();
                    var name = ParseString();
                    SkipWhitespace();
                    Expect(':');
                    var value = ParseValue();
                    obj.Set(name, value);
                    SkipWhitespace();
                    if (TryConsume('}'))
                        break;
                    Expect(',');
                }
                return obj;
            }

            private JsonNode ParseArray()
            {
                Expect('[');
                SkipWhitespace();
                var items = new List<JsonNode>();
                if (TryConsume(']'))
                    return new JsonArrayNode(items);
                while (true)
                {
                    var value = ParseValue();
                    items.Add(value);
                    SkipWhitespace();
                    if (TryConsume(']'))
                        break;
                    Expect(',');
                }
                return new JsonArrayNode(items);
            }

            private string ParseString()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (!End)
                {
                    char c = _text[_pos++];
                    if (c == '"')
                        break;
                    if (c == '\\')
                    {
                        if (End) throw new FormatException("Unterminated escape sequence.");
                        char esc = _text[_pos++];
                        switch (esc)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (_pos + 4 > _text.Length) throw new FormatException("Invalid unicode escape.");
                                string hex = _text.Substring(_pos, 4);
                                if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                    throw new FormatException("Invalid unicode escape.");
                                sb.Append((char)code);
                                _pos += 4;
                                break;
                            default:
                                throw new FormatException($"Invalid escape character '{esc}'.");
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                return sb.ToString();
            }

            private JsonNode ParseTrue()
            {
                ExpectSequence("true");
                return new JsonBooleanNode(true);
            }

            private JsonNode ParseFalse()
            {
                ExpectSequence("false");
                return new JsonBooleanNode(false);
            }

            private JsonNode ParseNull()
            {
                ExpectSequence("null");
                return JsonNullNode.Instance;
            }

            private double ParseNumber()
            {
                int start = _pos;
                if (_text[_pos] == '-') _pos++;
                while (!End && char.IsDigit(_text[_pos])) _pos++;
                if (!End && _text[_pos] == '.')
                {
                    _pos++;
                    while (!End && char.IsDigit(_text[_pos])) _pos++;
                }
                if (!End && (_text[_pos] == 'e' || _text[_pos] == 'E'))
                {
                    _pos++;
                    if (!End && (_text[_pos] == '+' || _text[_pos] == '-')) _pos++;
                    while (!End && char.IsDigit(_text[_pos])) _pos++;
                }
                var numberText = _text.Substring(start, _pos - start);
                if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    throw new FormatException($"Invalid number '{numberText}'.");
                return value;
            }

            private void Expect(char c)
            {
                SkipWhitespace();
                if (End || _text[_pos] != c)
                    throw new FormatException($"Expected '{c}' at position {_pos}.");
                _pos++;
            }

            private bool TryConsume(char c)
            {
                SkipWhitespace();
                if (!End && _text[_pos] == c)
                {
                    _pos++;
                    return true;
                }
                return false;
            }

            private void ExpectSequence(string expected)
            {
                if (string.IsNullOrEmpty(expected)) return;
                foreach (var ch in expected)
                {
                    if (End || _text[_pos] != ch)
                        throw new FormatException($"Expected '{expected}' at position {_pos}.");
                    _pos++;
                }
            }

            private bool End => _pos >= _text.Length;
        }
    }

    internal static class SimpleJsonWriter
    {
        public static string Write(JsonNode node, bool indented)
        {
            var sb = new StringBuilder();
            using (var writer = new StringWriter(sb, CultureInfo.InvariantCulture))
            {
                Write(node, writer, indented, 0);
            }
            return sb.ToString();
        }

        public static void Write(JsonNode node, TextWriter writer, bool indented, int depth)
        {
            node ??= JsonNullNode.Instance;
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    WriteObject((JsonObjectNode)node, writer, indented, depth);
                    break;
                case JsonValueKind.Array:
                    WriteArray((JsonArrayNode)node, writer, indented, depth);
                    break;
                case JsonValueKind.String:
                    WriteString(((JsonStringNode)node).Value, writer);
                    break;
                case JsonValueKind.Number:
                    writer.Write(((JsonNumberNode)node).Value.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JsonValueKind.True:
                    writer.Write("true");
                    break;
                case JsonValueKind.False:
                    writer.Write("false");
                    break;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                default:
                    writer.Write("null");
                    break;
            }
        }

        private static void WriteObject(JsonObjectNode obj, TextWriter writer, bool indented, int depth)
        {
            writer.Write('{');
            if (obj.Properties.Count > 0)
            {
                if (indented) writer.WriteLine();
                for (int i = 0; i < obj.Properties.Count; i++)
                {
                    var kv = obj.Properties[i];
                    if (indented) writer.Write(new string(' ', (depth + 1) * 2));
                    WriteString(kv.Key, writer);
                    writer.Write(indented ? ": " : ":");
                    Write(kv.Value, writer, indented, depth + 1);
                    if (i < obj.Properties.Count - 1)
                        writer.Write(',');
                    if (indented) writer.WriteLine();
                }
                if (indented) writer.Write(new string(' ', depth * 2));
            }
            writer.Write('}');
        }

        private static void WriteArray(JsonArrayNode array, TextWriter writer, bool indented, int depth)
        {
            writer.Write('[');
            if (array.Items.Count > 0)
            {
                if (indented) writer.WriteLine();
                for (int i = 0; i < array.Items.Count; i++)
                {
                    if (indented) writer.Write(new string(' ', (depth + 1) * 2));
                    Write(array.Items[i], writer, indented, depth + 1);
                    if (i < array.Items.Count - 1)
                        writer.Write(',');
                    if (indented) writer.WriteLine();
                }
                if (indented) writer.Write(new string(' ', depth * 2));
            }
            writer.Write(']');
        }

        private static void WriteString(string value, TextWriter writer)
        {
            writer.Write('"');
            if (!string.IsNullOrEmpty(value))
            {
                foreach (var c in value)
                {
                    switch (c)
                    {
                        case '\"': writer.Write("\\\""); break;
                        case '\\': writer.Write("\\\\"); break;
                        case '\b': writer.Write("\\b"); break;
                        case '\f': writer.Write("\\f"); break;
                        case '\n': writer.Write("\\n"); break;
                        case '\r': writer.Write("\\r"); break;
                        case '\t': writer.Write("\\t"); break;
                        default:
                            if (char.IsControl(c))
                            {
                                writer.Write("\\u");
                                writer.Write(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                writer.Write(c);
                            }
                            break;
                    }
                }
            }
            writer.Write('"');
        }
    }

    public readonly struct JsonElement
    {
        private readonly JsonNode _node;

        internal JsonElement(JsonNode node)
        {
            _node = node;
        }

        public JsonValueKind ValueKind => _node?.ValueKind ?? JsonValueKind.Undefined;

        internal JsonNode Node => _node;

        public string GetString()
        {
            if (_node is JsonStringNode s)
                return s.Value;
            throw new InvalidOperationException("Element is not a string.");
        }

        public double GetDouble()
        {
            if (_node is JsonNumberNode n)
                return n.Value;
            throw new InvalidOperationException("Element is not a number.");
        }

        public int GetInt32()
        {
            return (int)Math.Round(GetDouble());
        }

        public string GetRawText()
        {
            return SimpleJsonWriter.Write(_node, indented: false);
        }

        public IEnumerable<JsonElement> EnumerateArray()
        {
            if (_node is JsonArrayNode array)
            {
                foreach (var item in array.Items)
                    yield return new JsonElement(item);
            }
        }

        public IEnumerable<JsonProperty> EnumerateObject()
        {
            if (_node is JsonObjectNode obj)
            {
                foreach (var kv in obj.Properties)
                    yield return new JsonProperty(kv.Key, new JsonElement(kv.Value));
            }
        }

        public bool TryGetProperty(string propertyName, out JsonElement value)
        {
            if (_node is JsonObjectNode obj && obj.TryGet(propertyName, out var node))
            {
                value = new JsonElement(node);
                return true;
            }
            value = default;
            return false;
        }
    }

    public static class JsonSerializer
    {
        public static T Deserialize<T>(Stream utf8Json, JsonSerializerOptions options = null)
        {
            if (utf8Json == null)
                throw new ArgumentNullException(nameof(utf8Json));
            using var reader = new StreamReader(utf8Json, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            string text = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text))
                return default;
            if (text.Length > 0 && text[0] == '\ufeff')
                text = text.Substring(1);
            var node = SimpleJsonParser.Parse(text);
            return (T)ConvertNode(node, typeof(T), options ?? new JsonSerializerOptions());
        }

        public static void Serialize<T>(Utf8JsonWriter writer, T value, JsonSerializerOptions options = null)
        {
            Serialize(writer, value, typeof(T), options);
        }

        public static void Serialize(Utf8JsonWriter writer, object value, Type inputType, JsonSerializerOptions options = null)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            var opts = options ?? new JsonSerializerOptions();
            var node = CreateNode(value, inputType ?? value?.GetType(), opts);
            writer.WriteRaw(SimpleJsonWriter.Write(node, writer.Indented));
            writer.Flush();
        }

        public static string Serialize(object value, JsonSerializerOptions options = null)
        {
            var opts = options ?? new JsonSerializerOptions();
            var node = CreateNode(value, value?.GetType(), opts);
            return SimpleJsonWriter.Write(node, opts.WriteIndented);
        }

        private static object ConvertNode(JsonNode node, Type targetType, JsonSerializerOptions options)
        {
            if (targetType == null)
                targetType = typeof(object);

            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
            {
                if (node == null || node.ValueKind == JsonValueKind.Null || node.ValueKind == JsonValueKind.Undefined)
                    return null;
                targetType = underlying;
            }

            if (targetType == typeof(JsonElement))
                return new JsonElement(node);

            if (targetType == typeof(string))
            {
                if (node == null || node.ValueKind == JsonValueKind.Null || node.ValueKind == JsonValueKind.Undefined)
                    return null;
                if (node is JsonStringNode s) return s.Value;
                if (node is JsonNumberNode n) return n.Value.ToString("R", CultureInfo.InvariantCulture);
                if (node is JsonBooleanNode b) return b.Value ? "true" : "false";
                return SimpleJsonWriter.Write(node, indented: false);
            }

            if (targetType == typeof(bool))
            {
                if (node is JsonBooleanNode b) return b.Value;
                if (node is JsonStringNode s && bool.TryParse(s.Value, out var parsed)) return parsed;
                throw new InvalidOperationException("Element is not a boolean.");
            }

            if (targetType == typeof(int))
                return (int)Math.Round(GetNumber(node));
            if (targetType == typeof(long))
                return (long)Math.Round(GetNumber(node));
            if (targetType == typeof(double))
                return GetNumber(node);
            if (targetType == typeof(float))
                return (float)GetNumber(node);
            if (targetType == typeof(decimal))
                return (decimal)GetNumber(node);

            if (targetType == typeof(DateTime))
            {
                if (node is JsonStringNode s && DateTime.TryParse(s.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                    return dt;
                throw new InvalidOperationException("Element is not a DateTime string.");
            }

            if (targetType == typeof(Guid))
            {
                if (node is JsonStringNode s && Guid.TryParse(s.Value, out var guid))
                    return guid;
                throw new InvalidOperationException("Element is not a Guid string.");
            }

            if (targetType.IsEnum)
            {
                if (node is JsonStringNode s && Enum.IsDefined(targetType, s.Value))
                    return Enum.Parse(targetType, s.Value, ignoreCase: true);
                if (node is JsonNumberNode num)
                    return Enum.ToObject(targetType, (int)Math.Round(num.Value));
                throw new InvalidOperationException($"Element cannot be converted to enum {targetType.Name}.");
            }

            if (typeof(IDictionary).IsAssignableFrom(targetType))
                return ConvertDictionary(node, targetType, options);

            if (targetType.IsArray)
                return ConvertArray(node, targetType, options);

            if (typeof(IEnumerable).IsAssignableFrom(targetType) && targetType != typeof(string))
                return ConvertList(node, targetType, options);

            if (targetType == typeof(object))
                return ConvertToUntyped(node);

            if (node == null || node.ValueKind == JsonValueKind.Null || node.ValueKind == JsonValueKind.Undefined)
                return null;

            if (node is JsonObjectNode obj)
                return ConvertObject(obj, targetType, options);

            throw new InvalidOperationException($"Cannot convert JSON node of kind {node?.ValueKind} to {targetType}.");
        }

        private static object ConvertObject(JsonObjectNode obj, Type targetType, JsonSerializerOptions options)
        {
            var instance = Activator.CreateInstance(targetType);
            var bindingFlags = BindingFlags.Public | BindingFlags.Instance;
            var props = targetType.GetProperties(bindingFlags)
                .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
                .ToArray();

            var comparer = options?.PropertyNameCaseInsensitive == true ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var lookup = new Dictionary<string, PropertyInfo>(comparer);
            foreach (var prop in props)
            {
                var name = options?.PropertyNamingPolicy?.ConvertName(prop.Name) ?? prop.Name;
                lookup[name] = prop;
            }

            foreach (var kv in obj.Properties)
            {
                if (!lookup.TryGetValue(kv.Key, out var prop))
                    continue;
                var value = ConvertNode(kv.Value, prop.PropertyType, options);
                prop.SetValue(instance, value);
            }
            return instance;
        }

        private static object ConvertDictionary(JsonNode node, Type targetType, JsonSerializerOptions options)
        {
            if (!(node is JsonObjectNode obj))
                return null;

            var genericArgs = targetType.IsGenericType ? targetType.GetGenericArguments() : Type.EmptyTypes;
            var keyType = genericArgs.Length > 0 ? genericArgs[0] : typeof(object);
            var valueType = genericArgs.Length > 1 ? genericArgs[1] : typeof(object);

            IDictionary dict;
            try
            {
                dict = (IDictionary)Activator.CreateInstance(targetType);
            }
            catch
            {
                var fallbackType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
                dict = (IDictionary)Activator.CreateInstance(fallbackType);
            }
            foreach (var kv in obj.Properties)
            {
                var key = Convert.ChangeType(kv.Key, keyType, CultureInfo.InvariantCulture);
                var value = ConvertNode(kv.Value, valueType, options);
                dict[key] = value;
            }
            return dict;
        }

        private static object ConvertArray(JsonNode node, Type targetType, JsonSerializerOptions options)
        {
            if (!(node is JsonArrayNode array))
                return Array.CreateInstance(targetType.GetElementType(), 0);
            var elementType = targetType.GetElementType();
            var result = Array.CreateInstance(elementType, array.Items.Count);
            for (int i = 0; i < array.Items.Count; i++)
            {
                var value = ConvertNode(array.Items[i], elementType, options);
                result.SetValue(value, i);
            }
            return result;
        }

        private static object ConvertList(JsonNode node, Type targetType, JsonSerializerOptions options)
        {
            if (!(node is JsonArrayNode array))
            {
                try { return Activator.CreateInstance(targetType); }
                catch
                {
                    if (targetType.IsGenericType)
                    {
                        var element = targetType.GetGenericArguments()[0];
                        var fallback = typeof(List<>).MakeGenericType(element);
                        return Activator.CreateInstance(fallback);
                    }
                    return null;
                }
            }

            Type elementType = typeof(object);
            if (targetType.IsGenericType)
                elementType = targetType.GetGenericArguments()[0];

            IList list;
            try
            {
                list = (IList)Activator.CreateInstance(targetType);
            }
            catch
            {
                var fallback = typeof(List<>).MakeGenericType(elementType);
                list = (IList)Activator.CreateInstance(fallback);
            }
            foreach (var item in array.Items)
            {
                list.Add(ConvertNode(item, elementType, options));
            }
            return list;
        }

        private static object ConvertToUntyped(JsonNode node)
        {
            if (node == null)
                return null;
            switch (node.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Number:
                    return ((JsonNumberNode)node).Value;
                case JsonValueKind.String:
                    return ((JsonStringNode)node).Value;
                case JsonValueKind.Array:
                    return ((JsonArrayNode)node).Items.Select(n => ConvertToUntyped(n)).ToList();
                case JsonValueKind.Object:
                    var obj = (JsonObjectNode)node;
                    var dict = new Dictionary<string, object>();
                    foreach (var kv in obj.Properties)
                        dict[kv.Key] = ConvertToUntyped(kv.Value);
                    return dict;
                default:
                    return null;
            }
        }

        private static double GetNumber(JsonNode node)
        {
            if (node is JsonNumberNode n)
                return n.Value;
            if (node is JsonStringNode s && double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            throw new InvalidOperationException("Element is not numeric.");
        }

        private static JsonNode CreateNode(object value, Type type, JsonSerializerOptions options)
        {
            if (type == null && value != null)
                type = value.GetType();
            if (value == null)
                return JsonNullNode.Instance;

            type ??= value.GetType();
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null)
            {
                type = underlying;
                if (value == null)
                    return JsonNullNode.Instance;
            }

            if (value is JsonElement element)
                return element.Node ?? JsonNullNode.Instance;

            if (value is string s)
                return new JsonStringNode(s);
            if (value is bool b)
                return new JsonBooleanNode(b);
            if (value is int || value is long || value is short || value is byte || value is uint || value is ulong || value is ushort || value is sbyte)
                return new JsonNumberNode(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            if (value is float || value is double || value is decimal)
                return new JsonNumberNode(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            if (value is DateTime dt)
                return new JsonStringNode(dt.ToString("o", CultureInfo.InvariantCulture));
            if (value is Guid guid)
                return new JsonStringNode(guid.ToString());
            if (type.IsEnum)
                return new JsonStringNode(value.ToString());

            if (value is IDictionary dictionary)
            {
                var obj = new JsonObjectNode();
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key == null)
                        continue;
                    var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                    obj.Set(key, CreateNode(entry.Value, entry.Value?.GetType(), options));
                }
                return obj;
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                var items = new List<JsonNode>();
                foreach (var item in enumerable)
                {
                    items.Add(CreateNode(item, item?.GetType(), options));
                }
                return new JsonArrayNode(items);
            }

            var objectNode = new JsonObjectNode();
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0);
            foreach (var prop in props)
            {
                var propValue = prop.GetValue(value);
                var name = options?.PropertyNamingPolicy?.ConvertName(prop.Name) ?? prop.Name;
                objectNode.Set(name, CreateNode(propValue, prop.PropertyType, options));
            }
            return objectNode;
        }
    }
}
