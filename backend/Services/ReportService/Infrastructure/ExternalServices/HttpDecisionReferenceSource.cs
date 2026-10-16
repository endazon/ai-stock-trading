using System.Net.Http.Json;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;
using ReportService.Domain;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-08, #1356, IADR-0541 決定 2: 期間の判断が見た参考情報を監査台帳（GET /audit/events/by-type）から引く。
// 窓は判断根拠と同じ（JST の暦日の半開区間・AuditPeriodRange）。🔴 不達・不正な応答は null（未供給）。壊れた 1 件は捨てて残りで畳む。
public sealed class HttpDecisionReferenceSource(HttpClient httpClient, ILogger<HttpDecisionReferenceSource> logger)
    : IDecisionReferenceSource
{
    private static JsonSerializerOptions DetailOptions => AuditDetailJson.Options;

    // 引く種別（台帳の EventType はイベント型名そのもの）。gRPC 実装と共有する。
    internal static readonly string[] WantedTypes = [nameof(TradeDecisionMade), nameof(TradeDecisionHeld)];

    public async Task<IReadOnlyList<DecisionReferenceFeedBuilder.DecisionRecord>?> GetDecisionsAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default)
    {
        var (from, to) = HttpTradeRationaleSource.Window(fromInclusive, toInclusive);
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
                    "判断が参照したニュース・開示の照会に失敗しました（{Status}・{From}〜{To}）。**未供給として扱います**。",
                    (int)response.StatusCode, fromInclusive, toInclusive);
                return null;
            }

            var entries = await response.Content
                .ReadFromJsonAsync<IReadOnlyList<AuditLedgerEntry>>(cancellationToken)
                .ConfigureAwait(false);
            if (entries is null)
            {
                logger.LogWarning("判断が参照したニュース・開示の応答が不正（null）でした。**未供給として扱います**。");
                return null;
            }

            return Build(entries, logger);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("判断が参照したニュース・開示の照会がタイムアウトしました（{From}〜{To}）。**未供給として扱います**。",
                fromInclusive, toInclusive);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "判断が参照したニュース・開示の照会で例外が発生しました（{From}〜{To}）。**未供給として扱います**。",
                fromInclusive, toInclusive);
            return null;
        }
    }

    // 台帳の記録を判断ごとの参照へ戻して畳む。要求していない種別・壊れた本文は 1 件ずつ捨てる（黙って捨てない＝ログ）。
    internal static IReadOnlyList<DecisionReferenceFeedBuilder.DecisionRecord> Build(IReadOnlyList<AuditLedgerEntry> entries, ILogger logger)
    {
        var decisions = new List<DecisionReferenceFeedBuilder.DecisionRecord>(entries.Count);
        foreach (var e in entries)
        {
            try
            {
                switch (e.EventType)
                {
                    case nameof(TradeDecisionMade):
                        if (JsonSerializer.Deserialize<TradeDecisionMade>(e.Detail, DetailOptions) is { } made)
                            decisions.Add(new(made.Intent.Symbol, made.DecidedAt, made.References));
                        break;
                    case nameof(TradeDecisionHeld):
                        if (JsonSerializer.Deserialize<TradeDecisionHeld>(e.Detail, DetailOptions) is { } held)
                            decisions.Add(new(held.Symbol, held.DecidedAt, held.References));
                        break;
                    default:
                        logger.LogWarning("要求していない監査種別が返りました（{EventType}）。無視します。", e.EventType);
                        break;
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "監査記録の本文を復元できませんでした（{EventType} / {Id}）。当該 1 件を無視します。", e.EventType, e.Id);
            }
        }

        return decisions;
    }
}

// FR-06, #1356, IADR-0541 決定 2: gRPC 実装（`Audit:Grpc` を宣言したときだけ）。窓・種別・解釈は REST と同じ 1 つ。
public sealed class GrpcDecisionReferenceSource(AuditGrpcTransport transport, ILogger<GrpcDecisionReferenceSource> logger)
    : IDecisionReferenceSource
{
    public Task<IReadOnlyList<DecisionReferenceFeedBuilder.DecisionRecord>?> GetDecisionsAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default) =>
        transport.ReadAsync<IReadOnlyList<DecisionReferenceFeedBuilder.DecisionRecord>>(
            "判断が参照したニュース・開示",
            HttpTradeRationaleSource.Window(fromInclusive, toInclusive),
            HttpDecisionReferenceSource.WantedTypes,
            entries => HttpDecisionReferenceSource.Build(entries, logger),
            logger,
            cancellationToken);
}

// FR-06, #1356, IADR-0541 決定 2: 監査台帳の所在が未構成（常に null＝未供給。「参照なし」へ倒さない）。
public sealed class UnsuppliedDecisionReferenceSource : IDecisionReferenceSource
{
    public Task<IReadOnlyList<DecisionReferenceFeedBuilder.DecisionRecord>?> GetDecisionsAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DecisionReferenceFeedBuilder.DecisionRecord>?>(null);
}
