using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Logging;
using TradeDecisionService.Infrastructure.ExternalServices;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// #79, FR-04, ADR-0003, IADR-0017/0039: 実 LLM 補完を platform LLM ゲートウェイ（POST /complete）へ委譲する。
// ADR-0010（platform LLM ゲートウェイの越境ルーティング。本リポの FR-11=監査ログとは別物のため ID を使わず ADR で示す）:
// confidentiality/purpose を載せて送信先を判定させる（送信可否・モデル選択はゲートウェイ側）。
// フェイルセーフ（IADR-0017 の安全既定と一致）: 送信拒否（Sent=false）・非 2xx・例外・タイムアウト・空/不正応答は
// Hold（取引しない）に倒す。判断パーサはこの JSON を Hold として解釈する。
// #247, IADR-0104: さらに終了理由（stopReason）を**本文を読む前に**評価し、拒否（refusal）は本文が非空でも破棄して
// Hold に倒す（上流のゲートウェイが断片を破棄することに依存しない多層防御）。Hold の理由は系統別に分けて監査へ残す。
// #79, IADR-0055 決定3: 成功応答のトークンを ILlmUsageReporter へ渡す（計測点は egress）。既定 NoOp＝publish しない。
// #11, FR-11, IADR-0061 決定1: logPrompts=true でプロンプト本文と LLM 生出力を全量記録する（判断根拠の事後再構成）。
// プロンプトは保有ポジション・資金残枠等の機微を含むため既定オフ＝記録しない（最小権限）。
// #335, ADR-0014 §決定3, ADR-0017 決定2/決定3, IADR-0216: 取引判断は**フォールバックしない**。
// - 上流の失敗は 429（再試行）・401/403（認可）・残る 400 系（モデル不可）へ分け、モデル不可のときだけ
//   見送りとして記録・通知する（NFR-05, #724, IADR-0323 で認可を独立させた。それ以前は 401 がモデル不可へ倒れていた）。
// - 応答が返っても**ピン留めしたモデル以外が答えたなら本文を読まずに破棄する**（基盤で用途エントリが未登録・
//   ZDR 除外・提供終了だと LlmRouter が無音で DefaultModel へ落ちるため。platform IADR-0102）。
// いずれの経路でも返すのは Hold であり、**発注は構造的に生じない**。見送りは障害ではなく設計上の正常な結果である。
// #335, ADR-0014, ADR-0017, IADR-0212: **用途（purpose）は呼び出しごとに決まる。**
// 二段判断は層ごとに別の用途を名乗り（一次=trade-decision-screening／二次=trade-decision）、割当モデルの照合も
// 費用の計上区分もその用途で引かれる。`purposeOverride` は構成 `LlmGateway:Purpose` の明示設定で、指定時は
// 全呼び出しへ適用する（既存デプロイの非破壊。報告書側 HttpReportNarrativeDrafter と同型）。
// NFR, MSP:ADR-0029, IADR-0284, IADR-0328, IADR-0332, #746: **輸送は差し替えられる。**
// 🔴 本クラスは「送る」ではなく**「応答を解釈して Hold へ振り分ける判定器」**であり、REST（既定）と
// east-west gRPC のどちらで送っても同じ判定を通す（`ILlmCompletionTransport`）。gRPC 用の判定器を
// 別に作らないのは、片方だけ直る事故を構造的に入れないためである。
// クラス名 `Http…` は REST しか無かった頃の名残であり、**輸送を意味しない**（改名は挙動を変えないため
// 別 PR。IADR-0332 決定 5）。
public sealed class HttpLlmCompletionClient(
    ILlmCompletionTransport transport,
    ILogger<HttpLlmCompletionClient> logger,
    string confidentiality,
    string? purposeOverride,
    ILlmUsageReporter usageReporter,
    bool logPrompts = false,
    ILlmGovernanceReporter? governanceReporter = null)
    : ILlmCompletionClient
{
    /// <summary>
    /// REST（`POST /complete`）で送る従来の形。既存の配線・試験はこちらを使う（既定は REST のまま）。
    /// </summary>
    public HttpLlmCompletionClient(
        HttpClient httpClient,
        ILogger<HttpLlmCompletionClient> logger,
        string confidentiality,
        string? purposeOverride,
        ILlmUsageReporter usageReporter,
        bool logPrompts = false,
        ILlmGovernanceReporter? governanceReporter = null)
        : this(new RestLlmCompletionTransport(httpClient), logger, confidentiality, purposeOverride,
            usageReporter, logPrompts, governanceReporter)
    {
    }

    // 割当統制の記録先。未注入は安全既定（記録しないだけで、見送りの統制自体は本クラスが担う）。
    private readonly ILlmGovernanceReporter _governance = governanceReporter ?? new NoOpLlmGovernanceReporter();

    // IADR-0212: 用途の解決は 1 箇所に閉じる（構成の明示上書き → 呼び出し側の申告 → 安全既定の順）。
    // 安全既定を取引判断（本判断）にするのは、**費用上限の対象内**かつ**最も厳しい割当統制**が掛かる側だからである
    // （用途不明の呼び出しを対象外・統制外へ倒さない）。
    private string ResolvePurpose(string? purpose) =>
        string.IsNullOrWhiteSpace(purposeOverride)
            ? (string.IsNullOrWhiteSpace(purpose) ? LlmPurposes.TradeDecision : purpose)
            : purposeOverride;

    // #247, IADR-0104 決定3: Hold へ倒れる理由を系統別に分ける。倒れる先はいずれも Hold（IADR-0017 の安全既定は不変）だが、
    // 「なぜ倒れたか」を監査（FR-11）で切り分けられなければ運用で原因を追えない。
    private const string HoldFallback = """{"action":"Hold","rationale":"LLM ゲートウェイ送信不可のため見送り"}""";
    private const string HoldMalformed = """{"action":"Hold","rationale":"LLM ゲートウェイ応答不正のため見送り"}""";
    private const string HoldRefused = """{"action":"Hold","rationale":"LLM が要求を拒否したため見送り"}""";
    private const string HoldEmpty = """{"action":"Hold","rationale":"LLM 応答が空のため見送り"}""";
    private const string HoldMaxTokens = """{"action":"Hold","rationale":"LLM 応答が出力上限に到達し本文が無いため見送り"}""";

    // #335, ADR-0017 決定2: 割当モデルが使えないための見送り。**障害ではなく設計上の正常な結果**である。
    // 伝送の失敗（HoldFallback）と区別して記録する——「使えるモデルが無かった」は運用の判断材料が違う。
    private const string HoldModelUnavailable = """{"action":"Hold","rationale":"割当モデルが利用できないため取引判断を見送り（フォールバック禁止）"}""";

    // NFR-05, #724, IADR-0323: **認可の失敗による見送り。** 上の HoldModelUnavailable と必ず分ける——
    // 401/403 は「資格情報・付与ロールが足りない」であって「モデルが使えない」ではない。混ぜると
    // 監査台帳・月報に「割当モデルが利用できない」という誤った原因が残り、後から見た人が LLM 提供側を疑う。
    private const string HoldUnauthorized = """{"action":"Hold","rationale":"LLM ゲートウェイの認可が拒否されたため取引判断を見送り（資格情報・権限の不足）"}""";

    public async Task<string> CompleteAsync(
        string prompt, string? model = null, string? purpose = null, CancellationToken cancellationToken = default)
    {
        // IADR-0212: 以降の判定（送信の purpose・割当照合・見送りの記録・費用の計上区分）はすべてこの 1 値を使う。
        var effectivePurpose = ResolvePurpose(purpose);

        try
        {
            // FR-11, IADR-0061 決定1: 送信前にプロンプト全量を記録する（応答前に失敗しても入力は残る）。
            // NFR, IADR-0316, #708: プロンプトは収集した外部テキストを含むため、行指向のログへ偽の行を
            // 注入され得る（CWE-117）。**発生源で正規化してから渡す**（既定オフはこれの代替にならない）。
            if (logPrompts)
                logger.LogInformation("LLM 要求: model={Model} prompt={Prompt}", model, LogSanitizer.Sanitize(prompt));

            // IADR-0101, MSP/ADR-0025: MaxTokens は思考トークンと本文の合算上限（Opus 5 等は thinking が既定有効）。
            // purpose=trade-decision は基盤の PurposeModels に未登録で default（Opus 5 化される層）へ着地するため、
            // 1024 のままだと思考が上限を食い本文が空になり、下の空応答判定で全判断が Hold に固定される。
            var exchange = await transport
                .CompleteAsync(
                    new LlmCompletionCall(prompt, MaxTokens: 4096, model, confidentiality, effectivePurpose),
                    // 上限は輸送側が持つ（REST は HttpClient.Timeout、gRPC は構成の既定 deadline）。
                    // 取引判断は報告書と違い**種別による上限差が無い**ため、要求単位の指定はしない。
                    deadline: null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (exchange.Outcome == LlmTransportOutcome.Failed)
            {
                // #335, ADR-0017 決定3, IADR-0216: 429（再試行）と 400 系（モデル不可）を分ける。
                // **429 でスキップ事象を出さない** —— 混雑のたびに「モデルが使えない」という誤った
                // 運用シグナルが積み上がり、恒常的な格下げを疑う根拠になってしまう。
                // IADR-0332 決定 3: gRPC でも同じ語彙へ写す（status 名が `{Status}` に入る）。
                var status = exchange.Detail;
                var kind = exchange.Failure;

                // NFR-05, #724, IADR-0323: 認可の失敗（401/403）は**モデル不可へ倒さない**。
                // 🔴 TradeDecisionSkipped も publish しない —— 同イベントの通知本文は
                // 「取引判断の見送り: 割当モデルが利用できません」と題名に焼き込まれており
                //（NotificationFormatter）、事由の文字列だけ足しても誤帰属を別の層で再生産する。
                // 見送り自体は Hold として成立し、理由は下の専用の rationale が監査へ残す。
                if (kind == LlmFailureKind.Unauthorized)
                {
                    logger.LogWarning(
                        "LLM ゲートウェイ /complete が認可を拒否しました（{Status}）。s2s の資格情報（LlmGateway:Auth）または"
                        + "サービスアカウントの付与ロールを確認してください。取引判断を実行せず見送ります（モデルの可否とは無関係）。",
                        status);
                    return HoldUnauthorized;
                }

                if (kind == LlmFailureKind.ModelUnavailable)
                {
                    logger.LogWarning(
                        "LLM ゲートウェイ /complete がモデル不可（{Status}）。取引判断を実行せず見送ります（フォールバック禁止）。",
                        status);
                    await ReportSkipAsync(
                        effectivePurpose, TradeDecisionSkipReasons.ModelUnavailable, effectiveModel: null, cancellationToken)
                        .ConfigureAwait(false);
                    return HoldModelUnavailable;
                }

                logger.LogWarning(
                    "LLM ゲートウェイ /complete が非 2xx（{Status}・{Kind}）。取引しない安全側（Hold）に倒します。",
                    status, kind);
                return HoldFallback;
            }

            // 応答本文が空・不正 JSON で写像できない場合。取引しない安全側に倒す（送信可否は不明のため、伝送の失敗＝
            // HoldFallback とは区別して記録する。IADR-0104 決定3）。
            if (exchange.Outcome == LlmTransportOutcome.Malformed)
            {
                if (exchange.Error is { } malformed)
                    logger.LogWarning(malformed, "LLM ゲートウェイ /complete の応答を解釈できません（不正 JSON・想定外の形式）。取引しない安全側（Hold）に倒します。");
                else
                    logger.LogWarning("LLM ゲートウェイ /complete の応答が空です（JSON null）。取引しない安全側（Hold）に倒します。");
                return HoldMalformed;
            }

            var dto = exchange.Payload!;

            // Sent=false は機密区分による送信拒否（縮退）＝越境させておらず費用も発生していない。取引しない安全側に倒す。
            if (!dto.Sent)
            {
                logger.LogWarning("LLM ゲートウェイが送信不可（Sent=false・機密区分による縮退）。取引しない安全側（Hold）に倒します。");
                return HoldFallback;
            }

            // FR-11, IADR-0061 決定1: LLM の生出力を全量記録する（構造化解析前＝パーサが Hold へ丸める前の原文）。
            // #247, IADR-0104: 拒否・空応答の判定より前に記録し、以降で破棄する本文も事後に再構成できるようにする。
            // NFR, IADR-0316, #708: 生出力は最も直接的な外部入力である。全量記録の目的は変えずに 1 行へ収める。
            if (logPrompts)
                logger.LogInformation("LLM 応答: model={Model} tokens={Input}/{Output} stopReason={StopReason} text={Text}",
                    dto.Model, dto.InputTokens ?? 0, dto.OutputTokens ?? 0, dto.StopReason, LogSanitizer.Sanitize(dto.Text));

            // #79, IADR-0055, IADR-0104 決定4: 送信が成立した（Sent=true）応答のトークンを費用計測へ渡す。本文の扱い
            //（拒否・空・上限到達で破棄するか否か）とは独立に課金は発生しているため、本文を読む前に一度だけ計測する
            // （とくに上限到達は思考トークンを消費し切って本文が空になる形で課金される・IADR-0101）。
            // 計測は best-effort＝失敗しても LLM 応答は壊さない（費用計測の不調で取引判断を Hold に倒すのは過剰。
            // 計上漏れは at-least-once 再配信で緩和される）。
            // #303, IADR-0122 決定1: 応答が名乗った実効モデル（dto.Model）を併せて渡す。用途別モデル割当
            //（ADR-0014 / MSP/IADR-0112）でモデルが混在するため、単価は要求側の希望値ではなく実効モデルで引く。
            try
            {
                // NFR（費用）, #347, IADR-0212/0218: **その呼び出しの用途**を載せる。二段判断は層ごとに用途が違い、
                // 計上の区分（月次上限の対象範囲）は購読側が purpose だけを見て決めるため、層が混ざると内訳が壊れる。
                await usageReporter
                    .ReportAsync(
                        new LlmUsage(effectivePurpose, dto.InputTokens ?? 0, dto.OutputTokens ?? 0, dto.Model),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "LLM 費用計測の報告に失敗しました（応答は継続）。");
            }

            // #335, ADR-0014 §決定3, ADR-0017 決定2, IADR-0216: **ピン留めしたモデルが答えたかを本文より先に見る。**
            // 費用計測の後に置くのは、送信が成立した以上 課金は発生しており、モデルが違っても計上は要るためである。
            //
            // 🔴 ここで倒れるのは「取引判断を実行しない」であって障害ではない。別モデルの応答で発注すると、
            // ADR-0014 §決定3 が実弾解禁の条件に掲げた「検証したモデルと本番モデルの一致」がその場で空洞化する。
            // 鎖（フォールバック先）を持たない用途で別モデルが答えるのは、基盤の用途エントリが未登録・ZDR 除外・
            // 提供終了で `DefaultModel` へ無音に落ちたときであり、**まさに検知したい事象**である。
            var evaluation = LlmAssignmentEvaluator.Evaluate(effectivePurpose, dto.Model);
            if (!evaluation.Allowed)
            {
                var reason = evaluation.Outcome switch
                {
                    LlmAssignmentOutcome.Forbidden => TradeDecisionSkipReasons.ForbiddenModel,
                    _ => TradeDecisionSkipReasons.ModelMismatch,
                };

                logger.LogWarning(
                    "割当モデル以外が応答しました（purpose={Purpose} expected={Expected} effective={Effective} outcome={Outcome} textLength={TextLength}）。"
                    + "本文を破棄し、取引判断を実行せず見送ります（フォールバック禁止）。",
                    effectivePurpose, evaluation.ExpectedModel, evaluation.EffectiveModel, evaluation.Outcome,
                    dto.Text?.Length ?? 0);

                // ADR-0017 決定4: 発火は「埋もれない経路」で出す。見送りの記録（決定2）と両方を出す——
                // 前者は「割当が効いていない」、後者は「取引機会を逸した」という別々の運用事実である。
                await ReportFallbackAsync(evaluation, effectivePurpose, cancellationToken).ConfigureAwait(false);
                await ReportSkipAsync(effectivePurpose, reason, evaluation.EffectiveModel, cancellationToken)
                    .ConfigureAwait(false);
                return HoldModelUnavailable;
            }

            // #247, IADR-0104 決定2: 拒否（安全性分類器による停止）は**本文を読む前に**評価する。分類器は本文の途中で
            // 停止し得るため、上流が非空の断片を渡してきても判断材料にしない（上流の破棄実装に依存しない多層防御）。
            // 本文長だけは残す（全量ログ無効でも「断片が届いた＝この防御が効いた」事実を観測できるようにする）。
            if (LlmStopReasons.IsRefusal(dto.StopReason))
            {
                logger.LogWarning(
                    "LLM が要求を拒否しました（stopReason={StopReason} textLength={TextLength}）。本文を破棄し、取引しない安全側（Hold）に倒します。",
                    dto.StopReason, dto.Text?.Length ?? 0);
                return HoldRefused;
            }

            // 空応答も取引しない安全側に倒す。上限到達（思考が上限を食い本文が空・IADR-0101）は原因が異なるため区別する。
            if (string.IsNullOrWhiteSpace(dto.Text))
            {
                logger.LogWarning("LLM ゲートウェイの応答本文が空です（stopReason={StopReason}）。取引しない安全側（Hold）に倒します。",
                    dto.StopReason);
                return LlmStopReasons.IsMaxTokens(dto.StopReason) ? HoldMaxTokens : HoldEmpty;
            }

            // IADR-0104 決定5, IADR-0101: 上限到達は拒否ではなく劣化。本文は破棄せず判断へ渡し（破棄すると「本文が途中で
            // 切れる」ことの観測ができなくなる）、劣化として記録するにとどめる。
            if (LlmStopReasons.IsMaxTokens(dto.StopReason))
                logger.LogWarning(
                    "LLM 応答が出力上限に到達しました（stopReason={StopReason}）。本文が途中で切れている可能性があります。",
                    dto.StopReason);

            return dto.Text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // タイムアウト（呼び出し側キャンセルではない）＝ゲートウェイ応答遅延。取引しない安全側に倒す。
            logger.LogWarning("LLM ゲートウェイ /complete がタイムアウト。取引しない安全側（Hold）に倒します。");
            return HoldFallback;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "LLM ゲートウェイ /complete で例外。取引しない安全側（Hold）に倒します。");
            return HoldFallback;
        }
    }

    // #335, ADR-0017 決定2/決定4: 記録は best-effort＝失敗しても取引判断の結果（Hold）を変えない。
    // 記録できないことを理由に発注へ進むことは無いため、握り潰しても統制は緩まない（緩むのは可観測性だけ）。
    private async Task ReportSkipAsync(
        string purpose, string reason, string? effectiveModel, CancellationToken cancellationToken)
    {
        try
        {
            await _governance
                .DecisionSkippedAsync(
                    purpose, reason, LlmAssignments.For(purpose)?.PrimaryModel, effectiveModel, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "取引判断の見送りの記録に失敗しました（見送り自体は成立しています）。");
        }
    }

    private async Task ReportFallbackAsync(
        LlmAssignmentEvaluation evaluation, string purpose, CancellationToken cancellationToken)
    {
        try
        {
            await _governance.FallbackFiredAsync(evaluation, purpose, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "割当逸脱の記録に失敗しました（見送り自体は成立しています）。");
        }
    }

    // 要求・応答の写像（REST の CompletionApiRequest / CompletionApiResponse、gRPC の
    // CompleteRequest / CompleteResponse）は輸送側（`RestLlmCompletionTransport` /
    // `GrpcLlmCompletionTransport`）へ移した。本クラスが読むのは輸送に依らない `LlmCompletionPayload` である。
    // Sent=false は送信拒否（縮退）。InputTokens/OutputTokens は費用計測の入力（#79・IADR-0055）。
    // Model はゲートウェイが実際に選択したモデル（要求の Model は希望値であり、越境ルーティングで変わり得る）。
    // #247, IADR-0104: StopReason は**送信が成立した**場合のモデル側の終了理由（"end_turn" / "max_tokens" / "refusal" 等）で、
    // Sent とは独立した軸（Sent=false＝越境させていない／StopReason="refusal"＝送信したがモデルが拒否した）。
}
