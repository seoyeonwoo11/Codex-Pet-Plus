using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using CodexPetPlus.Core;

namespace CodexPetPlus.Windows;

internal sealed record AdminTaskResult(bool Success, bool Cancelled, string Message);

internal static class Administrator
{
    public static bool IsElevated
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
    }

    private static string ResultFile(string root, Guid id) => Path.Combine(root, "elevation", id.ToString("N") + ".json");

    public static async Task<AdminTaskResult> RunAsync(string operation, string appPath, IProgress<string> progress)
    {
        var installer = Environment.ProcessPath ?? throw new IOException("설치기 실행 경로를 찾지 못했습니다.");
        var request = new ElevationRequest(operation, Environment.ProcessId, Path.GetFullPath(appPath), Guid.NewGuid());
        var resultFile = ResultFile(DesktopModInstaller.BackupFolder, request.RequestId);
        Directory.CreateDirectory(Path.GetDirectoryName(resultFile)!);
        progress.Report("Windows 관리자 승인(UAC)을 기다립니다. 승인 후 선택한 작업을 실행합니다.");
        try
        {
            using var child = Process.Start(request.StartInfo(installer)) ?? throw new IOException("관리자 작업을 시작하지 못했습니다.");
            await child.WaitForExitAsync();
            if (!File.Exists(resultFile) || new FileInfo(resultFile).Length > 16 * 1024)
                return new(false, false, "관리자 작업 결과를 확인하지 못했습니다. 원래 사용자 정보 또는 관리자 권한을 확인해 주세요. 상태 확인에서 패치 기록을 확인할 수 있습니다.");
            var result = JsonSerializer.Deserialize<AdminTaskResult>(await File.ReadAllTextAsync(resultFile));
            return result ?? new(false, false, "관리자 작업 결과를 읽지 못했습니다.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { return new(false, true, "관리자 승인을 취소했습니다. 현재 화면에서 계속 사용할 수 있습니다."); }
        finally
        {
            try { if (File.Exists(resultFile)) File.Delete(resultFile); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public static async Task<int> RunWorkerAsync(ElevationRequest request)
    {
        string? root = null;
        AdminTaskResult result;
        try
        {
            if (!IsElevated || request.ParentProcessId == Environment.ProcessId) throw new IOException("Windows 관리자 승인을 확인하지 못했습니다.");
            var installer = Environment.ProcessPath ?? throw new IOException("설치기를 확인하지 못했습니다.");
            root = Path.Combine(WindowsTokenProfile.LocalAppDataOf(request.ParentProcessId, installer), "CodexPetPlus", "mod");
            DesktopModInstaller.DataRootOverride = root;
            var progress = new Progress<string>();
            if (request.Operation == "patch")
            {
                await DesktopModInstaller.InstallAsync(request.AppPath, progress);
                result = new(true, false, "관리자 승인으로 패치했습니다. 평소 앱 바로가기로 열고 원하는 펫을 켜 주세요.");
            }
            else if (request.Operation == "restore")
            {
                var current = DesktopModInstaller.Current();
                if (current is null || !Path.GetFullPath(current.Executable).Equals(request.AppPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("선택한 앱과 복원 기록이 일치하지 않습니다.");
                await DesktopModInstaller.RestoreAsync(progress);
                result = new(true, false, "관리자 승인으로 원본 파일을 복원했습니다.");
            }
            else
            {
                DesktopModInstaller.CheckAccess(request.AppPath);
                result = new(true, false, "관리자 승인 상태에서 쓰기 권한을 확인했습니다. ‘앱에 패치’를 누르면 필요한 관리자 승인을 다시 요청합니다.");
            }
        }
        catch (ModWriteAccessException)
        { result = new(false, false, "관리자 승인을 받았지만 이 설치 폴더의 쓰기 권한이 없습니다. WindowsApps의 소유권·패키지 보호는 UAC 승인만으로 해제되지 않습니다."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException or COMException)
        { result = new(false, false, "관리자 작업을 완료하지 못했습니다. " + error.Message); }
        if (root is null) return 2; // Never guess the elevated account's profile for backups.
        try
        {
            var file = ResultFile(root, request.RequestId);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + ".partial";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(result));
            File.Move(temporary, file, true);
            return result.Success ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 2; }
    }
}
