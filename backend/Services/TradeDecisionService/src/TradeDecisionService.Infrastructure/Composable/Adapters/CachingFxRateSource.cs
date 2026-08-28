using System.Collections.Concurrent;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Application.Ports;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Infrastructure.Adapters;

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
    ILogger<CachingFxRateSource> logger,
    IFxSourceStatusNotifier? statusNotifier = null)
    : IFxRateSource
{
    private readonly ConcurrentDictionary<Currency, (FxRate Rate, DateTimeOffset FetchedAt)> _cache = new();

    private readonly IFxSourceStatusNotifier _statusNotifier = statusNotifier ?? NoOpFxSourceStatusNotifier.Instance;

    /// <summary>
    /// 鮮度切れを <c>null</c> へ潰した結果を返す（新規建ての既定経路）。
    /// <para>
    /// 🔴 <b>ここで鮮度を判定し直さない。</b> <see cref="GetReadingAsync"/> の結果から導く——
    /// 二本立てにすると<b>片方だけ直して静かにずれる</b>（IADR-0197 決定3）。
    /// </para>
    /// </summary>
    public async Task<FxRate?> GetRateToBaseAsync(Currency quote, CancellationToken cancellationToken = default)
    {
        var reading = await GetReadingAsync(quote, cancellationToken).ConfigureAwait(false);
        return reading is { UsableForEntry: true } ? reading.Rate : null;
    }

    /// <summary>
    /// レートと鮮度の判定結果を返す（#506・IADR-0197 決定1・決定2）。
    /// <para>
    /// 🔴 <b>鮮度切れ（30 日超）でも値そのものは返す。</b> 計画 ADR-0022 決定5 は
    /// 「新規建てを停止する。<b>手仕舞い・損切りは止めない</b>」と定めており、
    /// <b>出口には古いレートでも実在する値が要る</b>——ここで <c>null</c> にすると、
    /// 呼び出し側が決済へ既定の <c>1m</c> を載せることになり、
    /// <b>監査台帳の金額が約 150 倍ずれる</b>（JPY を USD として記録する）。
    /// </para>
    /// </summary>
    public async Task<FxRateReading?> GetReadingAsync(Currency quote, CancellationToken cancellationToken = default)
    {
        // ポート契約: 基準通貨は外部へ問い合わせず必ずレート 1（観測ではないため鮮度判定もしない）。
        if (quote == MarketCurrency.Base)
            return new FxRateReading(FxRate.Identity(quote), FxRateFreshness.Fresh);

        var now = timeProvider.GetUtcNow();
        if (_cache.TryGetValue(quote, out var cached) && now - cached.FetchedAt < ttl)
            return new FxRateReading(cached.Rate, Classify(now - cached.Rate.AsOf));

        var rate = await inner.GetRateToBaseAsync(quote, cancellationToken).ConfigureAwait(false);
        if (rate is null)
            return null;

        // 齢は停止（maxRateAge）と警告（staleRateWarning）の両方が同じ観測を見て判断する。
        var age = now - rate.AsOf;
        var freshness = Classify(age);

        if (freshness == FxRateFreshness.Expired)
        {
            logger.LogWarning(
                "為替レートの観測が古いため新規建てには採用しません（{Quote}: 観測日 {AsOf} / 上限 {MaxAgeDays} 日）。" +
                "**手仕舞いは止めません**（ADR-0022 決定5・#506）。",
                quote, rate.AsOf, maxRateAge.TotalDays);

            // #381 停止側 / IADR-0198 決定1: **停止側も可視化する。** 従来はここで return しており、
            // **統制が発動した事実がログ以外どこにも出ていなかった**（ADR-0022 決定2「黙って劣化させない」）。
            await NotifyStaleAsync(quote, rate.AsOf, age, cancellationToken, entryBlocked: true).ConfigureAwait(false);

            // 🔴 **鮮度切れはキャッシュしない**（従来どおり）。一時障害を TTL のあいだ引きずると回復後も続くため。
            return new FxRateReading(rate, freshness);
        }

        // 警告域（警告しきい値超〜上限以下）: **値は返す**（新規建ては止めない）。気づくために警告だけ出す。
        if (freshness == FxRateFreshness.Warning)
        {
            logger.LogWarning(
                "為替レートの観測が古くなっています（{Quote}: 観測日 {AsOf} / 経過 {AgeDays:0.#} 日 / " +
                "警告 {WarnDays} 日 / 停止 {MaxDays} 日）。**直近レートで続行します**（新規建ては止めません）。" +
                "上限を超えると非基準通貨の新規建てが見送られます（ADR-0022 決定4・IADR-0174）。",
                quote, rate.AsOf, age.TotalDays, staleRateWarning.TotalDays, maxRateAge.TotalDays);

            // ADR-0022 決定5 / #381 第 2 層: 警告の宛先はログだけではない。監査・Discord・日報へ広げる
            // （IADR-0196）。暦日単位の抑止は通知側が持つため、**警告域にいる間は毎回呼んでよい**。
            await NotifyStaleAsync(quote, rate.AsOf, age, cancellationToken).ConfigureAwait(false);
        }

        _cache[quote] = (rate, now);
        return new FxRateReading(rate, freshness);
    }

    /// <summary>齢を 3 段の判定へ写す。**判定規則をここ 1 箇所に置く**（決定3）。</summary>
    private FxRateFreshness Classify(TimeSpan age) =>
        age > maxRateAge ? FxRateFreshness.Expired
        : age > staleRateWarning ? FxRateFreshness.Warning
        : FxRateFreshness.Fresh;

    /// <summary>
    /// 鮮度警告を可視化経路へ報告する。
    /// <para>
    /// 🔴 <b>可視化の失敗でレートを捨てない。</b> ここは「値は返す（新規建ては止めない）」と決めた経路であり
    /// （ADR-0022 決定5）、<b>通知が出せないことを理由に値を捨てると、警告が停止へ化ける。</b>
    /// 例外は飲み込むが、<b>飲み込んだ事実はログへ残す</b>。
    /// </para>
    /// </summary>
    private async Task NotifyStaleAsync(
        Currency quote,
        DateTimeOffset asOf,
        TimeSpan age,
        CancellationToken cancellationToken,
        bool entryBlocked = false)
    {
        try
        {
            await _statusNotifier
                .ReportStaleAsync(
                    CurrencyFormat.CodeOf(quote), asOf, age, staleRateWarning, maxRateAge, cancellationToken,
                    entryBlocked)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "為替レートの鮮度警告を可視化経路へ報告できませんでした（{Quote}）。" +
                "**この警告は監査・通知・日報に現れません**（値は返しており新規建ては止めていません）。",
                quote);
        }
    }
}
