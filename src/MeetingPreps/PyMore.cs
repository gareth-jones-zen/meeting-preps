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
}
