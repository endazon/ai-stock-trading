using System.Collections.Concurrent;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;

// FR-10, #257, #364, IADR-0107 決定5: 「そのレートを取りに行くか」「そのレートを使ってよいか」を決める装飾。
//   - TTL: 取得済みレートの再利用時間。日次系列（DEXJPUS）を判断サイクルごとに叩かない。
//   - 鮮度上限: 観測日が古すぎるレートは採らない（null＝レート無し＝非基準通貨の新規建て見送り）。
//   - 鮮度警告: 上限には届かないが古いレートは**採ったうえで警告する**（#381・IADR-0174 決定1）。
//
// FR-10, FR-17, ADR-0022 決定4・5: 計画 §5 の縮退は 3 段である——**5 日以下＝通常運用 ／ 5 日超〜30 日以下＝
// 直近レートで続行し警告（新規建ては止めない）／ 30 日超＝新規建てを停止**（手仕舞い・損切りは止めない）。
// **警告と停止は役割が違う**——前者は気づくため、後者は統制が意味を失った状態で発注しないためである。
// 旧実装は上限だけを持っており、この 2 つが同じ値に潰れていた。
// 失敗（null）・鮮度切れはキャッシュしない。一時障害を TTL のあいだ引きずると、回復後も見送りが続くため。
internal sealed class CachingFxRateSource(
    IFxRateSource inner,
    TimeSpan ttl,
    TimeSpan maxRateAge,
    TimeSpan staleRateWarning,
    TimeProvider timeProvider,
    ILogger<CachingFxRateSource> logger)
    : IFxRateSource
{
    private readonly ConcurrentDictionary<Currency, (FxRate Rate, DateTimeOffset FetchedAt)> _cache = new();

    public async Task<FxRate?> GetRateToBaseAsync(Currency quote, CancellationToken cancellationToken = default)
    {
        // ポート契約: 基準通貨は外部へ問い合わせず必ずレート 1（観測ではないため鮮度判定もしない）。
        if (quote == MarketCurrency.Base)
            return FxRate.Identity(quote);

        var now = timeProvider.GetUtcNow();
        if (_cache.TryGetValue(quote, out var cached) && now - cached.FetchedAt < ttl)
            return cached.Rate;

        var rate = await inner.GetRateToBaseAsync(quote, cancellationToken).ConfigureAwait(false);
        if (rate is null)
            return null;

        // 齢は停止（maxRateAge）と警告（staleRateWarning）の両方が同じ観測を見て判断する。
        var age = now - rate.AsOf;

        if (age > maxRateAge)
        {
            logger.LogWarning(
                "為替レートの観測が古いため採用しません（{Quote}: 観測日 {AsOf} / 上限 {MaxAgeDays} 日）。" +
                "当該通貨建て銘柄の新規建ては見送られます（IADR-0107）。",
                quote, rate.AsOf, maxRateAge.TotalDays);
            return null;
        }

        // 警告域（警告しきい値超〜上限以下）: **値は返す**（新規建ては止めない）。気づくために警告だけ出す。
        if (age > staleRateWarning)
        {
            logger.LogWarning(
                "為替レートの観測が古くなっています（{Quote}: 観測日 {AsOf} / 経過 {AgeDays:0.#} 日 / " +
                "警告 {WarnDays} 日 / 停止 {MaxDays} 日）。**直近レートで続行します**（新規建ては止めません）。" +
                "上限を超えると非基準通貨の新規建てが見送られます（ADR-0022 決定4・IADR-0174）。",
                quote, rate.AsOf, age.TotalDays, staleRateWarning.TotalDays, maxRateAge.TotalDays);
        }

        _cache[quote] = (rate, now);
        return rate;
    }
}
