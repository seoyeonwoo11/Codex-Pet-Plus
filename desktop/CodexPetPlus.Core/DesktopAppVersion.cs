namespace CodexPetPlus.Core;

public static class DesktopAppVersion
{
    // Electron's app.getVersion() reads the app package's version. The EXE may
    // instead advertise Chromium's version; MSIX folder versions are different too.
    public static string ReadFromExecutable(string executable)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        return Normalize(AsarModPatcher.ReadPackageVersion(Path.Combine(directory, "resources", "app.asar")));
    }

    public static string Normalize(string value)
    {
        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        text = text.Split('+', ' ', '-')[0];
        if (!Version.TryParse(text, out var version) || version.Build < 0)
            throw new IOException("앱 패키지의 버전 형식을 확인하지 못했습니다.");
        return version.Revision > 0 ? version.ToString(4) : version.ToString(3);
    }
}
