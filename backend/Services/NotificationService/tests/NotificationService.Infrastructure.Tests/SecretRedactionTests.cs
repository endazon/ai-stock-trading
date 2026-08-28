using NotificationService.Application.Ports;
using NotificationService.Application.Services;
using NotificationService.Application.State;
using NotificationService.Infrastructure.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace NotificationService.Infrastructure.Tests;

// NFR（セキュリティ）, FR-09, FR-14, #341, #318, IADR-0242 決定4:
// **秘密情報がログへ出力されないことの否定形。**
//
// 対象は 3 つ。いずれも**漏れれば失効・再発行が要る**（#318 は実際に Webhook URL の失効待ちである）。
//   1. Bot トークン（`Notifications:Discord:Bot:Token`）
//   2. kill switch 確認フレーズ（誤爆防止の閂そのもの）
//   3. Discord Webhook URL（**URI 自体が資格情報**。パスにトークンを含む）
//
// 🔴 既存の担保は 3 のうち HttpClient ログだけ（`DiscordWebhookHttpClientTests`）であり、
// **1 と 2 については 1 つも無かった**（#341 のギャップ分析で実測）。本ファイルで埋める。
//
// **「ログを出さない」ことは求めていない。** 障害切り分けができる状態は保ったまま、
// 秘密そのものが本文へ現れないことだけを固定する。
public class SecretRedactionTests
{
    private const string BotToken = "MTIzNDU2Nzg5MDEyMzQ1Njc4.GhIjKl.SUPER-SECRET-BOT-TOKEN-VALUE";
    private const string Phrase = "STOP TRADING NOW 2026";
    private const string WebhookUrl = "https://discord.com/api/webhooks/1234567890/SUPER-SECRET-WEBHOOK-TOKEN";
    private const string OwnerUser = "discord-owner-1";

    // ---- 1. Bot トークン ----

    [Fact]
    public void 認証設定の不足で接続しないときのログにトークンが現れない()
    {
        // FR-14, IADR-0062: 設定不備は起動時ログで気付けるようにしてある。**その診断にトークンを混ぜない。**
        var recorder = new RecordingLoggerProvider();
        var options = new DiscordBotOptions { Enabled = true, Token = BotToken };

        CreateGateway(options, recorder);

        recorder.Entries.Should().NotBeEmpty("設定不備は警告として記録されるはずである");
        recorder.Entries.Should().NotContain(e => e.Contains(BotToken, StringComparison.Ordinal));
    }

    [Fact]
    public void トークン未設定の警告にも他の秘密が現れない()
    {
        var recorder = new RecordingLoggerProvider();
        var options = FullyConfigured();
        options.Token = null;

        CreateGateway(options, recorder);

        recorder.Entries.Should().NotBeEmpty();
        recorder.Entries.Should().NotContain(e => e.Contains(Phrase, StringComparison.Ordinal));
    }

    [Fact]
    public void 設定が揃って接続対象になるときもトークンをログに書かない()
    {
        var recorder = new RecordingLoggerProvider();

        CreateGateway(FullyConfigured(), recorder);

        recorder.Entries.Should().NotContain(e => e.Contains(BotToken, StringComparison.Ordinal));
    }

    // ---- 2. kill switch 確認フレーズ ----

