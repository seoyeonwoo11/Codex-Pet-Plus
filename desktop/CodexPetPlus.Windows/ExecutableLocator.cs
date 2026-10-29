using System.Diagnostics;

namespace CodexPetPlus.Windows;

internal static class ExecutableLocator
{
    public static async Task<string?> FindAsync(string preferred, CancellationToken cancellation)
    {
        var candidates = new List<string>();
        var guiPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcessesByName("Codex").Concat(Process.GetProcessesByName("ChatGPT")))
        {
            using (process)
            {
                try
                {
                    var exe = process.MainModule?.FileName;
                    if (exe is null) continue;
                    if (process.MainWindowHandle != 0 || IsDesktopLauncher(exe)) guiPaths.Add(exe);
                    var directory = Path.GetDirectoryName(exe)!;
                    candidates.Add(Path.Combine(directory, "resources", "codex.exe"));
                    candidates.Add(Path.Combine(directory, "resources", "bin", "codex.exe"));
                    try
                    {
                        candidates.AddRange(Directory.EnumerateFiles(directory, "codex.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(32));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        if (!string.IsNullOrWhiteSpace(preferred)) candidates.Insert(0, preferred);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { if (!string.IsNullOrWhiteSpace(directory)) candidates.Add(Path.Combine(directory.Trim('"'), "codex.exe")); }
            catch (ArgumentException) { }
        }
        foreach (var file in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            if (guiPaths.Contains(file) || IsDesktopLauncher(file) || !File.Exists(file)) continue;
            if (await IsCliAsync(file, cancellation)) return file;
        }
        return null;
    }

    private static bool IsDesktopLauncher(string file)
    {
        var directory = Path.GetDirectoryName(file);
        return directory is not null && (File.Exists(Path.Combine(directory, "resources", "app.asar")) || Directory.Exists(Path.Combine(directory, "resources", "app")));
    }

    private static async Task<bool> IsCliAsync(string file, CancellationToken cancellation)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file) { ArgumentList = { "--version" }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (process is null) return false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                var text = (await output).Trim();
                await errors;
                return process.ExitCode == 0 && text.StartsWith("codex-cli ", StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } return false; }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return false; }
    }
}
