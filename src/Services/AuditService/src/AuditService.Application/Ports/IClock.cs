namespace AiStockTrading.Audit.Application.Ports;

// FR-11: 記録時刻（RecordedAt）の供給（テスト容易性のため抽象化）。
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
