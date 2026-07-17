namespace AiStockTrading.InformationCollection.Application.Ports;

// FR-01: 現在時刻の供給（テスト容易性のため抽象化）。レート制限（IADR-0064）の補充判定に用いる。
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