    [Theory]
    [InlineData("STOP TRADING NOW 2026")]   // 正しいフレーズ
    [InlineData("stop trading now 2027")]   // 誤ったフレーズ（利用者の入力）
    [InlineData("")]
    public async Task kill_switch_の確認ステップのログにフレーズが現れない(string entered)
    {
        // FR-14, UC-06: フレーズは誤爆防止の閂である。ログに残ると**閂の値が運用ログから読める**。
        // 入力値（利用者が打った文字列）も残さない——打ち間違いの記録が正解の推測を助けてはならない。
        var recorder = new RecordingLoggerProvider();
        using var factory = LoggerFactoryOf(recorder);
        var options = FullyConfigured();
        var handler = new KillSwitchCommandHandler(
            new StubKillSwitchController(), options, factory.CreateLogger<KillSwitchCommandHandler>());

        await handler.HandleAsync(
            new DiscordCommandContext("1", "2", OwnerUser, IsDirectMessage: false, "/killswitch"), entered);

        recorder.Entries.Should().NotContain(e => e.Contains(Phrase, StringComparison.Ordinal));
        recorder.Entries.Should().NotContain(e => e.Contains("stop trading now 2027", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GFV_解除の確認ステップのログにもフレーズが現れない()
    {
        // #464, ADR-0028: GFV 解除は kill switch と同じ Verify を通る。同じ規律を適用する。
        var recorder = new RecordingLoggerProvider();
        using var factory = LoggerFactoryOf(recorder);
        var handler = new GoodFaithViolationCommandHandler(
            new StubGoodFaithViolationController(), FullyConfigured(),
            factory.CreateLogger<GoodFaithViolationCommandHandler>());

        await handler.HandleAsync(
            new DiscordCommandContext("1", "2", OwnerUser, IsDirectMessage: false, "/gfv clear"),
            "誤ったフレーズ",
            "是正済み");

        recorder.Entries.Should().NotContain(e => e.Contains(Phrase, StringComparison.Ordinal));
        recorder.Entries.Should().NotContain(e => e.Contains("誤ったフレーズ", StringComparison.Ordinal));
    }

    // ---- 3. Webhook URL ----

    [Fact]
    public void 送信手段の選択ログに_Webhook_URL_が現れない()
    {
        // #289, #318: URI 自体が資格情報である。未知の provider の警告に URL を混ぜない。
        var recorder = new RecordingLoggerProvider();
        using var factory = LoggerFactoryOf(recorder);

        NotificationSenderFactory.Create("unknown-provider", WebhookUrl, new HttpClient(), factory);

        recorder.Entries.Should().NotBeEmpty();
        recorder.Entries.Should().NotContain(e => e.Contains(WebhookUrl, StringComparison.Ordinal));
    }

    [Fact]
    public async Task 送信失敗のログに_Webhook_URL_が現れない()
    {
        // 送信失敗は記録する（障害切り分けのため）。**URL は書かない。**
        var recorder = new RecordingLoggerProvider();
        using var factory = LoggerFactoryOf(recorder);
        var sender = new DiscordWebhookNotificationSender(
            new HttpClient(new StubHandler()) { BaseAddress = new Uri("https://discord.com") },
            WebhookUrl,
            factory.CreateLogger<DiscordWebhookNotificationSender>());

        var act = async () => await sender.SendAsync(
            new NotificationMessage("件名", "本文", NotificationSeverity.Info));

        // 送信失敗は例外化してメッセージングの再試行に委ねる（IADR-0020）。
        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().NotContain(WebhookUrl);
        recorder.Entries.Should().NotBeEmpty();
        recorder.Entries.Should().NotContain(e => e.Contains(WebhookUrl, StringComparison.Ordinal));
    }

    private static DiscordBotOptions FullyConfigured()
    {
        var options = new DiscordBotOptions
        {
            Enabled = true,
            Token = BotToken,
            GuildId = "1",
            ChannelId = "2",
            KillSwitchConfirmationPhrase = Phrase,
        };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = "endazon";
        return options;
    }

    private static ILoggerFactory LoggerFactoryOf(RecordingLoggerProvider recorder) =>
        LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(recorder);
        });

    private static void CreateGateway(DiscordBotOptions options, RecordingLoggerProvider recorder)
    {
        using var factory = LoggerFactoryOf(recorder);
        DiscordBotGatewayFactory.Create(
            options,
            new KillSwitchCommandHandler(
                new StubKillSwitchController(), options, factory.CreateLogger<KillSwitchCommandHandler>()),
            new PauseCommandHandler(
                new StubPauseController(), options, factory.CreateLogger<PauseCommandHandler>()),
            new StageGateCommandHandler(
                new StubStageGateController(), options, factory.CreateLogger<StageGateCommandHandler>()),
            new GoodFaithViolationCommandHandler(
                new StubGoodFaithViolationController(), options,
                factory.CreateLogger<GoodFaithViolationCommandHandler>()),
            new ReportCommandHandler(
                new StubReportReviewController(), new VersionedConfirmationGuard(), options,
                factory.CreateLogger<ReportCommandHandler>()),
            factory);
    }

    // 非 2xx を返すハンドラ（送信失敗の経路を通す）。
    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
    }

    private sealed class StubKillSwitchController : IKillSwitchController
    {
        public Task<KillSwitchResult> EngageAsync(string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(new KillSwitchResult(true, true, "起動"));

        public Task<KillSwitchResult> DisengageAsync(string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(new KillSwitchResult(true, false, "解除"));
    }

    private sealed class StubPauseController : IPauseController
    {
        public Task<PauseResult> PauseAsync(string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PauseResult(true, true, "一時停止"));

        public Task<PauseResult> ResumeAsync(string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PauseResult(true, false, "再開"));

        public Task<RiskStatusResult> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RiskStatusResult(true, "稼働状態"));
    }

    private sealed class StubStageGateController : IStageGateController
    {
        public Task<StageGateStatusResult> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StageGateStatusResult(true, "段階ゲート"));

        public Task<StageTransitionCommandResult> RequestTransitionAsync(
            int targetStage, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StageTransitionCommandResult(true, true, "遷移"));

        public Task<StageGateStatusResult> EvaluateWithdrawalAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StageGateStatusResult(true, "撤退評価"));
    }

    private sealed class StubGoodFaithViolationController : IGoodFaithViolationController
    {
        public Task<GoodFaithViolationClearResult> ClearAsync(
            string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GoodFaithViolationClearResult(true, true, "解除"));
    }

    private sealed class StubReportReviewController : IReportReviewController
    {
        public Task<ReportReviewResult> GetReviewAsync(
            string periodKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReportReviewResult(true, 1, "版 1"));

        public Task<ReportConfirmResult> ConfirmAsync(
            string periodKey, int expectedVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReportConfirmResult(true, true, "確定"));

        public Task<ReportReviewResult> RequestChangesAsync(
            string periodKey, int expectedVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReportReviewResult(true, 1, "差し戻し"));
    }

    // ログ出力（メッセージ本文・スコープ・例外メッセージ）を文字列として捕捉する ILogger テストダブル。
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
                owner.Record($"{category} [{logLevel}] {formatter(state, exception)} {exception}");
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
