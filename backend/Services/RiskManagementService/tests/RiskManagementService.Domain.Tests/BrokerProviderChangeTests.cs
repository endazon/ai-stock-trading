using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Domain.Tests;

// FR-20, FR-13, SC-02, INDEX 決定 46, #334, IADR-0141:
// 実弾（moomoo REAL）への切替に明示的な確認操作を強制する純ロジックの検証。
//
// 計画（FR-20 (1)・05_screens SC-02）:
//   「チェックボックスの同意と『REAL』の文字入力の**両方**が揃うまで切替ボタンを無効化する。
//     **既定の『OK』ボタン 1 押しで通過させない**」
//
// 本テスト群の主眼は**否定形**である——確認が欠けたときに「切り替わらない」ことを固定する。
// 肯定形（揃えば通る）だけを書くと、判定を常に true にする退行を検知できない。
public class BrokerProviderChangeTests
{
    private static readonly StageSettings Stage1 =
        new(TradingStage.Stage1Simulate, BrokerProvider.MoomooSimulate, TradingDefaults.FullCapitalCapRatio);

    private static readonly StageSettings Stage2 =
        new(TradingStage.Stage2MinimalLive, BrokerProvider.MoomooReal, TradingDefaults.Stage2MinimalLiveCapitalCapRatio);

    // 否定形（本 issue の中核）: 「OK」1 押し＝理由だけを添えた要求では実弾へ切り替わらない。
    [Fact]
    public void 実弾への切替は理由だけでは受理されない()
    {
        var request = new BrokerProviderChangeRequest(BrokerProvider.MoomooReal, "実弾へ移行する");

        var assessment = BrokerProviderChange.Evaluate(request, Stage2);

        assessment.Accepted.Should().BeFalse(
            "計画は「既定の『OK』ボタン 1 押しで切り替えられてはならない」と定める（FR-20 (1)）");
        assessment.Rejections.Should().Contain(BrokerProviderChangeRejection.LiveAcknowledgementMissing);
        assessment.Rejections.Should().Contain(BrokerProviderChangeRejection.LivePhraseMismatch);
    }

