using AiStockTrading.Configuration.Domain;
using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Report.Application.Services;

// FR-06/07/16, 04_report-templates, IADR-0032: 報告書ドラフトの生成オーケストレーション（日報/週報/月報）。
// 約定列＋前提条件から数値をコード集計（PnlAggregator）し、散文は LLM ドラフト（IReportNarrativeDrafter）で得て、
// ReportRenderer でテンプレートへ組み立てる（数値は LLM に計算させない）。生成のみでステートレス（永続化しない）。
public sealed class ReportDraftService(IReportNarrativeDrafter drafter)
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

        // 数値はコード集計（FR-16）。前提条件は暫定で既定値（#19 バージョン付き取得・#63 台帳連携は #22 後続）。
        var pnl = PnlAggregator.Aggregate(fills, TradingAssumptionsDefaults.Create(), request.CurrentPrices);

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
