using NotificationService.Features.Notifications;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace NotificationService.Tests;

// 🔴 T-10-1004・T-10-1005, FR-10, FR-09, UC-06, #879, IADR-0424 決定1: 建玉照会の不明で決済を見送った通知に、
// **その建玉の保護の記録**を書き足す。「保護レグを持たない**可能性**」と書くのは発注執行が判別できなかったとき
// （Unknown・項目なし）だけで、判別できたときは断定する（不明・無し・有りを混ぜない）。
public class NotificationFormatterForgoneCloseProtectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 6, 0, 0, TimeSpan.Zero);

    private static OrderIntent CloseIntent(int qty = 300) =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            qty, 100m, PositionEffect.Close);

    private static NotificationMessage Format(
        ForgoneCloseProtection? protection,
        OrderDispatchForgoneReason reason = OrderDispatchForgoneReason.BrokerPositionsIndeterminate,
        int qty = 300) =>
        NotificationFormatter.From(new OrderDispatchForgone(Guid.NewGuid(), CloseIntent(qty), reason, T0, protection));

    private const string MayLackProtection = "この建玉は保護レグを持たない可能性があります";

    // 🔴 T-10-1004: 判別できなかった（項目なし＝旧い送り手・Unknown・未知の序数）→「可能性」。
    public static IEnumerable<object?[]> Undetermined =>
    [
        [null],
        [new ForgoneCloseProtection(ForgoneCloseProtectionStatus.Unknown, 0, 0)],
        [new ForgoneCloseProtection((ForgoneCloseProtectionStatus)99, 100, 0)],
    ];

    [Theory]
    [MemberData(nameof(Undetermined))]
    public void 判別できなかったときだけ保護レグを持たない可能性と書く(ForgoneCloseProtection? protection)
    {
        var msg = Format(protection);

        msg.Severity.Should().Be(NotificationSeverity.Critical);
        msg.Content.Should().Contain(MayLackProtection)
            .And.Contain("保護の記録を確認できませんでした")
            .And.Contain("手仕舞いを出し直しても同じ理由で見送られます");
        msg.Content.Should().NotContain("株分あります", "分からないときに株数を書かない");
    }

    // 🔴 T-10-1004: 記録が 1 件も無い → 断定（「可能性」と書かない）。記録についての断定であることを限定する。
    [Fact]
    public void 保護記録が無ければ保護レグを持たない建玉と断定する()
    {
        var msg = Format(new ForgoneCloseProtection(ForgoneCloseProtectionStatus.NoneRecorded, 0, 0));

        msg.Severity.Should().Be(NotificationSeverity.Critical);
        msg.Content.Should().NotContain("可能性");
        msg.Content.Should().Contain("保護の記録（ブローカー側の逆指値・ソフトウェア逆指値）が 1 件もありません")
            .And.Contain("保護レグを持たない建玉です")
            .And.Contain("システムはこの建玉を自動で損切りせず")
            .And.Contain("自分で置いた注文はシステムからは見えません");
    }

    // 🔴 T-10-1004: 記録がある → 株数を分けて書く。ブローカー側は「生きていることは確認できていない」、
    // S1 は「照会できないあいだは据え置かれる」、決済しようとした株数のうちブローカー側の注文が無い株数を断定する。
    [Fact]
    public void 保護記録があればブローカー側とソフトウェア逆指値と覆われていない株数を書き分ける()
    {
        var msg = Format(new ForgoneCloseProtection(ForgoneCloseProtectionStatus.Recorded, 130, 50), qty: 300);

        msg.Severity.Should().Be(NotificationSeverity.Critical);
        msg.Content.Should().NotContain("可能性");
        msg.Content.Should().Contain("ブローカー側の保護注文（逆指値など）が 130 株分あります")
            .And.Contain("注文が生きていることは確認できていません")
            .And.Contain("ソフトウェア逆指値の記録が 50 株分ありますが")
            .And.Contain("建玉を照会できないあいだは据え置かれます")
            .And.Contain("決済しようとした 300 株のうち 170 株には、記録上ブローカー側の保護注文がありません");
    }

    [Fact]
    public void ブローカー側の注文が決済の株数を覆っていれば覆われていない株数を書かない()
    {
        var msg = Format(new ForgoneCloseProtection(ForgoneCloseProtectionStatus.Recorded, 300, 0), qty: 300);

        msg.Content.Should().Contain("300 株分あります");
        msg.Content.Should().NotContain("記録上ブローカー側の保護注文がありません");
        msg.Content.Should().NotContain("ソフトウェア逆指値の記録");
    }

    [Fact]
    public void 記録はあるが主張が0株なら0株と書き全株を覆われていないと書く()
    {
        var msg = Format(new ForgoneCloseProtection(ForgoneCloseProtectionStatus.Recorded, 0, 0), qty: 40);

        msg.Content.Should().Contain("記録上いま守っている株数は 0 株です")
            .And.Contain("決済しようとした 40 株のうち 40 株には");
        msg.Content.Should().NotContain("可能性");
    }

    // T-10-1005: 照会不明以外の理由には保護の文を足さない（項目が載っていても）。重大度も従来どおり。
    [Theory]
    [InlineData(OrderDispatchForgoneReason.BrokerPositionAbsent)]
    [InlineData(OrderDispatchForgoneReason.BrokerUnavailable)]
    [InlineData(OrderDispatchForgoneReason.StopLossPriceMissing)]
    public void 照会不明以外の理由には保護の文を足さない(OrderDispatchForgoneReason reason)
    {
        foreach (var protection in new ForgoneCloseProtection?[]
        {
            null, new ForgoneCloseProtection(ForgoneCloseProtectionStatus.NoneRecorded, 0, 0),
        })
        {
            var msg = Format(protection, reason);

            msg.Severity.Should().Be(NotificationSeverity.Warning);
            msg.Content.Should().NotContain("保護レグ").And.NotContain("保護の記録");
        }
    }
}
