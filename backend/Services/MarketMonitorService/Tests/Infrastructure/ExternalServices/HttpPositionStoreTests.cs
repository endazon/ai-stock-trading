extern alias RiskManagementWorker;

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using RiskTradingDefaults = RiskManagementWorker::RiskManagementService.Domain.TradingDefaults;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetOpenPositions;
using MarketMonitorService.Domain;
using MarketMonitorService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MarketMonitorService.Tests;

// FR-03, FR-10, IADR-0030: リスク管理の GET /risk-controls/open-positions を同期照会する実装の写像とフェイルセーフを
// fake HttpMessageHandler で検証する（実ネットワーク不使用）。未取得系は空列（＝損切り検知対象なし）へ倒す。
public class HttpPositionStoreTests
{
    // 打ち切りが効かなくなったときに、黙って固まる代わりに理由付きで赤くするための上限（合否の基準ではない）。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    // リスク管理の Minimal API（Results.Ok）と同じ web 既定（camelCase・列挙は数値）。
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static HttpPositionStore Store(
        HttpMessageHandler handler, BusinessMetrics? metrics = null, ILogger<HttpPositionStore>? logger = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://risk") },
            metrics ?? new BusinessMetrics(),
            logger ?? NullLogger<HttpPositionStore>.Instance);

