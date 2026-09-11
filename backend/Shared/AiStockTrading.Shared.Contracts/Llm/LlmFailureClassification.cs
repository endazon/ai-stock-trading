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

    // NFR, IADR-0332 決定 3, #746: east-west gRPC の canonical status code（数値）。
    //
    // 🔴 **なぜ `Grpc.Core.StatusCode` を引数に取らないか。** 本プロジェクトは Domain から到達でき、
    // 「Domain は .NET 標準のみに依存する」（MSP:ADR-0030 §基本方針 / IADR-0256）を守るため
    // **PackageReference をひとつも持たない**（`SharedProjectDependencyTests` が csproj の静的解析で止める）。
    // 分類の語彙を輸送側へ切り出すと `Classify`（HTTP）と 2 箇所に分かれて片方だけ直る事故が入るので、
    // **数値で受けて写像はここに残す**。値は gRPC の canonical code（不変）であり、
    // 実際の enum と食い違っていないことは `Grpc.Core` を参照できる層の試験が突き合わせる。

    /// <summary>`UNAUTHENTICATED`（トークン無し・失効）。REST の 401 に当たる。</summary>
    public const int UnauthenticatedGrpcStatusCode = 16;

    /// <summary>`PERMISSION_DENIED`（`platform-service` ロール未付与）。REST の 403 に当たる。</summary>
    public const int PermissionDeniedGrpcStatusCode = 7;

    /// <summary>`RESOURCE_EXHAUSTED`（レート制限）。REST の 429 に当たる。</summary>
    public const int ResourceExhaustedGrpcStatusCode = 8;

    public static LlmFailureKind Classify(int statusCode) => statusCode switch
    {
        RateLimitStatusCode => LlmFailureKind.Retryable,
        UnauthorizedStatusCode or ForbiddenStatusCode => LlmFailureKind.Unauthorized,
        >= 400 and < 500 => LlmFailureKind.ModelUnavailable,
        _ => LlmFailureKind.Other,
    };

    /// <summary>
    /// NFR, FR-04, MSP:ADR-0029, IADR-0323 決定 3, IADR-0328 決定 5, IADR-0332 決定 3, #746:
    /// east-west gRPC（`platform.llmgateway.v1.LlmCompletion`）の status を同じ語彙へ写す。
    /// **HTTP の写像（上の <see cref="Classify"/>）と同じファイルに置く** —— 分類の語彙を 2 箇所に置くと
    /// 片方だけ直る（IADR-0323 が閉じた誤帰属は、まさに分類が 1 つしか無かったから閉じられた）。
    /// </summary>
    /// <remarks>
    /// 🔴 **gRPC の status からは <see cref="LlmFailureKind.ModelUnavailable"/> を作らない。**
    /// 基盤の契約では「モデルが使えない」は**エラーではなく `sent=false` の応答**で来る
    /// （実装ガイド `docs/api/east-west-grpc.md`「縮退はエラーではない」）。
    /// `INVALID_ARGUMENT` や `UNIMPLEMENTED` を REST の 400 系になぞらえてモデル不可へ倒すと、
    /// **輸送の誤設定（proto の食い違い・gRPC 面の未配備）が「割当モデルが利用できません」として
    /// 監査台帳・月報・Discord へ残る** —— IADR-0323 が閉じた誤帰属を別の入口から作り直すことになる。
    /// これらは呼び出し先の不調と同じ <see cref="LlmFailureKind.Other"/> へ倒す（安全側は同じ Hold）。
    ///
    /// 🔴 `DEADLINE_EXCEEDED` / `CANCELLED` はここへ来ない。輸送が
    /// <see cref="System.OperationCanceledException"/> として上げ、呼び出し元の既存 catch が
    /// 「タイムアウト（縮退してよい）／停止要求（伝播すべき）」を判別する。
    /// </remarks>
    public static LlmFailureKind ClassifyGrpcStatus(int statusCode) => statusCode switch
    {
        // 認可の失敗。REST の 401 / 403 と同じ軸（基盤の拒否は「トークン無し → UNAUTHENTICATED、
        // `platform-service` 無し → PERMISSION_DENIED」。実装ガイド §4）。
        UnauthenticatedGrpcStatusCode or PermissionDeniedGrpcStatusCode => LlmFailureKind.Unauthorized,
        // レート制限。REST の 429 と同じく「再試行」であってモデル不可ではない（ADR-0017 決定 3）。
        ResourceExhaustedGrpcStatusCode => LlmFailureKind.Retryable,
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
