using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace CodexPetPlus.Core;

// A dedicated stdio child. It cannot start turns or modify the desktop app's tasks.
public sealed class CodexConnection : IAsyncDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly TimeSpan timeout;
    private readonly Task reader;
    private readonly Task errors;
    private long sequence;
    private int disposed;
    public string? CodexHome { get; private set; }

    private CodexConnection(Process process, TimeSpan timeout)
    {
        this.process = process; this.timeout = timeout;
        reader = Task.Run(ReadLoop);
        errors = Task.Run(DrainErrors);
    }

    public static async Task<CodexConnection> StartAsync(string executable, CancellationToken cancellation = default) =>
        await StartAsync(new ProcessStartInfo(executable) { ArgumentList = { "app-server" } }, TimeSpan.FromSeconds(15), cancellation);

    public static async Task<CodexConnection> StartAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellation = default)
    {
        start.UseShellExecute = false; start.CreateNoWindow = true;
        start.RedirectStandardInput = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        var process = Process.Start(start) ?? throw new IOException("Codex 연결 도구를 실행하지 못했습니다.");
        var connection = new CodexConnection(process, timeout);
        try
        {
            var initialized = await connection.Request("initialize", new { clientInfo = new { name = "codex_pet_plus", title = "Codex Pet Plus", version = "0.5.0" }, capabilities = new { experimentalApi = false } }, cancellation);
            if (initialized.TryGetProperty("codexHome", out var home) && home.ValueKind == JsonValueKind.String) connection.CodexHome = home.GetString();
            await connection.Write(new { method = "initialized" }, cancellation);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<UsageSnapshot?> ReadUsageAsync(CancellationToken cancellation = default) =>
        QuotaParser.FromAppServer(await Request("account/rateLimits/read", new { }, cancellation), DateTimeOffset.UtcNow);

    private async Task<JsonElement> Request(string method, object parameters, CancellationToken cancellation)
    {
        var id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        using var clock = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        clock.CancelAfter(timeout);
        try
        {
            await Write(new { id, method, @params = parameters }, clock.Token);
            return await completion.Task.WaitAsync(clock.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && !lifetime.IsCancellationRequested) { throw new TimeoutException("Codex의 한도 응답을 기다리는 시간이 초과되었습니다."); }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task Write(object value, CancellationToken cancellation)
    {
        await writes.WaitAsync(cancellation);
        try { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), cancellation); await process.StandardInput.FlushAsync(cancellation); }
        finally { writes.Release(); }
    }

    private async Task ReadLoop()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync(lifetime.Token) is { } line)
            {
                if (line.Length > 4 * 1024 * 1024) throw new IOException("Codex 응답 크기가 제한을 초과했습니다.");
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                using (doc)
                {
                    var message = doc.RootElement;
                    if (message.ValueKind != JsonValueKind.Object) continue;
                    if (!message.TryGetProperty("id", out var identity)) continue;
                    if (message.TryGetProperty("method", out _))
                    {
                        await Write(new { id = identity.Clone(), error = new { code = -32601, message = "This read-only companion does not handle server requests." } }, lifetime.Token);
                        continue;
                    }
                    if (identity.ValueKind != JsonValueKind.Number || !identity.TryGetInt64(out var id) || !pending.TryRemove(id, out var completion)) continue;
                    if (message.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
                    else completion.TrySetException(new IOException("Codex에서 계정 한도를 조회하지 못했습니다. 앱의 로그인 상태를 확인해 주세요."));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { foreach (var item in pending.Values) item.TrySetException(new IOException("Codex 로컬 연결이 종료되었습니다.")); }
    }

    private async Task DrainErrors()
    {
        try { while (await process.StandardError.ReadLineAsync(lifetime.Token) is not null) { } }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        try
        {
            process.StandardInput.Close();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        catch (InvalidOperationException) { }
        await Task.WhenAll(reader, errors);
        process.Dispose(); lifetime.Dispose(); writes.Dispose();
    }
}
