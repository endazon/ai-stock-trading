using AiStockTrading.Configuration.Domain;
using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Domain;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Report.Application.Services;

// FR-06/07/16, 04_report-templates, IADR-0032: 報告書ドラフトの生成オーケストレーション（日報/週報/月報）。
// 約定列＋前提条件から数値をコード集計（PnlAggregator）し、散文は LLM ドラフト（IReportNarrativeDrafter）で得て、
// ReportRenderer でテンプレートへ組み立てる（数値は LLM に計算させない）。生成のみでステートレス（永続化しない）。
//
// #81, IADR-0065: 評価損益（税引前・参考）の現在値は、要求が指定していないときだけ市場データ源から補完する
// （要求指定は上書きしない＝既存 API 互換）。marketData 未注入・取得不可なら現在値なし＝評価損益 0（現行挙動）。
public sealed class ReportDraftService(IReportNarrativeDrafter drafter, IMarketDataSource? marketData = null)
{
    public async Task<ReportDraft> BuildDraftAsync(DraftRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PeriodKey);

        // periodKey と種別/対象日の不整合を弾く（Date を正とする）。不整合な報告書が後続の永続化/KB 保存で残らないようにする。
        var periodLabel = ReportPeriod.Label(request.Kind, request.Date);
        var expectedKey = ReportPeriod.ExpectedKey(request.Kind, request.Date);
        if (!string.Equals(request.PeriodKey, expectedKey, StringComparison.Ordinal))
            throw new ArgumentException($"{request.Kind} 報告書の PeriodKey は種別・対象日と一致する必要があります（期待 '{expectedKey}'・実際 '{request.PeriodKey}'）。");

        var markets = request.Markets ?? [];
        var fills = request.Fills ?? [];

        // 評価損益の現在値: 要求指定が最優先。無ければ市場データ源から補完する（#81・IADR-0065）。
        var currentPrices = request.CurrentPrices
            ?? await ResolveCurrentPricesAsync(fills, cancellationToken).ConfigureAwait(false);

        // 数値はコード集計（FR-16）。前提条件は暫定で既定値（#19 バージョン付き取得・#63 台帳連携は #22 後続）。
        var pnl = PnlAggregator.Aggregate(fills, TradingAssumptionsDefaults.Create(), currentPrices);

        var buyCount = fills.Count(f => f.Side == TradeSide.Buy);
        var sellCount = fills.Count(f => f.Side == TradeSide.Sell);

        // 散文のみ LLM ドラフトへ委ねる（数値は提示のみで再計算させない）。
        var narrative = await drafter
            .DraftNarrativeAsync(new ReportNarrativeContext(request.Kind, request.PeriodKey, periodLabel, markets, pnl, request.PolicySummary), cancellationToken)
            .ConfigureAwait(false);

        var view = new ReportView
        {
            Kind = request.Kind,
            PeriodKey = request.PeriodKey,
            PeriodLabel = periodLabel,
            Markets = markets,
            AssumptionsVersion = request.AssumptionsVersion,
            BasedOn = request.BasedOn,
            ConfirmedAt = null, // ドラフトは未確定
            Pnl = pnl,
            BuyCount = buyCount,
            SellCount = sellCount,
            PolicySummary = request.PolicySummary,
            Narrative = narrative,
        };

        return new ReportDraft(ReportRenderer.RenderMarkdown(view), pnl);
    }

    // #81, IADR-0065: 期間末に建玉が残る銘柄の現在値を市場データ源から引く（全決済済みは評価に不要なので引かない
    // ＝無駄な市況取得でレート制限を消費しない）。取得不可の銘柄はキーを含めない＝当該建玉の評価損益は 0。
    //
    // PnlAggregator の現在値辞書は**銘柄コードのみ**がキーで市場を持たない（IADR-0025）。そのため同一銘柄コードが
    // 複数市場に現れる場合は市場を判別できず、**曖昧としてキーを落とす**（誤った市場の価格で評価するより 0 に倒す）。
    private async Task<IReadOnlyDictionary<string, decimal>?> ResolveCurrentPricesAsync(
        IReadOnlyList<PeriodTradeFill> fills,
        CancellationToken cancellationToken)
    {
        if (marketData is null || fills.Count == 0)
            return null;

        // IADR-0033: 平均取得単価法の畳み込みは共有の純関数（SignedInventory）を単一情報源とする。
        // ここでは建玉の有無（数量 ≠ 0）だけが要るため、符号付き在庫のみを畳み込む。
        var inventory = new Dictionary<(string Symbol, Market Market), InventoryLot>();
        foreach (var fill in fills.OrderBy(f => f.ExecutedAt))
        {
            var key = (fill.Symbol, fill.Market);
            var signedQ = fill.Side == TradeSide.Buy ? fill.Quantity : -fill.Quantity;
            inventory.TryGetValue(key, out var lot);
            inventory[key] = SignedInventory.Apply(lot, signedQ, fill.Price).Lot;
        }

        var open = inventory.Where(e => e.Value.Quantity != 0).Select(e => e.Key).ToList();

        // 銘柄コードが複数市場にまたがるものは判別できないため対象から外す。
        var ambiguous = open.GroupBy(k => k.Symbol).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

        var prices = new Dictionary<string, decimal>();
        foreach (var (symbol, market) in open)
        {
            if (ambiguous.Contains(symbol))
                continue;

            var quote = await marketData.GetLatestQuoteAsync(symbol, market, cancellationToken).ConfigureAwait(false);
            if (quote is not null)
                prices[symbol] = quote.Price;
        }

        return prices;
    }
}

// 報告書ドラフト生成の要求。Kind で日報/週報/月報を切り替える。Fills は集計対象の約定列（#63 台帳の実データ連携は #22 後続）。
public sealed record DraftRequest(
    ReportKind Kind,
    string PeriodKey,
    DateOnly Date,
    IReadOnlyList<string>? Markets,
    int AssumptionsVersion,
    string? BasedOn,
    string PolicySummary,
    IReadOnlyList<PeriodTradeFill>? Fills,
    IReadOnlyDictionary<string, decimal>? CurrentPrices);

// 生成結果（Markdown 本文＋集計した数値サマリ）。永続化はしない（本スライス）。
public sealed record ReportDraft(string Markdown, PnlSummary Pnl);
