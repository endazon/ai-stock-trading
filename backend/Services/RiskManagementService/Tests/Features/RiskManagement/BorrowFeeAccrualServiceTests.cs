using System.Reflection;
using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Common.Abstractions;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetShortSellingStatus;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, FR-11, SC-03, UC-06, #465, ADR-0027, IADR-0183:
// 借株料の累計（**記録側**）。ADR-0016 決定15 が表示と報告だけを要求し、その入力を作る要件が
// 欠けていた空白を ADR-0027 が 5 決定で埋めた。
//
// 🔴 **供給（実際に積み始めること）はまだ始まっていない。** ADR-0027 決定6 が ADR-0026 の
// PoC 項目 9（`ShortFeeRate` の単位確定・期限 2026-08-31）を前提としており、**単位を取り違えると
// 累計は 100 倍ずれる**。遮断が効いていることを T-10-275 / T-10-276 が構造で固定する。
public class BorrowFeeAccrualServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 9, 0, 0, TimeSpan.Zero);

    private static (BorrowFeeAccrualService Service, InMemoryBorrowFeeAccrualStore Store) NewService()
    {
        var store = new InMemoryBorrowFeeAccrualStore();
        return (new BorrowFeeAccrualService(store), store);
    }

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    // T-10-269: FR-10, ADR-0027 決定1/決定2 —— **建玉ごとに日次で積み、口座合計は合算で導出する。**
    // 銘柄別・口座全体を別に積むストアは存在しない（決定2「合算で導出する」）。
    [Fact]
    public void 建玉ごとの日次計上が積み上がり口座合計は合算で導出される()
    {
        var (service, _) = NewService();

        // AAPL: 2 日分（評価額 36,500 USD・年率 0.10 → 1 日 10 USD）
        service.Accrue("AAPL", Market.UnitedStates, D(2026, 8, 11), 0.10m, 36_500m, Now);
        service.Accrue("AAPL", Market.UnitedStates, D(2026, 8, 12), 0.10m, 36_500m, Now);
        // GME: 1 日分（評価額 7,300 USD・年率 0.20 → 1 日 4 USD）
        service.Accrue("GME", Market.UnitedStates, D(2026, 8, 12), 0.20m, 7_300m, Now);

        var apple = service.SummarizePosition(
            new BorrowFeePositionKey("AAPL", Market.UnitedStates), D(2026, 8, 1), D(2026, 8, 31));
        var account = service.SummarizeAccount(D(2026, 8, 1), D(2026, 8, 31));

        apple.AmountUsd.Should().Be(20m);
        apple.AccruedDayCount.Should().Be(2);

        // 口座合計は建玉ごとの合算そのものである（別に積んだ値ではない）。
        account.AmountUsd.Should().Be(24m);
        account.AccruedDayCount.Should().Be(3);

        var byPosition = service.SummarizeByPosition(D(2026, 8, 1), D(2026, 8, 31));
        byPosition.Values.Sum(s => s.AmountUsd).Should().Be(account.AmountUsd);
    }

    // T-10-270: FR-10, ADR-0027 決定1 —— **冪等**。同じ (銘柄・市場・取引日) を二度計上しても金額が狂わない。
    // 再送・リトライで二重計上すると、日次の内訳（監査）と累計が食い違う。
    [Fact]
    public void 同じ建玉の同じ取引日を二度計上しても金額が狂わない()
    {
        var (service, _) = NewService();

        var first = service.Accrue("AAPL", Market.UnitedStates, D(2026, 8, 12), 0.10m, 36_500m, Now);
        var second = service.Accrue("AAPL", Market.UnitedStates, D(2026, 8, 12), 0.10m, 36_500m, Now.AddHours(1));

        first.Should().NotBeNull();
        // **2 度目はイベントも出さない**——出すと監査の日次内訳が同じ日について 2 行になり、合計と食い違う。
        second.Should().BeNull();

        var summary = service.SummarizeAccount(D(2026, 8, 1), D(2026, 8, 31));
        summary.AmountUsd.Should().Be(10m);
        summary.AccruedDayCount.Should().Be(1);
    }

    // T-10-271: FR-10, ADR-0027 決定4 —— **計上日の料率**で評価する（建玉時の料率で固定しない）。
    // 建玉時で固定すると、長期保有の建玉で実費から乖離する。
    [Fact]
    public void 計上日の料率で評価される()
    {
        var (service, _) = NewService();

        // 初日は年率 0.10、翌日に料率が 0.20 へ変動した。
        service.Accrue("AAPL", Market.UnitedStates, D(2026, 8, 11), 0.10m, 36_500m, Now);
        service.Accrue("AAPL", Market.UnitedStates, D(2026, 8, 12), 0.20m, 36_500m, Now);

        var summary = service.SummarizeAccount(D(2026, 8, 11), D(2026, 8, 12));

        // 建玉時の 0.10 で固定していれば 20 USD になる。計上日の料率を用いるので 10 + 20 = 30 USD。
        summary.AmountUsd.Should().Be(30m);
    }

    // T-10-272（**否定形・最重要**）: FR-10, ADR-0027 決定4 ——
    // **料率が取得できなかった日を 0 として積まない。**
    //
    // 0 を積むと「その日は費用が発生しなかった」と読め、**Stage 1 の「借株料は 1 円も掛かっていない」という
    // 誤読が構造的に起こる**（計画が名指しで禁じた向き）。
    [Fact]
    public void 料率が取得できなかった日は0として積まれず未供給として記録される()
    {
        var (service, store) = NewService();

        service.Accrue("AAPL", Market.UnitedStates, D(2026, 8, 11), 0.10m, 36_500m, Now);
        var unavailable = service.MarkUnavailable(
            "AAPL", Market.UnitedStates, D(2026, 8, 12), "料率照会に失敗（TrdGetMarginRatio）", Now);

        unavailable.Should().NotBeNull();

        var summary = service.SummarizeAccount(D(2026, 8, 11), D(2026, 8, 12));

        // 合計は計上できた 1 日分のまま（未供給の日は 1 円も足さない）。
        summary.AmountUsd.Should().Be(10m);
        summary.AccruedDayCount.Should().Be(1);
        // **未供給の日数として現れる**——「費用が発生しなかった」と区別できる唯一の手段である。
        summary.UnsuppliedDayCount.Should().Be(1);
        summary.HasUnsuppliedDays.Should().BeTrue();

        // 台帳側でも未供給の日は計上表に 1 行も現れない（0 円の行を書いていない）。
        store.GetAccrualsBetween(D(2026, 8, 12), D(2026, 8, 12)).Should().BeEmpty();
        store.GetUnavailableDaysBetween(D(2026, 8, 12), D(2026, 8, 12)).Should().ContainSingle();
    }

    // T-10-272 続き: 未供給の記録も**冪等**である（同じ日を二度記録しても日数が狂わない）。
    [Fact]
    public void 未供給の日を二度記録しても日数が狂わない()
    {
        var (service, _) = NewService();

        service.MarkUnavailable("AAPL", Market.UnitedStates, D(2026, 8, 12), "照会失敗", Now);
        var second = service.MarkUnavailable("AAPL", Market.UnitedStates, D(2026, 8, 12), "照会失敗", Now.AddHours(1));

        second.Should().BeNull();
        service.SummarizeAccount(D(2026, 8, 1), D(2026, 8, 31)).UnsuppliedDayCount.Should().Be(1);
    }

    // T-10-273（**否定形・構造**）: FR-10, #465 受け入れ基準 ——
    // **未供給の日数を伴わない累計を返す API を公開面に置かない。**
    //
    // 受け入れ基準「未供給の日がある期間の累計が、未供給の日数を伴わずに表示されない」は、
    // 値の検査では守れない（新しい集計 API が 1 つ生えれば破れる）。**型で守る。**
    [Fact]
    public void 金額だけを返す集計APIが公開面に存在しない()
    {
        var returnTypes = typeof(BorrowFeeAccrualService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.ReturnType);

        returnTypes.Should().NotContain(
            t => t == typeof(decimal) || t == typeof(decimal?),
            "累計は必ず未供給日数を伴って返す（ADR-0027 決定4・issue #465 の受け入れ基準）");

        // 集計の戻り値は 1 種類（BorrowFeeAccrualSummary）であり、必ず未供給日数を運ぶ。
        typeof(BorrowFeeAccrualSummary).GetProperty(nameof(BorrowFeeAccrualSummary.UnsuppliedDayCount))
            .Should().NotBeNull();
    }

    // T-10-274: FR-10, ADR-0027 決定3 —— **按分しない。** 計上日の属する日・月へ帰属する。
    // **生涯累計と月次合計は別の値である**（同じ語で呼ばない）。
    [Fact]
    public void 月をまたぐ建玉を按分せず生涯累計と月次合計が別の値として取れる()
    {
        var (service, _) = NewService();
        var position = new BorrowFeePositionKey("AAPL", Market.UnitedStates);

        // 7/31 に建て、8/1・8/2 も保有し続けた建玉。
        service.Accrue("AAPL", Market.UnitedStates, D(2026, 7, 31), 0.10m, 36_500m, Now);
        service.Accrue("AAPL", Market.UnitedStates, D(2026, 8, 1), 0.10m, 36_500m, Now);
        service.Accrue("AAPL", Market.UnitedStates, D(2026, 8, 2), 0.10m, 36_500m, Now);

        var july = service.SummarizePosition(position, D(2026, 7, 1), D(2026, 7, 31));
        var august = service.SummarizePosition(position, D(2026, 8, 1), D(2026, 8, 31));
        var lifetime = service.SummarizePosition(position, D(2026, 7, 31), D(2026, 8, 31));

        // 前月分は前月の月報に、当月分は当月の月報に載る（按分すると 7 月分が減って 8 月分が増える）。
        july.AmountUsd.Should().Be(10m);
        august.AmountUsd.Should().Be(20m);

        // 生涯累計は月次合計と一致しない。**両者を同じ語で呼ばない**（決定3）。
        lifetime.AmountUsd.Should().Be(30m);
        lifetime.AmountUsd.Should().NotBe(august.AmountUsd);
    }

    // T-10-275（**否定形・構造**）: FR-10, ADR-0027 決定6, ADR-0026 PoC 項目 9, IADR-0158 決定3 と同型 ——
    // **単位が未確定の `ShortFeeRate` を計上へ写像しない。**
    //
    // 計上サービス・型・ストアの公開面に `ShortFeeRate` を含む名前が生えた時点で赤くなる。
    // **単位を取り違えると累計は 100 倍ずれる**（ADR-0027 決定6）。
    [Fact]
    public void 計上の公開面に単位未確定のShortFeeRateが現れない()
    {
        var surface = PublicSurfaceNames(typeof(BorrowFeeAccrualService))
            .Concat(PublicSurfaceNames(typeof(IBorrowFeeAccrualStore)))
            .Concat(PublicSurfaceNames(typeof(BorrowFeeAccrual)))
            .ToList();

        surface.Should().NotContain(
            name => name.Contains("ShortFee", StringComparison.OrdinalIgnoreCase),
            "借株料の単位が確定するまで moomoo の ShortFeeRate を計上へ写像してはならない（ADR-0027 決定6）");

        // 受ける契約は「**単位が確定した年率（比率）**」だけである（IADR-0158 決定3 と同じ語彙）。
        surface.Should().Contain("rateAnnual");
    }

    // T-10-276（**否定形・構造**）: FR-10, SC-03, ADR-0027 決定6 —— **供給を開始していないことを固定する。**
    //
    // SC-03 の集約に累計ストアを結線した時点で供給が始まる。**結線されていないこと**を構造で見る
    // （`BorrowFeeAvailability` の値だけを見ると、結線後に既定値が変わっただけでも緑のまま通り得る）。
    [Fact]
    public void SC03の集約は累計ストアを依存に持たない()
    {
        var dependencies = typeof(ShortSellingStatusService)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType);

        dependencies.Should().NotContain(
            typeof(IBorrowFeeAccrualStore),
            "供給の開始は ADR-0026 PoC 項目 9（ShortFeeRate の単位確定）の成立後である（ADR-0027 決定6）");
        dependencies.Should().NotContain(typeof(BorrowFeeAccrualService));
    }

    // T-10-277: FR-11, ADR-0027 決定1/決定4 —— **日次の計上額を監査へ残す。**
    // 計上と未供給は**別のイベント**であり、要約が「0 円だった」と「取れなかった」を必ず書き分ける。
    [Fact]
    public void 計上と未供給がそれぞれ別のイベントとして返る()
    {
        var (service, _) = NewService();

        var accrued = service.Accrue("AAPL", Market.UnitedStates, D(2026, 8, 12), 0.10m, 36_500m, Now);
        var unavailable = service.MarkUnavailable("GME", Market.UnitedStates, D(2026, 8, 12), "照会失敗", Now);

        accrued.Should().NotBeNull();
        accrued!.AmountUsd.Should().Be(10m);
        accrued.RateAnnual.Should().Be(0.10m);
        accrued.TradingDay.Should().Be(D(2026, 8, 12));

        unavailable.Should().NotBeNull();
        unavailable!.Reason.Should().Be("照会失敗");

        // **未供給のイベントは金額の欄そのものを持たない**（0 円として合計へ混ぜられない）。
        PublicSurfaceNames(unavailable.GetType()).Should().NotContain(
            name => name.Contains("Amount", StringComparison.OrdinalIgnoreCase));
    }

    // ADR-0027 決定5: 実弾解禁前の机上確認は「1 日分の計上額が**料率 × 建玉評価額 ÷ 365**と一致すること」。
    // 分母を取り違えると計上額が丸ごとずれるため、式そのものをテストで固定する。
    [Theory]
    [InlineData(0.10, 36500, 10)]
    [InlineData(0.20, 36500, 20)]
    [InlineData(0.015, 100000, 4.10958904109589041095890411)]
    public void 一日分の計上額は料率かける建玉評価額わる365である(double rate, double value, double expected)
    {
        var (service, _) = NewService();

        var accrued = service.Accrue(
            "AAPL", Market.UnitedStates, D(2026, 8, 12), (decimal)rate, (decimal)value, Now);

        accrued!.AmountUsd.Should().BeApproximately((decimal)expected, 0.0000001m);
        BorrowFeeAccrualService.DaysPerYear.Should().Be(365);
    }

    // 負の料率・負の評価額は計上しない（費用が「戻る」向きの記録を台帳へ入れない）。
    [Fact]
    public void 負の料率や負の評価額では計上しない()
    {
        var (service, store) = NewService();

        var negativeRate = () => service.Accrue("AAPL", Market.UnitedStates, D(2026, 8, 12), -0.1m, 100m, Now);
        var negativeValue = () => service.Accrue("AAPL", Market.UnitedStates, D(2026, 8, 12), 0.1m, -100m, Now);

        negativeRate.Should().Throw<ArgumentOutOfRangeException>();
        negativeValue.Should().Throw<ArgumentOutOfRangeException>();
        store.GetAccrualsBetween(D(2026, 8, 1), D(2026, 8, 31)).Should().BeEmpty();
    }

    // 型の公開面（メンバ名 ＋ メソッド・コンストラクタの引数名）を列挙する
    // （`CostCalculatorTests.PublicSurfaceNames`〔IADR-0158 決定3〕と同じ形）。
    private static IEnumerable<string> PublicSurfaceNames(Type type) =>
        type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .SelectMany(member => new[] { member.Name }.Concat(
                member is MethodBase method
                    ? method.GetParameters().Select(p => p.Name ?? string.Empty)
                    : Enumerable.Empty<string>()));
}
