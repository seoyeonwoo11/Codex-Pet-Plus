using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text;
using System.Buffers.Binary;
using System.Security.Cryptography;
using CodexPetPlus.Core;

if (args.FirstOrDefault() == "--rpc-fixture") { await Fixture(args.ElementAtOrDefault(1) ?? "normal"); return 0; }
if (args.FirstOrDefault() == "--patch-asar") { Console.WriteLine(JsonSerializer.Serialize(AsarModPatcher.Patch(args[1], args[2], File.ReadAllText(args[3])))); return 0; }

var now = DateTimeOffset.UtcNow;
var tests = new List<(string Name, Func<Task> Run)>();
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> run) => tests.Add((name, run));
JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }

Test("UAC launches the installer with a structured runas request", () => {
    var appPath = Path.Combine(Path.GetTempPath(), "앱 경로 & 문자", "ChatGPT.exe");
    var request = new ElevationRequest("patch", 123, appPath, Guid.NewGuid());
    var start = request.StartInfo(Path.Combine(Path.GetTempPath(), "도구 경로", "CodexPetPlus.exe"));
    Check(start.UseShellExecute && start.Verb == "runas", "Windows UAC was not requested");
    Check(start.Arguments == "" && start.ArgumentList[5] == appPath, "Path became shell text");
    Check(ElevationRequest.Parse(start.ArgumentList.ToArray()) == request, "UAC arguments lost the selected app");
});
Test("UAC helpers reject unknown actions and malformed handoff arguments", () => {
    var path = Path.Combine(Path.GetTempPath(), "ChatGPT.exe"); var id = Guid.NewGuid().ToString("N");
    foreach (var values in new[] {
        new[] { "--admin-task", "launch", "--parent", "123", "--app", path, "--request", id },
        new[] { "--admin-task", "patch", "--parent", "0", "--app", path, "--request", id },
        new[] { "--admin-task", "patch", "--parent", "123", "--app", "relative.exe", "--request", id },
        new[] { "--admin-task", "patch", "--parent", "123", "--app", path, "--request", "invalid" }
    }) { try { ElevationRequest.Parse(values); throw new Exception("Invalid admin request accepted"); } catch (ArgumentException) { } }
});
Test("UAC helpers accept only patch, restore, or write checks", () => {
    foreach (var operation in new[] { "patch", "restore", "check" })
    {
        var request = new ElevationRequest(operation, 123, Path.Combine(Path.GetTempPath(), "ChatGPT.exe"), Guid.NewGuid());
        Check(ElevationRequest.Parse(request.StartInfo(Environment.ProcessPath!).ArgumentList.ToArray()).Operation == operation, "Supported admin action rejected");
    }
});
if (OperatingSystem.IsWindows()) Test("Windows helper resolves the original process profile and rejects another image", () => {
    if (!OperatingSystem.IsWindows()) return;
    var actual = CodexPetPlus.Windows.WindowsTokenProfile.LocalAppDataOf(Environment.ProcessId, Environment.ProcessPath!);
    Check(Path.GetFullPath(actual).Equals(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), StringComparison.OrdinalIgnoreCase), "Elevation would change the backup owner profile");
    try { CodexPetPlus.Windows.WindowsTokenProfile.LocalAppDataOf(Environment.ProcessId, Path.Combine(Path.GetTempPath(), "not-this-installer.exe")); throw new Exception("Another caller image accepted"); } catch (IOException) { }
});

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

