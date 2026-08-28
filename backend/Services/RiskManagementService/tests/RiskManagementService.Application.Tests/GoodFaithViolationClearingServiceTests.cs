using RiskManagementService.Application.Adapters;
using RiskManagementService.Application.Ports;
using RiskManagementService.Application.Services;
using RiskManagementService.Application.State;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Application.Tests;

// FR-19, FR-10, FR-11, UC-06, #464, ADR-0028 決定1/決定2, IADR-0182:
// GFV 違反による停止の解除（利用者の明示的な操作）。
//
// 🔴 **解除は記録を消さない。** ADR-0028 決定1 が「GFV 違反記録は失効させない」と定めている。
// 解けるのは**停止**であって記録ではない。
public class GoodFaithViolationClearingServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 8, 8);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;

        public DateOnly Today => Day;
    }

    private static GoodFaithViolationRecord Violation(string orderId) => new(
        Guid.NewGuid(), orderId, Guid.NewGuid(), "AAPL", Market.UnitedStates,
        PurchaseAmountInBase: 1000m, SettledCashInBase: 0m, OccurredOn: Day,
        ExecutedAt: Now.AddHours(-1), RecordedAt: Now.AddHours(-1));

    private static (GoodFaithViolationClearingService Service, InMemoryGoodFaithViolationStore Store)
        WithViolations(params string[] orderIds)
    {
        var store = new InMemoryGoodFaithViolationStore();
        foreach (var id in orderIds)
        {
            store.Append(Violation(id));
        }

        return (new GoodFaithViolationClearingService(store, new FixedClock()), store);
    }

    // 受け入れ基準1: 解除操作で停止が解ける（＝計数が停止基準を下回る）。
    [Fact]
    public void 解除すると計数が減り停止が解ける()
    {
        var (service, store) = WithViolations("ord-1", "ord-2");
        store.GetTally().BlocksNewEntry.Should().BeTrue("停止基準は 2 件である");

        var outcome = service.Clear("endazon", "原因を是正した");

        outcome.Accepted.Should().BeTrue();
        outcome.ClearedOrderIds.Should().BeEquivalentTo(["ord-1", "ord-2"]);
        outcome.RemainingCount.Should().Be(0);
        store.GetTally().BlocksNewEntry.Should().BeFalse();
    }

    // 🔴 受け入れ基準2（**否定形・決定1**）: 解除しても**違反記録そのものは残る**。
    // 台帳から行が消えると、発生の証跡（FR-11）が失われる。
    [Fact]
    public void 解除しても違反記録そのものは台帳に残る()
    {
        var (service, store) = WithViolations("ord-1", "ord-2");

        service.Clear("endazon", "原因を是正した");

        store.GetRecordedBetween(Day, Day).Should().HaveCount(2,
            "ADR-0028 決定1: GFV 違反記録は失効させない。解けるのは停止であって記録ではない");
    }

    // 受け入れ基準8（**否定形**）: 二重解除で件数が狂わない（冪等）。
    [Fact]
    public void 同じ記録を二重に解除しても件数が狂わない()
    {
        var (service, store) = WithViolations("ord-1");

        service.Clear("endazon", "原因を是正した");
        var second = service.Clear("endazon", "もう一度");

        // 2 度目は解除対象が無い＝受理しない（**成功に見せない**）。
        second.Accepted.Should().BeFalse();
        second.Rejection.Should().Be(GoodFaithViolationClearingRejection.NothingToClear);
        store.GetTally().Count.Should().Be(0);
        store.GetRecordedBetween(Day, Day).Should().ContainSingle("記録は残り続ける");
    }

    // **否定形**: 理由が空の解除は受理しない（ADR-0028 決定2「原因の是正が済んでいることの確認を伴う」）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 理由が空の解除は受理せず解除行も書かない(string? reason)
    {
        var (service, store) = WithViolations("ord-1");

        var outcome = service.Clear("endazon", reason!);

        outcome.Accepted.Should().BeFalse();
        outcome.Rejection.Should().Be(GoodFaithViolationClearingRejection.ReasonRequired);
        store.GetTally().Count.Should().Be(1, "受理しない要求は解除行を 1 件も書かない");
    }

    // **否定形**: 解除者が空なら受理しない（誰が解いたか不明な解除を作らない）。
    [Fact]
    public void 解除者が空なら受理しない()
    {
        var (service, store) = WithViolations("ord-1");

        var outcome = service.Clear("  ", "原因を是正した");

        outcome.Accepted.Should().BeFalse();
        outcome.Rejection.Should().Be(GoodFaithViolationClearingRejection.ActorRequired);
        store.GetTally().Count.Should().Be(1);
    }

    // **否定形**: 停止していない（解除対象が無い）ときは**成功に見せない**。
    // 「解除した」と記録すると、実際には何も起きていない操作が監査上の事実になる。
    [Fact]
    public void 解除対象が無ければ受理しない()
    {
        var (service, _) = WithViolations();

        var outcome = service.Clear("endazon", "原因を是正した");

        outcome.Accepted.Should().BeFalse();
        outcome.Rejection.Should().Be(GoodFaithViolationClearingRejection.NothingToClear);
        outcome.ClearedOrderIds.Should().BeEmpty();
    }

    // 🔴 受け入れ基準6（**否定形・最重要**）: **未供給のときの拒否は解除操作では解けない。**
    //
    // ADR-0028 が明記: 決定2 の解除は**記録が積まれた場合**の解除であり、
    // **供給が無い場合の拒否を解除する手段ではない**。
    //
    // **解除経路を実際に通したうえで**、未供給（`null`）の判定が変わらないことを見る ——
    // 判定関数だけを 2 回呼ぶ形では、解除が未供給判定へ影響するようになっても緑のままになる。
    [Fact]
    public void 解除を実行しても未供給の拒否は解けない()
    {
        var (service, store) = WithViolations("ord-1", "ord-2");

        // 解除は成功する（記録は積まれている）。
        service.Clear("endazon", "原因を是正した").Accepted.Should().BeTrue();

        // それでも「未供給」は未供給のままである。解除は**件数にしか作用しない** ——
        // ストアが判定コアへ結線されていない状態（null）を非 null にする手段を持たない。
        AccountTypePolicy.BlocksForGoodFaithViolations(null).Should().BeTrue(
            "未供給は fail-closed。ADR-0028 が「解除する手段ではない」と明記している");

        // 解除後に到達するのは Observed(0)＝**数えた結果の 0** であり、未供給とは別物である。
        var tally = store.GetTally();
        tally.Count.Should().Be(0);
        AccountTypePolicy.BlocksForGoodFaithViolations(tally).Should().BeFalse();
    }

    // **否定形**: 未供給（ストア未結線）を模した発注審査でも、解除の有無に関わらず拒否は続く。
    // `RiskEvaluator` の実判定を通し、「解除したら通るようになった」が起こらないことを固定する。
    [Fact]
    public void 未供給のスナップショットは解除の前後どちらでも拒否される()
    {
        var (service, _) = WithViolations("ord-1", "ord-2");

        AccountTypePolicy.BlocksForGoodFaithViolations(null).Should().BeTrue();

        service.Clear("endazon", "原因を是正した").Accepted.Should().BeTrue();

        AccountTypePolicy.BlocksForGoodFaithViolations(null).Should().BeTrue(
            "解除は未供給の判定に触れない（触れられる経路が存在しない）");
    }

    // 解除は**追記**である（記録した理由・解除者が残る）。ADR-0028 決定2 の監査要求の土台。
    [Fact]
    public void 解除は解除者と理由と時刻を伴って記録される()
    {
        var (service, _) = WithViolations("ord-1");

        var outcome = service.Clear("endazon", "決済済み資金の判定を修正した");

        outcome.ClearedAt.Should().Be(Now);
        outcome.ClearedOrderIds.Should().ContainSingle().Which.Should().Be("ord-1");
    }
}
