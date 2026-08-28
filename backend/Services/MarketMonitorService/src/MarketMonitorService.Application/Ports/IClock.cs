namespace MarketMonitorService.Application.Ports;

// FR-03: 現在時刻の供給（テスト容易性のため抽象化）。クールダウン判定に用いる。
// 各サービスはポートを自己所有する（サービス間結合を避ける）。
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
