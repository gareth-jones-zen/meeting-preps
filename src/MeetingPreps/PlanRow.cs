using System.Text.Json.Nodes;

namespace MeetingPreps;

/// <summary>One meeting in the plan. The Python kept it as a dict, and `plan --json` shows its keys in the order they
/// were set, so the row keeps that order: setting a key again keeps its place, a new key goes at the end.</summary>
public sealed class PlanRow
{
    private readonly JsonObject data;

    public PlanRow(string start, string end, string subject)
    {
        data = new JsonObject { ["start"] = start, ["end"] = end, ["subject"] = subject };
    }

    private PlanRow(JsonObject data) => this.data = data;

    /// <summary>{**row}: a copy, keys in the same order.</summary>
    public PlanRow Copy() => new((JsonObject)data.DeepClone());

    public JsonObject Json => data;

    private string? Text(string key) => data.TryGetPropertyValue(key, out var v) && v is not null ? (string?)v : null;

    private void Set(string key, JsonNode? value) => data[key] = value;

    public bool Has(string key) => data.ContainsKey(key);

    public string Start => Text("start")!;
    public string End => Text("end")!;
    public string Subject => Text("subject")!;

    public string Decision { get => Text("decision")!; set => Set("decision", value); }
    public string? Why { get => Text("why"); set => Set("why", value); }
    public string? Kind { get => Text("kind"); set => Set("kind", value); }
    /// <summary>The person (null for a series, where the key is still there, as None).</summary>
    public string? With { get => Text("with"); set => Set("with", value); }
    public string? Series { get => Text("series"); set => Set("series", value); }
    public string? Due { get => Text("due"); set => Set("due", value); }
    public string? File { get => Text("file"); set => Set("file", value); }
    public bool Exists { get => data["exists"]?.GetValue<bool>() ?? false; set => Set("exists", value); }
    public string? Previous { get => Text("previous"); set => Set("previous", value); }
    public string? Companion { get => Text("companion"); set => Set("companion", value); }
    public string? NotAttending { get => Text("not_attending"); set => Set("not_attending", value); }

    public (string Subject, string Start, string End)? Absorbed
    {
        get => data["absorbed"] is JsonObject a ? ((string)a["subject"]!, (string)a["start"]!, (string)a["end"]!) : null;
        set => Set("absorbed", value is { } v ? new JsonObject { ["subject"] = v.Subject, ["start"] = v.Start, ["end"] = v.End } : null);
    }

    /// <summary>row.update(decision=..., why=...).</summary>
    public void Skip(string why)
    {
        Decision = "skip";
        Why = why;
    }
}
