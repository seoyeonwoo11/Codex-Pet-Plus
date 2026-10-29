using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodexPetPlus.Core;

namespace CodexPetPlus.Windows;

internal sealed record ModInstallation(string Executable, string SourceExecutable, string Version, DateTimeOffset InstalledAt);
internal sealed record ModRuntimeStatus(bool Running, int ProcessId, string AppVersion, int PetWindows, bool QuotaAvailable, DateTimeOffset UpdatedAt);

internal static class DesktopModInstaller
{
    public const string TargetVersion = "26.1023.51421";
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPetPlus", "mod");
    private static string Manifest => Path.Combine(Root, "installation.json");

    public static ModInstallation? Current()
    {
        try
        {
            var value = JsonSerializer.Deserialize<ModInstallation>(File.ReadAllText(Manifest));
            return value is not null && IsInsideRoot(value.Executable) && File.Exists(value.Executable) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    public static string VersionOf(string executable) => DesktopAppVersion.ReadFromExecutable(executable);

    public static string Status()
    {
        var installed = Current(); if (installed is null) return "먼저 모드를 설치해 주세요.";
        try
        {
            var file = Path.Combine(Path.GetDirectoryName(installed.Executable)!, "resources", "pet-plus-status.json");
            if (!File.Exists(file) || new FileInfo(file).Length > 8192) return "모드 실행 기록이 없습니다. ‘모드 앱 실행’으로 앱을 열어 주세요.";
            var status = JsonSerializer.Deserialize<ModRuntimeStatus>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (status is null || !status.Running || status.UpdatedAt < DateTimeOffset.UtcNow.AddMinutes(-3)) return "모드 앱이 종료됐거나 상태가 오래됐습니다. 앱 실행 상태를 확인해 주세요.";
            using var process = Process.GetProcessById(status.ProcessId);
            if (!string.Equals(process.MainModule?.FileName, installed.Executable, StringComparison.OrdinalIgnoreCase)) return "현재 모드 앱의 실행 상태를 확인하지 못했습니다.";
            return status.PetWindows == 0 ? "모드는 실행 중이며 펫 화면을 기다립니다. 앱에서 원하는 펫을 켜 주세요."
                : $"펫 {status.PetWindows}개에 연결됐습니다. " + (status.QuotaAvailable ? "계정 한도를 조회했습니다." : "계정 한도를 조회하지 못했습니다. Codex 로그인 상태를 확인해 주세요.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return "모드 상태를 읽지 못했습니다. 앱 실행 상태를 확인해 주세요."; }
    }

    public static async Task<string?> FindSourceAsync()
    {
        foreach (var process in Process.GetProcessesByName("ChatGPT").Concat(Process.GetProcessesByName("Codex")))
        {
            using (process)
            {
                try { if (process.MainModule?.FileName is { } file && IsSource(file)) return file; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        try
        {
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$ErrorActionPreference='Stop'; Get-AppxPackage -Name OpenAI.Codex | Select-Object -ExpandProperty InstallLocation");
            using var query = Process.Start(start);
            if (query is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var output = query.StandardOutput.ReadToEndAsync(timeout.Token); var errors = query.StandardError.ReadToEndAsync(timeout.Token);
                try
                {
                    await query.WaitForExitAsync(timeout.Token); await errors;
                    foreach (var directory in (await output).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    foreach (var name in new[] { "ChatGPT.exe", "Codex.exe" })
                    foreach (var subdirectory in new[] { "app", "" })
                    {
                        var file = Path.Combine(directory.Trim(), subdirectory, name);
                        if (IsSource(file)) return file;
                    }
                }
                catch (OperationCanceledException) { try { query.Kill(); } catch (InvalidOperationException) { } }
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { }
        return null;
    }

    public static async Task<ModInstallation> InstallAsync(string executable, IProgress<string> progress, CancellationToken cancellation = default)
    {
        executable = Path.GetFullPath(executable);
        if (!IsSource(executable)) throw new IOException("공식 앱 설치 폴더의 ChatGPT.exe 또는 Codex.exe를 선택해 주세요.");
        var version = VersionOf(executable);
        if (version != TargetVersion)
            throw new IOException($"이 설치기는 {TargetVersion}용입니다. 선택한 앱 버전은 {version}입니다.");
        var source = Path.GetDirectoryName(executable)!;
        if (Path.GetFullPath(Root).StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("모드 저장 폴더와 분리된 공식 앱 설치 폴더를 선택해 주세요.");
        Directory.CreateDirectory(Root);
        var staging = Path.Combine(Root, ".staging-" + Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(Root, "app-" + TargetVersion + "-" + Guid.NewGuid().ToString("N")[..8]);
        var previous = Current();
        var committed = false;
        try
        {
            progress.Report("앱 복사본을 만드는 중입니다. 앱 크기에 따라 시간이 걸릴 수 있습니다.");
            await CopyApp(source, staging, cancellation);
            cancellation.ThrowIfCancellationRequested();
            progress.Report("복사본에 펫 한도 모드를 설치하고 패키지 무결성을 확인합니다.");
            var copiedExe = Path.Combine(staging, Path.GetFileName(executable));
            var archive = Path.Combine(staging, "resources", "app.asar");
            var patchedArchive = archive + ".patched";
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexPetPlus.Mod.Runtime") ?? throw new IOException("모드 파일을 찾지 못했습니다.");
            using var reader = new StreamReader(resource);
            var runtime = await reader.ReadToEndAsync(cancellation);
            await Task.Run(() =>
            {
                var result = AsarModPatcher.Patch(archive, patchedArchive, runtime);
                AsarModPatcher.UpdateExecutableIntegrity(copiedExe, result.OriginalHeaderHash, result.PatchedHeaderHash);
                File.Move(patchedArchive, archive, true);
            }, cancellation);
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            var installed = new ModInstallation(Path.Combine(destination, Path.GetFileName(executable)), executable, TargetVersion, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(Manifest + ".tmp", JsonSerializer.Serialize(installed, new JsonSerializerOptions { WriteIndented = true }), cancellation);
            File.Move(Manifest + ".tmp", Manifest, true);
            committed = true;
            TryCreateShortcut(installed.Executable);
            if (previous is not null) TryRemoveCopy(Path.GetDirectoryName(previous.Executable)!);
            progress.Report("설치됐습니다. 앱을 완전히 종료한 뒤 ‘모드 앱 실행’을 누르세요.");
            return installed;
        }
        catch
        {
            TryRemoveCopy(staging); if (!committed) TryRemoveCopy(destination); throw;
        }
    }

    public static void Launch()
    {
        var installed = Current() ?? throw new IOException("먼저 모드를 설치해 주세요.");
        foreach (var process in Process.GetProcessesByName("ChatGPT").Concat(Process.GetProcessesByName("Codex")))
        {
            using (process)
            {
                try
                {
                    var file = process.MainModule?.FileName;
                    if (file is not null && File.Exists(Path.Combine(Path.GetDirectoryName(file)!, "resources", "app.asar")))
                        throw new IOException("ChatGPT/Codex 앱을 트레이에서도 완전히 종료한 뒤 모드 앱을 실행해 주세요. 진행 중인 작업은 먼저 완료해 주세요.");
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        Process.Start(new ProcessStartInfo(installed.Executable) { WorkingDirectory = Path.GetDirectoryName(installed.Executable), UseShellExecute = true });
    }

    private static bool IsInsideRoot(string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool IsSource(string file) => File.Exists(file) && !IsInsideRoot(file)
        && (Path.GetFileName(file).Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file).Equals("Codex.exe", StringComparison.OrdinalIgnoreCase))
        && File.Exists(Path.Combine(Path.GetDirectoryName(file)!, "resources", "app.asar"));

    private static async Task CopyApp(string source, string destination, CancellationToken cancellation)
    {
        var pending = new Stack<(string Source, string Destination)>(); pending.Push((source, destination));
        var userDataNames = new HashSet<string>(["User Data", "user-data", "userdata", "data", "profiles", "logs", ".codex", ".git", "auth.json", "Cookies"], StringComparer.OrdinalIgnoreCase);
        var count = 0; long bytes = 0;
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested(); Directory.CreateDirectory(current.Destination);
            if ((File.GetAttributes(current.Source) & FileAttributes.ReparsePoint) != 0) throw new IOException("앱 폴더의 링크를 복사할 수 없습니다.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(current.Source))
            {
                cancellation.ThrowIfCancellationRequested();
                if (current.Source == source && userDataNames.Contains(Path.GetFileName(entry))) continue;
                if (++count > 100000) throw new IOException("앱에 파일이 너무 많습니다.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("앱 폴더의 링크를 복사할 수 없습니다.");
                var target = Path.Combine(current.Destination, Path.GetFileName(entry));
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push((entry, target)); continue; }
                using var input = File.OpenRead(entry);
                bytes = checked(bytes + input.Length); if (bytes > 12L * 1024 * 1024 * 1024) throw new IOException("앱 복사본 크기가 제한을 초과했습니다.");
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
                await input.CopyToAsync(output, cancellation);
            }
        }
    }

    private static void TryRemoveCopy(string directory)
    {
        try
        {
            if (!IsInsideRoot(directory) || !Directory.Exists(directory)) return;
            // A running older copy may still be serving user tasks and loading its resources.
            foreach (var process in Process.GetProcessesByName("ChatGPT").Concat(Process.GetProcessesByName("Codex")))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName is { } file && file.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return; }
                }
            }
            Directory.Delete(directory, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryCreateShortcut(string executable)
    {
        object? shell = null, shortcut = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell"); if (type is null) return;
            shell = Activator.CreateInstance(type); if (shell is null) return;
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Codex Pet Plus.lnk");
            shortcut = ((dynamic)shell).CreateShortcut(file);
            ((dynamic)shortcut).TargetPath = executable;
            ((dynamic)shortcut).WorkingDirectory = Path.GetDirectoryName(executable);
            ((dynamic)shortcut).Description = "Codex Pet Plus · 펫 아래 남은 한도";
            ((dynamic)shortcut).Save();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
