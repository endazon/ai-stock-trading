using AiStockTrading.Shared.Contracts.Llm;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-04, ADR-0017 決定3, #335, IADR-0216: 429（再試行）と 400 系（モデル不可）の分岐（境界値テーブル）。
//
// 🔴 計画の明文: 「**レート制限（HTTP 429）は再試行であってフォールバックではない。**
// 区別せずに扱うと、混雑時に指定モデルから常時ずり落ちる。」
public class LlmFailureClassificationTests
{
    [Theory]
    // 400 系の下端・上端と、その外側（境界）。
    [InlineData(399, LlmFailureKind.Other)]
    [InlineData(400, LlmFailureKind.ModelUnavailable)]
    [InlineData(401, LlmFailureKind.ModelUnavailable)]
    [InlineData(404, LlmFailureKind.ModelUnavailable)]
    [InlineData(422, LlmFailureKind.ModelUnavailable)]
    // 🔴 429 は 400 系の内側にありながら**モデル不可ではない**。この 1 点が本分岐の全てである。
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

    // プロパティベース: 400..499 のうち ModelUnavailable でないものは 429 ただ 1 つである。
    [Fact]
    public void モデル不可から外れる_4xx_は_429_ただ1つ()
    {
        var exceptions = Enumerable.Range(400, 100)
            .Where(s => LlmFailureClassification.Classify(s) != LlmFailureKind.ModelUnavailable)
            .ToArray();

        exceptions.Should().Equal(429);
    }
}
