namespace NotificationService.Features.Notifications;

// FR-06, FR-14, UC-03〜05, 計画 ADR-0052 決定 1, #1156, IADR-0491 決定 1: 報告書サービスの作り直し（`POST /reports/{periodKey}/regenerate`・
// gRPC `ReportOwnerWrite/RegenerateReport`）の抽象。報告書レビュー（IReportReviewController・5 秒）と分けるのは、期間の入力の取得と
// 散文の LLM を待つため上限を長く取るからである（`/policy` の IPolicyRevisionController と同じ理由）。
//
// 🔴 **冪等でない**（LLM を呼び新しい版を作り、1 日の回数を消費する）。**再試行しない**。
// 🔴 **原則 A（不明・無し・有りを混ぜない）**: 提供側が明確に断った（4xx・gRPC の明確な失敗）＝作り直していない。
// 届いたか分からない（タイムアウト・不達）＝**不明**（作り直したかもしれない）。「失敗」と言わず `/report show` へ誘導する。
public interface IReportRegenerationController
{
    Task<ReportRegenerationCommandOutcome> RegenerateAsync(
        string periodKey, string onBehalfOf, CancellationToken cancellationToken = default);
}

/// <param name="Succeeded">報告書サービスが作り直して保存した（200）。</param>
/// <param name="OutcomeUnknown">届いたか分からない（作り直したかもしれない）。</param>
/// <param name="Message">利用者へ見せる文（報告書サービスの定数文、または本サービスの定数文）。</param>
/// <param name="Version">作り直した版（成功のときだけ）。</param>
public sealed record ReportRegenerationCommandOutcome(bool Succeeded, bool OutcomeUnknown, string Message, int? Version = null)
{
    // 🔴 届いたか分からない＝**不明**（作り直されたかもしれない）。
    public const string UnknownMessage =
        "報告書の作り直しの結果が分かりません（応答が届きませんでした）。作り直された可能性があるため、/report show で版を確認してください。";

    // 提供側が明確に断った（説明があればそれを見せる）。
    public static ReportRegenerationCommandOutcome Rejected(string statusLabel, string? error, string hint) =>
        new(false, false,
            error is null
                ? $"報告書の作り直しに失敗しました（{statusLabel}）{hint}。下書きは変わっていません。"
                : $"{error}{hint}");
}
