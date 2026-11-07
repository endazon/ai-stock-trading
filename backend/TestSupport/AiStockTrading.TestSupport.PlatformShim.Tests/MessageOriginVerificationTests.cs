using System.Globalization;
using System.Security.Cryptography;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// 🔴 NFR, FR-10, ADR-0009, IADR-0627 決定 2・3・4: 統制の入力になるメッセージの発行元を受け手で確かめる。
// 表（MessageOriginCatalog）の型ごとに、正しい印なら通ること、段 2 で
// ①印なし ②発行元の偽装（許されない発行元を名乗る／許された発行元を名乗って別の鍵で印を作る）③本文の改竄 ④再送 が拒まれること、
// 段 1 では記録だけで通ることを確かめる。
public class MessageOriginVerificationTests
{
    private static readonly MutableTimeProvider Clock = new();

    // 発行元ごとの鍵（試験用の乱数）。受け手は全部を持つ（確かめに要る鍵が欠けて Unverifiable になるのを避ける）。
    private static readonly Dictionary<string, string?> Keys =
        AllIssuers().ToDictionary(i => i, _ => (string?)NewKey(), StringComparer.Ordinal);

    private static IEnumerable<string> AllIssuers() =>
        MessageOriginCatalog.Rules.SelectMany(r => r.Issuers).Distinct(StringComparer.Ordinal);

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static MessageOriginSignature Signature() => MessageOriginSignature.Create(Keys);

    private static MessageOriginVerifier Verifier(MessageOriginMode mode) =>
        new(mode, Signature(), TimeSpan.FromHours(24), TimeSpan.FromMinutes(5), Clock, NullLogger<MessageOriginVerifier>.Instance);

    // 発行元の口（送る側の規則）で印を付けた封筒。
    private static Envelope Signed(Type type, object? message = null)
    {
        var rule = MessageOriginCatalog.Find(type)!;
        var envelope = new Envelope(message ?? MessageOriginSamples.Create(type));
        new MessageOriginSigningRule(rule.Issuers[0], Signature(), Clock).Modify(envelope);
        return envelope;
    }

    // 受け手が受け取ったときの形（ヘッダーは写し、本文は差し替えられる）。
    private static Envelope Received(Envelope sent, object? body = null)
    {
        var received = new Envelope(body ?? sent.Message!);
        foreach (var (k, v) in sent.Headers)
            received.Headers[k] = v;
        return received;
    }

    [Theory]
    [MemberData(nameof(MessageOriginSamples.CatalogTypes), MemberType = typeof(MessageOriginSamples))]
    public void 発行元の口で印を付けたメッセージは段2でも通る(Type type)
    {
        var verifier = Verifier(MessageOriginMode.Enforce);
        var envelope = Received(Signed(type));

        verifier.Check(envelope).Should().Be(MessageOriginCheck.Valid);
        verifier.Admit(envelope).Should().BeTrue();
        envelope.Headers[MessageOriginSignature.IssuerHeader].Should().Be(MessageOriginCatalog.Find(type)!.Issuers[0]);
    }

    // Wolverine の既定の直列化で往復しても印は合う（decimal の末尾ゼロ・時刻のオフセットを正規形が吸収する）。
    [Theory]
    [MemberData(nameof(MessageOriginSamples.CatalogTypes), MemberType = typeof(MessageOriginSamples))]
    public void Wolverineの直列化を往復しても印は合う(Type type)
    {
        var sent = Signed(type);
        var serializer = new WolverineOptions().DefaultSerializer;
        var bytes = serializer.WriteMessage(sent.Message!);
        var body = serializer.ReadFromData(type, new Envelope { Data = bytes });

        body.Should().NotBeNull();
        MessageOriginSignature.Body(type, body!).Should().Be(MessageOriginSignature.Body(type, sent.Message!));
        Verifier(MessageOriginMode.Enforce).Check(Received(sent, body)).Should().Be(MessageOriginCheck.Valid);
    }

