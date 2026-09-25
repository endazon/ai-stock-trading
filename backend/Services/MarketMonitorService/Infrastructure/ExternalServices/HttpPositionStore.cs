using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Domain;
using Microsoft.Extensions.Logging;

namespace MarketMonitorService.Infrastructure.ExternalServices;

// FR-03, FR-10, IADR-0030: 保有ポジションをリスク管理（#12・#63 台帳）の GET /risk-controls/open-positions から同期照会する。
// 未取得・非 2xx・例外・タイムアウトは空列の安全既定（＝損切り検知対象なし）に倒す。
// 注意: 空列は「取引しない」と異なり損切り保護が働かない側の縮退だが、保有情報なしに損切り価格は知り得ないため
// 依存先障害時に取り得る唯一の縮退（IADR-0030・既存 PlaceholderPositionStore と同一既定）。
//
// 🔴 FR-03, FR-10, #957, IADR-0399: **応答は行ごとに読む。1 行の不正で他の行の損切り検知を止めない。**
// 以前は応答を非 nullable の HeldPosition へ直接逆シリアル化していた。送り手（OpenPositionView）の項目名が変わると
// 銘柄は null（実運用の市況源で例外になり巡回全体が止まる）、損切りラインは 0（ロングは発火せず、含み益のショートは
// 毎巡回発火する）に化けていた（#943 の走査・PR #959 監査）。いまは受け手の DTO を全項目 nullable にし、
//   - 識別できない行（null の行・銘柄なし／空・市場／方向が無いか未定義・数量が無いか正でない）は評価に渡さない、
//   - 損切りラインが無い／正でない行は、平均取得単価があれば送り手と同じ式の近似のライン（StopLossApproximation）で評価し、
//     無ければ評価に渡さない（ライン 0 では評価しない）、
// のいずれも **Critical を 1 巡回 1 行**（全件の内訳つき）と計器 ast.market_monitor.position_rows_degraded で声に出す。
// 200 の本文が一覧として読めない（壊れた JSON・null・列挙が文字列）ときも空列のまま Critical と計器で出す（契約の食い違い）。
public sealed class HttpPositionStore(
    HttpClient httpClient,
    BusinessMetrics metrics,
    ILogger<HttpPositionStore> logger)
    : IPositionStore
{
    private static readonly IReadOnlyCollection<HeldPosition> Empty = [];

    // NFR, IADR-0427 決定 5, #997: ログに載せる照会元（gRPC 実装は自分の照会元を渡す）。
    internal const string RestSource = "GET /risk-controls/open-positions";

    public async Task<IReadOnlyCollection<HeldPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/risk-controls/open-positions", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("保有ポジションの照会に失敗（{Status}）。空列（損切り検知対象なし）に倒します。", (int)response.StatusCode);
                return Empty;
            }

            // 送り手は OpenPositionView（RiskManagement）。web 既定（camelCase・列挙は数値）で往復する。
            // 送り手の型で直列化した本文を読めることは T-10-803、行ごとの扱いは T-10-833〜836 が固定する。
            List<OpenPositionDto?>? rows;
            try
            {
                rows = await response.Content
                    .ReadFromJsonAsync<List<OpenPositionDto?>>(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                ReportUnreadableResponse(metrics, logger, RestSource, ex.Message);
                return Empty;
            }

            if (rows is null)
            {
                ReportUnreadableResponse(metrics, logger, RestSource, "本文が null です");
                return Empty;
            }

            return Classify(rows, metrics, logger, RestSource);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("保有ポジションの照会がタイムアウト。空列（損切り検知対象なし）に倒します。");
            return Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "保有ポジションの照会で例外。空列（損切り検知対象なし）に倒します。");
            return Empty;
        }
    }

    // #957, IADR-0399 決定1: 行ごとに「評価する／近似のラインで評価する／評価できない」へ分ける。
    // NFR, IADR-0427 決定 5, #997: 輸送に依らず 1 つ（GrpcPositionStore も proto を同じ nullable の行へ写してから呼ぶ）。
    // 切り出しで変えたのは、照会元をログの引数にしたことと、読めない応答の文面の「200 応答」を輸送に依らない「成功応答」に
    // したことだけである（判定・計器は不変）。
    internal static IReadOnlyCollection<HeldPosition> Classify(
        IReadOnlyList<OpenPositionDto?> rows, BusinessMetrics metrics, ILogger logger, string source)
    {
        var positions = new List<HeldPosition>(rows.Count);
        var degraded = new List<string>();
        var identityMissing = 0;
        var approximated = 0;
        var stopUnknown = 0;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row is not { Symbol: { } symbol, Market: { } market, Side: { } side, Quantity: { } quantity }
                || string.IsNullOrWhiteSpace(symbol)
                || !Enum.IsDefined(market)
                || !Enum.IsDefined(side)
                || quantity <= 0)
            {
                identityMissing++;
                degraded.Add($"#{i} {BusinessMetrics.PositionRowIdentityMissing}（{Describe(row)}）");
                continue;
            }

            if (row.StopLossPrice is { } line && line > 0m)
            {
                positions.Add(new HeldPosition(symbol, market, side, quantity, row.EntryPrice, line));
                continue;
            }

            // 🔴 ラインが無い／正でない: 0 のまま評価しない（ロングは発火せず、ショートは毎巡回発火する）。
            if (row.EntryPrice is { } entry && entry > 0m)
            {
                var approximate = StopLossApproximation.Approximate(side, entry);
                positions.Add(new HeldPosition(symbol, market, side, quantity, entry, approximate)
                {
                    StopLossApproximated = true,
                });
                approximated++;
                degraded.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"#{i} {BusinessMetrics.PositionRowStopLineApproximated}（{Describe(row)}）→ 近似のライン={approximate} で評価"));
                continue;
            }

            stopUnknown++;
            degraded.Add($"#{i} {BusinessMetrics.PositionRowStopLineUnknown}（{Describe(row)}）→ 評価しない");
        }

        if (degraded.Count > 0)
        {
            metrics.RecordMarketMonitorPositionRowsDegraded(BusinessMetrics.PositionRowIdentityMissing, identityMissing);
            metrics.RecordMarketMonitorPositionRowsDegraded(BusinessMetrics.PositionRowStopLineApproximated, approximated);
            metrics.RecordMarketMonitorPositionRowsDegraded(BusinessMetrics.PositionRowStopLineUnknown, stopUnknown);
            logger.LogCritical(
                "🔴 保有照会（{Source}）の応答に、そのまま損切り判定へ渡せない行があります"
                    + "（{Degraded} / {Total} 行。評価しない {Dropped} 行・近似のラインで評価する {Approximated} 行）。"
                    + "送り手（リスク管理）と市場監視の契約が食い違っている可能性があります（片方だけ先に配備した等）: {Details}。"
                    + " **評価しない行の建玉は、この巡回で損切り（S1）の到達を検知しません。** 近似のラインは平均取得単価から"
                    + "既定比率で見積もった値であり、実際のラインではありません。他の行の評価は続けます。",
                source, degraded.Count, rows.Count, identityMissing + stopUnknown, approximated, string.Join(" / ", degraded));
        }

        return positions;
    }

    internal static void ReportUnreadableResponse(BusinessMetrics metrics, ILogger logger, string source, string reason)
    {
        metrics.RecordMarketMonitorPositionRowsDegraded(BusinessMetrics.PositionRowsResponseUnreadable);
        logger.LogCritical(
            "🔴 保有照会（{Source}）の成功応答を保有の一覧として読めません（{Reason}）。"
                + "送り手（リスク管理）と市場監視の契約が食い違っている可能性があります。空列に倒すため、"
                + "**この巡回ではどの建玉の損切り（S1）の到達も検知しません。**",
            source, reason);
    }

    private static string Describe(OpenPositionDto? row) => row is null
        ? "行が null"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"symbol={Show(row.Symbol)} market={Show(row.Market)} side={Show(row.Side)} quantity={Show(row.Quantity)}"
                + $" entryPrice={Show(row.EntryPrice)} stopLossPrice={Show(row.StopLossPrice)}");

    private static string Show(object? value) => value switch
    {
        null => "なし",
        string s when string.IsNullOrWhiteSpace(s) => "空",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "なし",
    };

    // 🔴 #957, IADR-0399: 送り手 OpenPositionView と同じ項目を**全項目 nullable** で受ける（欠けた項目を既定値に化けさせない）。
    internal sealed record OpenPositionDto(
        string? Symbol,
        Market? Market,
        TradeSide? Side,
        int? Quantity,
        decimal? EntryPrice,
        decimal? StopLossPrice);
}
