using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DataDrivenGoap
{
    internal static class JsonUtilities
    {
        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return default;
            }

            var data = MiniJson.Deserialize(json);
            return (T)ConvertValue(typeof(T), data);
        }

        public static T ConvertTo<T>(object data)
        {
            if (data == null)
            {
                return default;
            }

            return (T)ConvertValue(typeof(T), data);
        }

        public static T Deserialize<T>(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            var json = reader.ReadToEnd();
            return Deserialize<T>(json);
        }

        public static Dictionary<string, string> ParseStringDictionary(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            if (MiniJson.Deserialize(json) is Dictionary<string, object> obj)
            {
                foreach (var kv in obj)
                {
                    if (kv.Value is string str && !string.IsNullOrWhiteSpace(str))
                    {
                        result[kv.Key] = str.Trim();
                    }
                }
            }

            return result;
        }

        private static object ConvertValue(Type targetType, object value)
        {
            if (targetType == typeof(object) || targetType == null)
            {
                return value;
            }

            if (value == null)
            {
                if (IsNullable(targetType))
                {
                    return null;
                }

                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            var nullableType = Nullable.GetUnderlyingType(targetType);
            if (nullableType != null)
            {
                return ConvertValue(nullableType, value);
            }

            if (targetType.IsAssignableFrom(value.GetType()))
            {
                return value;
            }

            if (targetType.IsEnum)
            {
                if (value is string enumString)
                {
                    return Enum.Parse(targetType, enumString, ignoreCase: true);
                }

                if (IsNumeric(value))
                {
                    return Enum.ToObject(targetType, Convert.ToInt32(value, CultureInfo.InvariantCulture));
                }
            }

            if (targetType == typeof(string))
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }

            if (targetType == typeof(bool))
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }

            if (targetType == typeof(int))
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }

            if (targetType == typeof(float))
            {
                return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            }

            if (targetType == typeof(double))
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }

            if (targetType == typeof(long))
            {
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }

            if (targetType == typeof(uint))
            {
                return Convert.ToUInt32(value, CultureInfo.InvariantCulture);
            }

            if (targetType == typeof(ulong))
            {
                return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            }

            if (targetType.IsArray)
            {
                var elementType = targetType.GetElementType();
                if (value is IList list)
                {
                    var array = Array.CreateInstance(elementType, list.Count);
                    for (int i = 0; i < list.Count; i++)
                    {
                        array.SetValue(ConvertValue(elementType, list[i]), i);
                    }

                    return array;
                }

                return Array.CreateInstance(elementType, 0);
            }

            if (ImplementsGenericInterface(targetType, typeof(IList<>), out var listElementType))
            {
                var list = (IList)Activator.CreateInstance(targetType);
                if (value is IList sourceList)
                {
                    foreach (var item in sourceList)
                    {
                        list.Add(ConvertValue(listElementType, item));
                    }
                }

                return list;
            }

            if (ImplementsGenericInterface(targetType, typeof(IDictionary<,>), out var keyType, out var valueType))
            {
                IDictionary dictionary;
                if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(Dictionary<,>) && keyType == typeof(string))
                {
                    try
                    {
                        dictionary = (IDictionary)Activator.CreateInstance(targetType, StringComparer.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        dictionary = (IDictionary)Activator.CreateInstance(targetType);
                    }
                }
                else
                {
                    dictionary = (IDictionary)Activator.CreateInstance(targetType);
                }

                if (value is Dictionary<string, object> dict)
                {
                    foreach (var kvp in dict)
                    {
                        var key = ConvertValue(keyType, kvp.Key);
                        var convertedValue = ConvertValue(valueType, kvp.Value);
                        dictionary[key] = convertedValue;
                    }
                }

                return dictionary;
            }

            if (value is Dictionary<string, object> obj)
            {
                var instance = Activator.CreateInstance(targetType);
                var fields = targetType.GetFields();
                foreach (var field in fields)
                {
                    if (field.IsInitOnly)
                    {
                        continue;
                    }

                    if (TryGetValue(obj, field.Name, out var fieldValue))
                    {
                        var converted = ConvertValue(field.FieldType, fieldValue);
                        field.SetValue(instance, converted);
                    }
                }

                var properties = targetType.GetProperties();
                foreach (var property in properties)
                {
                    if (!property.CanWrite)
                    {
                        continue;
                    }

                    if (TryGetValue(obj, property.Name, out var propertyValue))
                    {
                        var converted = ConvertValue(property.PropertyType, propertyValue);
                        property.SetValue(instance, converted);
                    }
                }

                return instance;
            }

            return value;
        }

        private static bool ImplementsGenericInterface(Type type, Type interfaceType, out Type elementType)
        {
            elementType = null;
            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == interfaceType)
                {
                    elementType = iface.GetGenericArguments()[0];
                    return true;
                }
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == interfaceType)
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }

            return false;
        }

        private static bool ImplementsGenericInterface(Type type, Type interfaceType, out Type keyType, out Type valueType)
        {
            keyType = null;
            valueType = null;
            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == interfaceType)
                {
                    var args = iface.GetGenericArguments();
                    keyType = args[0];
                    valueType = args[1];
                    return true;
                }
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == interfaceType)
            {
                var args = type.GetGenericArguments();
                keyType = args[0];
                valueType = args[1];
                return true;
            }

            return false;
        }

        private static bool TryGetValue(Dictionary<string, object> dict, string key, out object value)
        {
            if (dict.TryGetValue(key, out value))
            {
                return true;
            }

            foreach (var kv in dict)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = kv.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        private static bool IsNullable(Type type) => Nullable.GetUnderlyingType(type) != null;

        private static bool IsNumeric(object value)
        {
            return value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;
        }
    }

    internal static class MiniJson
    {
        public static object Deserialize(string json)
        {
            if (json == null)
            {
                return null;
            }

            return Parser.Parse(json);
        }

        private sealed class Parser : IDisposable
        {
            private readonly StringReader _reader;

            private Parser(string json)
            {
                _reader = new StringReader(json);
            }

            public static object Parse(string json)
            {
                using var parser = new Parser(json);
                return parser.ParseValue();
            }

            public void Dispose()
            {
                _reader.Dispose();
            }

            private char PeekChar() => Convert.ToChar(_reader.Peek());

            private char NextChar() => Convert.ToChar(_reader.Read());

            private string NextWord()
            {
                var sb = new StringBuilder();
                while (!IsEnd)
                {
                    char ch = PeekChar();
                    if (char.IsWhiteSpace(ch) || ch == ',' || ch == ']' || ch == '}' || ch == ':')
                    {
                        break;
                    }

                    sb.Append(ch);
                    NextChar();
                }

                return sb.ToString();
            }

            private void SkipWhitespace()
            {
                while (!IsEnd && char.IsWhiteSpace(PeekChar()))
                {
                    NextChar();
                }
            }

            private bool IsEnd => _reader.Peek() == -1;

            private object ParseValue()
            {
                SkipWhitespace();
                if (IsEnd)
                {
                    return null;
                }

                char ch = PeekChar();
                return ch switch
                {
                    '{' => ParseObject(),
                    '[' => ParseArray(),
                    '"' => ParseString(),
                    't' or 'f' => ParseBoolean(),
                    'n' => ParseNull(),
                    _ => ParseNumber(),
                };
            }

            private object ParseObject()
            {
                var table = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                NextChar(); // {
                while (true)
                {
                    SkipWhitespace();
                    if (IsEnd)
                    {
                        break;
                    }

                    char ch = PeekChar();
                    if (ch == '}')
                    {
                        NextChar();
                        break;
                    }

                    string key = ParseString();
                    SkipWhitespace();

                    if (!IsEnd && PeekChar() == ':')
                    {
                        NextChar();
                    }

                    var value = ParseValue();
                    table[key] = value;

                    SkipWhitespace();
                    if (!IsEnd)
                    {
                        ch = PeekChar();
                        if (ch == ',')
                        {
                            NextChar();
                            continue;
                        }

                        if (ch == '}')
                        {
                            NextChar();
                            break;
                        }
                    }
                }

                return table;
            }

            private object ParseArray()
            {
                var array = new List<object>();
                NextChar(); // [

                var parsing = true;
                while (parsing)
                {
                    SkipWhitespace();
                    if (IsEnd)
                    {
                        break;
                    }

                    char ch = PeekChar();
                    if (ch == ']')
                    {
                        NextChar();
                        break;
                    }

                    var value = ParseValue();
                    array.Add(value);

                    SkipWhitespace();
                    if (IsEnd)
                    {
                        break;
                    }

                    ch = PeekChar();
                    switch (ch)
                    {
                        case ',':
                            NextChar();
                            break;
                        case ']':
                            NextChar();
                            parsing = false;
                            break;
                        default:
                            parsing = false;
                            break;
                    }
                }

                return array;
            }

            private string ParseString()
            {
                var sb = new StringBuilder();

                NextChar(); // "
                while (!IsEnd)
                {
                    char ch = NextChar();
                    if (ch == '"')
                    {
                        break;
                    }

                    if (ch == '\\')
                    {
                        if (IsEnd)
                        {
                            break;
                        }

                        ch = NextChar();
                        switch (ch)
                        {
                            case '"':
                            case '\\':
                            case '/':
                                sb.Append(ch);
                                break;
                            case 'b':
                                sb.Append('\b');
                                break;
                            case 'f':
                                sb.Append('\f');
                                break;
                            case 'n':
                                sb.Append('\n');
                                break;
                            case 'r':
                                sb.Append('\r');
                                break;
                            case 't':
                                sb.Append('\t');
                                break;
                            case 'u':
                                var hex = new char[4];
                                for (int i = 0; i < 4; i++)
                                {
                                    hex[i] = NextChar();
                                }

                                sb.Append((char)Convert.ToInt32(new string(hex), 16));
                                break;
                        }
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                }

                return sb.ToString();
            }

            private object ParseNumber()
            {
                var word = NextWord();
                if (word.IndexOf('.') != -1 || word.IndexOf('e') != -1 || word.IndexOf('E') != -1)
                {
                    if (double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleResult))
                    {
                        return doubleResult;
                    }
                }
                else if (long.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longResult))
                {
                    return longResult;
                }

                return 0d;
            }

            private object ParseBoolean()
            {
                var word = NextWord();
                return string.Equals(word, "true", StringComparison.OrdinalIgnoreCase);
            }

            private object ParseNull()
            {
                NextWord();
                return null;
            }
        }
    }
}
