using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-20, UC-06, ADR-0016 決定14（2026-08-07 確定・verdict の形式）, #388, IADR-0281:
// **空売り実弾解禁の verdict**（実弾解禁前の確認が「済んだ」という判定）の有効性判定を固定する。
//
// 裁定が定めた受け入れ基準は**否定形が最重要**である。
//   ① equity $5,000 を満たしても verdict が無ければ解禁されない（fail-closed・最重要）
//   ② 空売りを含まない戦略の Stage 0 合格では解禁されない
//   ③ 供給の欠落はフェイルクローズのまま
//   ④ 31 日前の verdict では解禁不可・**30 日ちょうどは可**（境界）
//   ⑤ 情報源変更直後・戦略変更直後は期限内でも解禁不可
public class ShortSellReleaseVerdictTests
{
    private static readonly decimal ReleaseEquity = StageProductPolicy.ShortSellLiveReleaseEquityUsd;
    private static readonly DateTimeOffset Issued = ShortSellReleaseFixtures.IssuedAt;

    // ------------------------------------------------------------------
    // 1. verdict の有効性（純関数 ShortSellReleasePolicy）
    // ------------------------------------------------------------------

    [Fact]
    public void 有効期限は30日である()
    {
        // ADR-0016 決定14: 「30 日。期限を過ぎた verdict では解禁できない」。
        // 決定4 の強制買戻し禁止期間と同じ長さであり、計画 ADR 内に新しい時間単位を増やさない。
        ShortSellReleasePolicy.ValidityPeriod.Should().Be(TimeSpan.FromDays(30));
    }

    // **否定形①（最重要）**: verdict が無ければ、他のすべてを満たしていても解禁されない。
    [Fact]
    public void verdictが無ければ解禁されない()
    {
        ShortSellReleasePolicy.Evaluate(
                verdict: null,
                ShortSellReleaseFixtures.Fingerprint,
                ShortSellReleaseFixtures.StrategyId,
                Issued)
            .Should().Be(ShortSellReleaseVerdictStatus.Missing);
    }

    // **境界④**: 30 日ちょうどは有効・30 日 + 1 tick と 31 日は無効。
    [Theory]
    [InlineData(0, ShortSellReleaseVerdictStatus.Valid)]
    [InlineData(29, ShortSellReleaseVerdictStatus.Valid)]
    [InlineData(30, ShortSellReleaseVerdictStatus.Valid)]
    [InlineData(31, ShortSellReleaseVerdictStatus.Expired)]
    [InlineData(60, ShortSellReleaseVerdictStatus.Expired)]
    public void 有効期限の境界は30日ちょうどまで有効である(int elapsedDays, ShortSellReleaseVerdictStatus expected)
    {
        ShortSellReleasePolicy.Evaluate(
                ShortSellReleaseFixtures.Verdict(),
                ShortSellReleaseFixtures.Fingerprint,
                ShortSellReleaseFixtures.StrategyId,
                Issued.AddDays(elapsedDays))
            .Should().Be(expected);
    }

    [Fact]
    public void 有効期限は30日を1tick超えた時点で切れる()
    {
        var justOver = Issued + ShortSellReleasePolicy.ValidityPeriod + TimeSpan.FromTicks(1);

        ShortSellReleasePolicy.Evaluate(
                ShortSellReleaseFixtures.Verdict(),
                ShortSellReleaseFixtures.Fingerprint,
                ShortSellReleaseFixtures.StrategyId,
                justOver)
            .Should().Be(ShortSellReleaseVerdictStatus.Expired);
    }

    // **否定形（安全側の既定）**: 発行時刻が未来＝台帳の時刻が壊れている状態を「有効」と読まない。
    [Fact]
    public void 発行時刻が未来のverdictは期限切れとして扱う()
    {
        ShortSellReleasePolicy.Evaluate(
                ShortSellReleaseFixtures.Verdict(issuedAt: Issued.AddDays(1)),
                ShortSellReleaseFixtures.Fingerprint,
                ShortSellReleaseFixtures.StrategyId,
                Issued)
            .Should().Be(ShortSellReleaseVerdictStatus.Expired);
    }

