using System.Security.Cryptography;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Messaging;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.Messaging;

// NFR, FR-10, #1527, IADR-0610: 注文の承認の発行元の印（HMAC-SHA256）。
public class OrderApprovalSignatureTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 14, 30, 0, TimeSpan.Zero);

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static OrderApproved Approved(PositionEffect effect = PositionEffect.Close, int quantity = 10, decimal price = 10.50m) =>
        new(
            Guid.Parse("7a1f0c1e-9a51-4f7e-9b0a-0d7c3c2b1a01"),
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper,
                quantity, price, effect, StopLossPrice: null, FxRateToBase: 1m),
            quantity, T0, Origin: OrderApprovalOrigin.OwnerClose);

    private static OrderApprovalSignature Signer(string key, string? previous = null) =>
        OrderApprovalSignature.TryCreate(key, previous)!;

    [Fact]
    public void 同じ鍵で作った印は合う()
    {
        var signer = Signer(NewKey());
        var approved = Approved();

        signer.Verify(approved, signer.Sign(approved)).Should().Be(OrderApprovalVerification.Valid);
    }

    [Fact]
    public void 印が無ければ_Missing()
    {
        Signer(NewKey()).Verify(Approved(), null).Should().Be(OrderApprovalVerification.Missing);
        Signer(NewKey()).Verify(Approved(), string.Empty).Should().Be(OrderApprovalVerification.Missing);
    }

    [Fact]
    public void 別の鍵で作った印は合わない()
    {
        var approved = Approved();
        var forged = Signer(NewKey()).Sign(approved);

        Signer(NewKey()).Verify(approved, forged).Should().Be(OrderApprovalVerification.Mismatch);
    }

    [Theory]
    [InlineData("v1.!!!")]
    [InlineData("v2.AAAA")]
    [InlineData("garbage")]
    public void 形の違う印は合わない(string presented) =>
        Signer(NewKey()).Verify(Approved(), presented).Should().Be(OrderApprovalVerification.Mismatch);

    // 発注を左右する項目を 1 つでも書き換えた承認は、元の印では通らない。
    public static TheoryData<string> Tampering => new()
    {
        "quantity", "approvedQuantity", "side", "effect", "symbol", "price", "market", "mode", "origin", "decisionId", "marketOrder",
    };

    [Theory]
    [MemberData(nameof(Tampering))]
    public void 項目を書き換えた承認は元の印では合わない(string field)
    {
        var signer = Signer(NewKey());
        var original = Approved();
        var signature = signer.Sign(original);

        var tampered = field switch
        {
            "quantity" => original with { Intent = original.Intent with { Quantity = 1000 } },
            "approvedQuantity" => original with { ApprovedQuantity = 1000 },
            "side" => original with { Intent = original.Intent with { Side = TradeSide.Buy } },
            "effect" => original with { Intent = original.Intent with { PositionEffect = PositionEffect.Open } },
            "symbol" => original with { Intent = original.Intent with { Symbol = "MSFT" } },
            "price" => original with { Intent = original.Intent with { Price = 1m } },
            "market" => original with { Intent = original.Intent with { Market = Market.Japan } },
            "mode" => original with { Intent = original.Intent with { Mode = BrokerProvider.MoomooReal } },
            "origin" => original with { Origin = OrderApprovalOrigin.TradeDecision },
            "decisionId" => original with { DecisionId = Guid.NewGuid() },
            "marketOrder" => original with { Intent = original.Intent with { MarketOrder = true } },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        signer.Verify(tampered, signature).Should().Be(OrderApprovalVerification.Mismatch);
    }

    [Fact]
    public void JSON_の往復をしても印は合う()
    {
        // メッセージの本文は直列化して運ばれる。受け手が組み直した値で同じ正準形になること。
        var signer = Signer(NewKey());
        var approved = Approved();
        var signature = signer.Sign(approved);

        var restored = JsonSerializer.Deserialize<OrderApproved>(JsonSerializer.Serialize(approved))!;

        signer.Verify(restored, signature).Should().Be(OrderApprovalVerification.Valid);
    }

    [Fact]
    public void 末尾ゼロの違う同じ価格は同じ正準形になる() =>
        OrderApprovalSignature.Canonical(Approved(price: 10.50m))
            .Should().Be(OrderApprovalSignature.Canonical(Approved(price: 10.5m)));

    [Fact]
    public void 入れ替えの間は前の鍵で作った印も通る()
    {
        var oldKey = NewKey();
        var approved = Approved();
        var signedWithOld = Signer(oldKey).Sign(approved);

        Signer(NewKey(), previous: oldKey).Verify(approved, signedWithOld).Should().Be(OrderApprovalVerification.Valid);
        Signer(NewKey()).Verify(approved, signedWithOld).Should().Be(OrderApprovalVerification.Mismatch);
    }

    [Fact]
    public void 鍵が空なら作らない()
    {
        OrderApprovalSignature.TryCreate(null).Should().BeNull();
        OrderApprovalSignature.TryCreate("  ").Should().BeNull();
    }

    [Fact]
    public void 短い鍵と読めない鍵は起動で止め_値を文言に出さない()
    {
        var shortKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        var tooShort = () => OrderApprovalSignature.TryCreate(shortKey);
        tooShort.Should().Throw<InvalidOperationException>().Which.Message.Should().NotContain(shortKey);

        var unreadable = () => OrderApprovalSignature.TryCreate("not-base64-%%%");
        unreadable.Should().Throw<InvalidOperationException>().Which.Message.Should().NotContain("not-base64-%%%");
    }

    [Fact]
    public void 段2で鍵が無ければ起動で止まる_段1は鍵なしで動く()
    {
        var enforce = () => new OrderApprovalSigningOptions { Mode = OrderApprovalSigningMode.Enforce }.Resolve();
        enforce.Should().Throw<InvalidOperationException>();

        new OrderApprovalSigningOptions().Resolve().Should().BeNull("既定は段 1（報告のみ）");
        new OrderApprovalSigningOptions().Mode.Should().Be(OrderApprovalSigningMode.Report);
    }

    // 🔴 契約に項目を足したら、正準形（OrderApprovalSignature.Canonical）にも足す。足し忘れると、その項目は印で守られない。
    [Fact]
    public void 正準形が覆う契約の項目の一覧を固定する()
    {
        static string[] Names(Type t) => t.GetProperties()
            .Where(p => p.GetMethod is { IsStatic: false } && p.Name != "EqualityContract" && p.CanWrite)
            .Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

        Names(typeof(OrderApproved)).Should().Equal(
            "ApprovedAt", "ApprovedQuantity", "CycleStartedAt", "CycleTrigger", "DecisionId", "FromTradeDecision",
            "Intent", "Origin", "StopLossMethod");
        Names(typeof(OrderIntent)).Should().Equal(
            "Atr14", "EntryBasis", "FxRateToBase", "Market", "MarketOrder", "Mode", "PositionEffect", "Price",
            "ProductType", "Quantity", "Side", "StopFloorSource", "StopLossPrice", "Symbol");
        Names(typeof(EntryBasis)).Should().Equal("Condition", "ReferencePrice");
    }
}
