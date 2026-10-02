using System.Reflection;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NotificationService.Features.Notifications;
using NotificationService.Infrastructure.Steps;
using Wolverine;
using Wolverine.Runtime;
using Xunit;

namespace NotificationService.Tests;

// 🔴 T-10-2188, NFR, FR-09, FR-11, #1111, IADR-0483 決定4: 取引判断の最中の例外の最終の失敗（TradeDecisionFailed）は監査台帳へ
// 残すだけで、**通知はしない**（裁定 2026-10-02「最小限で残す」。数は夜間の要約 §14 で見る）。独立監査 🟡3 で、通知ハンドラを
// 足しても赤になる試験が無かった（変異「通知のハンドラを足す」が生き残った）ため、ここで固定する。
//
// 母集合は 2 つの面から引く: (1) 通知サービスのアセンブリにある全型の公開メソッドの引数（ハンドラの名前の規約に依らない）、
// (2) 本番と同じ発見範囲で起こした Wolverine が TradeDecisionFailed の実行器を「ハンドラ無し」と答えること。
public class TradeDecisionFailedIsNotNotifiedTests
{
    [Fact]
    public void T_10_2188_通知サービスのどの型もTradeDecisionFailedを引数に取らない_否定形()
    {
        var assembly = typeof(OrderExecutedNotificationHandler).Assembly;
        var takers = assembly.GetTypes()
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(TradeDecisionFailed)))
            .Select(m => $"{m.DeclaringType!.FullName}.{m.Name}")
            .ToList();

        assembly.GetTypes().Should().Contain(t => t.Name.EndsWith("NotificationHandler", StringComparison.Ordinal),
            "母集合が空なら本試験は何も守っていない");
        takers.Should().BeEmpty("取引判断の最中の例外の最終の失敗は監査台帳へ残すだけで、Discord へは通知しない");
    }

    [Fact]
    public async Task T_10_2188_本番と同じ発見範囲でTradeDecisionFailedのハンドラは無い_否定形()
    {
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

        runtime.FindInvoker(typeof(OrderExecuted)).GetType().Name.Should().NotBe(
            "NoHandlerExecutor", "対照: 通知する事象にはハンドラがある（判定の仕方が常に「無し」を返さない）");
        runtime.FindInvoker(typeof(TradeDecisionFailed)).GetType().Name.Should().Be(
            "NoHandlerExecutor", "取引判断の最中の例外の最終の失敗は通知しない");

        await host.StopAsync();
    }
}
