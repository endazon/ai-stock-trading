using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, FR-05, IADR-0018: 取引台帳の 1 約定（承認 Intent と OrderExecuted を DecisionId で相関して補完済み）。
// 射影（PortfolioProjection）の入力。銘柄・市場・約定方向・建玉効果・約定数量・約定単価・約定時刻を持つ。
// Quantity は約定数量（>0）、Price は約定単価（**銘柄の市場の通貨**＝ローカル通貨。IADR-0107 決定1）。
// StopLossPrice は承認 Intent 由来の損切り価格（IADR-0035・nullable＝レガシー/機械執行 Close は null）。
// FxRateToBase は承認 Intent 由来の基準通貨（USD）への換算レート（IADR-0107 決定2＝約定時レートの近似）。
// 既定 1＝基準通貨市場（米国株）。金額集計はこのレートで基準通貨へ揃える（#364・IADR-0152 決定1）。
// FR-06, FR-16, #563, IADR-0269: DecisionId は承認・約定を結ぶ相関キー（台帳は TradeFillRow に保持している）。
// 報告書の日報 §2「判断根拠（要約）」が、監査台帳の TradeDecisionMade を**この鍵で**引くために公開する。
// 既定 `default`（Guid.Empty）＝相関できない（レガシー行）。**推測で埋めない**——判断根拠は未供給になる。
public sealed record LedgerFill(
    string Symbol,
    Market Market,
    TradeSide Side,
    PositionEffect PositionEffect,
    int Quantity,
    decimal Price,
    DateTimeOffset ExecutedAt,
    decimal? StopLossPrice = null,
    decimal FxRateToBase = 1m,
    Guid DecisionId = default,
    // FR-06, FR-15, FR-20, #569, IADR-0149 決定1, IADR-0271: **実際に発注したアダプタの発注先**。
    // 月報 §5 の三者比較（バックテスト / SIMULATE / 実弾）が段を分ける鍵である。
    // **既定 null＝発注先不明（本列の追加前に記録された行）。推定で埋めない**——
    // 不明の約定はどちらの段にも算入しない。
    BrokerProvider? Provider = null,
    // FR-06, FR-16, #611, IADR-0286 決定1: 承認時点の**認識時レート**（基準通貨〔USD〕1 単位あたりの表示通貨〔JPY〕額
    // ＝1 USD あたりの円）。報告書の為替差損益（認識時レートと期末レートの差）の根。FxRateToBase（ローカル通貨→USD）
    // とは軸が違い、米国株では後者が 1 で円の情報を持たない。
    // **既定 null＝未記録（列追加前の行・承認時に為替レート源が解決できなかった行）。推定で埋めない**——
    // 報告書は当該約定を含む期間の為替差損益を未供給とし、未記録の件数を明記する。
    decimal? FxRateBaseToDisplay = null,
    // FR-11, FR-10, #849, #870, ADR-0041 決定 1, IADR-0350 決定 2/3, IADR-0360 決定 1: この行の**由来**
    // ——「**誰が約定させたか**」を表す軸である。🔴 **経費区分（TradeExpenseCategory）とは別の軸である**
    // （区分は「何の費用か」、由来は「誰が約定させたか」。混ぜない）。
    // TradeOrigin.ManualAdoption は**約定ではなく、利用者が承認した乖離の取り込み行**である。
    // システム外の売買は約定価格が分からないため、この行は**数量だけ**を運ぶ。射影は Price を使わず、
    // **その時点の平均取得単価で在庫だけを減らす**（実現損益 0 を構造的に保証する。PortfolioProjection.ApplyToLot）。
    // Price は取り込み時点の台帳の平均取得単価（参考）であり、**約定価格ではない**。
    // 本列は wire へ載せるが、🔴 **GET /risk-controls/fills に出る値は常に System である**
    // ——同経路は PeriodFillQuery が取り込み行そのものを除外するため、ManualAdoption の行は 1 件も通らない。
    // **監査で由来を読む手段は GET /risk-controls/drift-adoptions（DriftAdoptionView.Origin）が担う。**
    // ここで wire へ出す意味は「由来が 1 級の列である」という軸の表明に留まる（IADR-0360 決定 1・2026-09-19 の監査）。
    TradeOrigin Origin = TradeOrigin.System,
    // FR-10, FR-03, #936, IADR-0393（2026-09-25 追記）: この約定が属する**承認の時刻**（承認 Intent の ApprovedAt）。
    // 射影が保有中のエントリー（ロット）を並べる鍵である —— 発注執行は外部要因の減少を S1 の行の**作成時刻**
    // （＝承認を受けて発注した時刻）の古い順に割り当てるため、台帳も約定時刻ではなく発注の順でロットを並べる。
    // 🔴 **既定 null＝承認時刻が分からない**（乖離の取り込み行など承認を持たない行）。射影は約定時刻で代える。
    // **wire へは出さない**（射影の内部の鍵であり、報告書の入力ではない）。
    [property: System.Text.Json.Serialization.JsonIgnore] DateTimeOffset? EntryOrderedAt = null)
{
    /// <summary>基準通貨（USD）建ての約定単価。金額集計・実現損益・エクイティはこの単価で積む。**永続化しない計算値**である。</summary>
    public decimal PriceInBase => Price * FxRateToBase;

    /// <summary>
    /// 由来が<b>手動売買による取り込み</b>か（<see cref="Origin"/> からの導出）。
    /// 読み手（射影・期間約定の照会・強制買戻しの推定）は本述語で分岐する。**永続化も wire への露出もしない**
    /// ——軸そのものは <see cref="Origin"/> が運ぶ（同じ事実を 2 つの名前で wire へ出さない）。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDriftAdoption => Origin == TradeOrigin.ManualAdoption;
}
