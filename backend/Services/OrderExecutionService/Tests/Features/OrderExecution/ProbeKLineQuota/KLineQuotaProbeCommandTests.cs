using System.Reflection;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using OrderExecutionService.Features.OrderExecution.ProbeKLineQuota;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-02, FR-15, ADR-0048 決定 3, ADR-0023 決定 5, #1117, IADR-0464: 日足 K 線の取得枠と復権の扱いの 1 回実行の検証口。
// 受け入れ基準: 各要求は 1 回（再試行しない）／取得の前後で枠を照会し差を出す／直近 N 本の整形／復権区分ごとの並び／
// 書き込み系 API へ届かない構造／秘密を出さない／自制レート（2.5 秒間隔）／引数不正・構成不正は接続しない。
public class KLineQuotaProbeCommandTests
{
    private const string Flag = KLineQuotaProbeCommand.Flag;
    private static readonly DateOnly Today = new(2026, 9, 30);

    private static async Task<(int ExitCode, string Output, FakeQuery Query, List<TimeSpan> Delays)> Run(
        FakeQuery? query = null, IReadOnlyCollection<string>? sensitiveValues = null, params string[] args)
    {
        query ??= new FakeQuery();
        var delays = new List<TimeSpan>();
        var writer = new StringWriter();
        var exitCode = await KLineQuotaProbeCommand.RunAsync(
            args.Length == 0 ? [Flag] : args,
            () => query.Created(),
            writer,
            cancellationToken: TestContext.Current.CancellationToken,
            sensitiveValues: sensitiveValues,
            timeProvider: new FixedTimeProvider(Today),
            delay: (d, _) =>
            {
                delays.Add(d);
                return Task.CompletedTask;
            });
        return (exitCode, writer.ToString(), query, delays);
    }

    [Fact]
    public async Task 既定の手順は枠の照会と日足の取得を1回ずつ決まった順で撃つ()
    {
        var (exitCode, output, query, _) = await Run();

        exitCode.Should().Be(KLineQuotaProbeCommand.ExitAllSucceeded, output);
        query.Calls.Should().Equal(
            "quota(detail)",
            "kline(AAPL,Forward,2026-07-28,2026-09-30)", "quota",
            "kline(MSFT,Forward,2026-07-28,2026-09-30)", "quota",
            "kline(NVDA,None,2024-05-28,2024-06-21)", "quota",
            "kline(NVDA,Forward,2024-05-28,2024-06-21)", "quota",
            "kline(NVDA,Backward,2024-05-28,2024-06-21)", "quota",
            "quota(detail)");
        output.Should().Contain("result=ok").And.Contain("requests.sent=12 requests.failed=0").And.Contain("exitCode=0");
        query.Disposed.Should().BeTrue("接続は 1 回実行の後に閉じる");
    }

    [Fact]
    public async Task 要求の間隔は毎回2点5秒以上あけ最初の要求の前には待たない()
    {
        var (_, _, query, delays) = await Run();

        delays.Should().HaveCount(query.Calls.Count - 1, "最初の 1 回を除くすべての要求の前に待つ");
        delays.Should().AllSatisfy(d => d.Should().Be(TimeSpan.FromMilliseconds(2500)));
        (TimeSpan.FromMinutes(1) / KLineQuotaProbeCommand.MinRequestInterval).Should().BeLessThanOrEqualTo(30, "自制レート 30 回/分の内側");
    }

    [Fact]
    public async Task 直近N本を日時と始高安終と出来高と売買代金で出し空白足は除く()
    {
        var query = new FakeQuery { RecentBars = 40, BlankEvery = 5 };

        var (exitCode, output, _, _) = await Run(query, null, Flag, "--symbols", "AAPL", "--count", "3");

        exitCode.Should().Be(0, output);
        output.Should().Contain("kline[0] symbol=AAPL rehab=forward from=2026-09-10 to=2026-09-30 retType=0 bars=33 blank=8 hasMore=no");
        output.Should().Contain("kline[0].bars.shown=3");
        output.Should().Contain("kline[0].bar[2] time=2026-08-10 open=139 high=141 low=138 close=140 volume=1039 turnover=145460");
        output.Should().NotContain("kline[0].bar[3]", "直近 N 本だけ出す");
        output.Should().NotContain("time=2026-08-11", "空白足（IsBlank）は価格として出さない");
    }

