using System.Windows;

namespace CodexPetPlus.Windows;

public partial class SettingsWindow : Window
{
    private readonly App app;
    internal SettingsWindow(App app)
    {
        InitializeComponent(); this.app = app;
        ExecutablePath.Text = app.Preferences.CodexExecutable;
        RecordsPath.Text = app.Preferences.RecordsHome;
        RecentRecords.IsChecked = app.Preferences.UseRecentRecords;
        UpdateStatus();
        app.StatusChanged += UpdateStatus;
        Closed += (_, _) => app.StatusChanged -= UpdateStatus;
    }
    private void UpdateStatus() { PetStatusText.Text = app.PetStatus; UsageStatusText.Text = app.UsageStatus; }
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
            app.Preferences.Save();
            await app.ResetConnectionAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { System.Windows.MessageBox.Show(this, "설정을 저장하지 못했습니다. 파일과 폴더 경로를 확인해 주세요.", "Codex Pet Plus"); }
    }
    private async void Refresh(object sender, RoutedEventArgs args) => await app.RefreshUsageAsync();
}
