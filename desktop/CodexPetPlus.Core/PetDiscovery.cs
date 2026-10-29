using System.Text.RegularExpressions;

namespace CodexPetPlus.Core;

public sealed record PetWindow(nint Handle, int ProcessId, string Title, PixelRect Bounds, uint Dpi,
    string ClassName = "", uint Style = 0, uint ExtendedStyle = 0);

public sealed record PetSearchResult(PetWindow? Window, int CandidateCount);

// NativeWindows supplies only visible, uncloaked windows owned by Codex/ChatGPT.
public static partial class PetDiscovery
{
    private const uint Caption = 0x00C00000, ThickFrame = 0x00040000, Child = 0x40000000;
    private const uint Topmost = 0x00000008, ToolWindow = 0x00000080, AppWindow = 0x00040000,
        Layered = 0x00080000, NoActivate = 0x08000000;

    [GeneratedRegex(@"(?:^|[^\p{L}\p{N}])(?:pet|companion|buddy|avatar)(?:$|[^\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PetName();

    public static bool CanSelect(PetWindow window)
    {
        var scale = Math.Max(96, window.Dpi) / 96d;
        return window.Bounds.Width / scale is >= 30 and <= 650 && window.Bounds.Height / scale is >= 30 and <= 650
            && (window.Style & Child) == 0
            && window.ClassName is not ("#32770" or "#32768" or "tooltips_class32")
            && !window.Title.StartsWith("DevTools", StringComparison.OrdinalIgnoreCase);
    }

    public static PetSearchResult Find(IEnumerable<PetWindow> windows, string? preferredTitle = null, string? preferredClass = null)
    {
        var candidates = windows.Where(IsAutomaticCandidate).ToList();
        // Restore a deliberate selection only when its window signature is unique.
        // An empty title is a valid signature for an untitled pet window.
        if (!string.IsNullOrEmpty(preferredClass))
        {
            var previous = candidates.Where(window => window.Title == preferredTitle && window.ClassName == preferredClass).ToList();
            if (previous.Count == 1) return new(previous[0], candidates.Count);
        }
        var named = candidates.Where(HasPetName).ToList();
        if (named.Count == 1) return new(named[0], candidates.Count);
        if (named.Count > 1) return new(null, named.Count);
        return new(candidates.Count == 1 ? candidates[0] : null, candidates.Count);
    }

    private static bool HasPetName(PetWindow window) => PetName().IsMatch(window.Title) || window.Title.Contains("펫");

    private static bool IsAutomaticCandidate(PetWindow window)
    {
        if (!CanSelect(window) || (window.Style & ThickFrame) != 0) return false;
        var floating = (window.ExtendedStyle & (Topmost | ToolWindow | NoActivate)) != 0;
        var layered = (window.ExtendedStyle & Layered) != 0;
        if (HasPetName(window)) return (window.Style & Caption) == 0 || floating || layered;
        if ((window.Style & Caption) != 0) return false;
        var scale = Math.Max(96, window.Dpi) / 96d;
        var width = window.Bounds.Width / scale; var height = window.Bounds.Height / scale;
        if (width > 450 || height > 450 || width / height is < 0.4 or > 2.5) return false;
        var genericTitle = string.IsNullOrWhiteSpace(window.Title)
            || window.Title.Trim().Equals("Codex", StringComparison.OrdinalIgnoreCase)
            || window.Title.Trim().Equals("ChatGPT", StringComparison.OrdinalIgnoreCase);
        // Custom pet names are allowed when the window is both transparent and floating.
        // A title alone must not turn a normal app window into a pet.
        return genericTitle ? floating || (layered && (window.ExtendedStyle & AppWindow) == 0) : layered && floating;
    }
}
