using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RiskManagementService.Features.RiskManagement;
using Wolverine;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 NFR, FR-10, #1527, IADR-0610 決定1: 注文の承認は印を付ける口（OrderApprovalPublisher）からだけ出す。
public class OrderApprovalPublisherTests
{
    public class RecordingBus : DispatchProxy
    {
        public List<(object Message, DeliveryOptions? Options)> Published { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IMessageBus.PublishAsync))
                throw new NotSupportedException(targetMethod?.Name);
            Published.Add((args![0]!, args.Length > 1 ? args[1] as DeliveryOptions : null));
            return ValueTask.CompletedTask;
        }
    }

    private static OrderApproved Approved() => new(
        Guid.NewGuid(),
        new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper,
            5, 100m, PositionEffect.Close),
        5, DateTimeOffset.UnixEpoch, Origin: OrderApprovalOrigin.OwnerClose);

    [Fact]
    public async Task 鍵があれば承認に印のヘッダーを付け_受け手の検証で合う()
    {
        var signature = OrderApprovalSignature.TryCreate(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))!;
        var bus = DispatchProxy.Create<IMessageBus, RecordingBus>();
        var approved = Approved();

        await new OrderApprovalPublisher(signature, NullLogger<OrderApprovalPublisher>.Instance).PublishAsync(bus, approved);

        var (message, options) = ((RecordingBus)(object)bus).Published.Should().ContainSingle().Subject;
        message.Should().BeSameAs(approved);
        options.Should().NotBeNull();
        options!.Headers.Should().ContainKey(OrderApprovalSignature.HeaderName);
        signature.Verify(approved, options.Headers[OrderApprovalSignature.HeaderName])
            .Should().Be(OrderApprovalVerification.Valid);
    }

    [Fact]
    public async Task 鍵が無ければ印なしで従前どおり発行する()
    {
        var bus = DispatchProxy.Create<IMessageBus, RecordingBus>();

        await new OrderApprovalPublisher(null, NullLogger<OrderApprovalPublisher>.Instance).PublishAsync(bus, Approved());

        ((RecordingBus)(object)bus).Published.Should().ContainSingle();
    }

    // 🔴 書き手（審査・利用者の手仕舞い・維持率の自動縮小）が素の PublishAsync で承認を出すと、印の無い承認になる。
    // 承認を運ぶ変数名（Approved / Approval / approval / approvals の要素）を PublishAsync へ直接渡す書き方を本サービスのソースから探す。
    [Fact]
    public void 承認を素の_PublishAsync_で出す書き手が無い()
    {
        var root = ServiceRoot();
        var direct = new Regex(@"bus\.PublishAsync\(\s*(outcome\.Approv\w*!?|approval|approved)\s*\)", RegexOptions.CultureInvariant);

        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            // 唯一の口そのもの（印を付けた後の発行・鍵が無いときの印なしの発行）は除く。
            .Where(f => Path.GetFileName(f) != "OrderApprovalPublisher.cs")
            .Where(f => direct.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        offenders.Should().BeEmpty("注文の承認は OrderApprovalPublisher から出す（IADR-0610 決定1）");
        File.Exists(Path.Combine(root, "Features", "RiskManagement", "OrderApprovalPublisher.cs"))
            .Should().BeTrue("前提: 走査の起点が本サービスのソースである");
    }

    private static string ServiceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RiskManagementService.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("RiskManagementService.csproj が見つかりません");
    }
}
