using AiStockTrading.Shared.Contracts.Logging;
using ReportService.Domain;
using ReportService.Features.Reports.ConfirmReport;

namespace ReportService.Features.Reports.RevisePolicy;

internal static class RevisePolicyEndpoint
{
    // FR-07, FR-14, UC-03〜05, ADR-0003, #1016, IADR-0431: 利用者の自由文の指示から方針の改訂案を作り、新しい版の
    // ドラフトとして保存・提示する（**確定はしない**）。OwnerOnly（登録表 ReportEndpoints の owner グループ）。
    //
    // 改訂者は確定と同じ ConfirmingActorResolver で決める（Discord Bot は owner マップ機密クライアントのトークンで呼び、
    // 本文の onBehalfOf は信頼クライアントに限って採る。IADR-0240 決定11）。
    //
    // 応答: 200＝案を保存・提示した／400＝指示・会話キー・代理指定の不正／404＝対象なし（新しく作れるのは当日の日報だけ）／
    // 409＝確定済み・土台なし・確定しても効かない（並行更新の競合も登録表のフィルタが 409）／502＝AI の案を作れなかった。
    // **200 以外では何も保存していない。**
    public static void MapRevisePolicy(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/policy-revisions", async (RevisePolicyRequest req, ReportPolicyRevisionService svc,
            DelegatedActorOptions delegated, ILoggerFactory loggerFactory, HttpContext http) =>
        {
            var revising = ConfirmingActorResolver.Resolve(http.User, req.OnBehalfOf, delegated.TrustedClientIds);
            if (revising.Rejected)
            {
                loggerFactory.CreateLogger("ReportPolicyRevisionActor").LogWarning(
                    "方針の改訂の代理される利用者（OnBehalfOf）が値域外のため拒否しました。");
                return Results.BadRequest(new { error = "代理される利用者（onBehalfOf）の形式が不正です。" });
            }

            if (revising.IgnoredOnBehalfOf)
            {
                loggerFactory.CreateLogger("ReportPolicyRevisionActor").LogWarning(
                    "方針の改訂の OnBehalfOf を無視しました（信頼するクライアントのトークンではありません。改訂者={Actor}）。",
                    LogSanitizer.Sanitize(revising.Actor));
            }

            var result = await svc.ReviseAsync(req.PeriodKey, req.Instruction, revising.Actor, http.RequestAborted);
            return result.Status switch
            {
                PolicyRevisionStatus.Proposed => Results.Ok(PolicyRevisionResponse.From(result)),
                PolicyRevisionStatus.InvalidInstruction or PolicyRevisionStatus.InvalidPeriodKey =>
                    Results.BadRequest(new { error = result.Message }),
                PolicyRevisionStatus.NotFound => Results.NotFound(new { error = result.Message }),
                PolicyRevisionStatus.AiFailed => Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status502BadGateway),
                _ => Results.Conflict(new { error = result.Message }),
            };
        });
}

// FR-07, #1016, IADR-0431: 改訂の要求。Instruction は利用者の自由文（1000 文字まで）。PeriodKey 省略時は当日（JST）の日報。
// OnBehalfOf は代理される利用者（Discord Bot が載せる。信頼クライアント以外では無視）。
// NFR, IADR-0420: 受け手（通知サービス）の契約テストが送り手の本物の型として参照するため public。
public sealed record RevisePolicyRequest(string? Instruction, string? PeriodKey = null, string? OnBehalfOf = null);

// FR-07, #1016, IADR-0431 決定 5: 改訂の応答（200 のときだけ）。
// 🔴 **文字列は発行側で無害化して返す**（IADR-0116 決定3 と同じ位置。Discord へ投稿される本文には LLM の出力が入る）。
// 方針・説明・理由は ReportSummarySanitizer（制御文字・メンション構文・境界語）を通す。銘柄は検証済みの書式。
public sealed record PolicyRevisionResponse(
    string PeriodKey,
    int Version,
    bool Created,
    bool Presented,
    bool AutoGenerationSkipped,
    string Message,
    string PolicySummary,
    IReadOnlyList<WatchlistChangeView> WatchlistChanges,
    string? Rationale)
{
    internal static PolicyRevisionResponse From(PolicyRevisionResult result)
    {
        var proposal = result.Proposal!;
        return new PolicyRevisionResponse(
            result.PeriodKey!,
            result.Version,
            result.Created,
            result.Presented,
            result.AutoGenerationSkipped,
            result.Message,
            ReportSummarySanitizer.Sanitize(proposal.PolicySummary, PolicyRevisionProposalParser.MaxPolicySummaryLength),
            [.. proposal.WatchlistChanges.Select(c => new WatchlistChangeView(
                c.Action == WatchlistChangeAction.Add ? "add" : "remove",
                c.Symbol,
                ReportSummarySanitizer.Sanitize(c.Reason, PolicyRevisionProposalParser.MaxReasonLength)))],
            proposal.Rationale is { } rationale
                ? ReportSummarySanitizer.Sanitize(rationale, PolicyRevisionProposalParser.MaxRationaleLength)
                : null);
    }
}

// 監視銘柄の入れ替え案の 1 件（提示のみ）。Action は "add" / "remove"（列挙の JSON 表現に結合しない）。
public sealed record WatchlistChangeView(string Action, string Symbol, string Reason);