PetWindow Pet(int handle, string title = "", uint style = 0, uint extended = 0x00080008) => new((nint)handle, 123, title, new(100, 100, 260, 260), 96, "Chrome_WidgetWin_1", style, extended);
Test("untitled and custom-named floating pets connect automatically", () => {
    Check(PetDiscovery.Find([Pet(1)]).Window?.Handle == 1, "Untitled pet needs manual selection");
    Check(PetDiscovery.Find([Pet(2, "모찌 고양이")]).Window?.Handle == 2, "Custom name was rejected");
    Check(PetDiscovery.Find([Pet(3, "ChatGPT")]).Window?.Handle == 3, "ChatGPT host title was rejected");
});
Test("a pet name outranks generic floating windows without confusing project titles", () => {
    Check(PetDiscovery.Find([Pet(1, "Codex Pet"), Pet(2)]).Window?.Handle == 1, "Explicit pet was not preferred");
    Check(PetDiscovery.Find([Pet(3, "Codex Pet Plus project", 0x00C40000), Pet(4, "carpet", extended: 8)]).Window is null, "A normal window or title substring became a pet");
    Check(PetDiscovery.Find([Pet(5, "Codex Pet") with { ClassName = "#32770" }]).Window is null, "Dialog became a pet");
});
Test("ambiguous custom windows wait until a saved unique selection is available", () => {
    var windows = new[] { Pet(1, "고양이"), Pet(2, "강아지") };
    Check(PetDiscovery.Find(windows) is { Window: null, CandidateCount: 2 }, "Ambiguous windows were guessed");
    Check(PetDiscovery.Find(windows, "강아지", "Chrome_WidgetWin_1").Window?.Handle == 2, "Saved custom selection was lost");
    Check(PetDiscovery.Find([Pet(3), Pet(4)], "", "Chrome_WidgetWin_1").Window is null, "Identical window signatures were guessed");
});
Test("pet discovery uses logical size on high DPI displays", () => {
    var highDpi = Pet(1) with { Bounds = new(0, 0, 640, 640), Dpi = 192 };
    Check(PetDiscovery.Find([highDpi]).Window is not null, "High DPI pet was considered too large");
    Check(PetDiscovery.Find([highDpi with { Dpi = 96 }]).Window is null, "Large generic app window became a pet");
});

