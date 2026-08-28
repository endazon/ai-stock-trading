using System.Net;
using System.Text;
using System.Text.Json;
using NotificationService.Infrastructure.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NotificationService.Infrastructure.Tests;

// FR-20, FR-14, UC-06, ADR-0008, IADR-0070/0081: Risk の stage-gate エンドポイント呼び出しを fake
// HttpMessageHandler で検証する（実ネットワーク不使用）。要点は 200 受理／422 拒否（未充足基準）の整形と、
// Risk が数値でシリアライズする enum の射影（append-only・未知値フォールバック）。
public class HttpStageGateControllerTests
{
    private static HttpStageGateController Controller(FakeHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://risk-management-service") },
            NullLogger<HttpStageGateController>.Instance);

    // 段階ゲート現況（enum はすべて数値で届く）。
    private const string StatusBody = """
    {
        "currentStage": 1,
        "currentSettings": { "stage": 1, "mode": 0, "capitalCapRatio": 1.00 },
        "history": [
            { "sequence": 1, "fromStage": 0, "toStage": 1, "kind": 0, "approvedBy": "endazon", "occurredAtUtc": "2026-07-10T02:00:00+00:00", "reason": "backtest passed" }
        ],
        "promotion": { "targetStage": 2, "eligible": false, "unmetCriteria": [1, 2] },
        "withdrawal": { "triggered": false, "reason": null, "haltNewEntries": false, "proposedStage": null }
    }
    """;

