using RiskManagementService.Domain;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, SC-02, UC-06, #362, IADR-0151 決定2: リスク上限として設定できる値域の検証。
//
// 背景（本テスト群が守っているもの）:
//   #329 の equity 比化以降、SC-02 からのリスク上限の保存は 400 で拒否されていた。しかしそれは
//   **フロントが送るキー名が required プロパティを満たさなかった**からであり、値が危険だからではない。
//   サーバ側には値域検証が**一切無く**、`MaxOrderAmountRatio = 35000`（equity の 35,000 倍）を
//   そのまま受理していた。#362 でキー名を是正した時点でこの偶然の防壁は消えるため、本値域が唯一の関門になる。
//
// 3 点セット（docs/tests/README.md §2）: 境界値テーブル・プロパティベース・否定形をすべて置く。
public class RiskLimitBoundsTests
{
    private static RiskLimitSettings Defaults() => TradingDefaults.CreateRiskLimits();

    // ---- プロパティベース: 計画の既定値は常に受理される ----
    // 値域が既定値を弾いてしまえば、初期状態の設定を保存し直すことすらできない（#389 の契約フィクスチャ生成が
    // まさに既定値の再保存を行う）。「範囲を狭めすぎた」退行はここで落ちる。
    [Fact]
    public void 計画の既定値は値域に収まる()
    {
        RiskLimitBounds.Validate(Defaults()).Should().BeEmpty(
            "計画（05_trading-assumptions §5）の確定既定値が設定できない値域は誤りである");
    }

    // ---- 境界値テーブル: 比率 5 項目の下限直下・下限・上限一致・上限直上 ----
    // 上限は**含む**（以下）、下限 0 は**含まない**（0 は「発注できない」であって上限設定ではない）。
    [Theory]
    // 1 注文あたり発注金額上限（上限 1.00 ＝ 100%）
    [InlineData("MaxOrderAmountRatio", 0.0, false)]
    [InlineData("MaxOrderAmountRatio", 0.0001, true)]
    [InlineData("MaxOrderAmountRatio", 0.25, true)]
    [InlineData("MaxOrderAmountRatio", 1.00, true)]
    [InlineData("MaxOrderAmountRatio", 1.0001, false)]
    // 1 日あたり発注金額上限（上限 10.00 ＝ 1000%/日）
    [InlineData("MaxDailyOrderAmountRatio", 0.0, false)]
    [InlineData("MaxDailyOrderAmountRatio", 1.50, true)]
    [InlineData("MaxDailyOrderAmountRatio", 10.00, true)]
    [InlineData("MaxDailyOrderAmountRatio", 10.0001, false)]
    // 日次損失上限（上限 0.20 ＝ 20%）
    [InlineData("DailyLossLimitRatio", 0.0, false)]
    [InlineData("DailyLossLimitRatio", 0.02, true)]
    [InlineData("DailyLossLimitRatio", 0.20, true)]
    [InlineData("DailyLossLimitRatio", 0.2001, false)]
    // 1 取引あたりリスク（上限 0.10 ＝ 10%）
    [InlineData("PerTradeRiskRatio", 0.0, false)]
    [InlineData("PerTradeRiskRatio", 0.01, true)]
    [InlineData("PerTradeRiskRatio", 0.10, true)]
    [InlineData("PerTradeRiskRatio", 0.1001, false)]
    // 最大ドローダウン上限（上限 0.50 ＝ 50%）
    [InlineData("MaxDrawdownRatio", 0.0, false)]
    [InlineData("MaxDrawdownRatio", 0.10, true)]
    [InlineData("MaxDrawdownRatio", 0.50, true)]
    [InlineData("MaxDrawdownRatio", 0.5001, false)]
    public void 比率項目の境界値(string property, double value, bool accepted)
    {
        var limits = WithRatio(Defaults(), property, (decimal)value);

        RiskLimitBounds.Validate(limits).Count.Should().Be(accepted ? 0 : 1);
    }

    // ---- 境界値テーブル: 件数 2 項目（整数・下限も上限も含む） ----
    [Theory]
    [InlineData("MaxOpenPositions", 0, false)]
    [InlineData("MaxOpenPositions", 1, true)]
    [InlineData("MaxOpenPositions", 3, true)]
    [InlineData("MaxOpenPositions", 20, true)]
    [InlineData("MaxOpenPositions", 21, false)]
    [InlineData("MaxOpenPositions", -1, false)]
    [InlineData("LosingStreakThreshold", 0, false)]
    [InlineData("LosingStreakThreshold", 1, true)]
    [InlineData("LosingStreakThreshold", 5, true)]
    [InlineData("LosingStreakThreshold", 20, true)]
    [InlineData("LosingStreakThreshold", 21, false)]
    public void 件数項目の境界値(string property, int value, bool accepted)
    {
        var limits = property == "MaxOpenPositions"
            ? Defaults() with { MaxOpenPositions = value }
            : Defaults() with { LosingStreakThreshold = value };

        RiskLimitBounds.Validate(limits).Count.Should().Be(accepted ? 0 : 1);
    }

