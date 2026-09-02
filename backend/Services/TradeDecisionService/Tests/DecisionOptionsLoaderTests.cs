using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, IADR-0039, IADR-0212, IADR-0277, #571: Decision:* 構成の読み取りと安全側フォールバックの検証。
// VoteCount の既定＝現行挙動（1 票）を config 経由で壊さないことを保証する。
// EnableScreening は #571（基盤 trade-decision-screening 登録が前提）により既定 true へ反転した
// （IADR-0277。DecisionOrchestrationOptions.Default 自体は不変であり、ローダーの構成既定だけが変わる）。
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
    public void 未設定なら既定でスクリーニングが有効になる()
    {
        var options = Load();

        options.VoteCount.Should().Be(1);
        options.EnableScreening.Should().BeTrue();
        options.PrimaryModel.Should().BeNull();
        options.SecondaryModel.Should().BeNull();
    }

    // IADR-0277: 新既定（true）を構成で明示的に打ち消せること（fail-safe な上書き経路の否定形テスト）。
    [Fact]
    public void 明示的にfalseを設定すれば無効化できる()
    {
        Load(("Decision:EnableScreening", "false")).EnableScreening.Should().BeFalse();
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
    public void 不正なEnableScreeningは既定true()
    {
        // bool.TryParse が失敗する値（非 true/false）は新既定（true）のまま倒れる（IADR-0277）。
        Load(("Decision:EnableScreening", "yes")).EnableScreening.Should().BeTrue();
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
