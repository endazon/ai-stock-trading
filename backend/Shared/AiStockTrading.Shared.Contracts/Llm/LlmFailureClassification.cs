namespace AiStockTrading.Shared.Contracts.Llm;

// FR-04, ADR-0017 決定3, #335, IADR-0216: LLM ゲートウェイの失敗を「再試行」と「モデル不可」へ分ける純関数。
//
// 計画 ADR-0017 決定3 の条文:
//   「フォールバックの対象は『モデルが利用できない』失敗（HTTP 400 系）である。
//    **レート制限（HTTP 429）は再試行であってフォールバックではない。**
//    区別せずに扱うと、混雑時に指定モデルから常時ずり落ちる。」
//
// 🔴 本システムでは取引判断がフォールバック禁止であるため、この区別は「別モデルへ逃がすか」ではなく
// **「取引判断を見送った事実として記録・通知するか」**に効く。429 をモデル不可として記録すると、
// 混雑のたびに「モデルが使えない」という誤った運用シグナルが積み上がる。
//
// 5xx・通信断・ステータスの取れない失敗は Other である。ADR-0017 が挙げるのは 400 系だけであり、
// 呼び出し先の不調は「別モデルにすれば直る」種類の失敗ではない（基盤 LlmFallbackPolicy と同じ切り分け）。
//
// NFR-05, #724, IADR-0323: **認可の失敗（401 / 403）も 400 系の内側だがモデル不可ではない。**
// 429 と同じ性質の例外であり、分けなかったために「資格情報が足りない」を
// **「割当モデルが利用できない」として記録・通知する**誤帰属が起きていた（基盤 MSP#1364 が LLM ゲートウェイの
// REST 3 口へ認可を掛けたことで顕在化する）。原因を取り違えた記録は、後から見た人を LLM 提供側へ向かわせる。
public static class LlmFailureClassification
{
    /// <summary>レート制限。400 系だが ADR-0017 決定3 によりモデル不可から除く。</summary>
    public const int RateLimitStatusCode = 429;

    /// <summary>認証されていない（資格情報が無い・失効）。#724, IADR-0323 によりモデル不可から除く。</summary>
    public const int UnauthorizedStatusCode = 401;

    /// <summary>認証は通ったが権限が足りない（ロール不足）。401 と同じ「認可」の軸である。</summary>
    public const int ForbiddenStatusCode = 403;

    public static LlmFailureKind Classify(int statusCode) => statusCode switch
    {
        RateLimitStatusCode => LlmFailureKind.Retryable,
        UnauthorizedStatusCode or ForbiddenStatusCode => LlmFailureKind.Unauthorized,
        >= 400 and < 500 => LlmFailureKind.ModelUnavailable,
        _ => LlmFailureKind.Other,
    };
}

public enum LlmFailureKind
{
    /// <summary>一時的な失敗（429）。再試行の対象であり、モデルが使えないことを意味しない。</summary>
    Retryable,

    /// <summary>
    /// 認可の失敗（401 / 403）。**モデルの可用性とは別の軸**である（#724, IADR-0323）。
    /// 呼び出し側の資格情報・付与ロールの問題であり、再試行でも別モデルでも解決しない。
    /// </summary>
    Unauthorized,

    /// <summary>モデルが利用できない（400 系。ZDR 制約・提供終了・パラメータ非互換）。</summary>
    ModelUnavailable,

    /// <summary>それ以外（5xx・通信断・タイムアウト）。呼び出し先の不調であり、モデルの可否とは別。</summary>
    Other,
}