(JsonElement Header, long Offset) AsarHeader(string file)
{
    using var input = File.OpenRead(file); var prefix = new byte[16]; input.ReadExactly(prefix);
    var bytes = new byte[BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(12))]; input.ReadExactly(bytes);
    return (Json(Encoding.UTF8.GetString(bytes)), 8L + BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(4)));
}
string AsarText(string file, string name)
{
    var (header, offset) = AsarHeader(file); var entry = header;
    foreach (var part in name.Split('/')) entry = entry.GetProperty("files").GetProperty(part);
    using var input = File.OpenRead(file); input.Position = offset + long.Parse(entry.GetProperty("offset").GetString()!);
    var bytes = new byte[entry.GetProperty("size").GetInt32()]; input.ReadExactly(bytes); return Encoding.UTF8.GetString(bytes);
}
string FixtureArchive(string kind) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "app-" + kind + ".asar");
AsyncTest("app version comes from ASAR rather than browser EXE or MSIX folder versions", () => WithHome(home => {
    var app = Path.Combine(home, "OpenAI.Codex_26.1023.6621.0_x64__fixture", "app");
    Directory.CreateDirectory(Path.Combine(app, "resources"));
    var executable = Path.Combine(app, "ChatGPT.exe");
    File.WriteAllText(executable, "Browser engine version fixture: 156.0.8078.48");
    File.Copy(FixtureArchive("cjs"), Path.Combine(app, "resources", "app.asar"));
    var original = File.ReadAllBytes(Path.Combine(app, "resources", "app.asar"));
    Check(DesktopAppVersion.ReadFromExecutable(executable) == "26.1023.51421", "Executable or MSIX version replaced the real app version");
    Check(File.ReadAllBytes(Path.Combine(app, "resources", "app.asar")).SequenceEqual(original), "Version detection changed the original archive");
    return Task.CompletedTask;
}));
Test("app version normalizes equivalent build forms and rejects incomplete versions", () => {
    foreach (var value in new[] { "26.1023.51421", "26.1023.51421.0", " v26.1023.51421+build.123 " })
        Check(DesktopAppVersion.Normalize(value) == "26.1023.51421", "Equivalent app versions did not match");
    Check(DesktopAppVersion.Normalize("26.1023.51421.1") != "26.1023.51421", "A different revision was accepted");
    Check(DesktopAppVersion.Normalize("156.0.8078.48") != "26.1023.51421", "Runtime number became the supported app version");
    foreach (var value in new[] { "", "latest", "26.1023" })
    {
        try { DesktopAppVersion.Normalize(value); throw new Exception("Incomplete version accepted"); } catch (IOException) { }
    }
});
AsyncTest("missing package version stays unknown despite a versioned install folder", () => WithHome(home => {
    var app = Path.Combine(home, "OpenAI.Codex_26.1023.51421.0", "app");
    Directory.CreateDirectory(Path.Combine(app, "resources"));
    File.Copy(FixtureArchive("no-version"), Path.Combine(app, "resources", "app.asar"));
    try { DesktopAppVersion.ReadFromExecutable(Path.Combine(app, "ChatGPT.exe")); throw new Exception("Unknown package version was guessed"); }
    catch (IOException) { }
    return Task.CompletedTask;
}));
AsyncTest("official ASAR fixture gains a CJS runtime without changing source or unpacked entries", () => WithHome(home => {
    var source = FixtureArchive("cjs"); var before = SHA256.HashData(File.ReadAllBytes(source)); var output = Path.Combine(home, "app.asar");
    var result = AsarModPatcher.Patch(source, output, "module.exports = 'our runtime';");
    Check(AsarText(output, ".vite/build/main.cjs").Contains("require(\"../../codex-pet-plus-runtime.cjs\")"), "Wrong relative CJS loader");
    Check(AsarText(output, "codex-pet-plus-runtime.cjs") == "module.exports = 'our runtime';", "Runtime was not packed");
    Check(AsarText(output, "webview/assets/avatar-overlay-page-fixture.js").Contains("original pet renderer"), "Original pet renderer was modified");
    Check(AsarHeader(output).Header.GetProperty("files").GetProperty("native.node").GetProperty("unpacked").GetBoolean(), "Native unpacked entry was changed");
    Check(result.OriginalHeaderHash != result.PatchedHeaderHash && SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(before), "Source archive was modified");
    return Task.CompletedTask;
}));
AsyncTest("ESM ASAR entry uses a static import and valid per-file integrity", () => WithHome(home => {
    var output = Path.Combine(home, "app.asar"); AsarModPatcher.Patch(FixtureArchive("esm"), output, "module.exports = 42;");
    Check(AsarText(output, ".vite/build/main.mjs").Contains("import \"../../codex-pet-plus-runtime.cjs\";"), "ESM was patched with require");
    var entry = AsarHeader(output).Header.GetProperty("files").GetProperty("codex-pet-plus-runtime.cjs");
    var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(AsarText(output, "codex-pet-plus-runtime.cjs"))));
    Check(entry.GetProperty("integrity").GetProperty("hash").GetString() == expected, "Per-file integrity does not match");
    return Task.CompletedTask;
}));
AsyncTest("an app entry at the archive root loads the sibling runtime", () => WithHome(home => {
    var output = Path.Combine(home, "app.asar"); AsarModPatcher.Patch(FixtureArchive("root"), output, "module.exports = 42;");
    Check(AsarText(output, "main.cjs").Contains("require(\"./codex-pet-plus-runtime.cjs\")"), "Root entry runtime path is invalid");
    return Task.CompletedTask;
}));
AsyncTest("ASAR rejects repeat patches, truncated data, and writes to the original", () => WithHome(async home => {
    var output = Path.Combine(home, "app.asar"); AsarModPatcher.Patch(FixtureArchive("cjs"), output, "module.exports = 42;");
    foreach (var (source, target) in new[] { (output, Path.Combine(home, "again.asar")), (output, output) })
    {
        try { AsarModPatcher.Patch(source, target, "runtime"); throw new Exception("Unsafe patch was accepted"); } catch (IOException) { }
    }
    var bad = Path.Combine(home, "bad.asar"); await File.WriteAllBytesAsync(bad, File.ReadAllBytes(FixtureArchive("cjs"))[..30]);
    try { AsarModPatcher.Patch(bad, Path.Combine(home, "bad-output.asar"), "runtime"); throw new Exception("Truncated ASAR accepted"); } catch (IOException) { }
    Check(!File.Exists(Path.Combine(home, "bad-output.asar")), "Failed patch left a published archive");
}));
AsyncTest("executable metadata accepts only the verified original ASAR hash", () => WithHome(home => {
    var file = Path.Combine(home, "ChatGPT.exe"); var oldHash = new string('a', 64); var nextHash = new string('b', 64);
    var original = Encoding.ASCII.GetBytes("MZ fixture {\"file\":\"resources\\\\app.asar\",\"alg\":\"SHA256\",\"value\":\"" + oldHash + "\"} end");
    File.WriteAllBytes(file, original);
    try { AsarModPatcher.UpdateExecutableIntegrity(file, new string('c', 64), nextHash); throw new Exception("Mismatched integrity accepted"); } catch (IOException) { }
    Check(File.ReadAllBytes(file).SequenceEqual(original), "Rejected executable was modified");
    AsarModPatcher.UpdateExecutableIntegrity(file, oldHash, nextHash);
    Check(Encoding.ASCII.GetString(File.ReadAllBytes(file)) == Encoding.ASCII.GetString(original).Replace(oldHash, nextHash), "Executable bytes outside the integrity value changed");
    return Task.CompletedTask;
}));

