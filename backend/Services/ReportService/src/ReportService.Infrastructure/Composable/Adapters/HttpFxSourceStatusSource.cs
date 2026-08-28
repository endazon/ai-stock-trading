using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Report.Infrastructure.Composable.Adapters;

// FR-06, FR-10, FR-11, UC-06, #381, ADR-0022 決定1・決定2, IADR-0196 決定2〜4, IADR-0199:
// 為替の情報源の状態を**監査台帳**から期間で引く（GET /audit/events/by-type・OwnerOrService）。
//
// 🔴 **権威源は監査台帳であり、判断サービスではない**（IADR-0199 決定1）。
// 判断サービス側の状態（`FxSourceStatusTracker`）は **in-memory・プロセスごと**で**再起動で消える**。
// 台帳は**イベント全量を JSON で 7 年保持する**——期間の集計はここからしか復元できない。
//
// 🔴 **供給不達は `null`（未供給）へ倒す。空列（事象なし）と混ぜない**（IADR-0199 決定4）。
// 同居する `HttpPeriodFillSource`（照会失敗＝空列）とは**向きが逆**であり、
// `HttpBuyInInferenceRecordSource` と同じ向きである。**揃えてはならない**——
// **劣化があったのに「ありません」と書くのは、劣化を隠したのと同じ結果になる。**
internal sealed class HttpFxSourceStatusSource(
    HttpClient httpClient,
    ILogger<HttpFxSourceStatusSource> logger)
    : IFxSourceStatusSource
{
    // 🔴 **書き手と同じ 1 つの定義を使う**（`AuditDetailJson`・IADR-0199 決定6）。
    // ここへ設定を書き写すと、命名規約を片側だけ変えたときに **`JsonSerializer` は例外を投げず、
    // 既定値（0 / null）で埋めた record を黙って作る**——**数量 0 の行が報告書に載る。**
    private static JsonSerializerOptions DetailOptions => AuditDetailJson.Options;

    // 引く種別。**イベント型名がそのまま台帳の EventType である**（AuditEntryFactory が nameof で書く）。
    private static readonly string[] WantedTypes =
    [
        nameof(FxRateSourceFellBack),
        nameof(FxRateSourcePrimaryRestored),
        nameof(FxRateStale),
        nameof(PositionClosedWithStaleFxRate),
        // #513 / IADR-0225: **静かな期間の出典はこの種別からしか導けない**（遷移は平常時に出ない）。
        nameof(FxRateSourceUsed),
    ];

    /// <summary>
    /// JST 取引日 <paramref name="fromInclusive"/>〜<paramref name="toInclusive"/> の状態。
    /// <para>
    /// 🔴 <b>半開区間 [from 00:00 JST, to+1 日 00:00 JST) で引く</b>（IADR-0199 決定3）。
    /// 終端を <c>23:59:59</c> で閉じると<b>その日の最後の 1 秒が落ちる</b>。
    /// </para>
    /// </summary>
    public async Task<FxSourceStatus?> GetStatusAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default)
    {
        var (from, to) = AuditPeriodRange.JstHalfOpen(fromInclusive, toInclusive);
        var path = "/audit/events/by-type"
            + $"?from={Uri.EscapeDataString(from.ToString("o"))}"
            + $"&to={Uri.EscapeDataString(to.ToString("o"))}"
            + $"&types={string.Join(",", WantedTypes)}";

        try
        {
            using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "為替の情報源の状態の照会に失敗しました（{Status}・{From}〜{To}）。"
                        + "**未供給として扱います**（「切替なし」とは書きません）。",
                    (int)response.StatusCode, fromInclusive, toInclusive);
                return null;
            }

            var entries = await response.Content
                .ReadFromJsonAsync<IReadOnlyList<AuditEntryDto>>(cancellationToken)
                .ConfigureAwait(false);

            if (entries is null)
            {
                logger.LogWarning("為替の情報源の状態の応答が不正（null）でした。**未供給として扱います**。");
                return null;
            }

            return Build(entries);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "為替の情報源の状態の照会がタイムアウトしました（{From}〜{To}）。**未供給として扱います**。",
                fromInclusive, toInclusive);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "為替の情報源の状態の照会で例外が発生しました（{From}〜{To}）。**未供給として扱います**。",
                fromInclusive, toInclusive);
            return null;
        }
    }

    // 台帳の記録を種別ごとに戻す。**壊れた 1 件で期間全体を落とさない**——
    // 読めなかった記録は捨ててログへ残す（**黙って捨てない**）。
    private FxSourceStatus Build(IReadOnlyList<AuditEntryDto> entries)
    {
        var fellBacks = new List<FxRateSourceFellBack>();
        var restorations = new List<FxRateSourcePrimaryRestored>();
        var stales = new List<FxRateStale>();
        var staleCloses = new List<PositionClosedWithStaleFxRate>();
        var usages = new List<FxRateSourceUsed>();

        foreach (var e in entries)
        {
            switch (e.EventType)
            {
                case nameof(FxRateSourceFellBack):
                    Add(fellBacks, e);
                    break;
                case nameof(FxRateSourcePrimaryRestored):
                    Add(restorations, e);
                    break;
                case nameof(FxRateStale):
                    Add(stales, e);
                    break;
                case nameof(PositionClosedWithStaleFxRate):
                    Add(staleCloses, e);
                    break;
                case nameof(FxRateSourceUsed):
                    Add(usages, e);
                    break;
                default:
                    // 要求していない種別が返った＝台帳側の絞り込みが効いていない。混ぜずに落とす。
                    logger.LogWarning("要求していない監査種別が返りました（{EventType}）。無視します。", e.EventType);
                    break;
            }
        }

        return new FxSourceStatus(
            fellBacks, restorations, stales, Credits(fellBacks, restorations, usages), staleCloses, usages);
    }

    private void Add<T>(List<T> into, AuditEntryDto entry)
    {
        try
        {
            if (JsonSerializer.Deserialize<T>(entry.Detail, DetailOptions) is { } value)
            {
                into.Add(value);
                return;
            }

            logger.LogWarning("監査記録の本文が空でした（{EventType} / {Id}）。当該 1 件を無視します。", entry.EventType, entry.Id);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "監査記録の本文を復元できませんでした（{EventType} / {Id}）。当該 1 件を無視します。",
                entry.EventType, entry.Id);
        }
    }

    /// <summary>
    /// 🔴 <b>台帳に「使った証拠」のある情報源のクレジットだけを返す</b>（IADR-0199 決定5）。
    /// <para>
    /// 証拠は 3 種ある——<b>切替・復帰（遷移）と、暦日ごとの使用記録</b>（#513・IADR-0225）。
    /// 遷移だけを見ていた頃は<b>静かな期間にどの源を使ったのか証明できず</b>、
    /// 平常時こそ出典が空になっていた。<b>使用記録が入ったことで、平常時も証拠から導ける。</b>
    /// </para>
    /// <para>
    /// 🔴 <b>証拠の無い源のクレジットは今も出さない</b>——「たぶん第一の源だろう」で書かない
    /// （<b>使っていない源のクレジットを出すのは事実に反する</b>。IADR-0196 決定4）。
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> Credits(
        IReadOnlyList<FxRateSourceFellBack> fellBacks,
        IReadOnlyList<FxRateSourcePrimaryRestored> restorations,
        IReadOnlyList<FxRateSourceUsed> usages) =>
        [.. fellBacks.Select(e => e.SourceName)
            .Concat(restorations.Select(e => e.SourceName))
            .Concat(usages.Select(e => e.SourceName))
            .Select(FxSourceCredits.ForSource)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)];

    // 🔴 期間の作り方は AuditPeriodRange（#338 で 1 箇所へ集約）。
    // 照会元が 3 つに増えたため各アダプタで書き写さない——1 つで境界を間違えても他が正しいと気づけない。

    // 監査台帳の応答の受け皿。**必要な 3 項目だけ**を受ける（残りは報告書が使わない）。
    private sealed record AuditEntryDto(Guid Id, string EventType, string Detail);
}
