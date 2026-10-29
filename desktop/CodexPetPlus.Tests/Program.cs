using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CodexPetPlus.Core;

if (args.FirstOrDefault() == "--rpc-fixture") { await Fixture(args.ElementAtOrDefault(1) ?? "normal"); return 0; }

var now = DateTimeOffset.UtcNow;
var tests = new List<(string Name, Func<Task> Run)>();
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> run) => tests.Add((name, run));
JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }

Test("live API quota is remaining allowance, not token context", () => {
    var snapshot = QuotaParser.FromAppServer(Json("""{"rateLimits":{"limitId":"codex","primary":{"usedPercent":28,"windowDurationMins":300},"secondary":{"usedPercent":16,"windowDurationMins":10080}}}"""), now)!;
    Check(snapshot.Primary!.Remaining(now) == 72 && snapshot.Secondary!.Remaining(now) == 84, "Incorrect remaining percentages");
    Check(snapshot.Primary!.Label("?") == "5h" && snapshot.Secondary!.Label("?") == "주간", "Incorrect duration labels");
});
Test("multi-bucket response selects Codex instead of another model", () => {
    var snapshot = QuotaParser.FromAppServer(Json("""{"rateLimits":{"limitId":"other","primary":{"usedPercent":99}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":20}}}}"""), now);
    Check(snapshot?.Primary?.Remaining(now) == 80, "Wrong bucket selected");
    Check(QuotaParser.FromAppServer(Json("""{"rateLimits":{"limitId":"other","primary":{"usedPercent":20}}}"""), now) is null, "Other model presented as Codex");
});
Test("null, missing, and invalid quota fields stay unknown", () => {
    foreach (var raw in new[] { "null", "[]", "{}", """{"rateLimits":null}""", """{"rateLimits":{"primary":{"usedPercent":"10"}}}""", """{"rateLimits":{"primary":{"usedPercent":101}}}""" })
        Check(QuotaParser.FromAppServer(Json(raw), now) is null, "Invalid quota became a percentage");
    var valid = QuotaParser.FromAppServer(Json("""{"rateLimits":{"primary":{"usedPercent":0,"windowDurationMins":null,"resetsAt":null}}}"""), now);
    Check(valid?.Primary?.Remaining(now) == 100, "Zero usage was mistaken for missing data");
    Check(new QuotaWindow(100, null, null).Remaining(now) == 0, "Exhausted quota was hidden");
});
Test("expired reset timestamps never become a guessed full quota", () => {
    Check(new QuotaWindow(20, 300, now.AddSeconds(-1)).Remaining(now) is null, "Expired value displayed as current");
    Check(new QuotaWindow(double.NaN, 300, now.AddHours(1)).Remaining(now) is null, "Nonfinite value displayed");
});
Test("panel is centered directly below the pet", () => {
    var position = Placement.BelowPet(new(100, 100, 240, 240), new(0, 0, 1920, 1040), 180, 62);
    Check(position == new PanelPosition(80, 245, false), "Panel is not below its anchor");
});
Test("bottom edge puts the panel above without covering the pet", () => {
    var position = Placement.BelowPet(new(100, 950, 240, 1030), new(0, 0, 1920, 1040), 180, 62);
    Check(position.Above && position.Y + 62 < 950, "Panel overlaps pet at the bottom edge");
});
Test("negative monitor coordinates and edge clamping work", () => {
    var position = Placement.BelowPet(new(-1900, 100, -1780, 220), new(-1920, 0, 0, 1040), 180, 62);
    Check(position.X == -1920 && position.Y == 225, "Panel was moved to the wrong monitor");
});
Test("physical pixel placement preserves two-times DPI dimensions", () => {
    var position = Placement.BelowPet(new(200, 200, 480, 480), new(0, 0, 3840, 2080), 360, 124, 10);
    Check(position == new PanelPosition(160, 490, false), "DPI-scaled placement is incorrect");
});

