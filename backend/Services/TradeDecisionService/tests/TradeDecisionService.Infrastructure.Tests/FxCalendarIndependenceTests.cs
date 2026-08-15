using System.Reflection;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.TradeDecision.Infrastructure.Tests;

/// <summary>
/// FR-10, FR-17, #381, ADR-0022 <b>決定3</b>, IADR-0194:
/// <b>為替の鮮度判定が営業日カレンダーを持たないこと</b>を構造で固定する（否定形）。
/// <para>
/// 計画は「鮮度は営業日カレンダーではなく<b>冗長化</b>で担保する」と決めている。日銀は日本の営業日、
/// FRED は米国の公表スケジュールに従い、<b>日本の連休中も FRED 側は動いている</b>。カレンダーを持たないため
/// <b>カレンダーの誤りに起因する誤判定が原理的に起きない</b>——これが決定3 が選んだ性質そのものである。
/// </para>
/// <para>
/// 🔴 <b>本 issue 起票時の実測（2026-08-07）では、この不変条件を固定するテストが存在しなかった。</b>
/// <c>MarketCalendar</c> は同じ <c>Composable/Adapters/</c> 配下に実在するのに、FX の鮮度判定経路は
/// これを参照していない——<b>「参照していない」ことを機械が守る仕組みが無かった</b>。
/// 参照を足しても何も落ちないなら、決定3 は規約でしか守られていない。
/// </para>
/// <para>
/// <b>なぜ「参照しないこと」を検査するのか</b>: 値の一致を見るテストでは捕まらない。カレンダーを
/// 導入しても平常時のレートは同じ値になり、<b>祝日の並びが特殊な日にだけ挙動が変わる</b>。
/// つまり<b>普段は緑のまま、稀に誤判定する</b>——最も見つけにくい壊れ方である。
/// </para>
/// </summary>
public class FxCalendarIndependenceTests
{
    // 為替レートの取得と鮮度判定を構成する型（合成の全段）。
    private static readonly Type[] FxPipelineTypes =
    [
        typeof(BojFxRateSource),
        typeof(FredFxRateSource),
        typeof(FallbackFxRateSource),
        typeof(CachingFxRateSource),
        typeof(FxRateSourceFactory),
        typeof(FxOptions),
    ];

    [Fact]
    public void FX経路の型は営業日カレンダーを参照しない()
    {
        var offenders = FxPipelineTypes
            .Where(ReferencesMarketCalendar)
            .Select(t => t.Name)
            .ToArray();

        offenders.Should().BeEmpty(
            "ADR-0022 決定3 は営業日カレンダーを持たないと決めている（カレンダーの誤りが統制の誤判定に直結するため）。"
            + "鮮度は冗長化（日銀＋FRED）で担保する");
    }

    /// <summary>
    /// 検査そのものが効いていることの実証（ミューテーション相当）。
    /// <para>
    /// <b>「参照が無いこと」を確かめるテストは、検出器が壊れていても緑になる</b>——対象がゼロ件だからである。
    /// そこで<b>意図的に参照を持つ型</b>を食わせ、確かに検出できることを同じ経路で示す。
    /// これが無いと <c>ReferencesMarketCalendar</c> が常に <c>false</c> を返すよう壊れても誰も気付かない。
    /// </para>
    /// </summary>
    [Fact]
    public void 検査は営業日カレンダーへの参照を実際に検出できる()
    {
        ReferencesMarketCalendar(typeof(DeliberateCalendarUser)).Should().BeTrue(
            "検出器が働いていることを示す。これが false なら上のテストはゼロ件を数えているだけである");
    }

    /// <summary>
    /// 型がコンストラクタ引数・フィールド・プロパティ・メソッドシグネチャのいずれかで
    /// <see cref="IMarketCalendar"/> または <see cref="MarketCalendar"/> に触れているかを見る。
    /// <b>メソッド本体の IL までは見ない</b>——静的メソッド経由の間接利用は検出できない。
    /// 本検査が守るのは「型の依存として現れる形」であり、それが実際の導入経路である
    /// （DI で注入するには引数・フィールドのいずれかに現れる）。
    /// </summary>
    private static bool ReferencesMarketCalendar(Type type)
    {
        const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        bool IsCalendar(Type t) =>
            t == typeof(IMarketCalendar) || t == typeof(MarketCalendar)
            || (t.IsGenericType && t.GetGenericArguments().Any(IsCalendar));

        return type.GetConstructors(All).SelectMany(c => c.GetParameters()).Any(p => IsCalendar(p.ParameterType))
            || type.GetFields(All).Any(f => IsCalendar(f.FieldType))
            || type.GetProperties(All).Any(p => IsCalendar(p.PropertyType))
            || type.GetMethods(All).Any(m =>
                IsCalendar(m.ReturnType) || m.GetParameters().Any(p => IsCalendar(p.ParameterType)));
    }

    /// <summary>検出器が働くことを示すための、意図的にカレンダーへ依存した型。</summary>
    private sealed class DeliberateCalendarUser(IMarketCalendar calendar)
    {
        public IMarketCalendar Calendar { get; } = calendar;
    }
}
