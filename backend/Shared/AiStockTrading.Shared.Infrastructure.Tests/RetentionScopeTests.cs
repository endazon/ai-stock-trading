using AiStockTrading.Shared.Contracts.Operations;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests;

// NFR-08 / NFR-09 / NFR-10 / NFR-11, FR-11, #339, IADR-0228:
// **7 年保持の業務台帳・監査証跡がパージされない**ことの統制テスト（否定形が中心）。
//
// 非機能要件は「費用台帳・発注履歴・監査ログ（FR-11）は重複排除メタデータと区別し、7 年保持する
// （自動パージの対象外）」と定める。構造的には既にそうなっていたが、**それを固定する宣言も検査も無かった**
// —— 新しいパージ経路を足しても何も赤くならない状態だった。
public class RetentionScopeTests
{
    // ── 境界値テーブル: パージ可否 ───────────────────────────────────────────
    public static TheoryData<string, bool> PurgeDecisions { get; } = new()
    {
        // パージ可（重複排除メタデータ・NFR-08）。
        { "processed_messages", true },
        { "order_dispatch_reservations", true },

        // 7 年保持（NFR-10）。監査ログ・費用台帳・発注履歴・借株料台帳・段階ゲート台帳など。
        { "audit_events", false },
        { "cost_entries", false },
        { "approved_orders", false },
        { "executed_orders", false },
        { "order_activity", false },
        { "order_lifecycle_events", false },
        { "trade_fills", false },
        { "borrow_fee_accruals", false },
        { "borrow_fee_unavailable_days", false },
        { "buy_in_inferences", false },
        { "good_faith_violations", false },
        { "stage_transitions", false },
        { "reports", false },
        { "assumptions_change_log", false },

        // 未知のストアも**パージされない側**へ倒れる（閉世界・fail-safe）。
        { "some_table_added_tomorrow", false },
    };

    [Theory]
    [MemberData(nameof(PurgeDecisions))]
    public void パージ可否は宣言どおりである(string store, bool expected) =>
        RetentionScope.IsPurgeable(store).Should().Be(expected);

    [Fact]
    public void パージしてよいストアは重複排除メタデータの_2_つだけである()
    {
        RetentionScope.PurgeableStores.Should().Equal(
            "processed_messages", "order_dispatch_reservations");
    }

    // ── 否定形 ───────────────────────────────────────────────────────────────

    // 🔴 これが本 issue の受け入れ基準「7 年対象がパージされない」の中核である。
    [Fact]
    public void 否定形_監査台帳はパージ対象に含まれない()
    {
        RetentionScope.IsPurgeable("audit_events").Should().BeFalse();

        var act = () => RetentionScope.EnsurePurgeable("audit_events");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*audit_events*7 年保持*");
    }

    [Theory]
    [InlineData("cost_entries")]
    [InlineData("approved_orders")]
    [InlineData("executed_orders")]
    [InlineData("borrow_fee_accruals")]
    public void 否定形_業務台帳をパージ対象にしようとすると止まる(string store)
    {
        var act = () => RetentionScope.EnsurePurgeable(store);

        act.Should().Throw<InvalidOperationException>();
    }

    // 🔴 **未知のストアは 7 年側へ倒れる。** 7 年側を列挙する設計にすると、テーブルが増えるたびに
    // 表が腐り、漏れたストアが黙ってパージ可になる（fail-open）。閉世界なら必ず「消さない」へ倒れる。
    [Fact]
    public void 否定形_宣言に無いストアは既定でパージされない()
    {
        RetentionScope.IsPurgeable("unknown_store").Should().BeFalse();

        var act = () => RetentionScope.EnsurePurgeable("unknown_store");

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空のストア名は引数例外になる(string store)
    {
        var act = () => RetentionScope.EnsurePurgeable(store);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 宣言済みのストアは_EnsurePurgeable_を通る()
    {
        foreach (var store in RetentionScope.PurgeableStores)
        {
            var act = () => RetentionScope.EnsurePurgeable(store);
            act.Should().NotThrow($"{store} は重複排除メタデータであり、パージが要件（NFR-08）");
        }
    }

    // NFR-11: パージ処理は既定で無効（オプトイン）。RetentionOptionsTests が既定値そのものを固定しており、
    // ここでは「保持区分の宣言」と「既定オフ」が同じ要件の両輪であることを 1 本で押さえる。
    [Fact]
    public void パージは既定で無効である()
    {
        new RetentionOptions().Enabled.Should().BeFalse();
    }
}
