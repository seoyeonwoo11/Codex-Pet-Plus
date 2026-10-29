using System.Windows;
using System.Diagnostics;
using System.Text.Json;

namespace CodexPetPlus.Windows;

public partial class SettingsWindow : Window
{
    private readonly App app;
    private readonly CancellationTokenSource closing = new();
    private bool updatingToggle;
    internal bool IsModOperationRunning { get; private set; }

    internal SettingsWindow(App app)
    {
        InitializeComponent(); this.app = app;
        ExecutablePath.Text = app.Preferences.CodexExecutable;
        RecordsPath.Text = app.Preferences.RecordsHome;
        RecentRecords.IsChecked = app.Preferences.UseRecentRecords;
        FollowerEnabled.IsChecked = app.Preferences.FollowPetEnabled;
        FollowerEnabled.Checked += ToggleFollower;
        FollowerEnabled.Unchecked += ToggleFollower;
        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "0.6.0";
        Title = "Codex Pet Plus " + version;
        VersionText.Text = "버전 " + version + " · 앱 수정·복사 없이 실행";
        UpdateStatus();
        app.StatusChanged += UpdateStatus;
        Closed += (_, _) => { app.StatusChanged -= UpdateStatus; closing.Cancel(); };
        Closing += (_, args) => { if (IsModOperationRunning) { args.Cancel = true; ShowBusyNotice(); } };
        Loaded += (_, _) =>
        {
            try
            {
                var installed = DesktopModInstaller.Current();
                ModStatusText.Text = installed is null ? "복원할 이전 패치 기록이 없습니다."
                    : "이전 앱 모드 기록이 있습니다. 표시줄 중복을 피하려면 원본을 복원해 주세요. 백업은 복원 전까지 보관하세요.";
                LegacyRecovery.IsExpanded = installed is not null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
            { ModStatusText.Text = "이전 패치 기록을 확인하지 못했습니다. 백업 폴더를 보관하고 복원 상태를 확인해 주세요."; }
        };
    }

    private void UpdateStatus() { PetStatusText.Text = app.PetStatus; UsageStatusText.Text = app.UsageStatus; }
    private void FindPet(object sender, RoutedEventArgs args) => app.FindPetAgain();

    private async void ToggleFollower(object sender, RoutedEventArgs args)
    {
        if (updatingToggle) return;
        var previous = app.Preferences.FollowPetEnabled;
        try
        {
            app.Preferences.FollowPetEnabled = FollowerEnabled.IsChecked == true;
            app.Preferences.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            app.Preferences.FollowPetEnabled = previous;
            updatingToggle = true; FollowerEnabled.IsChecked = previous; updatingToggle = false;
            System.Windows.MessageBox.Show(this, "표시줄 설정을 저장하지 못했습니다. 사용자 폴더의 쓰기 권한을 확인해 주세요.", "Codex Pet Plus");
            return;
        }
        await app.ResetConnectionAsync();
    }

    internal void ShowBusyNotice() => ModStatusText.Text = "이전 앱 모드를 복원하는 중입니다. 작업이 끝나면 종료할 수 있습니다.";
    private void SetModBusy(bool busy)
    {
        IsModOperationRunning = busy;
        RestoreModButton.IsEnabled = !busy; BackupFolderButton.IsEnabled = !busy;
    }

    private async void RestoreMod(object sender, RoutedEventArgs args)
    {
        if (IsModOperationRunning) return; SetModBusy(true);
        try
        {
            var installed = DesktopModInstaller.Current() ?? throw new IOException("복원할 패치 기록이 없습니다.");
            var progress = new Progress<string>(message => ModStatusText.Text = message);
            try
            {
                await DesktopModInstaller.RestoreAsync(progress);
                ModStatusText.Text = "원본 파일을 복원했습니다. 평소처럼 앱과 펫을 켜면 새 표시줄이 따라갑니다.";
            }
            catch (ModWriteAccessException) when (!Administrator.IsElevated)
            { ModStatusText.Text = (await Administrator.RunAsync("restore", installed.Executable, progress)).Message; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or ArgumentException or FormatException or OverflowException or System.ComponentModel.Win32Exception)
        { ModStatusText.Text = "복원하지 못했습니다. " + error.Message; }
        finally { SetModBusy(false); }
    }

    private void CheckModStatus(object sender, RoutedEventArgs args)
    {
        if (!IsModOperationRunning) ModStatusText.Text = DesktopModInstaller.Current() is null
            ? "복원할 이전 패치 기록이 없습니다." : DesktopModInstaller.Status();
    }
    private void OpenModFolder(object sender, RoutedEventArgs args)
    {
        try { Directory.CreateDirectory(DesktopModInstaller.BackupFolder); Process.Start(new ProcessStartInfo(DesktopModInstaller.BackupFolder) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { ModStatusText.Text = "백업 폴더를 열지 못했습니다."; }
    }

    private async void SelectPet(object sender, RoutedEventArgs args)
    {
        SelectPetButton.IsEnabled = false;
        try
        {
            for (var seconds = 3; seconds > 0; seconds--)
            {
                SelectPetButton.Content = $"펫 위에 커서를 두세요 · {seconds}";
                await Task.Delay(1000, closing.Token);
            }
            app.SelectPetUnderCursor();
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        finally { SelectPetButton.Content = "펫 선택 (3초 후)"; SelectPetButton.IsEnabled = true; }
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
            var home = Path.GetFullPath(RecordsPath.Text.Trim());
            app.Preferences.CodexExecutable = ExecutablePath.Text.Trim();
            app.Preferences.RecordsHome = home;
            app.Preferences.UseRecentRecords = RecentRecords.IsChecked == true;
            app.Preferences.Save();
            await app.ResetConnectionAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { System.Windows.MessageBox.Show(this, "설정을 저장하지 못했습니다. 파일과 폴더 경로를 확인해 주세요.", "Codex Pet Plus"); }
    }
    private async void Refresh(object sender, RoutedEventArgs args) => await app.RefreshUsageAsync();
}
