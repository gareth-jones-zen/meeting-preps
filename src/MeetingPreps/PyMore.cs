using System.Globalization;
using System.Text;
using PyCompat;

namespace MeetingPreps;

/// <summary>A str.format field the port doesn't support: a conversion, a format spec, an index or an attribute.
/// None is used by the tool's own templates or by any settings seen; Python would accept some of them.</summary>
public sealed class UnsupportedFieldException(string message) : Exception(message);

/// <summary>A str.format field with no value: Python's KeyError.</summary>
public sealed class MissingFieldException(string field) : Exception($"'{field}'")
{
    public string Field { get; } = field;
}

/// <summary>The few Python behaviours this tool needs that PyCompat doesn't have.</summary>
public static class PyMore
{
    /// <summary>str.format with named fields only, in one pass (a value is never read as a template). "{{" and "}}"
    /// are literal braces. A field that has no value throws MissingFieldException, as Python's KeyError.</summary>
    public static string Format(string template, IReadOnlyDictionary<string, string> values)
    {
        var b = new StringBuilder(template.Length * 2);
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    b.Append('{');
                    i++;
                    continue;
                }
                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                    throw new UnsupportedFieldException("Single '{' encountered in format string");
                var field = template[(i + 1)..close];
                if (field.Length == 0 || field.IndexOfAny(['!', ':', '[', '.', '{']) >= 0 || char.IsAsciiDigit(field[0]))
                    throw new UnsupportedFieldException($"The field {{{field}}} isn't supported (only {{name}} is).");
                if (!values.TryGetValue(field, out var value))
                    throw new MissingFieldException(field);
                b.Append(value);
                i = close;
                continue;
            }
            if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}')
                {
                    b.Append('}');
                    i++;
                    continue;
                }
                throw new UnsupportedFieldException("Single '}' encountered in format string");
            }
            b.Append(c);
        }
        return b.ToString();
    }

    /// <summary>urllib.parse.urlencode for (key, value) text pairs: quote_plus on each, joined with "&amp;".</summary>
    public static string UrlEncode(IEnumerable<(string Key, string Value)> pairs) =>
        string.Join('&', pairs.Select(p => QuotePlus(p.Key) + "=" + QuotePlus(p.Value)));

    /// <summary>urllib.parse.quote_plus: letters, digits and "_.-~" kept, spaces as "+", the rest as %XX of UTF-8.</summary>
    public static string QuotePlus(string text)
    {
        var b = new StringBuilder();
        foreach (var x in Encoding.UTF8.GetBytes(text))
        {
            if (x is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
                || x is (byte)'_' or (byte)'.' or (byte)'-' or (byte)'~')
                b.Append((char)x);
            else if (x == (byte)' ')
                b.Append('+');
            else
                b.Append('%').Append(x.ToString("X2", CultureInfo.InvariantCulture));
        }
        return b.ToString();
    }

    /// <summary>repr() of a list of strings: ['a', 'b'].</summary>
    public static string ListRepr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Py.ReprString)) + "]";

    /// <summary>datetime.fromisoformat(text.replace("Z", "+00:00")) for Graph's UTC times: seconds, optional
    /// fraction (Python keeps microseconds, so anything finer is cut), and an offset.</summary>
    public static DateTimeOffset AwareFromIso(string text)
    {
        var t = text.Replace("Z", "+00:00", StringComparison.Ordinal);
        if (!DateTimeOffset.TryParseExact(t,
                ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mmzzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFFFzzz"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new FormatException($"Invalid isoformat string: {Py.ReprString(text)}");
        return new DateTimeOffset(value.Ticks - value.Ticks % 10, value.Offset);
    }

    /// <summary>len(text), in code points.</summary>
    public static int Len(string text) => PyStr.Length(text);

    /// <summary>text[-count:], for a list.</summary>
    public static List<T> Last<T>(List<T> items, int count) => count <= 0 ? items : items.Skip(Math.Max(0, items.Count - count)).ToList();

    /// <summary>str(error): the message, as Python printed an exception.</summary>
    public static string ErrorText(Exception error) => error.Message;

    /// <summary>type(error).__name__: the Python name for the exceptions this tool meets, so a log line or a prep's
    /// first lines read as they did; anything else keeps its .NET name.</summary>
    public static string PyName(Exception error) => error switch
    {
        CalendarUnavailableException => "CalendarUnavailable",
        ProcessTimeoutException => "TimeoutExpired",
        TimeoutException or OperationCanceledException => "TimeoutError",
        System.Net.Http.HttpRequestException => "URLError",
        FileNotFoundException or DirectoryNotFoundException => "FileNotFoundError",
        UnauthorizedAccessException => "PermissionError",
        IOException => "OSError",
        System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 } => "FileNotFoundError",
        System.ComponentModel.Win32Exception { NativeErrorCode: 5 } => "PermissionError",
        System.ComponentModel.Win32Exception => "OSError",
        System.Text.Json.JsonException => "JSONDecodeError",
        KeyNotFoundException => "KeyError",
        FormatException => "ValueError",
        InvalidOperationException => "RuntimeError",
        _ => error.GetType().Name,
    };

    /// <summary>The phrase Python's http.HTTPStatus gives each 4xx and 5xx status, as urllib's HTTPError.reason.</summary>
    private static readonly Dictionary<int, string> Phrases = new()
    {
        [400] = "Bad Request", [401] = "Unauthorized", [402] = "Payment Required", [403] = "Forbidden", [404] = "Not Found",
        [405] = "Method Not Allowed", [406] = "Not Acceptable", [407] = "Proxy Authentication Required", [408] = "Request Timeout",
        [409] = "Conflict", [410] = "Gone", [411] = "Length Required", [412] = "Precondition Failed", [413] = "Content Too Large",
        [414] = "URI Too Long", [415] = "Unsupported Media Type", [416] = "Range Not Satisfiable", [417] = "Expectation Failed",
        [418] = "I'm a Teapot", [421] = "Misdirected Request", [422] = "Unprocessable Content", [423] = "Locked",
        [424] = "Failed Dependency", [425] = "Too Early", [426] = "Upgrade Required", [428] = "Precondition Required",
        [429] = "Too Many Requests", [431] = "Request Header Fields Too Large", [451] = "Unavailable For Legal Reasons",
        [500] = "Internal Server Error", [501] = "Not Implemented", [502] = "Bad Gateway", [503] = "Service Unavailable",
        [504] = "Gateway Timeout", [505] = "HTTP Version Not Supported", [506] = "Variant Also Negotiates",
        [507] = "Insufficient Storage", [508] = "Loop Detected", [510] = "Not Extended", [511] = "Network Authentication Required",
    };

    /// <summary>engine.reason(): why a Graph call failed, short enough for a prep's first lines and a notification.</summary>
    public static string Reason(Exception error)
    {
        if (error is M365Graph.GraphRequestException graph)
        {
            var code = (int)graph.Status;
            return $"HTTP {code} {Phrases.GetValueOrDefault(code, "")}".TrimEnd();
        }
        if (error is M365Graph.SignInNeededException)
            return "sign-in needed";
        var text = error.Message;
        for (Exception? e = error; e is not null; e = e.InnerException)
            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound
                    or System.Net.Sockets.SocketError.TryAgain or System.Net.Sockets.SocketError.NoData })
                return "no network (name lookup failed)";
        if (text.Contains("getaddrinfo", StringComparison.Ordinal) || text.Contains("NameResolution", StringComparison.Ordinal))
            return "no network (name lookup failed)";
        return PyStr.Head($"{PyName(error)}: {text}", 120);
    }

    /// <summary>engine.and_list(): "a", "a and b", "a, b and c".</summary>
    public static string AndList(IReadOnlyList<string> items) =>
        items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    /// <summary>f"{x:g}": six significant figures, trailing zeros dropped, an exponent when it's very big or small.</summary>
    public static string FormatG(double x) => Py.G(x).Replace("E", "e", StringComparison.Ordinal);

    /// <summary>float(text): Python's reading of a number in text (spaces around it, "_" between digits, inf and nan),
    /// or its ValueError, as a SettingsException.</summary>
    public static double ParseFloat(string text)
    {
        var t = PyStr.Strip(text);
        var bad = new SettingsException($"could not convert string to float: {Py.ReprString(text)}");
        if (t.Length == 0)
            throw bad;
        var lower = t.ToLowerInvariant().TrimStart('+', '-');
        var negative = t.StartsWith('-');
        if (lower is "inf" or "infinity" && t.Length - lower.Length <= 1)
            return negative ? double.NegativeInfinity : double.PositiveInfinity;
        if (lower == "nan" && t.Length - lower.Length <= 1)
            return double.NaN;
        for (var i = 0; i < t.Length; i++)
            if (t[i] == '_' && !(i > 0 && char.IsAsciiDigit(t[i - 1]) && i + 1 < t.Length && char.IsAsciiDigit(t[i + 1])))
                throw bad;
        if (!double.TryParse(t.Replace("_", "", StringComparison.Ordinal),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out var value))
            throw bad;
        return value;
    }
}
