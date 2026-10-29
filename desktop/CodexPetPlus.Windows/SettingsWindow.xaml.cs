using System.Windows;
using System.Diagnostics;
using System.Text.Json;

namespace CodexPetPlus.Windows;

public partial class SettingsWindow : Window
{
    private readonly App app;
    private readonly CancellationTokenSource closing = new();
    internal SettingsWindow(App app)
    {
        InitializeComponent(); this.app = app;
        ExecutablePath.Text = app.Preferences.CodexExecutable;
        RecordsPath.Text = app.Preferences.RecordsHome;
        RecentRecords.IsChecked = app.Preferences.UseRecentRecords;
        CompanionOverlay.IsChecked = app.Preferences.UseCompanionOverlay;
        ModVersionText.Text = "대상 버전: " + DesktopModInstaller.TargetVersion;
        UpdateStatus();
        app.StatusChanged += UpdateStatus;
        Closed += (_, _) => { app.StatusChanged -= UpdateStatus; closing.Cancel(); };
        Loaded += async (_, _) =>
        {
            try
            {
                var installed = DesktopModInstaller.Current();
                if (installed is not null) ModStatusText.Text = installed.Version + " 모드가 설치돼 있습니다.";
                var source = await DesktopModInstaller.FindSourceAsync();
                if (string.IsNullOrWhiteSpace(DesktopAppPath.Text)) DesktopAppPath.Text = source ?? "";
                if (installed is null) ModStatusText.Text = source is null ? "앱을 켜거나 ‘찾기…’에서 설치된 앱 실행 파일을 선택해 주세요." : "앱을 찾았습니다. ‘모드 설치’를 눌러 복사본에 설치하세요.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { ModStatusText.Text = "앱 실행 파일을 자동으로 찾지 못했습니다. ‘찾기…’에서 선택해 주세요."; }
        };
    }
    private void UpdateStatus() { PetStatusText.Text = app.PetStatus; UsageStatusText.Text = app.UsageStatus; }
    private void FindPet(object sender, RoutedEventArgs args) => app.FindPetAgain();
    private void ChooseDesktopApp(object sender, RoutedEventArgs args)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "설치된 ChatGPT / Codex 앱 선택", Filter = "앱 실행 파일|ChatGPT.exe;Codex.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) DesktopAppPath.Text = dialog.FileName;
    }
    private async void InstallMod(object sender, RoutedEventArgs args)
    {
        InstallModButton.IsEnabled = false; LaunchModButton.IsEnabled = false;
        try
        {
            await DesktopModInstaller.InstallAsync(DesktopAppPath.Text.Trim(), new Progress<string>(message => ModStatusText.Text = message), closing.Token);
        }
        catch (OperationCanceledException) { ModStatusText.Text = "설치를 취소했습니다."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or ArgumentException or FormatException or OverflowException or System.ComponentModel.Win32Exception)
        { ModStatusText.Text = "설치하지 못했습니다. " + ex.Message; }
        finally { InstallModButton.IsEnabled = true; LaunchModButton.IsEnabled = true; }
    }
    private void LaunchMod(object sender, RoutedEventArgs args)
    {
        try { DesktopModInstaller.Launch(); ModStatusText.Text = "모드 앱을 실행했습니다. 앱에서 기본 펫 또는 커스텀 펫을 켜 주세요."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { ModStatusText.Text = ex.Message; }
    }
    private void CheckModStatus(object sender, RoutedEventArgs args) => ModStatusText.Text = DesktopModInstaller.Status();
    private void OpenModFolder(object sender, RoutedEventArgs args)
    {
        var installed = DesktopModInstaller.Current();
        if (installed is null) { ModStatusText.Text = "먼저 모드를 설치해 주세요."; return; }
        try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(installed.Executable)!) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { ModStatusText.Text = "설치 폴더를 열지 못했습니다."; }
    }
    private async void SelectPet(object sender, RoutedEventArgs args)
    {
        SelectPetButton.IsEnabled = false;
        for (var seconds = 3; seconds > 0; seconds--) { SelectPetButton.Content = $"펫 위에 커서를 두세요 · {seconds}"; await Task.Delay(1000); }
        app.SelectPetUnderCursor();
        SelectPetButton.Content = "펫 선택 (3초 후)"; SelectPetButton.IsEnabled = true;
    }
    private void ChooseExecutable(object sender, RoutedEventArgs args)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Codex 설치 폴더의 resources 안에 있는 codex.exe 선택", Filter = "Codex 연결 도구|codex.exe|실행 파일|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) ExecutablePath.Text = dialog.FileName;
    }
    private async void Save(object sender, RoutedEventArgs args)
    {
        try
        {
            app.Preferences.CodexExecutable = ExecutablePath.Text.Trim();
            app.Preferences.RecordsHome = Path.GetFullPath(RecordsPath.Text.Trim());
            app.Preferences.UseRecentRecords = RecentRecords.IsChecked == true;
            app.Preferences.UseCompanionOverlay = CompanionOverlay.IsChecked == true;
            app.Preferences.Save();
            await app.ResetConnectionAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { System.Windows.MessageBox.Show(this, "설정을 저장하지 못했습니다. 파일과 폴더 경로를 확인해 주세요.", "Codex Pet Plus"); }
    }
    private async void Refresh(object sender, RoutedEventArgs args) => await app.RefreshUsageAsync();
}
