namespace AiStockTrading.RiskManagement.Domain.Manipulation;

// FR-19, IADR-0037: 相場操縦検知の窓長・最小標本数・各しきい値。既定値と逆算根拠は IADR-0037。
// 現状は TradingDefaults の静的既定のみで、実行時の変更経路は持たない（生成AI・自動処理が改ざんできない安全側）。
// 運用ログでの較正は当面コード変更＋再デプロイで行い、設定ストア化は後続で判断する（IADR-0037 のフォローアップ）。
public record ManipulationDetectionSettings
{
    /// <summary>判定に用いる直近窓の長さ。</summary>
    public required TimeSpan LookbackWindow { get; init; }

    /// <summary>これ未満の発注数の窓は常に無嫌疑（低頻度の正常取引での誤検知を防ぐ安全側ガード）。</summary>
    public required int MinimumSampleSize { get; init; }

    /// <summary>過剰取消: 約定なし取消数 / 発注数 がこの比率を超えると該当。</summary>
    public required decimal MaxCancellationRatio { get; init; }

    /// <summary>過剰訂正: 訂正総数 / 発注数 がこの値を超えると該当。</summary>
    public required decimal MaxAmendmentsPerOrder { get; init; }

    /// <summary>見せ玉: 約定/一部約定数 / 発注数 がこの比率未満だと約定意思が希薄とみなす。</summary>
    public required decimal MinFillRatio { get; init; }

    /// <summary>見せ玉: 発注→取消の生存時間がこの値以下の約定なし取消を「短命取消」とみなす。</summary>
    public required TimeSpan ShortLivedCancelThreshold { get; init; }

    /// <summary>見せ玉: 短命取消がこの件数以上（かつ低約定率）で該当。</summary>
    public required int MaxShortLivedCancels { get; init; }

    /// <summary>板演出: 同一方向の約定なし取消注文の同時生存本数がこの値以上で該当。</summary>
    public required int LayeringOrderCount { get; init; }
}
