using System.Diagnostics;
using System.Globalization;

namespace CodexPetPlus.Core;

public sealed record ElevationRequest(string Operation, int ParentProcessId, string AppPath, Guid RequestId)
{
    public static ElevationRequest Parse(string[] args)
    {
        if (args.Length != 8 || args[0] != "--admin-task" || args[2] != "--parent" || args[4] != "--app" || args[6] != "--request"
            || args[1] is not ("patch" or "restore" or "check")
            || !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var parent) || parent <= 0
            || !Path.IsPathFullyQualified(args[5]) || !Guid.TryParseExact(args[7], "N", out var id) || id == Guid.Empty)
            throw new ArgumentException("관리자 작업 요청이 올바르지 않습니다.");
        return new(args[1], parent, Path.GetFullPath(args[5]), id);
    }

    public ProcessStartInfo StartInfo(string installer)
    {
        var arguments = new[] { "--admin-task", Operation, "--parent", ParentProcessId.ToString(CultureInfo.InvariantCulture), "--app", AppPath, "--request", RequestId.ToString("N") };
        _ = Parse(arguments);
        var info = new ProcessStartInfo(Path.GetFullPath(installer)) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(installer)) };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
}
