using System.Net;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.ExternalServices;
using Xunit;

namespace ReportService.Tests;

// FR-06, NFR-05, #840, IADR-0352 決定 1・2: 依存先への鎖の最外に挿す門＋観測を検証する。
//   門  : サービストークンを取得できないなら**送信しない**（認証なしの送信で代替しない）。
//   観測: 失敗を「待てば直り得る（一過性）」と「待っても直らない（恒常）」に分けて記録する。
public class ReportDependencyHandlerTests
{
    private sealed class StubTokenProvider(string? token) : IServiceAccessTokenProvider
    {
        public int Calls { get; private set; }

        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(token);
        }
    }

    // 上流（一次ハンドラ）。呼ばれた回数と受け取った Authorization を記録する。
    private sealed class Upstream(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(respond(request));
        }
    }

    private static HttpClient NewClient(
        ReportDependencyProbe probe,
        IServiceAccessTokenProvider? tokenProvider,
        Upstream upstream,
        bool timeoutIsTransient = true) =>
        new(new ReportDependencyHandler(
            probe, tokenProvider, "risk-ledger", timeoutIsTransient, NullLogger<ReportDependencyHandler>.Instance)
        {
            InnerHandler = upstream,
        })
        {
            BaseAddress = new Uri("http://risk-management-service"),
        };

    private static Upstream Ok() => new(_ => new HttpResponseMessage(HttpStatusCode.OK));

    // ---- 門 -----------------------------------------------------------------------------------

    [Fact]
    public async Task トークンを取得できなければ_上流へ送信せず例外で未供給へ倒す()
    {
        var probe = new ReportDependencyProbe();
        var upstream = Ok();
        using var http = NewClient(probe, new StubTokenProvider(null), upstream);
        using var observation = probe.Begin();
        observation.Enter(ReportInput.OpenPositions);

        var act = () => http.GetAsync("/risk-controls/open-positions");

        (await act.Should().ThrowAsync<ServiceTokenUnavailableException>())
            .Which.Dependency.Should().Be("risk-ledger");
        // 🔴 否定形: 認証なしの要求は 1 件も出ていない（本変更前は Authorization 無しで送っていた）。
        upstream.Calls.Should().Be(0);
        observation.Failures.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Input = (ReportInput?)ReportInput.OpenPositions,
            Dependency = "risk-ledger",
            Kind = ReportDependencyFailureKind.ServiceTokenUnavailable,
            Transient = true,
        });
    }

    [Fact]
    public async Task トークン取得不能の例外は_通信失敗として受ける呼び出し元でも捕まる()
    {
        // 供給元は Exception で受けているが、HttpRequestException で受ける実装が混ざっても同じ縮退へ倒れること。
        using var http = NewClient(new ReportDependencyProbe(), new StubTokenProvider(""), Ok());

        await ((Func<Task>)(() => http.GetAsync("/x"))).Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task トークンが取れれば_Bearer_を付けて送信し_失敗は記録しない()
    {
        var probe = new ReportDependencyProbe();
        var upstream = Ok();
        using var http = NewClient(probe, new StubTokenProvider("T"), upstream);
        using var observation = probe.Begin();

        using var response = await http.GetAsync("/risk-controls/open-positions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        upstream.LastAuthorization.Should().Be("Bearer T");
        observation.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task 資格情報が未整備なら_門は素通しで_従来どおり認証なしで送る()
    {
        // 未整備は「取得に失敗した」ではない（dev・単体実行の構成）。止めると認証なしで動く構成が全滅する。
        foreach (var provider in new IServiceAccessTokenProvider?[] { null, NoServiceAccessTokenProvider.Instance })
        {
            var upstream = Ok();
            using var http = NewClient(new ReportDependencyProbe(), provider, upstream);

            using var response = await http.GetAsync("/risk-controls/open-positions");

            upstream.Calls.Should().Be(1);
            upstream.LastAuthorization.Should().BeNull();
        }
    }

    [Fact]
    public async Task 既に_Authorization_が付いていれば_トークンを取りに行かない()
    {
        var tokens = new StubTokenProvider(null);
        var upstream = Ok();
        using var http = NewClient(new ReportDependencyProbe(), tokens, upstream);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/x");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "given");

        using var response = await http.SendAsync(request);

        tokens.Calls.Should().Be(0);
        upstream.LastAuthorization.Should().Be("Bearer given");
    }

    // ---- 観測（分類） ---------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    // #866: **401 は一過性**。上流の JwtBearer 設定取得器（IdentityModel 8.0.1）は起動時の取得失敗に
    // バックオフを持ち、Keycloak が戻ってからも 24.6 秒は正しいトークンで 401 を返す（監査の実測）。
    [InlineData(HttpStatusCode.Unauthorized, true)]
    // 🔴 403 は恒常のまま。ロール未付与などの設定誤りは待っても変わらない。
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task 非_2xx_は状態で一過性と恒常に分ける(HttpStatusCode status, bool transient)
    {
        var probe = new ReportDependencyProbe();
        using var http = NewClient(probe, new StubTokenProvider("T"), new Upstream(_ => new HttpResponseMessage(status)));
        using var observation = probe.Begin();
        observation.Enter(ReportInput.CurrentStage);

        using var response = await http.GetAsync("/risk-controls/stage-gate");

        // 応答はそのまま呼び出し元へ返す（供給元の縮退とログを変えない）。
        response.StatusCode.Should().Be(status);
        var failure = observation.Failures.Should().ContainSingle().Subject;
        failure.Kind.Should().Be(ReportDependencyFailureKind.HttpStatus);
        failure.Transient.Should().Be(transient);
        failure.Detail.Should().Be(((int)status).ToString());
        observation.HasTransientFailure(ReportInput.CurrentStage).Should().Be(transient);
        // 別の入力の失敗としては数えない。
        observation.HasFailure(ReportInput.OpenPositions).Should().BeFalse();
    }

    [Fact]
    public async Task 接続できない失敗は一過性として記録し_例外はそのまま返す()
    {
        var probe = new ReportDependencyProbe();
        using var http = NewClient(
            probe, new StubTokenProvider("T"), new Upstream(_ => throw new HttpRequestException("connection refused")));
        using var observation = probe.Begin();

        await ((Func<Task>)(() => http.GetAsync("/x"))).Should().ThrowAsync<HttpRequestException>();

        observation.Failures.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Kind = ReportDependencyFailureKind.Unreachable, Transient = true });
    }

    [Theory]
    [InlineData(true)]
    // LLM はタイムアウトを一過性に数えない（モデルの所要時間であり、繰り返せば費用だけが増える）。
    [InlineData(false)]
    public async Task タイムアウトの扱いは依存先ごとに決める(bool timeoutIsTransient)
    {
        var probe = new ReportDependencyProbe();
        using var http = NewClient(
            probe, new StubTokenProvider("T"), new Upstream(_ => throw new TaskCanceledException("timeout")),
            timeoutIsTransient);
        using var observation = probe.Begin();

        await ((Func<Task>)(() => http.GetAsync("/x"))).Should().ThrowAsync<OperationCanceledException>();

        observation.Failures.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Kind = ReportDependencyFailureKind.Timeout, Transient = timeoutIsTransient });
    }

    // ---- 観測（開閉） ---------------------------------------------------------------------------

    [Fact]
    public async Task 観測が開かれていない経路では_何も記録せず素通しする()
    {
        // 手動の API・確定後の KB 保存など、生成器の外から同じ HttpClient が使われても落ちない。
        var probe = new ReportDependencyProbe();
        using var http = NewClient(probe, new StubTokenProvider("T"), new Upstream(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        using var response = await http.GetAsync("/x");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task 閉じた観測へは_後から記録されない()
    {
        var probe = new ReportDependencyProbe();
        using var http = NewClient(probe, new StubTokenProvider("T"), new Upstream(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var first = probe.Begin();
        first.Dispose();

        using var response = await http.GetAsync("/x");

        first.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task 並行する別の流れの失敗は_自分の観測に混ざらない()
    {
        // 生成器の観測は AsyncLocal で受け渡す。手動 API など別の流れの失敗を拾って見送りを誤判定しない。
        var probe = new ReportDependencyProbe();
        using var failing = NewClient(probe, new StubTokenProvider("T"), new Upstream(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        async Task<int> GenerationFlowAsync()
        {
            using var mine = probe.Begin();
            await Task.Delay(50);
            return mine.Failures.Count;
        }

        var generation = Task.Run(GenerationFlowAsync);
        using var response = await Task.Run(() => failing.GetAsync("/x"));

        (await generation).Should().Be(0);
    }
}
