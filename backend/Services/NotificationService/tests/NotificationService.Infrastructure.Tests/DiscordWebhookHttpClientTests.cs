using System.Net;
using AiStockTrading.Notification.Application.State;
using AiStockTrading.Notification.Infrastructure.Composable.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiStockTrading.Notification.Infrastructure.Tests;

// #289, FR-09, NFR（セキュリティ）: Discord Webhook URL は「知っていれば認証なしで投稿できる」＝それ自体が資格情報。
// IHttpClientFactory の既定ログはリクエスト URI を平文で出力し、otel 経由で Loki に蓄積されるため、
// Webhook 送信専用クライアントでは URL がログへ出ないことを回帰テストで固定する。
public class DiscordWebhookHttpClientTests
{
    // 実 URL と同じ形（/api/webhooks/<id>/<token>）のダミー。id を 18 桁数字にすると
    // gitleaks の discord-client-id 規則が誤検知するため、非数値の値を使う。
    private const string WebhookUrl = "https://discord.com/api/webhooks/wh-test-id/wh-test-token";

    private static NotificationMessage Message() => new("取引実行", "約定 Filled 10@1050", NotificationSeverity.Info);

    [Fact]
    public async Task Webhook_送信のログに_URL_が平文で現れない()
    {
        var recorder = new RecordingLoggerProvider();
        using var provider = BuildProvider(recorder, HttpStatusCode.NoContent);

        await CreateSender(provider).SendAsync(Message());

        // 「ログを消す」対策ではないことを固定する。宛先ホストと HTTP ステータスは残る。
        recorder.Entries.Should().Contain(e =>
            e.Contains("https://discord.com/***", StringComparison.Ordinal) && e.Contains("204", StringComparison.Ordinal));
        recorder.Entries.Should().NotContain(e => e.Contains(WebhookUrl, StringComparison.Ordinal));
        recorder.Entries.Should().NotContain(e => e.Contains("wh-test-token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 送信失敗時はステータスがログに残る()
    {
        var recorder = new RecordingLoggerProvider();
        using var provider = BuildProvider(recorder, HttpStatusCode.TooManyRequests);

        var act = () => CreateSender(provider).SendAsync(Message());

        await act.Should().ThrowAsync<InvalidOperationException>();
        recorder.Entries.Should().Contain(e => e.Contains("429", StringComparison.Ordinal));
        recorder.Entries.Should().NotContain(e => e.Contains(WebhookUrl, StringComparison.Ordinal));
    }

    // 応答が返らない経路（接続拒否・DNS 失敗・タイムアウト）は IHttpClientLogger.LogRequestFailed を通る。
    // ここでも URL が出ず、切り分けに要る情報（宛先ホスト・例外）は残ることを固定する。
    [Fact]
    public async Task 送信が例外で失敗しても_URL_は現れず失敗が記録される()
    {
        var recorder = new RecordingLoggerProvider();
        var services = NewServices(recorder);
        services.AddDiscordWebhookHttpClient();
        services.AddHttpClient(DiscordWebhookHttpClientExtensions.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new ThrowingHandler());
        using var provider = services.BuildServiceProvider();

        var act = () => CreateSender(provider).SendAsync(Message());

        await act.Should().ThrowAsync<HttpRequestException>();
        recorder.Entries.Should().Contain(e =>
            e.Contains("HTTP 送信失敗", StringComparison.Ordinal)
            && e.Contains("https://discord.com/***", StringComparison.Ordinal));
        recorder.Entries.Should().NotContain(e => e.Contains(WebhookUrl, StringComparison.Ordinal));
        recorder.Entries.Should().NotContain(e => e.Contains("wh-test-token", StringComparison.Ordinal));
    }

    // 抑止は Webhook クライアントに閉じる。他の名前付きクライアント（risk-kill-switch 等）の既定ログは変えない。
    [Fact]
    public async Task 他の名前付きクライアントの既定リクエストログは変わらない()
    {
        const string OtherUrl = "https://risk.example.invalid/risk-controls/kill-switch";
        var recorder = new RecordingLoggerProvider();
        var services = NewServices(recorder);
        services.AddDiscordWebhookHttpClient();
        services.AddHttpClient("risk-kill-switch")
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(HttpStatusCode.OK));
        using var provider = services.BuildServiceProvider();

        using var response = await provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient("risk-kill-switch").GetAsync(OtherUrl);

        recorder.Entries.Should().Contain(e => e.Contains(OtherUrl, StringComparison.Ordinal));
    }

    private static ServiceCollection NewServices(RecordingLoggerProvider recorder)
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(recorder);
        });
        return services;
    }

    private static ServiceProvider BuildProvider(RecordingLoggerProvider recorder, HttpStatusCode status)
    {
        var services = NewServices(recorder);
        services.AddDiscordWebhookHttpClient();
        // 実ネットワークを使わないよう、本番と同じ名前付きクライアントの一次ハンドラだけ差し替える。
        services.AddHttpClient(DiscordWebhookHttpClientExtensions.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(status));
        return services.BuildServiceProvider();
    }

    private static DiscordWebhookNotificationSender CreateSender(IServiceProvider provider) =>
        new(provider.GetRequiredService<IHttpClientFactory>().CreateClient(DiscordWebhookHttpClientExtensions.ClientName),
            WebhookUrl,
            provider.GetRequiredService<ILogger<DiscordWebhookNotificationSender>>());

    // 応答を返さず送出時に失敗するハンドラ（接続拒否相当）。.NET の HttpRequestException は URI を含まない。
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
    }

    // ログ出力（メッセージ本文とスコープ）を文字列として捕捉する ILogger テストダブル。
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries
        {
            get { lock (_entries) return [.. _entries]; }
        }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

        public void Dispose()
        {
        }

        private void Record(string entry)
        {
            lock (_entries) _entries.Add(entry);
        }

        private sealed class RecordingLogger(RecordingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull
            {
                owner.Record($"{category} [scope] {state}");
                return NullScope.Instance;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                owner.Record($"{category} [{logLevel}] {formatter(state, exception)} {exception?.Message}");
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
