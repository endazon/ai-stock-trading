using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.SecondFactor;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using Xunit;

namespace RiskManagementService.Tests;

// NFR-06, FR-14, FR-19, FR-20, FR-10, #1552, IADR-0615: **受理される要求**で、段階の昇格・GFV 停止の解除・乖離の取り込みが
// Enforce（段 2）でも**正しいコードなら通る**こと、コードが無い・違うなら 403 で何も変えないことを固定する。
//
// #1542 の試験は「正しいコードで通る」を kill switch の解除でしか見ていなかった（他の 3 操作は受理不能の要求で
// 「コードを消費しない」側だけ）。段 2 へ上げる前の前提として、受理される状態を作る土台（Arrange）をここに置く。
// 状態は専用の Factory（隔離 InMemory DB）に仕込む（共有すると他の試験へ漏れる）。
public class OwnerSecondFactorAcceptedRequestTests
{
    // RFC 6238 付録 B の SHA1 の公開の試験値の種を実行時に base32 へ組み立てる（本物の秘密ではない。
    // 文字列の定数で書くと秘密の走査に掛かる）。OwnerSecondFactorWiringTests と同じ。
    private static readonly string Secret = Totp.EncodeBase32(System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("1234567890", 2))));

    private static RiskWorkerWebApplicationFactory Enforce() => new()
    {
        HostSettings = new Dictionary<string, string?>
        {
            [$"{OwnerSecondFactorOptions.SectionName}:Mode"] = "Enforce",
            [$"{OwnerSecondFactorOptions.SectionName}:Secret"] = Secret,
        },
    };

    private static HttpClient Owner(RiskWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    private static string CurrentCode()
    {
        Totp.TryDecodeBase32(Secret, out var key);
        return Totp.Compute(key, Totp.StepOf(DateTimeOffset.UtcNow));
    }

    // 前後 1 段のどれとも違う 6 桁（固定の値だと偶然一致し得る）。
    private static string WrongCode()
    {
        Totp.TryDecodeBase32(Secret, out var key);
        var step = Totp.StepOf(DateTimeOffset.UtcNow);
        var accepted = new[] { step - 1, step, step + 1 }.Select(s => Totp.Compute(key, s)).ToHashSet();
        return Enumerable.Range(0, 1_000_000).Select(n => n.ToString("D6")).First(c => !accepted.Contains(c));
    }

    // ---- 受理される要求の土台（Arrange） ----

    // 段階の昇格（Stage 0 → Stage 1）: バックテスト合格だけが基準（StageTransitionApproverTests と同じ仕込み）。
    private static void ArrangePromotable(RiskWorkerWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IStagePerformanceStore>().Save(new StagePerformance { BacktestPassed = true });
    }

    private static object PromoteBody(string? code) =>
        new { TargetStage = (int)TradingStage.Stage1Simulate, SecondFactorCode = code };

    private static async Task<TradingStage> CurrentStageAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<StageGateDto>("/risk-controls/stage-gate"))!.CurrentStage;

    private sealed record StageGateDto(TradingStage CurrentStage);

    // GFV 停止の解除: 違反の記録が 1 件ある（GoodFaithViolationClearEndpointTests と同じ仕込み）。
    private static void ArrangeViolation(RiskWorkerWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IGoodFaithViolationStore>().Append(new GoodFaithViolationRecord(
            Guid.NewGuid(), "ord-1", Guid.NewGuid(), "AAPL", Market.UnitedStates,
            PurchaseAmountInBase: 1000m, SettledCashInBase: 0m,
            OccurredOn: new DateOnly(2026, 8, 8),
            ExecutedAt: DateTimeOffset.UtcNow, RecordedAt: DateTimeOffset.UtcNow));
    }

    private static object ClearBody(string? code) => new { Reason = "決済済み資金の判定を修正した", SecondFactorCode = code };

    private static int ActiveViolations(RiskWorkerWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IGoodFaithViolationStore>().GetTally().Count;
    }

    // 乖離の取り込み: 台帳に建玉があり、ブローカーの観測では 0 株（報告済みの乖離。PositionDriftAdoptionEndpointTests と同じ仕込み）。
    private static void ArrangeDrift(RiskWorkerWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var ledger = sp.GetRequiredService<IPortfolioLedgerStore>();
        var decisionId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        ledger.AppendApproval(
            decisionId,
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
                10, 100m, PositionEffect.Open, StopLossPrice: 95m),
            at);
        ledger.AppendFill(decisionId, $"open-{decisionId:N}", 10, 100m, at);

        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        for (var i = 0; i < 2; i++)
        {
            using var inner = factory.Services.CreateScope();
            var isp = inner.ServiceProvider;
            isp.GetRequiredService<IBrokerPositionObservationStore>().Record([], observedAt.AddSeconds(i));
            var drifts = PositionDriftDetector.Detect(
                PortfolioProjection.ProjectOpenPositions(isp.GetRequiredService<IPortfolioLedgerStore>().GetFills()), []);
            isp.GetRequiredService<PositionDriftTracker>().ShouldReport(drifts);
        }
    }

    private static object AdoptBody(string? code) =>
        new { Symbol = "AAPL", Market = (int)Market.UnitedStates, Reason = "証券会社のアプリで全株を売却した", SecondFactorCode = code };

    private static int LedgerQuantity(RiskWorkerWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return PortfolioProjection
            .ProjectOpenPositions(scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>().GetFills())
            .Where(p => p.Symbol == "AAPL")
            .Sum(p => p.Quantity);
    }

    // ---- 段階の昇格 ----

    [Fact]
    public async Task 昇格はEnforceでも正しいコードなら受理する()
    {
        using var factory = Enforce();
        ArrangePromotable(factory);
        var client = Owner(factory);

        (await client.PostAsJsonAsync("/risk-controls/stage-gate/transition", PromoteBody(CurrentCode())))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await CurrentStageAsync(client)).Should().Be(TradingStage.Stage1Simulate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 受理できる昇格でもコードが無い_違うなら403で段は変わらない(bool wrong)
    {
        using var factory = Enforce();
        ArrangePromotable(factory);
        var client = Owner(factory);

        (await client.PostAsJsonAsync("/risk-controls/stage-gate/transition", PromoteBody(wrong ? WrongCode() : null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CurrentStageAsync(client)).Should().Be(TradingStage.Stage0Verification, "拒んだら何も変えない");
    }

    // ---- GFV 停止の解除 ----

    [Fact]
    public async Task GFVの解除はEnforceでも正しいコードなら解除する()
    {
        using var factory = Enforce();
        ArrangeViolation(factory);
        var client = Owner(factory);

        (await client.PostAsJsonAsync("/risk-controls/good-faith-violations/clear", ClearBody(CurrentCode())))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        ActiveViolations(factory).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 解除できるGFVでもコードが無い_違うなら403で記録は残る(bool wrong)
    {
        using var factory = Enforce();
        ArrangeViolation(factory);
        var client = Owner(factory);

        (await client.PostAsJsonAsync("/risk-controls/good-faith-violations/clear", ClearBody(wrong ? WrongCode() : null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        ActiveViolations(factory).Should().Be(1, "拒んだら何も変えない");
    }

    // ---- 乖離の取り込み ----

    [Fact]
    public async Task 乖離の取り込みはEnforceでも正しいコードなら台帳を合わせる()
    {
        using var factory = Enforce();
        ArrangeDrift(factory);
        var client = Owner(factory);

        (await client.PostAsJsonAsync("/risk-controls/position-drift/adopt", AdoptBody(CurrentCode())))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        LedgerQuantity(factory).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 取り込める乖離でもコードが無い_違うなら403で台帳は動かない(bool wrong)
    {
        using var factory = Enforce();
        ArrangeDrift(factory);
        var client = Owner(factory);

        (await client.PostAsJsonAsync("/risk-controls/position-drift/adopt", AdoptBody(wrong ? WrongCode() : null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        LedgerQuantity(factory).Should().Be(10, "拒んだら何も変えない");
    }
}
