extern alias AuditWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AuditWorker::AuditService.Domain;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.Persistence;
using Xunit;

namespace ReportService.Tests;

// 🔴 T-10-1092, FR-06, FR-10, FR-11, ADR-0040 決定1, #1002, IADR-0429 決定7: **本番の Program.cs の組み立て**（名前付き
// HttpClient `audit-ledger` の鎖・供給元の登録・自動生成オーケストレータへの注入・描画）を通して、監査台帳が返す解決結果が
// 日報の「実際に適用された手法」に載ることを固定する。
//
// 差し替えるのは外界だけ: 台帳の HTTP の最下層（送り手＝監査の本物の記録の組み立て `AuditEntryFactory` で作った `AuditEntry` を
// web 既定で返す）と時計。供給元の選択・DI・生成器・描画はすべて Program.cs のまま。Program.cs から供給元の登録を外す・
// 生成器が受け取らない・照会の窓が期間ぴったりに戻る（境界際の承認の解決結果が拾えない）、のいずれでも本試験は落ちる。
public class StopLossMethodResolutionWiringTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // 2026-07-08（水）16:00 JST。日報（7/8）だけが生成境界を越えている時刻。
    private static readonly DateTimeOffset WedAfterClose = new(2026, 7, 8, 7, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    // 監査台帳の by-type。種別と半開区間 [from, to) で絞る（実物と同じ）。
    private sealed class Ledger(IReadOnlyList<AuditEntry> entries) : HttpMessageHandler
    {
        public List<string> Types { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var types = (query["types"] ?? string.Empty).Split(',');
            var from = DateTimeOffset.Parse(query["from"]!, System.Globalization.CultureInfo.InvariantCulture);
            var to = DateTimeOffset.Parse(query["to"]!, System.Globalization.CultureInfo.InvariantCulture);
            lock (Types)
                Types.AddRange(types);

            var rows = entries.Where(e => types.Contains(e.EventType) && e.OccurredAt >= from && e.OccurredAt < to).ToList();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(rows, Web), Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task T_10_1092_本番の組み立てで台帳の解決結果が日報の2行目に載る()
    {
        var noon = new DateTimeOffset(2026, 7, 8, 3, 0, 0, TimeSpan.Zero);      // 7/8 12:00 JST
        var lastSecond = new DateTimeOffset(2026, 7, 8, 14, 59, 59, TimeSpan.Zero); // 7/8 23:59:59 JST
        var s0 = Approved(StopLossExecutionMethod.BrokerStopOrder, noon);
        var s2 = Approved(StopLossExecutionMethod.NoProtectiveStop, noon);
        var boundary = Approved(StopLossExecutionMethod.NoProtectiveStop, lastSecond);
        var entries = new List<AuditEntry>
        {
            AuditEntryFactory.From(s0, Guid.NewGuid(), noon),
            AuditEntryFactory.From(s2, Guid.NewGuid(), noon),
            AuditEntryFactory.From(boundary, Guid.NewGuid(), lastSecond),
            AuditEntryFactory.From(Resolved(s0, StopLossExecutionMethod.BrokerStopOrder, StopLossMethodResolutionReason.AsSelected,
                BrokerProvider.MoomooSimulate, noon.AddSeconds(1)), Guid.NewGuid(), noon.AddSeconds(1)),
            AuditEntryFactory.From(Resolved(s2, null, StopLossMethodResolutionReason.BrokerNotMoomooSimulate,
                BrokerProvider.MoomooReal, noon.AddSeconds(1)), Guid.NewGuid(), noon.AddSeconds(1)),
            // 境界際の承認は JST 0 時を跨いで 7/9 00:00:02 JST に解決された（承認の日＝7/8 に数える）。
            AuditEntryFactory.From(Resolved(boundary, StopLossExecutionMethod.NoProtectiveStop, StopLossMethodResolutionReason.AsSelected,
                BrokerProvider.MoomooSimulate, lastSecond.AddSeconds(3)), Guid.NewGuid(), lastSecond.AddSeconds(3)),
        };
        var ledger = new Ledger(entries);

        using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Audit:BaseUrl", "http://audit-service");
            b.ConfigureServices(services =>
                services.AddHttpClient("audit-ledger").ConfigurePrimaryHttpMessageHandler(() => ledger));
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FixedClock(WedAfterClose));
            });
        });

        using (var scope = app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ReportAutoGenerator>().RunOnceAsync();
        }

        using var verify = app.Services.CreateScope();
        var daily = verify.ServiceProvider.GetRequiredService<IReportStore>().List()
            .Should().ContainSingle(r => r.Kind == ReportKind.Daily).Which;
        daily.Body.Should().Contain(
            "- **選ばれていた手法（承認時点）**: 計 3 件 — S0 ブローカー側逆指値 1 件 / S2 逆指値なしの建玉を許容 2 件");
        daily.Body.Should().Contain(
            "- **実際に適用された手法（発注執行の解決結果）**: 計 3 件 — S0 ブローカー側逆指値 1 件 / S2 逆指値なしの建玉を許容 1 件 / 見送り（実際の発注先が SIMULATE でない）1 件");
        daily.Body.Should().Contain("- **選択と実際の食い違い: 1 件** — S2 逆指値なしの建玉を許容 → 見送り（実際の発注先が SIMULATE でない）1 件"
            + "（理由: 実際の発注先が SIMULATE でないための見送り。実際の発注先: moomoo REAL）");
        daily.Body.Should().NotContain("解決結果の記録が見つからない承認", "境界際の承認の解決結果も窓で拾えている");
        daily.UnsuppliedInputs.Should().NotContain(ReportInput.StopLossMethods);
        daily.UnsuppliedInputs.Should().NotContain(ReportInput.StopLossMethodResolutions);
        ledger.Types.Should().Contain(nameof(StopLossMethodResolved), "Program.cs の供給元が台帳を引いた");
    }

    private static OrderApproved Approved(StopLossExecutionMethod method, DateTimeOffset at) => new(
        Guid.NewGuid(),
        new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 200m),
        10,
        at,
        StopLossMethod: method);

    private static StopLossMethodResolved Resolved(
        OrderApproved a, StopLossExecutionMethod? applied, StopLossMethodResolutionReason reason, BrokerProvider provider,
        DateTimeOffset at) =>
        new(a.DecisionId, a.Intent.Symbol, a.Intent.Market, a.Intent.ProductType, a.StopLossMethod, applied, reason, provider, at);
}
