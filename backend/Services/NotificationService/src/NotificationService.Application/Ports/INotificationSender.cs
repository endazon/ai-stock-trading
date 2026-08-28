using NotificationService.Application.State;

namespace NotificationService.Application.Ports;

// FR-09, IADR-0020: 通知の送信ポート。既定は外部送信しない no-op（ログのみ）。実 Discord 送信は構成で明示有効化する。
// 送信失敗は例外化し、呼び出し側（メッセージングのハンドラ）の再試行→デッドレターで可用性を担保する。
public interface INotificationSender
{
    Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default);
}
