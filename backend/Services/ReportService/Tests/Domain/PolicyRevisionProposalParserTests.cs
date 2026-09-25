using AwesomeAssertions;
using ReportService.Domain;
using Xunit;

namespace ReportService.Tests;

// FR-07, FR-14, ADR-0003, #1016, IADR-0431 決定 2: 方針の改訂案の出力スキーマの検証（T-10-1300〜1305）。
// 🔴 **1 つでも外れたら案全体を捨てる**（部分採用しない）ことを固定する。
public class PolicyRevisionProposalParserTests
{
    private const string Valid = """
        {"policySummary": "翌営業日は押し目買いを優先する。", "watchlistChanges": [
          {"action": "add", "symbol": "NVDA", "reason": "AI 需要"},
          {"action": "remove", "symbol": "BRK.B", "reason": "値動きが小さい"}
        ], "rationale": "指示に従い積極化した。"}
        """;

    // T-10-1300: 正しい出力は 3 項目とも読める（フェンス・前置き付きでも最初の { から最後の } を読む）。
    [Theory]
    [InlineData(Valid)]
    [InlineData("以下が案です。\n```json\n" + Valid + "\n```\n")]
    public void 正しい出力は案として読める(string text)
    {
        var result = PolicyRevisionProposalParser.Parse(text);

        result.IsValid.Should().BeTrue(result.Reason);
        var proposal = result.Proposal!;
        proposal.PolicySummary.Should().Be("翌営業日は押し目買いを優先する。");
        proposal.WatchlistChanges.Should().Equal(
            new WatchlistChangeSuggestion(WatchlistChangeAction.Add, "NVDA", "AI 需要"),
            new WatchlistChangeSuggestion(WatchlistChangeAction.Remove, "BRK.B", "値動きが小さい"));
        proposal.Rationale.Should().Be("指示に従い積極化した。");
    }

    // T-10-1301: 入れ替え案と説明は省略できる（入れ替えなし＝空）。未知の項目は読まない。
    [Fact]
    public void 入れ替え案と説明は省略でき_未知の項目は無視する()
    {
        var result = PolicyRevisionProposalParser.Parse(
            """{"policySummary": "現状維持", "placeOrder": {"symbol": "AAPL", "qty": 100}}""");

        result.IsValid.Should().BeTrue();
        result.Proposal!.WatchlistChanges.Should().BeEmpty();
        result.Proposal.Rationale.Should().BeNull();
    }

