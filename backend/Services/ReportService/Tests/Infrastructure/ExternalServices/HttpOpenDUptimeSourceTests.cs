using System.Net;
using System.Text;
using ReportService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-20, #569, INDEX 決定34, IADR-0051, IADR-0271: 権威源（リスク管理の稼働観測ログ）への
// s2s 照会と、その **fail-safe の向き**（未供給へ倒す）を fake HttpMessageHandler で検証する。
//
// 🔴 **同居する HttpPeriodFillSource（空列へ倒す）と向きが逆である。** 稼働率 0% は
// 「終日停止していた」という別の主張であり、揃えてはならない。
public class HttpOpenDUptimeSourceTests
{
    private static readonly DateOnly From = new(2026, 8, 3);
    private static readonly DateOnly To = new(2026, 8, 7);

    private static HttpOpenDUptimeSource Source(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://risk-management") },
            NullLogger<HttpOpenDUptimeSource>.Instance);

    [Fact]
    public async Task 期間を要求のクエリ文字列に載せる()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"days":[],"stage1CumulativeCountedDays":0}""");

        await Source(handler).GetUptimeAsync(From, To);

        handler.LastUri!.AbsolutePath.Should().Be("/risk-controls/session-uptime");
        handler.LastUri.Query.Should().Contain("from=2026-08-03").And.Contain("to=2026-08-07");
    }

    // **対の肯定形**: 供給された稼働率を報告書の型へ写す。
    [Fact]
    public async Task 稼働率と累計算入日数を写す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"days":[{"sessionDateEasternTime":"2026-08-03","uptimeRatio":1.0},
                     {"sessionDateEasternTime":"2026-08-04","uptimeRatio":0.6}],
             "stage1CumulativeCountedDays":41}
            """);

        var record = await Source(handler).GetUptimeAsync(From, To);

        record.Should().NotBeNull();
        record!.Days.Should().HaveCount(2);
        record.Days[0].SessionDateEasternTime.Should().Be(new DateOnly(2026, 8, 3));
        record.Days[0].UptimeRatio.Should().Be(1.0m);
        record.Days[1].UptimeRatio.Should().Be(0.6m);
        record.Stage1CumulativeCountedDays.Should().Be(41);
    }

    // 🔴 観測された取引日が 1 日も無い応答は「**空の記録**」であり、未供給ではない。
    // （描画側は Days が空なら日報で「供給されていません」と出すが、月報の分布は 0 日として描ける。
    //   ここで null へ倒すと、その区別を供給側で潰すことになる。）
    [Fact]
    public async Task 観測が無い応答は空の記録として返す()
    {
        var record = await Source(new StubHandler(HttpStatusCode.OK, """{"days":[],"stage1CumulativeCountedDays":0}"""))
            .GetUptimeAsync(From, To);

        record.Should().NotBeNull();
        record!.Days.Should().BeEmpty();
    }

    // 🔴 **否定形**: 供給不達はすべて null（未供給）へ倒す。空の記録（0 日）へ倒さない。
    [Fact]
    public async Task 非_2xx_は未供給へ倒す()
    {
        (await Source(new StubHandler(HttpStatusCode.Forbidden, "")).GetUptimeAsync(From, To)).Should().BeNull();
    }

    [Fact]
    public async Task 不正なボディは未供給へ倒す()
    {
        (await Source(new StubHandler(HttpStatusCode.OK, "not-json")).GetUptimeAsync(From, To)).Should().BeNull();
        (await Source(new StubHandler(HttpStatusCode.OK, "null")).GetUptimeAsync(From, To)).Should().BeNull();
        (await Source(new StubHandler(HttpStatusCode.OK, "{}")).GetUptimeAsync(From, To)).Should().BeNull();
    }

    [Fact]
    public async Task 例外_不達_は未供給へ倒す()
    {
        (await Source(new ThrowingHandler()).GetUptimeAsync(From, To)).Should().BeNull();
    }

    [Fact]
    public async Task タイムアウトは未供給へ倒す()
    {
        var http = new HttpClient(new DelayingHandler(TimeSpan.FromSeconds(2)))
        {
            BaseAddress = new Uri("http://risk-management"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };

        (await new HttpOpenDUptimeSource(http, NullLogger<HttpOpenDUptimeSource>.Instance)
            .GetUptimeAsync(From, To)).Should().BeNull();
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("接続できません");
    }

    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"days":[],"stage1CumulativeCountedDays":0}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
