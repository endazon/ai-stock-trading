namespace AiStockTrading.Report.Application.Ports;

// FR-07: 現在時刻の供給（テスト容易性のため抽象化）。確定日時に用いる。
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