    [Fact]
    public async Task 取得の前後で枠を照会し銘柄単位で消費されるなら単位の読みはper_security()
    {
        // 未知の銘柄の取得で +1、既知（取る前から詳細一覧に在る・この実行で既に取った）の取得で 0。
        var query = new FakeQuery { Unit = QuotaUnit.PerSecurity, KnownBefore = ["US.AAPL"] };

        var (_, output, _, _) = await Run(query);

        output.Should().Contain("quota[0] label=before retType=0 used=1 remain=299")
            .And.Contain("quota[0].detail.count=1")
            .And.Contain("quota[0].detail[0] security=US.AAPL name=Apple requestTime=2026-09-29 10:00:00 requestTimeStamp=1790000000")
            .And.Contain("quota[1] label=after-kline[0] retType=0 used=1 remain=299 delta.used=0")
            .And.Contain("quota[2] label=after-kline[1] retType=0 used=2 remain=298 delta.used=+1");
        output.Should().Contain("quota.step[0] symbol=AAPL rehab=forward known=yes delta.used=0")
            .And.Contain("quota.step[1] symbol=MSFT rehab=forward known=no delta.used=+1")
            .And.Contain("quota.step[2] symbol=NVDA rehab=none known=no delta.used=+1")
            .And.Contain("quota.step[3] symbol=NVDA rehab=forward known=yes delta.used=0")
            .And.Contain("quota.unit.unknownSecurityDeltas=+1,+1")
            .And.Contain("quota.unit.knownSecurityDeltas=0,0,0")
            .And.Contain("quota.unit.reading=per-security")
            .And.Contain("quota.used.before=1 quota.used.final=3 quota.used.delta=+2");
    }

    [Fact]
    public async Task 要求ごとに消費されるなら単位の読みはper_request()
    {
        var query = new FakeQuery { Unit = QuotaUnit.PerRequest };

        var (_, output, _, _) = await Run(query);

        output.Should().Contain("quota.unit.knownSecurityDeltas=+1,+1")
            .And.Contain("quota.unit.reading=per-request")
            .And.Contain("quota.used.delta=+5");
    }

    [Theory]
    [InlineData(new[] { 1, 1 }, new[] { 0, 0 }, "per-security")]
    [InlineData(new int[0], new[] { 0 }, "per-security")]
    [InlineData(new[] { 1 }, new[] { 1, 1 }, "per-request")]
    [InlineData(new[] { 1, 1 }, new int[0], "inconclusive")]
    [InlineData(new[] { 1 }, new[] { 0, 1 }, "inconclusive")]
    [InlineData(new[] { 2 }, new[] { 0 }, "inconclusive")]
    [InlineData(new int[0], new int[0], "inconclusive")]
    public void 取得枠の単位の読みは既知の銘柄の取得の差で分ける(int[] unknown, int[] known, string expected) =>
        KLineQuotaProbeCommand.ReadQuotaUnit(unknown, known).Should().Be(expected);

    [Fact]
    public async Task 分割の比較は日付ごとに復権区分の終値と出来高を並べ無復権と異なる日数を出す()
    {
        // 前復権は分割前の価格を 1/10 にし出来高は調整しない、後復権は分割後の価格を 10 倍にする、という偽の OpenD。
        var (exitCode, output, _, _) = await Run(new FakeQuery());

        exitCode.Should().Be(0, output);
        output.Should().Contain("split.symbol=NVDA split.dates=4")
            .And.Contain("split.row date=2024-06-06 none.close=1200 none.volume=400 forward.close=120 forward.volume=400 backward.close=1200 backward.volume=400")
            .And.Contain("split.row date=2024-06-10 none.close=121 none.volume=4000 forward.close=121 forward.volume=4000 backward.close=1210 backward.volume=4000")
            .And.Contain("split.forward.vsNone dates=4 closeDiffers=2 volumeDiffers=0 closeRatio.noneOverRehab=1..10 volumeRatio.rehabOverNone=1..1")
            .And.Contain("split.backward.vsNone dates=4 closeDiffers=2 volumeDiffers=0 closeRatio.noneOverRehab=0.1..1 volumeRatio.rehabOverNone=1..1");
    }