    // 否定形: 同意だけ・文字入力だけの片方では通らない（「両方」が計画の要求）。
    [Theory]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(false, "REAL")]
    public void 実弾への切替は同意と文字入力の片方だけでは受理されない(bool acknowledged, string? phrase)
    {
        var request = new BrokerProviderChangeRequest(
            BrokerProvider.MoomooReal, "実弾へ移行する", acknowledged, phrase);

        BrokerProviderChange.Evaluate(request, Stage2).Accepted.Should().BeFalse();
    }

    // 否定形・境界値: 照合は前後空白を除いた完全一致であり、**大文字小文字を区別する**。
    // **FR-20 の 2026-08-07 追記 (2) が確定させた規則である**（#422。「前後空白のみを除いた完全一致とし
    // 大文字小文字を区別する。`real` は受理しない。明記しないと後から緩める変更が『利便性の改善』として
    // 入り込む」）。IADR-0141 決定2 が先に同じ規則へ到達しており、裁定はそれを追認した。
    [Theory]
    [InlineData("real")]
    [InlineData("Real")]
    [InlineData("REAl")]
    [InlineData("rEaL")]
    [InlineData("REALLY")]
    [InlineData("RE AL")]
    [InlineData("REA")]
    [InlineData("実弾")]
    [InlineData("ＲＥＡＬ")]  // 全角。見た目は同じでも別の符号位置であり受理しない
    [InlineData("ＲeＡl")]
    [InlineData("R E A L")]
    [InlineData("REAL REAL")]
    [InlineData("REA\nL")]   // 内部の改行は「前後空白」ではない
    public void 確認文字列がREALと完全一致しなければ受理されない(string phrase)
    {
        var request = new BrokerProviderChangeRequest(BrokerProvider.MoomooReal, "移行", true, phrase);

        var assessment = BrokerProviderChange.Evaluate(request, Stage2);

        assessment.Accepted.Should().BeFalse();
        assessment.Rejections.Should().Contain(BrokerProviderChangeRejection.LivePhraseMismatch);
    }

    // 境界値: 前後空白のみは除く（貼り付けで混じる空白は利用者の意図と無関係）。
    [Theory]
    [InlineData("REAL")]
    [InlineData(" REAL")]
    [InlineData("REAL ")]
    [InlineData("  REAL  ")]
    [InlineData("\tREAL\n")] // Trim は Unicode 空白全般を除く（貼り付けで混じる改行・タブも同じ扱い）
    public void 前後空白を除いてREALと一致すれば受理される(string phrase)
    {
        var request = new BrokerProviderChangeRequest(BrokerProvider.MoomooReal, "移行", true, phrase);

        BrokerProviderChange.Evaluate(request, Stage2).Accepted.Should().BeTrue();
    }

    // FR-13:「変更理由を必須（1 文字以上）とする」。空白のみも空とみなす。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 変更理由が空なら受理されない(string? reason)
    {
        var request = new BrokerProviderChangeRequest(BrokerProvider.MoomooSimulate, reason!);

        var assessment = BrokerProviderChange.Evaluate(request, Stage1);

        assessment.Accepted.Should().BeFalse();
        assessment.Rejections.Should().Contain(BrokerProviderChangeRejection.ReasonRequired);
    }

    // 非対称（IADR-0141 決定1）: 安全な方向（内蔵 paper / moomoo SIMULATE）への切替に確認は求めない。
    [Theory]
    [InlineData(BrokerProvider.InternalPaper)]
    [InlineData(BrokerProvider.MoomooSimulate)]
    public void 実弾以外への切替は理由だけで受理される(BrokerProvider target)
    {
        var request = new BrokerProviderChangeRequest(target, "デバッグのため内蔵擬似約定へ落とす");

        var assessment = BrokerProviderChange.Evaluate(request, Stage1);

        assessment.Accepted.Should().BeTrue();
        assessment.RequiresLiveConfirmation.Should().BeFalse();
    }

    // 05_screens SC-02 ②: Stage 1 のまま実弾へ切り替える場合は「段階ゲートを飛ばしている」旨を出す。
    // **保存は妨げない**（警告であって拒否理由ではない）。
    [Fact]
    public void Stage1のまま実弾へ切り替える要求は段階ゲート飛ばしとして警告されるが保存は妨げられない()
    {
        var request = new BrokerProviderChangeRequest(BrokerProvider.MoomooReal, "検証を打ち切る", true, "REAL");

        var assessment = BrokerProviderChange.Evaluate(request, Stage1);

        assessment.SkipsStageGate.Should().BeTrue();
        assessment.Accepted.Should().BeTrue(
            "計画は「運用段階との組み合わせは保存を妨げないが…警告を表示する」と定める（05_screens SC-02）");
        assessment.Rejections.Should().BeEmpty();
    }

    // 否定形の裏面: 段階が既に実弾（Stage 2）なら「飛ばしている」警告は出さない（狼少年にしない）。
    [Fact]
    public void 段階が既に実弾なら段階ゲート飛ばしの警告は出ない()
    {
        var request = new BrokerProviderChangeRequest(BrokerProvider.MoomooReal, "実弾へ移行", true, "REAL");

        BrokerProviderChange.Evaluate(request, Stage2).SkipsStageGate.Should().BeFalse();
    }

    // 否定形: 3 値のいずれでもない整数を送りつけても受理しない（既定値 0 へ暗黙に倒さない）。
    [Fact]
    public void 未定義の発注先は受理されない()
    {
        var request = new BrokerProviderChangeRequest((BrokerProvider)99, "不正な値");

        var assessment = BrokerProviderChange.Evaluate(request, Stage1);

        assessment.Accepted.Should().BeFalse();
        assessment.Rejections.Should().Contain(BrokerProviderChangeRejection.UnknownProvider);
    }

    // プロパティベース: 確認操作の 2 要素の全組み合わせ（4 通り）× 全発注先（3 値）で、
    // **受理されるのは「実弾でない」か「両方揃っている」場合に限る**。
    [Fact]
    public void 受理されるのは実弾でないか確認2要素が揃う場合に限る()
    {
        foreach (var target in Enum.GetValues<BrokerProvider>())
        {
            foreach (var acknowledged in new[] { false, true })
            {
                foreach (var phrase in new string?[] { null, "", "real", "REAL" })
                {
                    var request = new BrokerProviderChangeRequest(target, "理由", acknowledged, phrase);
                    var accepted = BrokerProviderChange.Evaluate(request, Stage1).Accepted;

                    var expected = target != BrokerProvider.MoomooReal
                        || (acknowledged && phrase == "REAL");

                    accepted.Should().Be(
                        expected,
                        "target={0} acknowledged={1} phrase={2}",
                        target,
                        acknowledged,
                        phrase ?? "<null>");
                }
            }
        }
    }

    // 拒否理由は最初の 1 件で打ち切らず全件返す（FR-11 監査・RiskEvaluator と同じ流儀）。
    [Fact]
    public void 拒否理由は全件列挙される()
    {
        var request = new BrokerProviderChangeRequest(BrokerProvider.MoomooReal, "  ");

        var assessment = BrokerProviderChange.Evaluate(request, Stage1);

        assessment.Rejections.Should().BeEquivalentTo(
        [
            BrokerProviderChangeRejection.ReasonRequired,
            BrokerProviderChangeRejection.LiveAcknowledgementMissing,
            BrokerProviderChangeRejection.LivePhraseMismatch,
        ]);
    }
}
