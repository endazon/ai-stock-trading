using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-06, FR-20, #569, INDEX 決定34, 04_report-templates 日報 §1 / 月報 §6.2, IADR-0051, IADR-0271:
// 報告書サービスが期間の OpenD 稼働率を s2s 照会する経路（GET /risk-controls/session-uptime）と、
// 三者比較のために現在段階を読む経路（GET /risk-controls/stage-gate）の認可・入力検証を固定する。
public class SessionUptimeEndpointTests(RiskWorkerWebApplicationFactory factory)
    : IClassFixture<RiskWorkerWebApplicationFactory>
{
    private const string Service = "trading-service";

    private HttpClient ClientWithRoles(string roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    // 🔴 受け入れ基準: **無認可の内部エンドポイントを増やさない**。
    [Fact]
    public async Task 未認証の_session_uptime_取得は401()
    {
        var client = factory.CreateClient();

        var res = await client.GetAsync("/risk-controls/session-uptime?from=2026-08-03&to=2026-08-07");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 読み取りロールを持たない_session_uptime_取得は403()
    {
        var client = ClientWithRoles("viewer");

        var res = await client.GetAsync("/risk-controls/session-uptime?from=2026-08-03&to=2026-08-07");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // **対の肯定形**: 報告書サービスの s2s ロールで 200 が返る（観測が無ければ空の Days）。
    [Fact]
    public async Task サービスロールは_session_uptime_を取得できる()
    {
        var client = ClientWithRoles(Service);

        var res = await client.GetAsync("/risk-controls/session-uptime?from=2026-08-03&to=2026-08-07");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var view = await res.Content.ReadFromJsonAsync<SessionUptimeDto>();
        view.Should().NotBeNull();
        view!.Days.Should().NotBeNull();
    }

    // 🔴 **期間の省略・逆順は 400 とする。** 黙って空を返すと、それが「稼働率 0%」「算入 0 日」として
    // 報告書に載り得る（`/buy-in-inferences` と同じ向き。`/fills` の 200 空列とは向きが違う）。
    [Theory]
    [InlineData("/risk-controls/session-uptime")]
    [InlineData("/risk-controls/session-uptime?from=2026-08-03")]
    [InlineData("/risk-controls/session-uptime?to=2026-08-07")]
    [InlineData("/risk-controls/session-uptime?from=2026-08-07&to=2026-08-03")]
    public async Task 期間が不正な_session_uptime_取得は400(string path)
    {
        var client = ClientWithRoles(Service);

        var res = await client.GetAsync(path);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // FR-06, FR-15, #569, IADR-0271: 月報 §5 の三者比較は「その段に到達しているか」を要する。
    // 読み取り専用の現況照会を s2s へ開いたことを固定する（遷移は OwnerOnly のまま）。
    [Fact]
    public async Task サービスロールは段階ゲートの現況を取得できる()
    {
        var client = ClientWithRoles(Service);

        var res = await client.GetAsync("/risk-controls/stage-gate");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 🔴 **対の否定形**: 書き込み（段階遷移）は s2s へ開いていない（最小権限・IADR-0051）。
    [Fact]
    public async Task サービスロールは段階遷移できない_403()
    {
        var client = ClientWithRoles(Service);

        var res = await client.PostAsJsonAsync("/risk-controls/stage-gate/transition", new { targetStage = 1 });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private sealed record SessionUptimeDto(List<SessionUptimeDayDto>? Days, int Stage1CumulativeCountedDays);

    private sealed record SessionUptimeDayDto(DateOnly SessionDateEasternTime, decimal UptimeRatio);
}
