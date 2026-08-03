using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Notification.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Notification.Infrastructure.Composable.Adapters;

// FR-20, FR-14, UC-06, ADR-0007/0008, IADR-0070/0081: 段階ゲート（#20）の OwnerOnly エンドポイントを呼ぶだけの
// アダプタ。通知サービスは段階ゲートの状態を持たない（権威は Risk 側）。kill switch / pause と同型。
//
// 当該エンドポイントは OwnerOnly（trading-owner）であり、s2s トークン（trading-service）では 403 になる。本アダプタが
// 使う名前付き HttpClient には Bot 専用の owner マップ機密クライアントのトークンを付与する。資格情報が未設定なら
// トークン無し＝401 となり操作は失敗する（安全側）。
//
// IADR-0081 決定1: Risk は enum を数値でシリアライズする（Risk Worker に文字列 enum コンバータの登録なし）。
// 数値 enum（段階・モード・遷移方向・拒否基準）の射影 DTO と Discord 表示テキストへの整形を**本アダプタ 1 か所に閉じ**、
// Application 層（ポート・ハンドラ）は整形済み文字列だけを扱う（pause の HttpPauseController と同じ representation-agnostic）。
// enum は codebase 慣行（連番・append-only）に依拠し、未知の数値は fail-safe のフォールバック文言に倒す（例外を出さない）。
//
// 失敗時の方針: 送信側（IADR-0020）と異なり握り潰さない。利用者が結果を知る必要がある操作であり、
// 「失敗を成功に見せない」ことが安全側になる。
internal sealed class HttpStageGateController(
    HttpClient httpClient,
    ILogger<HttpStageGateController> logger)
    : IStageGateController
{
    private const string OwnerHint = "（Bot の owner クライアント設定・trading-owner ロール割当を確認してください）";

    // 表示に載せる直近履歴の件数（多すぎると Discord の表示が冗長になるため直近のみ）。
    private const int RecentHistoryCount = 5;

    public async Task<StageGateStatusResult> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/risk-controls/stage-gate", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return Fail("段階ゲートの照会", response.StatusCode);

            var view = await response.Content
                .ReadFromJsonAsync<StageGateStatusView>(cancellationToken)
                .ConfigureAwait(false);

            if (view is null)
            {
                logger.LogWarning("段階ゲートの応答を解釈できませんでした。");
                return new StageGateStatusResult(false, "段階ゲートの応答を解釈できませんでした");
            }

            return new StageGateStatusResult(true, FormatStatus(view));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("段階ゲートの照会がタイムアウトしました。");
            return new StageGateStatusResult(false, "段階ゲートの照会がタイムアウトしました");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "段階ゲートの照会で例外が発生しました。");
            return new StageGateStatusResult(false, $"段階ゲートの照会に失敗しました（{ex.GetType().Name}）");
        }
    }

    public async Task<StageTransitionCommandResult> RequestTransitionAsync(
        int targetStage, CancellationToken cancellationToken = default)
    {
        try
        {
            // 承認者は要求本文ではなく Risk 側が認証済みトークン（owner マップ）から取る（承認なりすまし防止）。
            using var response = await httpClient
                .PostAsJsonAsync("/risk-controls/stage-gate/transition", new { targetStage }, cancellationToken)
                .ConfigureAwait(false);

            // 200＝受理・422＝受理不能（未充足基準／飛び級／現段階指定）。どちらも本文に結果 JSON を持つ。
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity)
            {
                var result = await response.Content
                    .ReadFromJsonAsync<StageTransitionResultView>(cancellationToken)
                    .ConfigureAwait(false);

                if (result is null)
                {
                    logger.LogWarning("段階遷移の応答を解釈できませんでした。");
                    return new StageTransitionCommandResult(false, false, "段階遷移の応答を解釈できませんでした");
                }

                return result.Accepted
                    ? new StageTransitionCommandResult(true, true,
                        $"段階を {StageLabel(result.Transition?.ToStage ?? targetStage)} へ遷移しました。")
                    : new StageTransitionCommandResult(true, false, FormatRejection(result.RejectionReasons));
            }

            // 400（不正な targetStage）・401/403（owner 設定不備）・その他は失敗として返す。
            var status = Fail("段階遷移", response.StatusCode);
            return new StageTransitionCommandResult(false, false, status.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("段階遷移がタイムアウトしました。");
            return new StageTransitionCommandResult(false, false, "段階遷移がタイムアウトしました（状態は不明です）");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "段階遷移で例外が発生しました。");
            return new StageTransitionCommandResult(false, false, $"段階遷移に失敗しました（{ex.GetType().Name}）");
        }
    }

    public async Task<StageGateStatusResult> EvaluateWithdrawalAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // POST（本文なし）。安全側の評価で、HaltNewEntries 成立時は Risk が kill switch を自動起動する。
            using var response = await httpClient
                .PostAsync("/risk-controls/stage-gate/withdrawal/evaluate", content: null, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return Fail("撤退評価", response.StatusCode);

            var view = await response.Content
                .ReadFromJsonAsync<WithdrawalAssessmentView>(cancellationToken)
                .ConfigureAwait(false);

            if (view is null)
            {
                logger.LogWarning("撤退評価の応答を解釈できませんでした。");
                return new StageGateStatusResult(false, "撤退評価の応答を解釈できませんでした");
            }

            return new StageGateStatusResult(true, FormatWithdrawal(view));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("撤退評価がタイムアウトしました。");
            return new StageGateStatusResult(false, "撤退評価がタイムアウトしました");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "撤退評価で例外が発生しました。");
            return new StageGateStatusResult(false, $"撤退評価に失敗しました（{ex.GetType().Name}）");
        }
    }

    private StageGateStatusResult Fail(string operation, HttpStatusCode status)
    {
        var hint = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? OwnerHint : string.Empty;
        logger.LogWarning("{Operation}に失敗しました（{Status}）。{Hint}", operation, (int)status, hint);
        return new StageGateStatusResult(false, $"{operation}に失敗しました（HTTP {(int)status}）{hint}");
    }

    // ---- 整形（数値 enum → 表示テキスト。ここに Risk の JSON 表現への結合を閉じる） ----

    internal static string FormatStatus(StageGateStatusView v)
    {
        var lines = new List<string>
        {
            $"現段階: {StageLabel(v.CurrentStage)}（モード: {ModeLabel(v.CurrentSettings.Mode)} / 資金上限: {v.CurrentSettings.CapitalCap:N0} 円）",
            FormatPromotion(v.Promotion),
            FormatWithdrawal(v.Withdrawal),
        };

        var history = v.History ?? [];
        if (history.Count > 0)
        {
            lines.Add("直近の遷移:");
            foreach (var t in history.OrderByDescending(h => h.Sequence).Take(RecentHistoryCount))
            {
                lines.Add(
                    $"  #{t.Sequence} {KindLabel(t.Kind)} {StageLabel(t.FromStage)}→{StageLabel(t.ToStage)}" +
                    $"（承認: {t.ApprovedBy} / {t.OccurredAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC）");
            }
        }

        return string.Join('\n', lines);
    }

    private static string FormatPromotion(PromotionAssessmentView p)
    {
        if (p.TargetStage is not { } target)
            return "昇格: これ以上の段階はありません（最上段）。";

        if (p.Eligible)
            return $"昇格: {StageLabel(target)} へ昇格可能です。";

        var reasons = FormatCriteria(p.UnmetCriteria);
        return $"昇格: {StageLabel(target)} へは未充足の基準があります（{reasons}）。";
    }

    internal static string FormatWithdrawal(WithdrawalAssessmentView w)
    {
        if (!w.Triggered)
            return "撤退評価: 撤退基準には抵触していません。";

        var proposal = w.ProposedStage is { } stage ? $" 提案: {StageLabel(stage)} へ差し戻し。" : string.Empty;
        var halt = w.HaltNewEntries ? " 新規建てを自動停止しました（kill switch 起動）。" : string.Empty;
        return $"撤退評価: 撤退基準に抵触（{WithdrawalReasonLabel(w.Reason)}）。{halt}{proposal}".TrimEnd();
    }

    private static string FormatRejection(IReadOnlyList<int>? reasons) =>
        $"段階遷移は受理されませんでした（未充足の基準: {FormatCriteria(reasons)}）。";

    private static string FormatCriteria(IReadOnlyList<int>? criteria) =>
        criteria is { Count: > 0 }
            ? string.Join(" / ", criteria.Select(CriterionLabel))
            : "理由不明";

    // TradingStage（0〜3・append-only）。未知値はフォールバック（数値のみ表示）。
    private static string StageLabel(int stage) => stage switch
    {
        0 => "Stage 0（検証）",
        1 => "Stage 1（ペーパー）",
        2 => "Stage 2（少額実弾）",
        3 => "Stage 3（拡大実弾）",
        _ => $"Stage {stage}",
    };

    // TradeMode（0=Paper・1=Live）。
    private static string ModeLabel(int mode) => mode switch
    {
        0 => "ペーパー",
        1 => "実弾",
        _ => $"不明({mode})",
    };

    // StageTransitionKind（0=Promotion・1=Demotion）。
    private static string KindLabel(int kind) => kind switch
    {
        0 => "昇格",
        1 => "差し戻し",
        _ => $"不明({kind})",
    };

    // WithdrawalReason（0=DrawdownBreachedMultiple・1=PaperDeviationUnexplained）。
    private static string WithdrawalReasonLabel(int? reason) => reason switch
    {
        0 => "実DDがバックテスト最大DD×倍率に到達",
        1 => "バックテストとの乖離が説明不能",
        null => "理由不明",
        _ => $"不明な理由({reason})",
    };

    // StageGateCriterion（0〜8・append-only）。未知値は fail-safe のフォールバック文言に倒す。
    private static string CriterionLabel(int criterion) => criterion switch
    {
        0 => "バックテスト未合格（Stage 0→1）",
        1 => "バックテストとの乖離が説明不能（Stage 1→2）",
        2 => "統制違反あり（Stage 1→2）",
        3 => "実効スリッページ・費用が想定超過（Stage 2→3）",
        4 => "日次損失上限の運用違反あり（Stage 2→3）",
        5 => "承認がありません",
        6 => "昇格は 1 段ずつ（飛び級不可）",
        7 => "遷移先が現段階と同じ",
        8 => "既に最上段（昇格先なし）",
        _ => $"不明な基準({criterion})",
    };

    // ---- Risk 応答の射影 DTO（数値 enum を int で受ける。web JSON = camelCase） ----

    internal sealed record StageGateStatusView(
        int CurrentStage,
        StageSettingsView CurrentSettings,
        IReadOnlyList<StageTransitionView>? History,
        PromotionAssessmentView Promotion,
        WithdrawalAssessmentView Withdrawal);

    internal sealed record StageSettingsView(int Stage, int Mode, decimal CapitalCap);

    internal sealed record StageTransitionView(
        int Sequence,
        int FromStage,
        int ToStage,
        int Kind,
        string ApprovedBy,
        DateTimeOffset OccurredAtUtc,
        string Reason);

    internal sealed record PromotionAssessmentView(int? TargetStage, bool Eligible, IReadOnlyList<int>? UnmetCriteria);

    internal sealed record WithdrawalAssessmentView(bool Triggered, int? Reason, bool HaltNewEntries, int? ProposedStage);

    internal sealed record StageTransitionResultView(
        bool Accepted,
        StageTransitionView? Transition,
        StageSettingsView? ResultingSettings,
        IReadOnlyList<int>? RejectionReasons);
}
