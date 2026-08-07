using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Domain.Tests;

// FR-20 (1), SC-02, UC-06, #422, IADR-0161:
// **「設定は保存できるが、発注は段階が実弾を既定とするまで拒否する」を 1 か所で同時に固定する。**
//
// 計画（FR-20 の 2026-08-07 追記 (1)）:
//   「『発注先の変更が保存できる』ことは『発注できる』ことを意味しない。**Stage 1 のまま実弾を選んでも
//     発注は行われない**（段階ゲートを設定 1 つで飛び越えられるなら統制ではないため）」
//
// 保存側（BrokerProviderChange）と発注側（RiskEvaluator）は別のクラスであり、**それぞれのテストは
// 既にある**。にもかかわらず本ファイルを置くのは、この 2 つが**同時に成り立つこと**こそが裁定の内容
// だからである。片方だけを見ていると「保存できる＝統制が無い」「発注が止まる＝保存できないはず」と
// いう 2 通りの誤読のどちらにも倒れ得る（実際、画面は前者に倒れて「段階ゲートを飛ばしている」とだけ
// 表示していた）。
//
// **拒否理由は新設していない。** StageProhibitsLiveTrading（既存・クラス B）と原因（段階が実弾を
// 既定としない）も解除条件（段階の昇格）も同一である（ADR-0016 決定10 の規律）。
public class Stage1LiveProviderOrderRejectionTests
{
    private static readonly StageSettings Stage1 =
        new(TradingStage.Stage1Simulate, BrokerProvider.MoomooSimulate, TradingDefaults.FullCapitalCapRatio);

    private static readonly StageSettings Stage2 =
        new(TradingStage.Stage2MinimalLive, BrokerProvider.MoomooReal, TradingDefaults.FullCapitalCapRatio);

    private static PortfolioSnapshot Snapshot() => new()
    {
        Capital = 100_000m,
        SymbolsTradedToday = new HashSet<(string, Market)>(),
    };

    private static OrderIntent LiveBuy() => new(
        "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
        BrokerProvider.MoomooReal, Quantity: 1, Price: 100m, PositionEffect.Open);

    // **本 issue の中核**: Stage 1 のまま実弾を選ぶ操作は「保存は通る」「発注は止まる」の両方が同時に成り立つ。
    [Fact]
    public void Stage1のまま実弾を選ぶと保存は受理され発注は段階ゲートで拒否される()
    {
        // 1. 保存: 同意と「REAL」の入力が揃えば受理される（段階との組み合わせは保存を妨げない）。
        var change = BrokerProviderChange.Evaluate(
            new BrokerProviderChangeRequest(BrokerProvider.MoomooReal, "実弾へ移行する", true, "REAL"),
            Stage1);

        change.Accepted.Should().BeTrue("計画は保存を妨げないと定める（05_screens SC-02）");
        change.SkipsStageGate.Should().BeTrue();

        // 2. 発注: 保存されたその発注先で注文を出しても 1 件も通らない。
        var settings = TradingDefaults.CreateSettings() with
        {
            Stage = Stage1,
            BrokerProvider = BrokerProvider.MoomooReal, // 上で保存が受理された結果の状態
        };

        var screening = RiskEvaluator.Evaluate(LiveBuy(), settings, Snapshot());

        screening.IsApproved.Should().BeFalse(
            "段階ゲートを設定 1 つで飛び越えられるなら統制ではない（FR-20 (1)）");
        screening.Reasons.Should().Contain(RejectionReason.StageProhibitsLiveTrading);
    }

    // 拒否理由の分類は **クラス B**（既存）である。新設していないことを固定する
    // （新しい理由コードを足すと監査集計・通知文言・画面の対応表がすべて分岐する）。
    [Fact]
    public void 拒否理由はクラスBのStageProhibitsLiveTradingである()
    {
        RejectionReasonClassification.ClassOf(RejectionReason.StageProhibitsLiveTrading)
            .Should().Be(RejectionReasonClass.B);
    }

    // 段階が実弾を既定とする Stage 2 では、同じ注文が段階ゲートでは止まらない
    // （「段階が実弾を既定とするまで」という条件が実際に効いていることの裏面）。
    [Fact]
    public void 段階が実弾を既定とすれば段階ゲートでは止まらない()
    {
        var settings = TradingDefaults.CreateSettings() with
        {
            Stage = Stage2,
            BrokerProvider = BrokerProvider.MoomooReal,
        };

        RiskEvaluator.Evaluate(LiveBuy(), settings, Snapshot())
            .Reasons.Should().NotContain(RejectionReason.StageProhibitsLiveTrading);
    }

    // 全段階 × 全発注先の走査: **実弾の注文が通り得るのは段階の既定が実弾のときだけ**。
    // 個別の [Fact] を並べるより、規則そのものを固定する（将来 Stage が増えても抜けない）。
    [Fact]
    public void 実弾の注文が段階ゲートを通るのは段階の既定が実弾のときに限る()
    {
        foreach (var stage in Enum.GetValues<TradingStage>())
        {
            foreach (var stageMode in Enum.GetValues<BrokerProvider>())
            {
                var settings = TradingDefaults.CreateSettings() with
                {
                    Stage = new StageSettings(stage, stageMode, TradingDefaults.FullCapitalCapRatio),
                    BrokerProvider = BrokerProvider.MoomooReal,
                };

                var rejected = RiskEvaluator.Evaluate(LiveBuy(), settings, Snapshot())
                    .Reasons.Contains(RejectionReason.StageProhibitsLiveTrading);

                rejected.Should().Be(
                    stageMode != BrokerProvider.MoomooReal,
                    "stage={0} stageMode={1}",
                    stage,
                    stageMode);
            }
        }
    }
}
