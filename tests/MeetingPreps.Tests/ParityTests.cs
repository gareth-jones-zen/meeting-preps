using System.Text.Json.Nodes;

using MeetingPreps.Parity;

namespace MeetingPreps.Tests;

/// <summary>Every scenario the Python oracle ran (tests/parity/oracle.py), run again here: the records must be the same.
/// Golden/scenarios.jsonl is synthetic and committed. MEETING_PREPS_PARITY_FILE names a larger local file (the fuzz
/// run, or one built on real settings), which never goes in the repo.</summary>
public class ParityTests
{
    public static IEnumerable<TheoryDataRow<string>> Committed() =>
        Lines(Path.Combine(AppContext.BaseDirectory, "Golden", "scenarios.jsonl")).Select(n => new TheoryDataRow<string>(n));

    private static readonly Dictionary<string, JsonObject> Cache = [];

    private static IEnumerable<string> Lines(string file)
    {
        foreach (var line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var node = (JsonObject)JsonNode.Parse(line)!;
            var name = (string)node["scenario"]!["name"]!;
            lock (Cache)
                Cache[file + "|" + name] = node;
            yield return file + "|" + name;
        }
    }

    private static void Check(string key)
    {
        JsonObject golden;
        lock (Cache)
            golden = Cache[key];
        var scenario = (JsonObject)golden["scenario"]!;
        var actual = JsonNode.Parse(ScenarioRunner.Run(scenario).ToJsonString());
        var difference = ScenarioRunner.Difference(golden["result"], actual, "$", (bool?)scenario["loose_message"] ?? false);
        Assert.True(difference is null, $"{scenario["name"]}: {difference}");
    }

    [Theory]
    [MemberData(nameof(Committed))]
    public void Matches_the_python(string key) => Check(key);

    [Fact]
    public void Matches_the_python_on_the_local_file()
    {
        var file = Environment.GetEnvironmentVariable("MEETING_PREPS_PARITY_FILE");
        if (string.IsNullOrEmpty(file))
            Assert.Skip("MEETING_PREPS_PARITY_FILE isn't set");
        var failures = new List<string>();
        var count = 0;
        foreach (var key in Lines(file).ToList())
        {
            count++;
            try
            {
                Check(key);
            }
            catch (Exception error)
            {
                failures.Add(error.Message);
            }
        }
        File.WriteAllText(file + ".net-summary.txt", $"{count} scenarios, {failures.Count} different\n" + string.Join("\n\n", failures.Take(20)));
        Assert.True(failures.Count == 0, $"{failures.Count} of {count} differ; first: {failures.FirstOrDefault()}");
    }
}
