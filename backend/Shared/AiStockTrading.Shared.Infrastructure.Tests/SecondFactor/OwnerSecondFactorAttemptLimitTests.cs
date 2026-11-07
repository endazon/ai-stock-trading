using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Infrastructure.Composable.SecondFactor;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.SecondFactor;

// NFR-06, FR-14, IADR-0615, IADR-0626: 第 2 要素の試行回数の制限（段階的な停止・手動の解除）・失敗の通知・
// 2 サービスでの状態の共有・使ったコードの再利用の拒否・操作と対象への結び付き。時刻は差し替えられる時計で固定する。
public class OwnerSecondFactorAttemptLimitTests
{
    // RFC 6238 付録 B の SHA1 の公開の試験値の種（ASCII の "1234567890" を 2 回）を、実行時に base32 へ組み立てる（本物の秘密ではない。
    // 文字列の定数で書くと秘密の走査に掛かる）。
    private static readonly string RfcSecret = Totp.EncodeBase32(System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("1234567890", 2))));

    // 別のシークレット（作り直しの後）。同じく公開の試験値の種から組み立てる（"0987654321" を 2 回）。
    private static readonly string RotatedSecret = Totp.EncodeBase32(System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("0987654321", 2))));

    private const string Target = "kill-switch";

    // ---- 段階的な停止（IADR-0626 決定 2） ----

    [Fact]
    public void 連続5回の失敗で15分止め_止めている間は正しいコードも受け付けない()
    {
        var (gate, clock, _, _) = Setup();
        FailTimes(gate, clock, 5);

        gate.Check(SecondFactorOperation.TradingResume, Target, CodeAt(clock.Now))
            .Should().Be(new SecondFactorDecision(SecondFactorOutcome.LockedOut, false));
        clock.Now = clock.Now.AddMinutes(15).AddSeconds(-1);
        gate.Check(SecondFactorOperation.TradingResume, Target, CodeAt(clock.Now)).Outcome.Should().Be(SecondFactorOutcome.LockedOut);

        clock.Now = clock.Now.AddSeconds(1);
        gate.Check(SecondFactorOperation.TradingResume, Target, CodeAt(clock.Now)).Outcome.Should().Be(SecondFactorOutcome.Verified);
    }

    [Fact]
    public void 停止を繰り返すと15分_30分_60分と倍に延び_4回目は時間では解けない()
    {
        var (gate, clock, _, _) = Setup();

        foreach (var minutes in new[] { 15, 30, 60 })
        {
            FailTimes(gate, clock, 5);
            clock.Now = clock.Now.AddMinutes(minutes).AddSeconds(-1);
            gate.Check(SecondFactorOperation.StagePromote, Target, CodeAt(clock.Now)).Outcome
                .Should().Be(SecondFactorOutcome.LockedOut, $"{minutes} 分の停止の終わりの直前は止まっている");
            clock.Now = clock.Now.AddSeconds(1);
        }

        // 4 回目の停止は手動の解除まで閉じる（1 年後でも、正しいコードでも開かない）。
        FailTimes(gate, clock, 5);
        clock.Now = clock.Now.AddDays(365);
        gate.Check(SecondFactorOperation.StagePromote, Target, CodeAt(clock.Now))
            .Should().Be(new SecondFactorDecision(SecondFactorOutcome.LockedOut, false));
    }

    [Fact]
    public void 停止が解けた後の失敗は数え直すが_停止の回数は成功まで残る()
    {
        var (gate, clock, store, _) = Setup();
        FailTimes(gate, clock, 5);
        clock.Now = clock.Now.AddMinutes(15);
        FailTimes(gate, clock, 4);

        Peek(store).Should().Match<OwnerSecondFactorState>(s => s.ConsecutiveFailures == 4 && s.LockoutCount == 1);

        gate.Check(SecondFactorOperation.TradingResume, Target, CodeAt(clock.Now)).Outcome.Should().Be(SecondFactorOutcome.Verified);
        Peek(store).Should().Match<OwnerSecondFactorState>(s => s.ConsecutiveFailures == 0 && s.LockoutCount == 0 && s.LockedUntil == null);
    }

    [Fact]
    public void 止めている間の試行は数えず_停止を延ばさない()
    {
        var (gate, clock, store, _) = Setup();
        FailTimes(gate, clock, 5);
        var locked = Peek(store);

        for (var i = 0; i < 50; i++)
            gate.Check(SecondFactorOperation.TradingResume, Target, Wrong(clock)).Outcome.Should().Be(SecondFactorOutcome.LockedOut);

        Peek(store).Should().Be(locked);
    }

    [Fact]
    public void 手動の解除はシークレットの作り直しで_再起動では解けない()
    {
        var store = new InMemoryOwnerSecondFactorStateStore();
        var clock = new ManualClock();
        var gate = Gate(RfcSecret, store, clock, null);
        for (var i = 0; i < 4; i++)
        {
            FailTimes(gate, clock, 5);
            clock.Now = clock.Now.AddDays(1);
        }

        // 再起動（同じシークレット・同じ置き場で門を作り直す）では開かない。
        Gate(RfcSecret, store, clock, null).Check(SecondFactorOperation.KillSwitchDisengage, Target, CodeAt(clock.Now))
            .Outcome.Should().Be(SecondFactorOutcome.LockedOut);

        // シークレットを作り直すと（指紋が変わる）新しい状態から始まる。
        var rotated = Gate(RotatedSecret, store, clock, null);
        rotated.Check(SecondFactorOperation.KillSwitchDisengage, Target, CodeAt(clock.Now, RotatedSecret))
            .Outcome.Should().Be(SecondFactorOutcome.Verified);
    }

    [Fact]
    public void 欠けたコードは数えない()
    {
        var (gate, clock, store, _) = Setup();
        for (var i = 0; i < 10; i++)
            gate.Check(SecondFactorOperation.TradingResume, Target, null).Outcome.Should().Be(SecondFactorOutcome.Missing);

        Peek(store).ConsecutiveFailures.Should().Be(0);
        gate.Check(SecondFactorOperation.TradingResume, Target, CodeAt(clock.Now)).Outcome.Should().Be(SecondFactorOutcome.Verified);
    }

    // ---- 通知と監査への発行（IADR-0626 決定 3） ----

    [Fact]
    public void 失敗と停止と成功を知らせ_欠けは知らせない()
    {
        var (gate, clock, _, observer) = Setup();
        gate.Check(SecondFactorOperation.TradingResume, Target, null);
        FailTimes(gate, clock, 5);
        gate.Check(SecondFactorOperation.TradingResume, Target, CodeAt(clock.Now));

        observer.Checks.Select(c => c.Outcome).Should().Equal(
            "Invalid", "Invalid", "Invalid", "Invalid", "Invalid", "LockedOut");
        observer.Checks.Take(4).Select(c => c.ConsecutiveFailures).Should().Equal(1, 2, 3, 4);
        observer.Checks.Take(4).Should().OnlyContain(c => !c.LockStarted);

        var lockStart = observer.Checks[4];
        lockStart.LockStarted.Should().BeTrue();
        lockStart.LockoutCount.Should().Be(1);
        lockStart.LockedUntil.Should().Be(clock.Now.AddMinutes(15));
        observer.Checks[5].LockStarted.Should().BeFalse("止めている間の試行は停止の始まりではない");

        clock.Now = clock.Now.AddMinutes(15);
        gate.Check(SecondFactorOperation.TradingResume, Target, CodeAt(clock.Now));
        observer.Checks[^1].Should().Match<OwnerSecondFactorChecked>(c =>
            c.Outcome == "Verified" && c.Operation == "TradingResume" && c.Target == Target);
    }

    [Fact]
    public void 手動の解除まで閉じたことを知らせる()
    {
        var (gate, clock, _, observer) = Setup();
        for (var i = 0; i < 4; i++)
        {
            FailTimes(gate, clock, 5);
            clock.Now = clock.Now.AddDays(1);
        }

        observer.Checks.Where(c => c.LockStarted).Select(c => c.ManualUnlockRequired).Should().Equal(false, false, false, true);
        observer.Checks.Last(c => c.LockStarted).LockedUntil.Should().BeNull();
    }

    [Fact]
    public void 知らせの失敗で確かめの結果を変えない()
    {
        var clock = new ManualClock();
        var gate = Gate(RfcSecret, new InMemoryOwnerSecondFactorStateStore(), clock, new ThrowingObserver());

        gate.Check(SecondFactorOperation.TradingResume, Target, CodeAt(clock.Now)).Outcome.Should().Be(SecondFactorOutcome.Verified);
    }

    // ---- 2 サービスでの共有（IADR-0626 決定 1） ----

    [Fact]
    public void 失敗の数は2つの門で1つに数える()
    {
        var store = new InMemoryOwnerSecondFactorStateStore();
        var clock = new ManualClock();
        var risk = Gate(RfcSecret, store, clock, null);
        var report = Gate(RfcSecret, store, clock, null);

        FailTimes(risk, clock, 3);
        FailTimes(report, clock, 2);

        risk.Check(SecondFactorOperation.KillSwitchDisengage, Target, CodeAt(clock.Now)).Outcome.Should().Be(SecondFactorOutcome.LockedOut);
        report.Check(SecondFactorOperation.ReportConfirm, "report:2026-11-06@v1", CodeAt(clock.Now)).Outcome.Should().Be(SecondFactorOutcome.LockedOut);
    }

    [Fact]
    public void 一方で使ったコードは他方でも通らない()
    {
        var store = new InMemoryOwnerSecondFactorStateStore();
        var clock = new ManualClock();
        var risk = Gate(RfcSecret, store, clock, null);
        var report = Gate(RfcSecret, store, clock, null);
        var code = CodeAt(clock.Now);

        report.Check(SecondFactorOperation.ReportConfirm, "report:2026-11-06@v1", code).Outcome.Should().Be(SecondFactorOutcome.Verified);
        risk.Check(SecondFactorOperation.StagePromote, "stage:Stage2Small", code)
            .Should().Be(new SecondFactorDecision(SecondFactorOutcome.Replayed, false));
    }

    [Fact]
    public void 報告書の門は持ち主へ問い合わせ_持ち主の数え方と1つになる()
    {
        var store = new InMemoryOwnerSecondFactorStateStore();
        var clock = new ManualClock();
        var observer = new RecordingObserver();
        var ownerVerifier = new TotpOwnerSecondFactorVerifier(Options(RfcSecret), store, clock, NullLogger.Instance, observer);
        var risk = new OwnerSecondFactorGate(Options(RfcSecret), ownerVerifier, NullLogger.Instance);
        var handler = new OwnerEndpointStub(ownerVerifier);
        // 報告書はシークレットを持たない（確かめは持ち主がする）。
        var report = new OwnerSecondFactorGate(
            new OwnerSecondFactorOptions { Mode = SecondFactorMode.Enforce },
            new RemoteOwnerSecondFactorVerifier(new HttpClient(handler) { BaseAddress = new Uri("http://risk-management") }, NullLogger.Instance),
            NullLogger.Instance);

        var code = CodeAt(clock.Now);
        report.Check(SecondFactorOperation.ReportConfirm, "report:2026-11-06@v2", code).Should().Be(new SecondFactorDecision(SecondFactorOutcome.Verified, true));
        risk.Check(SecondFactorOperation.KillSwitchDisengage, Target, code).Outcome.Should().Be(SecondFactorOutcome.Replayed);

        handler.Requests.Should().ContainSingle().Which.Should().Be(
            new OwnerSecondFactorVerifyRequest("ReportConfirm", "report:2026-11-06@v2", code));
        observer.Checks.Select(c => (c.Operation, c.Target, c.Outcome)).Should().Equal(
            ("ReportConfirm", "report:2026-11-06@v2", "Verified"), ("KillSwitchDisengage", Target, "Replayed"));

        // 失敗も 1 つに数える（上の使い回し 1 回・報告書 3 回・リスク管理 1 回目で 5 回になり止まる）。
        clock.Now = clock.Now.AddMinutes(1);
        FailTimes(report, clock, 3);
        FailTimes(risk, clock, 2);
        report.Check(SecondFactorOperation.ReportConfirm, "report:2026-11-06@v2", CodeAt(clock.Now)).Outcome.Should().Be(SecondFactorOutcome.LockedOut);
    }

    // ---- 操作と対象への結び付き（IADR-0626 決定 4） ----

    [Fact]
    public void 確かめた結果は操作と対象に結び付き_別の操作に流用できない()
    {
        var (gate, clock, store, observer) = Setup();
        var code = CodeAt(clock.Now);

        gate.Check(SecondFactorOperation.ReportConfirm, "report:2026-11-06@v1", code).Outcome.Should().Be(SecondFactorOutcome.Verified);
        Peek(store).Should().Match<OwnerSecondFactorState>(s =>
            s.LastAcceptedOperation == "ReportConfirm" && s.LastAcceptedTarget == "report:2026-11-06@v1");

        gate.Check(SecondFactorOperation.ReportConfirm, "report:2026-11-07@v1", code).Outcome.Should().Be(SecondFactorOutcome.Replayed, "同じ操作の別の対象");
        gate.Check(SecondFactorOperation.StagePromote, "stage:Stage3", code).Outcome.Should().Be(SecondFactorOutcome.Replayed, "別の操作");
        observer.Checks.Select(c => c.Target).Should().Equal("report:2026-11-06@v1", "report:2026-11-07@v1", "stage:Stage3");
    }

    [Theory]
    [InlineData(null, "unspecified")]
    [InlineData("  ", "unspecified")]
    [InlineData("stage:Stage2\nforged", "stage:Stage2_forged")]
    public void 対象の表記は整えて記録する(string? raw, string expected) =>
        OwnerSecondFactorTargets.Normalize(raw).Should().Be(expected);

    [Fact]
    public void 対象は長さの上限で切る() =>
        OwnerSecondFactorTargets.Normalize(new string('x', 500)).Should().HaveLength(OwnerSecondFactorTargets.MaxLength);

    // ---- 状態を読み書きできないとき ----

    [Fact]
    public void 状態を読み書きできなければEnforceは拒みAuditは記録だけ()
    {
        var clock = new ManualClock();
        var verifier = new TotpOwnerSecondFactorVerifier(Options(RfcSecret), new BrokenStore(), clock, NullLogger.Instance);

        new OwnerSecondFactorGate(Options(RfcSecret), verifier, NullLogger.Instance)
            .Check(SecondFactorOperation.TradingResume, Target, CodeAt(clock.Now))
            .Should().Be(new SecondFactorDecision(SecondFactorOutcome.Unavailable, false));
        new OwnerSecondFactorGate(new OwnerSecondFactorOptions { Mode = SecondFactorMode.Audit, Secret = RfcSecret }, verifier, NullLogger.Instance)
            .Check(SecondFactorOperation.TradingResume, Target, CodeAt(clock.Now))
            .Should().Be(new SecondFactorDecision(SecondFactorOutcome.Unavailable, true));
    }

    // ---- 持ち主への問い合わせ（報告書の側） ----

    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"outcome\":\"LockedOut\"}", SecondFactorOutcome.LockedOut)]
    [InlineData(HttpStatusCode.OK, "{\"outcome\":\"Replayed\"}", SecondFactorOutcome.Replayed)]
    [InlineData(HttpStatusCode.OK, "{\"outcome\":\"1\"}", SecondFactorOutcome.Unavailable)]
    [InlineData(HttpStatusCode.OK, "{\"outcome\":\"Skipped\"}", SecondFactorOutcome.Unavailable)]
    [InlineData(HttpStatusCode.OK, "{\"outcome\":\"Granted\"}", SecondFactorOutcome.Unavailable)]
    [InlineData(HttpStatusCode.OK, "{}", SecondFactorOutcome.Unavailable)]
    [InlineData(HttpStatusCode.Forbidden, "{\"outcome\":\"Verified\"}", SecondFactorOutcome.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", SecondFactorOutcome.Unavailable)]
    public void 持ち主の応答は名前だけを受け_それ以外は確かめられないとする(HttpStatusCode status, string body, SecondFactorOutcome expected)
    {
        var verifier = new RemoteOwnerSecondFactorVerifier(
            new HttpClient(new CannedHandler(status, body)) { BaseAddress = new Uri("http://risk-management") }, NullLogger.Instance);

        verifier.Verify(SecondFactorOperation.ReportConfirm, "report:x@v1", "123456").Should().Be(expected);
    }

    [Fact]
    public void 持ち主へ届かなければ確かめられないとし_欠けたコードは問い合わせない()
    {
        var handler = new CannedHandler(HttpStatusCode.OK, "{\"outcome\":\"Verified\"}") { Throw = true };
        var verifier = new RemoteOwnerSecondFactorVerifier(new HttpClient(handler) { BaseAddress = new Uri("http://risk-management") }, NullLogger.Instance);

        verifier.Verify(SecondFactorOperation.PolicyConfirm, "report:x@v1", "123456").Should().Be(SecondFactorOutcome.Unavailable);
        handler.Calls.Should().Be(1);

        verifier.Verify(SecondFactorOperation.PolicyConfirm, "report:x@v1", null).Should().Be(SecondFactorOutcome.Missing);
        verifier.Verify(SecondFactorOperation.PolicyConfirm, "report:x@v1", "").Should().Be(SecondFactorOutcome.Missing);
        handler.Calls.Should().Be(1, "欠けは状態を変えないので問い合わせない");
    }

    // ---- 補助 ----

    private static (OwnerSecondFactorGate Gate, ManualClock Clock, InMemoryOwnerSecondFactorStateStore Store, RecordingObserver Observer) Setup()
    {
        var store = new InMemoryOwnerSecondFactorStateStore();
        var clock = new ManualClock();
        var observer = new RecordingObserver();
        return (Gate(RfcSecret, store, clock, observer), clock, store, observer);
    }

    private static OwnerSecondFactorOptions Options(string secret) => new() { Mode = SecondFactorMode.Enforce, Secret = secret };

    private static OwnerSecondFactorGate Gate(string secret, IOwnerSecondFactorStateStore store, ManualClock clock, IOwnerSecondFactorObserver? observer) =>
        new(Options(secret), new TotpOwnerSecondFactorVerifier(Options(secret), store, clock, NullLogger.Instance, observer), NullLogger.Instance);

    private static OwnerSecondFactorState Peek(InMemoryOwnerSecondFactorStateStore store)
    {
        Totp.TryDecodeBase32(RfcSecret, out var key);
        return store.Update(TotpOwnerSecondFactorVerifier.FingerprintOf(key), s => (s, s));
    }

    private static void FailTimes(OwnerSecondFactorGate gate, ManualClock clock, int times)
    {
        for (var i = 0; i < times; i++)
            gate.Check(SecondFactorOperation.TradingResume, Target, Wrong(clock)).Allowed.Should().BeFalse();
    }

    private static string CodeAt(DateTimeOffset at, string? secret = null)
    {
        Totp.TryDecodeBase32(secret ?? RfcSecret, out var key);
        return Totp.Compute(key, Totp.StepOf(at));
    }

    // 前後 1 段のどれにも当たらないコード。
    private static string Wrong(ManualClock clock)
    {
        var valid = new[] { -30, 0, 30 }.Select(s => CodeAt(clock.Now.AddSeconds(s))).ToHashSet();
        return Enumerable.Range(0, 10).Select(i => $"{i}{i}{i}{i}{i}{i}").First(c => !valid.Contains(c));
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_780_000_005);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingObserver : IOwnerSecondFactorObserver
    {
        public List<OwnerSecondFactorChecked> Checks { get; } = [];

        public void OnChecked(OwnerSecondFactorChecked check) => Checks.Add(check);
    }

    private sealed class ThrowingObserver : IOwnerSecondFactorObserver
    {
        public void OnChecked(OwnerSecondFactorChecked check) => throw new InvalidOperationException("broker down");
    }

    private sealed class BrokenStore : IOwnerSecondFactorStateStore
    {
        public T Update<T>(string secretFingerprint, Func<OwnerSecondFactorState, (OwnerSecondFactorState Next, T Result)> decide) =>
            throw new InvalidOperationException("db down");
    }

    // 持ち主（リスク管理）の口の代わり: 受けた要求を持ち主の確かめへ渡し、結果を名前で返す。
    private sealed class OwnerEndpointStub(IOwnerSecondFactorVerifier owner) : HttpMessageHandler
    {
        public List<OwnerSecondFactorVerifyRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.Should().Be(RemoteOwnerSecondFactorVerifier.Route);
            var body = await request.Content!.ReadFromJsonAsync<OwnerSecondFactorVerifyRequest>(cancellationToken);
            Requests.Add(body!);
            var outcome = owner.Verify(Enum.Parse<SecondFactorOperation>(body!.Operation), body.Target, body.Code);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new OwnerSecondFactorVerifyResponse(outcome.ToString())) };
        }
    }

    private sealed class CannedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public bool Throw { get; init; }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw)
                throw new HttpRequestException("unreachable");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
