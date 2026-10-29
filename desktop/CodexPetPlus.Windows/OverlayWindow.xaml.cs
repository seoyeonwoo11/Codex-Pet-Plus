using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CodexPetPlus.Core;

namespace CodexPetPlus.Windows;

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeWindows.MakeCompanion(new WindowInteropHelper(this).Handle);
    }
    public void Display(UsageSnapshot? snapshot, string status, bool above)
    {
        var now = DateTimeOffset.UtcNow;
        var primary = snapshot?.Primary?.Remaining(now); var secondary = snapshot?.Secondary?.Remaining(now);
        PrimaryLabel.Text = snapshot?.Primary?.Label("기본") ?? "기본";
        SecondaryLabel.Text = snapshot?.Secondary?.Label("추가") ?? "추가";
        PrimaryValue.Text = primary is { } p ? $"{p:0.#}%" : "—";
        SecondaryValue.Text = secondary is { } s ? $"{s:0.#}%" : "—";
        PrimaryBar.Value = primary ?? 0; SecondaryBar.Value = secondary ?? 0;
        PrimaryBar.Foreground = new SolidColorBrush(primary < 20 ? Color.FromRgb(225, 151, 110) : Color.FromRgb(158, 180, 134));
        SecondaryBar.Foreground = new SolidColorBrush(secondary < 20 ? Color.FromRgb(225, 151, 110) : Color.FromRgb(158, 180, 134));
        var age = snapshot is null ? 0 : Math.Max(0, (now - snapshot.RecordedAt).TotalMinutes);
        SourceLabel.Text = snapshot is null ? "한도 확인 필요 · 우클릭으로 설정" : snapshot.Source == UsageSource.AppServer && age < 2 ? "연결됨 · 남은 한도" : $"{(snapshot.Source == UsageSource.LocalRecord ? "최근 기록" : "이전 조회")} · {(age < 1 ? "방금" : $"{(int)age}분 전")}";
        Card.ToolTip = $"{status}\n{(snapshot is null ? "" : $"조회 시각: {snapshot.RecordedAt.LocalDateTime:g}")}\n기본 초기화: {Reset(snapshot?.Primary)}\n추가 초기화: {Reset(snapshot?.Secondary)}\n{(above ? "펫 위에서 위치를 따라갑니다." : "화면 위쪽 공간이 부족해 펫 아래에 표시 중입니다.")}\n우클릭: 설정 / 새로고침 / 종료";
    }
    private static string Reset(QuotaWindow? window) => window?.ResetsAt?.LocalDateTime.ToString("g") ?? "확인 불가";
}
