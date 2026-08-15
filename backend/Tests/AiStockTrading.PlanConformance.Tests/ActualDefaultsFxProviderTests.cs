using System.Collections.Immutable;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.PlanConformance.Tests;

/// <summary>
/// FR-10, ADR-0022, IADR-0135 決定6, #378:
/// 為替レート源の provider 集合の**抽出そのもの**を検査する（否定形）。
/// <para>
/// 計画適合検査は「実装から機械的に抽出した実際値」を計画値と突き合わせる機構であり、
/// <b>抽出が誤ると機構は誤った値のまま緑になる</b>。値の一致だけを見るテスト
/// （<see cref="PlanConformanceTests"/>）はこの失敗モードを検知できないため、
/// 抽出の入力を差し替えられる形（<see cref="ActualDefaults.FxProviderNamesFrom"/>）にして
/// 「拾ってはいけないものを拾わない」ことを直接証明する。
/// </para>
/// </summary>
public class ActualDefaultsFxProviderTests
{
    // 本命の否定形: provider と無関係な public const string を実装が足しても、provider 集合に混入しない。
    // 旧実装（型の public const string を全件収集し "none" だけを除く形）はこのテストで落ちる
    // ——SectionName / LogPrefix / ConfigurationKey が provider として数えられるため。
    [Fact]
    public void provider集合に無関係なpublic_const_stringは混入しない()
    {
        ActualDefaults.FxProviderNamesFrom(typeof(FactoryWithUnrelatedConstants))
            .Should().Be("fred", "抽出は ProviderNames メンバだけを読み、他の定数は provider ではない");
    }

    // 抽出の出どころが単一メンバであることの裏面: 定数がいくら provider らしく見えても、
    // 宣言メンバが無ければ「集合が読めない」ことを値として返す（黙って定数から組み立てない）。
    // 型名の変更・メンバの削除は (not found) として実際値に現れ、計画適合検査を赤にする。
    [Fact]
    public void 宣言メンバが無ければ定数からは組み立てず不在をそのまま値とする()
    {
        ActualDefaults.FxProviderNamesFrom(typeof(FactoryWithoutDeclaration))
            .Should().Be($"(FxRateSourceFactory.{ActualDefaults.FxProviderNamesMemberName} not found)");
    }

    [Fact]
    public void 型が見つからなければ不在をそのまま値とする()
    {
        ActualDefaults.FxProviderNamesFrom(null).Should().Be("(type FxRateSourceFactory not found)");
    }

    // 「未接続（no-op）」は情報源ではないため集合に数えない。集合が none だけなら情報源はゼロである。
    [Fact]
    public void 未接続を表すnoneは情報源として数えない()
    {
        ActualDefaults.FxProviderNamesFrom(typeof(FactoryWithNoneOnly))
            .Should().Be("(no fx provider names declared)");
    }

    // 上記の seam が実装の本物の型に結線されていること（偽の型だけ緑という状態を作らない）。
    // 実際値が変わったときは KnownPlanDeviations の更新が別途強制される。
    //
    // #381, IADR-0194: 日銀アダプタの新設により「fred のみ」→「boj, fred」へ変わった。
    // **集合は Sorted で返るため boj が先に来る。これは順位ではない**——順位（日銀が第一）は
    // 値ではなく振る舞いであり、FallbackFxRateSourceTests / FxRateSourceFactoryTests が担保する。
    [Fact]
    public void 実装スナップショットのprovider集合は日銀とFREDである()
    {
        ActualDefaults.Snapshot()["Fx.RateSourceProviders"].Should().Be("boj, fred");
    }

    /// <summary>
    /// 実装が将来足し得る「provider と無関係な <c>public const string</c>」を模した型。
    /// 旧方式ではこれらが provider として収集された。
    /// </summary>
    private static class FactoryWithUnrelatedConstants
    {
        public const string SectionName = "Fx";
        public const string LogPrefix = "fx-rate";
        public const string ConfigurationKey = "Fx:Provider";
        public const string None = "none";
        public const string Fred = "fred";

        public static readonly ImmutableArray<string> ProviderNames = [None, Fred];
    }

    /// <summary>provider らしい定数は持つが、集合の宣言メンバを持たない型。</summary>
    private static class FactoryWithoutDeclaration
    {
        public const string None = "none";
        public const string Fred = "fred";
    }

    /// <summary>未接続しか宣言していない型（＝情報源がゼロ）。</summary>
    private static class FactoryWithNoneOnly
    {
        public static readonly ImmutableArray<string> ProviderNames = ["none"];
    }
}
