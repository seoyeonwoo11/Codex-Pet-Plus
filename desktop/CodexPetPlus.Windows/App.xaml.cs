using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using CodexPetPlus.Core;
using Forms = System.Windows.Forms;

namespace CodexPetPlus.Windows;

public partial class App : System.Windows.Application
{
    internal Preferences Preferences { get; } = Preferences.Load();
    internal string PetStatus { get; private set; } = "Codex 앱에서 기본 펫을 켜고 선택해 주세요.";
    internal string UsageStatus { get; private set; } = "한도 확인 중";
    internal event Action? StatusChanged;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim quotaGate = new(1, 1);
    private readonly DispatcherTimer followTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer usageTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private OverlayWindow overlay = null!;
    private SettingsWindow? settings;
    private Forms.NotifyIcon tray = null!;
    private CodexConnection? connection;
    private UsageSnapshot? snapshot;
    private PetWindow? pet;
    private nint attachedHandle;
    private string? executable;
    private DateTime nextPetSearch;
    private Mutex? singleInstance;
    private bool closing;
    private bool above;

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        singleInstance = new Mutex(true, @"Local\CodexPetPlus", out var first);
        if (!first) { System.Windows.MessageBox.Show("이미 실행 중입니다. 작업 표시줄 트레이의 Codex Pet Plus 아이콘을 열어 주세요.", "Codex Pet Plus"); Shutdown(); return; }
        overlay = new OverlayWindow();
        overlay.ContextMenu = OverlayMenu();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("설정 · 펫 선택", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("한도 새로고침", null, (_, _) => Dispatcher.InvokeAsync(async () => await RefreshUsageAsync()));
        menu.Items.Add("종료", null, (_, _) => Dispatcher.Invoke(Quit));
        tray = new Forms.NotifyIcon { Text = "Codex Pet Plus", Icon = System.Drawing.SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(OpenSettings);
        followTimer.Tick += (_, _) => FollowPet();
        usageTimer.Tick += async (_, _) => await RefreshUsageAsync();
        followTimer.Start(); usageTimer.Start();
        OpenSettings();
        _ = RefreshUsageAsync();
    }

    private ContextMenu OverlayMenu()
    {
        var menu = new ContextMenu();
        var setup = new MenuItem { Header = "설정 · 펫 선택" }; setup.Click += (_, _) => OpenSettings();
        var refresh = new MenuItem { Header = "한도 새로고침" }; refresh.Click += async (_, _) => await RefreshUsageAsync();
        var exit = new MenuItem { Header = "종료" }; exit.Click += (_, _) => Quit();
        menu.Items.Add(setup); menu.Items.Add(refresh); menu.Items.Add(exit); return menu;
    }

    private void OpenSettings()
    {
        if (settings is null) { settings = new SettingsWindow(this); settings.Closed += (_, _) => settings = null; settings.Show(); }
        settings.Activate();
    }

    internal void SelectPetUnderCursor()
    {
        pet = NativeWindows.UnderCursor();
        attachedHandle = pet?.Handle ?? 0;
        if (pet is null) PetStatus = "커서 아래에서 작은 Codex 펫 창을 찾지 못했습니다. 펫 위에서 다시 선택해 주세요.";
        else
        {
            Preferences.PetTitle = pet.Title;
            try { Preferences.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            PetStatus = "펫에 연결됐습니다. 이동하면 한도 표시줄도 따라갑니다.";
        }
        StatusChanged?.Invoke(); FollowPet();
    }

    private void FollowPet()
    {
        if (closing) return;
        pet = attachedHandle == 0 ? null : NativeWindows.Inspect(attachedHandle);
        if (pet is null && DateTime.UtcNow >= nextPetSearch)
        {
            pet = NativeWindows.Find(Preferences.PetTitle);
            nextPetSearch = DateTime.UtcNow.AddSeconds(3);
            if (pet is not null) attachedHandle = pet.Handle;
        }
        if (pet is null)
        {
            if (overlay.IsVisible) { overlay.Hide(); PetStatus = "펫이 숨겨졌거나 닫혔습니다. 다시 나타나면 연결을 확인합니다."; StatusChanged?.Invoke(); }
            return;
        }
        if (!overlay.IsVisible)
        {
            PetStatus = "펫에 연결됐습니다. 이동하면 한도 표시줄도 따라갑니다."; StatusChanged?.Invoke();
            overlay.Opacity = 0; overlay.Show();
        }
        var handle = new WindowInteropHelper(overlay).EnsureHandle();
        above = NativeWindows.Follow(handle, pet, 180, 62).Above;
        overlay.Opacity = 1;
        overlay.Display(snapshot, UsageStatus, above);
    }

    internal async Task ResetConnectionAsync()
    {
        await quotaGate.WaitAsync();
        try { if (connection is not null) await connection.DisposeAsync(); connection = null; executable = null; snapshot = null; }
        finally { quotaGate.Release(); }
        await RefreshUsageAsync();
    }

    internal async Task RefreshUsageAsync()
    {
        if (closing || !await quotaGate.WaitAsync(0)) return;
        try
        {
            try
            {
                executable ??= await ExecutableLocator.FindAsync(Preferences.CodexExecutable, lifetime.Token);
                if (executable is not null)
                {
                    connection ??= await CodexConnection.StartAsync(executable, lifetime.Token);
                    var live = await connection.ReadUsageAsync(lifetime.Token);
                    if (live is not null) { snapshot = live; UsageStatus = "Codex 로컬 연결에서 계정의 남은 한도를 조회했습니다."; return; }
                }
                UsageStatus = "Codex 연결 도구나 한도를 찾지 못했습니다. 최근 기록을 확인합니다.";
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                if (connection is not null) await connection.DisposeAsync(); connection = null;
                UsageStatus = "Codex의 현재 한도를 조회하지 못했습니다. 앱의 로그인 상태와 연결 도구 경로를 확인해 주세요.";
            }
            if (Preferences.UseRecentRecords)
            {
                var home = connection?.CodexHome ?? Preferences.RecordsHome;
                var record = await Task.Run(() => RecentUsageReader.ReadAsync(home, lifetime.Token));
                if (record is not null && (snapshot is null || record.RecordedAt > snapshot.RecordedAt)) snapshot = record;
                else if (snapshot is not null) snapshot = snapshot with { Source = UsageSource.Cached };
                if (snapshot is not null && snapshot.RecordedAt < DateTimeOffset.UtcNow.AddHours(-24)) snapshot = null;
                if (snapshot is not null) UsageStatus += " 표시된 비율은 최근 기록이며 현재 계정의 실시간 한도는 아닐 수 있습니다.";
            }
            else snapshot = null;
        }
        catch (OperationCanceledException) when (closing) { }
        finally
        {
            quotaGate.Release();
            if (!closing) { overlay.Display(snapshot, UsageStatus, above); StatusChanged?.Invoke(); }
        }
    }

    private async void Quit()
    {
        if (closing) return;
        closing = true; followTimer.Stop(); usageTimer.Stop(); lifetime.Cancel();
        await quotaGate.WaitAsync();
        try { if (connection is not null) await connection.DisposeAsync(); }
        finally
        {
            quotaGate.Release(); tray.Visible = false; tray.Dispose(); overlay.Close(); settings?.Close();
            singleInstance?.Dispose(); lifetime.Dispose(); Shutdown();
        }
    }
}
