using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Logging;
using ReportService.Features.Reports;
using ReportService.Domain;
using Microsoft.Extensions.Logging;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06/16, IADR-0071 決定1, ADR-0003: 報告書の散文ドラフトを platform LLM ゲートウェイ（POST /complete）へ委譲する実装。
// IADR-0061（#11 の実 LLM 接続）と同形の安全既定・fail-safe に倣う:
// - プロンプトは純関数 ReportNarrativePromptBuilder で構築（散文のみ・数値は再計算/改変しない＝数値はコード集計が権威・FR-16）。
// - 送信拒否（Sent=false）・非 2xx・タイムアウト・空/不正応答・例外は「プレースホルダ散文」へ倒す。取引判断の Hold と異なり
//   報告書は発注を伴わないため、安全側＝捏造しない定型散文（数値には一切関与しない）。
// - NFR-05, #724, IADR-0323: **`/complete` へは MSP レルムの s2s トークンを付ける。**
//   〔2026-09-10 是正〕従前ここには「MSP/ADR-0010: /complete は匿名エンドポイントゆえ s2s トークンは付けない」と
//   書いてあったが、基盤が REST 3 口へ端点単位の認可（`ServiceCaller`）を掛けたため事実でなくなった（MSP#1364）。
//   付与は Program.cs の名前付き HttpClient（"report-llm"）で `LlmGateway:Auth` から行う。未設定なら付けない
//   ＝現行どおり（→ 認可を要求する上流では 401 → 下の非 2xx 分岐でプレースホルダ散文へ倒れる＝安全側）。
//   リトライはゲートウェイ側一元化（MSP/ADR-0010）に委ね重ねない。
// - IADR-0061 決定1: logPrompts=true でプロンプト本文と LLM 生出力を全量記録する。既定オフ＝機微を既定でログ基盤へ流さない。
// - IADR-0120 決定1/2: purpose は要求ごとに種別（context.Kind）から決める。purposeOverride は構成
//   LlmGateway:Purpose の明示設定で、指定時は全種別へ適用する（既存デプロイの非破壊）。
// - IADR-0123 決定1, #308: タイムアウトも要求ごとに種別から決める（timeoutFor）。種別ごとに別モデルが
//   割り当たる（IADR-0120）以上、サービス共通の 1 本では週報・月報が構造的に間に合わない。
//   timeoutFor 未注入なら従来どおり HttpClient.Timeout のみが効く（非破壊）。
// - #335, #347, ADR-0017 決定4, IADR-0217/0219: 送信が成立した応答から **①実効モデル（報告書メタ）**・
//   **②フォールバック発火の通知**・**③費用の計上（月報の利用実績）** の 3 つを取り出す。
//   従来は応答の `Model` を受け取りながら捨てており、報告書がどのモデルで書かれたか誰も知り得なかった。
// NFR, MSP:ADR-0029, IADR-0284, IADR-0328, IADR-0332, #746: **輸送は差し替えられる。**
// 🔴 本クラスは「送る」ではなく**「応答を解釈してプレースホルダへ振り分ける判定器」**であり、
// REST（既定）と east-west gRPC のどちらで送っても同じ判定を通す（`ILlmCompletionTransport`）。
// クラス名 `Http…` は REST しか無かった頃の名残であり、**輸送を意味しない**（改名は挙動を変えないため
// 別 PR。IADR-0332 決定 5）。
public sealed class HttpReportNarrativeDrafter(
    ILlmCompletionTransport transport,
    ILogger<HttpReportNarrativeDrafter> logger,
    string confidentiality,
    string? purposeOverride,
    bool logPrompts = false,
    Func<ReportKind, TimeSpan>? timeoutFor = null,
    ILlmUsageReporter? usageReporter = null,
    ILlmGovernanceReporter? governanceReporter = null,
    TimeSpan? transportTimeout = null)
    : IReportNarrativeDrafter
{
    /// <summary>
    /// REST（`POST /complete`）で送る従来の形。既存の配線・試験はこちらを使う（既定は REST のまま）。
    /// 種別別の上限が無い構成では <c>HttpClient.Timeout</c> が上限であり、縮退ログの秒数もそこから採る
    /// （IADR-0123 決定 5）。
    /// </summary>
    public HttpReportNarrativeDrafter(
        HttpClient httpClient,
        ILogger<HttpReportNarrativeDrafter> logger,
        string confidentiality,
        string? purposeOverride,
        bool logPrompts = false,
        Func<ReportKind, TimeSpan>? timeoutFor = null,
        ILlmUsageReporter? usageReporter = null,
        ILlmGovernanceReporter? governanceReporter = null)
        : this(new RestLlmCompletionTransport(httpClient), logger, confidentiality, purposeOverride,
            logPrompts, timeoutFor, usageReporter, governanceReporter, httpClient.Timeout)
    {
    }

    private readonly ILlmUsageReporter _usage = usageReporter ?? new NoOpLlmUsageReporter();
    private readonly ILlmGovernanceReporter _governance = governanceReporter ?? new NoOpLlmGovernanceReporter();

    public async Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default) =>
        (await DraftAsync(context, cancellationToken).ConfigureAwait(false)).Text;

    public async Task<ReportNarrativeDraft> DraftAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var prompt = ReportNarrativePromptBuilder.Build(context);

        // IADR-0120 決定1, #291: 報告書は方針階層（月報→週報→日報→取引）をなす方針書であり、上位ほど難度が高い。
        // 種別ごとの purpose を送ることで基盤の Llm:Routing:PurposeModels が種別ごとのモデルを解決する。
        // 従来は単一の固定値を送っており、基盤に該当エントリが無いため 3 種別すべてが DefaultModel へ着地していた。
        var purpose = string.IsNullOrWhiteSpace(purposeOverride)
            ? ReportNarrativePurpose.For(context.Kind)
            : purposeOverride;

        // IADR-0123 決定1, #308: 種別ごとの上限を要求単位で適用する。呼び出し側の停止要求と linked にすることで、
        // 「タイムアウト（縮退してよい）」と「停止要求（伝播すべき）」の区別は下の catch の条件式がそのまま担う。
        var timeout = timeoutFor?.Invoke(context.Kind);
        using var timeoutCts = timeout is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts?.CancelAfter(timeout!.Value);
        var requestToken = timeoutCts?.Token ?? cancellationToken;

        // #335, ADR-0017 決定4-(1), IADR-0217: 報告書のメタ情報へ残す「実際に使用したモデル」。
        // 応答が返るまでは **null＝照会できていない**であり、「フォールバックしていない」ではない。
        // 縮退（非 2xx・タイムアウト・送信拒否）でプレースホルダへ倒れた場合も null のまま返す。
        LlmModelUsage? modelUsage = null;

        try
        {
            // NFR, IADR-0316, #708: プロンプト本文は外部由来（収集情報・LLM 生成の前段散文）を含み、改行や ESC を
            // 素通しすると行指向のログへ偽の行を注入できる（CWE-117）。**発生源で正規化してから渡す。**
            if (logPrompts)
                logger.LogInformation("報告書散文 LLM 要求: kind={Kind} periodKey={PeriodKey} prompt={Prompt}",
                    context.Kind, context.PeriodKey, LogSanitizer.Sanitize(prompt));

            // IADR-0101, MSP/ADR-0025: MaxTokens は思考トークンと本文の合算上限（Opus 5 等は thinking が既定有効）。
            // 1024 のままだと思考が上限を食い、途中で切れた文章がそのまま成果物になる（安全網なし）。
            // IADR-0120 決定1: Model は引き続き明示しない（null）＝モデルの決定権は基盤の LlmRouter に残す。
            // AST がモデル ID を持つと NonZdrModels による除外や版数改定へ追随できず、許可一覧との整合も崩れる。
            // IADR-0123 決定1 / IADR-0332 決定4: 種別ごとの上限を要求単位で渡す。REST は上の CTS が担い、
            // gRPC は `CallOptions.Deadline` へ写す（deadline はサーバ側へも伝播する）。
            var exchange = await transport
                .CompleteAsync(
                    new LlmCompletionCall(prompt, MaxTokens: 4096, Model: null, confidentiality, purpose),
                    timeout,
                    requestToken)
                .ConfigureAwait(false);

            if (exchange.Outcome == LlmTransportOutcome.Failed)
            {
                // NFR-05, #724, IADR-0323: 倒れ先はプレースホルダ散文のまま（報告書は発注を伴わない＝安全側）だが、
                // **原因は取り違えない。** 認可の失敗（401/403・gRPC の UNAUTHENTICATED / PERMISSION_DENIED）を
                // 「モデルが使えない」と読める記録にしない。
                var status = exchange.Detail;
                if (exchange.Failure == LlmFailureKind.Unauthorized)
                    logger.LogWarning(
                        "報告書散文 LLM /complete が認可を拒否しました（{Status}）。s2s の資格情報（LlmGateway:Auth）または"
                        + "サービスアカウントの付与ロールを確認してください。プレースホルダ散文に倒します（モデルの可否とは無関係）。",
                        status);
                else
                    logger.LogWarning("報告書散文 LLM /complete が非 2xx（{Status}）。プレースホルダ散文に倒します。", status);

                return Placeholder(modelUsage);
            }

            // Sent=false は機密区分による送信拒否（縮退）。空応答・欠落もプレースホルダ散文に倒す。
            // #247, IADR-0104 決定3: 縮退の理由（応答不正 / 送信拒否 / 拒否 / 空応答 / 上限到達）を区別して記録する。
            if (exchange.Outcome == LlmTransportOutcome.Malformed)
            {
                if (exchange.Error is { } malformed)
                    logger.LogWarning(malformed, "報告書散文 LLM /complete の応答を解釈できません（不正 JSON・想定外の形式）。プレースホルダ散文に倒します。");
                else
                    logger.LogWarning("報告書散文 LLM /complete の応答が空です（JSON null）。プレースホルダ散文に倒します。");
                return Placeholder(modelUsage);
            }

            var dto = exchange.Payload!;

            if (!dto.Sent)
            {
                logger.LogWarning("報告書散文 LLM が送信不可（Sent=false・機密区分による縮退）。プレースホルダ散文に倒します。");
                return Placeholder(modelUsage);
            }

            // IADR-0061 決定1: 生出力の全量記録は、以降で破棄し得る本文も含めて拒否・空応答の判定より前に行う。
            // NFR, IADR-0316, #708: 生出力は最も直接的な外部入力である。全量記録という目的は変えずに、
            // 制御文字だけを潰して 1 行へ収める（本文の先頭は残るため、調査の用は足りる）。
            if (logPrompts)
                logger.LogInformation("報告書散文 LLM 応答: model={Model} stopReason={StopReason} text={Text}",
                    dto.Model, dto.StopReason, LogSanitizer.Sanitize(dto.Text));

            // #347, IADR-0219, IADR-0104 決定4: 送信が成立した（Sent=true）応答のトークンを費用計測へ渡す。
            // 本文の扱い（拒否・空・上限到達で破棄するか否か）とは独立に課金は発生しているため、
            // 本文を読む前に一度だけ計測する。**報告書の費用は月次上限の対象外だが、実績は月報に記載する**
            // （05_trading-assumptions §6.1。対象範囲の判別は購読側が purpose で行う）。
            // 計測は best-effort＝失敗しても報告書生成は壊さない。
            try
            {
                await _usage
                    .ReportAsync(
                        new LlmUsage(purpose, dto.InputTokens ?? 0, dto.OutputTokens ?? 0, dto.Model), requestToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "報告書散文の LLM 費用計測の報告に失敗しました（応答は継続）。");
            }

            // #335, ADR-0017 決定4, IADR-0217: 実効モデルを割当表と突き合わせ、①メタ情報へ載せ、
            // ピン以外が答えたなら②警告通知・③月報集計の供給元へ流す。
            // **沈黙のフォールバックを作らないことが決定 4 の目的である。**
            var evaluation = LlmAssignmentEvaluator.Evaluate(purpose, dto.Model);
            modelUsage = new LlmModelUsage(
                purpose, evaluation.ExpectedModel, evaluation.EffectiveModel, evaluation.Outcome.ToString());

            if (evaluation.Outcome != LlmAssignmentOutcome.Primary)
                await ReportFallbackAsync(evaluation, purpose, requestToken).ConfigureAwait(false);

            // ADR-0015 / ADR-0017 決定1: 本システムで使用しないと決めたモデルの出力は成果物にしない。
            // 報告書はフォールバックを許すが、**禁止モデル（ZDR 非対応）だけは許可集合の外側**である。
            // 未割当（基盤の DefaultModel へ落ちた等）は記録に留めて本文を採る——報告書は発注を伴わず、
            // 基盤の構成ドリフトのたびに方針階層が途切れる不利益のほうが大きい（ADR-0017 §理由）。
            if (evaluation.Outcome == LlmAssignmentOutcome.Forbidden)
            {
                logger.LogWarning(
                    "報告書散文が本システムで使用しないモデルで生成されました（model={Model}）。本文を破棄し、プレースホルダ散文に倒します。",
                    evaluation.EffectiveModel);
                return Placeholder(modelUsage);
            }

            // #247, IADR-0104 決定2: 拒否（安全性分類器による停止）は**本文を読む前に**評価し、本文が非空でも破棄する。
            // 拒否された断片が報告書の成果物になることを、上流の破棄実装に依存せず防ぐ（多層防御）。
            if (LlmStopReasons.IsRefusal(dto.StopReason))
            {
                logger.LogWarning(
                    "報告書散文 LLM が要求を拒否しました（stopReason={StopReason} textLength={TextLength}）。本文を破棄し、プレースホルダ散文に倒します。",
                    dto.StopReason, dto.Text?.Length ?? 0);
                return Placeholder(modelUsage);
            }

            if (string.IsNullOrWhiteSpace(dto.Text))
            {
                logger.LogWarning("報告書散文 LLM の応答本文が空です（stopReason={StopReason}）。プレースホルダ散文に倒します。",
                    dto.StopReason);
                return Placeholder(modelUsage);
            }

            // IADR-0104 決定5, IADR-0101: 上限到達は拒否ではなく劣化。本文は破棄せず（途中で切れることの観測を残し）、
            // 劣化として記録するにとどめる。
            if (LlmStopReasons.IsMaxTokens(dto.StopReason))
                logger.LogWarning(
                    "報告書散文 LLM の応答が出力上限に到達しました（stopReason={StopReason}）。散文が途中で切れている可能性があります。",
                    dto.StopReason);

            return new ReportNarrativeDraft(dto.Text, modelUsage);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // IADR-0123 決定5: どの上限で切られたのかを残す（種別ごとに上限が変わるため、秒数が無いと切り分けできない）。
            // timeout が null（種別別の上限が無い構成）のときは HttpClient 自体の上限で切られている。
            logger.LogWarning(
                "報告書散文 LLM /complete がタイムアウト（kind={Kind} timeoutSeconds={TimeoutSeconds}）。プレースホルダ散文に倒します。",
                context.Kind, (timeout ?? transportTimeout)?.TotalSeconds);
            return Placeholder(modelUsage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "報告書散文 LLM /complete で例外。プレースホルダ散文に倒します。");
            return Placeholder(modelUsage);
        }
    }

    // 縮退時の戻り値。散文はプレースホルダ（捏造しない定型文）、メタは分かっている分だけ載せる。
    private static ReportNarrativeDraft Placeholder(LlmModelUsage? modelUsage) =>
        new(ReportNarrativeDefaults.PlaceholderText, modelUsage);

    // #335, ADR-0017 決定4: 発火の通知は best-effort（記録に失敗しても報告書生成は壊さない）。
    private async Task ReportFallbackAsync(
        LlmAssignmentEvaluation evaluation, string purpose, CancellationToken cancellationToken)
    {
        try
        {
            await _governance.FallbackFiredAsync(evaluation, purpose, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "報告書散文の割当逸脱の記録に失敗しました（応答は継続）。");
        }
    }

    // 要求・応答の写像（REST の CompletionApiRequest / CompletionApiResponse、gRPC の
    // CompleteRequest / CompleteResponse）は輸送側（`RestLlmCompletionTransport` /
    // `GrpcLlmCompletionTransport`）へ移した。本クラスが読むのは輸送に依らない `LlmCompletionPayload` である。
    // Sent=false は送信拒否（縮退）。#247, IADR-0104: StopReason は送信が成立した場合のモデル側の終了理由
    // （Sent とは独立した軸）。未設定（null）＝上流未更新・未対応プロバイダでは従来どおりの分岐へ素通りする。
    // #347, IADR-0219: InputTokens/OutputTokens は費用計測の入力。欠落時は 0 として扱う（部分写像・非破壊）。
}