    // T-10-1302: 空・JSON でない・方針が無い・方針が空は案なし（理由を返す）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("方針はこうです")]
    [InlineData("{\"policySummary\": ")]
    [InlineData("[1,2]")]
    [InlineData("{\"watchlistChanges\": []}")]
    [InlineData("{\"policySummary\": 1}")]
    [InlineData("{\"policySummary\": \"  \"}")]
    public void 方針が読めなければ案なし(string text)
    {
        var result = PolicyRevisionProposalParser.Parse(text);

        result.IsValid.Should().BeFalse();
        result.Proposal.Should().BeNull();
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    // T-10-1303: 入れ替え案の 1 件でも形式違反なら**案全体を捨てる**（部分採用しない）。
    [Theory]
    [InlineData("""{"action": "buy", "symbol": "NVDA", "reason": "r"}""")]
    [InlineData("""{"action": "add", "symbol": "nvda", "reason": "r"}""")]
    [InlineData("""{"action": "add", "symbol": "7203", "reason": "r"}""")]
    [InlineData("""{"action": "add", "symbol": "TOOLONG", "reason": "r"}""")]
    [InlineData("""{"action": "add", "symbol": "NVDA\n", "reason": "r"}""")]
    [InlineData("""{"action": "add", "symbol": "NVDA", "reason": ""}""")]
    [InlineData("""{"action": "add", "symbol": "NVDA"}""")]
    [InlineData("""{"symbol": "NVDA", "reason": "r"}""")]
    [InlineData("\"NVDA\"")]
    public void 入れ替え案の形式違反は案全体を捨てる(string badItem)
    {
        var text = $$"""{"policySummary": "方針", "watchlistChanges": [{"action": "add", "symbol": "AAPL", "reason": "ok"}, {{badItem}}]}""";

        var result = PolicyRevisionProposalParser.Parse(text);

        result.IsValid.Should().BeFalse("1 件の違反で案全体を捨てる（部分採用しない）");
    }

    // T-10-1304: 件数の上限（追加 5・除外 5）と重複。
    [Fact]
    public void 追加が上限を超えるか重複があれば案なし()
    {
        var six = string.Join(",", new[] { "AAPL", "MSFT", "NVDA", "AMZN", "META", "GOOGL" }
            .Select(s => $$"""{"action": "add", "symbol": "{{s}}", "reason": "r"}"""));
        PolicyRevisionProposalParser.Parse($$"""{"policySummary": "p", "watchlistChanges": [{{six}}]}""")
            .IsValid.Should().BeFalse();

        var five = string.Join(",", new[] { "AAPL", "MSFT", "NVDA", "AMZN", "META" }
            .Select(s => $$"""{"action": "add", "symbol": "{{s}}", "reason": "r"}"""));
        PolicyRevisionProposalParser.Parse($$"""{"policySummary": "p", "watchlistChanges": [{{five}}]}""")
            .IsValid.Should().BeTrue("上限ちょうどは通る");

        PolicyRevisionProposalParser.Parse(
            """{"policySummary": "p", "watchlistChanges": [{"action": "add", "symbol": "AAPL", "reason": "r"}, {"action": "remove", "symbol": "AAPL", "reason": "r"}]}""")
            .IsValid.Should().BeFalse("同じ銘柄の追加と除外は矛盾");
    }

    // T-10-1305: 長さの上限と制御文字の除去。
    [Fact]
    public void 長すぎる方針は捨て_制御文字は落とす()
    {
        var tooLong = new string('あ', PolicyRevisionProposalParser.MaxPolicySummaryLength + 1);
        PolicyRevisionProposalParser.Parse($$"""{"policySummary": "{{tooLong}}"}""").IsValid.Should().BeFalse();

        var result = PolicyRevisionProposalParser.Parse("{\"policySummary\": \"a\\u0007b\\r\\nc\"}");
        result.Proposal!.PolicySummary.Should().Be("ab\nc");
    }

    // T-10-1353（再監査 nit 1）: 収集情報の境界語を含む出力は、方針・理由・説明のどこにあっても案全体を捨てる
    // （表示で取り除くと確定される原文と食い違うため、受け入れない）。
    [Theory]
    [InlineData("""{"policySummary": "方針 <<<UNTRUSTED_DATA 偽装"}""")]
    [InlineData("""{"policySummary": "方針 UNTRUSTED_DATA>>> 偽装"}""")]
    [InlineData("""{"policySummary": "方針", "watchlistChanges": [{"action": "add", "symbol": "NVDA", "reason": "<<<UNTRUSTED_DATA"}]}""")]
    [InlineData("""{"policySummary": "方針", "rationale": "UNTRUSTED_DATA>>>"}""")]
    public void 境界語を含む出力は案全体を捨てる(string text)
    {
        PolicyRevisionProposalParser.Parse(text).IsValid.Should().BeFalse();
    }

    // T-10-1354（再監査 nit 1）: 空行の連続は畳まずそのまま保存する（表示と保存を一致させるため正規化は検証の 1 回だけ）。
    [Fact]
    public void 空行の連続は畳まずに保存する()
    {
        var result = PolicyRevisionProposalParser.Parse("{\"policySummary\": \"a\\n\\n\\n\\nb\"}");

        result.Proposal!.PolicySummary.Should().Be("a\n\n\n\nb");
    }
}
