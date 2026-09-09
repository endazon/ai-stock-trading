using AiStockTrading.Shared.Contracts.Llm;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-04, ADR-0017 決定3, #335, IADR-0216: 429（再試行）と 400 系（モデル不可）の分岐（境界値テーブル）。
// NFR-05, #724, IADR-0323: これに **401/403（認可）** の 3 つ目の軸を足した。
//
// 🔴 計画の明文: 「**レート制限（HTTP 429）は再試行であってフォールバックではない。**
// 区別せずに扱うと、混雑時に指定モデルから常時ずり落ちる。」
//
// 🔴 #724 の明文（同型の誤帰属）: 401 をモデル不可へ倒すと「割当モデルが利用できない」という
// **誤った原因**が記録・通知される。実際は s2s の資格情報・付与ロールの不足であり、モデルの可用性ではない。
public class LlmFailureClassificationTests
{
    [Theory]
    // 400 系の下端・上端と、その外側（境界）。
    [InlineData(399, LlmFailureKind.Other)]
    [InlineData(400, LlmFailureKind.ModelUnavailable)]
    // 🔴 401/403 は 400 系の内側にありながら**モデル不可ではない**（#724, IADR-0323）。
    // 402（Payment Required）を挟むことで「4xx をまとめて認可扱い」への退行も捕まえる。
    [InlineData(401, LlmFailureKind.Unauthorized)]
    [InlineData(402, LlmFailureKind.ModelUnavailable)]
    [InlineData(403, LlmFailureKind.Unauthorized)]
    [InlineData(404, LlmFailureKind.ModelUnavailable)]
    [InlineData(422, LlmFailureKind.ModelUnavailable)]
    // 🔴 429 も 400 系の内側にありながらモデル不可ではない（ADR-0017 決定3）。
    [InlineData(428, LlmFailureKind.ModelUnavailable)]
    [InlineData(429, LlmFailureKind.Retryable)]
    [InlineData(430, LlmFailureKind.ModelUnavailable)]
    [InlineData(499, LlmFailureKind.ModelUnavailable)]
    // 5xx・通信断は「別モデルにすれば直る」種類の失敗ではない（基盤 LlmFallbackPolicy と同じ切り分け）。
    [InlineData(500, LlmFailureKind.Other)]
    [InlineData(502, LlmFailureKind.Other)]
    [InlineData(503, LlmFailureKind.Other)]
    [InlineData(200, LlmFailureKind.Other)]
    [InlineData(0, LlmFailureKind.Other)]
    public void ステータスコードから失敗の種類を分類する(int statusCode, LlmFailureKind expected) =>
        LlmFailureClassification.Classify(statusCode).Should().Be(expected);

    // プロパティベース: 400..499 のうち ModelUnavailable から外れるのは 401 / 403 / 429 の 3 つだけである。
    // 「例外を増やしすぎた（4xx を広く認可扱いにした）」も「戻した（401 をモデル不可へ返した）」も、
    // どちらもこの 1 本が捕まえる。
    [Fact]
    public void モデル不可から外れる_4xx_は_401と403と429_の3つだけ()
    {
        var exceptions = Enumerable.Range(400, 100)
            .Where(s => LlmFailureClassification.Classify(s) != LlmFailureKind.ModelUnavailable)
            .ToArray();

        exceptions.Should().Equal(401, 403, 429);
    }

    // 🔴 否定形: 認可の失敗はモデル不可でもレート制限（再試行）でもない。
    // 再試行へ倒すと、資格情報が無いまま同じ要求を繰り返す（直らないうえ費用と負荷だけ増える）。
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void 認可の失敗はモデル不可でも再試行でもない(int statusCode)
    {
        var kind = LlmFailureClassification.Classify(statusCode);

        kind.Should().Be(LlmFailureKind.Unauthorized);
        kind.Should().NotBe(LlmFailureKind.ModelUnavailable);
        kind.Should().NotBe(LlmFailureKind.Retryable);
    }
}
