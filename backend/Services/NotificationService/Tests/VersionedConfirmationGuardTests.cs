using NotificationService.Features.Notifications;
using AwesomeAssertions;
using Xunit;

namespace NotificationService.Tests;

// FR-14, IADR-0062 決定6: 版番号付き冪等確定（詳細設計07「二重実行防止」）。
// 受け入れ基準10: 同一 対象ID＋版番号 の再確定は AlreadyConfirmed・古い版は Stale。
// 会話キーは report_type + period（例: daily-2026-07-07）。
public class VersionedConfirmationGuardTests
{
    private const string Daily = "daily-2026-07-07";

    [Fact]
    public void 未確定の版は受理される()
    {
        var guard = new VersionedConfirmationGuard();

        guard.TryConfirm(Daily, 1).Should().Be(ConfirmationOutcome.Accepted);
    }

    // 冪等: 2回目以降は「確定済み」を返すのみで副作用なし。
    [Fact]
    public void 同一の対象IDと版番号の再確定は確定済みを返す()
    {
        var guard = new VersionedConfirmationGuard();
        guard.TryConfirm(Daily, 2).Should().Be(ConfirmationOutcome.Accepted);

        guard.TryConfirm(Daily, 2).Should().Be(ConfirmationOutcome.AlreadyConfirmed);
        guard.TryConfirm(Daily, 2).Should().Be(ConfirmationOutcome.AlreadyConfirmed);
    }

    // 楽観ロック: 版が古い確定要求は拒否し「最新ドラフトを確認してください」と応答させる。
    [Fact]
    public void 確定済みより古い版の確定要求は拒否される()
    {
        var guard = new VersionedConfirmationGuard();
        guard.TryConfirm(Daily, 3).Should().Be(ConfirmationOutcome.Accepted);

        guard.TryConfirm(Daily, 2).Should().Be(ConfirmationOutcome.Stale);
    }

    // ドラフト更新後（v1→v2）の確定は受け付ける。
    [Fact]
    public void より新しい版の確定は受理される()
    {
        var guard = new VersionedConfirmationGuard();
        guard.TryConfirm(Daily, 1).Should().Be(ConfirmationOutcome.Accepted);

        guard.TryConfirm(Daily, 2).Should().Be(ConfirmationOutcome.Accepted);
    }

    // 対象IDごとに独立（日報の確定が週報に影響しない）。
    [Fact]
    public void 対象IDが異なれば独立して確定できる()
    {
        var guard = new VersionedConfirmationGuard();
        guard.TryConfirm(Daily, 1).Should().Be(ConfirmationOutcome.Accepted);

        guard.TryConfirm("weekly-2026-W28", 1).Should().Be(ConfirmationOutcome.Accepted);
    }

    // チャットUIと Discord の同時確定でも一度しか有効にならない（詳細設計07）。
    // 並行に同一版を確定要求しても Accepted はちょうど1つ。
    [Fact]
    public async Task 同時確定でも受理は一度だけである()
    {
        var guard = new VersionedConfirmationGuard();

        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => Task.Run(() => guard.TryConfirm(Daily, 1))));

        outcomes.Count(o => o == ConfirmationOutcome.Accepted).Should().Be(1);
        outcomes.Count(o => o == ConfirmationOutcome.AlreadyConfirmed).Should().Be(31);
    }

    [Fact]
    public void 対象IDが空なら例外になる()
    {
        var guard = new VersionedConfirmationGuard();

        var act = () => guard.TryConfirm("  ", 1);

        act.Should().Throw<ArgumentException>();
    }

    // 版番号は 1 始まりの正数（詳細設計07 の v1・v2）。0/負数は呼び出し側のバグとして弾く。
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 版番号が正でなければ例外になる(int version)
    {
        var guard = new VersionedConfirmationGuard();

        var act = () => guard.TryConfirm(Daily, version);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // --- #341, IADR-0240 決定3: 失敗時の予約解放 -------------------------------------------------

    [Fact]
    public void 解放した版は再び受理される()
    {
        // 🔴 解放しないと、確定 API が失敗したとき**同じ版を二度と確定できなくなる**
        //（Discord は破壊的統制操作の唯一の窓口であり、詰みは Bot の再起動でしか解けない）。
        var guard = new VersionedConfirmationGuard();
        guard.TryConfirm(Daily, 2).Should().Be(ConfirmationOutcome.Accepted);

        guard.Release(Daily, 2);

        guard.TryConfirm(Daily, 2).Should().Be(ConfirmationOutcome.Accepted);
    }

    [Fact]
    public void 別の版を指定した解放は予約を消さない()
    {
        // 否定形: 取り違えた解放で他の版の予約が消えると、二重確定の穴になる。
        var guard = new VersionedConfirmationGuard();
        guard.TryConfirm(Daily, 2);

        guard.Release(Daily, 3);

        guard.TryConfirm(Daily, 2).Should().Be(ConfirmationOutcome.AlreadyConfirmed);
    }

    [Fact]
    public void 別の対象IDを指定した解放は予約を消さない()
    {
        var guard = new VersionedConfirmationGuard();
        guard.TryConfirm(Daily, 2);

        guard.Release("weekly-2026-W35", 2);

        guard.TryConfirm(Daily, 2).Should().Be(ConfirmationOutcome.AlreadyConfirmed);
    }

    [Fact]
    public void 未登録の解放は何も起こさない()
    {
        var guard = new VersionedConfirmationGuard();

        var act = () => guard.Release(Daily, 1);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 解放の版番号が正でなければ例外になる(int version)
    {
        var guard = new VersionedConfirmationGuard();

        var act = () => guard.Release(Daily, version);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
