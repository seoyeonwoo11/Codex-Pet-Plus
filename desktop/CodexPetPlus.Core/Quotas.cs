using System.Text.Json;

namespace CodexPetPlus.Core;

public enum UsageSource { AppServer, LocalRecord, Cached }

public sealed record QuotaWindow(double UsedPercent, long? Minutes, DateTimeOffset? ResetsAt)
{
    public double? Remaining(DateTimeOffset now) => !double.IsFinite(UsedPercent) || UsedPercent is < 0 or > 100 || ResetsAt <= now ? null : Math.Round(100 - UsedPercent, 1);
    public string Label(string fallback) => Minutes switch { 10080 => "주간", >= 60 when Minutes % 60 == 0 => $"{Minutes / 60}h", > 0 => $"{Minutes}분", _ => fallback };
}

public sealed record UsageSnapshot(QuotaWindow? Primary, QuotaWindow? Secondary, DateTimeOffset RecordedAt, UsageSource Source);

public static class QuotaParser
{
    public static UsageSnapshot? FromAppServer(JsonElement result, DateTimeOffset now)
    {
        if (result.ValueKind != JsonValueKind.Object) return null;
        JsonElement rate;
        if (result.TryGetProperty("rateLimitsByLimitId", out var all) && all.ValueKind == JsonValueKind.Object && all.TryGetProperty("codex", out rate)) { }
        else if (!result.TryGetProperty("rateLimits", out rate)) return null;
        if (rate.ValueKind != JsonValueKind.Object || IsOtherLimit(rate, "limitId")) return null;
        return Snapshot(rate, now, UsageSource.AppServer, false);
    }

    public static UsageSnapshot? FromRecord(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != "event_msg" ||
            !record.TryGetProperty("timestamp", out var time) || time.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(time.GetString(), out var at) ||
            !record.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "token_count" ||
            !payload.TryGetProperty("rate_limits", out var rate) || rate.ValueKind != JsonValueKind.Object || IsOtherLimit(rate, "limit_id")) return null;
        return Snapshot(rate, at, UsageSource.LocalRecord, true);
    }

    private static bool IsOtherLimit(JsonElement rate, string key) => rate.TryGetProperty(key, out var id) && id.ValueKind == JsonValueKind.String && id.GetString() != "codex";
    private static UsageSnapshot? Snapshot(JsonElement rate, DateTimeOffset at, UsageSource source, bool snake)
    {
        var primary = Window(rate, "primary", snake);
        var secondary = Window(rate, "secondary", snake);
        return primary is null && secondary is null ? null : new(primary, secondary, at, source);
    }
    private static QuotaWindow? Window(JsonElement rate, string key, bool snake)
    {
        if (!rate.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty(snake ? "used_percent" : "usedPercent", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) || !double.IsFinite(percent) || percent is < 0 or > 100) return null;
        long? minutes = window.TryGetProperty(snake ? "window_minutes" : "windowDurationMins", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt64(out var m) && m > 0 ? m : null;
        DateTimeOffset? resets = null;
        if (window.TryGetProperty(snake ? "resets_at" : "resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out var seconds))
        {
            try { resets = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { }
        }
        return new(percent, minutes, resets);
    }
}
