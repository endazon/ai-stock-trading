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
        RecordWith(asOf, majority, signedQuantity, null, raws);

    // FR-15, ADR-0036 決定1, #749, IADR-0387: 申告つきの記録（null は**未申告**＝旧記録の形）。
    private static Stage0DecisionRecord RecordWith(
        DateOnly asOf,
        Stage0DecisionAction majority,
        int signedQuantity,
        IReadOnlyList<Stage0AsOfInputStatus>? asOfInputs,
        params Stage0RawDecision[] raws) =>
        new("AAPL", Market.UnitedStates, asOf, "fingerprint-1", "claude-sonnet-5",
            VoteCount: raws.Length, raws, majority, "多数決の根拠", signedQuantity,
            CostJpy: 12.5m, InputTokens: 3_600, OutputTokens: 540, AsOfInputs: asOfInputs);

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

    // ---- T-15-107 FR-15, ADR-0036 決定1, #749, IADR-0387: 再構成可否は記録の一部である ----

    private static IReadOnlyList<Stage0AsOfInputStatus> Declared(
        Stage0AsOfInputAvailability news = Stage0AsOfInputAvailability.Reconstructed) =>
    [
        new(Stage0AsOfInputKind.NewsAndDisclosures, news, news == Stage0AsOfInputAvailability.Reconstructed
            ? string.Empty
            : "情報源が過去分を提供しない"),
        new(Stage0AsOfInputKind.DailyPolicy, Stage0AsOfInputAvailability.Reconstructed),
        new(Stage0AsOfInputKind.FxRateToBase, Stage0AsOfInputAvailability.Reconstructed),
    ];

    // 🔴 **肯定形（最重要）**: 申告は JSON 往復で落ちない。記録はファイルで別サービスへ渡るため、
    // ここが落ちると再生側では全件が**未申告**に見え、判定が組めなくなる（安全側だが Stage 0 が進まない）。
    [Fact]
    public void as_of入力の再構成可否は往復で落ちない()
    {
        var original = SetOf(RecordWith(
            new DateOnly(2026, 6, 2), Stage0DecisionAction.Buy, 10,
            Declared(Stage0AsOfInputAvailability.NotReconstructable), Raw(1, Stage0DecisionAction.Buy)));

        var restored = Stage0DecisionRecordJson.TryDeserialize(Stage0DecisionRecordJson.Serialize(original))!;

        var inputs = restored.Records.Should().ContainSingle().Which.AsOfInputs;
        Stage0AsOfInputs.IsDeclared(inputs).Should().BeTrue();
        Stage0AsOfInputs.NotReconstructableKinds(inputs)
            .Should().ContainSingle().Which.Should().Be(Stage0AsOfInputKind.NewsAndDisclosures);
        // 列挙は文字列で残る（宣言順が変わっても意味が動かない）。
        Stage0DecisionRecordJson.Serialize(original).Should().Contain("NotReconstructable");
    }

    // 🔴 **否定形（最重要・0 件と未供給の区別）**: 申告の欄を持たない JSON は **null（未申告）**へ復元され、
    // 「すべて再構成できた」へは倒れない。旧記録・手書きの記録が黙って充足側へ入る口を塞ぐ。
    [Fact]
    public void 申告の無い旧記録は未申告へ復元され充足へ倒れない()
    {
        var json = Stage0DecisionRecordJson.Serialize(SetOf(RecordWith(
            new DateOnly(2026, 6, 2), Stage0DecisionAction.Buy, 10, null, Raw(1, Stage0DecisionAction.Buy))));

        json.Should().NotContain("asOfInputs\":[");
        var restored = Stage0DecisionRecordJson.TryDeserialize(json)!;

        var record = restored.Records.Should().ContainSingle().Subject;
        record.AsOfInputs.Should().BeNull();
        Stage0AsOfInputs.IsDeclared(record.AsOfInputs).Should().BeFalse();
        // 🔴 「未申告」を「除外すべきものが無い」と読めてはならない。
        Stage0AsOfInputs.IsExcluded(record.AsOfInputs).Should().BeFalse();
    }

    // 🔴 **否定形**: 判断列が同じでも申告が違えば別の戦略である（評価する母集団が違うため）。
    // ここが同一 ID になると、**別の母集団で採った合格が生き残る**（IADR-0281 決定3 の無効化契機が効かない）。
    [Fact]
    public void 再構成可否が違えば戦略IDが変わる()
    {
        IReadOnlyList<Stage0RecordedSymbol> symbols = [new("AAPL", Market.UnitedStates)];
        var complete = RecordWith(
            new DateOnly(2026, 6, 2), Stage0DecisionAction.Buy, 10, Declared(), Raw(1, Stage0DecisionAction.Buy));
        var thin = RecordWith(
            new DateOnly(2026, 6, 2), Stage0DecisionAction.Buy, 10,
            Declared(Stage0AsOfInputAvailability.NotReconstructable), Raw(1, Stage0DecisionAction.Buy));
        var undeclared = RecordWith(
            new DateOnly(2026, 6, 2), Stage0DecisionAction.Buy, 10, null, Raw(1, Stage0DecisionAction.Buy));

        string Hash(Stage0DecisionRecord r) => Stage0StrategyIdentity.ComputeContentHash(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 31), symbols,
            new DateOnly(2026, 3, 31), "claude-sonnet-5", [r]);

        new[] { Hash(complete), Hash(thin), Hash(undeclared) }.Distinct().Should().HaveCount(3);
    }

    // ---- T-10-1621 FR-04, ADR-0044 決定 3, #1034, IADR-0440 決定 7: (e) 当時の監視銘柄の申告と戦略 ID ----

    private static string HashOf(Stage0DecisionRecord r) => Stage0StrategyIdentity.ComputeContentHash(
        new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 31), [new Stage0RecordedSymbol("AAPL", Market.UnitedStates)],
        new DateOnly(2026, 3, 31), "claude-sonnet-5", [r]);

    // 🔴 T-10-1621 **否定形（最重要）**: (e) を申告しない記録（ADR-0044 より前の記録）の戦略 ID は変わらない。
    // 期待値は (e) を足す前の実装（origin/develop `209ae4ce`）で同じ記録から計算した値である。変われば、既存の verdict が
    // 「戦略の変更」として無効化される（IADR-0281 決定3）。
    [Fact]
    public void 監視銘柄を申告しない記録の戦略IDは変わらない()
    {
        var legacy = RecordWith(
            new DateOnly(2026, 6, 2), Stage0DecisionAction.Buy, 10, Declared(), Raw(1, Stage0DecisionAction.Buy));

        HashOf(legacy).Should().Be(LegacyDeclaredHash);
    }

    private const string LegacyDeclaredHash = "757fd6292352d39c";

    // 🔴 T-10-1621 **否定形**: (e) の申告の有無・可否が違えば別の戦略である（何を合否から外すかが違う）。
    // (e) の申告は JSON 往復で落ちず、再構成不可は除外の理由として読める。
    [Fact]
    public void 監視銘柄の申告の有無と可否が違えば戦略IDが変わり往復で落ちない()
    {
        Stage0DecisionRecord With(Stage0AsOfInputAvailability? watchlist) => RecordWith(
            new DateOnly(2026, 6, 2), Stage0DecisionAction.Buy, 10,
            watchlist is { } w ? [.. Declared(), new(Stage0AsOfInputKind.Watchlist, w)] : Declared(),
            Raw(1, Stage0DecisionAction.Buy));

        new[]
        {
            HashOf(With(null)),
            HashOf(With(Stage0AsOfInputAvailability.Reconstructed)),
            HashOf(With(Stage0AsOfInputAvailability.NotReconstructable)),
        }.Distinct().Should().HaveCount(3);

        var original = SetOf(With(Stage0AsOfInputAvailability.NotReconstructable));
        var json = Stage0DecisionRecordJson.Serialize(original);
        json.Should().Contain("\"Watchlist\"");
        var inputs = Stage0DecisionRecordJson.TryDeserialize(json)!.Records.Should().ContainSingle().Which.AsOfInputs;
        Stage0AsOfInputs.NotReconstructableKinds(inputs).Should().Equal(Stage0AsOfInputKind.Watchlist);
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

    // ---- T-15-120 FR-15, ADR-0054 決定3, #1196, IADR-0498: 一次スクリーニングと両層の実効モデル ----

    private static Stage0DecisionRecord TwoTier(
        string? screeningModel = "claude-haiku-4-5", string? decisionModel = "claude-sonnet-5") =>
        RecordWith(
            new DateOnly(2026, 6, 2), Stage0DecisionAction.Buy, 10, Declared(),
            Raw(1, Stage0DecisionAction.Buy) with { EffectiveModelId = decisionModel }) with
        {
            Screening = new Stage0ScreeningDecision(
                Stage0DecisionAction.Buy, Unparseable: false, "関心あり", 300, 40, screeningModel),
        };

    // 🔴 T-15-120（受け入れ基準 2）: 一次の判断と両層の実効モデルは JSON 往復で落ちない（層ごとに別々に残る）。
    [Fact]
    public void 一次と両層の実効モデルは往復で落ちない()
    {
        var restored = Stage0DecisionRecordJson.TryDeserialize(Stage0DecisionRecordJson.Serialize(SetOf(TwoTier())))!;

        var record = restored.Records.Should().ContainSingle().Subject;
        record.Screening.Should().Be(new Stage0ScreeningDecision(
            Stage0DecisionAction.Buy, false, "関心あり", 300, 40, "claude-haiku-4-5"));
        record.RawDecisions.Should().ContainSingle().Which.EffectiveModelId.Should().Be("claude-sonnet-5");
        Stage0TwoTierModels.IsScreeningRecorded(record).Should().BeTrue();
        Stage0TwoTierModels.MatchesPinnedAssignments(record).Should().BeTrue();
    }

    // 🔴 T-15-120 **否定形（受け入れ基準 5）**: 一次の欄を持たない旧 JSON は **null（一次を記録していない）**へ復元され、
    // 二段の記録へは倒れない（再生側で評価不能になる）。ピンとの一致も主張しない。
    [Fact]
    public void 一次の無い旧記録はnullへ復元され二段の記録へ倒れない()
    {
        var json = Stage0DecisionRecordJson.Serialize(SetOf(RecordWith(
            new DateOnly(2026, 6, 2), Stage0DecisionAction.Buy, 10, Declared(), Raw(1, Stage0DecisionAction.Buy))));
        var withoutField = json.Replace(",\"screening\":null", string.Empty, StringComparison.Ordinal);

        withoutField.Should().NotContain("screening");
        var record = Stage0DecisionRecordJson.TryDeserialize(withoutField)!.Records.Should().ContainSingle().Subject;
        record.Screening.Should().BeNull();
        record.RawDecisions[0].EffectiveModelId.Should().BeNull();
        Stage0TwoTierModels.IsScreeningRecorded(record).Should().BeFalse();
        Stage0TwoTierModels.MatchesPinnedAssignments(record).Should().BeFalse();
    }

    // 🔴 T-15-120: ピンとの照合は用途ごと（一次＝haiku・本判断＝sonnet）で、層を取り違えた・名乗らない記録は一致にならない。
    [Theory]
    [InlineData("claude-haiku-4-5", "claude-sonnet-5", true)]
    [InlineData("CLAUDE-HAIKU-4-5", " claude-sonnet-5 ", true)]  // 大小・前後空白は照合器の規則どおり
    [InlineData("claude-sonnet-5", "claude-haiku-4-5", false)]   // 層の取り違え
    [InlineData("claude-sonnet-5", "claude-sonnet-5", false)]    // 一次だけピン外
    [InlineData("claude-haiku-4-5", "claude-opus-5", false)]     // 本判断だけピン外
    [InlineData(null, "claude-sonnet-5", false)]                 // 一次が不明
    [InlineData("claude-haiku-4-5", null, false)]                // 本判断が不明
    public void 両層の実効モデルは用途ごとのピンと照合される(string? screeningModel, string? decisionModel, bool expected) =>
        Stage0TwoTierModels.MatchesPinnedAssignments(TwoTier(screeningModel, decisionModel)).Should().Be(expected);

    // T-15-120: 一次で見送った判断（本判断の票 0）は一次の実効モデルだけで照合する。
    [Fact]
    public void 一次で見送った判断は一次の実効モデルだけで照合される()
    {
        var screenedOut = TwoTier() with
        {
            RawDecisions = [],
            Screening = new Stage0ScreeningDecision(Stage0DecisionAction.Hold, false, "関心なし", 300, 40, "claude-haiku-4-5"),
        };

        screenedOut.Screening!.Interested.Should().BeFalse();
        Stage0TwoTierModels.MatchesPinnedAssignments(screenedOut).Should().BeTrue();
        Stage0TwoTierModels.MatchesPinnedAssignments(
            screenedOut with { Screening = screenedOut.Screening with { EffectiveModelId = "claude-sonnet-5" } })
            .Should().BeFalse();
    }

    // 🔴 T-15-120: 一次と実効モデルは戦略 ID に入る（違えば別の戦略）。一次の無い旧記録の戦略 ID は変わらない
    // （`監視銘柄を申告しない記録の戦略IDは変わらない` の固定値 LegacyDeclaredHash がその陰性対照である）。
    [Fact]
    public void 一次と両層の実効モデルが違えば戦略IDが変わる()
    {
        var legacy = RecordWith(
            new DateOnly(2026, 6, 2), Stage0DecisionAction.Buy, 10, Declared(), Raw(1, Stage0DecisionAction.Buy));
        var screenedHold = TwoTier() with
        {
            Screening = TwoTier().Screening! with { Action = Stage0DecisionAction.Hold },
        };

        HashOf(legacy).Should().Be(LegacyDeclaredHash);
        new[]
        {
            HashOf(legacy),
            HashOf(TwoTier()),
            HashOf(TwoTier(screeningModel: "claude-sonnet-5")),
            HashOf(TwoTier(decisionModel: "claude-opus-5")),
            HashOf(screenedHold),
        }.Distinct().Should().HaveCount(5);
    }
}
