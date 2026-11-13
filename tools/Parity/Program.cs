// Parity.exe <goldens.jsonl> [<dump.jsonl>]: runs every scenario the Python oracle ran and compares the records.
// Exit 1 if any differ. With a second argument, each .NET record is written there too, for a closer look.
using System.Text.Json.Nodes;
using MeetingPreps.Parity;

var count = 0;
var different = new List<string>();
foreach (var line in File.ReadLines(args[0]))
{
    if (string.IsNullOrWhiteSpace(line))
        continue;
    var golden = (JsonObject)JsonNode.Parse(line)!;
    var scenario = (JsonObject)golden["scenario"]!;
    count++;
    var record = ScenarioRunner.Run(scenario).ToJsonString();
    if (args.Length > 1)
        File.AppendAllText(args[1], record + "\n");
    if (ScenarioRunner.Difference(golden["result"], JsonNode.Parse(record), "$", (bool?)scenario["loose_message"] ?? false) is { } difference)
        different.Add($"{scenario["name"]}: {difference}");
}
Console.WriteLine($"{count} scenarios, {different.Count} different");
foreach (var d in different.Take(10))
    Console.WriteLine(d);
return different.Count == 0 ? 0 : 1;
