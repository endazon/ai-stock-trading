extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// NFR, FR-04, FR-10, MSP:ADR-0029, IADR-0284 決定 5（段 2）, IADR-0427, #997 (#753):
// サイジング文脈を **gRPC 生成クライアント**（`RiskControlsRead/GetSizingContext`）で照会する `ISizingContextProvider` の
// 2 つ目の実装。REST 実装（HttpSizingContextProvider）と並走し、選ぶのは Program.cs（`RiskManagement:Grpc`。**既定は REST**）。
//
// 🔴 **解釈は REST 実装と同じ 1 つ**（`HttpSizingContextProvider.Interpret`。IADR-0427 決定 5）。ここは proto を同じ
// nullable の DTO へ写すだけである。
// 🔴 **原則 A**（IADR-0354 / IADR-0408 の区別をそのまま運ぶ）:
//   - 資金・段階残枠・日次残枠の欠落は **null（不明）**。0 と読むと「枠を使い切った」になり、プロンプトと監査に「0」と出る。
//   - 連敗数・DD 比率・動作モード・上限の欠落は、REST と同じく**契約の食い違い**として残枠 0 の安全既定へ倒す
//     （0 と読むと縮小係数が外れる＝上限側への fail-open）。上限は 8 項目が 1 つでも欠ければ「上限が無い」と同じ扱い。
//   - 損切りの実行機構の未指定・未知は null（不明）。S0 と読まない。
// 🔴 取得できない・読めない（status・deadline・10 進の書式）は REST と同じ残枠 0 の安全既定。
public sealed class GrpcSizingContextProvider(
    RiskManagementGrpcTransport transport,
    ILogger<GrpcSizingContextProvider> logger)
    : ISizingContextProvider
{
    public async Task<SizingContext> GetContextAsync(CancellationToken cancellationToken = default)
    {
        var response = await transport.CallAsync(
            "サイジング文脈",
            "残枠 0 の安全既定に倒します。",
            (client, options) => client.GetSizingContextAsync(new Proto.GetSizingContextRequest(), options),
            cancellationToken).ConfigureAwait(false);
        if (response is null)
            return HttpSizingContextProvider.SafeDefault();

        HttpSizingContextProvider.SizingContextDto dto;
        try
        {
            dto = ToDto(response);
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "サイジング文脈の gRPC 応答を読めません（10 進の書式）。残枠 0 の安全既定に倒します。");
            return HttpSizingContextProvider.SafeDefault();
        }

        return HttpSizingContextProvider.Interpret(dto, logger);
    }

    // 線上 → REST と同じ nullable の DTO（欠落・未指定は null）。
    internal static HttpSizingContextProvider.SizingContextDto ToDto(Proto.GetSizingContextResponse r) => new(
        RiskManagementWire.Decimal(r.HasCapital, r.Capital),
        RiskManagementWire.Decimal(r.HasStageCapitalRemaining, r.StageCapitalRemaining),
        RiskManagementWire.Decimal(r.HasDailyOrderRemaining, r.DailyOrderRemaining),
        r.HasConsecutiveLosses ? r.ConsecutiveLosses : null,
        RiskManagementWire.Decimal(r.HasDrawdownRatio, r.DrawdownRatio),
        RiskManagementWire.Provider(r.Mode),
        ToLimits(r.Limits),
        RiskManagementWire.StopLossMethod(r.StopLossMethod));

    // 上限は 8 項目すべてが揃ったときだけ組む。1 つでも欠ければ null（＝Interpret が契約の食い違いとして安全既定へ倒す）。
    // REST では RiskLimitSettings の required 項目の欠落が逆シリアル化の例外になり、同じ安全既定へ倒れていた。
    private static RiskLimitSettings? ToLimits(Proto.RiskLimits? l)
    {
        if (l is null)
            return null;

        var maxOrder = RiskManagementWire.Decimal(l.HasMaxOrderAmountRatio, l.MaxOrderAmountRatio);
        var maxDaily = RiskManagementWire.Decimal(l.HasMaxDailyOrderAmountRatio, l.MaxDailyOrderAmountRatio);
        var dailyLoss = RiskManagementWire.Decimal(l.HasDailyLossLimitRatio, l.DailyLossLimitRatio);
        var perTrade = RiskManagementWire.Decimal(l.HasPerTradeRiskRatio, l.PerTradeRiskRatio);
        var maxDrawdown = RiskManagementWire.Decimal(l.HasMaxDrawdownRatio, l.MaxDrawdownRatio);
        var sizeFactor = RiskManagementWire.Decimal(l.HasLosingStreakSizeFactor, l.LosingStreakSizeFactor);

        if (maxOrder is not { } mo || maxDaily is not { } md || !l.HasMaxOpenPositions || dailyLoss is not { } dl
            || perTrade is not { } pt || maxDrawdown is not { } mdd || !l.HasLosingStreakThreshold || sizeFactor is not { } sf)
        {
            return null;
        }

        return new RiskLimitSettings
        {
            MaxOrderAmountRatio = mo,
            MaxDailyOrderAmountRatio = md,
            MaxOpenPositions = l.MaxOpenPositions,
            DailyLossLimitRatio = dl,
            PerTradeRiskRatio = pt,
            MaxDrawdownRatio = mdd,
            LosingStreakThreshold = l.LosingStreakThreshold,
            LosingStreakSizeFactor = sf,
        };
    }
}
