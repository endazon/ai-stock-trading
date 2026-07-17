namespace AiStockTrading.Notification.Application.Ports;

// FR-14, IADR-0062 決定1: Discord Gateway（WebSocket）常駐の抽象。
//
// 詳細設計07 は Gateway 接続（アウトバウンドのみ・受信ポートを外部公開しない）を採用し、Interactions
// Endpoint 方式を明示的に不採用としている。実装（Discord.Net）は Worker のアダプタに隔離し、Application は
// 本ポートのみに依存する。これにより多層認証・コマンド解析・冪等機構は実 Discord なしで全数テストできる。
//
// 既定の実装は接続しない no-op（NullDiscordBotGateway）。IADR-0020 の送信側と同じ安全既定。
public interface IDiscordBotGateway
{
    // Gateway へ接続し、スラッシュコマンドを登録して着信の購読を開始する。
    Task StartAsync(CancellationToken cancellationToken = default);

    // Gateway から切断する。
    Task StopAsync(CancellationToken cancellationToken = default);
}
