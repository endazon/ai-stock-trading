namespace AiStockTrading.RiskManagement.Application.Ports;

// FR-10: 現在時刻の供給（テスト容易性のため抽象化）。ロックアウトの翌営業日解除・変更履歴の日時に用いる。
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>基準タイムゾーンにおける当日（日付）。ロックアウトの解除判定に用いる。</summary>
    DateOnly Today { get; }
}
