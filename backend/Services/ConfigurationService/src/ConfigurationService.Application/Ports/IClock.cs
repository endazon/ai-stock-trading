namespace ConfigurationService.Application.Ports;

// FR-17: 現在時刻の供給（テスト容易性のため抽象化）。変更履歴の日時に用いる。
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
