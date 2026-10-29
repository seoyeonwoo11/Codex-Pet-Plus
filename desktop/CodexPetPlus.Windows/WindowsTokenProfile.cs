using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexPetPlus.Windows;

[SupportedOSPlatform("windows")]
internal static class WindowsTokenProfile
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, SafeAccessTokenHandle token, out nint path);

    public static string LocalAppDataOf(int processId, string expectedInstaller)
    {
        using var process = OpenProcess(0x1000, false, processId); // query-limited information
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var name = new StringBuilder(32768); uint length = (uint)name.Capacity;
        if (!QueryFullProcessImageName(process, 0, name, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!Path.GetFullPath(name.ToString()).Equals(Path.GetFullPath(expectedInstaller), StringComparison.OrdinalIgnoreCase))
            throw new IOException("관리자 작업을 요청한 설치기를 확인하지 못했습니다.");
        if (!OpenProcessToken(process, 0x0008 | 0x0004, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            var folder = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
            var result = SHGetKnownFolderPath(ref folder, 0, token, out var value);
            try
            {
                Marshal.ThrowExceptionForHR(result);
                return Marshal.PtrToStringUni(value) ?? throw new IOException("원래 사용자의 데이터 폴더를 찾지 못했습니다.");
            }
            finally { if (value != 0) Marshal.FreeCoTaskMem(value); }
        }
    }
}
