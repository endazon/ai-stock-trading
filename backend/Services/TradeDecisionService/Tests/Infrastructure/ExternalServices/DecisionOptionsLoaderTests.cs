using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-02, FR-04, IADR-0039, IADR-0212, IADR-0278, IADR-0313, #571, #567: Decision:* 構成の読み取りと安全側フォールバックの検証。
// VoteCount の既定＝現行挙動（1 票）を config 経由で壊さないことを保証する。
// EnableScreening は #571（基盤 trade-decision-screening 登録が前提）により既定 true へ反転した
// （IADR-0278。DecisionOrchestrationOptions.Default 自体は不変であり、ローダーの構成既定だけが変わる）。
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

    // IADR-0278: 新既定（true）を構成で明示的に打ち消せること（fail-safe な上書き経路の否定形テスト）。
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
        // bool.TryParse が失敗する値（非 true/false）は新既定（true）のまま倒れる（IADR-0278）。
        Load(("Decision:EnableScreening", "yes")).EnableScreening.Should().BeTrue();
    }

    // #337, IADR-0247 / #567, IADR-0313 決定3: スクリーニング入力のコンテキスト予算（縮退制御）の読み込み。
    [Fact]
    public void スクリーニング予算を読み込む()
    {
        Load(("Decision:ScreeningContextBudgetChars", "500000"))
            .ScreeningContextBudgetChars.Should().Be(500_000);
    }

    // #567, IADR-0313 決定2: 未設定なら既定値（150,000 文字）＝**縮退制御は既定で有効**。
    [Fact]
    public void 未設定なら既定の予算で縮退制御が有効になる()
    {
        Load().ScreeningContextBudgetChars
            .Should().Be(DecisionOrchestrationOptions.DefaultScreeningContextBudgetChars);
    }

    // #567, IADR-0313 決定1: 既定値そのものを固定する（算出式 200,000 × 1.0 × 0.75 の結果）。
    // 値を動かすなら IADR-0313 決定1 の算出過程も一緒に改める、という束縛をテストで表す。
    [Fact]
    public void 既定の予算は算出式どおり150000文字である()
    {
        DecisionOrchestrationOptions.DefaultScreeningContextBudgetChars.Should().Be(150_000);
    }

    // #567, IADR-0313 決定3: 統制を落とす操作は明示に限る（否定形＝無効化の経路が残っていること）。
    [Theory]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData(" off ")]
    public void 明示的に無効化すれば縮退制御なしへ戻せる(string raw)
    {
        Load(("Decision:ScreeningContextBudgetChars", raw)).ScreeningContextBudgetChars.Should().BeNull();
    }

    // #567, IADR-0313 決定3: **不正値の倒し先が反転した。** 既定が有効になった以上、不正値で統制が
    // 黙って外れてはならない（旧テスト `不正なスクリーニング予算は未設定のまま_縮退制御なし` の置き換え）。
    [Theory]
    [InlineData("-1")]
    [InlineData("-150000")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("150000.5")]
    public void 不正なスクリーニング予算は既定へ倒れる_統制を残す側(string raw)
    {
        Load(("Decision:ScreeningContextBudgetChars", raw)).ScreeningContextBudgetChars
            .Should().Be(DecisionOrchestrationOptions.DefaultScreeningContextBudgetChars);
    }

    // #567, IADR-0313 決定2: レコードの既定（単体テストの「現行挙動」基準値）は据え置きであること。
    // ここが動くと DecisionOrchestratorTests 等の意味が変わる（IADR-0278 と同じ線引き）。
    [Fact]
    public void レコードの既定は縮退制御なしのまま_ローダーだけが既定を持つ()
    {
        DecisionOrchestrationOptions.Default.ScreeningContextBudgetChars.Should().BeNull();
        DecisionOrchestrationOptions.Default.EnableScreening.Should().BeFalse();
    }
}
