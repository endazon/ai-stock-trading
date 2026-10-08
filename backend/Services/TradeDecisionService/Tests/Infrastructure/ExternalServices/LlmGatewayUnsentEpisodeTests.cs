using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, FR-09, FR-11, #1267, IADR-0517: Sent=false の連続の通知（しきい値・連続 1 回につき 1 通・回復）。
//
// 時刻は偽の TimeProvider で進める（実時間を待たない）。2026-10-07 の再現: 22 サイクル × 保有 6 = 132 件が
// 1 時間 49 分続いた。通知は Warning 1 通＋回復 1 通でなければならない（132 通にも 0 通にもしない）。
public class LlmGatewayUnsentEpisodeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 18, 11, 11, TimeSpan.Zero);

    private static readonly LlmGatewayUnsentCause Upstream =
        new(LlmGatewayUnsentKind.UpstreamError, 429, "internal は anthropic-managed へ送信可", "呼び出し先が現在利用できません");

    private const string Purpose = LlmPurposes.TradeDecisionScreening;

    // ---- T-04-012: しきい値と抑止 -----------------------------------------------------------------

    [Fact]
    public void T_04_012_しきい値の回数に達したときに1回だけ通知し_続く間は黙る()
    {
        var tracker = new LlmGatewayUnsentEpisodeTracker(threshold: 5);

        for (var i = 0; i < 4; i++)
            tracker.OnUnsent(Purpose, Upstream, T0.AddMinutes(i)).Should().BeNull($"{i + 1} 回目はしきい値未満");

        var detected = tracker.OnUnsent(Purpose, Upstream, T0.AddMinutes(4));
        detected.Should().NotBeNull("5 回目でしきい値に達する");
        detected!.ConsecutiveUnsent.Should().Be(5);
        detected.FirstUnsentAt.Should().Be(T0, "連続の最初の Sent=false の時刻");
        detected.OccurredAt.Should().Be(T0.AddMinutes(4));
        detected.Purpose.Should().Be(Purpose);
        detected.FailureKind.Should().Be("UpstreamError");
        detected.UpstreamStatusCode.Should().Be(429);
        detected.RoutingReason.Should().Be(Upstream.RoutingReason);
        detected.GatewayText.Should().Be(Upstream.GatewayText);

        for (var i = 5; i < 132; i++)
            tracker.OnUnsent(Purpose, Upstream, T0.AddMinutes(i)).Should().BeNull("連続の間は 1 回だけ");
    }

    // 境界: しきい値 1 は最初の Sent=false で通知する（構成で最も敏感にした場合）。
    [Fact]
    public void T_04_012_しきい値1は最初の送信不可で通知する()
    {
        new LlmGatewayUnsentEpisodeTracker(threshold: 1).OnUnsent(Purpose, Upstream, T0).Should().NotBeNull();
    }

    [Fact]
    public void T_04_012_しきい値0以下は構成できない()
    {
        var act = () => new LlmGatewayUnsentEpisodeTracker(threshold: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void T_04_012_既定のしきい値は5回連続()
    {
        LlmGatewayUnsentEpisodeTracker.DefaultThreshold.Should().Be(5);
        new LlmGatewayUnsentEpisodeTracker().Threshold.Should().Be(5);
    }

    // ---- T-04-013: 回復 ----------------------------------------------------------------------------

    [Fact]
    public void T_04_013_通知済みの連続の後の送信で回復を1回だけ返し_次の連続は再び通知する()
    {
        var tracker = new LlmGatewayUnsentEpisodeTracker(threshold: 5);
        for (var i = 0; i < 132; i++)
            tracker.OnUnsent(Purpose, Upstream, T0.AddSeconds(49 * i));

        var recoveredAt = T0.AddMinutes(109);
        var recovered = tracker.OnSent(recoveredAt);

        recovered.Should().NotBeNull();
        recovered!.UnsentCalls.Should().Be(132);
        recovered.FirstUnsentAt.Should().Be(T0);
        recovered.UnsentDuration.Should().Be(TimeSpan.FromMinutes(109));
        tracker.OnSent(recoveredAt.AddMinutes(1)).Should().BeNull("回復は 1 回だけ");

        for (var i = 0; i < 4; i++)
            tracker.OnUnsent(Purpose, Upstream, recoveredAt.AddHours(1)).Should().BeNull();
        tracker.OnUnsent(Purpose, Upstream, recoveredAt.AddHours(1)).Should().NotBeNull("新しい連続は改めて通知する");
    }

    // ---- T-04-014: 否定形（一過性の不調・連続でない送信不可） --------------------------------------------

    [Fact]
    public void T_04_014_しきい値未満で途切れた連続は通知も回復も出さず_数え直す()
    {
        var tracker = new LlmGatewayUnsentEpisodeTracker(threshold: 5);

        for (var i = 0; i < 4; i++)
            tracker.OnUnsent(Purpose, Upstream, T0).Should().BeNull();
        tracker.OnSent(T0).Should().BeNull("通知していない連続の終わりに回復は出さない");

        // 途切れた後は 0 から数える（前の 4 回を持ち越さない）。
        for (var i = 0; i < 4; i++)
            tracker.OnUnsent(Purpose, Upstream, T0).Should().BeNull("持ち越していれば 1 回目で通知してしまう");
    }

    [Fact]
    public void T_04_014_送信不可が無いまま送信が続いても何も出さない()
    {
        var tracker = new LlmGatewayUnsentEpisodeTracker();
        for (var i = 0; i < 10; i++)
            tracker.OnSent(T0.AddMinutes(i)).Should().BeNull();
    }

    // 交互（Sent=false と Sent=true が 1 件ずつ）は連続ではない。
    [Fact]
    public void T_04_014_送信不可と送信が交互なら通知しない()
    {
        var tracker = new LlmGatewayUnsentEpisodeTracker(threshold: 2);
        for (var i = 0; i < 20; i++)
        {
            tracker.OnUnsent(Purpose, Upstream, T0).Should().BeNull();
            tracker.OnSent(T0).Should().BeNull();
        }
    }

    // ---- T-04-015: 発行の失敗の巻き戻し ---------------------------------------------------------------

    [Fact]
    public void T_04_015_通知の発行に失敗したら巻き戻し_次の送信不可で再び通知する()
    {
        var tracker = new LlmGatewayUnsentEpisodeTracker(threshold: 2);
        tracker.OnUnsent(Purpose, Upstream, T0);
        var first = tracker.OnUnsent(Purpose, Upstream, T0.AddMinutes(1))!;

        tracker.Rollback(first);

        var retried = tracker.OnUnsent(Purpose, Upstream, T0.AddMinutes(2));
        retried.Should().NotBeNull("巻き戻さないと送信不可が続いているのに二度と通知されない");
        retried!.ConsecutiveUnsent.Should().Be(3);
        retried.FirstUnsentAt.Should().Be(T0, "連続の始まりは保つ");
    }

    [Fact]
    public void T_04_015_回復の発行に失敗したら巻き戻し_次の送信で再び回復を出す()
    {
        var tracker = new LlmGatewayUnsentEpisodeTracker(threshold: 2);
        tracker.OnUnsent(Purpose, Upstream, T0);
        tracker.OnUnsent(Purpose, Upstream, T0.AddMinutes(1));
        var recovered = tracker.OnSent(T0.AddMinutes(30))!;

        tracker.Rollback(recovered);

        var retried = tracker.OnSent(T0.AddMinutes(31));
        retried.Should().NotBeNull();
        retried!.UnsentCalls.Should().Be(2);
        retried.FirstUnsentAt.Should().Be(T0);
    }

    // ---- T-04-017: 発行（Wolverine・偽の時計） ------------------------------------------------------

    private const string ServiceName = "ai-stock-trading.trade-decision-service";

    private static Task<IHost> StartHostAsync() =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    // T-04-017: 2026-10-07 の再現。132 件の送信不可（22 サイクル × 6・5 分間隔）→ Warning 1 件、回復 → 1 件（期間 1:49）。
    [Fact]
    public async Task T_04_017_132件の送信不可は通知1件と回復1件になり_期間は偽の時計で測る()
    {
        using var host = await StartHostAsync();
        var time = new MutableTimeProvider();
        var start = time.GetUtcNow();
        var notifier = new PublishingLlmGatewayUnsentNotifier(
            host.Services.GetRequiredService<IWolverineRuntime>(), time,
            NullLogger<PublishingLlmGatewayUnsentNotifier>.Instance);

        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
        {
            for (var cycle = 0; cycle < 22; cycle++)
            {
                for (var held = 0; held < 6; held++)
                    await notifier.ReportUnsentAsync(Purpose, Upstream);
                time.Advance(TimeSpan.FromMinutes(5));
            }

            time.Advance(TimeSpan.FromMinutes(-1));
            await notifier.ReportSentAsync();
            await notifier.ReportSentAsync();
        }));

        var detected = session.Sent.MessagesOf<LlmGatewayUnsentDetected>().Should().ContainSingle().Subject;
        detected.ConsecutiveUnsent.Should().Be(5, "1 サイクル目（保有 6 件）の 5 件目で通知する");
        detected.FirstUnsentAt.Should().Be(start);
        detected.FailureKind.Should().Be("UpstreamError");

        var recovered = session.Sent.MessagesOf<LlmGatewayUnsentRecovered>().Should().ContainSingle().Subject;
        recovered.UnsentCalls.Should().Be(132);
        recovered.UnsentDuration.Should().Be(TimeSpan.FromMinutes(109));

        await host.StopAsync();
    }

    // T-04-017（否定形）: 発行に失敗したら投げ直し、状態を戻す（次の機会に再び発行する）。
    [Fact]
    public async Task T_04_017_発行に失敗したら投げ直し_次の送信不可で再発行する()
    {
        var published = new List<object>();
        var fail = true;
        var notifier = new PublishingLlmGatewayUnsentNotifier(
            runtime: null!, new MutableTimeProvider(), NullLogger<PublishingLlmGatewayUnsentNotifier>.Instance, threshold: 1)
        {
            PublishOverride = evt =>
            {
                if (fail)
                    throw new InvalidOperationException("ブローカ不達");
                published.Add(evt);
                return Task.CompletedTask;
            },
        };

        var act = () => notifier.ReportUnsentAsync(Purpose, Upstream);
        await act.Should().ThrowAsync<InvalidOperationException>();

        fail = false;
        await notifier.ReportUnsentAsync(Purpose, Upstream);

        published.Should().ContainSingle().Which.Should().BeOfType<LlmGatewayUnsentDetected>()
            .Which.ConsecutiveUnsent.Should().Be(2);
    }
}
