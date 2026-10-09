using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// 🔴 FR-10, FR-04, #1113, IADR-0463 決定 3・4: 銘柄単位の新規建ての可否を、リスク管理の
// GET /risk-controls/entry-blockers?symbol&market（OwnerOrService・読み取り専用）から同期照会する。
//
// fail-safe の区別: 非 2xx・例外・タイムアウト・不正な応答は **null（不明）**。不明のとき判断は LLM を呼ぶ（見送らない）。
// 空の一覧（確定する拒否は無い）と不明を取り違えない —— 取り違えても費用が戻るだけで統制は緩まない（審査は残る）が、
// 「塞がっている」を読み違えて**見送る**向きの誤り（別銘柄の応答・欠落した項目・未知の理由）は必ず不明へ倒す。
public sealed class HttpEntryBlockersProvider(
    HttpClient httpClient,
    ILogger<HttpEntryBlockersProvider> logger)
    : IEntryBlockersProvider
{
    public async Task<EntryBlockers?> GetAsync(
        string symbol, Market market, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync(
                    $"/risk-controls/entry-blockers?symbol={Uri.EscapeDataString(symbol)}&market={(int)market}",
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("新規建ての可否の照会に失敗（{Status}）。不明として扱います（LLM を呼びます）。", (int)response.StatusCode);
                return null;
            }

            var dto = await response.Content
                .ReadFromJsonAsync<EntryBlockersDto>(cancellationToken)
                .ConfigureAwait(false);
            return Interpret(dto, symbol, market, logger);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("新規建ての可否の照会がタイムアウト。不明として扱います（LLM を呼びます）。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "新規建ての可否の照会で例外。不明として扱います（LLM を呼びます）。");
            return null;
        }
    }

    // NFR, IADR-0427 決定 5 と同じ形: 応答の**解釈**は輸送に依らず 1 つ（gRPC の実装も同じ行へ写してからここを呼ぶ）。
    // 🔴 規則（どの理由で塞ぐか）は持たない。持つのは「応答が問いに答えているか」の検証だけである。
    internal static EntryBlockers? Interpret(EntryBlockersDto? dto, string symbol, Market market, ILogger logger)
    {
        if (dto is null)
        {
            logger.LogWarning("新規建ての可否の応答を解釈できません。不明として扱います。");
            return null;
        }

        // 🔴 別の銘柄・市場の答え（項目名の変更で既定値に落ちた場合を含む）を、この銘柄の答えとして読まない。
        if (!string.Equals(dto.Symbol, symbol, StringComparison.Ordinal) || dto.Market != market)
        {
            logger.LogWarning(
                "新規建ての可否の応答が問いの銘柄・市場と一致しません（{Symbol}/{Market}）。不明として扱います。", dto.Symbol, dto.Market);
            return null;
        }

        // 🔴 方向の一覧が無い・未知の理由（送り手が新しい理由を足し、受け手が知らない）を含む応答は不明。
        if (dto.LongSide is null || dto.ShortSide is null
            || dto.LongSide.Concat(dto.ShortSide).Any(r => r is not { } v || !Enum.IsDefined(v)))
        {
            logger.LogWarning("新規建ての可否の応答に方向の一覧の欠落、または未知の理由があります。不明として扱います。");
            return null;
        }

        // 🔴 #1286, IADR-0521 決定 4: 全注文の拒否。項目の欠落（旧い送り手）は不明（null）。未知の理由を含めば全注文の側だけを不明にする
        // （新規建ての方向の答えは使える。全注文の拒否を読み違えて保有のみの銘柄を判断対象から外す向きの誤りを避ける）。
        IReadOnlyList<RejectionReason>? anyOrder = dto.AnyOrder is null || dto.AnyOrder.Any(r => r is not { } v || !Enum.IsDefined(v))
            ? null
            : [.. dto.AnyOrder.Select(r => r!.Value)];

        return new EntryBlockers(
            [.. dto.LongSide.Select(r => r!.Value)],
            [.. dto.ShortSide.Select(r => r!.Value)],
            anyOrder);
    }

    // EntryBlockersView（RiskManagement・#1113）。camelCase・列挙は数値で往復する。
    // 🔴 全項目を nullable で受ける（項目の欠落を既定値〔市場 0＝日本・空の一覧〕と区別する。#943 と同じ規律）。
    internal sealed record EntryBlockersDto(
        string? Symbol, Market? Market, IReadOnlyList<RejectionReason?>? LongSide, IReadOnlyList<RejectionReason?>? ShortSide,
        IReadOnlyList<RejectionReason?>? AnyOrder = null);
}
