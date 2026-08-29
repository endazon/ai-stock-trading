namespace TradeDecisionService.Common.Abstractions;

// FR-04: 現在時刻の供給（テスト容易性のため抽象化）。各サービスはポートを自己所有する。
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