    // ---- 境界値テーブル・否定形: 連敗時サイズ縮小係数は上限 1.0 を**含まない** ----
    // 1.0 は「縮小しない」＝連敗時縮小の無効化である。統制を無効化する値を「設定値」として認めない
    // （他の項目が「以下」であるのに対しここだけ「未満」である理由）。
    [Theory]
    [InlineData(0.0, false)]
    [InlineData(0.0001, true)]
    [InlineData(0.5, true)]
    [InlineData(0.9999, true)]
    [InlineData(1.0, false)]
    [InlineData(1.0001, false)]
    [InlineData(2.0, false)]
    public void 連敗時サイズ縮小係数は1を含まない(double value, bool accepted)
    {
        var limits = Defaults() with { LosingStreakSizeFactor = (decimal)value };

        RiskLimitBounds.Validate(limits).Count.Should().Be(accepted ? 0 : 1);
    }

    // ---- 否定形（本 issue の中核）: 統制を無効化する値が受理されない ----
    // #362 が名指しした事故そのものである——利用者が旧来の感覚で「35000」と入力し、それが equity 比として
    // 保存されると **equity の 35,000 倍**が 1 注文の上限になる（＝統制の消滅）。
    [Fact]
    public void equityの35000倍を1注文上限にはできない()
    {
        var limits = Defaults() with { MaxOrderAmountRatio = 35_000m };

        var violations = RiskLimitBounds.Validate(limits);

        violations.Should().ContainSingle();
        violations[0].Should().Contain("1注文あたりの発注金額上限");
    }

    // 否定形: 比率入力を前提にした誤り（`35` ＝ equity の 35 倍）も同じく拒否される。
    // 画面は百分率入力（IADR-0151 決定1）だが、**API を直接叩けば比率がそのまま届く**ため、
    // 画面の表現に依存しないこの層で塞いでおく必要がある。
    [Theory]
    [InlineData(35.0)]
    [InlineData(100.0)]
    [InlineData(3_500.0)]
    [InlineData(35_000.0)]
    public void 比率として桁が過大な1注文上限は拒否される(double ratio)
    {
        RiskLimitBounds.Validate(Defaults() with { MaxOrderAmountRatio = (decimal)ratio })
            .Should().NotBeEmpty();
    }

    // 否定形: 複数項目が同時に範囲外なら**全件**返す（1 件で打ち切らない）。
    // 打ち切ると、直しては弾かれるを 8 回繰り返させることになる。
    [Fact]
    public void 複数項目の違反を全件返す()
    {
        var limits = Defaults() with
        {
            MaxOrderAmountRatio = 35_000m,
            MaxDailyOrderAmountRatio = 0m,
            MaxOpenPositions = 0,
            LosingStreakSizeFactor = 1.0m,
        };

        RiskLimitBounds.Validate(limits).Should().HaveCount(4);
    }

    // 否定形: `ThrowIfOutOfRange` は違反があれば必ず投げる（サービス層の不変条件）。
    [Fact]
    public void 値域外はThrowIfOutOfRangeが投げる()
    {
        var limits = Defaults() with { MaxOrderAmountRatio = 35_000m };

        var act = () => RiskLimitBounds.ThrowIfOutOfRange(limits);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 値域内はThrowIfOutOfRangeが投げない()
    {
        var act = () => RiskLimitBounds.ThrowIfOutOfRange(Defaults());

        act.Should().NotThrow();
    }

    // ---- プロパティベース: 違反の説明には必ず項目名と指定値が現れる ----
    // 400 の details をそのまま利用者へ見せるため、「何が」「いくつだったか」が読めなければ直せない。
    [Fact]
    public void 違反の説明は項目名と指定値を含む()
    {
        var limits = Defaults() with { MaxOpenPositions = 99 };

        var violations = RiskLimitBounds.Validate(limits);

        violations.Should().ContainSingle();
        violations[0].Should().Contain("保有建玉数上限").And.Contain("99");
    }

    private static RiskLimitSettings WithRatio(RiskLimitSettings limits, string property, decimal value) =>
        property switch
        {
            "MaxOrderAmountRatio" => limits with { MaxOrderAmountRatio = value },
            "MaxDailyOrderAmountRatio" => limits with { MaxDailyOrderAmountRatio = value },
            "DailyLossLimitRatio" => limits with { DailyLossLimitRatio = value },
            "PerTradeRiskRatio" => limits with { PerTradeRiskRatio = value },
            "MaxDrawdownRatio" => limits with { MaxDrawdownRatio = value },
            _ => throw new ArgumentOutOfRangeException(nameof(property), property, "未知の比率項目"),
        };
}
