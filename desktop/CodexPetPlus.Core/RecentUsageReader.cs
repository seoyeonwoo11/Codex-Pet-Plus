using System.Text.Json;
using System.Text;

namespace CodexPetPlus.Core;

public static class RecentUsageReader
{
    public static async Task<UsageSnapshot?> ReadAsync(string codexHome, CancellationToken cancellation = default)
    {
        var files = new List<(string File, DateTime Modified)>();
        var stack = new Stack<(string Directory, int Depth)>();
        stack.Push((Path.Combine(codexHome, "sessions"), 0));
        var entriesRead = 0;
        while (stack.TryPop(out var current) && entriesRead < 10000)
        {
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(current.Directory); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                cancellation.ThrowIfCancellationRequested();
                if (++entriesRead > 10000) break;
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (attributes.HasFlag(FileAttributes.Directory)) { if (current.Depth < 6) stack.Push((entry, current.Depth + 1)); }
                    else if (entry.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) files.Add((entry, File.GetLastWriteTimeUtc(entry)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        UsageSnapshot? latest = null;
        var now = DateTimeOffset.UtcNow;
        foreach (var file in files.OrderByDescending(file => file.Modified).Take(12))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(file.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var end = stream.Length;
                var offset = Math.Max(0, end - 512 * 1024);
                stream.Seek(offset, SeekOrigin.Begin);
                var bytes = new byte[(int)(end - offset)];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(count), cancellation);
                    if (read == 0) break;
                    count += read;
                }
                using var reader = new StringReader(Encoding.UTF8.GetString(bytes, 0, count));
                if (offset > 0) reader.ReadLine();
                while (reader.ReadLine() is { } line)
                {
                    try
                    {
                        using var record = JsonDocument.Parse(line);
                        var snapshot = QuotaParser.FromRecord(record.RootElement);
                        if (snapshot is not null && snapshot.RecordedAt <= now.AddMinutes(1) && snapshot.RecordedAt >= now.AddHours(-24) && (latest is null || latest.RecordedAt < snapshot.RecordedAt)) latest = snapshot;
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return latest;
    }
}
