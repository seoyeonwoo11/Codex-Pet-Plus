using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodexPetPlus.Core;

namespace CodexPetPlus.Windows;

internal sealed record ModInstallation(string Executable, string SourceExecutable, string Version, DateTimeOffset InstalledAt,
    string Mode = "copy", string? BackupFile = null, string State = "installed");
internal sealed record ModRuntimeStatus(bool Running, int ProcessId, string AppVersion, int PetWindows, bool QuotaAvailable, DateTimeOffset UpdatedAt);

internal static class DesktopModInstaller
{
    public const string TargetVersion = "26.1023.51421";
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPetPlus", "mod");
    private static string Manifest => Path.Combine(Root, "installation.json");
    private static string RuntimeStatus => Path.Combine(Root, "runtime-status.json");

    public static ModInstallation? Current()
    {
        try
        {
            var value = JsonSerializer.Deserialize<ModInstallation>(File.ReadAllText(Manifest));
            if (value is null || !File.Exists(value.Executable)) return null;
            if (value.Mode == "direct") return value.BackupFile is not null && IsInsideRoot(value.BackupFile) && File.Exists(value.BackupFile) && IsSource(value.Executable) ? value : null;
            return IsInsideRoot(value.Executable) ? value : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    public static string VersionOf(string executable) => DesktopAppVersion.ReadFromExecutable(executable);

    public static long TemporaryBytes(string executable) => checked(new FileInfo(executable).Length + new FileInfo(ArchiveOf(executable)).Length);

    public static string Status()
    {
        var installed = Current(); if (installed is null) return "먼저 앱에 패치해 주세요.";
        if (installed.Mode == "direct" && installed.State != "installed") return "중단된 패치 기록이 있습니다. 앱을 종료하고 ‘원본 복원’을 실행해 주세요.";
        try
        {
            var legacyStatus = Path.Combine(Path.GetDirectoryName(installed.Executable)!, "resources", "pet-plus-status.json");
            var file = File.Exists(RuntimeStatus) ? RuntimeStatus : legacyStatus;
            if (!File.Exists(file) || new FileInfo(file).Length > 8192) return "모드 실행 기록이 없습니다. 앱을 열고 원하는 펫을 켜 주세요.";
            var status = JsonSerializer.Deserialize<ModRuntimeStatus>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (status is null || !status.Running || status.UpdatedAt < DateTimeOffset.UtcNow.AddMinutes(-3)) return "앱이 종료됐거나 상태가 오래됐습니다. 앱 실행 상태를 확인해 주세요.";
            using var process = Process.GetProcessById(status.ProcessId);
            if (!string.Equals(process.MainModule?.FileName, installed.Executable, StringComparison.OrdinalIgnoreCase)) return "패치한 앱의 실행 상태를 확인하지 못했습니다.";
            return status.PetWindows == 0 ? "모드는 실행 중이며 펫 화면을 기다립니다. 앱에서 원하는 펫을 켜 주세요."
                : $"펫 {status.PetWindows}개에 연결됐습니다. " + (status.QuotaAvailable ? "계정 한도를 조회했습니다." : "계정 한도를 조회하지 못했습니다. Codex 로그인 상태를 확인해 주세요.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
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
        if (!IsSource(executable)) throw new IOException("기존에 설치된 앱의 ChatGPT.exe 또는 Codex.exe를 선택해 주세요.");
        var version = VersionOf(executable);
        if (version != TargetVersion) throw new IOException($"이 설치기는 {TargetVersion}용입니다. 선택한 앱 버전은 {version}입니다.");
        var previous = Current();
        if (previous?.Mode == "direct") throw new IOException("직접 패치 기록이 있습니다. 먼저 ‘원본 복원’을 실행한 뒤 다시 패치해 주세요.");
        EnsureClosed(executable);
        if (previous is not null && previous.Mode != "direct") EnsureClosed(previous.Executable);
        CheckWritable(executable);
        Directory.CreateDirectory(Path.Combine(Root, "backups"));
        var backup = Path.Combine(Root, "backups", "recovery-" + Guid.NewGuid().ToString("N") + ".zip");
        var pending = new ModInstallation(executable, executable, version, DateTimeOffset.UtcNow, "direct", backup, "pending");
        var installed = pending with { State = "installed" }; var recorded = false;
        try
        {
            progress.Report("변경할 두 파일을 준비하고 작은 압축 복원 데이터를 만듭니다. 앱 전체는 복사하지 않습니다.");
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexPetPlus.Mod.Runtime") ?? throw new IOException("모드 파일을 찾지 못했습니다.");
            using var reader = new StreamReader(resource); var runtime = await reader.ReadToEndAsync(cancellation);
            await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                using var plan = DirectModPatch.Prepare(executable, backup, runtime);
                cancellation.ThrowIfCancellationRequested();
                EnsureClosed(executable);
                WriteManifest(pending); recorded = true;
                progress.Report("기존 앱의 app.asar와 실행 파일에 패치를 적용합니다.");
                plan.Commit(); WriteManifest(installed);
            }, cancellation);
        }
        catch
        {
            if (recorded)
            {
                try { if (DirectModPatch.IsOriginal(executable, backup)) { WriteManifest(previous); File.Delete(backup); } }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
            }
            else { try { if (File.Exists(backup)) File.Delete(backup); } catch (IOException) { } }
            throw;
        }
        if (previous is not null && previous.Mode != "direct")
        {
            TryRemoveCopy(Path.GetDirectoryName(previous.Executable)!); TryRemoveLegacyShortcut();
        }
        progress.Report($"앱에 패치했습니다. 압축 복원 데이터: {new FileInfo(backup).Length / 1024d / 1024d:0.##} MB. 평소처럼 앱을 열고 펫을 켜 주세요.");
        return installed;
    }

    public static async Task RestoreAsync(IProgress<string> progress)
    {
        var installed = Current() ?? throw new IOException("복원할 패치 기록이 없습니다.");
        EnsureClosed(installed.Executable);
        if (installed.Mode == "direct")
        {
            CheckWritable(installed.Executable);
            WriteManifest(installed with { State = "pending" });
            progress.Report("압축 복원 데이터와 현재 앱을 확인해 원본을 복원합니다.");
            await Task.Run(() => DirectModPatch.Restore(installed.Executable, installed.BackupFile!));
            WriteManifest(null);
            try { File.Delete(installed.BackupFile!); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            progress.Report("원본 파일을 복원했습니다. 기존 앱을 실행할 수 있습니다.");
        }
        else
        {
            TryRemoveCopy(Path.GetDirectoryName(installed.Executable)!); TryRemoveLegacyShortcut(); WriteManifest(null);
            progress.Report("이전 복사본 모드 기록을 정리했습니다. 기존 공식 앱은 그대로 사용할 수 있습니다.");
        }
    }

    public static void CheckAccess(string executable)
    {
        executable = Path.GetFullPath(executable);
        if (!IsSource(executable)) throw new IOException("설치된 앱의 실행 파일을 선택해 주세요.");
        EnsureClosed(executable); CheckWritable(executable);
    }

    public static void Launch()
    {
        var installed = Current() ?? throw new IOException("먼저 앱에 패치해 주세요.");
        if (installed.State != "installed") throw new IOException("중단된 패치 기록이 있습니다. 먼저 ‘원본 복원’을 실행해 주세요.");
        EnsureClosed(installed.Executable);
        Process.Start(new ProcessStartInfo(installed.Executable) { WorkingDirectory = Path.GetDirectoryName(installed.Executable), UseShellExecute = true });
    }

    public static string BackupFolder => Root;
    private static string ArchiveOf(string executable) => Path.Combine(Path.GetDirectoryName(executable)!, "resources", "app.asar");
    private static bool IsInsideRoot(string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool IsSource(string file) => File.Exists(file) && !IsInsideRoot(file)
        && (Path.GetFileName(file).Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file).Equals("Codex.exe", StringComparison.OrdinalIgnoreCase))
        && File.Exists(ArchiveOf(file));

    private static void EnsureClosed(string executable)
    {
        foreach (var process in Process.GetProcessesByName("ChatGPT").Concat(Process.GetProcessesByName("Codex")))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("진행 중인 작업을 마치고 앱을 트레이에서도 완전히 종료한 뒤 패치·복원·실행해 주세요.");
                }
                catch (System.ComponentModel.Win32Exception) { throw new IOException("실행 중인 앱 상태를 확인하지 못했습니다. 앱을 완전히 종료해 주세요."); }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static void CheckWritable(string executable)
    {
        try
        {
            foreach (var file in new[] { executable, ArchiveOf(executable) })
            {
                using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                var probe = Path.Combine(Path.GetDirectoryName(file)!, ".pet-plus-permission-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            }
        }
        catch (UnauthorizedAccessException error)
        { throw new IOException("앱 설치 폴더에 쓰기 권한이 없어 직접 패치할 수 없습니다. WindowsApps 보호 설치본에서는 수정이 제한됩니다.", error); }
        catch (IOException error)
        { throw new IOException("앱 파일이 사용 중이거나 설치 폴더를 수정할 수 없습니다. 앱을 완전히 종료하고 쓰기 권한을 확인해 주세요.", error); }
    }

    private static void WriteManifest(ModInstallation? installed)
    {
        if (installed is null) { if (File.Exists(Manifest)) File.Delete(Manifest); return; }
        Directory.CreateDirectory(Root);
        var temporary = Manifest + ".partial";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, installed); file.Flush(true); }
        File.Move(temporary, Manifest, true);
    }

    private static void TryRemoveCopy(string directory)
    {
        try
        {
            if (!IsInsideRoot(directory) || !Directory.Exists(directory)) return;
            foreach (var process in Process.GetProcessesByName("ChatGPT").Concat(Process.GetProcessesByName("Codex")))
            {
                using (process)
                {
                    try { if (process.MainModule?.FileName is { } file && file.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return; }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return; }
                }
            }
            Directory.Delete(directory, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException) { }
    }

    private static void TryRemoveLegacyShortcut()
    {
        object? shell = null, shortcut = null;
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Codex Pet Plus.lnk");
            if (!File.Exists(file)) return;
            var type = Type.GetTypeFromProgID("WScript.Shell"); if (type is null) return;
            shell = Activator.CreateInstance(type); if (shell is null) return;
            shortcut = ((dynamic)shell).CreateShortcut(file);
            if (((dynamic)shortcut).TargetPath is string target && IsInsideRoot(target)) File.Delete(file);
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or IOException or ArgumentException or TargetInvocationException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
