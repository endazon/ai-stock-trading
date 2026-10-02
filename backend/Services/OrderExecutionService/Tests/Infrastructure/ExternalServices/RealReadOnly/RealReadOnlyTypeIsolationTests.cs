using System.Reflection;
using AiStockTrading.Shared.Contracts.Ports;
using AwesomeAssertions;
using Moomoo.OpenApi.Pb;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ProbeOrderFee;
using OrderExecutionService.Features.OrderExecution.QueryShortPermit;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.ExternalServices.RealReadOnly;
using Xunit;

namespace OrderExecutionService.Tests;

// T-10-2063, FR-10, #1000, IADR-0482 決定2: 実弾口座の読み取り専用の照会の**型の上の切り離し**。
//   - 照会のクライアントが実装するのは照会ポート（IShortPermitSource）と SDK のコールバックだけで、発注・訂正・取消の型を実装しない。
//   - 照会側の型のどのメンバ（フィールド・コンストラクタ・メソッド・プロパティ）も、発注の型を受け取らない。
//   - 接続のシームの面は口座一覧と TrdGetMarginRatio だけで、発注・訂正・解錠の要求の型を受け取るメソッドが無い。
// ソースの走査（識別子）による検査は Architecture.Tests の RealReadOnlyQueryIsolationTests が持つ（二重化）。
public class RealReadOnlyTypeIsolationTests
{
    private const string RealReadOnlyNamespace = "OrderExecutionService.Infrastructure.ExternalServices.RealReadOnly";

    // 発注経路の型（照会側から到達してはならない）。
    private static readonly Type[] OrderPathTypes =
    [
        typeof(IMoomooTradeClient), typeof(MMApiMoomooTradeClient), typeof(IMoomooTradeConnection),
        typeof(IMoomooTradeConnectionFactory), typeof(IBrokerAdapter), typeof(MoomooBrokerAdapter),
        typeof(IClientOrderIdBroker), typeof(IOrderFeeQuery),
        typeof(TrdPlaceOrder.Request), typeof(TrdModifyOrder.Request), typeof(TrdPlaceComboOrder.Request), typeof(TrdUnlockTrade.Request),
    ];

    private static IEnumerable<Type> RealReadOnlyTypes() =>
        typeof(MMApiRealMarginQueryClient).Assembly.GetTypes()
            .Where(t => t.Namespace == RealReadOnlyNamespace && !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)));

    [Fact]
    public void 照会のクライアントは照会ポートとSDKのコールバックだけを実装する()
    {
        var implemented = typeof(MMApiRealMarginQueryClient).GetInterfaces().Select(i => i.Name).ToHashSet();

        implemented.Should().BeEquivalentTo(["MMSPI_Trd", "MMSPI_Conn", nameof(IShortPermitSource), nameof(IDisposable)]);
        typeof(IMoomooTradeClient).IsAssignableFrom(typeof(MMApiRealMarginQueryClient)).Should().BeFalse();
        typeof(IBrokerAdapter).IsAssignableFrom(typeof(MMApiRealMarginQueryClient)).Should().BeFalse();
    }

    [Fact]
    public void 照会側の型のメンバは発注の型を受け取らない()
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var types = RealReadOnlyTypes().ToList();
        types.Should().Contain(typeof(MMApiRealMarginQueryClient), "走査の対象が空でないこと");

        var violations = new List<string>();
        foreach (var type in types)
        {
            var used = type.GetFields(all).Select(f => (f.Name, f.FieldType))
                .Concat(type.GetProperties(all).Select(p => (p.Name, p.PropertyType)))
                .Concat(type.GetConstructors(all).SelectMany(c => c.GetParameters()).Select(p => (p.Name ?? "?", p.ParameterType)))
                .Concat(type.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => (m.Name, p.ParameterType)).Append((m.Name, m.ReturnType))));
            violations.AddRange(used
                .Where(u => OrderPathTypes.Any(forbidden => Mentions(u.Item2, forbidden)))
                .Select(u => $"{type.Name}.{u.Item1}: {u.Item2.Name}"));
            violations.AddRange(type.GetInterfaces().Where(i => OrderPathTypes.Any(f => f == i)).Select(i => $"{type.Name} : {i.Name}"));
        }

        violations.Should().BeEmpty("実弾口座の読み取り専用の照会から発注経路へ到達できてはならない（IADR-0482 決定2）");
    }

    [Fact]
    public void 接続のシームの面は口座一覧と借株可否の照会だけ()
    {
        var methods = typeof(IMoomooMarginQueryConnection).GetMethods().Select(m => m.Name).ToHashSet();

        methods.Should().BeEquivalentTo(
            ["SetClientInfo", "SetConnCallback", "SetTrdCallback", "SetRsaPrivateKey", "InitConnect", "Close",
             "GetAccList", "GetMarginRatio"],
            "発注・訂正・取消・解錠の面を足さない（足すなら別の裁定が要る）");
        typeof(IMoomooTradeConnection).IsAssignableFrom(typeof(IMoomooMarginQueryConnection)).Should().BeFalse();
        typeof(IMoomooMarginQueryConnection).IsAssignableFrom(typeof(IMoomooTradeConnection)).Should().BeFalse();
    }

    private static bool Mentions(Type used, Type forbidden)
    {
        if (used == forbidden || (forbidden.IsAssignableFrom(used) && used != typeof(object)))
            return true;
        if (used.HasElementType && Mentions(used.GetElementType()!, forbidden))
            return true;
        return used.IsGenericType && used.GetGenericArguments().Any(a => Mentions(a, forbidden));
    }
}
