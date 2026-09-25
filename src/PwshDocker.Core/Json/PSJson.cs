using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Internal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PwshDocker;

/// <summary>Converts between Engine API JSON and PowerShell objects.</summary>
public static class PSJson
{
    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>
    /// Converts JSON to PowerShell values: objects become PSCustomObjects (property order preserved), arrays become
    /// object[], integers become long, and "Labels" maps become case-sensitive dictionaries.
    /// </summary>
    public static object? ToPSObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ConvertObject(element),
        JsonValueKind.Array => ConvertArray(element),
        JsonValueKind.String => element.GetString(),
        // Box each branch: `cond ? long : double` would silently make every integer a double.
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? (object)integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary>Parses JSON text and converts it with <see cref="ToPSObject(JsonElement)"/>.</summary>
    public static object? ToPSObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ToPSObject(document.RootElement);
    }

    /// <summary>Converts a JSON object of string values to a case-sensitive dictionary (null/absent gives an empty one).</summary>
    public static Dictionary<string, string> ToStringDictionary(JsonElement element)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return dictionary;
        }

        foreach (var property in element.EnumerateObject())
        {
            dictionary[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Null => string.Empty,
                _ => property.Value.GetRawText(),
            };
        }

        return dictionary;
    }

    private static PSObject ConvertObject(JsonElement element)
    {
        var result = new PSObject();
        foreach (var property in element.EnumerateObject())
        {
            object? value = property.Name == "Labels" && property.Value.ValueKind == JsonValueKind.Object
                ? ToStringDictionary(property.Value)
                : ToPSObject(property.Value);

            // PSObject property names are case-insensitive; last one wins on collisions.
            if (result.Properties[property.Name] is { } existing)
            {
                existing.Value = value;
            }
            else
            {
                result.Properties.Add(new PSNoteProperty(property.Name, value));
            }
        }

        return result;
    }

    private static object?[] ConvertArray(JsonElement element)
    {
        var items = new object?[element.GetArrayLength()];
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            items[index++] = ToPSObject(item);
        }

        return items;
    }

    /// <summary>Removes the PSObject wrapper around .NET values (PSCustomObjects are kept).</summary>
    public static object? Unwrap(object? value)
    {
        if (value is PSObject psObject)
        {
            if (ReferenceEquals(psObject, AutomationNull.Value))
            {
                return null;
            }

            return psObject.BaseObject is PSCustomObject ? psObject : psObject.BaseObject;
        }

        return value;
    }

    /// <summary>
    /// Serializes PowerShell values to JSON: hashtables/ordered dictionaries and PSCustomObjects become objects,
    /// sequences become arrays, switches become booleans, TimeSpans become nanoseconds (the engine's duration
    /// unit), dates become RFC 3339 strings.
    /// </summary>
    public static string Serialize(object? value, bool indented = false)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented, Encoder = Encoder }))
        {
            Write(writer, value, 0);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(Utf8JsonWriter writer, object? value, int depth)
    {
        if (depth > 64)
        {
            throw new ArgumentException("The object is nested too deeply to serialize (is there a reference cycle?).");
        }

        value = Unwrap(value);
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string text:
                writer.WriteStringValue(text);
                return;
            case char character:
                writer.WriteStringValue(character.ToString());
                return;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                return;
            case SwitchParameter switchParameter:
                writer.WriteBooleanValue(switchParameter.IsPresent);
                return;
            case byte or sbyte or short or ushort or int or uint or long:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return;
            case ulong unsigned:
                writer.WriteNumberValue(unsigned);
                return;
            case float or double:
                writer.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                return;
            case decimal number:
                writer.WriteNumberValue(number);
                return;
            case Enum enumValue:
                writer.WriteStringValue(enumValue.ToString());
                return;
            case DateTime dateTime:
                writer.WriteStringValue(dateTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                return;
            case DateTimeOffset dateTimeOffset:
                writer.WriteStringValue(dateTimeOffset.ToString("o", CultureInfo.InvariantCulture));
                return;
            case TimeSpan timeSpan:
                writer.WriteNumberValue(timeSpan.Ticks * 100);
                return;
            case Guid or Version or Uri:
                writer.WriteStringValue(value.ToString());
                return;
            case JsonElement element:
                element.WriteTo(writer);
                return;
            case JsonNode node:
                node.WriteTo(writer);
                return;
            case IDictionary dictionary:
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    writer.WritePropertyName(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty);
                    Write(writer, entry.Value, depth + 1);
                }

                writer.WriteEndObject();
                return;
            case PSObject customObject:
                writer.WriteStartObject();
                foreach (var property in customObject.Properties)
                {
                    if (!property.IsGettable)
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, depth + 1);
                }

                writer.WriteEndObject();
                return;
            case IEnumerable sequence:
                writer.WriteStartArray();
                foreach (var item in sequence)
                {
                    Write(writer, item, depth + 1);
                }

                writer.WriteEndArray();
                return;
            default:
                JsonSerializer.Serialize(writer, value, value.GetType());
                return;
        }
    }
}
