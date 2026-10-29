namespace CodexPetPlus.Core;

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}
public readonly record struct PanelPosition(int X, int Y, bool Above);

public static class Placement
{
    public static PanelPosition BelowPet(PixelRect pet, PixelRect workArea, int width, int height, int gap = 5)
    {
        var x = Math.Clamp(pet.Left + (pet.Width - width) / 2, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        var below = pet.Bottom + gap;
        var above = below + height > workArea.Bottom;
        var y = above ? pet.Top - gap - height : below;
        return new(x, Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height)), above);
    }
}