    // **否定形⑤（裁定が名指しした穴）**: 「借株料の照会経路が変わった翌日に古い verdict で解禁できてしまう」。
    [Fact]
    public void 情報源が変わったら期限内でも無効になる()
    {
        ShortSellReleasePolicy.Evaluate(
                ShortSellReleaseFixtures.Verdict(),
                // 借株照会の経路が差し替わった（アダプタの登録が変わった）状態。
                currentSourceFingerprint: "borrow=other-broker;margin=broker-funds",
                ShortSellReleaseFixtures.StrategyId,
                Issued.AddDays(1))
            .Should().Be(ShortSellReleaseVerdictStatus.SourceChanged);
    }

    // **否定形⑤**: 戦略の変更も同じく無効化の契機である（決定14 は verdict を戦略に紐づけている）。
    [Fact]
    public void 戦略が変わったら期限内でも無効になる()
    {
        ShortSellReleasePolicy.Evaluate(
                ShortSellReleaseFixtures.Verdict(),
                ShortSellReleaseFixtures.Fingerprint,
                currentStrategyId: "short-momentum-v3",
                Issued.AddDays(1))
            .Should().Be(ShortSellReleaseVerdictStatus.StrategyChanged);
    }

    // **否定形**: 戦略の同一性を名乗れない（空文字）なら「同じ」と読まない。
    // バックテスト verdict が未供給のとき（既定の空文字）に verdict が有効化されるのを塞ぐ。
    [Theory]
    [InlineData("", ShortSellReleaseFixtures.StrategyId)]
    [InlineData(ShortSellReleaseFixtures.StrategyId, "")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void 戦略識別子が空なら一致しているように見えても無効である(string verdictStrategy, string currentStrategy)
    {
        ShortSellReleasePolicy.Evaluate(
                ShortSellReleaseFixtures.Verdict(strategyId: verdictStrategy),
                ShortSellReleaseFixtures.Fingerprint,
                currentStrategy,
                Issued.AddDays(1))
            .Should().Be(ShortSellReleaseVerdictStatus.StrategyChanged);
    }

    // ------------------------------------------------------------------
    // 2. 情報源フィンガープリント（IADR-0281 決定2）
    // ------------------------------------------------------------------

    // **供給元が未実装であることの正しい表現。** 空文字ではなく `none` と名乗る。
    [Fact]
    public void 供給アダプタが1つも無ければフィンガープリントはnoneである()
    {
        ShortSellReleaseSources.Fingerprint([], []).Should().Be("borrow=none;margin=none");
        ShortSellReleaseSources.Fingerprint(null, null).Should().Be("borrow=none;margin=none");
    }

    // 登録順・重複・空白は結果に影響しない——DI の登録順が変わっただけで verdict が失効するのは、
    // 裁定が意図した「情報源の変更」ではない。
    [Fact]
    public void フィンガープリントは登録順と重複と空白に依存しない()
    {
        var a = ShortSellReleaseSources.Fingerprint(["moomoo", " finnhub "], ["funds"]);
        var b = ShortSellReleaseSources.Fingerprint(["finnhub", "moomoo", "moomoo", "  "], ["funds"]);

        a.Should().Be(b);
        a.Should().Be("borrow=finnhub,moomoo;margin=funds");
    }

    // **供給が結線されればフィンガープリントは必ず変わる**＝既存 verdict が自動失効する（裁定 ①）。
    [Fact]
    public void 供給が結線されるとフィンガープリントが変わる()
    {
        var unsupplied = ShortSellReleaseSources.Fingerprint([], []);
        var supplied = ShortSellReleaseSources.Fingerprint(["moomoo-margin-ratio"], []);

        supplied.Should().NotBe(unsupplied);
    }

    // 登録アダプタの列挙（Features 層の目印インターフェース）から同じ値が出ることを固定する。
    [Fact]
    public void 登録アダプタの列挙からフィンガープリントを作る()
    {
        new Features.RiskManagement.ShortSellReleaseSourceInventory([])
            .CurrentFingerprint().Should().Be("borrow=none;margin=none");

        new Features.RiskManagement.ShortSellReleaseSourceInventory(
                [
                    new FakeReleaseSource(ShortSellReleaseSourceKind.BorrowLookup, "moomoo-margin-ratio"),
                    new FakeReleaseSource(ShortSellReleaseSourceKind.MaintenanceMargin, "broker-funds"),
                ])
            .CurrentFingerprint().Should().Be(ShortSellReleaseFixtures.Fingerprint);
    }

    // ------------------------------------------------------------------
    // 3. 段階別の商品種別強制との合成（3 項の AND）
    // ------------------------------------------------------------------

    // **否定形①**: equity を満たしても verdict が無ければ Stage 3 の空売りは開かない。
    [Fact]
    public void equityを満たしてもverdictが無ければ空売りは開かない()
    {
        var release = ShortSellReleaseFixtures.WithoutVerdict();

        StageProductPolicy.Evaluate(
                TradingStage.Stage3ScaledLive, ProductType.ShortSell, ReleaseEquity * 100m, release)
            .Should().Be(RejectionReason.StageShortSellReleaseUnmet);
        release.VerdictStatus.Should().Be(ShortSellReleaseVerdictStatus.Missing);
    }

    // **否定形②**: 空売りを含まない戦略の Stage 0 合格では解禁されない（verdict が有効でも）。
    [Fact]
    public void 空売りを含まない戦略のStage0合格では解禁されない()
    {
        var release = ShortSellReleaseFixtures.Released(shortSellStrategyBacktestPassed: false);

        release.VerdictStatus.Should().Be(ShortSellReleaseVerdictStatus.Valid, "verdict 自体は有効である");
        StageProductPolicy.Evaluate(
                TradingStage.Stage3ScaledLive, ProductType.ShortSell, ReleaseEquity * 100m, release)
            .Should().Be(RejectionReason.StageShortSellReleaseUnmet);
    }

    // **否定形④⑤**: 期限切れ・情報源の変更・戦略の変更のいずれでも空売りは開かない。
    [Theory]
    [InlineData(31, ShortSellReleaseFixtures.Fingerprint, ShortSellReleaseFixtures.StrategyId)]
    [InlineData(1, "borrow=none;margin=none", ShortSellReleaseFixtures.StrategyId)]
    [InlineData(1, ShortSellReleaseFixtures.Fingerprint, "short-momentum-v3")]
    public void verdictが無効なら空売りは開かない(int elapsedDays, string fingerprint, string strategyId)
    {
        var release = ShortSellReleaseFixtures.Released(
            now: Issued.AddDays(elapsedDays), currentFingerprint: fingerprint, currentStrategyId: strategyId);

        release.VerdictStatus.Should().NotBe(ShortSellReleaseVerdictStatus.Valid);
        StageProductPolicy.Evaluate(
                TradingStage.Stage3ScaledLive, ProductType.ShortSell, ReleaseEquity * 100m, release)
            .Should().Be(RejectionReason.StageShortSellReleaseUnmet);
    }

    // **境界④の合成**: 30 日ちょうどは開き、31 日は開かない。
    [Fact]
    public void 空売りは30日ちょうどまで開き31日で閉じる()
    {
        StageProductPolicy.Evaluate(
                TradingStage.Stage3ScaledLive, ProductType.ShortSell, ReleaseEquity,
                ShortSellReleaseFixtures.Released(now: Issued.AddDays(30)))
            .Should().BeNull("30 日ちょうどの verdict は有効である");

        StageProductPolicy.Evaluate(
                TradingStage.Stage3ScaledLive, ProductType.ShortSell, ReleaseEquity,
                ShortSellReleaseFixtures.Released(now: Issued.AddDays(31)))
            .Should().Be(RejectionReason.StageShortSellReleaseUnmet, "31 日前の verdict では解禁できない");
    }

    // **否定形③**: 供給（文脈そのもの）の欠落は従来どおりフェイルクローズのままである。
    [Fact]
    public void 解禁条件の供給が無ければ従来どおり空売りは開かない()
    {
        StageProductPolicy.Evaluate(
                TradingStage.Stage3ScaledLive, ProductType.ShortSell, ReleaseEquity * 100m, release: null)
            .Should().Be(RejectionReason.StageShortSellReleaseUnmet);
    }

    private sealed record FakeReleaseSource(ShortSellReleaseSourceKind Kind, string SourceId)
        : Features.RiskManagement.IShortSellReleaseSource;
}
