namespace AiStockTrading.Backtest.Infrastructure.Composable.Adapters;

// FR-15, ADR-0004, #208, IADR-0105: バックテストの過去データ源の構成（セクション "Backtest:BarData"）。
public sealed class BarDataOptions
{
    public const string SectionName = "Backtest:BarData";

    /// <summary>
    /// 過去データ源の選択。既定・空・"none" は no-op＝**外部へ 1 リクエストも出さない**。
    /// 現在の実装は "stooq" のみ（未知の値も安全既定の no-op へ倒す・IADR-0068 と同形）。
    /// <para>
    /// ADR-0023 / IADR-0156（#382）: **唯一の実装である "stooq" は現在取得不能**（ボット検知チャレンジ。
    /// 回避実装は計画が禁止）であり、代替源 moomoo は実測済みだが**採用も実装も未了**である。
    /// したがって本設定に指定できる有効な値は事実上存在せず、既定 none は恒久の状態である。
    /// **既定値をここで変更しないこと**（既定が none であることは HistoricalBarSourceFactoryTests が固定する）。
    /// </para>
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>Stooq（Provider="stooq"）の構成。</summary>
    public StooqBarDataOptions Stooq { get; set; } = new();
}

// #208, IADR-0105: Stooq は登録不要（API キーが無い）ため、opt-in の閂は Provider の明示指定そのものである。
public sealed class StooqBarDataOptions
{
    /// <summary>ベース URL。既定で足りるため通常は設定不要（テスト・将来の移行用）。不正な URL は no-op へ倒す。</summary>
    public string BaseUrl { get; set; } = StooqHistoricalBarSource.DefaultBaseUrl;

    /// <summary>
    /// レート予算（回/分）。既定 10。Stooq は上限を公表しておらず個人運営で SLA も無いため、
    /// 保守的な既定で自制する（IADR-0064 と同じ「送信前に自制する」方針）。0 以下は 1 回/分へクランプする。
    /// </summary>
    public int RequestsPerMinute { get; set; } = 10;
}
