using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CodexPetPlus.Core;
using CodexPetPlus.Windows;

internal static class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();

    [STAThread]
    private static int Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 1;
        application.Startup += async (_, _) =>
        {
            try { await RunAsync(); exitCode = 0; Console.WriteLine("WINDOWS_FOLLOWER_PASS"); }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { application.Shutdown(exitCode); }
        };
        application.Run();
        return exitCode;
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static Window Fixture(string title) => new()
    {
        Title = title, Width = 140, Height = 140,
        Left = SystemParameters.WorkArea.Left + 240, Top = SystemParameters.WorkArea.Top + 180,
        WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
        Background = Brushes.Tan, Topmost = true, ShowActivated = false, ShowInTaskbar = false,
        Content = new TextBlock { Text = "합성 테스트 펫", VerticalAlignment = VerticalAlignment.Center }
    };

    private static async Task RunAsync()
    {
        var main = new Window { Title = "Codex Pet Plus project", Width = 400, Height = 300 };
        var pet = Fixture("");
        // The synthetic host shares the ChatGPT process name, so exclude its panel
        // using the same DevTools exclusion used for real host windows.
        var panel = new OverlayWindow { Title = "DevTools fixture panel" };
        Window? replacement = null;
        try
        {
            main.Show(); pet.Show();
            await Task.Delay(150);
            var petHandle = new WindowInteropHelper(pet).EnsureHandle();
            Check(NativeWindows.Find().Window?.Handle == petHandle, "Untitled pet was not found automatically");
            pet.Title = "모찌 고양이";
            Check(NativeWindows.Find().Window?.Handle == petHandle, "Custom pet title was not recognized");
            var foreground = GetForegroundWindow();
            panel.Show();
            await Task.Delay(100);
            var panelHandle = new WindowInteropHelper(panel).EnsureHandle();
            var anchor = NativeWindows.Inspect(petHandle)!;
            var position = NativeWindows.Follow(panelHandle, anchor, 180, 62);
            Check(position.Above, "Native panel did not prefer above the pet");
            Check(GetWindowRect(panelHandle, out var first) && first.Bottom < anchor.Bounds.Top, "Native panel covers the pet");
            Check(first.Left == position.X && first.Top == position.Y, "Native placement did not use physical pixels");
            Check(GetForegroundWindow() == foreground, "Showing or following the panel stole keyboard focus");

            pet.Left += 45; pet.Top += 30;
            await Task.Delay(100);
            anchor = NativeWindows.Inspect(petHandle)!;
            NativeWindows.Follow(panelHandle, anchor, 180, 62);
            Check(GetWindowRect(panelHandle, out var moved) && moved.Left > first.Left && moved.Top > first.Top && moved.Bottom < anchor.Bounds.Top,
                "Native panel did not follow the moving pet");
            pet.Top = SystemParameters.WorkArea.Top;
            await Task.Delay(100);
            anchor = NativeWindows.Inspect(petHandle)!;
            Check(!NativeWindows.Follow(panelHandle, anchor, 180, 62).Above, "Native top edge did not fall back below");
            Check(GetWindowRect(panelHandle, out var below) && below.Top > anchor.Bounds.Bottom, "Top-edge panel covers the pet");

            var now = DateTimeOffset.UtcNow;
            panel.Display(new(new(28, 300, now.AddHours(1)), new(16, 10080, now.AddDays(1)), now, UsageSource.AppServer), "Synthetic quota", true);
            Check(((TextBlock)panel.FindName("PrimaryValue")).Text == "72%" && ((TextBlock)panel.FindName("SecondaryValue")).Text == "84%", "Remaining percentages were not rendered");
            panel.Display(null, "No quota", true);
            Check(((TextBlock)panel.FindName("PrimaryValue")).Text == "—", "Unknown quota was invented");

            pet.Hide();
            Check(NativeWindows.Inspect(petHandle) is null && NativeWindows.Find().Window is null, "Hidden pet was still detected");
            pet.Show();
            Check(NativeWindows.Find().Window?.Handle == petHandle, "Reopened pet did not reconnect");
            pet.Close();
            replacement = Fixture("커스텀 토끼"); replacement.Show();
            await Task.Delay(100);
            Check(NativeWindows.Find().Window?.Handle == new WindowInteropHelper(replacement).EnsureHandle(), "Recreated custom pet did not reconnect");
        }
        finally { panel.Close(); replacement?.Close(); if (pet.IsLoaded) pet.Close(); main.Close(); }
    }
}
