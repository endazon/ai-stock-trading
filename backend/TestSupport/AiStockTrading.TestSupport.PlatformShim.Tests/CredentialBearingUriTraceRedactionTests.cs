using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Observability;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// FR-09, NFR（セキュリティ）, #751, #313, #318, IADR-0121, IADR-0333:
// **Discord Webhook URL がトレース（Tempo）へ平文で出ていかないことを、実送信で固定する。**
//
// Webhook URL は「知っていれば認証なしで当該チャンネルへ投稿できる」＝**それ自体が資格情報**であり、
// 失効手段は再発行しかない。IADR-0121 はログ経路（Loki）を塞いだが、`AddHttpClientInstrumentation()` の
// `url.full` タグには**フル URL（パス込み）**が載り続けていた（#313 が指摘・後継なしでクローズ・#751 で再起票）。
//
// 🔴 **肯定形だけでは足りない。** 「トークンが出ていない」は、計装がそもそも動いていないときにも緑になる。
// そのため**陰性対照**（同じ入力でも秘匿を積まないパイプラインならトークンが確かに残る）を対で置く。
//
// 🔴 **`Activity` はプロセス全体で共有される。** xUnit は**クラスを跨いで**並列実行し、本アセンブリには
// `TracerProvider` を解決したまま破棄しない試験が別クラスに在る（`FoundationRegistrationTests`）。
// つまり**本クラスの試験が走っている間、他クラスの秘匿プロセッサが生きている**。そのため:
//   - 実送信の試験は、捕捉した中から**自分の送信先（`server.port`・URL）で必ず絞り込む**。
//   - **陰性対照は実送信では書けない**（秘匿が先に効いて赤になる。実測）。専用の `ActivitySource` を使う。
public sealed class CredentialBearingUriTraceRedactionTests
{
    // 実 Webhook と同じ形（/api/webhooks/<id>/<token>）のダミー。**この定数がトレースへ現れたら不合格**である。
    private const string WebhookId = "wh-test-id";
    private const string WebhookToken = "SUPER-SECRET-WEBHOOK-TOKEN";

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    // 🔴 空きポートは **OS の動的（ephemeral）範囲の外**から選ぶ（GrpcListenerBindingTests と同じ理由。
    // 動的範囲は送信側ソケットの割当にも使われ、並列テストと衝突する）。**待ち受けは立てない** ——
    // アクティビティは**リクエスト開始時**に `url.full` を刻むため、接続拒否でも観測できる。
    // サーバを立てないぶん、この試験は外部依存も Docker も持たない。
    private static int UnusedLoopbackPort()
    {
        var random = new Random();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var candidate = random.Next(20000, 30000);
            var probe = new TcpListener(IPAddress.Loopback, candidate);
            try
            {
                probe.Start();
            }
            catch (SocketException)
            {
                continue; // 使用中。次の候補へ。
            }
            finally
            {
                probe.Stop();
            }

            return candidate;
        }

