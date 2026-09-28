using System.Reflection;
using AwesomeAssertions;
using OrderExecutionService.Features.OrderExecution.ProbeOrderFee;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-11, FR-16, ADR-0016 決定15, #1086, IADR-0300（2026-09-29 追記）: 注文費用照会の 1 回実行の検証口。
// 受け入れ基準: 1 回だけ呼ぶ／書き込み系 API を呼べない構造／秘密を出さない／失敗時の出力と終了コード／引数不正。
public class OrderFeeProbeCommandTests
{
    private const string Flag = OrderFeeProbeCommand.Flag;

    private static readonly OrderFeeQueryResult RepliedWithFees = new(
        OrderFeeQueryOutcome.Replied, "****08", "EX-1", "US", 11, 0, "",
        [new OrderFeeEntry("EX-1", 1.99, [new OrderFeeItem("Commission", 0.99), new OrderFeeItem("Platform Fee", 1.0)])]);

    private static async Task<(int ExitCode, string Output, CountingQuery Query)> Run(
        OrderFeeQueryResult? result = null, Exception? throws = null, params string[] args)
    {
        var query = new CountingQuery(result ?? RepliedWithFees, throws);
        var writer = new StringWriter();
        var exitCode = await OrderFeeProbeCommand.RunAsync(
            args.Length == 0 ? [Flag, "123456789"] : args, () => query.Created(), writer,
            cancellationToken: TestContext.Current.CancellationToken);
        return (exitCode, writer.ToString(), query);
    }

    [Fact]
    public async Task 照会成功で費用項目が返れば項目名と値を出して終了コード0()
    {
        var (exitCode, output, query) = await Run();

        exitCode.Should().Be(OrderFeeProbeCommand.ExitFeesReturned);
        query.Calls.Should().Be(1);
        query.OrderIds.Should().Equal("123456789");
        output.Should().Contain("result=fees-returned")
            .And.Contain("retType=0")
            .And.Contain("fee[0].feeAmount=1.99")
            .And.Contain("fee[0].item[0].title=Commission")
            .And.Contain("fee[0].item[0].value=0.99")
            .And.Contain("fee[0].item[1].title=Platform Fee")
            .And.Contain("getOrderFee.sent=yes")
            .And.Contain("account=SIMULATE(****08)")
            .And.Contain("exitCode=0");
        query.Disposed.Should().BeTrue("接続は 1 回実行の後に閉じる");
    }

    [Fact]
    public async Task 照会成功だが費用が空なら値を返さないとして終了コード3()
    {
        var empty = RepliedWithFees with { Fees = [] };

        var (exitCode, output, query) = await Run(empty);

        exitCode.Should().Be(OrderFeeProbeCommand.ExitNoFees);
        query.Calls.Should().Be(1);
        output.Should().Contain("result=no-fees").And.Contain("fees.orders=0 fees.items=0");
    }

    [Fact]
    public async Task 非成功の応答は再試行せずretTypeとretMsgを出して終了コード1()
    {
        var failed = RepliedWithFees with
        {
            Outcome = OrderFeeQueryOutcome.Failed,
            RetType = -1,
            RetMsg = "not supported in simulate\nline2",
            Fees = [],
        };

        var (exitCode, output, query) = await Run(failed);

        exitCode.Should().Be(OrderFeeProbeCommand.ExitQueryFailed);
        query.Calls.Should().Be(1, "失敗しても撃ち直さない（頻度制限を消費しない）");
        output.Should().Contain("result=failed")
            .And.Contain("retType=-1")
            .And.Contain("retMsg=not supported in simulate line2", "retMsg は 1 行に畳む")
            .And.Contain("exitCode=1");
    }

    [Fact]
    public async Task 照会が例外でも再試行せず型と文言を出して終了コード1()
    {
        var (exitCode, output, query) = await Run(throws: new TimeoutException("返信待ちで打ち切り"));

        exitCode.Should().Be(OrderFeeProbeCommand.ExitQueryFailed);
        query.Calls.Should().Be(1, "例外でも撃ち直さない");
        output.Should().Contain("result=error")
            .And.Contain("error[0].type=TimeoutException")
            .And.Contain("error[0].message=返信待ちで打ち切り")
            .And.Contain("getOrderFee.sent=unknown");
        query.Disposed.Should().BeTrue();
    }

