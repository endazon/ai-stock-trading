namespace AiStockTrading.Shared.Contracts.Trading;

// FR-10, FR-11, FR-19, FR-20: 発注拒否の理由コード。監査ログ・Discord 通知で利用する
public enum RejectionReason
{
    KillSwitchActive,
    StageProhibitsLiveTrading,
    StageCapitalCapExceeded,
    ProductTypeDisabled,
    MarketDisabled,
    BannedSymbol,
    SameDayReentry,
    PerOrderAmountExceeded,
    DailyOrderAmountExceeded,
    MaxPositionsExceeded,
    DailyLossLimitReached,
    MaxDrawdownReached,

    /// <summary>相場操縦とみなされ得る発注パターン（約定意思のない発注・板演出・過剰な訂正/取消）。FR-19。</summary>
    ManipulativeOrderPattern,

    /// <summary>利用者による取引の一時停止（pause）中。新規建てのみ停止し手仕舞い・損切りは止めない。FR-10, ADR-0009。</summary>
    TradingPaused,

    // --- 空売り専用の拒否理由 7 種（FR-10, ADR-0016 決定10。#329 第 2 段階） ---
    // **7 種すべてクラス A**（統制が設計どおり作動した記録）である。「統制違反 0 件」の計上対象は
    // クラス C（BannedSymbol / ManipulativeOrderPattern）限定であり（project-planning#58 の裁定）、
    // 7 種はその件数に影響しない。分類は RejectionReasonClassification が単一情報源である。

    /// <summary>空売りが無効に設定されている（既定は現物のみ）。FR-10, ADR-0016 決定1/13。</summary>
    ShortSellDisabled,

    /// <summary>借株できない（locate 失敗・借株料を事前照会できない・強制買戻し後の禁止期間中）。ADR-0016 決定3/4。</summary>
    BorrowUnavailable,

    /// <summary>借株料が年率 20% を超える。ADR-0016 決定3。</summary>
    BorrowCostExceeded,

    /// <summary>空売り建玉の上限（1 銘柄あたり equity の 10% / 空売り比率 50%）を超える。ADR-0016 決定2(a)/決定9。</summary>
    ShortExposureExceeded,

    /// <summary>維持率が閾値（40% と規制要求の厳しい方）を割り込む。ADR-0016 決定7。</summary>
    MaintenanceMarginBreach,

    /// <summary>権利確定日が近い（前日の新規空売りは禁止）。ADR-0016 決定5。</summary>
    DividendRecordDateNear,

    /// <summary>株価が $5.00 未満（空売りの対象外）。ADR-0016 決定7。**BannedSymbol で表現してはならない**。</summary>
    ShortPriceFloorBreach,

    /// <summary>
    /// 逆指値（ストップ注文）を建玉と同時に発注できない。FR-10「逆指値が未受理・失効した場合、および
    /// 逆指値を受け付けない銘柄・時間帯では建玉を持たない」・ADR-0016 決定2(b)。
    /// <para>
    /// **計画との差異（#329 第 2 段階・IADR-0131 決定3）**: 本規則は FR-10 本文と ADR-0016 決定2(b) が
    /// 明示的に課しているが、ADR-0016 決定10 の拒否理由 7 種には対応するコードが無い。規則を実装しないと
    /// 「逆指値なしの空売り」が素通りするため、実装側で 1 種を新設した。クラス分類は**クラス A**とし、
    /// コード名の追認は計画へ環流する（`feedback/` 参照）。
    /// </para>
    /// </summary>
    StopOrderRequired,
}
