using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Features.OrderExecution.ObserveBrokerPositions;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 FR-10, UC-02, ADR-0040 決定1（S1）, #1114, IADR-0344 追記(18):
// moomoo はエントリーの発注応答を Accepted・約定 0 で返し、約定追跡（既定 30 秒）が発注記録を Filled へ直すまでの窓がある。
// その窓でガード（30 秒）か建玉観測の常駐（600 秒）が走ると、自分の新規建て（稼働 PoC では AMZN 970 株）を
// 帰属不明の建玉として誤って知らせていた（誤警報のみ。売買・状態の変更は起きない）。
//
// 是正は検知の側だけ: 確定前の S1 行のうち、エントリーの記録が**非終端**のものは max(約定数量, 行の数量) まで説明が付く。
// 観測を数え続けてよいかの門（ReconcileShares）は従来どおり約定数量で数える（#820 の 7 巡目監査 BLK-7-1）。
//
// 検知の本体（ProtectiveStopNetting.DetectUnattributedPositions）の呼び出し元は 2 つあり、両方で確かめる:
//   - ガードの経路: ProtectiveStopGuard.RunOnceAsync（巡回の先頭で終端の記録を確定してから検知する）
//   - 常駐の経路: UnattributedPositionDetector.Detect（建玉観測の常駐が巡回ごとに呼ぶ。確定を通さない）
public class UnattributedEntryFillWindowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 7, 44, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = T0;
    }

    private sealed class GuardBroker : IBrokerAdapter, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [];

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("本テストは発注しない");

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) =>
            throw new NotSupportedException("本テストは取り消さない");

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Positions);
    }

    public enum Path
    {
        Guard,
        Resident,
    }

    private sealed class Fixture
    {
        public FakeClock Clock { get; } = new();

        public GuardBroker Broker { get; } = new();

        public InMemoryProtectiveStopOrderStore Stops { get; } = new();

        public InMemoryExecutedOrderStore Store { get; } = new();

        /// <summary>指定の経路で 1 巡回ぶん検知し、出た帰属不明の通知を返す。</summary>
        public async Task<IReadOnlyList<SoftwareStopExecuted>> DetectAsync(
            Path path, params BrokerPositionSnapshot[] positions)
        {
            Broker.Positions = positions;
            IReadOnlyList<object> events = path switch
            {
                Path.Guard => (await new ProtectiveStopGuard(
                    Broker, Broker, Stops, Store, new InMemoryOrderReservationStore(), Clock).RunOnceAsync(50)).Events,
                _ => new UnattributedPositionDetector(Stops, Store, Clock, batchSize: 50).Detect(positions),
            };
            return events.OfType<SoftwareStopExecuted>()
                .Where(e => e.Outcome == SoftwareStopOutcome.UnattributedPosition)
                .ToList();
        }

        /// <summary>武装済み・未到達・確定前（RemainingProtected=null）の S1 行。</summary>
        public ProtectiveStopOrder Arm(string symbol, int quantity, TradeSide entrySide = TradeSide.Buy, DateTimeOffset? createdAt = null)
        {
            var id = Guid.NewGuid();
            var at = createdAt ?? T0;
            var row = new ProtectiveStopOrder(
                id, ProtectiveStopIds.SoftwareStopId(id), string.Empty, symbol, Market.UnitedStates, entrySide,
                ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, 245.14m, 1m, 0, ProtectiveStopState.Active,
                at, at, StopLossExecutionMethod.SoftwareStop);
            Stops.Save(row);
            return row;
        }

        /// <summary>S0（ブローカー側逆指値）の未確定の行。逆指値は受理済み（StopOrderId あり）。主張は行の数量。</summary>
        public ProtectiveStopOrder ArmS0(string symbol, int quantity, DateTimeOffset? createdAt = null)
        {
            var id = Guid.NewGuid();
            var at = createdAt ?? T0;
            var row = new ProtectiveStopOrder(
                id, ProtectiveStopIds.StopDecisionId(id, 1), $"stop-{id:N}", symbol, Market.UnitedStates, TradeSide.Buy,
                ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, 245.14m, 1m, 1, ProtectiveStopState.Active,
                at, at, StopLossExecutionMethod.BrokerStopOrder);
            Stops.Save(row);
            return row;
        }

        /// <summary>エントリーの発注記録（約定追跡が書き換える側）。</summary>
        public void Entry(ProtectiveStopOrder row, OrderStatus status, int filled) =>
            Store.Save(new ExecutionRecord(
                row.EntryDecisionId, $"entry-{row.EntryDecisionId:N}", row.Symbol, row.Market, row.EntrySide,
                ProductType.Cash, PositionEffect.Open, row.Quantity, 250m, filled, filled > 0 ? 250m : 0m, status, 0m, T0));

        /// <summary>約定追跡が記録を書き換える（本番の約定追跡と同じ口）。</summary>
        public void Track(ProtectiveStopOrder row, OrderStatus status, int filled) =>
            Store.UpdateOutcome($"entry-{row.EntryDecisionId:N}", status, filled, filled > 0 ? 250m : 0m, 0m, Clock.UtcNow)
                .Should().BeTrue("記録は在る");
    }

    private static BrokerPositionSnapshot Position(string symbol, int qty) => new(symbol, Market.UnitedStates, qty, 250m);

    // ---- T-10-1776: 窓の増える側（本件）。約定済みの自分の新規建てを帰属不明と読まない ----

    [Theory]
    [InlineData(Path.Guard)]
    [InlineData(Path.Resident)]
    public async Task T_10_1776_エントリーの記録がAcceptedで約定0のあいだ純額の自分の建玉を帰属不明と知らせない(Path path)
    {
        // T-10-1776, FR-10, #1114, IADR-0344 追記(18): 稼働 PoC の配置（AMZN 970 株。発注応答は Accepted・約定 0）。
        var f = new Fixture();
        var row = f.Arm("AMZN", 970);
        f.Entry(row, OrderStatus.Accepted, filled: 0);

        var emitted = await f.DetectAsync(path, Position("AMZN", 970));

        emitted.Should().BeEmpty("約定追跡が記録を直す前の窓でも、行の数量までは自分の新規建てで説明が付く");
        var kept = f.Stops.Find(row.EntryDecisionId)!;
        kept.UnattributedNotifiedQuantity.Should().BeNull("鳴らさない巡回で通知済みの印を書かない");
        kept.State.Should().Be(ProtectiveStopState.Active);
        kept.RemainingProtected.Should().BeNull("非終端の記録では確定しない（主張は 0 のまま）");
    }

    // ---- T-10-1777: 約定追跡の反映後も鳴らない（対照） ----

    [Theory]
    [InlineData(Path.Guard)]
    [InlineData(Path.Resident)]
    public async Task T_10_1777_約定追跡が記録をFilledへ直した後も帰属不明と知らせない(Path path)
    {
        // T-10-1777, FR-10, #1114: 窓の前（Accepted・約定 0）→ 窓の後（Filled 970）。ガードの経路では確定もする。
        var f = new Fixture();
        var row = f.Arm("AMZN", 970);
        f.Entry(row, OrderStatus.Accepted, filled: 0);
        (await f.DetectAsync(path, Position("AMZN", 970))).Should().BeEmpty();

        f.Clock.UtcNow = T0.AddSeconds(30);
        f.Track(row, OrderStatus.Filled, filled: 970);

        (await f.DetectAsync(path, Position("AMZN", 970))).Should().BeEmpty();
        if (path == Path.Guard)
            f.Stops.Find(row.EntryDecisionId)!.RemainingProtected.Should().Be(970, "ガードは巡回の先頭で確定する");

        f.Clock.UtcNow = T0.AddSeconds(60);
        (await f.DetectAsync(path, Position("AMZN", 970))).Should().BeEmpty("確定後の巡回でも鳴らない");
    }

    // ---- T-10-1778: 窓のあいだでも他人の建玉は従来どおり鳴る（ロング・ショート） ----

    [Theory]
    [InlineData(Path.Guard, TradeSide.Buy)]
    [InlineData(Path.Resident, TradeSide.Buy)]
    [InlineData(Path.Guard, TradeSide.Sell)]
    [InlineData(Path.Resident, TradeSide.Sell)]
    public async Task T_10_1778_窓のあいだに他人の建玉が混ざれば行の数量を超えた分で知らせる(Path path, TradeSide entrySide)
    {
        // T-10-1778, FR-10, #1114, IADR-0344 追記(18): 差し引きは「その行がこれから約定し得る株数」まで。
        // 純額 970＋30 の 30 株は、どの記録も説明しない（S2・人手の建玉）。
        var f = new Fixture();
        var row = f.Arm("AMZN", 970, entrySide);
        f.Entry(row, OrderStatus.Accepted, filled: 0);
        var sign = entrySide == TradeSide.Buy ? 1 : -1;

        var emitted = await f.DetectAsync(path, Position("AMZN", sign * 1_000));

        emitted.Should().ContainSingle("行の数量を超えた建玉は帰属不明である")
            .Which.Quantity.Should().Be(30);
        emitted.Single().EntryDecisionId.Should().Be(row.EntryDecisionId);
        f.Stops.Find(row.EntryDecisionId)!.UnattributedNotifiedQuantity.Should().Be(30);
    }

    [Theory]
    [InlineData(Path.Guard)]
    [InlineData(Path.Resident)]
    public async Task T_10_1778_別銘柄の未確定エントリーの見込みで他人の建玉を隠さない(Path path)
    {
        // T-10-1778, FR-10, #1114, IADR-0344 追記(18)（#1115 監査 F1・M10）: 差し引きは群（銘柄・市場・方向）の行だけで数える。
        // AMZN の未確定エントリー（970 株・受付・約定 0）の見込みを META の群へ持ち込むと、META の他人の 50 株が隠れる。
        var f = new Fixture();
        var amzn = f.Arm("AMZN", 970);
        f.Entry(amzn, OrderStatus.Accepted, filled: 0);
        var meta = f.Arm("META", 100, createdAt: T0.AddMinutes(-5));
        f.Entry(meta, OrderStatus.Filled, filled: 100);

        var emitted = await f.DetectAsync(path, Position("AMZN", 970), Position("META", 150));

        emitted.Should().ContainSingle("META の 50 株はどの記録も主張していない（AMZN の見込みは META を説明しない）")
            .Which.Quantity.Should().Be(50);
        emitted.Single().Symbol.Should().Be("META");
        emitted.Single().EntryDecisionId.Should().Be(meta.EntryDecisionId);
    }

    [Theory]
    [InlineData(Path.Guard)]
    [InlineData(Path.Resident)]
    public async Task T_10_1778_S0の記録が非終端で混在しても見込みとして二重に引かず他人の建玉を知らせる(Path path)
    {
        // T-10-1778, FR-10, #1114, IADR-0344 追記(18)（#1115 監査 F1・M5）: 見込みは S1 の行だけ。
        // S0 の未確定の行は主張（ProtectedQuantity＝行の数量）で既に引かれているため、見込みでも引くと二重になる。
        // 純額 970（S1 の窓）＋100（S0）＋30（他人）＝1,100 のうち 30 株を知らせる。
        var f = new Fixture();
        var s0 = f.ArmS0("AMZN", 100, createdAt: T0.AddMinutes(-5));
        f.Entry(s0, OrderStatus.Accepted, filled: 0);
        var s1 = f.Arm("AMZN", 970);
        f.Entry(s1, OrderStatus.Accepted, filled: 0);

        var emitted = await f.DetectAsync(path, Position("AMZN", 1_100));

        emitted.Should().ContainSingle("S0 の行の株数は主張で一度だけ引く")
            .Which.Quantity.Should().Be(30);
        emitted.Single().EntryDecisionId.Should().Be(s1.EntryDecisionId, "群の代表は S1 行");
    }

    // ---- T-10-1779: 窓の減る側。約定 0 のまま終端したエントリーの見込みは消える ----

    [Theory]
    [InlineData(Path.Guard, OrderStatus.Rejected)]
    [InlineData(Path.Resident, OrderStatus.Rejected)]
    [InlineData(Path.Guard, OrderStatus.Cancelled)]
    [InlineData(Path.Resident, OrderStatus.Cancelled)]
    public async Task T_10_1779_エントリーが約定0のまま終端なら純額の建玉を帰属不明と知らせる(Path path, OrderStatus terminal)
    {
        // T-10-1779, FR-10, #1114, IADR-0344 追記(18): 終端の記録はこれ以上約定しない。
        // 🔴 常駐の経路は確定を通さないため、行は未確定（RemainingProtected=null）のまま検知に来る——
        // 終端かどうかを見ずに行の数量で説明すると、他人の 970 株を隠し続ける。
        var f = new Fixture();
        var row = f.Arm("AMZN", 970);
        f.Entry(row, terminal, filled: 0);

        var emitted = await f.DetectAsync(path, Position("AMZN", 970));

        emitted.Should().ContainSingle("自分のエントリーは 1 株も約定していない＝純額の 970 株はどの記録も主張していない")
            .Which.Quantity.Should().Be(970);
    }

    [Theory]
    [InlineData(Path.Guard)]
    [InlineData(Path.Resident)]
    public async Task T_10_1779_発注記録が無い確定前の記録は行の数量で差し引かず純額の建玉を知らせる(Path path)
    {
        // T-10-1779, FR-10, #1114, IADR-0344 追記(18)（#1115 監査 F1・M3）: 記録が無い行（届いたか不明）は見込みを数えない。
        // 行の数量で差し引くと、到達しない限り閉じない行が他人の建玉を期限なく隠す。
        var f = new Fixture();
        var row = f.Arm("AMZN", 970);

        var emitted = await f.DetectAsync(path, Position("AMZN", 970));

        emitted.Should().ContainSingle("記録の無い行は純額の 970 株を説明しない")
            .Which.Quantity.Should().Be(970);
        emitted.Single().EntryDecisionId.Should().Be(row.EntryDecisionId);
    }

    [Theory]
    [InlineData(Path.Guard)]
    [InlineData(Path.Resident)]
    public async Task T_10_1779_窓のあいだは通知済みの印が消えて黙り記録が終端になった巡回で再び知らせる(Path path)
    {
        // T-10-1779, FR-10, #1114, IADR-0344 追記(18)（#1115 監査 F2・受容した残余）: 他人の 30 株を通知済みのところへ
        // 新しいエントリー（受付・約定 0）が来ると、その見込み 970 株が 30 株を覆い、旧行の通知済みの印は null に戻る（黙る）。
        // 記録が終端になった巡回で見込みが消え、30 株を改めて知らせる。
        var f = new Fixture();
        var old = f.Arm("AMZN", 100, createdAt: T0.AddMinutes(-10));
        f.Entry(old, OrderStatus.Filled, filled: 100);

        (await f.DetectAsync(path, Position("AMZN", 130))).Should().ContainSingle()
            .Which.Quantity.Should().Be(30);
        f.Stops.Find(old.EntryDecisionId)!.UnattributedNotifiedQuantity.Should().Be(30);

        // 窓: 新しいエントリーが受付・約定 0（純額はまだ 130）。
        f.Clock.UtcNow = T0.AddSeconds(30);
        var fresh = f.Arm("AMZN", 970, createdAt: T0.AddSeconds(30));
        f.Entry(fresh, OrderStatus.Accepted, filled: 0);

        (await f.DetectAsync(path, Position("AMZN", 130))).Should().BeEmpty("見込み 970 株が他人の 30 株を覆う（受容した残余）");
        f.Stops.Find(old.EntryDecisionId)!.UnattributedNotifiedQuantity.Should().BeNull("窓で印はリセットされる");
        f.Stops.Find(fresh.EntryDecisionId)!.UnattributedNotifiedQuantity.Should().BeNull();

        // 記録が約定 0 のまま終端（取消）: 見込みが消え、30 株を再び知らせる（代表は新しい行）。
        f.Clock.UtcNow = T0.AddSeconds(60);
        f.Track(fresh, OrderStatus.Cancelled, filled: 0);

        var renotified = await f.DetectAsync(path, Position("AMZN", 130));
        renotified.Should().ContainSingle("終端になった巡回で他人の 30 株を改めて知らせる")
            .Which.Quantity.Should().Be(30);
        renotified.Single().EntryDecisionId.Should().Be(fresh.EntryDecisionId);
    }

    // ---- T-10-1780: 一部約定・複数の未確定エントリー ----

    [Theory]
    [InlineData(Path.Guard)]
    [InlineData(Path.Resident)]
    public async Task T_10_1780_一部約定と複数の未確定エントリーは非終端なら行の数量まで終端なら約定数量まで説明が付く(Path path)
    {
        // T-10-1780, FR-10, #1114, IADR-0344 追記(18): 非終端は max(約定数量, 行の数量)、終端は約定数量で合計する。
        var f = new Fixture();
        var first = f.Arm("AMZN", 970, createdAt: T0.AddMinutes(-5));
        f.Entry(first, OrderStatus.PartiallyFilled, filled: 500);
        var second = f.Arm("AMZN", 100);
        f.Entry(second, OrderStatus.Accepted, filled: 0);

        // 非終端 2 件: 970 + 100 まで説明が付く（記録の約定は 500 だが、ブローカーは 1,070 株まで約定し得る）。
        (await f.DetectAsync(path, Position("AMZN", 1_070))).Should().BeEmpty("非終端の 2 件で 1,070 株まで説明が付く");

        // 1 件目が一部約定のまま終端（取消）: 1 件目は 500 株しか説明しない。純額 1,070 なら 470 株が帰属不明。
        f.Clock.UtcNow = T0.AddSeconds(30);
        f.Track(first, OrderStatus.Cancelled, filled: 500);
        var emitted = await f.DetectAsync(path, Position("AMZN", 1_070));

        emitted.Should().ContainSingle("終端した一部約定の残りはもう約定しない")
            .Which.Quantity.Should().Be(470);
        emitted.Single().EntryDecisionId.Should().Be(second.EntryDecisionId, "群の代表は作成が最も新しい S1 行");
    }

    [Theory]
    [InlineData(Path.Guard)]
    [InlineData(Path.Resident)]
    public async Task T_10_1780_一部約定の非終端の記録は残りの株数までしか見込まず他人の建玉を知らせる(Path path)
    {
        // T-10-1780, FR-10, #1114, IADR-0344 追記(18)（#1115 監査 F1・M9）: 見込みは「行の数量 − 約定数量」。
        // 970 株のうち 500 株約定・非終端なら、約定 500（SharesAccountedElsewhere）＋見込み 470 で 970 株まで説明が付く。
        // 見込みを行の数量（970）で数えると 1,470 株まで説明が付き、純額 1,000 の他人の 30 株が隠れる。
        var f = new Fixture();
        var row = f.Arm("AMZN", 970);
        f.Entry(row, OrderStatus.PartiallyFilled, filled: 500);

        var emitted = await f.DetectAsync(path, Position("AMZN", 1_000));

        emitted.Should().ContainSingle("約定済み 500 株を見込みでも数えると二重になる")
            .Which.Quantity.Should().Be(30);
        emitted.Single().EntryDecisionId.Should().Be(row.EntryDecisionId);
    }

    // ---- T-10-1781: 観測を数え続けてよいかの門（ReconcileShares）は約定数量で数える（BLK-7-1）を変えない ----

    [Fact]
    public async Task T_10_1781_観測の門は非終端で約定0の新規エントリーを数えず一巡回だけ過少な照会で行を失わない()
    {
        // T-10-1781, FR-10, #1114, IADR-0344 追記(18)・追記(7)（T-10-410 と同型の配置を本件の数量で）:
        // 検知の差し引き（行の数量まで）を門へ持ち込むと、1 株も約定していない新規エントリー 970 株が
        // 「超過はまだ消えていない」の根拠になり、1 巡回だけ過少に返った照会の観測が確定して、実在する 10 株の行が主張を失う。
        var f = new Fixture();
        var covered = f.Arm("AMZN", 10, createdAt: T0.AddHours(-2));
        f.Entry(covered, OrderStatus.Filled, filled: 10);
        var fresh = f.Arm("AMZN", 970);
        f.Entry(fresh, OrderStatus.Accepted, filled: 0);

        // 巡回 1: 建玉照会が 1 巡回だけ過少に返る（実在する 10 株が 0 に見える）。
        var cycle1 = await f.DetectAsync(Path.Guard);
        cycle1.Should().BeEmpty();

        // 巡回 2・3: 照会が回復する（新規エントリーはまだ約定していない）。
        f.Clock.UtcNow = T0.AddSeconds(30);
        var cycle2 = await f.DetectAsync(Path.Guard, Position("AMZN", 10));
        f.Clock.UtcNow = T0.AddSeconds(60);
        var cycle3 = await f.DetectAsync(Path.Guard, Position("AMZN", 10));

        var kept = f.Stops.Find(covered.EntryDecisionId)!;
        kept.RemainingProtected.Should().Be(10, "建玉 10 株が実在する行の帳簿を、未約定の新規エントリーの数量で消してはならない");
        kept.State.Should().Be(ProtectiveStopState.Active);
        kept.EffectiveProtectedQuantity.Should().Be(10, "観測は失効し、到達すれば 10 株を決済できる");
        cycle2.Concat(cycle3).Should().BeEmpty("主張どおりの建玉なので帰属不明も出ない");
    }
}
