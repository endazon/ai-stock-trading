using AiStockTrading.Backtest.Application;
using AiStockTrading.Backtest.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Backtest.Infrastructure.Composable.Adapters;

// FR-15, ADR-0023 決定5, IADR-0157, #382: moomoo OpenAPI の履歴 K 線（QotRequestHistoryKL）による
// **米国株の日足 OHLCV** の取得。ADR-0023 決定5（利用者裁定 2026-08-06・環流 project-planning#205）が
// 米国株日足 OHLC の履歴源として moomoo を正式採用したことへの追随である。
//
// ⚠️⚠️ **本アダプタを構成しても、確認が済むまで本番のバックテストへ流してはならない。** ⚠️⚠️
// ADR-0023 決定5 は次の 2 点を「**本決定の前提であり、確認するまで本番のバックテストへ流さない**」と明記した。
// いずれも**実 OpenD でしか確認できず、本実装では解決していない**（LoadBarsAsync が毎回警告を出す）。
//
//   1. **取得枠の単位と回復周期。** `remainQuota: 300` が銘柄数かリクエスト数か分からない（PoC は 2 リクエストを
//      投げたが `usedQuota` は 0 のままだった）。**バックテストで多数銘柄を遡ると枠を使い切る可能性がある。**
//      枠が銘柄数単位であれば、バックテスト対象銘柄数の上限が制約になる。
//   2. **前復権（RehabType_Forward）の調整方式がバックテストの費用モデルと整合するか。** ADR-0016 決定14 は
//      借株料・配当相当額を織り込むと定めており、**復権方式と配当の扱いが二重計上や欠落を起こさないか**の確認が要る。
//
// **既定 provider は `none` のままである。** 本アダプタは `Backtest:BarData:Provider=moomoo` を
// **明示的に構成したときだけ**使われる（HistoricalBarSourceFactory の allow-list）。
//
// 取得の仕様（ADR-0023 決定5・2026-08-05 の PoC 実測に一致させる）:
//   - `KLType_Day` / **`RehabType_Forward`（前復権）**。復権を外すと分割を跨いで価格が不連続になる。
//   - **1 リクエスト 1,000 件が上限**。`NextReqKey` が返る限りページングする。
//     **止めると長期間の履歴が黙って切り詰められ、Stage 0 の判定（最大 DD・ウォークフォワード）が歪む。**
//   - 送信前に必ずレート制御を通す（IADR-0064: 429 を受けてから対処するのでは規約違反そのものを防げない）。
//   - 米国株以外・空の銘柄・OpenD の非成功応答は**銘柄ごとの欠測**（HistoricalBarGap）として残し、他銘柄は続行する。
//     欠測を無音破棄しないのは、バーが欠けたまま Stage 0 を合格させないためである（IHistoricalBarSource の契約）。
public sealed class MoomooHistoricalBarSource(
    IMoomooHistoryKLineClient client,
    IRateLimiter rateLimiter,
    ILogger<MoomooHistoricalBarSource> logger) : IHistoricalBarSource
{
    /// <summary>1 リクエストで要求する K 線の最大件数（ADR-0023 決定5: OpenD の上限は 1,000 件）。</summary>
    public const int MaxKLinesPerRequest = 1000;

    /// <summary>
    /// バックテストで用いる復権方式。**ADR-0023 決定5 が定めた前復権（RehabType_Forward）から変更しないこと。**
    /// </summary>
    public const MoomooKLineAdjustment Adjustment = MoomooKLineAdjustment.ForwardAdjusted;

    public async Task<HistoricalBarLoad> LoadBarsAsync(
        IReadOnlyList<(string Symbol, Market Market)> symbols,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        WarnUnconfirmedPreconditions();

        var bars = new List<PriceBar>();
        var gaps = new List<HistoricalBarGap>();

        foreach (var (symbol, market) in symbols)
        {
            var trimmed = (symbol ?? "").Trim();
            if (trimmed.Length == 0 || market != Market.UnitedStates)
            {
                // ADR-0023 決定5 が定めたのは**米国株**の日足 OHLC 履歴源である。日本株を勝手に足さず欠測として残す。
                Record(gaps, symbol ?? "", market, "moomoo の履歴 K 線は米国株のみを対象とする（ADR-0023 決定5）");
                continue;
            }

            var fetched = await FetchAllPagesAsync(trimmed, market, from, to, gaps, cancellationToken)
                .ConfigureAwait(false);
            if (fetched is null)
                continue; // 欠測は FetchAllPagesAsync が記録済み。半端に取れた分は捨てる（部分履歴で合格させない）。

            bars.AddRange(fetched);
        }

        return new HistoricalBarLoad(bars, gaps);
    }

    // ADR-0023 決定5: NextReqKey が返る限り繰り返す。1 リクエスト 1,000 件の上限で打ち切ると履歴が黙って切り詰められる。
    // 取得できなければ欠測を記録して null を返す（その銘柄のバーは 1 本も採らない）。
    private async Task<List<PriceBar>?> FetchAllPagesAsync(
        string symbol,
        Market market,
        DateOnly from,
        DateOnly to,
        List<HistoricalBarGap> gaps,
        CancellationToken cancellationToken)
    {
        var bars = new List<PriceBar>();
        byte[]? nextRequestKey = null;

        do
        {
            await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            MoomooHistoryKLinePage page;
            try
            {
                page = await client.RequestUsDailyKLinesAsync(
                    new MoomooHistoryKLineRequest(symbol, from, to, Adjustment, MaxKLinesPerRequest, nextRequestKey),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (MoomooHistoryKLineException ex)
            {
                // OpenD の非成功応答はその銘柄の欠測。通信例外・キャンセルは握りつぶさず送出する（Stooq と同方針）。
                Record(gaps, symbol, market, $"moomoo の履歴 K 線取得に失敗: {ex.Message}");
                return null;
            }

            // 期間指定を無視した応答（データ源の揺れ）を素通しさせない。
            bars.AddRange(page.KLines
                .Where(k => k.Date >= from && k.Date <= to)
                .Select(k => new PriceBar(symbol, market, k.Date, k.Open, k.High, k.Low, k.Close, k.Volume)));

            // 続きの鍵があっても 1 件も返らなかったページで打ち切る（進捗しないページングで回り続けない）。
            nextRequestKey = page.KLines.Count > 0 ? page.NextRequestKey : null;
        }
        while (nextRequestKey is not null);

        if (bars.Count == 0)
        {
            Record(gaps, symbol, market, "moomoo の履歴 K 線が 0 件（期間内のデータなし）");
            return null;
        }

        return bars;
    }

    // ADR-0023 決定5「実装側で確認を要する 2 点」。**構成した人が実行時に必ず目にする場所**として、
    // 取得のたびに警告を出す（コメント・仕様書だけでは、構成した人が読まなければ効かない）。
    private void WarnUnconfirmedPreconditions() =>
        logger.LogWarning(
            "moomoo の履歴 K 線から過去データを取得します（ADR-0023 決定5）。" +
            "**本番のバックテストへ流す前に未確認の 2 点を実 OpenD で確認してください**（決定5 の明文の前提）: " +
            "(1) 取得枠の単位と回復周期（remainQuota=300 が銘柄数かリクエスト数か。多数銘柄を遡ると枠を使い切り得る）、" +
            "(2) 前復権（RehabType_Forward）の調整方式が ADR-0016 決定14 の費用モデル（借株料・配当相当額）と" +
            "二重計上・欠落を起こさないか。詳細は IADR-0157 と docs/blocked-tasks.md A-3。");

    private void Record(List<HistoricalBarGap> gaps, string symbol, Market market, string reason)
    {
        // 欠測は無音破棄しない。バックテストの合否に影響する（銘柄が丸ごと欠けた検証は合格材料にできない）。
        logger.LogWarning(
            "moomoo から過去データを取得できませんでした（銘柄 {Symbol} / {Market}）: {Reason}。欠測として記録します。",
            symbol, market, reason);
        gaps.Add(new HistoricalBarGap(symbol, market, reason));
    }
}