    [Fact]
    public async Task 現況照会は_stage_gate_を_GET_し整形結果を返す()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, StatusBody);

        var result = await Controller(handler).GetStatusAsync();

        handler.RequestUri.Should().Be("http://risk-management-service/risk-controls/stage-gate");
        handler.Method.Should().Be(HttpMethod.Get);
        result.Succeeded.Should().BeTrue();
        // 現段階・モード・昇格可否（未充足基準）・撤退・履歴が数値 enum から整形される。
        result.Message.Should().Contain("Stage 1（SIMULATE）");
        result.Message.Should().Contain("ペーパー");
        result.Message.Should().Contain("昇格");
        result.Message.Should().Contain("統制違反あり"); // criterion 2
        result.Message.Should().Contain("直近の遷移");
        result.Message.Should().Contain("endazon");
    }

    // ---- AST #423, FR-20, SC-02, §4.1 条件 3 / §4.3, IADR-0164 決定6 ----
    //
    // 計画は「100 件未満を設定した場合、**画面と昇格承認の双方**に『統計的な根拠（§4.3）を満たさない
    // 設定である』旨の警告を常時表示する」と定める。Discord は昇格承認の窓口であり、承認者が読むのは
    // 本 `/stage status` の応答である。
    //
    // **判定は Risk が `belowStatisticalBasis` で宣言したものに従う**（閾値 100 をここに写経しない）。

    private const string StatusBodyWithLoweredTradeCount = """
    {
        "currentStage": 1,
        "currentSettings": { "stage": 1, "mode": 0, "capitalCapRatio": 1.00 },
        "history": [],
        "promotion": { "targetStage": 2, "eligible": false, "unmetCriteria": [10] },
        "withdrawal": { "triggered": false, "reason": null, "haltNewEntries": false, "proposedStage": null },
        "stage1Criteria": { "targetTradingDays": 60, "minimumTradeCount": 30, "maximumTradingDays": 120, "belowStatisticalBasis": true }
    }
    """;

    private const string StatusBodyWithDefaultTradeCount = """
    {
        "currentStage": 1,
        "currentSettings": { "stage": 1, "mode": 0, "capitalCapRatio": 1.00 },
        "history": [],
        "promotion": { "targetStage": 2, "eligible": false, "unmetCriteria": [10] },
        "withdrawal": { "triggered": false, "reason": null, "haltNewEntries": false, "proposedStage": null },
        "stage1Criteria": { "targetTradingDays": 60, "minimumTradeCount": 100, "maximumTradingDays": 120, "belowStatisticalBasis": false }
    }
    """;

    [Fact]
    public async Task 最小取引件数が100未満なら昇格承認の窓口に警告を出す()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, StatusBodyWithLoweredTradeCount);

        var result = await Controller(handler).GetStatusAsync();

        result.Succeeded.Should().BeTrue();
        result.Message.Should().Contain("最小取引件数");
        result.Message.Should().Contain("30 件");
        result.Message.Should().Contain("統計的な根拠");
    }

    // 逆方向の否定形。**満たしているのに警告を出すと、警告が常時出て誰も読まなくなる。**
    [Fact]
    public async Task 最小取引件数が既定なら警告を出さない()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, StatusBodyWithDefaultTradeCount);

        var result = await Controller(handler).GetStatusAsync();

        result.Succeeded.Should().BeTrue();
        result.Message.Should().NotContain("統計的な根拠");
    }

    // 旧版 Risk（本項目を返さない）でも壊れない・警告も出さない（宣言が無いものを推測しない）。
    [Fact]
    public async Task 合格条件を返さない応答では警告を出さず整形も壊れない()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, StatusBody);

        var result = await Controller(handler).GetStatusAsync();

        result.Succeeded.Should().BeTrue();
        result.Message.Should().NotContain("統計的な根拠");
    }

    [Fact]
    public async Task 昇格は_transition_へ_targetStage_つきで_POST_し受理を返す()
    {
        var body = """
        {
            "accepted": true,
            "transition": { "sequence": 2, "fromStage": 1, "toStage": 2, "kind": 0, "approvedBy": "endazon", "occurredAtUtc": "2026-07-18T00:00:00+00:00", "reason": "approved" },
            "resultingSettings": { "stage": 2, "mode": 1, "capitalCapRatio": 0.30 },
            "rejectionReasons": []
        }
        """;
        var handler = new FakeHandler(HttpStatusCode.OK, body);

        var result = await Controller(handler).RequestTransitionAsync(2);

        handler.RequestUri.Should().Be("http://risk-management-service/risk-controls/stage-gate/transition");
        handler.Method.Should().Be(HttpMethod.Post);
        using var doc = JsonDocument.Parse(handler.Body);
        doc.RootElement.GetProperty("targetStage").GetInt32().Should().Be(2);

        result.Succeeded.Should().BeTrue();
        result.Accepted.Should().BeTrue();
        result.Message.Should().Contain("Stage 2");
    }

    // 受け入れ基準4: 422（未充足基準）の拒否理由を整形して返す。Succeeded は true（Risk は明確に応答した）だが Accepted は false。
    [Fact]
    public async Task 昇格の_422_は拒否理由を整形して返す()
    {
        // rejectionReasons: [0]=BacktestNotPassed（Stage 0→1 バックテスト未合格）。
        var body = """
        { "accepted": false, "transition": null, "resultingSettings": null, "rejectionReasons": [0] }
        """;
        var handler = new FakeHandler(HttpStatusCode.UnprocessableEntity, body);

        var result = await Controller(handler).RequestTransitionAsync(1);

        result.Succeeded.Should().BeTrue();
        result.Accepted.Should().BeFalse();
        result.Message.Should().Contain("受理されませんでした");
        result.Message.Should().Contain("バックテスト未合格");
    }

    // 受け入れ基準6: 二重適用防止＝現段階への遷移は 422 TargetIsCurrentStage（criterion 7）。
    [Fact]
    public async Task 現段階への昇格は_422_で現段階指定として返る()
    {
        var body = """
        { "accepted": false, "transition": null, "resultingSettings": null, "rejectionReasons": [7] }
        """;
        var handler = new FakeHandler(HttpStatusCode.UnprocessableEntity, body);

        var result = await Controller(handler).RequestTransitionAsync(1);

        result.Accepted.Should().BeFalse();
        result.Message.Should().Contain("現段階と同じ");
    }

    // 未知の criterion 数値（enum 追加で将来増える）は fail-safe のフォールバック文言に倒す（例外を出さない）。
    [Fact]
    public async Task 未知の拒否基準はフォールバック文言に倒す()
    {
        var body = """
        { "accepted": false, "transition": null, "resultingSettings": null, "rejectionReasons": [99] }
        """;
        var handler = new FakeHandler(HttpStatusCode.UnprocessableEntity, body);

        var result = await Controller(handler).RequestTransitionAsync(1);

        result.Accepted.Should().BeFalse();
        result.Message.Should().Contain("不明な基準(99)");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task 昇格の認可失敗は失敗として返し_owner_設定を示唆する(HttpStatusCode status)
    {
        var handler = new FakeHandler(status, "");

        var result = await Controller(handler).RequestTransitionAsync(2);

        result.Succeeded.Should().BeFalse();
        result.Accepted.Should().BeFalse();
        result.Message.Should().Contain("owner");
    }

    [Fact]
    public async Task 撤退評価は_withdrawal_evaluate_へ_POST_し整形結果を返す()
    {
        // triggered=true・haltNewEntries=true・proposedStage=1・reason=0（DD 到達）。
        var body = """
        { "triggered": true, "reason": 0, "haltNewEntries": true, "proposedStage": 1 }
        """;
        var handler = new FakeHandler(HttpStatusCode.OK, body);

        var result = await Controller(handler).EvaluateWithdrawalAsync();

        handler.RequestUri.Should().Be("http://risk-management-service/risk-controls/stage-gate/withdrawal/evaluate");
        handler.Method.Should().Be(HttpMethod.Post);
        result.Succeeded.Should().BeTrue();
        result.Message.Should().Contain("撤退基準に抵触");
        result.Message.Should().Contain("kill switch"); // haltNewEntries=true
        result.Message.Should().Contain("Stage 1"); // proposedStage
    }

    [Fact]
    public async Task 現況照会の非2xx_は失敗として返す()
    {
        var handler = new FakeHandler(HttpStatusCode.Forbidden, "");

        var result = await Controller(handler).GetStatusAsync();

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("owner");
    }

    [Fact]
    public async Task 現況照会の解釈不能本文は失敗として返す()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "null");

        var result = await Controller(handler).GetStatusAsync();

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task 例外は失敗として返し_送出しない()
    {
        var handler = new FakeHandler(new HttpRequestException("接続できません"));

        var result = await Controller(handler).RequestTransitionAsync(2);

        result.Succeeded.Should().BeFalse();
        result.Accepted.Should().BeFalse();
    }

    // ---- #466, FR-20, SC-02, §4.1 追補3（質問票 第15回 Q13-a）, IADR-0180 ----
    // **遷移応答（`/stage promote` が叩く POST）も合格条件を運ぶ**。判定はサーバ（Risk）が
    // `belowStatisticalBasis` で宣言し、アダプタは閾値 100 を写経せず表示するだけである。

    private const string TransitionBodyBelowBasis = """
    {
        "accepted": true,
        "transition": { "sequence": 2, "fromStage": 1, "toStage": 2, "kind": 0, "approvedBy": "endazon", "occurredAtUtc": "2026-08-08T00:00:00+00:00", "reason": "approved" },
        "resultingSettings": { "stage": 2, "mode": 1, "capitalCapRatio": 0.30 },
        "rejectionReasons": [],
        "stage1Criteria": { "targetTradingDays": 60, "minimumTradeCount": 5, "maximumTradingDays": 120, "belowStatisticalBasis": true }
    }
    """;

    [Fact]
    public async Task 遷移応答が引き下げを宣言していれば警告文言を別項目で返す()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, TransitionBodyBelowBasis);

        var result = await Controller(handler).RequestTransitionAsync(2);

        result.Accepted.Should().BeTrue();
        // **Message へ混ぜない**——昇格か差し戻しかを知るのはハンドラであり、付加の可否はそちらが決める。
        result.Message.Should().NotContain("⚠");
        result.Stage1Warning.Should().NotBeNull();
        result.Stage1Warning.Should().Contain("5 件");
        result.Stage1Warning.Should().Contain("統計的な根拠");
    }

    // **否定形**: 宣言が false なら警告は返らない（既定値のままなら警告は出ない）。
    [Fact]
    public async Task 遷移応答の宣言が_false_なら警告を返さない()
    {
        var body = TransitionBodyBelowBasis
            .Replace("\"minimumTradeCount\": 5", "\"minimumTradeCount\": 100", StringComparison.Ordinal)
            .Replace("\"belowStatisticalBasis\": true", "\"belowStatisticalBasis\": false", StringComparison.Ordinal);
        var handler = new FakeHandler(HttpStatusCode.OK, body);

        var result = await Controller(handler).RequestTransitionAsync(2);

        result.Stage1Warning.Should().BeNull();
    }

    // **否定形**: 本項目を返さない旧版 Risk では警告を出さない（null＝宣言が無い。`/stage status` と同型）。
    [Fact]
    public async Task 合格条件を返さない旧版応答では警告を返さない()
    {
        var body = """
        {
            "accepted": true,
            "transition": { "sequence": 2, "fromStage": 1, "toStage": 2, "kind": 0, "approvedBy": "endazon", "occurredAtUtc": "2026-08-08T00:00:00+00:00", "reason": "approved" },
            "resultingSettings": { "stage": 2, "mode": 1, "capitalCapRatio": 0.30 },
            "rejectionReasons": []
        }
        """;
        var handler = new FakeHandler(HttpStatusCode.OK, body);

        var result = await Controller(handler).RequestTransitionAsync(2);

        result.Stage1Warning.Should().BeNull();
    }

    // 決定1: **422（拒否）でも警告を返す。** 承認操作は行われており、設定が下がっている事実は変わらない。
    [Fact]
    public async Task 受理されなかった遷移でも警告を返す()
    {
        var body = """
        {
            "accepted": false,
            "transition": null,
            "resultingSettings": null,
            "rejectionReasons": [10],
            "stage1Criteria": { "targetTradingDays": 60, "minimumTradeCount": 5, "maximumTradingDays": 120, "belowStatisticalBasis": true }
        }
        """;
        var handler = new FakeHandler(HttpStatusCode.UnprocessableEntity, body);

        var result = await Controller(handler).RequestTransitionAsync(2);

        result.Succeeded.Should().BeTrue();
        result.Accepted.Should().BeFalse();
        result.Stage1Warning.Should().NotBeNull();
    }

    // 決定3: `/stage status` と `/stage promote` の文言が**同一の定数**から出ることを固定する。
    // 経路ごとに書き下ろすと必ず割れる（issue #466 が SC-02 との一致を求めている）。
    [Fact]
    public async Task 現況照会と遷移応答の警告文言が一致する()
    {
        var statusBody = """
        {
            "currentStage": 1,
            "currentSettings": { "stage": 1, "mode": 0, "capitalCapRatio": 1.00 },
            "history": [],
            "promotion": { "targetStage": 2, "eligible": false, "unmetCriteria": [10] },
            "withdrawal": { "triggered": false, "reason": null, "haltNewEntries": false, "proposedStage": null },
            "stage1Criteria": { "targetTradingDays": 60, "minimumTradeCount": 5, "maximumTradingDays": 120, "belowStatisticalBasis": true }
        }
        """;
        var status = await Controller(new FakeHandler(HttpStatusCode.OK, statusBody)).GetStatusAsync();
        var transition = await Controller(new FakeHandler(HttpStatusCode.OK, TransitionBodyBelowBasis))
            .RequestTransitionAsync(2);

        transition.Stage1Warning.Should().NotBeNull();
        status.Message.Should().Contain(transition.Stage1Warning!);
    }

    // 決定3 の残余リスクの明示: C# 側の文言を**リテラルで**固定する。
    // SC-02（`frontend/src/features/risk/contracts.ts` の `STAGE1_TRADE_COUNT_BELOW_BASIS_WARNING`）と
    // 同一であること自体はクロス言語のため機械的に強制できない——**片方を変えたら両方を直すこと**。
    [Fact]
    public void 警告文言が_SC02_の文言と同一である()
    {
        HttpStageGateController.BelowStatisticalBasisWarning.Should().Be(
            "統計的な根拠（06_daytrading-review §4.3）を満たさない設定です。"
            + "100 件は「30 件が床・100 件が実用最低限」という実務上の一致点の下限であり、"
            + "これを下回ると勝率・平均損益の推定分散が大きく、"
            + "条件 3 の目的（運用に足るかを統計的に判断できる）を満たしません。"
            + "この警告は設定を妨げません（変更は理由とともに履歴へ残ります）。");
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Exception? _throw;

        public FakeHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public FakeHandler(Exception toThrow)
        {
            _throw = toThrow;
            _body = string.Empty;
        }

        public string? RequestUri { get; private set; }

        public HttpMethod? Method { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Method = request.Method;
            if (request.Content is not null)
                Body = await request.Content.ReadAsStringAsync(cancellationToken);

            if (_throw is not null)
                throw _throw;

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
