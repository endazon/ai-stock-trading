using System.Text.Json;
using System.Text.RegularExpressions;
using ReportService.Common.Abstractions;
using ReportService.Features.Reports.ConfirmReport;
using ReportService.Features.Reports.RevisePolicy;

namespace ReportService.Features.Reports.WatchlistProposal;

// FR-13, FR-14, ADR-0042 決定 1, #1025, IADR-0433 決定 1: 確定した版の監視銘柄の入れ替え案の照会と、適用の内訳の記録。OwnerOnly。
//
// 🔴 **適用する銘柄は、Discord から打ち込まれた値ではなく、この台帳に記録された案から取る**（ADR-0042 決定 1「案に載った銘柄だけ」）。
// Bot は確認ボタン（会話キー＋版）から、その版を作った試行の案と、案を作った時点の監視銘柄（楽観排他の基準）を引く。
internal static partial class WatchlistProposalEndpoints
{
    [GeneratedRegex(@"\A[A-Za-z0-9-]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PeriodKeyPattern();

    public static void MapWatchlistProposal(this IEndpointRouteBuilder owner)
    {
        // 照会: 200＝案（入れ替え・案を作った時点の監視銘柄〔null＝照会できなかった〕・適用の記録の有無）／404＝その版の案が無い／
        // 409＝その版で確定されていない。
        // 🔴 PR #1027 の監査 H1: **報告書がこの版で確定されているときだけ案を返す**（確定済みかつ現在の版＝版＋1）。Bot の窓口の
        // 版番号ガードはプロセス内にしかなく、再起動の後に古い確認ボタン（別の版）が押されると、冪等な再確定の 200 を「確定した」と
        // 読んで確定されていない案を適用していた（監査が実測）。適用の可否の権威をこの照会（報告書サービス）へ置く。
        owner.MapGet("/policy-revisions/watchlist-proposal", (string? periodKey, int? version, IPolicyRevisionLedger ledger, IReportStore store) =>
        {
            if (periodKey is null || !PeriodKeyPattern().IsMatch(periodKey) || version is not >= 1)
                return Results.BadRequest(new { error = "会話キー（periodKey）と版（version）が必要です。" });

            if (ledger.FindProposed(periodKey, version.Value) is not { } attempt)
                return Results.NotFound(new { error = $"報告書 {periodKey}（版 {version}）は /policy の案ではありません。" });

            if (store.Get(periodKey) is not { } report || !report.IsConfirmedAtDraftVersion(version.Value))
                return Results.Conflict(new { error = $"報告書 {periodKey} は版 {version} で確定されていません。入れ替えは適用しません。" });

            return Results.Ok(new WatchlistProposalView(
                attempt.Id,
                attempt.PeriodKey,
                attempt.ReportVersion!.Value,
                Parse<WatchlistChangeView>(attempt.WatchlistChangesJson) ?? [],
                Parse<WatchlistSnapshotEntryView>(attempt.WatchlistSnapshotJson),
                attempt.WatchlistAppliedAt is not null));
        });

        // 記録: 200＝記録した／409＝既に記録済み（1 回だけ）・案でない・その版で確定されていない／404＝試行が無い。
        // 🔴 PR #1027 の監査 L1: 記録は 1 回だけで、書くとその案の適用を永久に塞ぐ。したがって**確定された案の試行にだけ**受け付ける
        // （照会と同じ条件）。任意の試行 ID で適用を塞げないようにする。
        owner.MapPost("/policy-revisions/{attemptId:guid}/watchlist-apply-result",
            (Guid attemptId, WatchlistApplyResultRequest req, IPolicyRevisionLedger ledger, IReportStore store, IClock clock,
                DelegatedActorOptions delegated, HttpContext http) =>
            {
                var recording = ConfirmingActorResolver.Resolve(http.User, req.OnBehalfOf, delegated.TrustedClientIds);
                if (recording.Rejected)
                    return Results.BadRequest(new { error = "代理される利用者（onBehalfOf）の形式が不正です。" });
                if (string.IsNullOrWhiteSpace(req.Outcome) || req.Outcome.Length > 32)
                    return Results.BadRequest(new { error = "適用の結果（outcome）が必要です。" });
                if (ledger.Find(attemptId) is not { } attempt)
                    return Results.NotFound();
                if (attempt is not { Outcome: PolicyRevisionAttemptOutcome.Proposed, ReportVersion: { } draftVersion }
                    || store.Get(attempt.PeriodKey) is not { } report || !report.IsConfirmedAtDraftVersion(draftVersion))
                    return Results.Conflict(new { error = "確定された /policy の案ではないため、適用の内訳を記録しません。" });

                var json = JsonSerializer.Serialize(new
                {
                    outcome = req.Outcome,
                    recordedBy = recording.Actor,
                    items = req.Items ?? [],
                    message = req.Message,
                }, ReportPolicyRevisionService.LedgerJson);
                // 列は text（上限なし）だが、要求の大きさは抑える（入れ替え 10 件の内訳に十分な量）。
                if (json.Length > 65536)
                    return Results.BadRequest(new { error = "適用の内訳が長すぎます。" });

                return ledger.RecordWatchlistApply(attemptId, json, clock.UtcNow)
                    ? Results.Ok(new { attemptId })
                    : Results.Conflict(new { error = "この案の適用の内訳は記録済みです。" });
            });
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static IReadOnlyList<T>? Parse<T>(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<List<T>>(json, Web);
}

// FR-13, #1025: 確定した版の入れ替え案。Snapshot が null なら案を作った時点の監視銘柄が分からない（適用しない）。
// NFR, IADR-0420: 受け手（通知サービス）の契約テストが送り手の本物の型として参照するため public。
public sealed record WatchlistProposalView(
    Guid AttemptId,
    string PeriodKey,
    int ReportVersion,
    IReadOnlyList<WatchlistChangeView> Changes,
    IReadOnlyList<WatchlistSnapshotEntryView>? Snapshot,
    bool ApplyRecorded);

public sealed record WatchlistSnapshotEntryView(string Symbol, string Market);

// 適用の内訳の記録。Outcome は Bot が決める種別（applied / stale / snapshot-unknown / indeterminate / failed 等）。
public sealed record WatchlistApplyResultRequest(
    string? Outcome,
    IReadOnlyList<WatchlistApplyItemRecord>? Items,
    string? Message,
    string? OnBehalfOf = null);

public sealed record WatchlistApplyItemRecord(string? Action, string? Symbol, bool Applied, string? SkipReason);
