using System.Net.Http.Json;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.TradeDecision.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TradeDecision.Worker.Composable.Adapters;

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
internal sealed class HttpLlmCompletionClient(
    HttpClient httpClient,
    ILogger<HttpLlmCompletionClient> logger,
    string confidentiality,
    string purpose,
    ILlmUsageReporter usageReporter,
    bool logPrompts = false)
    : ILlmCompletionClient
{
    // #247, IADR-0104 決定3: Hold へ倒れる理由を系統別に分ける。倒れる先はいずれも Hold（IADR-0017 の安全既定は不変）だが、
    // 「なぜ倒れたか」を監査（FR-11）で切り分けられなければ運用で原因を追えない。
    private const string HoldFallback = """{"action":"Hold","rationale":"LLM ゲートウェイ送信不可のため見送り"}""";
    private const string HoldMalformed = """{"action":"Hold","rationale":"LLM ゲートウェイ応答不正のため見送り"}""";
    private const string HoldRefused = """{"action":"Hold","rationale":"LLM が要求を拒否したため見送り"}""";
    private const string HoldEmpty = """{"action":"Hold","rationale":"LLM 応答が空のため見送り"}""";
    private const string HoldMaxTokens = """{"action":"Hold","rationale":"LLM 応答が出力上限に到達し本文が無いため見送り"}""";

    public async Task<string> CompleteAsync(string prompt, string? model = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // FR-11, IADR-0061 決定1: 送信前にプロンプト全量を記録する（応答前に失敗しても入力は残る）。
            if (logPrompts)
                logger.LogInformation("LLM 要求: model={Model} prompt={Prompt}", model, prompt);

            // IADR-0101, MSP/ADR-0025: MaxTokens は思考トークンと本文の合算上限（Opus 5 等は thinking が既定有効）。
            // purpose=trade-decision は基盤の PurposeModels に未登録で default（Opus 5 化される層）へ着地するため、
            // 1024 のままだと思考が上限を食い本文が空になり、下の空応答判定で全判断が Hold に固定される。
            var request = new CompletionRequest(prompt, MaxTokens: 4096, model, confidentiality, purpose);
            using var response = await httpClient
                .PostAsJsonAsync("/complete", request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("LLM ゲートウェイ /complete が非 2xx（{Status}）。取引しない安全側（Hold）に倒します。",
                    (int)response.StatusCode);
                return HoldFallback;
            }

            // 応答本文が空・不正 JSON で写像できない場合。取引しない安全側に倒す（送信可否は不明のため、伝送の失敗＝
            // HoldFallback とは区別して記録する。IADR-0104 決定3）。
            CompletionResponse? dto;
            try
            {
                dto = await response.Content
                    .ReadFromJsonAsync<CompletionResponse>(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                logger.LogWarning(ex, "LLM ゲートウェイ /complete の応答を解釈できません（不正 JSON・想定外の形式）。取引しない安全側（Hold）に倒します。");
                return HoldMalformed;
            }

            if (dto is null)
            {
                logger.LogWarning("LLM ゲートウェイ /complete の応答が空です（JSON null）。取引しない安全側（Hold）に倒します。");
                return HoldMalformed;
            }

            // Sent=false は機密区分による送信拒否（縮退）＝越境させておらず費用も発生していない。取引しない安全側に倒す。
            if (!dto.Sent)
            {
                logger.LogWarning("LLM ゲートウェイが送信不可（Sent=false・機密区分による縮退）。取引しない安全側（Hold）に倒します。");
                return HoldFallback;
            }

            // FR-11, IADR-0061 決定1: LLM の生出力を全量記録する（構造化解析前＝パーサが Hold へ丸める前の原文）。
            // #247, IADR-0104: 拒否・空応答の判定より前に記録し、以降で破棄する本文も事後に再構成できるようにする。
            if (logPrompts)
                logger.LogInformation("LLM 応答: model={Model} tokens={Input}/{Output} stopReason={StopReason} text={Text}",
                    dto.Model, dto.InputTokens ?? 0, dto.OutputTokens ?? 0, dto.StopReason, dto.Text);

            // #79, IADR-0055, IADR-0104 決定4: 送信が成立した（Sent=true）応答のトークンを費用計測へ渡す。本文の扱い
            //（拒否・空・上限到達で破棄するか否か）とは独立に課金は発生しているため、本文を読む前に一度だけ計測する
            // （とくに上限到達は思考トークンを消費し切って本文が空になる形で課金される・IADR-0101）。
            // 計測は best-effort＝失敗しても LLM 応答は壊さない（費用計測の不調で取引判断を Hold に倒すのは過剰。
            // 計上漏れは at-least-once 再配信で緩和される）。
            try
            {
                await usageReporter
                    .ReportAsync(new LlmUsage(dto.InputTokens ?? 0, dto.OutputTokens ?? 0), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "LLM 費用計測の報告に失敗しました（応答は継続）。");
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

    // POST /complete の要求（platform LlmGateway CompletionApiRequest 相当・camelCase JSON）。
    private sealed record CompletionRequest(string Prompt, int MaxTokens, string? Model, string? Confidentiality, string? Purpose);

    // POST /complete の応答（CompletionApiResponse の必要部分）。Sent=false は送信拒否（縮退）。
    // InputTokens/OutputTokens は費用計測の入力（#79・IADR-0055）。欠落時は 0 として扱う。
    // Model はゲートウェイが実際に選択したモデル（要求の Model は希望値であり、越境ルーティングで変わり得る）。
    // FR-11 の全量ログで「どのモデルが答えたか」を残すために受ける（IADR-0061 決定1）。
    // 実基盤の契約（microservices-platform リポジトリ
    // src/platform/backend/Shared/Platform.Shared.Contracts/Dtos/CompletionDto.cs の CompletionApiResponse。
    // 2026-07-17 時点で Text/Model/InputTokens/OutputTokens/Sent/Endpoint/RoutingReason）に Model は実在する。
    // 本リポジトリからは参照できない外部契約のため、追随漏れの検出はここの写像ではなく実結線時の疎通に委ねる。
    // 本 record は必要部分のみを受ける部分写像であり、欠落しても既定値に落ちるだけで安全側は崩れない。
    // #247, IADR-0104: StopReason は**送信が成立した**場合のモデル側の終了理由（"end_turn" / "max_tokens" / "refusal" 等）で、
    // Sent とは独立した軸（Sent=false＝越境させていない／StopReason="refusal"＝送信したがモデルが拒否した）。
    // 上流（MSP #379 / PR #391）が CompletionApiResponse に追加した。未設定（null）＝上流未更新・未対応プロバイダでは
    // 本フィールドを見ない従来どおりの分岐へ素通りする（非破壊）。
    private sealed record CompletionResponse(
        string? Text, bool Sent, int? InputTokens, int? OutputTokens, string? Model, string? StopReason = null);
}
