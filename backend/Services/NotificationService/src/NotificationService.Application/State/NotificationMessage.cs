namespace NotificationService.Application.State;

// FR-09: 通知の重大度（テンプレート・宛先の出し分けや将来の抑制ポリシーに用いる）。
public enum NotificationSeverity
{
    Info,
    Warning,
    Critical,
}

// FR-09, IADR-0020: 送信可能な形へ整形済みの通知メッセージ（イベント非依存）。送信ポート INotificationSender が受け取る。
public sealed record NotificationMessage(string Title, string Content, NotificationSeverity Severity);
