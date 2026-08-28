using TradeDecisionService.Application.Services;
using TradeDecisionService.Infrastructure.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TradeDecisionService.Infrastructure.Tests;

// FR-04, IADR-0039: Decision:* 構成の読み取りと安全側フォールバックの検証。
// 既定＝現行挙動（1 票・スクリーニング無効）を config 経由で壊さないことを保証する。
public class DecisionOptionsLoaderTests
{
    private static DecisionOrchestrationOptions Load(params (string Key, string? Value)[] pairs)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();
        return DecisionOptionsLoader.FromConfiguration(config);
    }

    [Fact]
    public void 未設定なら既定_現行挙動と等価()
    {
        var options = Load();

        options.VoteCount.Should().Be(1);
        options.EnableScreening.Should().BeFalse();
        options.PrimaryModel.Should().BeNull();
        options.SecondaryModel.Should().BeNull();
    }

    [Fact]
    public void 全項目を設定から読み取る()
    {
        var options = Load(
            ("Decision:VoteCount", "5"),
            ("Decision:EnableScreening", "true"),
            ("Decision:PrimaryModel", "light"),
            ("Decision:SecondaryModel", "pro"));

        options.VoteCount.Should().Be(5);
        options.EnableScreening.Should().BeTrue();
        options.PrimaryModel.Should().Be("light");
        options.SecondaryModel.Should().Be("pro");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    public void 不正なVoteCountは既定1のまま_安全側(string raw)
    {
        // 0・負数・非数値・空は既定 1（現行挙動）を保つ。
        Load(("Decision:VoteCount", raw)).VoteCount.Should().Be(1);
    }

    [Fact]
    public void 空文字のモデル指定はnullに正規化する()
    {
        var options = Load(("Decision:PrimaryModel", ""), ("Decision:SecondaryModel", "   "));

        options.PrimaryModel.Should().BeNull();
        options.SecondaryModel.Should().BeNull();
    }

    [Fact]
    public void 不正なEnableScreeningは既定false()
    {
        Load(("Decision:EnableScreening", "yes")).EnableScreening.Should().BeFalse();
    }

    // #337, IADR-0247: スクリーニング入力のコンテキスト予算（縮退制御）の読み込み。
    [Fact]
    public void スクリーニング予算を読み込む()
    {
        Load(("Decision:ScreeningContextBudgetChars", "500000"))
            .ScreeningContextBudgetChars.Should().Be(500_000);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    public void 不正なスクリーニング予算は未設定のまま_縮退制御なし(string raw)
    {
        // 0・負数・非数値・空は null（縮退制御なし＝現行プロンプト）を保つ安全側フォールバック。
        Load(("Decision:ScreeningContextBudgetChars", raw)).ScreeningContextBudgetChars.Should().BeNull();
    }
}