    [Theory]
    [InlineData(OrderFeeQueryOutcome.OrderNotFound, "result=order-not-found")]
    [InlineData(OrderFeeQueryOutcome.OrderIdExMissing, "result=order-id-ex-missing")]
    public async Task 照会を撃てなかった結末は失敗として終了コード1(OrderFeeQueryOutcome outcome, string expected)
    {
        var notSent = RepliedWithFees with { Outcome = outcome, OrderIdEx = null, RetType = null, RetMsg = null, Fees = [] };

        var (exitCode, output, _) = await Run(notSent);

        exitCode.Should().Be(OrderFeeProbeCommand.ExitQueryFailed);
        output.Should().Contain(expected).And.Contain("getOrderFee.sent=no");
    }

    public static TheoryData<string[]> InvalidArguments => new()
    {
        new[] { Flag },                         // 値が無い
        new[] { Flag, "123", "456" },           // 余分な引数
        new[] { Flag, "123", Flag, "456" },     // 重複
        new[] { Flag, "--live" },               // 別の旗の取り違え
        new[] { Flag, "12 34" },                // 空白
        new[] { Flag, "12;rm" },                // 記号
        new[] { Flag, "" },                     // 空
        new[] { "--other", Flag },              // 旗の位置
        new[] { Flag + "=" },                   // = 付きで値が空
        new[] { Flag + "=123", "456" },         // = 付きに余分な引数
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task 引数不正は照会口を組まずに終了コード2(string[] args)
    {
        var (exitCode, output, query) = await Run(null, null, args);

        exitCode.Should().Be(OrderFeeProbeCommand.ExitUsageOrConfiguration);
        query.CreateCount.Should().Be(0, "引数不正では接続しない");
        query.Calls.Should().Be(0);
        output.Should().Contain("result=usage-error").And.Contain("exitCode=2");
    }

    [Fact]
    public void 旗が在れば形が誤っていても検証口の起動と判定しHostを立てない()
    {
        OrderFeeProbeCommand.IsRequested([Flag]).Should().BeTrue();
        OrderFeeProbeCommand.IsRequested([Flag + "=1"]).Should().BeTrue();
        OrderFeeProbeCommand.IsRequested([]).Should().BeFalse();
        OrderFeeProbeCommand.IsRequested(["codegen", "write"]).Should().BeFalse("Dockerfile の codegen を横取りしない");
    }

    [Fact]
    public async Task イコール付きの形も同じ値として受け付ける()
    {
        // claude-review 🟢: IsRequested が `=` 形を起動と判定する以上、解釈も揃える（非対称を作らない）。
        var (exitCode, _, query) = await Run(null, null, Flag + "=123456789");

        exitCode.Should().Be(OrderFeeProbeCommand.ExitFeesReturned);
        query.OrderIds.Should().Equal("123456789");
    }

    [Fact]
    public async Task 出力の最終段で例外文を含むすべての行を照会口の伏せに通す()
    {
        // #1086 AI レビュー 🔴: 注文一覧の照会の失敗は生の retMsg（口座 ID を含み得る）を例外文に載せる。
        // 検証口は照会口が伏せの口を持てば、例外文も含めて 1 行ずつ通してから書く。
        var query = new RedactingThrowingQuery("283745190123",
            new InvalidOperationException("moomoo GetHistoryOrderList が失敗しました（retType=-1）: acc 283745190123 denied"));
        var writer = new StringWriter();

        var exitCode = await OrderFeeProbeCommand.RunAsync(
            [Flag, "123"], () => query, writer, cancellationToken: TestContext.Current.CancellationToken);

        exitCode.Should().Be(OrderFeeProbeCommand.ExitQueryFailed);
        writer.ToString().Should().NotContain("283745190123").And.Contain("acc ****23 denied");
    }

    [Fact]
    public void 終了コードの数値を固定する()
    {
        // 別文脈監査: 定数どうしの比較だけでは値の入れ替わり（3 を 0 にする等）を捕まえられない。手順書の表と同じ数値で固定する。
        OrderFeeProbeCommand.ExitFeesReturned.Should().Be(0);
        OrderFeeProbeCommand.ExitQueryFailed.Should().Be(1);
        OrderFeeProbeCommand.ExitUsageOrConfiguration.Should().Be(2);
        OrderFeeProbeCommand.ExitNoFees.Should().Be(3);
    }

    [Fact]
    public async Task 構成由来の伏せる値は照会口の生成前の構成不正の文でも伏せる()
    {
        var writer = new StringWriter();
        var exitCode = await OrderFeeProbeCommand.RunAsync(
            [Flag, "123"],
            () => throw new InvalidOperationException("OpenD への InitConnect が失敗しました（opend-x:23456）。鍵 /run/k/rsa.pem"),
            writer,
            cancellationToken: TestContext.Current.CancellationToken,
            sensitiveValues: ["opend-x:23456", "opend-x", "/run/k/rsa.pem"]);

        exitCode.Should().Be(2);
        writer.ToString().Should().NotContain("opend-x").And.NotContain("23456").And.NotContain("/run/k/rsa.pem")
            .And.Contain("error[0].type=InvalidOperationException")
            .And.Contain("InitConnect が失敗しました（<伏せ>）。鍵 <伏せ>");
    }

    [Theory]
    [InlineData("acc 283745190123 denied", "acc ****23 denied")]
    [InlineData("retType=-100", "retType=-100")]
    [InlineData("code 12345", "code 12345")]
    public void 例外文の6桁以上の数字の並びは末尾2桁以外を伏せる(string input, string expected) =>
        OrderFeeProbeCommand.MaskLongDigitRuns(input).Should().Be(expected);

    [Fact]
    public async Task 構成不正は照会せずに終了コード2()
    {
        var writer = new StringWriter();
        var exitCode = await OrderFeeProbeCommand.RunAsync(
            [Flag, "123"], () => throw new InvalidOperationException("moomoo の SIMULATE 階層でだけ動きます"), writer,
            cancellationToken: TestContext.Current.CancellationToken);

        exitCode.Should().Be(OrderFeeProbeCommand.ExitUsageOrConfiguration);
        writer.ToString().Should().Contain("result=config-error").And.Contain("getOrderFee.sent=no");
    }

    [Fact]
    public void 検証口は書き込み系を持つ型を受け取らずポートのメソッドは1つだけ()
    {
        // 🔴 構造で固定する: ポートに発注・取消を足す／検証口が IMoomooTradeClient や IBrokerAdapter を受け取る、と落ちる。
        typeof(IOrderFeeQuery).GetMethods().Select(m => m.Name).Should().Equal(nameof(IOrderFeeQuery.QueryOrderFeeAsync));
        typeof(IOrderFeeQuery).GetInterfaces().Should().BeEmpty("他のポートを継承して面を広げない");

        var forbidden = new[] { "IMoomooTradeClient", "IBrokerAdapter", "IMoomooTradeConnection", "IServiceProvider" };
        var parameterTypes = typeof(OrderFeeProbeCommand)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .SelectMany(m => m.GetParameters())
            .SelectMany(p => p.ParameterType.IsGenericType
                ? p.ParameterType.GetGenericArguments().Append(p.ParameterType)
                : [p.ParameterType])
            .Select(t => t.Name)
            .ToList();
        parameterTypes.Should().Contain(nameof(IOrderFeeQuery));
        parameterTypes.Should().NotContain(forbidden);
    }

    private sealed class CountingQuery(OrderFeeQueryResult result, Exception? throws) : IOrderFeeQuery, IDisposable
    {
        public int Calls { get; private set; }

        public int CreateCount { get; private set; }

        public bool Disposed { get; private set; }

        public List<string> OrderIds { get; } = [];

        public IOrderFeeQuery Created()
        {
            CreateCount++;
            return this;
        }

        public Task<OrderFeeQueryResult> QueryOrderFeeAsync(string orderId, CancellationToken cancellationToken = default)
        {
            Calls++;
            OrderIds.Add(orderId);
            return throws is null ? Task.FromResult(result) : Task.FromException<OrderFeeQueryResult>(throws);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class RedactingThrowingQuery(string secret, Exception throws) : IOrderFeeQuery, IProbeOutputRedactor
    {
        public Task<OrderFeeQueryResult> QueryOrderFeeAsync(string orderId, CancellationToken cancellationToken = default) =>
            Task.FromException<OrderFeeQueryResult>(throws);

        public string Redact(string text) => text.Replace(secret, "****" + secret[^2..], StringComparison.Ordinal);
    }
}
