using System.Reflection;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Infrastructure.Tests;

// FR-05, FR-10, FR-20, #331, IADR-0211: 「拒否」（証券会社が受理しなかった状態＝OrderExecuted の Rejected）と
// 「見送り」（OrderDispatchForgone）が、**事前拒否（リスク管理・OrderRejected）の統制違反観測へ混入しない**ことの
// 構造テスト（issue #331 受け入れ基準 3 の否定形）。
//
// 統制違反観測（IControlViolationObservationStore）は段階昇格ゲートの「統制違反 0 件」（クラス C 限定・
// planning#58）の集計元である。証券会社拒否・接続断の見送りは AI の行動と無関係な事象であり、
// 混入すると昇格ゲートと監査（FR-11）の意味が壊れる。
public class RejectionSeparationTests
{
    private static IEnumerable<Type> HandlersOf(Type messageType) =>
        typeof(TradeDecisionMadeHandler).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Any(m => m.Name == "Handle" && m.GetParameters().FirstOrDefault()?.ParameterType == messageType));

    [Theory]
    [InlineData(typeof(OrderExecuted))]
    [InlineData(typeof(OrderDispatchForgone))]
    public void 証券会社拒否と見送りのハンドラは統制違反観測ストアへ依存しない_構造(Type messageType)
    {
        var handlers = HandlersOf(messageType).ToList();

        foreach (var handler in handlers)
        {
            var dependencies = handler.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Select(p => p.ParameterType);

            dependencies.Should().NotContain(typeof(IControlViolationObservationStore),
                $"{handler.Name} が統制違反観測へ書けると、証券会社拒否・見送りが「統制違反」へ混入し得る"
                + "（別状態・別集計。FR-05 planning#60 裁定）");
        }
    }

    [Fact]
    public void 事前拒否の経路だけが統制違反観測ストアへ依存する_正の対照()
    {
        // 上の否定形が「そもそも誰も依存していない」ために空振りで緑になっていないことの対照。
        // 事前拒否（TradeDecisionMade → スクリーニング → OrderRejected）は観測を記録する側である。
        typeof(TradeDecisionMadeHandler).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .Should().Contain(typeof(IControlViolationObservationStore));
    }

    [Fact]
    public void 見送りはリスク管理では購読しない_発注経路を作らない()
    {
        // 見送り（キューイングしない）を購読して再発注する経路がリスク管理側に生えると、
        // 「見送りは破棄・再発注は次の取引判断からのみ」（IADR-0211 決定3）が破れる。
        HandlersOf(typeof(OrderDispatchForgone)).Should().BeEmpty(
            "見送りの記録は監査サービス・通知は通知サービスが担う（リスク管理は関与しない）");
    }
}