    [Fact]
    public async Task 非成功の応答は撃ち直さず次の段へ進みretMsgを出して終了コード1()
    {
        var query = new FakeQuery { FailRehab = KLineRehab.Backward };

        var (exitCode, output, q, _) = await Run(query);

        exitCode.Should().Be(KLineQuotaProbeCommand.ExitQueryFailed, output);
        q.Calls.Count(c => c.StartsWith("kline(NVDA,Backward", StringComparison.Ordinal)).Should().Be(1, "失敗しても撃ち直さない");
        q.Calls.Should().HaveCount(12, "失敗した段の後も手順を続ける");
        output.Should().Contain("kline[4] symbol=NVDA rehab=backward from=2024-05-28 to=2024-06-21 retType=-1 bars=0")
            .And.Contain("kline[4].retMsg=backward not supported line2")
            .And.Contain("split.backward.vsNone dates=0")
            .And.Contain("result=partial")
            .And.Contain("requests.failed=1");
        output.Should().NotContain("rehab=backward known=", "失敗した取得は枠の単位の読みに数えない");
    }

    [Fact]
    public async Task 接続の失敗や例外では以後の要求を撃たず型と文言を出して終了コード1()
    {
        var query = new FakeQuery { ThrowOnCall = 3 };

        var (exitCode, output, q, _) = await Run(query);

        exitCode.Should().Be(KLineQuotaProbeCommand.ExitQueryFailed, output);
        q.Calls.Should().HaveCount(3, "例外の後は撃たない（撃ち直さない）");
        output.Should().Contain("result=error")
            .And.Contain("error[0].type=TimeoutException")
            .And.Contain("error[0].message=返信待ちで打ち切り")
            .And.Contain("requests.sent=3")
            .And.Contain("exitCode=1");
        q.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task 銘柄と本数と分割の銘柄と期間を引数で指定できる()
    {
        var (exitCode, output, query, _) = await Run(null, null,
            Flag, "--symbols", "us.tsla,BRK.B", "--count", "10", "--split-symbol", "AAPL", "--split-from", "2020-08-17", "--split-to", "2020-09-11");

        exitCode.Should().Be(0, output);
        query.Calls.Where(c => c.StartsWith("kline", StringComparison.Ordinal)).Should().Equal(
            "kline(TSLA,Forward,2026-08-27,2026-09-30)",
            "kline(BRK.B,Forward,2026-08-27,2026-09-30)",
            "kline(AAPL,None,2020-08-17,2020-09-11)",
            "kline(AAPL,Forward,2020-08-17,2020-09-11)",
            "kline(AAPL,Backward,2020-08-17,2020-09-11)");
        output.Should().Contain("probe=kline-quota market=US symbols=TSLA,BRK.B count=10 split=AAPL:2020-08-17..2020-09-11");
    }

    public static TheoryData<string[]> InvalidArguments => new()
    {
        new[] { Flag + "=AAPL" },                                    // = 形は受け付けない
        new[] { "--other", Flag },                                   // 旗が先頭でない
        new[] { Flag, "--unknown", "x" },                            // 未知のオプション
        new[] { Flag, "--symbols" },                                 // 値が無い
        new[] { Flag, "--symbols", "--count" },                      // 値の取り違え
        new[] { Flag, "--symbols", "AAPL", "--symbols", "MSFT" },    // 重複
        new[] { Flag, "--symbols", "AAPL,AAPL" },                    // 銘柄の重複
        new[] { Flag, "--symbols", "A,B,C,D,E,F" },                  // 銘柄が多すぎる
        new[] { Flag, "--symbols", "JP.7203" },                      // 米国株以外
        new[] { Flag, "--symbols", "AA;rm" },                        // 記号
        new[] { Flag, "--count", "0" },                              // 本数の下限
        new[] { Flag, "--count", "101" },                            // 本数の上限
        new[] { Flag, "--count", "-5" },                             // 負
        new[] { Flag, "--split-symbol", "NVDA" },                    // 分割は 3 つそろえる
        new[] { Flag, "--split-symbol", "NVDA", "--split-from", "2024-06-21", "--split-to", "2024-05-28" }, // from > to
        new[] { Flag, "--split-symbol", "NVDA", "--split-from", "2024-01-01", "--split-to", "2024-06-21" }, // 120 日超
        new[] { Flag, "--split-symbol", "NVDA", "--split-from", "2026-09-01", "--split-to", "2026-10-01" }, // 未来
        new[] { Flag, "--split-symbol", "NVDA", "--split-from", "2024/05/28", "--split-to", "2024-06-21" }, // 書式
        new[] { Flag, "--probe-order-fee", "123" },                  // 別の検証口の旗
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task 引数不正は照会口を組まずに終了コード2(string[] args)
    {
        var (exitCode, output, query, _) = await Run(null, null, args);

        exitCode.Should().Be(KLineQuotaProbeCommand.ExitUsageOrConfiguration, output);
        query.CreateCount.Should().Be(0, "引数不正では接続しない");
        query.Calls.Should().BeEmpty();
        output.Should().Contain("result=usage-error").And.Contain("exitCode=2");
    }

    [Fact]
    public async Task 構成不正は照会せずに終了コード2で構成由来の値を伏せる()
    {
        var writer = new StringWriter();
        var exitCode = await KLineQuotaProbeCommand.RunAsync(
            [Flag],
            () => throw new InvalidOperationException("Broker:Moomoo:OpenD:RsaPrivateKeyPath '/run/k/rsa-20260930.pem' にファイルがありません（opend-x:23456）"),
            writer,
            cancellationToken: TestContext.Current.CancellationToken,
            sensitiveValues: ["opend-x:23456", "opend-x", "/run/k/rsa-20260930.pem"],
            timeProvider: new FixedTimeProvider(Today));

        exitCode.Should().Be(KLineQuotaProbeCommand.ExitUsageOrConfiguration);
        var output = writer.ToString();
        output.Should().Contain("result=config-error").And.Contain("requests.sent=0")
            .And.Contain("RsaPrivateKeyPath '<伏せ>' にファイルがありません（<伏せ>）");
        output.Should().NotContain("opend-x").And.NotContain("23456").And.NotContain("rsa-2026");
    }

    [Fact]
    public async Task 例外文と応答の文言に構成由来の値が現れても伏せ長い数字の並びは例外文だけ伏せる()
    {
        var query = new FakeQuery
        {
            ThrowOnCall = 1,
            ThrowMessage = "OpenD（相場）への InitConnect が失敗しました（opend-probe.internal:23456）。id 283745190123",
        };

        var (_, output, _, _) = await Run(query, ["opend-probe.internal:23456", "opend-probe.internal"]);

        output.Should().Contain("InitConnect が失敗しました（<伏せ>）。id ****23");
        output.Should().NotContain("opend-probe").And.NotContain("283745190123");
    }

    [Fact]
    public void 旗が在れば形が誤っていても検証口の起動と判定しHostを立てない()
    {
        KLineQuotaProbeCommand.IsRequested([Flag]).Should().BeTrue();
        KLineQuotaProbeCommand.IsRequested([Flag + "=1"]).Should().BeTrue();
        KLineQuotaProbeCommand.IsRequested(["--symbols", "AAPL", Flag]).Should().BeTrue();
        KLineQuotaProbeCommand.IsRequested([]).Should().BeFalse();
        KLineQuotaProbeCommand.IsRequested(["codegen", "write"]).Should().BeFalse("Dockerfile の codegen を横取りしない");
        KLineQuotaProbeCommand.IsRequested(["--probe-order-fee", "1"]).Should().BeFalse("注文費用照会の旗は別の検証口");
    }

    [Fact]
    public void 終了コードと既定値の数値を固定する()
    {
        KLineQuotaProbeCommand.ExitAllSucceeded.Should().Be(0);
        KLineQuotaProbeCommand.ExitQueryFailed.Should().Be(1);
        KLineQuotaProbeCommand.ExitUsageOrConfiguration.Should().Be(2);
        KLineQuotaProbeCommand.DefaultCount.Should().Be(25, "#1117: 既定は直近 25 本");
        KLineQuotaProbeCommand.DefaultSymbols.Should().HaveCountLessThanOrEqualTo(3, "取得枠を消費するため既定は少数");
        KLineQuotaProbeCommand.SplitRehabs.Should().Equal(KLineRehab.None, KLineRehab.Forward, KLineRehab.Backward);
        KLineQuotaProbeCommand.MinRequestInterval.Should().Be(TimeSpan.FromMilliseconds(2500));
    }

    [Fact]
    public void 検証口は書き込み系を持つ型を受け取らずポートのメソッドは読み取りの2つだけ()
    {
        // 🔴 構造で固定する: ポートに発注・取消を足す／検証口が発注の型を受け取る、と落ちる。
        typeof(IKLineQuotaQuery).GetMethods().Select(m => m.Name).Should().BeEquivalentTo(
            nameof(IKLineQuotaQuery.QueryQuotaAsync), nameof(IKLineQuotaQuery.RequestDailyKLinesAsync));
        typeof(IKLineQuotaQuery).GetInterfaces().Should().BeEmpty("他のポートを継承して面を広げない");

        var forbidden = new[] { "IMoomooTradeClient", "IBrokerAdapter", "IMoomooTradeConnection", "IServiceProvider", "IOrderFeeQuery" };
        var parameterTypes = typeof(KLineQuotaProbeCommand)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .SelectMany(m => m.GetParameters())
            .SelectMany(p => p.ParameterType.IsGenericType
                ? p.ParameterType.GetGenericArguments().Append(p.ParameterType)
                : [p.ParameterType])
            .Select(t => t.Name)
            .ToList();
        parameterTypes.Should().Contain(nameof(IKLineQuotaQuery));
        parameterTypes.Should().NotContain(forbidden);
    }

    [Fact]
    public void Programは検証口の旗をHostより前に判定し構成から照会口を組んで終える()
    {
        // 配線（Program.cs の分岐）を固定する。Host を組んでから判定すると、Wolverine・DB・常駐ジョブが起動してしまう。
        var program = File.ReadAllText(ProgramPath());

        var branch = program.IndexOf("if (KLineQuotaProbeCommand.IsRequested(args))", StringComparison.Ordinal);
        var host = program.IndexOf("var builder = WebApplication.CreateBuilder(args);", StringComparison.Ordinal);
        branch.Should().BeGreaterThan(0, "検証口の分岐が在る");
        host.Should().BeGreaterThan(branch, "分岐は Host の組み立てより前");
        var body = program[branch..host];
        body.Should().Contain("KLineQuotaProbeComposition.CreateQuery(klineProbeConfiguration)")
            .And.Contain("sensitiveValues: KLineQuotaProbeComposition.SensitiveValues(klineProbeConfiguration)")
            .And.Contain("Environment.Exit(klineProbeExitCode)")
            .And.NotContain("CreateBuilder(args)", "引数は構成へ渡さない");
    }

    private static string ProgramPath([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "Program.cs"));

    private enum QuotaUnit
    {
        PerSecurity,
        PerRequest,
    }

    private sealed class FixedTimeProvider(DateOnly today) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
    }

    // 偽の照会口。取得枠は Unit に従って動かす。K 線は銘柄と復権区分から決まる値を返す。
    private sealed class FakeQuery : IKLineQuotaQuery, IDisposable
    {
        private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
        private int _used;

        public QuotaUnit Unit { get; init; } = QuotaUnit.PerSecurity;

        public string[] KnownBefore { get; init; } = [];

        public int RecentBars { get; init; } = 30;

        public int BlankEvery { get; init; }

        public KLineRehab? FailRehab { get; init; }

        public int? ThrowOnCall { get; init; }

        public string ThrowMessage { get; init; } = "返信待ちで打ち切り";

        public List<string> Calls { get; } = [];

        public int CreateCount { get; private set; }

        public bool Disposed { get; private set; }

        public IKLineQuotaQuery Created()
        {
            CreateCount++;
            foreach (var s in KnownBefore)
                _consumed.Add(s);
            _used = KnownBefore.Length;
            return this;
        }

        public Task<KLineQuotaReading> QueryQuotaAsync(bool includeDetail, CancellationToken cancellationToken = default)
        {
            Calls.Add(includeDetail ? "quota(detail)" : "quota");
            ThrowIfAsked();
            var details = includeDetail
                ? _consumed.Select(s => new KLineQuotaDetail(s, s == "US.AAPL" ? "Apple" : null, "2026-09-29 10:00:00", 1790000000L)).ToList()
                : [];
            return Task.FromResult(new KLineQuotaReading(0, "", _used, 300 - _used, details));
        }

        public Task<DailyKLineReading> RequestDailyKLinesAsync(DailyKLineProbeRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add($"kline({request.Symbol},{request.Rehab},{request.From:yyyy-MM-dd},{request.To:yyyy-MM-dd})");
            ThrowIfAsked();
            if (FailRehab == request.Rehab)
                return Task.FromResult(new DailyKLineReading(-1, "backward not supported\nline2", [], false));
            var security = "US." + request.Symbol;
            if (Unit == QuotaUnit.PerRequest || _consumed.Add(security))
                _used++;
            _consumed.Add(security);
            var bars = request.From.Year == 2024 ? SplitBars(request.Rehab) : RecentKLines();
            return Task.FromResult(new DailyKLineReading(0, "", bars, false));
        }

        private void ThrowIfAsked()
        {
            if (ThrowOnCall == Calls.Count)
                throw new TimeoutException(ThrowMessage);
        }

        // 2026-07-02 から毎日 1 本（BlankEvery ごとに空白足）。値は通番から決まる。
        private List<ProbeKLine> RecentKLines()
        {
            var list = new List<ProbeKLine>();
            for (var i = 0; i < RecentBars; i++)
            {
                var date = new DateOnly(2026, 7, 2).AddDays(i);
                var blank = BlankEvery > 0 && i % BlankEvery == BlankEvery - 1;
                list.Add(blank
                    ? new ProbeKLine(date.ToString("yyyy-MM-dd"), true, null, null, null, null, null, null)
                    : new ProbeKLine(date.ToString("yyyy-MM-dd"), false, 100 + i, 102 + i, 99 + i, 101 + i, 1000 + i, (101 + i) * (1000 + i)));
            }
            // 末尾に続けて 3 本（直近 3 本の検証用）: 2026-08-08〜08-10（08-11 は空白足）。
            list.RemoveAll(k => string.CompareOrdinal(k.Time, "2026-08-08") >= 0);
            list.Add(new ProbeKLine("2026-08-08", false, 137, 139, 136, 138, 1037, 143106));
            list.Add(new ProbeKLine("2026-08-09", false, 138, 140, 137, 139, 1038, 144282));
            list.Add(new ProbeKLine("2026-08-10", false, 139, 141, 138, 140, 1039, 145460));
            list.Add(new ProbeKLine("2026-08-11", true, null, null, null, null, null, null));
            return list;
        }

        // 2024-06-10 に 10:1 分割。前復権は分割前の価格を 1/10（出来高は調整しない）、後復権は分割後の価格を 10 倍。
        private static List<ProbeKLine> SplitBars(KLineRehab rehab)
        {
            (string Date, double Close, long Volume, bool PreSplit)[] raw =
            [
                ("2024-06-06", 1200, 400, true),
                ("2024-06-07", 1210, 410, true),
                ("2024-06-10", 121, 4000, false),
                ("2024-06-11", 122, 4100, false),
            ];
            return raw.Select(r =>
            {
                var close = rehab switch
                {
                    KLineRehab.Forward when r.PreSplit => r.Close / 10,
                    KLineRehab.Backward when !r.PreSplit => r.Close * 10,
                    _ => r.Close,
                };
                return new ProbeKLine(r.Date, false, close, close, close, close, r.Volume, close * r.Volume);
            }).ToList();
        }

        public void Dispose() => Disposed = true;
    }
}