    [Theory]
    [MemberData(nameof(MessageOriginSamples.CatalogTypes), MemberType = typeof(MessageOriginSamples))]
    public void 印の無いメッセージは段2で拒まれる(Type type)
    {
        var verifier = Verifier(MessageOriginMode.Enforce);
        var envelope = new Envelope(MessageOriginSamples.Create(type));

        verifier.Check(envelope).Should().Be(MessageOriginCheck.Missing);
        verifier.Admit(envelope).Should().BeFalse();
    }

    // 偽装 1: その型を発行してよいサービスではない発行元を名乗る（自分の鍵で正しく印を作っても通らない）。
    [Theory]
    [MemberData(nameof(MessageOriginSamples.CatalogTypes), MemberType = typeof(MessageOriginSamples))]
    public void 許されない発行元を名乗るメッセージは段2で拒まれる(Type type)
    {
        var rule = MessageOriginCatalog.Find(type)!;
        var impostor = AllIssuers().First(i => !rule.Issuers.Contains(i));
        var envelope = new Envelope(MessageOriginSamples.Create(type));
        new MessageOriginSigningRuleForTest(impostor, Signature(), Clock).Modify(envelope);

        var verifier = Verifier(MessageOriginMode.Enforce);
        verifier.Check(envelope).Should().Be(MessageOriginCheck.ForbiddenIssuer);
        verifier.Admit(envelope).Should().BeFalse();
    }