        throw new InvalidOperationException("20000〜29999 に空きポートが見つからなかった。");
    }

    private static async Task SendAsync(string url)
    {
        using var client = new HttpClient();
        try
        {
            await client.PostAsync(new Uri(url), new StringContent("{}"), TestContext.Current.CancellationToken);
        }
        catch (HttpRequestException)
        {
            // 待ち受けが無いので接続は必ず失敗する。**見たいのは送信の成否ではなくアクティビティのタグ**である。
        }
    }

    /// <summary>Activity の観測可能な文字列（表示名＋全タグ値）を平坦化する。タグ名を 1 つずつ知らなくても漏れを検出できる。</summary>
    private static IReadOnlyList<string> ObservableStrings(Activity activity) =>
    [
        activity.DisplayName,
        .. activity.TagObjects.Select(t => $"{t.Key}={t.Value}"),
    ];

    // ---- 1. 肯定形（配線）: 実パイプラインを通すとトークンは出ていかない ----

    [Fact]
    public async Task 共有の可観測性配線を通した_Webhook_送信のトレースにトークンが現れない()
    {
        var sink = new ActivitySink();
        var port = UnusedLoopbackPort();
        var url = $"http://localhost:{port}/api/webhooks/{WebhookId}/{WebhookToken}";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingObservability(EmptyConfig(), "notification-service");
        // **exporter と同じ「後ろ」の位置から観測する。** ここで捕捉できる状態が、そのまま OTLP へ出ていく状態である
        // （プロセッサは登録順に OnEnd が走るため、秘匿より後ろに居る本プロセッサは秘匿後を見る）。
        services.ConfigureOpenTelemetryTracerProvider(builder => builder.AddProcessor(sink));

        using var provider = services.BuildServiceProvider();
        // TracerProvider は解決した時点で構築される（ホストが無いので明示的に取る）。
        var tracerProvider = provider.GetRequiredService<TracerProvider>();

        await SendAsync(url);

        // 戻り値は見ない —— 本構成には OTLP exporter も載っており、テスト環境に otel-collector は居ないため
        // ForceFlush は false を返す（**外部送信の失敗は想定内**。BusinessMetricsWiringTests と同じ判断）。
        tracerProvider.ForceFlush(10_000);

        // 🔴 URL では絞れない（**秘匿済みなのでポートごと消えている**）。秘匿されない `server.port` で自分の送信を引く。
        var mine = sink.Snapshot()
            .Where(a => a.GetTagItem("server.port") is int p && p == port).ToList();
        var activity = mine.Should().ContainSingle().Subject;

        ObservableStrings(activity).Should().NotContain(
            s => s.Contains(WebhookToken, StringComparison.Ordinal),
            "URI 自体が資格情報である。トークンはスパン名にもどのタグにも現れてはならない");
        ObservableStrings(activity).Should().NotContain(
            s => s.Contains(WebhookId, StringComparison.Ordinal),
            "部分開示（token だけ伏せて id は出す）もしない");
        activity.GetTagItem("url.full").Should().Be("http://localhost/***");
    }

    // ---- 2. 陰性対照（A/B）: 同じ入力で、プロセッサの有無だけを変える ----
    //
    // 🔴 **この 2 本は「1 行の差」しか持たない。** 片方だけがプロセッサを積み、他はすべて同一である。
    // 陰性対照（A）が赤くなったら、肯定形の緑は「そもそも何も出ていないから緑」を疑うべきである。
    //
    // 🔴 **ここでは実送信を使わない。** `Activity` はプロセス全体で共有され、`ActivityListener` は
    // **登録順**に停止コールバックが走る。本アセンブリには **TracerProvider を解決したまま破棄しない試験**が
    // 別クラスに在り（`FoundationRegistrationTests`）、その秘匿プロセッサが並列実行中は生きている ——
    // 実送信の陰性対照は「秘匿が先に効いてしまって赤」になり得る（実測）。
    // **専用の ActivitySource なら誰も listen していない**ため、A/B は外乱なしに成立する。
    // ランタイムが `url.full` にパスを丸ごと書くこと自体は、下の 3（実送信・非 Webhook 形）が示す。

    private static Activity EmitWithUrlFull(string url, bool withRedaction)
    {
        var sink = new ActivitySink();
        using var source = new ActivitySource($"ast.test.{Guid.NewGuid():N}");
        var builder = Sdk.CreateTracerProviderBuilder().AddSource(source.Name);
        if (withRedaction) builder = builder.AddProcessor(new CredentialBearingUriRedactionProcessor());
        using var provider = builder.AddProcessor(sink).Build();

        using (var activity = source.StartActivity("POST", ActivityKind.Client))
        {
            // ランタイム（DiagnosticsHandler）が刻むのと同じタグ・同じ値を与える。
            activity!.SetTag("url.full", url);
        }

        return sink.Snapshot().Should().ContainSingle().Subject;
    }

    [Fact]
    public void 陰性対照_秘匿プロセッサを入れないとトークンは_url_full_に残ったまま出ていく()
    {
        var url = $"https://discord.com/api/webhooks/{WebhookId}/{WebhookToken}";

        var activity = EmitWithUrlFull(url, withRedaction: false);

        activity.GetTagItem("url.full").Should().Be(url,
            "秘匿が無ければ Webhook のトークンはフル URL のまま exporter まで到達する（#313 が指摘した漏洩そのもの）");
    }

    [Fact]
    public void 同じ入力でも秘匿プロセッサを入れるとトークンは落ちる()
    {
        var url = $"https://discord.com/api/webhooks/{WebhookId}/{WebhookToken}";

        var activity = EmitWithUrlFull(url, withRedaction: true);

        activity.GetTagItem("url.full").Should().Be("https://discord.com/***");
    }

    // ---- 3. 巻き添えなし ＋ ランタイムがパスを丸ごと書くことの実証 ----

    [Fact]
    public async Task 資格情報を含まない_URL_の_url_full_はそのまま残る()
    {
        // 2 つのことを同時に示す。
        //   (1) 可観測性を不必要に落とさない（#313 の受け入れ基準 2 / IADR-0121 決定 4 と同じ向き）。
        //       Discord Bot Gateway の API パスのように、**秘密がヘッダにあってパスは診断に要る**送信がある。
        //   (2) 🔴 **ランタイムは `url.full` にパスを丸ごと書く。** ここで**実送信**の url.full が
        //       パス込みで一致することが、「Webhook なら token まで載る」＝#313 の漏洩の実在の根拠である
        //       （2 の A/B は合成した Activity で行うため、この実証はここが担う）。
        var sink = new ActivitySink();
        var port = UnusedLoopbackPort();
        var url = $"http://localhost:{port}/api/v10/channels/1234567890/messages";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingObservability(EmptyConfig(), "notification-service");
        services.ConfigureOpenTelemetryTracerProvider(builder => builder.AddProcessor(sink));

        using var provider = services.BuildServiceProvider();
        var tracerProvider = provider.GetRequiredService<TracerProvider>();

        await SendAsync(url);
        tracerProvider.ForceFlush(10_000);

        sink.Snapshot().Should().ContainSingle(a => a.GetTagItem("url.full") as string == url);
    }

    // ---- 4. 判定・置換の純関数 ----

    [Theory]
    // Webhook 形（実物・試験のダミー・大文字混じり・クエリつき）は scheme+host まで落ちる。
    [InlineData("https://discord.com/api/webhooks/1234567890/SECRET", "https://discord.com/***")]
    [InlineData("http://localhost:18289/api/webhooks/wh-test-id/wh-test-token", "http://localhost/***")]
    [InlineData("https://discord.com/API/Webhooks/1234567890/SECRET", "https://discord.com/***")]
    [InlineData("https://discord.com/api/webhooks/1234567890/SECRET?wait=true", "https://discord.com/***")]
    // userinfo も落ちる（Host だけを使うため）。
    [InlineData("https://user:pw@discord.com/api/webhooks/1234567890/SECRET", "https://discord.com/***")]
    public void 資格情報を含む_URI_はスキームとホストだけになる(string url, string expected)
    {
        CredentialBearingUriRedactionProcessor.TryRedact(url, out var redacted).Should().BeTrue();
        redacted.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://discord.com/api/v10/channels/1/messages")]     // Bot API（秘密はヘッダ側）
    [InlineData("https://discord.com/api/webhooks/1234567890")]         // token を持たない形は資格情報ではない
    [InlineData("http://risk-management-service:8080/health/ready")]    // 内部の s2s
    // 🔴 **先頭が `/` の相対パスは、Unix では `file:///…` として絶対 URI と見なされる**（Windows では見なされない）。
    // scheme を見ないと `file:///***` を書き戻す。**Windows では緑・Linux の CI でだけ赤**になった実測に基づく。
    [InlineData("/api/webhooks/1234567890/SECRET")]
    [InlineData("file:///api/webhooks/1234567890/SECRET")]
    [InlineData("ftp://example.com/api/webhooks/1234567890/SECRET")]
    [InlineData("")]
    [InlineData(null)]
    public void 資格情報を含まない_URI_は書き換えない(string? url)
    {
        CredentialBearingUriRedactionProcessor.TryRedact(url, out var redacted).Should().BeFalse();
        redacted.Should().BeEmpty();
    }

    /// <summary>
    /// 終了した Activity を集める受け皿（InMemory exporter パッケージを足さないため自前）。
    /// **並列で走る他クラスの送信も入り得る**ため、追加は排他し、読み出しはスナップショットで返す。
    /// </summary>
    private sealed class ActivitySink : BaseProcessor<Activity>
    {
        private readonly List<Activity> _items = [];

        public override void OnEnd(Activity data)
        {
            lock (_items) _items.Add(data);
        }

        public IReadOnlyList<Activity> Snapshot()
        {
            lock (_items) return [.. _items];
        }
    }
}
