using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-04, FR-15, ADR-0003, ADR-0033 決定2/決定4, #632, IADR-0318: 記録の契約（直列化と戦略の同一性）。
//
// 見るのは 3 点である。
//   1. JSON で往復でき、**生の判断が 1 件も落ちない**（ADR-0003 の全量ログ・ADR-0033 決定4）
//   2. 行動（Hold/Buy/Sell）が**文字列**で残る（enum の宣言順が変われば数値表現は意味が動く）
//   3. 戦略 ID が記録の内容だけから決まる（作成時刻では変わらず、判断が変われば変わる）
public class Stage0DecisionRecordTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 9, 3, 0, 0, TimeSpan.Zero);

    private static Stage0RawDecision Raw(int attempt, Stage0DecisionAction action, bool unparseable = false) =>
        new(attempt, action, $"根拠{attempt}", 100.5m, 2.5m, InputTokens: 1_200, OutputTokens: 180, unparseable);

    private static Stage0DecisionRecord Record(
        DateOnly asOf, Stage0DecisionAction majority, int signedQuantity, params Stage0RawDecision[] raws) =>
        new("AAPL", Market.UnitedStates, asOf, "fingerprint-1", "claude-sonnet-5",
            VoteCount: raws.Length, raws, majority, "多数決の根拠", signedQuantity,
            CostJpy: 12.5m, InputTokens: 3_600, OutputTokens: 540);

    private static Stage0DecisionRecordSet SetOf(params Stage0DecisionRecord[] records)
    {
        IReadOnlyList<Stage0RecordedSymbol> symbols = [new("AAPL", Market.UnitedStates)];
        var hash = Stage0StrategyIdentity.ComputeContentHash(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 31), symbols,
            new DateOnly(2026, 3, 31), "claude-sonnet-5", records);
        return new Stage0DecisionRecordSet(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 31), symbols,
            new DateOnly(2026, 3, 31), CreatedAt, "claude-sonnet-5",
            Stage0StrategyIdentity.StrategyIdFor("claude-sonnet-5", hash), records);
    }

    // 肯定形: JSON で往復しても記録は同値である（記録は再生側の別サービスがファイルで受け取る）。
    [Fact]
    public void 記録集合はJSONで往復できる()
    {
        var original = SetOf(Record(
            new DateOnly(2026, 6, 2), Stage0DecisionAction.Buy, 10,
            Raw(1, Stage0DecisionAction.Buy), Raw(2, Stage0DecisionAction.Buy), Raw(3, Stage0DecisionAction.Hold)));

        var restored = Stage0DecisionRecordJson.TryDeserialize(Stage0DecisionRecordJson.Serialize(original));

        restored.Should().BeEquivalentTo(original);
    }

    // 🔴 **否定形**（ADR-0003 の全量ログ・ADR-0033 決定4）: 生の判断は 1 件も落ちない。
    // 多数決結果だけを残す形にすると、成績のばらつきが LLM の非決定性由来か記録の質由来かを事後に切り分けられない。
    [Fact]
    public void 生の判断は往復で1件も落ちない()
    {
        var original = SetOf(Record(
            new DateOnly(2026, 6, 2), Stage0DecisionAction.Hold, 0,
            Raw(1, Stage0DecisionAction.Buy), Raw(2, Stage0DecisionAction.Sell),
            Raw(3, Stage0DecisionAction.Hold, unparseable: true)));

        var restored = Stage0DecisionRecordJson.TryDeserialize(Stage0DecisionRecordJson.Serialize(original))!;

        var raws = restored.Records.Should().ContainSingle().Which.RawDecisions;
        raws.Should().HaveCount(3);
        raws.Select(r => r.Action).Should().Equal(
            Stage0DecisionAction.Buy, Stage0DecisionAction.Sell, Stage0DecisionAction.Hold);
        // #290, IADR-0248: 解析不能と見送りは別の事実である。往復でその区別が消えない。
        raws[2].Unparseable.Should().BeTrue();
        raws[0].Unparseable.Should().BeFalse();
    }

    // 行動は**文字列**で残す（数値表現だと enum の宣言順が変わったときに Hold が Buy へ化ける）。
    [Fact]
    public void 行動は文字列として直列化される()
    {
        var json = Stage0DecisionRecordJson.Serialize(
            SetOf(Record(new DateOnly(2026, 6, 2), Stage0DecisionAction.Sell, -5, Raw(1, Stage0DecisionAction.Sell))));

        json.Should().Contain("\"Sell\"");
    }

    // 解釈できない JSON は例外にせず null（呼び出し元は「記録なし」として fail-closed へ倒す）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ この行は JSON ではない }")]
    public void 解釈不能な記録はnullを返す(string? json) =>
        Stage0DecisionRecordJson.TryDeserialize(json).Should().BeNull();

    // 戦略 ID は記録の内容から決まる（同じ内容→同じ ID）。
    [Fact]
    public void 同じ内容の記録は同じ戦略IDになる()
    {
        var a = SetOf(Record(new DateOnly(2026, 6, 2), Stage0DecisionAction.Buy, 10, Raw(1, Stage0DecisionAction.Buy)));
        var b = SetOf(Record(new DateOnly(2026, 6, 2), Stage0DecisionAction.Buy, 10, Raw(1, Stage0DecisionAction.Buy)));

        b.StrategyId.Should().Be(a.StrategyId);
        a.StrategyId.Should().StartWith($"{Stage0StrategyIdentity.Prefix}/claude-sonnet-5/");
    }

    // 🔴 **否定形**（IADR-0281 決定3）: 判断が 1 件でも変われば戦略 ID が変わる。
    // 同じ ID のまま中身が変わると、Risk 側で古い verdict が「戦略は変わっていない」として生き残る。
    [Fact]
    public void 判断が変われば戦略IDが変わる()
    {
        var buy = SetOf(Record(new DateOnly(2026, 6, 2), Stage0DecisionAction.Buy, 10, Raw(1, Stage0DecisionAction.Buy)));
        var hold = SetOf(Record(new DateOnly(2026, 6, 2), Stage0DecisionAction.Hold, 0, Raw(1, Stage0DecisionAction.Hold)));

        hold.StrategyId.Should().NotBe(buy.StrategyId);
    }

    // 🔴 **否定形**: 票の割れ方が違えば別の記録である（多数決結果が同じでも同一性を名乗らせない）。
    [Fact]
    public void 多数決結果が同じでも票の割れ方が違えば戦略IDが変わる()
    {
        var unanimous = SetOf(Record(
            new DateOnly(2026, 6, 2), Stage0DecisionAction.Buy, 10,
            Raw(1, Stage0DecisionAction.Buy), Raw(2, Stage0DecisionAction.Buy), Raw(3, Stage0DecisionAction.Buy)));
        var split = SetOf(Record(
            new DateOnly(2026, 6, 2), Stage0DecisionAction.Buy, 10,
            Raw(1, Stage0DecisionAction.Buy), Raw(2, Stage0DecisionAction.Buy), Raw(3, Stage0DecisionAction.Hold)));

        split.StrategyId.Should().NotBe(unanimous.StrategyId);
    }

    // 作成時刻は同一性に含めない（保存し直しただけで別戦略に見えると、受け手が有効な verdict を捨てる）。
    [Fact]
    public void 記録の並び順と作成時刻は戦略IDを変えない()
    {
        var first = Record(new DateOnly(2026, 6, 2), Stage0DecisionAction.Buy, 10, Raw(1, Stage0DecisionAction.Buy));
        var second = Record(new DateOnly(2026, 6, 3), Stage0DecisionAction.Sell, -4, Raw(1, Stage0DecisionAction.Sell));
        IReadOnlyList<Stage0RecordedSymbol> symbols = [new("AAPL", Market.UnitedStates)];

        var ascending = Stage0StrategyIdentity.ComputeContentHash(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 31), symbols,
            new DateOnly(2026, 3, 31), "claude-sonnet-5", [first, second]);
        var descending = Stage0StrategyIdentity.ComputeContentHash(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 31), symbols,
            new DateOnly(2026, 3, 31), "claude-sonnet-5", [second, first]);

        descending.Should().Be(ascending);
    }

    // 🔴 **否定形**（ADR-0033 決定3）: カットオフ日が違えば別の記録である。
    // 別のカットオフ前提で採った記録を、いま構成されているカットオフの検証結果として使わせない。
    [Fact]
    public void カットオフ日が違えば戦略IDが変わる()
    {
        var record = Record(new DateOnly(2026, 6, 2), Stage0DecisionAction.Buy, 10, Raw(1, Stage0DecisionAction.Buy));
        IReadOnlyList<Stage0RecordedSymbol> symbols = [new("AAPL", Market.UnitedStates)];

        var march = Stage0StrategyIdentity.ComputeContentHash(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 31), symbols,
            new DateOnly(2026, 3, 31), "claude-sonnet-5", [record]);
        var april = Stage0StrategyIdentity.ComputeContentHash(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 31), symbols,
            new DateOnly(2026, 4, 30), "claude-sonnet-5", [record]);

        april.Should().NotBe(march);
    }
}
