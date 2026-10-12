using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

// FR-04, FR-15, FR-07, ADR-0033 決定2, ADR-0036 決定1, #1308, IADR-0528: Stage 0 の記録器の as-of 入力の**基底**の供給。
//
// 監視銘柄（(e)）・出来高・ATR は外側のデコレータが埋める（IADR-0442・0479・0486）。ここは価格・方針・参考情報・為替・サイジングを組む。
// すべての入力を判断時点 t（判断日の通常取引の終値の時刻。`Stage0DecisionInstant`）で切る。
//
//   - (a) 価格: 判断日の足の終値を参照価格、それより前の最後の終値を前日終値（決定2）。判断日の足が無い（休場）なら null。
//   - (c) 方針: t までに確定した日報の最新（決定3）。無ければ null（本番も確定日報が無ければ取引しない。FR-07。利用者裁定 2026-10-13 の 1）。
//     方針の日付が判断日より後なら null（型が未来の方針を例外にするため、受け取る前に落とす）。
//   - (b) 参考情報: 3 源を本番の取得規則で組む（決定4）。必須の源が取れなければ (b) を再構成不可と申告し、欠けた源を理由欄に残す。
//   - (d) 為替: 基準通貨の市場だけ（レート 1。決定5）。他の市場は null（未対応）。
//   - サイジング: 固定値（決定6）。
// 🔴 **照会の失敗は null に倒さない**（`AsOfInputUnavailableException` を投げ、記録を止める）。null は「当時無かった」だけに使う。
public sealed class BaseAsOfDecisionInputProvider(
    IAsOfPriceSource prices,
    IAsOfDailyPolicySource policies,
    AsOfReferenceAssembler references,
    ILogger<BaseAsOfDecisionInputProvider> logger)
    : IAsOfDecisionInputProvider
{
    public async Task<AsOfDecisionInput?> GetAsync(
        string symbol, Market market, DateOnly asOf, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        if (!MarketCurrency.IsBaseCurrency(market))
        {
            logger.LogWarning(
                "Stage 0 の as-of 入力: {Symbol}（{Market}）は基準通貨の市場ではありません。当時の為替レートを供給しないため判断しません。",
                symbol, market);
            return null;
        }

        if (Stage0DecisionInstant.Of(market, asOf) is not { } at)
        {
            logger.LogWarning("Stage 0 の as-of 入力: {Market} の判断時点を決められません（通常取引の終値の時刻が無い）。", market);
            return null;
        }

        var price = await prices.GetAsync(symbol, market, asOf, cancellationToken).ConfigureAwait(false);
        if (price.Close is not { } close)
            return null; // 判断日の足が無い（休場）。判断を発明しない。
        if (close.Date != asOf)
            throw new AsOfInputUnavailableException($"価格の口が判断日 {asOf} ではなく {close.Date} の足を参照価格として返しました（{symbol}）。");

        var policy = await policies.GetAsync(at, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            logger.LogInformation(
                "Stage 0 の as-of 入力: {AsOf} の判断時点 {At} までに確定した日報がありません（本番も取引しない）。{Symbol} は判断しません。",
                asOf, at, symbol);
            return null;
        }

        if (policy.Date > asOf)
        {
            logger.LogWarning(
                "Stage 0 の as-of 入力: 判断時点 {At} の方針の日付 {PolicyDate} が判断日 {AsOf} より後です。判断しません。",
                at, policy.Date, asOf);
            return null;
        }

        var set = await references.AssembleAsync(symbol, market, at, cancellationToken).ConfigureAwait(false);
        Stage0AsOfInputKind[] declared = set.IsComplete ? [] : [Stage0AsOfInputKind.NewsAndDisclosures];

        return new AsOfDecisionInput(
            asOf,
            policy,
            Stage0AsOfSizing.Create(),
            close,
            set.Contexts,
            rateToBase: 1m,
            notReconstructable: declared,
            previousClose: price.PreviousClose,
            newsAndDisclosuresNote: set.Note());
    }
}
