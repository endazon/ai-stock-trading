namespace AiStockTrading.CostControl.Application.Ports;

// NFR（費用）: 現在時刻の供給（テスト容易性のため抽象化）。費用計上月・日時に用いる。
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
