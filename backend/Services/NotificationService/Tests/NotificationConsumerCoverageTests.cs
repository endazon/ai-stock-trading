using System.Reflection;
using NotificationService.Features.Notifications;
using NotificationService.Infrastructure.Steps;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime;
using Xunit;

namespace NotificationService.Tests;

// FR-09, ADR-0013, IADR-0129, #354, #381, IADR-0198: 通知ハンドラが**実際に発見されている**ことの担保。
//
// 🔴 **本テストは、存在しない担保が存在すると書かれていたために追加された。**
// `NotificationService.Api/Program.cs` は「購読するのは NotificationHandlers.cs の 10 種」「10 種が
// 扱われることは Infrastructure のテストが固定する」と書いていたが、**実測でハンドラは 16 種あり、
// 種類数を固定するテストは 1 つも無かった**（#381 停止側で 16 種目を足した際に判明）。
// **緑だが検査されていない**——本プロジェクトで繰り返している事故の型そのものである。
//
// **監査側（AuditConsumerCoverageTests）と母集合の取り方が違う。** 監査は「**全イベント**を記録する」
// （FR-11）ため母集合はイベント型だが、通知は**選んだ事象だけ**を Discord へ出す——
// 全イベントに通知ハンドラを求めるのは誤りである。よって母集合は**ハンドラ型そのもの**とし、
// 「宣言されたハンドラが 1 つ残らず発見されているか」を見る。
//
// 🔴 **数を書かない。** 母集合をアセンブリから引くため、ハンドラを足しても本テストの更新は要らない
// ——**上の Program.cs のコメントが陳腐化したのと同じ壊れ方をしない。**
public class NotificationConsumerCoverageTests
{
    [Fact]
    public async Task 宣言された通知ハンドラはすべてWolverineに発見される()
    {
        // 母集合はアセンブリから引く（手書きの列挙にすると、足したときに更新を忘れる）。
        var handled = typeof(OrderExecutedNotificationHandler).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.Name.EndsWith("NotificationHandler", StringComparison.Ordinal))
            .Select(t => t.GetMethod("Handle", BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m is not null)
            .Select(m => m!.GetParameters()[0].ParameterType)
            .Distinct()
            .ToList();

        handled.Should().NotBeEmpty("ハンドラの母集合が空なら本テストは何も守っていない");

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<INotificationSender>(new RecordingNotificationSender());
                // 本番（Program.cs）と同じ発見範囲。
                opts.Discovery.IncludeAssembly(typeof(OrderExecutedNotificationHandler).Assembly);
                opts.StubAllExternalTransports();
            })
            .StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        // 扱えない型は「ハンドラ無し」を表す NoHandlerExecutor が返る（＝Discord へ何も出ない）。
        var missing = handled
            .Where(t => runtime.FindInvoker(t).GetType().Name == "NoHandlerExecutor")
            .Select(t => t.Name)
            .ToList();

        missing.Should().BeEmpty(
            "ハンドラのクラスはあるが発見されない状態（public でない・メソッド名が規約外など）は"
            + "**静かな未通知**になる。イベントは流れるが Discord には何も出ない");

        await host.StopAsync();
    }
}
