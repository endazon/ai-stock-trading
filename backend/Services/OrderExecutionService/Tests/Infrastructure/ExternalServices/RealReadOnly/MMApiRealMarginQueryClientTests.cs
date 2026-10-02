using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moomoo.OpenApi.Pb;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.ExternalServices.RealReadOnly;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, UC-06, ADR-0016 決定3（2026-08-06 追記）, #1000, IADR-0482 決定1・4・5（T-10-2060 / T-10-2061 / T-10-2064 / T-10-2067）:
// 実弾口座の読み取り専用の照会クライアント。
//   - 口座一覧から **Real × Margin × 米国株** を 1 つだけ選び、その口座の**実弾のヘッダ**で TrdGetMarginRatio を送る。
//   - 照会を送ったら成否に関わらず監査を 1 件出す（取引環境 Real・口座は末尾 2 桁だけ）。監査に残せなければ答えを使わない。
//   - 口座を選べない（無い・複数）なら照会を送らない（監査も出ない＝実弾のヘッダは出ていない）。
public class MMApiRealMarginQueryClientTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 14, 0, 0, TimeSpan.Zero);

    private static MMApiRealMarginQueryClient Client(
        FakeMarginQueryOpenD openD, RecordingRealReadOnlyQueryAudit audit, ILogger<MMApiRealMarginQueryClient>? logger = null) =>
        new(new MoomooBrokerOptions("opend", 11111) { ReplyTimeout = Timeout.InfiniteTimeSpan },
            audit,
            new FixedClock(Now),
            logger ?? NullLogger<MMApiRealMarginQueryClient>.Instance,
            openD);

    private static Task<bool?> Query(MMApiRealMarginQueryClient client) =>
        client.GetShortPermitAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken)
            .WaitAsync(Guard, TestContext.Current.CancellationToken);

    // T-10-2060
    [Theory]
    [InlineData(true, RealAccountReadOnlyQueryOutcomes.Permitted)]
    [InlineData(false, RealAccountReadOnlyQueryOutcomes.NotPermitted)]
    public async Task 実弾の信用口座のヘッダで照会し答えと監査を返す(bool permit, string outcome)
    {
        var openD = new FakeMarginQueryOpenD(FakeMarginQueryOpenD.DefaultAccounts(),
            _ => FakeMarginQueryOpenD.Reply(0, "", FakeMarginQueryOpenD.Row("AAPL", permit)));
        var audit = new RecordingRealReadOnlyQueryAudit();
        using var client = Client(openD, audit);

        (await Query(client)).Should().Be(permit);

        var sent = openD.Requests.Should().ContainSingle().Subject;
        sent.C2S.Header.TrdEnv.Should().Be((int)TrdCommon.TrdEnv.TrdEnv_Real, "照会は実弾口座のヘッダで送る（SIMULATE では失敗する）");
        sent.C2S.Header.AccID.Should().Be(FakeMarginQueryOpenD.RealMarginAccId,
            "選ぶのは Real × Margin（SIMULATE の発注口座・実弾の現金口座ではない）");
        sent.C2S.Header.TrdMarket.Should().Be((int)TrdCommon.TrdMarket.TrdMarket_US);

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.TradingEnvironment.Should().Be("Real");
        entry.Operation.Should().Be("GetMarginRatio");
        entry.MaskedTail.Should().Be("****99");
        entry.Symbol.Should().Be("AAPL");
        entry.Market.Should().Be(Market.UnitedStates);
        entry.Outcome.Should().Be(outcome);
        entry.QueriedAt.Should().Be(Now);
        // 🔴 口座 ID の全桁はイベントのどこにも現れない（監査の Detail は record の全量 JSON）。
        entry.ToString().Should().NotContain(FakeMarginQueryOpenD.RealMarginAccId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // T-10-2060（否定形）: 行・欄が無ければ「分からない」と監査に残す。
    [Fact]
    public async Task 当該銘柄の行や欄が無ければ分からないを返し監査にも残す()
    {
        var openD = new FakeMarginQueryOpenD(FakeMarginQueryOpenD.DefaultAccounts(),
            _ => FakeMarginQueryOpenD.Reply(0, "", FakeMarginQueryOpenD.Row("AAPL", null)));
        var audit = new RecordingRealReadOnlyQueryAudit();
        using var client = Client(openD, audit);

        (await Query(client)).Should().BeNull();
        audit.Entries.Should().ContainSingle().Which.Outcome.Should().Be(RealAccountReadOnlyQueryOutcomes.FieldMissing);
    }

    // T-10-2061: 失敗の retMsg に現れた口座 ID は伏せ、失敗も監査に残す。
    [Fact]
    public async Task 照会の失敗は例外で返し_retMsgの口座IDを伏せ_監査に失敗として残す()
    {
        var openD = new FakeMarginQueryOpenD(FakeMarginQueryOpenD.DefaultAccounts(),
            _ => FakeMarginQueryOpenD.Reply(-1, $"acc {FakeMarginQueryOpenD.RealMarginAccId} has no margin data"));
        var audit = new RecordingRealReadOnlyQueryAudit();
        using var client = Client(openD, audit);

        var ex = (await FluentThrow(() => Query(client))).Which;
        ex.RetMsg.Should().Be("acc ****99 has no margin data");
        ex.Message.Should().NotContain(FakeMarginQueryOpenD.RealMarginAccId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        audit.Entries.Should().ContainSingle().Which.Outcome.Should().Be(RealAccountReadOnlyQueryOutcomes.Failed);
    }

    // T-10-2061（否定形）: 実弾の信用口座を 1 つに決められなければ、照会を送らない（実弾のヘッダを出さない）。
    [Fact]
    public async Task 実弾の信用口座が無いか複数なら照会を送らない()
    {
        var none = new FakeMarginQueryOpenD(
            [
                FakeMarginQueryOpenD.Account(FakeMarginQueryOpenD.SimAccId, TrdCommon.TrdEnv.TrdEnv_Simulate,
                    TrdCommon.TrdAccType.TrdAccType_Margin, TrdCommon.TrdMarket.TrdMarket_US),
                FakeMarginQueryOpenD.Account(FakeMarginQueryOpenD.RealCashAccId, TrdCommon.TrdEnv.TrdEnv_Real,
                    TrdCommon.TrdAccType.TrdAccType_Cash, TrdCommon.TrdMarket.TrdMarket_US),
                // 実弾の信用口座だが米国株の取扱が無い。
                FakeMarginQueryOpenD.Account(281230001UL, TrdCommon.TrdEnv.TrdEnv_Real,
                    TrdCommon.TrdAccType.TrdAccType_Margin, TrdCommon.TrdMarket.TrdMarket_JP),
            ],
            _ => FakeMarginQueryOpenD.Reply(0, "", FakeMarginQueryOpenD.Row("AAPL", true)));
        var two = new FakeMarginQueryOpenD(
            [
                FakeMarginQueryOpenD.Account(281230001UL, TrdCommon.TrdEnv.TrdEnv_Real,
                    TrdCommon.TrdAccType.TrdAccType_Margin, TrdCommon.TrdMarket.TrdMarket_US),
                FakeMarginQueryOpenD.Account(281230002UL, TrdCommon.TrdEnv.TrdEnv_Real,
                    TrdCommon.TrdAccType.TrdAccType_Margin, TrdCommon.TrdMarket.TrdMarket_US),
            ],
            _ => FakeMarginQueryOpenD.Reply(0, "", FakeMarginQueryOpenD.Row("AAPL", true)));

        foreach (var openD in new[] { none, two })
        {
            var audit = new RecordingRealReadOnlyQueryAudit();
            using var client = Client(openD, audit);

            await FluentThrow<InvalidOperationException>(() => Query(client));

            openD.Requests.Should().BeEmpty("口座を決められないまま実弾のヘッダで照会しない");
            audit.Entries.Should().BeEmpty("実弾のヘッダは出ていない");
        }
    }

    // T-10-2064: 監査に残せない照会の答えは使わない（例外＝呼び手は「分からない」）。
    [Fact]
    public async Task 監査に残せなければ答えを使わない()
    {
        var openD = new FakeMarginQueryOpenD(FakeMarginQueryOpenD.DefaultAccounts(),
            _ => FakeMarginQueryOpenD.Reply(0, "", FakeMarginQueryOpenD.Row("AAPL", true)));
        var audit = new RecordingRealReadOnlyQueryAudit { Fail = true };
        using var client = Client(openD, audit);

        await FluentThrow<InvalidOperationException>(() => Query(client));
    }

    // 口座の選び方（T-10-2060 の前提）。日本株だけの実弾信用口座・口座 ID 0 も選ばない（独立監査 🟢1・2026-10-02）。
    [Theory]
    [InlineData(TrdCommon.TrdEnv.TrdEnv_Real, TrdCommon.TrdAccType.TrdAccType_Margin, TrdCommon.TrdMarket.TrdMarket_US, 281234599UL, true)]
    [InlineData(TrdCommon.TrdEnv.TrdEnv_Simulate, TrdCommon.TrdAccType.TrdAccType_Margin, TrdCommon.TrdMarket.TrdMarket_US, 281234599UL, false)]
    [InlineData(TrdCommon.TrdEnv.TrdEnv_Real, TrdCommon.TrdAccType.TrdAccType_Cash, TrdCommon.TrdMarket.TrdMarket_US, 281234599UL, false)]
    [InlineData(TrdCommon.TrdEnv.TrdEnv_Real, TrdCommon.TrdAccType.TrdAccType_Margin, TrdCommon.TrdMarket.TrdMarket_JP, 281234599UL, false)]
    [InlineData(TrdCommon.TrdEnv.TrdEnv_Real, TrdCommon.TrdAccType.TrdAccType_Margin, TrdCommon.TrdMarket.TrdMarket_US, 0UL, false)]
    public void 照会に使える口座はRealかつMarginかつ米国株だけ(
        TrdCommon.TrdEnv env, TrdCommon.TrdAccType type, TrdCommon.TrdMarket market, ulong accId, bool expected)
    {
        MMApiRealMarginQueryClient.IsRealMarginUs(FakeMarginQueryOpenD.Account(accId, env, type, market))
            .Should().Be(expected);
    }

    // T-10-2067（#1000・独立監査 🟡2・2026-10-02）: 照会クライアントのログに口座 ID の全桁を出さない（末尾 2 桁だけ）。
    // 接続（開始・完了）・照会の成功・欄の欠落・照会の失敗・監査の失敗・切断の各経路を、記録するロガーで通して検査する。
    // 書式化後の文言だけでなく、構造化ログの state（テンプレート引数）の値も見る（シンクは state を別に保存し得るため）。
    [Fact]
    public async Task 照会クライアントのログに口座IDの全桁を出さない()
    {
        var fullId = FakeMarginQueryOpenD.RealMarginAccId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var logger = new RecordingLogger<MMApiRealMarginQueryClient>();
        var replies = new Queue<TrdGetMarginRatio.Response>(
        [
            FakeMarginQueryOpenD.Reply(0, "", FakeMarginQueryOpenD.Row("AAPL", true)),
            FakeMarginQueryOpenD.Reply(0, "", FakeMarginQueryOpenD.Row("AAPL", null)),
            FakeMarginQueryOpenD.Reply(-1, $"acc {fullId} has no margin data"),
        ]);
        var openD = new FakeMarginQueryOpenD(FakeMarginQueryOpenD.DefaultAccounts(), _ => replies.Dequeue());
        var audit = new RecordingRealReadOnlyQueryAudit();
        using (var client = Client(openD, audit, logger))
        {
            (await Query(client)).Should().BeTrue();
            (await Query(client)).Should().BeNull();
            await FluentThrow(() => Query(client));
            client.OnDisconnect(new Moomoo.OpenApi.MMAPI_Conn(), 1);
        }

        // 監査の失敗の経路（別のクライアントで接続からやり直す）。
        var failingAudit = new RecordingRealReadOnlyQueryAudit { Fail = true };
        var openD2 = new FakeMarginQueryOpenD(FakeMarginQueryOpenD.DefaultAccounts(),
            _ => FakeMarginQueryOpenD.Reply(0, "", FakeMarginQueryOpenD.Row("AAPL", true)));
        using (var client2 = Client(openD2, failingAudit, logger))
        {
            await FluentThrow<InvalidOperationException>(() => Query(client2));
        }

        var records = logger.Records;
        foreach (var record in records)
        {
            record.Message.Should().NotContain(fullId, "ログの文言に口座 ID の全桁を出さない");
            foreach (var value in record.StateValues)
                value.Should().NotContain(fullId, "構造化ログの引数に口座 ID の全桁を出さない");
        }
        records.Should().Contain(r => r.Message.Contains("接続完了", StringComparison.Ordinal) && r.Message.Contains("****99", StringComparison.Ordinal),
            "接続完了のログは口座を末尾 2 桁だけで示す（ログが出ていなければこの検査は空振りになる）");
    }

    private static async Task<AwesomeAssertions.Specialized.ExceptionAssertions<MoomooTradeRequestException>> FluentThrow(
        Func<Task> act) => await act.Should().ThrowAsync<MoomooTradeRequestException>();

    private static async Task FluentThrow<T>(Func<Task> act) where T : Exception => await act.Should().ThrowAsync<T>();

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<(string Message, IReadOnlyList<string> StateValues)> _records = [];

        public IReadOnlyList<(string Message, IReadOnlyList<string> StateValues)> Records
        {
            get { lock (_records) return _records.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.Select(p => Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).ToList()
                : [];
            lock (_records) _records.Add((formatter(state, exception), values));
        }
    }
}
