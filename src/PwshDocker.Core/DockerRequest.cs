using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PwshDocker;

/// <summary>An Engine API request. Paths are unversioned ("/containers/json"); the client adds "/v1.xx".</summary>
public sealed class DockerRequest
{
    public DockerRequest(string method, string path)
    {
        Method = method.ToUpperInvariant();
        Path = path.StartsWith('/') ? path : "/" + path;
    }

    public DockerRequest(string method, string path, IDictionary? query) : this(method, path)
    {
        AddQuery(query);
    }

    public DockerRequest(string method, string path, IDictionary? query, object? body) : this(method, path, query)
    {
        Body = body;
    }

    public string Method { get; set; }

    public string Path { get; set; }

    public List<KeyValuePair<string, string>> Query { get; } = new();

    /// <summary>
    /// Request body: a hashtable/PSObject/array (serialized as JSON), a JSON string, byte[], Stream or HttpContent.
    /// </summary>
    public object? Body { get; set; }

    /// <summary>Overrides the body content type (JSON bodies default to application/json).</summary>
    public string? ContentType { get; set; }

    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Send without the /v1.xx prefix (e.g. /_ping).</summary>
    public bool Unversioned { get; set; }

    /// <summary>Request an HTTP/1.1 upgrade (connection hijack) for attach/exec streams.</summary>
    public bool Upgrade { get; set; }

    /// <summary>Time allowed for the engine to start responding. Null means no limit.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Caller-defined correlation value (for parallel operations).</summary>
    public object? State { get; set; }

    /// <summary>Adds every entry of a hashtable to the query string. Null values are skipped.</summary>
    public DockerRequest AddQuery(IDictionary? query)
    {
        if (query is null)
        {
            return this;
        }

        foreach (DictionaryEntry entry in query)
        {
            AddQuery(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty, entry.Value);
        }

        return this;
    }

    /// <summary>
    /// Adds a query parameter. Booleans become true/false, dates become Unix timestamps, arrays repeat the key,
    /// and dictionaries are JSON-encoded (filters values are normalized to string arrays).
    /// </summary>
    public DockerRequest AddQuery(string name, object? value)
    {
        value = PSJson.Unwrap(value);
        switch (value)
        {
            case null:
                break;
            case SwitchParameter switchValue:
                Query.Add(new(name, switchValue.IsPresent ? "true" : "false"));
                break;
            case bool boolean:
                Query.Add(new(name, boolean ? "true" : "false"));
                break;
            case string text:
                Query.Add(new(name, text));
                break;
            case DateTime dateTime:
                Query.Add(new(name, DockerTime.ToUnixTimestamp(dateTime)));
                break;
            case DateTimeOffset dateTimeOffset:
                Query.Add(new(name, DockerTime.ToUnixTimestamp(dateTimeOffset)));
                break;
            case IDictionary dictionary:
                Query.Add(new(name, name == "filters" ? SerializeFilters(dictionary) : PSJson.Serialize(dictionary)));
                break;
            case IEnumerable sequence:
                foreach (var item in sequence)
                {
                    AddQuery(name, item);
                }

                break;
            case Enum enumValue:
                Query.Add(new(name, enumValue.ToString()));
                break;
            case IFormattable formattable:
                Query.Add(new(name, formattable.ToString(null, CultureInfo.InvariantCulture)));
                break;
            default:
                Query.Add(new(name, value.ToString() ?? string.Empty));
                break;
        }

        return this;
    }

    /// <summary>Serializes engine filters: {"key": ["v1", "v2"]}. Scalar values become one-element arrays.</summary>
    public static string SerializeFilters(IDictionary filters)
    {
        var normalized = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in filters)
        {
            var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
            if (!normalized.TryGetValue(key, out var values))
            {
                normalized[key] = values = new List<string>();
            }

            var value = PSJson.Unwrap(entry.Value);
            if (value is string or not IEnumerable)
            {
                if (value is not null)
                {
                    values.Add(FormatFilterValue(value));
                }
            }
            else
            {
                foreach (var item in (IEnumerable)value)
                {
                    var unwrapped = PSJson.Unwrap(item);
                    if (unwrapped is not null)
                    {
                        values.Add(FormatFilterValue(unwrapped));
                    }
                }
            }
        }

        return JsonSerializer.Serialize(normalized);
    }

    private static string FormatFilterValue(object value) => value switch
    {
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Builds the relative request URI, including the API version prefix and query string.</summary>
    public string BuildRelativeUri(Version? apiVersion)
    {
        var builder = new StringBuilder();
        if (apiVersion is not null && !Unversioned)
        {
            builder.Append("/v").Append(apiVersion.ToString(2));
        }

        builder.Append(Path);
        for (var i = 0; i < Query.Count; i++)
        {
            builder.Append(i == 0 ? '?' : '&')
                .Append(Uri.EscapeDataString(Query[i].Key))
                .Append('=')
                .Append(Uri.EscapeDataString(Query[i].Value));
        }

        return builder.ToString();
    }

    internal HttpRequestMessage ToHttpRequestMessage(Version? apiVersion)
    {
        var message = new HttpRequestMessage(new HttpMethod(Method), new Uri(BuildRelativeUri(apiVersion), UriKind.Relative));
        foreach (var header in Headers)
        {
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (Upgrade)
        {
            message.Headers.Connection.Add("Upgrade");
            message.Headers.Upgrade.Add(new ProductHeaderValue("tcp"));
        }

        if (Body is not null)
        {
            message.Content = CreateContent();
        }

        return message;
    }

    private HttpContent CreateContent()
    {
        var body = PSJson.Unwrap(Body);
        HttpContent content = body switch
        {
            HttpContent httpContent => httpContent,
            Stream stream => new StreamContent(stream, 81920),
            byte[] bytes => new ByteArrayContent(bytes),
            string text => new StringContent(text, new UTF8Encoding(false), "application/json"),
            _ => new StringContent(PSJson.Serialize(body), new UTF8Encoding(false), "application/json"),
        };

        if (ContentType is not null)
        {
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContentType);
        }

        return content;
    }

    public override string ToString() => $"{Method} {BuildRelativeUri(null)}";
}
