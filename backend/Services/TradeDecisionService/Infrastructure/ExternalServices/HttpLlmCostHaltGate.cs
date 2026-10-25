using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// 🔴 NFR-13, #1423 M-5, ADR-0074 決定2, IADR-0582: 費用統制の GET /costs/state を照会し、`isHalted` だけを読む。
//
// 情報収集の HttpCostControlGate（IADR-0031）と同じ口・同じ読み方（名前の大小を区別しない・真偽は true/false のみ）。
// 停止しているのは **`isHalted` が明示的に true のときだけ**である。非 2xx・例外・タイムアウト・項目の欠落・不正な本文は
// 「停止していない」へ倒す（判断は LLM を呼ぶ＝従来どおり）。費用の統制で資金の保護を止めない向きであり、IADR-0031 と同じ判断である。
// 認証は client_credentials サービストークン（trading-service。Program.cs の AddAiStockTradingServiceToken）。
public sealed class HttpLlmCostHaltGate(HttpClient httpClient, ILogger<HttpLlmCostHaltGate> logger) : ILlmCostHaltGate
{
    public async Task<bool> IsHaltedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("/costs/state", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("費用統制の照会に失敗（{Status}）。停止していないものとして判断を続けます。", (int)response.StatusCode);
                return false;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            // 同名の項目が複数あれば最後のものを採る（HttpCostControlGate と同じ）。
            bool? halted = null;
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, "isHalted", StringComparison.OrdinalIgnoreCase))
                    {
                        halted = property.Value.ValueKind switch
                        {
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => null,
                        };
                    }
                }
            }

            if (halted is { } value)
                return value;

            logger.LogWarning("費用統制の応答に isHalted を読めません。停止していないものとして判断を続けます。");
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("費用統制の照会がタイムアウト。停止していないものとして判断を続けます。");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "費用統制の照会で例外。停止していないものとして判断を続けます。");
            return false;
        }
    }
}
