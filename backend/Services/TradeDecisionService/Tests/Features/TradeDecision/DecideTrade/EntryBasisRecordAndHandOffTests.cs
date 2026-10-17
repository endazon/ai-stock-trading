extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace TradeDecisionService.Tests;

// 🔴 FR-04, FR-10, FR-11, ADR-0003, #1384, IADR-0557: 新規建ての判断に「成立した条件の記号」と「基準価格」を構造化して記録し、
// 保有中の判断（本判断・一次の両方）へ建玉（ロット）ごとに渡す。記録の無いロット・供給なしは「不明」と渡し、方針の既定の出口で扱わせる。
// 実測（PoC 2026-10-17）: 方針に「条件 B で建てた建玉は日中安値割れで撤退」を書いても、判断は保有をどの条件で建てたかを知らず動かなかった。
public class EntryBasisRecordAndHandOffTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 14, 0, 0, TimeSpan.Zero);

    private const string PolicyText =
        "条件 A: AAPL 前日比 -3% 以下で買い\n買い条件 B（急落反発）: 日中安値から +1% 戻したら買い\n既定の撤退: 平均取得単価から -1.5%";

    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 10, 17), PolicyText);

    // 前日終値 100・始値 99・高値 101・安値 95。現在値 96。
    private static readonly IntradayPriceContext Intraday = new(100m, 99m, 101m, 95m);

    private static DecisionTrigger Scheduled() => DecisionTrigger.Scheduled("AAPL", Market.UnitedStates, Now);

    // ================================================================================================
    // 検証（EntryBasisValidator）
    // ================================================================================================

    // #1384 AC-04-01: 方針の条件集合に含まれる記号と、当日の範囲内の基準価格は記録される（全角・小文字・前後の空白は寄せる）。
    [Theory]
    [InlineData("B", 95.0, "B")]
    [InlineData(" ｂ ", 95.0, "B")]
    [InlineData("A", 100.0, "A")] // 前日終値（前日比の条件）は日中の範囲の外でも範囲に含める
    public void 方針の条件集合に含まれる記号と当日の範囲内の基準価格は記録される(string answered, double price, string expected)
    {
        var result = EntryBasisValidator.Validate(PolicyText, answered, (decimal)price, Intraday, 96m, Scheduled());

        result.Basis.Should().Be(new EntryBasis(expected, (decimal)price));
        result.Reason.Should().BeNull();
    }

    // #1384 AC-04-02（否定形）: 記号が欠落・形が違う・方針の集合に無い・方針に「条件 X:」行が無い → 記録全体が不明（見送りにはしない）。
    [Theory]
    [InlineData(PolicyText, null)]
    [InlineData(PolicyText, "C")]
    [InlineData(PolicyText, "AB")]
    [InlineData(PolicyText, "1")]
    [InlineData("押し目で買う（記号つきの条件なし）。条件 B に近い動きを狙う。", "B")]
    public void 記号が方針の条件集合に無ければ記録は不明になる(string policy, string? answered)
    {
        var result = EntryBasisValidator.Validate(policy, answered, 95m, Intraday, 96m, Scheduled());

        result.Basis.Should().BeNull();
        result.Reason.Should().NotBeNull();
    }

    // #1384 AC-04-03（否定形）: 基準価格が欠落・0 以下・当日の範囲外・範囲が不明 → 記号は残し基準価格だけ不明。
    [Theory]
    [InlineData(null, true)]
    [InlineData(0.0, true)]
    [InlineData(-1.0, true)]
    [InlineData(94.99, true)]
    [InlineData(101.01, true)]
    [InlineData(95.0, false)] // 範囲は正しいが日中の高安が不明
    public void 基準価格が正でないか当日の範囲外か範囲が不明なら基準価格だけ不明になる(double? price, bool intradayKnown)
    {
        var intraday = intradayKnown ? Intraday : new IntradayPriceContext(100m, 99m, null, 95m);

        var result = EntryBasisValidator.Validate(PolicyText, "B", (decimal?)price, intraday, 96m, Scheduled());

        result.Basis.Should().Be(new EntryBasis("B", null));
        result.Reason.Should().NotBeNull();
    }

    // #1384 AC-04-04: 急変の基準値・現在値は範囲を広げる（急変の条件は基準値を参照し得る）。定時では広げない。
    [Fact]
    public void 急変の基準値は範囲に含め定時では含めない()
    {
        var movement = new DecisionTrigger("AAPL", Market.UnitedStates, DecisionTriggerKind.PriceMovement, Price: 96m, BaselinePrice: 105m);

        EntryBasisValidator.Validate(PolicyText, "B", 105m, Intraday, 96m, movement).Basis
            .Should().Be(new EntryBasis("B", 105m));
        EntryBasisValidator.Validate(PolicyText, "B", 105m, Intraday, 96m, Scheduled()).Basis
            .Should().Be(new EntryBasis("B", null));
    }

    // ================================================================================================
    // 解釈（TradeDecisionParser）
    // ================================================================================================

    // #1384 AC-04-05: 2 項目は寛容に読む。欠落・型違いでも解析不能・見送りにならない（新規建ては従来どおり成立する）。
    [Theory]
    [InlineData("""{"action":"Buy","rationale":"r","referencePrice":96,"stopLossDistancePerShare":3,"entryCondition":"B","entryReferencePrice":95}""", "B", 95.0)]
    [InlineData("""{"action":"Buy","rationale":"r","referencePrice":96,"stopLossDistancePerShare":3,"entryCondition":"B","entryReferencePrice":"95"}""", "B", 95.0)]
    [InlineData("""{"action":"Buy","rationale":"r","referencePrice":96,"stopLossDistancePerShare":3}""", null, null)]
    [InlineData("""{"action":"Buy","rationale":"r","referencePrice":96,"stopLossDistancePerShare":3,"entryCondition":7,"entryReferencePrice":"安値"}""", null, null)]
    [InlineData("""{"action":"Buy","rationale":"r","referencePrice":96,"stopLossDistancePerShare":3,"entryCondition":null,"entryReferencePrice":null}""", null, null)]
    public void 建てた条件の2項目は寛容に読み欠落でも解析は成功する(string json, string? condition, double? price)
    {
        var parsed = TradeDecisionParser.ParseDetailed(json, signedHeldQuantity: 0);

        parsed.IsUnparseable.Should().BeFalse();
        parsed.Decision.Action.Should().Be(TradeAction.Buy);
        parsed.Decision.EntryConditionAnswer.Should().Be(condition);
        parsed.Decision.EntryReferencePriceAnswer.Should().Be((decimal?)price);
    }

    // ================================================================================================
    // 判断サービス（新規建ての発注意図・決済）
    // ================================================================================================

    // #1384 AC-04-06: 新規建ての発注意図に検証済みの記録が載る。記号が集合に無ければ記録は無い（null）が、新規建ては見送らない。
    [Theory]
    [InlineData("B", 95.0, "B", 95.0)]
    [InlineData("B", 200.0, "B", null)]
    [InlineData("Z", 95.0, null, null)]
    [InlineData(null, null, null, null)]
    public async Task 新規建ての発注意図に検証済みの建てた条件が載り不正でも見送らない(
        string? answered, double? price, string? expectedCondition, double? expectedPrice)
    {
        var json = $$"""{"action":"Buy","rationale":"反発","referencePrice":96,"stopLossDistancePerShare":3,"entryCondition":{{(answered is null ? "null" : $"\"{answered}\"")}},"entryReferencePrice":{{(price is null ? "null" : price.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}}}""";
        var service = Create(json, held: 0);

        var made = await service.DecideAsync(Scheduled());

        made.Should().NotBeNull("記録のための項目が通らなくても新規建ては見送らない");
        made!.Intent.PositionEffect.Should().Be(PositionEffect.Open);
        made.Intent.EntryBasis.Should().Be(expectedCondition is null ? null : new EntryBasis(expectedCondition, (decimal?)expectedPrice));
    }

    // #1384 AC-04-07（否定形）: 決済（ロング保有中の Sell）の発注意図には建てた条件を載せない。
    [Fact]
    public async Task 決済の発注意図には建てた条件を載せない()
    {
        var service = Create(
            """{"action":"Sell","rationale":"B の安値割れ","referencePrice":96,"stopLossDistancePerShare":null,"entryCondition":"B","entryReferencePrice":95}""",
            held: 10);

        var made = await service.DecideAsync(Scheduled());

        made!.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        made.Intent.EntryBasis.Should().BeNull();
    }

    // #1384 AC-04-08: 本判断の出力形式に 2 項目があり、新規建てでだけ入れる規則がある。一次の出力形式には無い（一次は方向だけ）。
    [Fact]
    public void 本判断の出力形式に建てた条件の2項目と入れ方の規則がある()
    {
        var main = TradeDecisionPromptBuilder.Build(Scheduled(), Policy, Context());
        var withProfit = TradeDecisionPromptBuilder.Build(Scheduled(), Policy, Context(), includeProfitability: true);
        var screening = TradeDecisionPromptBuilder.BuildScreening(Scheduled(), Policy, Context());

        main.Should().Contain(TradeDecisionPromptBuilder.EntryBasisOutputFields).And.Contain(TradeDecisionPromptBuilder.OutputEntryBasisRule);
        withProfit.Should().Contain(TradeDecisionPromptBuilder.EntryBasisOutputFields);
        screening.Should().NotContain("entryCondition");
        TradeDecisionPromptBuilder.OutputEntryBasisRule.Should().Contain("条件 X:").And.Contain("null");
    }

    // ================================================================================================
    // 受け渡し（保有状況節）
    // ================================================================================================

    private static readonly DateTimeOffset T1 = new(2026, 10, 16, 14, 5, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 10, 17, 13, 40, 0, TimeSpan.Zero);

    private static HeldPosition HeldWith(params HeldPositionLot[] lots) =>
        new(lots.Sum(l => l.Quantity), 97m, 92m) { Lots = lots };

    // #1384 AC-04-09: 本判断の保有状況節にロットごとの条件・基準価格・数量・承認時刻が古い順に並び、当てはめ方の規則が出る。
    [Fact]
    public void 本判断の保有状況節にロットごとの建てた条件と基準価格が並ぶ()
    {
        var held = HeldWith(new HeldPositionLot(Guid.NewGuid(), 50, T1, "B", 95m));

        var section = HeldSection(TradeDecisionPromptBuilder.Build(Scheduled(), Policy, Context(), currentPrice: 96m, held: held, working: WorkingEntryOrders.None));

        section.Should().Contain($"- {TradeDecisionPromptBuilder.EntryBasisLotsLead}:\n  - 50 株: 条件 B / 基準価格: 95 / 承認: 2026-10-16 14:05 UTC\n");
        section.Should().Contain(TradeDecisionPromptBuilder.EntryBasisByLotRule);
        section.Should().NotContain(TradeDecisionPromptBuilder.EntryBasisUnknownRule, "不明のロットが無い");
        section.Should().NotContain(TradeDecisionPromptBuilder.EntryBasisMixedRule, "ロットは 1 つ");
    }

    // #1384 AC-04-10: 記録の無いロットは「不明」と書き、既定の出口の固定文を出す。条件の異なるロットが混ざれば混在の規則を出す。
    [Fact]
    public void 記録の無いロットは不明と書き混在なら混在の規則を出す()
    {
        var held = HeldWith(
            new HeldPositionLot(Guid.Empty, 30, T1, null, null),
            new HeldPositionLot(Guid.NewGuid(), 20, T2, "B", null));

        var section = HeldSection(TradeDecisionPromptBuilder.Build(Scheduled(), Policy, Context(), currentPrice: 96m, held: held, working: WorkingEntryOrders.None));

        section.Should().Contain($"  - 30 株: 条件 {TradeDecisionPromptBuilder.EntryBasisUnknownText} / 基準価格: 不明 / 承認: 2026-10-16 14:05 UTC\n");
        section.Should().Contain("  - 20 株: 条件 B / 基準価格: 不明 / 承認: 2026-10-17 13:40 UTC\n");
        section.Should().Contain(TradeDecisionPromptBuilder.EntryBasisUnknownRule);
        section.Should().Contain(TradeDecisionPromptBuilder.EntryBasisMixedRule);
        section.IndexOf("30 株", StringComparison.Ordinal).Should().BeLessThan(section.IndexOf("20 株", StringComparison.Ordinal), "古い順");
    }

    // #1384 AC-04-11（否定形）: 同じ条件のロットだけなら混在の規則は出さない。
    [Fact]
    public void 同じ条件のロットだけなら混在の規則は出さない()
    {
        var held = HeldWith(new HeldPositionLot(Guid.NewGuid(), 30, T1, "B", 95m), new HeldPositionLot(Guid.NewGuid(), 20, T2, "B", 94.5m));

        var section = HeldSection(TradeDecisionPromptBuilder.Build(Scheduled(), Policy, Context(), currentPrice: 96m, held: held, working: WorkingEntryOrders.None));

        section.Should().NotContain(TradeDecisionPromptBuilder.EntryBasisMixedRule);
    }

    // #1384 AC-04-12: 供給なし（Lots が null）は本判断・一次の両方で不明と書き、既定の出口の固定文を出す。固定文は方針の値（-1.5%）を書かない。
    [Fact]
    public void 供給なしは本判断と一次の両方で不明と書き既定の出口で扱う()
    {
        var held = new HeldPosition(50, 97m, 92m);

        var main = HeldSection(TradeDecisionPromptBuilder.Build(Scheduled(), Policy, Context(), currentPrice: 96m, held: held, working: WorkingEntryOrders.None));
        var screening = HeldSection(TradeDecisionPromptBuilder.BuildScreening(Scheduled(), Policy, Context(), currentPrice: 96m, held: held, working: WorkingEntryOrders.None));

        foreach (var section in new[] { main, screening })
        {
            section.Should().Contain(TradeDecisionPromptBuilder.EntryBasisUnsuppliedLine);
            section.Should().Contain(TradeDecisionPromptBuilder.EntryBasisUnknownRule);
        }

        TradeDecisionPromptBuilder.EntryBasisUnknownRule.Should().Contain("方針の既定の出口").And.NotContain("1.5");
    }

    // #1384 AC-04-13: 一次の短縮版は 1 行の要約（不明があれば既定の出口の固定文）。保有なし・不明では出さない。
    [Fact]
    public void 一次の短縮版は建てた条件を1行に要約し保有なしと不明では出さない()
    {
        var held = HeldWith(new HeldPositionLot(Guid.NewGuid(), 50, T1, "B", 95m), new HeldPositionLot(Guid.Empty, 10, T2, null, null));

        var section = HeldSection(TradeDecisionPromptBuilder.BuildScreening(Scheduled(), Policy, Context(), currentPrice: 96m, held: held, working: WorkingEntryOrders.None));
        var none = HeldSection(TradeDecisionPromptBuilder.BuildScreening(Scheduled(), Policy, Context(), currentPrice: 96m, held: HeldPosition.None, working: WorkingEntryOrders.None));
        var unknown = HeldSection(TradeDecisionPromptBuilder.BuildScreening(Scheduled(), Policy, Context(), currentPrice: 96m, held: null));

        section.Should().Contain("- 建てた条件（ロットごと）: B 基準価格 95（50 株） / 不明（10 株）\n");
        section.Should().Contain(TradeDecisionPromptBuilder.EntryBasisUnknownRule);
        none.Should().NotContain("建てた条件");
        unknown.Should().NotContain("建てた条件");
    }

    // #1384 AC-04-14: 一次の要約の最悪長（並べる上限のロット・長い価格・不明の規則・残りの件数）が縮退の保護分に収まる。
    [Fact]
    public void 一次の要約の最悪長は縮退の保護分に収まる()
    {
        var lots = Enumerable.Range(0, TradeDecisionPromptBuilder.ScreeningMaxLots + 3)
            .Select(i => new HeldPositionLot(Guid.NewGuid(), 123_456, T1, i % 2 == 0 ? "Z" : null, i % 2 == 0 ? 123_456.7891m : null))
            .ToArray();
        var withLots = HeldSection(TradeDecisionPromptBuilder.BuildScreening(
            Scheduled() with { Market = Market.Japan, Symbol = "7203" }, Policy, Context(), currentPrice: 96m, held: HeldWith(lots), working: WorkingEntryOrders.None));
        var withoutLots = HeldSection(TradeDecisionPromptBuilder.BuildScreening(
            Scheduled() with { Market = Market.Japan, Symbol = "7203" }, Policy, Context(), currentPrice: 96m, held: new HeldPosition(lots.Sum(l => l.Quantity), 97m, 92m) { Lots = [] }, working: WorkingEntryOrders.None));

        var added = withLots.Split('\n').Where(l => l.StartsWith("- 建てた条件", StringComparison.Ordinal) || l == $"- {TradeDecisionPromptBuilder.EntryBasisUnknownRule}")
            .Sum(l => l.Length + 1);
        added.Should().BeLessThanOrEqualTo(ScreeningContextAssembler.HeldEntryBasisReserveChars);
        withLots.Should().Contain("ほか 3 ロット");
        withoutLots.Should().Contain(TradeDecisionPromptBuilder.EntryBasisUnsuppliedLine);
    }

    // ================================================================================================
    // 受け手（リスク管理の応答 → HeldPosition.Lots）
    // ================================================================================================

    // #1384 AC-04-15: 応答のロットの列を読む。項目が無い（旧い送り手）は null＝供給なし。数量の合わない列・欠けた行も null（保有は落とさない）。
    [Fact]
    public void 応答のロットの列を読み欠けや数量の不一致は供給なしにする()
    {
        var id = Guid.NewGuid();
        HttpHeldPositionProvider.OpenPositionDto Row(List<HttpHeldPositionProvider.OpenPositionEntryDto>? entries) =>
            new("AAPL", Market.UnitedStates, TradeSide.Buy, 50, 97m, 92m, entries);

        var ok = HttpHeldPositionProvider.InterpretPositions(
            [Row([new(id, 30, T1, "B", 95m), new(Guid.Empty, 20, T2, null, 0m)])], "AAPL", Market.UnitedStates, NullLogger.Instance);
        ok!.Lots.Should().Equal(new HeldPositionLot(id, 30, T1, "B", 95m), new HeldPositionLot(Guid.Empty, 20, T2, null, null));

        HttpHeldPositionProvider.InterpretPositions([Row(null)], "AAPL", Market.UnitedStates, NullLogger.Instance)!
            .Lots.Should().BeNull("旧い送り手");
        var mismatched = HttpHeldPositionProvider.InterpretPositions(
            [Row([new(id, 30, T1, "B", 95m)])], "AAPL", Market.UnitedStates, NullLogger.Instance);
        mismatched!.SignedQuantity.Should().Be(50, "ロットが読めなくても保有は落とさない");
        mismatched.Lots.Should().BeNull("数量が建玉と合わない");
        HttpHeldPositionProvider.InterpretPositions(
            [Row([new(null, 50, T1, "B", 95m)])], "AAPL", Market.UnitedStates, NullLogger.Instance)!
            .Lots.Should().BeNull("DecisionId が欠けている");
    }

    // #1384 AC-04-16: gRPC の行を同じ nullable の行へ写す（entries 未設定＝供給なし、UUID として読めない DecisionId は null）。
    [Fact]
    public void gRPCのロットの行を同じ行へ写す()
    {
        var id = Guid.NewGuid();
        var row = new Proto.OpenPositionRow { Symbol = "AAPL", Quantity = 50, EntryPrice = "97", StopLossPrice = "92" };
        GrpcHeldPositionProvider.ToRow(row).Entries.Should().BeNull("entries 未設定（旧い送り手）");

        row.Entries = new Proto.OpenPositionEntries();
        row.Entries.Rows.Add(new Proto.OpenPositionEntryRow
        {
            DecisionId = id.ToString(),
            Quantity = 50,
            OrderedAt = T1.ToString("O"),
            EntryCondition = "B",
            EntryReferencePrice = "95.5",
        });
        row.Entries.Rows.Add(new Proto.OpenPositionEntryRow { DecisionId = "not-a-uuid", Quantity = 1, OrderedAt = T1.ToString("O") });

        var entries = GrpcHeldPositionProvider.ToRow(row).Entries!;
        entries[0].Should().Be(new HttpHeldPositionProvider.OpenPositionEntryDto(id, 50, T1, "B", 95.5m));
        entries[1].DecisionId.Should().BeNull();
        entries[1].EntryCondition.Should().BeNull();
    }

    // ================================================================================================

    private static SizingContext Context() =>
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private static string HeldSection(string prompt)
    {
        prompt = prompt.Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = prompt.IndexOf(TradeDecisionPromptBuilder.HeldPositionSectionTitle, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = prompt.IndexOf("\n# ", start, StringComparison.Ordinal);
        return end < 0 ? prompt[start..] : prompt[start..(end + 1)];
    }

    private static AppSvc Create(string llmOutput, int held) => new(
        new FixedLlm(llmOutput), new FakePolicy(), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
        currentPrice: new FakeCurrentPrice(new CurrentPriceReading(96m, Intraday)),
        heldPosition: new FakeHeld(held));

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class FixedLlm(string output) : ILlmCompletionClient
    {
        public Task<string> CompleteAsync(string prompt, string? model = null, string? purpose = null, CancellationToken ct = default) =>
            Task.FromResult(output);
    }

    private sealed class FakePolicy : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult<DailyPolicy?>(Policy);
    }

    private sealed class FakeSizing : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(Context());
    }

    private sealed class FakeCurrentPrice(CurrentPriceReading? reading) : ICurrentPriceProvider
    {
        public bool IsEnabled => true;

        public Task<CurrentPriceReading?> GetCurrentPriceAsync(DecisionTrigger trigger, CancellationToken ct = default) =>
            Task.FromResult(reading);
    }

    private sealed class FakeHeld(int held) : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<int?>(held);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<HeldPosition?>(held == 0 ? HeldPosition.None : new HeldPosition(held, 97m, 92m));

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<WorkingEntryOrders?>(WorkingEntryOrders.None);
    }
}
