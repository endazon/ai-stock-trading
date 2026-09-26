using System.Text.RegularExpressions;
using AiStockTrading.Shared.Contracts.Logging;
using AiStockTrading.Shared.Contracts.Trading;
using MarketMonitorService.Domain;

namespace MarketMonitorService.Features.MarketMonitor.ApplyWatchlistProposal;

// FR-13, FR-14, ADR-0042 決定 1・2, #1025, IADR-0433 決定 2・3: `/policy` の監視銘柄の入れ替え案を 1 回で適用する。
// 変更は利用者のみ（登録表の owner＝OwnerOnly）。Discord Bot が、利用者が確認ボタンで確定した案の銘柄だけを運ぶ。
//
// 応答: 200＝適用の内訳（適用した銘柄・適用しなかった銘柄と理由・Finnhub の日次要求の推定）／400＝案の形の違反・代理指定の不正／
// 409＝案を作った後に監視銘柄が変わった（1 件も適用しない）・保存の競合（登録表のフィルタ）。**200 以外では 1 件も適用していない。**
internal static partial class ApplyWatchlistProposalEndpoint
{
    // 案の出所（会話キー＋版。例 daily-2026-09-28-v3）。変更履歴の理由へ載るため英数字とハイフンだけ。
    [GeneratedRegex(@"\A[A-Za-z0-9-]{1,48}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProposalRefPattern();

    public static void MapApplyWatchlistProposal(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/watchlist/proposal-apply", (WatchlistProposalApplyRequest req, MonitorWatchlistService svc,
            DelegatedActorOptions delegated, WatchlistVolumeEstimator estimator, ILoggerFactory loggerFactory, HttpContext http) =>
        {
            var logger = loggerFactory.CreateLogger("WatchlistProposalApply");
            var applying = DelegatedActorResolver.Resolve(http.User, req.OnBehalfOf, delegated.TrustedClientIds);
            if (applying.Rejected)
                return Results.BadRequest(new { error = "代理される利用者（onBehalfOf）の形式が不正です。" });
            if (applying.IgnoredOnBehalfOf)
                logger.LogWarning("入れ替え案の適用の OnBehalfOf を無視しました（信頼するクライアントのトークンではありません。変更者={Actor}）。",
                    LogSanitizer.Sanitize(applying.Actor));

            if (req.ProposalRef is not { } proposalRef || !ProposalRefPattern().IsMatch(proposalRef))
                return Results.BadRequest(new { error = "案の出所（proposalRef）の形式が不正です。" });

            var expected = req.ExpectedWatchlist?.Select(ToSymbol).ToList();
            if (expected is not null && expected.Any(s => s is null))
                return Results.BadRequest(new { error = "案を作った時点の監視銘柄（expectedWatchlist）の形式が不正です。" });
            var changes = req.Changes?.Select(ToChange).ToList();
            if (changes is not null && changes.Any(c => c is null))
                return Results.BadRequest(new { error = "入れ替え（changes）の操作は add / remove に限ります。" });

            // 期待値・入れ替えの欠落は ApplyProposal の検証が 400 にする（1 件も適用しない）。
            // PR #1027 の監査 L1（任意）: 変更履歴の理由に、どの経路で適用されたかを事実どおりに書く
            // （信頼クライアント＝Discord Bot の代理か、利用者のトークンで直接呼ばれたか）。
            var via = applying.AuthorizedBy is { } client
                ? $"Discord の確認ボタンで適用・代理 {client}"
                : "利用者のトークンで直接適用";
            var plan = svc.ApplyProposal(
                expected?.Cast<MonitoredSymbol>().ToList(), changes?.Cast<ProposedWatchlistChange>().ToList(),
                applying.Actor, proposalRef, via);
            if (plan.Stale)
            {
                logger.LogWarning("入れ替え案 {ProposalRef} は案の作成後に監視銘柄が変わったため適用しませんでした。", proposalRef);
                return Results.Conflict(new
                {
                    error = "案を作った後に監視銘柄が変わったため、入れ替えを 1 件も適用していません。設定画面で確認してください。",
                });
            }

            logger.LogInformation(
                "入れ替え案 {ProposalRef} を適用しました（変更者={Actor}・代理={AuthorizedBy}・適用={Applied}・適用せず={Skipped}）。",
                proposalRef, LogSanitizer.Sanitize(applying.Actor), applying.AuthorizedBy,
                plan.Items.Count(i => i.Applied), plan.Items.Count(i => !i.Applied));

            return Results.Ok(new WatchlistProposalApplyResponse(
                [.. plan.Items.Select(i => new WatchlistProposalItemResult(
                    i.Change.Action == ProposedWatchlistAction.Add ? "add" : "remove", i.Change.Symbol, i.Applied, i.SkipReason))],
                applying.Actor,
                estimator.Estimate(plan.Resulting.Count)));
        });

    // 期待値（案を作った時点の監視銘柄）の 1 件。銘柄が空・市場の省略は形式違反（null＝400）。期待値は現在の監視銘柄の写しであり、
    // 日本株も含み得る（入れ替え〔changes〕が米国のティッカーに限られるのは案の形の検証〔WatchlistProposalPlan〕の側）。
    private static MonitoredSymbol? ToSymbol(WatchlistSymbolRef s) =>
        string.IsNullOrWhiteSpace(s.Symbol) || s.Market is null ? null : new MonitoredSymbol(s.Symbol.Trim(), s.Market.Value);

    private static ProposedWatchlistChange? ToChange(WatchlistProposalChangeRequest c) => c.Action switch
    {
        "add" => new ProposedWatchlistChange(ProposedWatchlistAction.Add, c.Symbol ?? string.Empty, c.Reason ?? string.Empty),
        "remove" => new ProposedWatchlistChange(ProposedWatchlistAction.Remove, c.Symbol ?? string.Empty, c.Reason ?? string.Empty),
        _ => null,
    };
}

// FR-13, #1025: 入れ替え案の適用要求。ExpectedWatchlist は案を作った時点の監視銘柄（楽観排他の基準）。
// NFR, IADR-0420: 受け手（通知サービス）の契約テストが送り手の本物の型として参照するため public。
public sealed record WatchlistProposalApplyRequest(
    IReadOnlyList<WatchlistSymbolRef>? ExpectedWatchlist,
    IReadOnlyList<WatchlistProposalChangeRequest>? Changes,
    string? ProposalRef,
    string? OnBehalfOf = null);

public sealed record WatchlistSymbolRef(string? Symbol, Market? Market);

public sealed record WatchlistProposalChangeRequest(string? Action, string? Symbol, string? Reason);

// 適用の内訳。Actor は変更履歴に残した変更者。Estimate は適用後の Finnhub の日次要求の推定（Finnhub を使わない構成では null）。
public sealed record WatchlistProposalApplyResponse(
    IReadOnlyList<WatchlistProposalItemResult> Items,
    string Actor,
    FinnhubDailyVolumeEstimateView? Estimate);

public sealed record WatchlistProposalItemResult(string Action, string Symbol, bool Applied, string? SkipReason);

// ADR-0031, ADR-0042 決定 1（2026-09-26 利用者裁定＝警告のみ）: 推定 1 日の要求数と暫定上限。**適用を止めない。**
public sealed record FinnhubDailyVolumeEstimateView(long EstimatedDailyRequests, int ProvisionalDailyLimit, bool Exceeds);
