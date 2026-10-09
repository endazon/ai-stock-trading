using System.Globalization;
using System.Text.RegularExpressions;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Features.Notifications;
using NotificationService.Infrastructure.Steps;
using Xunit;

namespace NotificationService.Tests;

// FR-09, FR-10, UC-02, #1280, IADR-0520 決定4: 損切りライン到達の通知だけを、同じ到達（銘柄・市場・建玉方向・ライン）につき 3 分に 1 回へ絞る。
// 市場監視は前回の発行より不利な価格の到達を巡回ごとに出し直し（低いラインの S1 の行を遅らせない。T-10-2479）、発注執行・監査は
// 別のキューでそれを全部受け取る。ここで抑えるのは Discord への通知だけである。
public class StopLossNotificationSuppressionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 16, 57, 28, TimeSpan.Zero);

    private static StopLossTriggered Nvda(decimal price, DateTimeOffset at, decimal line = 233m) =>
        new(Guid.NewGuid(), "NVDA", Market.UnitedStates, TradeSide.Buy, 100, price, line, at);

    private static (StopLossTriggeredNotificationHandler Handler, RecordingNotificationSender Sender) Create(
        StopLossNotificationSuppressor? suppressor = null)
    {
        var sender = new RecordingNotificationSender();
        return (new StopLossTriggeredNotificationHandler(
            sender, suppressor ?? new StopLossNotificationSuppressor(),
            NullLogger<StopLossTriggeredNotificationHandler>.Instance), sender);
    }

    // 🔴 T-10-2481: 価格が下げ続ける（PoC の NVDA 232.90 → 232.63。市場監視は 2 件とも発行する）同じ到達の通知は 1 件だけ。
    // 3 分を過ぎて残れば念押しを 1 件送り、ラインが変われば別の到達として送る。
    [Fact]
    public async Task T_10_2481_下げ続ける同じ到達の通知は3分に1件で_ラインが変われば別に送る()
    {
        var (handler, sender) = Create();

        await handler.Handle(Nvda(232.90m, T0), CancellationToken.None);
        await handler.Handle(Nvda(232.63m, T0.AddSeconds(60)), CancellationToken.None);
        await handler.Handle(Nvda(232.40m, T0.AddSeconds(120)), CancellationToken.None);
        sender.Sent.Should().ContainSingle("同じ到達の通知は重ねない（PoC の 2 件目）")
            .Which.Content.Should().Contain("232.9");

        await handler.Handle(Nvda(232.30m, T0 + StopLossNotificationSuppressor.RenotifyAfter), CancellationToken.None);
        sender.Sent.Should().HaveCount(2, "3 分を過ぎて残る到達は念押しを送る");

        await handler.Handle(Nvda(230.00m, T0.AddSeconds(200), line: 231m), CancellationToken.None);
        sender.Sent.Should().HaveCount(3, "ラインが変われば別の到達");

        await handler.Handle(
            new StopLossTriggered(Guid.NewGuid(), "NVDA", Market.UnitedStates, TradeSide.Sell, 100, 240m, 233m, T0.AddSeconds(210)),
            CancellationToken.None);
        sender.Sent.Should().HaveCount(4, "建玉方向が違えば別の到達");
    }

    // T-10-2482: 送信に失敗した到達は記憶を戻し、例外を伝える（メッセージングの再試行で送り直す）。遅れて届いた古い到達は送らない。
    [Fact]
    public async Task T_10_2482_送信に失敗した到達は再試行で送り直し_遅れて届いた古い到達は送らない()
    {
        var suppressor = new StopLossNotificationSuppressor();
        var failing = new FailingOnceSender();
        var handler = new StopLossTriggeredNotificationHandler(
            failing, suppressor, NullLogger<StopLossTriggeredNotificationHandler>.Instance);
        var arrival = Nvda(232.90m, T0);

        var first = () => handler.Handle(arrival, CancellationToken.None);
        await first.Should().ThrowAsync<InvalidOperationException>();
        await handler.Handle(arrival, CancellationToken.None); // 再試行
        failing.Sent.Should().ContainSingle("失敗した送信は通知済みに数えない");

        await handler.Handle(Nvda(232.95m, T0.AddSeconds(-60)), CancellationToken.None);
        failing.Sent.Should().ContainSingle("前の巡回の到達が遅れて届いても重ねない");
    }

    // T-10-2483: 本番の組み立ては抑止の記憶を singleton で持つ（メッセージごとに作り直すと何も抑止しない）。
    [Fact]
    public void T_10_2483_本番の組み立ては抑止の記憶を1つだけ持つ()
    {
        using var factory = new NotificationWorkerWebApplicationFactory();

        var a = factory.Services.GetRequiredService<StopLossNotificationSuppressor>();
        var b = factory.Services.CreateScope().ServiceProvider.GetRequiredService<StopLossNotificationSuppressor>();

        a.Should().BeSameAs(b);
    }

    // T-10-2483: 通知の抑止の間隔は市場監視の出し直しの間隔（StopLossArrivalGate.RepublishAfter）と同じ。
    // 市場監視はこの試験から参照できないので、値は宣言の行（ソース）から読む。
    [Fact]
    public void T_10_2483_通知の抑止の間隔は市場監視の出し直しの間隔と同じ()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "backend", "backend.slnx")))
            root = root.Parent;
        root.Should().NotBeNull("リポジトリの最上位が見つかる");
        var source = File.ReadAllText(Path.Combine(
            root!.FullName, "backend", "Services", "MarketMonitorService", "Features", "MarketMonitor", "StopLossArrivalGate.cs"));
        var match = Regex.Match(source, @"TimeSpan RepublishAfter = TimeSpan\.FromMinutes\((\d+)\);");
        match.Success.Should().BeTrue("市場監視の出し直しの間隔の宣言を読める");

        StopLossNotificationSuppressor.RenotifyAfter.Should().Be(
            TimeSpan.FromMinutes(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)));
    }

    private sealed class FailingOnceSender : INotificationSender
    {
        private bool _failed;

        public List<NotificationMessage> Sent { get; } = [];

        public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
        {
            if (!_failed)
            {
                _failed = true;
                throw new InvalidOperationException("Discord への送信に失敗した（試験）");
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
