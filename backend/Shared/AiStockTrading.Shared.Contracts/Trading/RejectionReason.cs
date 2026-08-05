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

    // --- 空売り専用の拒否理由 9 種（FR-10, ADR-0016 決定10。#329 第 2 段階・#374 で 7 種から改訂） ---
    // **9 種すべてクラス A**（統制が設計どおり作動した記録）である。「統制違反 0 件」の計上対象は
    // クラス C（BannedSymbol / ManipulativeOrderPattern）限定であり（project-planning#58 の裁定）、
    // 9 種はその件数に影響しない。分類は RejectionReasonClassification が単一情報源である。
    //
    // **メンバの序数は不変**である。拒否理由は HTTP 経路で整数として往来する（段階ゲートの拒否理由）ため、
    // 既存メンバの間へ挿入すると過去の記録の意味が変わる。**新設は常に末尾へ追加する**（IADR-0134 決定2）。

    /// <summary>空売りが無効に設定されている（既定は現物のみ）。FR-10, ADR-0016 決定1/13。</summary>
    ShortSellDisabled,

    /// <summary>
    /// 借株できない（locate 失敗・借株料を事前照会できない）。ADR-0016 決定3。
    /// <para>
    /// **強制買戻しの 30 日禁止期間中はここへ写像しない**（<see cref="BuyInBanned"/> を用いる。
    /// ADR-0016 決定10 の 2026-08-04 追記）。本理由は**都度の借株需給**による locate 失敗であり、
    /// 期間の経過では解除されない。
    /// </para>
    /// </summary>
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
    /// #329 第 2 段階で実装側が先行して新設したコードであり（IADR-0131 決定3・計画へ環流）、
    /// **2026-08-04 に計画側が同名で追認した**（ADR-0016 決定10 の改訂・project-planning#178）。
    /// クラス分類は**クラス A**である。
    /// </para>
    /// </summary>
    StopOrderRequired,

    /// <summary>
    /// 強制買戻し（buy-in）の発生により **30 日間の空売り禁止期間中**である。ADR-0016 決定4/決定10。
    /// <para>
    /// **<see cref="BorrowUnavailable"/> へ写像してはならない**（ADR-0016 決定10 の 2026-08-04 追記）。
    /// `BorrowUnavailable` は都度の借株需給による locate 失敗であり、本理由は**期間の経過**で解除される
    /// 禁止状態である。原因も解除条件も異なるため、写像すると監査ログ（FR-11）の理由が実態と食い違い、
    /// 原因究明が壊れる。決定15 が日報・月報へ「強制買戻しの発生有無・発生回数」を求めている以上、
    /// 区別できることに実益がある。
    /// </para>
    /// <para>
    /// **<see cref="BannedSymbol"/>（クラス C）にも混ぜない**。30 日リストは利用者が登録する禁止銘柄
    /// リストとは別の**空売り専用リスト**であり、強制買戻しは借株需給の逼迫という市況由来の事象である。
    /// クラス C へ混入させると段階昇格ゲート（「統制違反 0 件」）が機能しなくなる。
    /// </para>
    /// </summary>
    BuyInBanned,

    // --- 段階別の商品種別強制（FR-20, ADR-0016 決定8・決定14。#333） ---
    // **クラス B**（段階制約による拒否）である。統制違反 0 件（クラス C 限定）の件数には影響しない。
    // 設定による商品種別の無効化（ProductTypeDisabled）とは**別の規則**である——前者は利用者設定、
    // 本 2 種は段階ゲートが課す強制であり、設定で有効にしても段階が許さなければ通らない。

    /// <summary>
    /// FR-20, ADR-0016 決定8: **その運用段階では当該商品種別の新規建てを行わない。**
    /// Stage 2（最小実弾）は現物のみであり、信用買い・空売りは Stage 3 からである。
    /// <para>
    /// **適用は新規建てのみ**（project-planning#179 の裁定）。手仕舞い・損切りは止めない——
    /// 段階を上げる前に建てた建玉を閉じられないと、FR-10 の不変条件（ADR-0009）に反する。
    /// </para>
    /// </summary>
    StageProductTypeProhibited,

    /// <summary>
    /// FR-20, ADR-0016 決定8・決定14: **Stage 3 の空売り実弾解禁条件を満たしていない。**
    /// 条件は「1 銘柄あたりの空売り上限が $500 以上（＝**自己資金 $5,000 以上**）」**かつ**
    /// 「空売りを含む戦略で **Stage 0 の 7 条件を再度満たす**」の**両方**である。
    /// <para>
    /// <see cref="StageProductTypeProhibited"/> と分けるのは、原因も解除条件も異なるためである——
    /// 前者は「段階が上がるまで不可」、本理由は「Stage 3 に居るが解禁条件が未充足」である。
    /// 畳むと監査ログ（FR-11）の理由が実態と食い違う（ADR-0016 決定10 の 2026-08-04 追記と同じ規律）。
    /// </para>
    /// </summary>
    StageShortSellReleaseUnmet,
}
