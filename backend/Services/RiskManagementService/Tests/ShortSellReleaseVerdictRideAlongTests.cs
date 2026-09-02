using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-20, UC-06, ADR-0016 決定14（2026-08-07 確定）, #388, IADR-0281 決定1:
// **verdict が段階ゲートの承認記録と「同じ経路」に載っていること**を構造で固定する。
//
// 裁定は「利用者承認とし、段階ゲートの承認記録（FR-20 / UC-06）と同じ経路に載せる。**別記録にしない**」と定めた。
// 「別記録にしない」は**否定形の要求**であり、機能テストでは守れない——verdict 専用テーブル・専用エンドポイントを
// 足しても、値が読み書きできる限り機能テストは緑のまま通る。**構造そのものを固定する。**
public class ShortSellReleaseVerdictRideAlongTests
{
    private const string Owner = "trading-owner";

    // verdict の記録に使ってよい唯一の経路（既存の段階遷移エンドポイント）。
    private const string ApprovalRoute = "/risk-controls/stage-gate/transition";

    private static HttpClient OwnerClient(RiskWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, Owner);
        return client;
    }

    // ------------------------------------------------------------------
    // 1. 別テーブルを作っていない（DbContext の DbSet 列挙）
    // ------------------------------------------------------------------

    [Fact]
    public void verdict専用のテーブルを作っていない()
    {
        var entityTypes = typeof(RiskManagementDbContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType
                && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0].Name)
            .ToList();

        // 母集合が空だと「見つからない」が常に真になる（真空的に緑）。下限で守る。
        entityTypes.Should().HaveCountGreaterThan(5);
        entityTypes.Should().Contain(nameof(StageTransitionRow), "verdict は段階遷移の台帳へ相乗りする");
        entityTypes.Should().NotContain(
            n => n.Contains("Verdict", StringComparison.OrdinalIgnoreCase),
            "裁定は「別記録にしない」と定めた（verdict 専用テーブルを作らない）");
    }

    [Fact]
    public void verdictの材料は段階遷移の行が持つ()
    {
        // 承認者・発行時刻・承認記録 ID は**既存列**が担い、verdict 固有の 2 列だけを足す（重複して持たない）。
        var columns = typeof(StageTransitionRow).GetProperties().Select(p => p.Name).ToList();

        columns.Should().Contain(nameof(StageTransitionRow.ApprovedBy));
        columns.Should().Contain(nameof(StageTransitionRow.OccurredAtUtc));
        columns.Should().Contain(nameof(StageTransitionRow.Sequence));
        columns.Should().Contain(nameof(StageTransitionRow.ShortSellReleaseSourceFingerprint));
        columns.Should().Contain(nameof(StageTransitionRow.ShortSellReleaseStrategyId));
    }

    // ------------------------------------------------------------------
    // 2. 別 API を作っていない（エンドポイント列挙）
    // ------------------------------------------------------------------

    [Fact]
    public void verdict専用のエンドポイントを作っていない()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        // ルート表を実体化させるためにホストを起動する（CreateClient がホストを構築する）。
        using var _ = factory.CreateClient();

        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .ToList();

        routes.Should().HaveCountGreaterThan(10, "母集合が空だと否定形が真空的に成立する");
        routes.Should().Contain(ApprovalRoute, "verdict は既存の承認エンドポイントへ相乗りする");
        routes.Should().NotContain(
            r => r.Contains("verdict", StringComparison.OrdinalIgnoreCase)
                || r.Contains("short-sell-release", StringComparison.OrdinalIgnoreCase),
            "裁定は「別記録にしない」と定めた（verdict 専用エンドポイントを作らない）");
    }

    // ------------------------------------------------------------------
    // 3. 承認経路としての振る舞い
    // ------------------------------------------------------------------

    [Fact]
    public async Task verdictは承認記録として台帳に載り段階を動かさない()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var res = await client.PostAsJsonAsync(ApprovalRoute, new { approval = 1 });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await (await client.GetAsync("/risk-controls/stage-gate"))
            .Content.ReadFromJsonAsync<StatusDto>();

        // 段階は動かない（verdict は昇格ではない）。
        status!.CurrentStage.Should().Be(TradingStage.Stage0Verification);
        // 承認記録（履歴）には載る＝監査・通知の既存経路にそのまま乗る。
        status.History.Should().ContainSingle();
        status.History[0].Kind.Should().Be(StageTransitionKind.ShortSellReleaseVerdict);
        status.History[0].ApprovedBy.Should().NotBeEmpty();
        status.History[0].FromStage.Should().Be(status.History[0].ToStage);
        // 供給アダプタが未登録なので、写し取られたフィンガープリントは `none` である。
        status.ShortSellRelease.Verdict!.SourceFingerprint.Should().Be("borrow=none;margin=none");
        status.ShortSellRelease.CurrentSourceFingerprint.Should().Be("borrow=none;margin=none");
        // 戦略識別子が未供給（既定の空文字）のため、verdict は「戦略の同一性を名乗れない」として無効である。
        status.ShortSellRelease.Status.Should().Be(ShortSellReleaseVerdictStatus.StrategyChanged);
    }

    [Fact]
    public async Task verdictの記録では段階を指定できない_400()
    {
        // 「昇格のつもりが verdict だけ記録された」を黙って通さない。
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var res = await client.PostAsJsonAsync(
            ApprovalRoute, new { approval = 1, targetStage = (int)TradingStage.Stage1Simulate });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 承認種別の省略は従来どおりの段階遷移である()
    {
        // 後方互換: approval を省略した要求は、これまでどおり段階遷移として扱われる
        // （Stage 0 → 1 はバックテスト未合格で 422）。
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var res = await client.PostAsJsonAsync(
            ApprovalRoute, new { targetStage = (int)TradingStage.Stage1Simulate });

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task サービスロールはverdictを記録できない_403()
    {
        // verdict は**利用者承認**である（裁定）。生成AI・自動処理の権限（trading-service）では出せない。
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");

        var res = await client.PostAsJsonAsync(ApprovalRoute, new { approval = 1 });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------
    // 4. 承認なしに verdict は生じない（純ドメイン）
    // ------------------------------------------------------------------

    [Fact]
    public void 承認者が空ならverdictは記録されない()
    {
        var result = StageGate.RequestShortSellReleaseVerdict(
            TradingStage.Stage3ScaledLive,
            nextSequence: 1,
            new StageApproval(TradingStage.Stage3ScaledLive, "  "),
            new ShortSellReleaseAttestation("borrow=x;margin=y", "s1"),
            TradingDefaults.CreateStagePolicy(),
            DateTimeOffset.UtcNow);

        result.Accepted.Should().BeFalse();
        result.Transition.Should().BeNull();
        result.RejectionReasons.Should().Contain(StageGateCriterion.NoUserApproval);
    }

    [Fact]
    public void 台帳は最新のverdictを復元し段階の畳み込みを壊さない()
    {
        var now = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);
        var ledger = StageGateLedger.Empty(TradingStage.Stage3ScaledLive)
            .Append(new StageTransition(
                1, TradingStage.Stage3ScaledLive, TradingStage.Stage3ScaledLive,
                StageTransitionKind.ShortSellReleaseVerdict, "endazon", now, "verdict",
                new ShortSellReleaseAttestation("borrow=old;margin=old", "s1")))
            .Append(new StageTransition(
                2, TradingStage.Stage3ScaledLive, TradingStage.Stage3ScaledLive,
                StageTransitionKind.ShortSellReleaseVerdict, "endazon", now.AddDays(1), "verdict",
                new ShortSellReleaseAttestation("borrow=new;margin=new", "s2")));

        // 段階は動かない（verdict の行は ToStage == FromStage）。
        ledger.CurrentStage.Should().Be(TradingStage.Stage3ScaledLive);
        ledger.NextSequence.Should().Be(3);

        var verdict = ledger.LatestShortSellReleaseVerdict;
        verdict!.ApprovalSequence.Should().Be(2, "最新の verdict が有効である");
        verdict.SourceFingerprint.Should().Be("borrow=new;margin=new");
        verdict.StrategyId.Should().Be("s2");
        verdict.IssuedAtUtc.Should().Be(now.AddDays(1));
    }

    [Fact]
    public void 段階遷移だけの台帳にはverdictが無い()
    {
        // **否定形（最重要）**: 段階を承認しただけでは verdict は生じない（裁定が塞ごうとした状態そのもの）。
        var ledger = StageGateLedger.Empty(TradingStage.Stage0Verification)
            .Append(new StageTransition(
                1, TradingStage.Stage0Verification, TradingStage.Stage1Simulate,
                StageTransitionKind.Promotion, "endazon", DateTimeOffset.UtcNow, "昇格"));

        ledger.LatestShortSellReleaseVerdict.Should().BeNull();
    }

    // 応答 JSON の受け皿（プロパティ名は web 既定=camelCase）。
    private sealed record TransitionDto(
        int Sequence,
        TradingStage FromStage,
        TradingStage ToStage,
        StageTransitionKind Kind,
        string ApprovedBy);

    private sealed record VerdictDto(string SourceFingerprint, string StrategyId, int ApprovalSequence);

    private sealed record ReleaseDto(
        ShortSellReleaseVerdictStatus Status, VerdictDto? Verdict, string CurrentSourceFingerprint);

    private sealed record StatusDto(
        TradingStage CurrentStage, List<TransitionDto> History, ReleaseDto ShortSellRelease);
}
