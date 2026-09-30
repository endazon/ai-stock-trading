using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement.GetEntryBlockers;

// FR-10, FR-04, #1113, IADR-0463 決定 3: 銘柄単位の「新規建ての可否」（取引判断が LLM を呼ぶ前に読む。OwnerOrService）。
// symbol・market（数値または名前）は必須。欠落・未定義の市場は 400（読み取り群のフィルタが ArgumentException を 400 へ写す）。
internal static class GetEntryBlockersEndpoint
{
    public static void MapGetEntryBlockers(this IEndpointRouteBuilder read) =>
        read.MapGet("/entry-blockers", (string? symbol, Market? market, EntryBlockersService svc) =>
            string.IsNullOrWhiteSpace(symbol) || market is not { } m || !Enum.IsDefined(m)
                ? Results.BadRequest("symbol・market は必須です。")
                : Results.Ok(svc.Build(symbol, m)));
}