string Record(int percent, DateTimeOffset at, string limit = "codex") => JsonSerializer.Serialize(new { timestamp = at, type = "event_msg", payload = new { type = "token_count", rate_limits = new { limit_id = limit, primary = new { used_percent = percent, window_minutes = 300, resets_at = at.AddHours(2).ToUnixTimeSeconds() } } } });
async Task WithHome(Func<string, Task> action)
{
    var home = Path.Combine(Path.GetTempPath(), "codex-pet-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(home, "sessions"));
    try { await action(home); } finally { Directory.Delete(home, true); }
}
AsyncTest("local reader tolerates a partial live write and ignores other quotas", () => WithHome(async home => {
    await File.WriteAllTextAsync(Path.Combine(home, "auth.json"), "PRIVATE_FIXTURE_AUTH");
    await File.WriteAllTextAsync(Path.Combine(home, "sessions", "one.jsonl"), Record(28, now) + "\n" + Record(99, now.AddSeconds(1), "other") + "\n{partial");
    var result = await RecentUsageReader.ReadAsync(home);
    Check(result?.Primary?.Remaining(now) == 72 && result.Source == UsageSource.LocalRecord, "Incorrect local record read");
    Check(!JsonSerializer.Serialize(result).Contains("PRIVATE_FIXTURE_AUTH"), "Unrelated credentials returned");
}));
AsyncTest("record timestamps, not file timestamps, determine the latest quota", () => WithHome(async home => {
    await File.WriteAllTextAsync(Path.Combine(home, "sessions", "one.jsonl"), Record(10, now.AddMinutes(-10)));
    await File.WriteAllTextAsync(Path.Combine(home, "sessions", "two.jsonl"), Record(30, now));
    File.SetLastWriteTimeUtc(Path.Combine(home, "sessions", "one.jsonl"), DateTime.UtcNow.AddHours(1));
    Check((await RecentUsageReader.ReadAsync(home))?.Primary?.Remaining(now) == 70, "File mtime overrode the observed quota time");
}));
AsyncTest("large tails are bounded and recent quota records are still found", () => WithHome(async home => {
    await File.WriteAllTextAsync(Path.Combine(home, "sessions", "large.jsonl"), new string('x', 700000) + "\n" + Record(40, now));
    Check((await RecentUsageReader.ReadAsync(home))?.Primary?.Remaining(now) == 60, "Bounded tail lost its latest event");
}));
AsyncTest("missing and day-old history never invent a current quota", () => WithHome(async home => {
    Check(await RecentUsageReader.ReadAsync(home) is null, "Empty history produced quota data");
    await File.WriteAllTextAsync(Path.Combine(home, "sessions", "old.jsonl"), Record(10, now.AddDays(-2)));
    Check(await RecentUsageReader.ReadAsync(home) is null, "Ancient history was presented as current");
}));

ProcessStartInfo FixtureProcess(string mode)
{
    var executable = Environment.ProcessPath!;
    var start = new ProcessStartInfo(executable);
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--rpc-fixture"); start.ArgumentList.Add(mode);
    return start;
}
AsyncTest("real stdio transport initializes, rejects server actions, and reads quotas", async () => {
    await using var client = await CodexConnection.StartAsync(FixtureProcess("normal"), TimeSpan.FromSeconds(5));
    Check(client.CodexHome == "/fixture", "Initialize response was not consumed");
    Check((await client.ReadUsageAsync())?.Primary?.Remaining(now) == 72, "Real process/JSON-RPC round trip failed");
});
AsyncTest("out-of-order responses are matched to the correct request", async () => {
    await using var client = await CodexConnection.StartAsync(FixtureProcess("reverse"), TimeSpan.FromSeconds(5));
    var first = client.ReadUsageAsync(); var second = client.ReadUsageAsync();
    Check((await first)?.Primary?.Remaining(now) == 80 && (await second)?.Primary?.Remaining(now) == 40, "Response IDs were ignored");
});
AsyncTest("server errors do not leak provider messages or authentication data", async () => {
    await using var client = await CodexConnection.StartAsync(FixtureProcess("error"), TimeSpan.FromSeconds(5));
    try { await client.ReadUsageAsync(); throw new Exception("Server error was swallowed"); }
    catch (IOException ex) { Check(!ex.Message.Contains("PRIVATE_FIXTURE_TOKEN"), "Raw server error leaked"); }
});
AsyncTest("an unresponsive process times out and is disposed", async () => {
    var clock = Stopwatch.StartNew();
    try { await using var client = await CodexConnection.StartAsync(FixtureProcess("hang"), TimeSpan.FromSeconds(1)); throw new Exception("Unresponsive server did not time out"); }
    catch (TimeoutException) { Check(clock.Elapsed < TimeSpan.FromSeconds(10), "Timed-out process was not stopped"); }
});

var failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
}
Console.WriteLine($"Desktop core: {tests.Count - failed} passed, {failed} failed, 0 skipped.");
return failed == 0 ? 0 : 1;

static async Task Fixture(string mode)
{
    var initialized = false; var hello = false;
    var ids = new List<long>(); long waiting = 0;
    void Write(object value) { Console.WriteLine(JsonSerializer.Serialize(value)); }
    object Result(int used) => new { rateLimits = new { limitId = "codex", primary = new { usedPercent = used, windowDurationMins = 300 } } };
    while (Console.ReadLine() is { } line)
    {
        using var doc = JsonDocument.Parse(line); var msg = doc.RootElement;
        if (msg.TryGetProperty("error", out var error) && msg.GetProperty("id").GetInt64() == 777)
        {
            if (error.GetProperty("code").GetInt32() == -32601) Write(new { id = waiting, result = Result(28) });
            continue;
        }
        var method = msg.GetProperty("method").GetString();
        if (method == "initialize")
        {
            if (mode == "hang") continue;
            hello = msg.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString() == "codex_pet_plus";
            Write(new { id = msg.GetProperty("id").GetInt64(), result = new { codexHome = "/fixture", userAgent = "fixture" } });
        }
        else if (method == "initialized") initialized = hello && !msg.TryGetProperty("params", out _);
        else if (method == "account/rateLimits/read" && initialized)
        {
            var id = msg.GetProperty("id").GetInt64();
            if (mode == "error") { Write(new { id, error = new { code = 401, message = "PRIVATE_FIXTURE_TOKEN" } }); continue; }
            if (mode == "reverse") { ids.Add(id); if (ids.Count == 2) { Write(new { id = ids[1], result = Result(60) }); Write(new { id = ids[0], result = Result(20) }); } continue; }
            Console.WriteLine("fixture stdout noise");
            Write(new { method = "account/rateLimits/updated", @params = new { } });
            waiting = id; Write(new { id = 777, method = "item/commandExecution/requestApproval", @params = new { } });
        }
        else Write(new { id = msg.GetProperty("id").GetInt64(), error = new { code = -32601, message = "Only read-only quota methods are allowed" } });
    }
    await Task.CompletedTask;
}