    [Fact]
    public async Task 応答を_HeldPosition_列に写像する()
    {
        // web 既定（camelCase・列挙は数値）で往復させる JSON を用意する。
        var payload = new[]
        {
            new HeldPosition("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 970m),
            new HeldPosition("MSFT", Market.UnitedStates, TradeSide.Sell, 5, 2_000m, 2_060m),
        };
        var body = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var handler = new StubHandler(HttpStatusCode.OK, body);

        var positions = await Store(handler).GetOpenPositionsAsync();

        positions.Should().HaveCount(2);
        var aapl = positions.Single(p => p.Symbol == "AAPL");
        aapl.Side.Should().Be(TradeSide.Buy);
        aapl.Quantity.Should().Be(10);
        aapl.StopLossPrice.Should().Be(970m);
        handler.LastPath.Should().Be("/risk-controls/open-positions");
    }

    // 🔴 T-10-803, FR-03, FR-10, #943, IADR-0390（T-10-744 の同型）: **送り手の本物の型（`OpenPositionView`）を直列化した応答**を読めることを固定する。
    // 上の写像テストは**受け手自身の型（HeldPosition）**を直列化しており、リスク管理側で項目名を変えても（例: `StopLossPrice` →
    // `StopPrice`）緑のままになる。実行時は既定値で逆シリアル化され、損切り価格 0 のロングは現在値が 0 以下にならない限り
    // 発火しない（`StopLossEvaluator`: price ≦ stop）＝**損切り保護が黙って外れる**。方向の改名は 0＝買いに化け、ショートを
    // ロングとして判定する。本テストは改名を赤で止める（変異注入で実測）。
    [Fact]
    public async Task 送り手の本物の型を直列化した応答から損切り判定に使う建玉を読める()
    {
        IReadOnlyList<OpenPositionView> views =
        [
            new("AAPL", Market.UnitedStates, TradeSide.Buy, 3_378, 337.63m, 320.75m),
            new("TSLA", Market.UnitedStates, TradeSide.Sell, 5, 240m, 252m),
        ];
        // リスク管理の Minimal API（Results.Ok）と同じ web 既定（camelCase・列挙は数値）。送り手の Program.cs がこの既定のまま
        // 出していることはリスク管理側の T-10-805 が固定する。
        var body = JsonSerializer.Serialize(views, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var positions = await Store(new StubHandler(HttpStatusCode.OK, body)).GetOpenPositionsAsync();

        positions.Should().BeEquivalentTo(
            [
                new HeldPosition("AAPL", Market.UnitedStates, TradeSide.Buy, 3_378, 337.63m, 320.75m),
                new HeldPosition("TSLA", Market.UnitedStates, TradeSide.Sell, 5, 240m, 252m),
            ],
            o => o.WithStrictOrdering());
        var aapl = positions.Single(p => p.Symbol == "AAPL");
        var tsla = positions.Single(p => p.Symbol == "TSLA");
        StopLossEvaluator.IsTriggered(aapl, 320m).Should().BeTrue("ロングは損切り価格以下で発火する");
        StopLossEvaluator.IsTriggered(tsla, 253m).Should().BeTrue("ショートは損切り価格以上で発火する");
        StopLossEvaluator.IsTriggered(tsla, 239m).Should().BeFalse("ショートを買いと読むと、下落で誤って発火する");
    }

    [Fact]
    public async Task 未取得_404_は空列_損切り検知対象なし()
    {
        (await Store(new StubHandler(HttpStatusCode.NotFound, "")).GetOpenPositionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task 非_2xx_は空列_損切り検知対象なし()
    {
        (await Store(new StubHandler(HttpStatusCode.Unauthorized, "")).GetOpenPositionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task 例外_不達_は空列_損切り検知対象なし()
    {
        (await Store(new ThrowingHandler()).GetOpenPositionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task 不正_空ボディの_200_は空列_損切り検知対象なし()
    {
        (await Store(new StubHandler(HttpStatusCode.OK, "")).GetOpenPositionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task タイムアウト_応答遅延_は空列_損切り検知対象なし()
    {
        // #885, IADR-0379: 従来は「壁時計 50 ms の HttpClient.Timeout」対「壁時計 2 秒のハンドラ遅延」という
        // **時刻どうしの競争**で合否が決まっていた（#900 / #901 と同型。機序は IADR-0367）。
        // 🔴 本ケースは遅延が勝っても空列になるため赤くはならなかったが、その代わり**打ち切り経路を黙って
        // 検査しなくなる**（変異注入で実測: 遅延を勝たせても緑のままだった）。応答が返らない上流に変え、
        // 打ち切りで終わったことを観測して確定させる。**上限値（50 ms）は動かしていない。**
        var handler = new NeverRespondingHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://risk"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var store = new HttpPositionStore(http, new BusinessMetrics(), NullLogger<HttpPositionStore>.Instance);

        // Guard は「打ち切りが効かない」ときに黙って固まらないための上限であり、合否の基準ではない。
        (await store.GetOpenPositionsAsync().WaitAsync(Guard)).Should().BeEmpty();
        (await handler.Cancellation.WaitAsync(Guard)).Should()
            .BeTrue("上限に達した要求は打ち切られる（応答は返っていない）");
    }

    // ---- 🔴 #957, IADR-0399: 行ごとの堅牢化（1 行の不正で他の行の損切り検知を止めない・ラインを 0 で評価しない） ----
    //
    // 本文はすべて**送り手の本物の型（OpenPositionView）を web 既定で直列化したもの**から作り、1 行だけを壊す
    // （送り手の改名・項目の欠落を、送り手の型の実際の JSON 名の上で再現する）。
    // 行 0: AAPL ロング（健全）／行 1: TSLA ショート（健全）／行 2: MSFT ロング（壊す対象）。
    private static readonly HeldPosition AaplHeld = new("AAPL", Market.UnitedStates, TradeSide.Buy, 3_378, 337.63m, 320.75m);
    private static readonly HeldPosition TslaHeld = new("TSLA", Market.UnitedStates, TradeSide.Sell, 5, 240m, 252m);

    private static string SenderBody(Action<JsonArray>? mutate = null)
    {
        IReadOnlyList<OpenPositionView> views =
        [
            new("AAPL", Market.UnitedStates, TradeSide.Buy, 3_378, 337.63m, 320.75m),
            new("TSLA", Market.UnitedStates, TradeSide.Sell, 5, 240m, 252m),
            new("MSFT", Market.UnitedStates, TradeSide.Buy, 10, 400m, 380m),
        ];
        var array = JsonSerializer.SerializeToNode(views, Web)!.AsArray();
        mutate?.Invoke(array);
        return array.ToJsonString();
    }

    private static JsonObject Row(JsonArray array, int index) => array[index]!.AsObject();

    private static (HttpPositionStore Store, MeterCapture Capture, BusinessMetrics Metrics,
        StopLossLivenessReporterTests.RecordingLogger<HttpPositionStore> Log) Isolated(string body, [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        // 否定形（「計上しなかった」）も表明するため、Meter 名はテストごとに隔離する（#695）。
        var meterName = MeterCapture.NewIsolatedMeterName(caller);
        var capture = new MeterCapture(meterName);
        var metrics = BusinessMetrics.WithMeterName(meterName);
        var log = new StopLossLivenessReporterTests.RecordingLogger<HttpPositionStore>();
        return (Store(new StubHandler(HttpStatusCode.OK, body), metrics, log), capture, metrics, log);
    }

    public static TheoryData<string> IdentityBreaks =>
    [
        "symbol を消す", "symbol を ticker へ改名", "symbol を空", "symbol を空白", "market を消す", "market を未定義値",
        "side を消す", "side を未定義値", "quantity を消す", "quantity を 0", "quantity を負", "行を null",
    ];

    private static void BreakIdentity(JsonArray array, string how)
    {
        switch (how)
        {
            case "symbol を消す": Row(array, 2).Remove("symbol"); break;
            case "symbol を ticker へ改名": Row(array, 2).Remove("symbol"); Row(array, 2)["ticker"] = "MSFT"; break;
            case "symbol を空": Row(array, 2)["symbol"] = ""; break;
            case "symbol を空白": Row(array, 2)["symbol"] = "  "; break;
            case "market を消す": Row(array, 2).Remove("market"); break;
            case "market を未定義値": Row(array, 2)["market"] = 99; break;
            case "side を消す": Row(array, 2).Remove("side"); break;
            case "side を未定義値": Row(array, 2)["side"] = 9; break;
            case "quantity を消す": Row(array, 2).Remove("quantity"); break;
            case "quantity を 0": Row(array, 2)["quantity"] = 0; break;
            case "quantity を負": Row(array, 2)["quantity"] = -10; break;
            case "行を null": array[2] = null; break;
            default: throw new ArgumentOutOfRangeException(nameof(how), how, null);
        }
    }

    // 🔴 T-10-833, FR-03, FR-10, #957, IADR-0399 決定1・決定2: 識別できない行は評価に渡さず、**他の行はそのまま返る**。
    // 以前は銘柄 null の行がそのまま巡回へ渡り、実運用の市況源（Finnhub）の例外で巡回全体が止まった（全建玉の損切り検知が止まる）。
    // 数量・方向・市場の欠落は既定値（0・買い・日本）に化けていた。黙って捨てず、Critical 1 行と計器で声に出す。
    [Theory]
    [MemberData(nameof(IdentityBreaks))]
    public async Task T_10_833_識別できない行は評価に渡さず_他の行はそのまま返り_Criticalと計器で声に出す(string how)
    {
        var (store, capture, metrics, log) = Isolated(SenderBody(a => BreakIdentity(a, how)));
        using var _ = capture;
        using var __ = metrics;

        var positions = await store.GetOpenPositionsAsync();

        positions.Should().BeEquivalentTo([AaplHeld, TslaHeld], o => o.WithStrictOrdering(), "健全な行は 1 行の不正に巻き込まれない");
        capture.SumOf(BusinessMetricNames.MarketMonitorPositionRowsDegraded).Should().Be(1);
        capture.TagValuesOf(BusinessMetricNames.MarketMonitorPositionRowsDegraded, BusinessMetricNames.TagReason)
            .Should().Equal("identity-missing");
        log.Entries.Where(e => e.Level == LogLevel.Critical).Should().ContainSingle()
            .Which.Message.Should().Contain("#2 identity-missing").And.Contain("評価しない行の建玉は、この巡回で損切り（S1）の到達を検知しません");
    }

    public static TheoryData<string> StopLineBreaks => ["stopLossPrice を消す", "stopLossPrice を stopPrice へ改名", "stopLossPrice を 0", "stopLossPrice を負"];

    private static void BreakStopLine(JsonObject row, string how)
    {
        switch (how)
        {
            case "stopLossPrice を消す": row.Remove("stopLossPrice"); break;
            case "stopLossPrice を stopPrice へ改名": row["stopPrice"] = row["stopLossPrice"]!.DeepClone(); row.Remove("stopLossPrice"); break;
            case "stopLossPrice を 0": row["stopLossPrice"] = 0; break;
            case "stopLossPrice を負": row["stopLossPrice"] = -1; break;
            default: throw new ArgumentOutOfRangeException(nameof(how), how, null);
        }
    }

    // 🔴 T-10-834, FR-03, FR-10, #957, IADR-0399 決定2: 損切りラインが無い／正でない行は **0 で評価しない**。送り手（リスク管理の
    // OpenPositionsService・IADR-0393 の StopLossUnknown）と同じ式（平均取得単価 × (1 ∓ 既定比率)）の近似で評価し、近似と印を付ける。
    // 以前は 0 に化け、ロングは発火せず（price ≦ 0）、含み益のショートは毎巡回発火した（price ≧ 0）。
    [Theory]
    [MemberData(nameof(StopLineBreaks))]
    public async Task T_10_834_損切りラインの無い行は0ではなく送り手と同じ近似のラインで評価し_近似と印を付ける(string how)
    {
        var (store, capture, metrics, log) = Isolated(SenderBody(a =>
        {
            BreakStopLine(Row(a, 1), how); // TSLA ショート
            BreakStopLine(Row(a, 2), how); // MSFT ロング
        }));
        using var _ = capture;
        using var __ = metrics;

        var positions = await store.GetOpenPositionsAsync();

        // 送り手の比率（リスク管理の TradingDefaults）で期待値を組む —— 受け手が別の比率を持つと赤になる。
        var ratio = RiskTradingDefaults.DefaultStopLossRatio;
        positions.Should().HaveCount(3);
        positions.Should().OnlyContain(p => p.StopLossPrice > 0m, "ライン 0 では評価しない");
        var tsla = positions.Single(p => p.Symbol == "TSLA");
        var msft = positions.Single(p => p.Symbol == "MSFT");
        tsla.Should().Be(new HeldPosition("TSLA", Market.UnitedStates, TradeSide.Sell, 5, 240m, 240m * (1m + ratio)) { StopLossApproximated = true });
        msft.Should().Be(new HeldPosition("MSFT", Market.UnitedStates, TradeSide.Buy, 10, 400m, 400m * (1m - ratio)) { StopLossApproximated = true });
        positions.Single(p => p.Symbol == "AAPL").Should().Be(AaplHeld, "健全な行は実値のまま・近似の印なし");

        StopLossEvaluator.IsTriggered(tsla, 239m).Should().BeFalse("含み益のショートを毎巡回発火させない（ライン 0 の症状）");
        StopLossEvaluator.IsTriggered(msft, 388m).Should().BeTrue("ロングは近似のラインで発火する（ライン 0 だと price ≦ 0 まで発火しない）");

        capture.SumOf(BusinessMetricNames.MarketMonitorPositionRowsDegraded).Should().Be(2);
        capture.TagValuesOf(BusinessMetricNames.MarketMonitorPositionRowsDegraded, BusinessMetricNames.TagReason)
            .Should().Equal("stop-line-approximated");
        log.Entries.Where(e => e.Level == LogLevel.Critical).Should().ContainSingle()
            .Which.Message.Should().Contain("#1 stop-line-approximated").And.Contain("#2 stop-line-approximated").And.Contain("実際のラインではありません");
    }

    // 🔴 T-10-835, FR-03, FR-10, #957, IADR-0399 決定2・決定4: ラインも平均取得単価も無い行は見積もれない —— 評価に渡さず（0 で評価しない）、
    // stop-line-unknown と Critical で出す。平均取得単価だけが無い行はラインで評価でき、平均取得単価は null（0 ではない）。
    [Fact]
    public async Task T_10_835_ラインも平均取得単価も無い行は評価せず_平均取得単価だけ無い行は評価し_平均取得単価はnullのまま()
    {
        var (store, capture, metrics, log) = Isolated(SenderBody(a =>
        {
            Row(a, 1).Remove("entryPrice");           // TSLA: 平均取得単価だけ無い → 評価する
            Row(a, 2).Remove("entryPrice");           // MSFT: どちらも無い → 評価しない
            Row(a, 2).Remove("stopLossPrice");
        }));
        using var _ = capture;
        using var __ = metrics;

        var positions = await store.GetOpenPositionsAsync();

        positions.Should().BeEquivalentTo(
            [AaplHeld, TslaHeld with { EntryPrice = null }], o => o.WithStrictOrdering());
        positions.Single(p => p.Symbol == "TSLA").EntryPrice.Should().BeNull("欠けた平均取得単価を 0 に化けさせない");
        capture.SumOf(BusinessMetricNames.MarketMonitorPositionRowsDegraded).Should().Be(1);
        capture.TagValuesOf(BusinessMetricNames.MarketMonitorPositionRowsDegraded, BusinessMetricNames.TagReason)
            .Should().Equal("stop-line-unknown");
        log.Entries.Where(e => e.Level == LogLevel.Critical).Should().ContainSingle()
            .Which.Message.Should().Contain("#2 stop-line-unknown");
    }

    public static TheoryData<string> UnreadableBodies => ["壊れた JSON", "null", "列挙が文字列"];

    // 🔴 T-10-836, FR-03, FR-10, #957, IADR-0399 決定2: 200 で本文が一覧として読めない（壊れた JSON・null・送り手が列挙を文字列で出す）は
    // 従来どおり空列だが、**契約の食い違い**として Critical と計器（response-unreadable）で出す（非 2xx・例外の Warning と分ける）。
    [Theory]
    [MemberData(nameof(UnreadableBodies))]
    public async Task T_10_836_200で一覧として読めない本文は空列のままCriticalと計器で出す(string kind)
    {
        var body = kind switch
        {
            "壊れた JSON" => "[{\"symbol\":",
            "null" => "null",
            "列挙が文字列" => JsonSerializer.Serialize(
                new[] { new OpenPositionView("AAPL", Market.UnitedStates, TradeSide.Buy, 3_378, 337.63m, 320.75m) },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        var (store, capture, metrics, log) = Isolated(body);
        using var _ = capture;
        using var __ = metrics;

        (await store.GetOpenPositionsAsync()).Should().BeEmpty();

        capture.TagValuesOf(BusinessMetricNames.MarketMonitorPositionRowsDegraded, BusinessMetricNames.TagReason)
            .Should().Equal("response-unreadable");
        log.Entries.Where(e => e.Level == LogLevel.Critical).Should().ContainSingle()
            .Which.Message.Should().Contain("保有の一覧として読めません");
    }

    // T-10-836（否定形）: 健全な応答では計器も Critical も出ない（平常時 0 件＝アラートの前提）。
    [Fact]
    public async Task T_10_836_健全な応答では計器もCriticalも出ない()
    {
        var (store, capture, metrics, log) = Isolated(SenderBody());
        using var _ = capture;
        using var __ = metrics;

        (await store.GetOpenPositionsAsync()).Should().HaveCount(3);

        capture.Measurements.Should().BeEmpty();
        log.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("リスク管理サービス不達");
    }

    // #885, IADR-0379: 時間では応答しない上流。終わり方は打ち切り（＝要求トークンの発火）だけであり、
    // 「遅延が上限に勝つ」という競争そのものが存在しない。
    private sealed class NeverRespondingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _cancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // 要求が打ち切られたか（true＝上限で切られた）。テストはこれで「応答で終わっていない」ことを確定させる。
        public Task<bool> Cancellation => _cancellation.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _cancellation.TrySetResult(cancellationToken.IsCancellationRequested);
            }

            throw new InvalidOperationException("到達しない（無期限待ちは打ち切りでしか終わらない）。");
        }
    }
}
