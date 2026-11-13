using System.Text.Json.Nodes;
using M365Graph;

namespace MeetingPreps;

/// <summary>What the tool asks of Microsoft Graph: reads only. Tests and parity checks give a fake.</summary>
public interface IGraph
{
    /// <summary>One GET, as a JSON object.</summary>
    JsonObject Get(string url);

    /// <summary>Every item across @odata.nextLink pages, the same headers on every page. Lazy, as Python's
    /// generator was: a caller that stops early fetches no more pages.</summary>
    IEnumerable<JsonNode?> Pages(string url, IReadOnlyDictionary<string, string>? headers = null);
}

/// <summary>The real Graph, through M365Graph. Only GET exists here: meeting-preps never writes to Graph.</summary>
public sealed class GraphReader(GraphClient client) : IGraph
{
    public GraphClient Client { get; } = client;

    public static GraphClient ClientFor(Config config, bool? interactive = null)
    {
        var options = new GraphClientOptions
        {
            Tenant = config.Tenant,
            LoginHint = config.LoginHint,
            Scopes = config.Scopes,
            Interactive = interactive,
            SignInHint = "At a console, run: meeting-preps sign-in",
        };
        if (config.MsalCache is not null)
            options = options with { CachePath = config.MsalCache };
        return new GraphClient(options);
    }

    public JsonObject Get(string url) => Client.GetAsync(url).GetAwaiter().GetResult();

    public IEnumerable<JsonNode?> Pages(string url, IReadOnlyDictionary<string, string>? headers = null)
    {
        string? next = url;
        while (!string.IsNullOrEmpty(next))
        {
            var page = Client.GetAsync(next, null, headers).GetAwaiter().GetResult();
            if (page["value"] is JsonArray items)
                foreach (var item in items)
                    yield return item;
            next = (string?)page["@odata.nextLink"];
        }
    }
}
