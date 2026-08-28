using CostControlService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CostControlService.Tests;

// FR-17, IADR-0063 決定 4/5: バージョン付き前提条件のキャッシュ・イベント無効化・TTL 失効・フェイルセーフ順序を検証する。
//
// NFR, #526, IADR-0264 決定 1: 旧 ConfigurationService.Client.Tests から移した（共有クライアントの廃止に伴い、
// 実装が呼び出し元ごとに複製されたためテストも呼び出し元ごとに持つ）。**移送時にテスト本文は変えていない**
// （namespace 宣言と using の書き換えのみ）。
public class CachedAssumptionsProviderTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private static VersionedAssumptions Versioned(int version, decimal totalLimit) =>
        new(TradingAssumptionsDefaults.Create() with
        {
            CostLimits = new MonthlyCostLimits(totalLimit, Llm: 1_000m, Infrastructure: 500m, Data: 0m),
        }, version);

    private static (CachedAssumptionsProvider Provider, FakeAssumptionsSource Source, MutableTimeProvider Time) Build(
        VersionedAssumptions? initial)
    {
        var source = new FakeAssumptionsSource { Next = initial };
        var time = new MutableTimeProvider();
        var provider = new CachedAssumptionsProvider(
            source, time, NullLogger<CachedAssumptionsProvider>.Instance, Ttl);
        return (provider, source, time);
    }

    [Fact]
    public async Task 初回は取得して返す()
    {
        var (provider, source, _) = Build(Versioned(3, 12_000m));

        var current = await provider.GetCurrentAsync();

        current.Version.Should().Be(3);
        current.IsResolved.Should().BeTrue();
        current.Assumptions.CostLimits.Total.Should().Be(12_000m);
        source.FetchCount.Should().Be(1);
    }

    [Fact]
    public async Task 二回目はキャッシュから返す()
    {
        var (provider, source, _) = Build(Versioned(3, 12_000m));

        await provider.GetCurrentAsync();
        var second = await provider.GetCurrentAsync();

        second.Version.Should().Be(3);
        source.FetchCount.Should().Be(1, "TTL 内かつ無効化されていなければ HTTP を叩かない");
    }

    // #139 の受け入れ基準「版が上がったときに追随する」＝ AssumptionsChanged による無効化。
    [Fact]
    public async Task 無効化後は再取得して新しい版を返す()
    {
        var (provider, source, _) = Build(Versioned(3, 12_000m));
        await provider.GetCurrentAsync();

        source.Next = Versioned(4, 5_000m);
        provider.Invalidate();
        var after = await provider.GetCurrentAsync();

        after.Version.Should().Be(4);
        after.Assumptions.CostLimits.Total.Should().Be(5_000m);
        source.FetchCount.Should().Be(2);
    }

    // 取得の最中に版が上がった場合、その取得結果は既に古い。無効化を「取得成功で解除」すると取りこぼして
    // TTL 経過まで古い版を掴み続けるため、取得開始時点のチケットで失効を判定する。
    [Fact]
    public async Task 取得中に届いた変更は取りこぼさない()
    {
        var (provider, source, _) = Build(Versioned(3, 12_000m));

        // v3 を取得している最中に利用者が v4 へ変更した（AssumptionsChanged 到着）。
        source.OnFetching = () => provider.Invalidate();
        (await provider.GetCurrentAsync()).Version.Should().Be(3);

        source.OnFetching = null;
        source.Next = Versioned(4, 5_000m);
        var next = await provider.GetCurrentAsync();

        next.Version.Should().Be(4, "取得中に届いた無効化を取得成功で消してはならない");
        source.FetchCount.Should().Be(2);
    }

    // IADR-0063 決定 4: イベント取りこぼしに備え TTL でも失効させる。
    [Fact]
    public async Task TTL経過で再取得する()
    {
        var (provider, source, time) = Build(Versioned(3, 12_000m));
        await provider.GetCurrentAsync();

        time.Advance(Ttl - TimeSpan.FromSeconds(1));
        await provider.GetCurrentAsync();
        source.FetchCount.Should().Be(1, "TTL 内はキャッシュのまま");

        time.Advance(TimeSpan.FromSeconds(2));
        source.Next = Versioned(9, 3_000m);
        var after = await provider.GetCurrentAsync();

        after.Version.Should().Be(9);
        source.FetchCount.Should().Be(2);
    }

    // IADR-0063 決定 5: 一度も取得できていなければ既定値（現行挙動）へ倒れる。例外は出さない。
    [Fact]
    public async Task 一度も取得できなければ既定へ倒れる()
    {
        var (provider, _, _) = Build(initial: null);

        var current = await provider.GetCurrentAsync();

        current.Version.Should().Be(VersionedAssumptions.UnresolvedVersion);
        current.IsResolved.Should().BeFalse();
        current.Assumptions.Should().Be(TradingAssumptionsDefaults.Create());
        current.Assumptions.CostLimits.Total.Should().Be(20_000m);
    }

    // IADR-0063 決定 5: 取得済みなら既定へ戻さず last known good を返す（既定へ戻すと利用者が絞った上限が緩む側へ倒れる）。
    [Fact]
    public async Task 取得失敗時は既定ではなく最後の成功値を返す()
    {
        var (provider, source, time) = Build(Versioned(4, 5_000m));
        await provider.GetCurrentAsync();

        source.Next = null; // 設定サービス障害
        time.Advance(Ttl * 10);
        var stale = await provider.GetCurrentAsync();

        stale.Version.Should().Be(4);
        stale.Assumptions.CostLimits.Total.Should().Be(5_000m, "既定の 20,000 へ戻すと利用者が絞った上限が緩む");
        source.FetchCount.Should().Be(2, "失効しているので再取得は試みる");
    }

    // 復旧したら次の参照で最新へ戻る。
    [Fact]
    public async Task 障害復旧後は最新版へ戻る()
    {
        var (provider, source, time) = Build(Versioned(4, 5_000m));
        await provider.GetCurrentAsync();

        source.Next = null;
        time.Advance(Ttl * 2);
        (await provider.GetCurrentAsync()).Version.Should().Be(4);

        source.Next = Versioned(5, 6_000m);
        time.Advance(Ttl * 2);
        (await provider.GetCurrentAsync()).Version.Should().Be(5);
    }
}
