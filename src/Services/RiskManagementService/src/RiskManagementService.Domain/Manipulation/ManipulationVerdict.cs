namespace AiStockTrading.RiskManagement.Domain.Manipulation;

// FR-19, IADR-0037: 相場操縦検知の判定結果。該当シグナルを列挙して保持する（監査/将来のログ用に理由を残す）。
public sealed record ManipulationVerdict
{
    /// <summary>該当した相場操縦シグナル（空＝無嫌疑）。</summary>
    public required IReadOnlyList<ManipulationSignal> Signals { get; init; }

    /// <summary>いずれかのシグナルに該当し、相場操縦とみなされ得るか。</summary>
    public bool IsSuspected => Signals.Count > 0;

    /// <summary>無嫌疑（該当シグナルなし）。</summary>
    public static ManipulationVerdict Clean { get; } = new() { Signals = [] };

    /// <summary>指定シグナルで嫌疑ありとする。</summary>
    public static ManipulationVerdict Flag(IReadOnlyList<ManipulationSignal> signals) =>
        new() { Signals = signals };
}