async Task WithDirectApp(Func<string, string, string, byte[], byte[], Task> action)
{
    await WithHome(async home => {
        var directory = Path.Combine(home, "installed-app"); Directory.CreateDirectory(Path.Combine(directory, "resources"));
        var archive = Path.Combine(directory, "resources", "app.asar"); File.Copy(FixtureArchive("cjs"), archive);
        var originalArchive = File.ReadAllBytes(archive);
        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(originalArchive.AsSpan(12));
        var headerHash = Convert.ToHexString(SHA256.HashData(originalArchive.AsSpan(16, headerLength)));
        // Preserve uppercase integrity values and changes that cross a 64 KiB boundary.
        var marker = "{\"file\":\"resources\\\\app.asar\",\"alg\":\"SHA256\",\"value\":\"" + headerHash + "\"}";
        var originalExe = Encoding.ASCII.GetBytes("MZ" + new string('x', 65464) + marker + "UNCHANGED_CERTIFICATE_FIXTURE");
        var executable = Path.Combine(directory, "ChatGPT.exe"); File.WriteAllBytes(executable, originalExe);
        await File.WriteAllBytesAsync(Path.Combine(directory, "unchanged.dll"), RandomNumberGenerator.GetBytes(512 * 1024));
        Directory.CreateDirectory(archive + ".unpacked"); File.WriteAllText(Path.Combine(archive + ".unpacked", "native.node"), "UNCHANGED_NATIVE_FIXTURE");
        await action(executable, archive, Path.Combine(home, "recovery.zip"), originalExe, originalArchive);
    });
}
AsyncTest("direct patch preparation backs up only changed information and leaves originals intact", () => WithDirectApp((exe, archive, backup, originalExe, originalArchive) => {
    using var plan = DirectModPatch.Prepare(exe, backup, "module.exports = 'direct runtime';");
    Check(File.ReadAllBytes(exe).SequenceEqual(originalExe) && File.ReadAllBytes(archive).SequenceEqual(originalArchive), "Preparation modified the installed app");
    using var zip = System.IO.Compression.ZipFile.OpenRead(backup);
    Check(zip.Entries.Select(entry => entry.FullName).Order().SequenceEqual(new[] { "asar-header.bin", "asar-main.bin", "metadata.json" }), "Backup contains whole app files");
    Check(new FileInfo(backup).Length < 8192, "Small changed information expanded into an executable/app copy");
    Check(Path.GetDirectoryName(plan.StagedExecutable) == Path.GetDirectoryName(exe) && Path.GetDirectoryName(plan.StagedArchive) == Path.GetDirectoryName(archive), "Replacement files are on another volume");
    return Task.CompletedTask;
}));
AsyncTest("direct patch and restore reproduce both original files byte for byte", () => WithDirectApp((exe, archive, backup, originalExe, originalArchive) => {
    using var plan = DirectModPatch.Prepare(exe, backup, "module.exports = 'direct runtime';"); plan.Commit();
    Check(!File.ReadAllBytes(exe).SequenceEqual(originalExe), "Executable integrity was not updated");
    Check(AsarText(archive, "codex-pet-plus-runtime.cjs") == "module.exports = 'direct runtime';", "Mod runtime is missing");
    DirectModPatch.Restore(exe, backup); DirectModPatch.Restore(exe, backup);
    Check(File.ReadAllBytes(exe).SequenceEqual(originalExe) && File.ReadAllBytes(archive).SequenceEqual(originalArchive), "Original signatures or archive bytes were not restored");
    Check(File.ReadAllText(Path.Combine(archive + ".unpacked", "native.node")) == "UNCHANGED_NATIVE_FIXTURE", "Unpacked native resources changed");
    Check(DirectModPatch.IsOriginal(exe, backup), "Restored originals were not recognized");
    return Task.CompletedTask;
}));
AsyncTest("interruption between the two replacements can restore a mixed file state", () => WithDirectApp((exe, archive, backup, originalExe, originalArchive) => {
    using var plan = DirectModPatch.Prepare(exe, backup, "module.exports = 1;");
    File.Replace(plan.StagedArchive, archive, null);
    DirectModPatch.Restore(exe, backup);
    Check(File.ReadAllBytes(exe).SequenceEqual(originalExe) && File.ReadAllBytes(archive).SequenceEqual(originalArchive), "Interrupted installation could not recover");
    return Task.CompletedTask;
}));
AsyncTest("restore refuses to overwrite an app update or another mod", () => WithDirectApp((exe, archive, backup, _, _) => {
    using var plan = DirectModPatch.Prepare(exe, backup, "module.exports = 1;"); plan.Commit();
    File.AppendAllText(archive, "EXTERNAL_UPDATE_FIXTURE");
    var beforeExe = File.ReadAllBytes(exe); var beforeArchive = File.ReadAllBytes(archive);
    try { DirectModPatch.Restore(exe, backup); throw new Exception("External update overwritten"); } catch (IOException) { }
    Check(File.ReadAllBytes(exe).SequenceEqual(beforeExe) && File.ReadAllBytes(archive).SequenceEqual(beforeArchive), "A rejected restore changed files");
    return Task.CompletedTask;
}));
AsyncTest("source or staged file changes abort direct patch before any replacement", async () => {
    foreach (var changeStaged in new[] { false, true }) await WithDirectApp((exe, archive, backup, _, originalArchive) => {
        using var plan = DirectModPatch.Prepare(exe, backup, "module.exports = 1;");
        File.AppendAllText(changeStaged ? plan.StagedExecutable : exe, "CHANGED_FILE_FIXTURE");
        var before = File.ReadAllBytes(exe);
        try { plan.Commit(); throw new Exception("Changed files accepted"); } catch (IOException) { }
        Check(File.ReadAllBytes(exe).SequenceEqual(before) && File.ReadAllBytes(archive).SequenceEqual(originalArchive), "Aborted patch modified the app");
        return Task.CompletedTask;
    });
});
AsyncTest("corrupt recovery data never changes installed files", () => WithDirectApp((exe, archive, backup, _, _) => {
    using var plan = DirectModPatch.Prepare(exe, backup, "module.exports = 1;"); plan.Commit();
    File.WriteAllText(backup, "CORRUPT_BACKUP_FIXTURE");
    var beforeExe = File.ReadAllBytes(exe); var beforeArchive = File.ReadAllBytes(archive);
    try { DirectModPatch.Restore(exe, backup); throw new Exception("Corrupt backup accepted"); } catch (IOException) { }
    Check(File.ReadAllBytes(exe).SequenceEqual(beforeExe) && File.ReadAllBytes(archive).SequenceEqual(beforeArchive), "Corrupt backup changed installed files");
    return Task.CompletedTask;
}));

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
