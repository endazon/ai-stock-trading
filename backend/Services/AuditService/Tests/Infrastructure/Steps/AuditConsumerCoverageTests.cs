using AuditService.Common.Abstractions;
using AuditService.Features.AuditEvents;
using AuditService.Infrastructure.Persistence;
using AuditService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime;
using Xunit;

namespace AuditService.Tests;

// FR-11, #80: 「全イベントの時系列記録」の担保。Shared.Contracts.Events の全イベントに対応する監査ハンドラが
// 存在することを検証し、新規イベント追加時の監査購読の追随漏れを CI で検知する。
//
// ADR-0013, IADR-0129, #354: 判定根拠を「`IConsumer<T>` を実装する型がある」（型の形）から
// 「**Wolverine のホストが実際にその型を扱える**」（実行時の発見結果）へ移した。
// Wolverine はハンドラを明示登録せずアセンブリ走査で発見するため、型の形だけを見ると
// 「クラスはあるが発見されない」（public でない・メソッド名が規約外など）を見逃す。
// 本テストは本番と同じ発見範囲でホストを起こし、扱えない型が 1 つも無いことを確かめる。
public class AuditConsumerCoverageTests
{
    [Fact]
    public async Task 全ドメインイベントに対応する監査コンシューマが存在する()
    {
        // Shared.Contracts.Events 名前空間の全イベント型（record のみ）。母集合は EventTypeDiscovery で単一化し、
        // 後方互換契約テスト（EventBackwardCompatibilityTests）と同一の対象を共有する（IADR-0079。片方だけ条件を
        // 変えて対象がサイレントに乖離するのを防ぐ）。static 補助クラス等は record 判定で自然に除外される。
        var eventTypes = EventTypeDiscovery.GetEventTypes();

        eventTypes.Should().NotBeEmpty();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton<IAuditEventStore, InMemoryAuditEventStore>();
                // 本番（Program.cs）と同じ発見範囲。
                opts.Discovery.IncludeAssembly(typeof(PriceMovementDetectedAuditHandler).Assembly);
                opts.StubAllExternalTransports();
            })
            .StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        // 扱えない型は「ハンドラ無し」を表す NoHandlerExecutor が返る（＝監査台帳へ記録されない）。
        var missing = eventTypes
            .Where(e => runtime.FindInvoker(e).GetType().Name == "NoHandlerExecutor")
            .Select(e => e.Name)
            .ToList();

        missing.Should().BeEmpty(
            "全ドメインイベントは監査台帳へ記録する（FR-11）。未購読のイベントは AuditEventHandlers にハンドラを追加すること");

        await host.StopAsync();
    }
}