    // 偽装 2: 許された発行元を名乗るが、その発行元の鍵を持たない（別の発行元の鍵で印を作る）。
    [Theory]
    [MemberData(nameof(MessageOriginSamples.CatalogTypes), MemberType = typeof(MessageOriginSamples))]
    public void 許された発行元を名乗っても別の鍵の印は段2で拒まれる(Type type)
    {
        var rule = MessageOriginCatalog.Find(type)!;
        var attackerKeys = new Dictionary<string, string?> { [rule.Issuers[0]] = NewKey() };
        var envelope = new Envelope(MessageOriginSamples.Create(type));
        new MessageOriginSigningRule(rule.Issuers[0], MessageOriginSignature.Create(attackerKeys), Clock).Modify(envelope);

        var verifier = Verifier(MessageOriginMode.Enforce);
        verifier.Check(envelope).Should().Be(MessageOriginCheck.Mismatch);
        verifier.Admit(envelope).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(MessageOriginSamples.CatalogTypes), MemberType = typeof(MessageOriginSamples))]
    public void 本文を改竄したメッセージは段2で拒まれる(Type type)
    {
        var tampered = MessageOriginSamples.Tampered(type);
        MessageOriginSignature.Body(type, tampered).Should().NotBe(
            MessageOriginSignature.Body(type, MessageOriginSamples.Create(type)), "改竄の見本は本文が違わなければならない");
        var envelope = Received(Signed(type), tampered);

        var verifier = Verifier(MessageOriginMode.Enforce);
        verifier.Check(envelope).Should().Be(MessageOriginCheck.Mismatch);
        verifier.Admit(envelope).Should().BeFalse();
    }

    // 印のうちメッセージ ID・時刻・型を書き換えても合わない（印の対象に含まれる）。
    [Theory]
    [InlineData(MessageOriginSignature.IdHeader)]
    [InlineData(MessageOriginSignature.TimeHeader)]
    public void 印の対象の見出しを書き換えたメッセージは段2で拒まれる(string header)
    {
        var envelope = Received(Signed(typeof(AiStockTrading.Shared.Contracts.Events.StopLossTriggered)));
        envelope.Headers[header] = header == MessageOriginSignature.IdHeader
            ? Guid.NewGuid().ToString("D")
            : (long.Parse(envelope.Headers[header]!, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);

        Verifier(MessageOriginMode.Enforce).Check(envelope).Should().Be(MessageOriginCheck.Mismatch);
    }

    [Theory]
    [MemberData(nameof(MessageOriginSamples.CatalogTypes), MemberType = typeof(MessageOriginSamples))]
    public void 処理し終えたメッセージの再送は段2で拒まれる(Type type)
    {
        var verifier = Verifier(MessageOriginMode.Enforce);
        var sent = Signed(type);
        var first = Received(sent);
        verifier.Admit(first).Should().BeTrue();
        verifier.MarkHandled(first);

        var replay = Received(sent);
        verifier.Check(replay).Should().Be(MessageOriginCheck.Replayed);
        verifier.Admit(replay).Should().BeFalse();
    }

    // 失敗からの再試行は再送ではない（ID は成功したときにだけ記録する）。
    [Fact]
    public void 処理が成功する前の再試行は再送に数えない()
    {
        var verifier = Verifier(MessageOriginMode.Enforce);
        var envelope = Received(Signed(typeof(AiStockTrading.Shared.Contracts.Events.OrderExecuted)));

        verifier.Admit(envelope).Should().BeTrue();
        verifier.Admit(envelope).Should().BeTrue("ハンドラが失敗して再試行されたときは記録が無い");

        // 処理の記録の後に後処理（送信の確定など）が失敗し、同じ配信（同じ封筒）で再試行されたときも通す。
        verifier.MarkHandled(envelope);
        envelope.Attempts = 2;
        verifier.Check(envelope).Should().Be(MessageOriginCheck.Valid);

        // 同じ実体でも 1 回目の試行（プロセス内の呼び出しで実体が使い回された）なら再送として拒む。
        envelope.Attempts = 1;
        verifier.Check(envelope).Should().Be(MessageOriginCheck.Replayed);
    }

    [Theory]
    [MemberData(nameof(MessageOriginSamples.CatalogTypes), MemberType = typeof(MessageOriginSamples))]
    public void 段1では合わないメッセージも記録だけで通す(Type type)
    {
        var verifier = Verifier(MessageOriginMode.Report);
        var rule = MessageOriginCatalog.Find(type)!;

        var unsigned = new Envelope(MessageOriginSamples.Create(type));
        var impostor = new Envelope(MessageOriginSamples.Create(type));
        new MessageOriginSigningRuleForTest(AllIssuers().First(i => !rule.Issuers.Contains(i)), Signature(), Clock).Modify(impostor);
        var tampered = Received(Signed(type), MessageOriginSamples.Tampered(type));
        var sent = Signed(type);
        var first = Received(sent);
        verifier.Admit(first).Should().BeTrue();
        verifier.MarkHandled(first);

        verifier.Admit(unsigned).Should().BeTrue();
        verifier.Admit(impostor).Should().BeTrue();
        verifier.Admit(tampered).Should().BeTrue();
        verifier.Admit(Received(sent)).Should().BeTrue();
    }

    [Fact]
    public void 古すぎるメッセージと先にありすぎるメッセージは段2で拒まれる()
    {
        var type = typeof(AiStockTrading.Shared.Contracts.Events.BrokerAccountObserved);
        var clock = new MutableTimeProvider();
        var signature = Signature();
        var verifier = new MessageOriginVerifier(
            MessageOriginMode.Enforce, signature, TimeSpan.FromHours(24), TimeSpan.FromMinutes(5), clock,
            NullLogger<MessageOriginVerifier>.Instance);

        var old = new Envelope(MessageOriginSamples.Create(type));
        new MessageOriginSigningRule(MessageOriginCatalog.OrderExecution, signature, clock).Modify(old);
        clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        verifier.Check(old).Should().Be(MessageOriginCheck.Stale);

        var ahead = new MutableTimeProvider();
        ahead.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(6));
        var future = new Envelope(MessageOriginSamples.Create(type));
        new MessageOriginSigningRule(MessageOriginCatalog.OrderExecution, signature, ahead).Modify(future);
        verifier.Check(future).Should().Be(MessageOriginCheck.Future);

        var withinSkew = new MutableTimeProvider();
        withinSkew.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(4));
        var near = new Envelope(MessageOriginSamples.Create(type));
        new MessageOriginSigningRule(MessageOriginCatalog.OrderExecution, signature, withinSkew).Modify(near);
        verifier.Check(near).Should().Be(MessageOriginCheck.Valid);
    }

    [Fact]
    public void 受け手に発行元の鍵が無ければ確かめられない()
    {
        var verifier = new MessageOriginVerifier(
            MessageOriginMode.Report, MessageOriginSignature.Create(new Dictionary<string, string?>()),
            TimeSpan.FromHours(24), TimeSpan.FromMinutes(5), Clock, NullLogger<MessageOriginVerifier>.Instance);

        verifier.Check(Received(Signed(typeof(AiStockTrading.Shared.Contracts.Events.OrderExecuted))))
            .Should().Be(MessageOriginCheck.Unverifiable);
    }

    [Fact]
    public void 前の鍵で作った印も入れ替えの間は合う()
    {
        var type = typeof(AiStockTrading.Shared.Contracts.Events.PositionDriftAdopted);
        var oldKey = NewKey();
        var sent = new Envelope(MessageOriginSamples.Create(type));
        new MessageOriginSigningRule(
            MessageOriginCatalog.RiskManagement,
            MessageOriginSignature.Create(new Dictionary<string, string?> { [MessageOriginCatalog.RiskManagement] = oldKey }),
            Clock).Modify(sent);

        var rotated = MessageOriginSignature.Create(
            new Dictionary<string, string?> { [MessageOriginCatalog.RiskManagement] = NewKey() },
            new Dictionary<string, string?> { [MessageOriginCatalog.RiskManagement] = oldKey });
        new MessageOriginVerifier(
                MessageOriginMode.Enforce, rotated, TimeSpan.FromHours(24), TimeSpan.FromMinutes(5), Clock,
                NullLogger<MessageOriginVerifier>.Instance)
            .Check(sent).Should().Be(MessageOriginCheck.Valid);
    }

    [Theory]
    [InlineData("not-base64!")]
    [InlineData("AAAA")]
    public void 読めない鍵や短い鍵は段に依らず例外で止まり値を表示しない(string key)
    {
        var act = () => MessageOriginSignature.Create(new Dictionary<string, string?> { ["trade-decision"] = key });

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("MessageOrigin:Keys:trade-decision").And.NotContain(key);
    }

    [Fact]
    public void 表の外の型には印を付けない()
    {
        var envelope = new Envelope(new NotInCatalog("x"));
        new MessageOriginSigningRule(MessageOriginCatalog.TradeDecision, Signature(), Clock).Modify(envelope);

        envelope.Headers.Should().NotContainKey(MessageOriginSignature.SignatureHeader);
    }

    [Fact]
    public void 発行元として許されていない型には自分の鍵で印を付けない()
    {
        // 判断（TradeDecisionMade）の発行元は取引判断だけ。発注執行の口からは印を付けない。
        var envelope = new Envelope(MessageOriginSamples.Create(typeof(AiStockTrading.Shared.Contracts.Events.TradeDecisionMade)));
        new MessageOriginSigningRule(MessageOriginCatalog.OrderExecution, Signature(), Clock).Modify(envelope);

        envelope.Headers.Should().NotContainKey(MessageOriginSignature.SignatureHeader);
    }

    public sealed record NotInCatalog(string Value);
}

// 試験だけの発行の口: 表の許可を見ずに名乗った発行元で印を付ける（偽装の見本を作るため）。
internal sealed class MessageOriginSigningRuleForTest(string issuerId, MessageOriginSignature signature, TimeProvider time)
{
    public void Modify(Envelope envelope)
    {
        var message = envelope.Message!;
        var id = Guid.NewGuid();
        var ticks = time.GetUtcNow().UtcTicks;
        envelope.Headers[MessageOriginSignature.IssuerHeader] = issuerId;
        envelope.Headers[MessageOriginSignature.IdHeader] = id.ToString("D");
        envelope.Headers[MessageOriginSignature.TimeHeader] = ticks.ToString(CultureInfo.InvariantCulture);
        envelope.Headers[MessageOriginSignature.SignatureHeader] = signature.Sign(message.GetType(), id, issuerId, ticks, message)!;
    }
}
