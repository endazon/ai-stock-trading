using System.Reflection;
using System.Security.Cryptography;
using OrderExecutionService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 NFR, FR-10, #1527, IADR-0610 決定2: 発注執行は受け取った承認の発行元の印を確かめる。
// 段 1（Report）は記録だけで通し、段 2（Enforce）は印の無い・合わない承認を発注しない（何も発行しない）。
public class OrderApprovalVerificationTests
{
    // 発行されたものを記録するだけのバス。
    public class RecordingBus : DispatchProxy
    {
        public List<object> Published { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IMessageBus.PublishAsync))
                throw new NotSupportedException(targetMethod?.Name);
            Published.Add(args![0]!);
            return ValueTask.CompletedTask;
        }
    }

    private static readonly OrderApprovalSignature Signer =
        OrderApprovalSignature.TryCreate(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))!;

    private static OrderApprovalVerifier Verifier(OrderApprovalSigningMode mode, OrderApprovalSignature? signature) =>
        new(mode, signature, NullLogger<OrderApprovalVerifier>.Instance);

    private static Envelope EnvelopeOf(OrderApproved approved, string? signature)
    {
        var envelope = new Envelope(approved);
        if (signature is not null)
            envelope.Headers[OrderApprovalSignature.HeaderName] = signature;
        return envelope;
    }

    private static async Task<List<object>> HandleAsync(OrderApprovalVerifier verifier, OrderApproved approved, Envelope envelope)
    {
        var bus = DispatchProxy.Create<IMessageBus, RecordingBus>();
        await OrderApprovedMethodResolvedPublishFailureTests.NewHandler(verifier)
            .Handle(approved, envelope, bus, CancellationToken.None);
        return ((RecordingBus)(object)bus).Published;
    }

    [Fact]
    public async Task 段2_印の無い承認は発注しない()
    {
        var approved = OrderApprovedMethodResolvedPublishFailureTests.Approved(StopLossExecutionMethod.BrokerStopOrder);

        var published = await HandleAsync(Verifier(OrderApprovalSigningMode.Enforce, Signer), approved, EnvelopeOf(approved, null));

        published.Should().BeEmpty("発注しない＝発注結果も見送りも出さない");
    }

    [Fact]
    public async Task 段2_別の鍵で作った印の承認は発注しない()
    {
        var approved = OrderApprovedMethodResolvedPublishFailureTests.Approved(StopLossExecutionMethod.BrokerStopOrder);
        var forger = OrderApprovalSignature.TryCreate(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))!;

        var published = await HandleAsync(
            Verifier(OrderApprovalSigningMode.Enforce, Signer), approved, EnvelopeOf(approved, forger.Sign(approved)));

        published.Should().BeEmpty();
    }

    [Fact]
    public void 段2_決済でも印の無い承認は発注しない()
    {
        // kill switch は決済を止めない（ADR-0009 決定 4）。決済を守るのは発行元の検証である。
        var entry = OrderApprovedMethodResolvedPublishFailureTests.Approved(StopLossExecutionMethod.BrokerStopOrder);
        var close = entry with { Intent = entry.Intent with { PositionEffect = PositionEffect.Close, Side = TradeSide.Sell } };

        Verifier(OrderApprovalSigningMode.Enforce, Signer).Admit(close, EnvelopeOf(close, null)).Should().BeFalse();
        Verifier(OrderApprovalSigningMode.Enforce, Signer).Admit(close, EnvelopeOf(close, Signer.Sign(close))).Should().BeTrue();
    }

    [Fact]
    public async Task 段2_正しい印の承認は従前どおり発注する()
    {
        var approved = OrderApprovedMethodResolvedPublishFailureTests.Approved(StopLossExecutionMethod.BrokerStopOrder);

        var published = await HandleAsync(
            Verifier(OrderApprovalSigningMode.Enforce, Signer), approved, EnvelopeOf(approved, Signer.Sign(approved)));

        published.OfType<OrderExecuted>().Should().ContainSingle().Which.DecisionId.Should().Be(approved.DecisionId);
    }

    [Fact]
    public async Task 段1_印の無い承認も従前どおり発注する()
    {
        var approved = OrderApprovedMethodResolvedPublishFailureTests.Approved(StopLossExecutionMethod.BrokerStopOrder);

        var published = await HandleAsync(Verifier(OrderApprovalSigningMode.Report, Signer), approved, EnvelopeOf(approved, null));

        published.OfType<OrderExecuted>().Should().ContainSingle();
    }

    [Fact]
    public void 段1_鍵の無い受け手は確かめられないが通す()
    {
        var approved = OrderApprovedMethodResolvedPublishFailureTests.Approved(StopLossExecutionMethod.BrokerStopOrder);

        Verifier(OrderApprovalSigningMode.Report, signature: null).Admit(approved, EnvelopeOf(approved, null)).Should().BeTrue();
    }

    [Theory]
    [InlineData(OrderApprovalSigningMode.Report, true)]
    [InlineData(OrderApprovalSigningMode.Enforce, false)]
    public void 発注意図の無い承認は段に従い_例外にしない(OrderApprovalSigningMode mode, bool admitted)
    {
        var approved = OrderApprovedMethodResolvedPublishFailureTests.Approved(StopLossExecutionMethod.BrokerStopOrder);
        var broken = approved with { Intent = null! };

        var admit = () => Verifier(mode, Signer).Admit(broken, EnvelopeOf(broken, Signer.Sign(approved)));

        admit.Should().NotThrow();
        admit().Should().Be(admitted);
    }
}
