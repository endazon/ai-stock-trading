using System.Globalization;
using AiStockTrading.Shared.Kernel.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Proto = AiStockTrading.Shared.Grpc.Configuration.V1;

namespace ConfigurationService.Features.Assumptions.GetAssumptions;

// FR-17, UC-06, NFR, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 1）, IADR-0328, IADR-0331, #745 (#584):
// 全体前提条件の照会の gRPC 面。REST の `GET /assumptions`（`GetAssumptionsEndpoint`）と**同じ**
// `AssumptionsService.GetCurrent()` を呼ぶ —— 評価器を 2 つにしない（基盤の参照実装と同じ作法）。
//
// 認可（IADR-0063 決定 2）: REST の読み取りと**同じ** `OwnerOrService`。s2s トークンが無ければ
// `UNAUTHENTICATED`、`trading-service`／`trading-owner` のいずれも持たなければ `PERMISSION_DENIED` になる
// （ASP.NET Core の gRPC は認可失敗をこの 2 つへ写像する）。呼び出し元はどちらも既存の fail-safe へ倒す。
//
// 🔴 **並走中の正は REST である**（MSP:ADR-0029 の 2026-08-04 追記・IADR-0284 決定 1 の順序）。
// REST 面は 1 バイトも変えていない。撤去は段 6 の判断である。
[Authorize(Policy = AiStockTradingAuthPolicies.OwnerOrService)]
internal sealed class AssumptionsGrpcService(AssumptionsService assumptions) : Proto.Assumptions.AssumptionsBase
{
    public override Task<Proto.GetAssumptionsResponse> Get(
        Proto.GetAssumptionsRequest request, ServerCallContext context) =>
        Task.FromResult(AssumptionsWireMapping.ToProto(assumptions.GetCurrent()));
}

// FR-17, IADR-0331 決定 2: ドメイン型 ↔ 線上表現の写像。
//
// 🔴 **金額・率は `decimal` のまま扱い、線上は不変文化の 10 進文字列にする。** proto3 に `decimal` は無く、
// `double` へ落とすと `0.20315`（譲渡益税率）や手数料率が 2 進浮動小数へ丸められ、**同じ版なのに REST と
// gRPC で採算判定・費用上限判定の結果が変わり得る**（REST は `System.Text.Json` が `decimal` を桁を保った
// まま JSON 数値へ書いている）。ここが本 rpc で唯一「意味が静かに変わる」余地のある箇所である。
internal static class AssumptionsWireMapping
{
    // decimal → 線上。`InvariantCulture` を明示する（既定文化だと小数点が `,` になる環境で壊れる）。
    internal static string ToWire(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    // 線上 → decimal。空文字は proto3 の「未指定」であり 0 として読む（REST の DTO 既定と同じ向き）。
    internal static decimal FromWire(string? value) =>
        string.IsNullOrEmpty(value)
            ? 0m
            : decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    internal static Proto.GetAssumptionsResponse ToProto(VersionedAssumptions current)
    {
        ArgumentNullException.ThrowIfNull(current);

        return new Proto.GetAssumptionsResponse
        {
            Version = current.Version,
            Assumptions = new Proto.TradingAssumptions
            {
                CapitalGainsTaxRate = ToWire(current.Assumptions.CapitalGainsTaxRate),
                JapanCommission = ToProto(current.Assumptions.JapanCommission),
                UnitedStatesCommission = ToProto(current.Assumptions.UnitedStatesCommission),
                FxSpreadRatio = ToWire(current.Assumptions.FxSpreadRatio),
                MinimumExpectedProfitMultiple = ToWire(current.Assumptions.MinimumExpectedProfitMultiple),
                CostLimits = new Proto.MonthlyCostLimits
                {
                    Total = ToWire(current.Assumptions.CostLimits.Total),
                    Llm = ToWire(current.Assumptions.CostLimits.Llm),
                    Infrastructure = ToWire(current.Assumptions.CostLimits.Infrastructure),
                    Data = ToWire(current.Assumptions.CostLimits.Data),
                },
            },
        };
    }

    private static Proto.CommissionSchedule ToProto(CommissionSchedule schedule) =>
        new()
        {
            Rate = ToWire(schedule.Rate),
            Minimum = ToWire(schedule.Minimum),
            Cap = ToWire(schedule.Cap),
        };
}
