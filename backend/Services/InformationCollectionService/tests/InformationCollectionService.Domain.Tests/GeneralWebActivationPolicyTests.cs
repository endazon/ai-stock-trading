using System.Globalization;
using AiStockTrading.InformationCollection.Domain;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.InformationCollection.Domain.Tests;

// FR-01, ADR-0020 決定4: 一般インターネット収集（最終手段）の**発動条件 4 件の境界テスト**（#336 受け入れ基準③）。
//
// 🔴 **条件のない「最終手段」は運用時の裁量になる。** 境界（5 営業日）と、各条件の単独欠落を固定する。
public class GeneralWebActivationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static GeneralWebActivationRequest AllSatisfied() => new(
        Category: "news",
        OutageBusinessDays: 5,
        ProviderAnnouncedDiscontinuation: false,
        HarmConfirmedInReports: true,
        TermsPermitAutomatedAccess: true,
        DataSeparationApplied: true,
        CorroboratedByIndependentSources: true);

    // --- 境界値: 条件1（欠測が 5 営業日以上継続） ---

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]  // 境界の 1 つ手前は成立しない
    [InlineData(5, true)]   // 境界ちょうどで成立する
    [InlineData(6, true)]
    public void 欠測日数の境界は5営業日である(int outageBusinessDays, bool approved)
    {
        var request = AllSatisfied() with { OutageBusinessDays = outageBusinessDays };

        GeneralWebActivationPolicy.Evaluate(request, Now).Approved.Should().Be(approved);
    }

    // 提供終了・有料化の公表があれば日数を待たない（条件1 は「または」である）。
    [Fact]
    public void 提供終了の公表があれば欠測日数を待たずに条件1を満たす()
    {
        var request = AllSatisfied() with
        {
            OutageBusinessDays = 0,
            ProviderAnnouncedDiscontinuation = true,
        };

        GeneralWebActivationPolicy.Evaluate(request, Now).Approved.Should().BeTrue();
    }

    // --- 各条件の単独欠落（4 件すべてを満たさなければ発動しない） ---

    [Fact]
    public void 実害の記録が無ければ発動しない()
    {
        var decision = GeneralWebActivationPolicy.Evaluate(
            AllSatisfied() with { HarmConfirmedInReports = false }, Now);

        decision.Approved.Should().BeFalse();
        decision.UnmetConditions.Should().ContainMatch("条件2*");
    }

    [Fact]
    public void 利用規約が自動取得を禁止していれば発動しない()
    {
        var decision = GeneralWebActivationPolicy.Evaluate(
            AllSatisfied() with { TermsPermitAutomatedAccess = false }, Now);

        decision.Approved.Should().BeFalse();
        decision.UnmetConditions.Should().ContainMatch("条件3*");
    }

    [Fact]
    public void データ分離が無ければ発動しない()
    {
        var decision = GeneralWebActivationPolicy.Evaluate(
            AllSatisfied() with { DataSeparationApplied = false }, Now);

        decision.Approved.Should().BeFalse();
        decision.UnmetConditions.Should().ContainMatch("条件4a*");
    }

    [Fact]
    public void 複数独立ソースの裏取りが無ければ発動しない()
    {
        var decision = GeneralWebActivationPolicy.Evaluate(
            AllSatisfied() with { CorroboratedByIndependentSources = false }, Now);

        decision.Approved.Should().BeFalse();
        decision.UnmetConditions.Should().ContainMatch("条件4b*");
    }

    // 🔴 **満たしていない条件は全部返す。** 1 つ直すたびに再申請する運用は、条件の確認を形骸化させる。
    [Fact]
    public void 満たしていない条件をすべて列挙する()
    {
        var decision = GeneralWebActivationPolicy.Evaluate(
            new GeneralWebActivationRequest("news", 0, false, false, false, false, false), Now);

        decision.Approved.Should().BeFalse();
        decision.UnmetConditions.Should().HaveCount(5); // 条件1・2・3・4a・4b
        decision.ProvisionalUntil.Should().BeNull();
    }

    // --- 暫定期限（次回月報まで・恒久化しない） ---

    [Fact]
    public void 承認されると次回月報までの暫定期限が付く()
    {
        var decision = GeneralWebActivationPolicy.Evaluate(AllSatisfied(), Now);

        decision.Approved.Should().BeTrue();
        decision.ProvisionalUntil.Should().Be(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("2026-01-15T00:00:00Z", "2026-02-01T00:00:00Z")]
    [InlineData("2026-12-31T23:59:59Z", "2027-01-01T00:00:00Z")] // 年跨ぎ
    [InlineData("2026-02-01T00:00:00Z", "2026-03-01T00:00:00Z")] // 月初ちょうど
    public void 暫定期限は翌月1日である(string now, string expected)
    {
        GeneralWebActivationPolicy.NextMonthlyReportBoundary(DateTimeOffset.Parse(now, CultureInfo.InvariantCulture))
            .Should().Be(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture));
    }
}
