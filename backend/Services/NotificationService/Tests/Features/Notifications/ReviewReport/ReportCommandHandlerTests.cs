using NotificationService.Domain;
using NotificationService.Features.Notifications;
using NotificationService.Features.Notifications.ReviewReport;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NotificationService.Tests;

// FR-14, FR-07, UC-03〜05, ADR-0003, #341, IADR-0240: 報告書レビューコマンドの閂
//（多層認証 → 解析 → 版番号ガード → 報告書サービス呼び出し）。
//
// 受け入れ基準の中核 3 点を本ファイルで固定する。
//   ① **冪等確定**: 同一 periodKey ＋ 版番号 の二重送信で確定 API が 1 回しか呼ばれない
//   ② **多層認証を通らない確定の拒否**（否定形。kill switch・昇格承認と同水準）
//   ③ **失敗時に予約を解放する**（解放しないと、唯一の確定窓口で同じ版を二度と確定できない）
//
// 統制系の 3 点セット（`docs/tests/README.md`）:
//   境界値テーブル → 版番号の境界（0・1・最新・古い版）
//   プロパティベース → 任意の送信回数で確定 API の呼び出しが 1 回に収束すること
//   否定形         → 認証・解析・版落ち・失敗時の非実行
public class ReportCommandHandlerTests
{
    private const string Guild = "guild-1";
    private const string Channel = "channel-1";
    private const string OwnerUser = "discord-owner-1";
    private const string PeriodKey = "daily-2026-08-28";

    private sealed class FakeReportReviewController : IReportReviewController
    {
        public FakeReportReviewController(int version = 2) => Version = version;

        public int Version { get; set; }

        public int ReviewCalls { get; private set; }

        public int ConfirmCalls { get; private set; }

        public int RequestChangesCalls { get; private set; }

        public int? LastConfirmedVersion { get; private set; }

        // #835: 報告書サービスへ渡った会話キー（大小文字を含めて確認する）。
        public string? LastPeriodKey { get; private set; }

        // 呼び出し自体の失敗（HTTP エラー・タイムアウト）を模す。
        public bool ConfirmFails { get; set; }

        // 2xx だが受理されない（版不一致の 409）を模す。
        public bool ConfirmRejected { get; set; }

        public bool ReviewFails { get; set; }

        public Task<ReportReviewResult> GetReviewAsync(
            string periodKey, CancellationToken cancellationToken = default)
        {
            ReviewCalls++;
            LastPeriodKey = periodKey;
            return Task.FromResult(ReviewFails
                ? new ReportReviewResult(false, 0, "レビュー局面の照会に失敗しました（HTTP 503）")
                : new ReportReviewResult(true, Version, $"報告書 {periodKey}: 版 {Version}"));
        }

        public Task<ReportConfirmResult> ConfirmAsync(
            string periodKey, int expectedVersion, CancellationToken cancellationToken = default)
        {
            ConfirmCalls++;
            LastConfirmedVersion = expectedVersion;
            LastPeriodKey = periodKey;

            if (ConfirmFails)
                return Task.FromResult(new ReportConfirmResult(false, false, "報告書の確定に失敗しました（HTTP 503）"));

            if (ConfirmRejected)
                return Task.FromResult(new ReportConfirmResult(true, false, "版番号が一致しません。"));

            return Task.FromResult(new ReportConfirmResult(true, true, $"報告書 {periodKey}（版 {expectedVersion}）を確定しました。"));
        }

        public Task<ReportReviewResult> RequestChangesAsync(
            string periodKey, int expectedVersion, CancellationToken cancellationToken = default)
        {
            RequestChangesCalls++;
            return Task.FromResult(new ReportReviewResult(
                true, expectedVersion, $"報告書 {periodKey}（版 {expectedVersion}）を差し戻しました。"));
        }

        // #834: 入力補完の候補（新しい順に並んだ会話キー）。アダプタの契約どおり、失敗は空で返す。
        public List<string> PeriodKeys { get; } = [];

        public int ListCalls { get; private set; }

        public Task<IReadOnlyList<string>> ListPeriodKeysAsync(CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<string>>(PeriodKeys);
        }
    }

    private static DiscordBotOptions FullyConfigured()
    {
        var options = new DiscordBotOptions
        {
            GuildId = Guild,
            ChannelId = Channel,
        };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = "endazon";
        return options;
    }

    private static ReportCommandHandler Handler(
        IReportReviewController controller, DiscordBotOptions options, VersionedConfirmationGuard? guard = null) =>
        new(controller, guard ?? new VersionedConfirmationGuard(), options,
            NullLogger<ReportCommandHandler>.Instance);

    private static DiscordCommandContext Context(string raw, string? user = OwnerUser, bool isDm = false) =>
        new(Guild, Channel, user, isDm, raw);

    // ---- ① 冪等確定（受け入れ基準の中核） ----

    [Fact]
    public async Task 本人の確定は版番号つきで報告書サービスを呼ぶ()
    {
        // FR-07, FR-14: 詳細設計07「確定要求は 対象ID＋版番号 を必須とする」。
        var controller = new FakeReportReviewController();

        var result = await Handler(controller, FullyConfigured())
            .HandleAsync(Context($"/report approve {PeriodKey} 2"));

        result.WasExecuted.Should().BeTrue();
        result.ConfirmedNow.Should().BeTrue();
        controller.ConfirmCalls.Should().Be(1);
        controller.LastConfirmedVersion.Should().Be(2);
    }

    [Fact]
    public async Task 週報の会話キーは大文字のまま報告書サービスへ渡る()
    {
        // FR-07, #835: 週報の自然キーは ISO 週の W が大文字（weekly-2026-W38）。小文字化すると
        // 報告書サービスの完全一致検索に掛からず 404 になり、**週報を一度も確定できない**（稼働環境で実測）。
        // 確認ボタン（CustomId 経由）も同じ文字列を組み立てて本ハンドラを通るため、この 1 点で両経路が決まる。
        const string weeklyKey = "weekly-2026-W38";
        var controller = new FakeReportReviewController();
        var handler = Handler(controller, FullyConfigured());

        var show = await handler.HandleAsync(Context($"/report show {weeklyKey}"));
        controller.LastPeriodKey.Should().Be(weeklyKey);
        show.WasExecuted.Should().BeTrue();

        var approve = await handler.HandleAsync(Context($"/report approve {weeklyKey} 2"));
        approve.ConfirmedNow.Should().BeTrue();
        controller.ConfirmCalls.Should().Be(1);
        controller.LastPeriodKey.Should().Be(weeklyKey, "確定 API にも大文字のまま渡る");
    }

    [Fact]
    public async Task 同一版の二重送信では確定APIを一度しか呼ばない()
    {
        // FR-14, #341 受け入れ基準: 「同一確定操作の二重送信で方針が二重適用されないこと」。
        // 2 回目は **確定 API を呼ばずに**「確定済み」を返す（副作用なし）。
        var controller = new FakeReportReviewController();
        var handler = Handler(controller, FullyConfigured());

        var first = await handler.HandleAsync(Context($"/report approve {PeriodKey} 2"));
        var second = await handler.HandleAsync(Context($"/report approve {PeriodKey} 2"));

        first.ConfirmedNow.Should().BeTrue();
        second.WasExecuted.Should().BeTrue();
        second.ConfirmedNow.Should().BeFalse("2 回目は確定を起こしていない（冪等）");
        second.Message.Should().Contain("確定済み");
        controller.ConfirmCalls.Should().Be(1, "二重送信でも確定 API は 1 回だけ呼ばれる");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(20)]
    public async Task 何回送信しても確定APIの呼び出しは一度に収束する(int submissions)
    {
        // プロパティベース: 送信回数 n によらず確定 API の呼び出しは 1 回（冪等性の不変条件）。
        var controller = new FakeReportReviewController();
        var handler = Handler(controller, FullyConfigured());

        for (var i = 0; i < submissions; i++)
            await handler.HandleAsync(Context($"/report approve {PeriodKey} 3"));

        controller.ConfirmCalls.Should().Be(1);
    }

    [Fact]
    public async Task 同時確定でも確定APIは一度しか呼ばれない()
    {
        // チャットUI と Discord の同時確定に相当する並行送信。ガードは lock で直列化される。
        var controller = new FakeReportReviewController();
        var handler = Handler(controller, FullyConfigured());

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            handler.HandleAsync(Context($"/report approve {PeriodKey} 4"))));

        controller.ConfirmCalls.Should().Be(1);
    }

    [Theory]
    // 境界値テーブル: 版番号の値域。0・負数は解析されず、確定済みより古い版は Stale。
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("1", true)]
    [InlineData("99", true)]
    public async Task 版番号の境界は解析の段階で選別される(string version, bool parsed)
    {
        var controller = new FakeReportReviewController();

        var result = await Handler(controller, FullyConfigured())
            .HandleAsync(Context($"/report approve {PeriodKey} {version}"));

        controller.ConfirmCalls.Should().Be(parsed ? 1 : 0);
        if (!parsed)
            result.IsDenied.Should().BeTrue("版番号として解釈できない要求は確定を実行しない");
    }

    [Fact]
    public async Task 確定済みより古い版の要求は拒否され確定APIを呼ばない()
    {
        // 詳細設計07: 「版が古い確定要求は拒否し『最新ドラフトを確認してください』と応答する」。
        var controller = new FakeReportReviewController();
        var handler = Handler(controller, FullyConfigured());

        await handler.HandleAsync(Context($"/report approve {PeriodKey} 5"));
        var stale = await handler.HandleAsync(Context($"/report approve {PeriodKey} 3"));

        stale.WasExecuted.Should().BeFalse();
        stale.IsDenied.Should().BeFalse("版落ちは拒否ではなく、利用者へ理由を返してよい失敗である");
        stale.Message.Should().Contain("最新ドラフト");
        controller.ConfirmCalls.Should().Be(1, "古い版で確定 API を呼ばない");
    }

    // ---- ③ 失敗時の予約解放（IADR-0240 決定3） ----

    [Fact]
    public async Task 確定の呼び出しが失敗したら同じ版で再試行できる()
    {
        // 🔴 予約を解放しないと、唯一の確定窓口で同じ版を二度と確定できなくなる。
        var controller = new FakeReportReviewController { ConfirmFails = true };
        var handler = Handler(controller, FullyConfigured());

        var failed = await handler.HandleAsync(Context($"/report approve {PeriodKey} 2"));
        failed.WasExecuted.Should().BeFalse();
        failed.ConfirmedNow.Should().BeFalse();

        controller.ConfirmFails = false;
        var retried = await handler.HandleAsync(Context($"/report approve {PeriodKey} 2"));

        retried.ConfirmedNow.Should().BeTrue("失敗した確定の予約は解放され、同じ版で再試行できる");
        controller.ConfirmCalls.Should().Be(2);
    }

    [Fact]
    public async Task 受理されなかった確定も同じ版で再試行できる()
    {
        // 2xx だが受理されなかった（版不一致の 409）場合も予約を解放する。
        var controller = new FakeReportReviewController { ConfirmRejected = true };
        var handler = Handler(controller, FullyConfigured());

        var rejected = await handler.HandleAsync(Context($"/report approve {PeriodKey} 2"));
        rejected.WasExecuted.Should().BeFalse();

        controller.ConfirmRejected = false;
        var retried = await handler.HandleAsync(Context($"/report approve {PeriodKey} 2"));

        retried.ConfirmedNow.Should().BeTrue();
    }

    // ---- ② 多層認証の否定形（kill switch・昇格承認と同水準） ----

    [Fact]
    public async Task DM_では報告書サービスを呼ばない()
    {
        var controller = new FakeReportReviewController();

        var result = await Handler(controller, FullyConfigured())
            .HandleAsync(Context($"/report approve {PeriodKey} 2", isDm: true));

        result.WasExecuted.Should().BeFalse();
        result.IsDenied.Should().BeTrue();
        controller.ConfirmCalls.Should().Be(0);
    }

    [Fact]
    public async Task 許可リスト外のユーザーでは報告書サービスを呼ばない()
    {
        var controller = new FakeReportReviewController();

        var result = await Handler(controller, FullyConfigured())
            .HandleAsync(Context($"/report approve {PeriodKey} 2", user: "intruder"));

        result.WasExecuted.Should().BeFalse();
        controller.ConfirmCalls.Should().Be(0);
        controller.ReviewCalls.Should().Be(0, "許可外の着信へ版番号すら漏らさない");
    }

    [Fact]
    public async Task 多層認証の設定が空なら全拒否で報告書サービスを呼ばない()
    {
        var controller = new FakeReportReviewController();

        var result = await Handler(controller, new DiscordBotOptions())
            .HandleAsync(Context($"/report approve {PeriodKey} 2"));

        result.WasExecuted.Should().BeFalse();
        controller.ConfirmCalls.Should().Be(0);
    }

    [Fact]
    public async Task Keycloak_マッピングが無ければ報告書サービスを呼ばない()
    {
        var controller = new FakeReportReviewController();
        var options = FullyConfigured();
        options.UserMapping.Remove(OwnerUser);

        var result = await Handler(controller, options).HandleAsync(Context($"/report approve {PeriodKey} 2"));

        result.WasExecuted.Should().BeFalse();
        controller.ConfirmCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("/killswitch")]
    [InlineData("/pause")]
    [InlineData("/stage promote 2")]
    [InlineData("/gfv clear")]
    [InlineData("/report")]
    [InlineData("/report approve")]
    // IADR-0240 決定6: URL パスへ載る値であるため、書式外の periodKey は解析の段階で落とす。
    [InlineData("/report approve ../../secrets 1")]
    [InlineData("/report approve daily_2026 1")]
    public async Task 報告書レビュー系でないコマンドでは報告書サービスを呼ばない(string raw)
    {
        var controller = new FakeReportReviewController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context(raw));

        result.WasExecuted.Should().BeFalse();
        result.IsDenied.Should().BeTrue();
        controller.ConfirmCalls.Should().Be(0);
        controller.ReviewCalls.Should().Be(0);
    }

    // ---- 参照・差し戻し ----

    [Fact]
    public async Task 照会は参照のみで確定を呼ばない()
    {
        var controller = new FakeReportReviewController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context($"/report show {PeriodKey}"));

        result.WasExecuted.Should().BeTrue();
        result.Version.Should().Be(2);
        controller.ReviewCalls.Should().Be(1);
        controller.ConfirmCalls.Should().Be(0);
    }

    [Fact]
    public async Task 版番号なしの承認要求は確認前の照会であり確定を呼ばない()
    {
        // 確認ボタンを出す前段。ここで確定してしまうと 2 段階の確認が消える。
        var controller = new FakeReportReviewController();

        var result = await Handler(controller, FullyConfigured())
            .HandleAsync(Context($"/report approve {PeriodKey}"));

        result.WasExecuted.Should().BeTrue();
        result.Version.Should().Be(2, "確認ボタンへ載せる版番号が返る");
        result.ConfirmedNow.Should().BeFalse();
        controller.ConfirmCalls.Should().Be(0);
    }

    [Fact]
    public async Task 差し戻しは版番号を照会してから実行する()
    {
        var controller = new FakeReportReviewController(version: 7);

        var result = await Handler(controller, FullyConfigured())
            .HandleAsync(Context($"/report request-changes {PeriodKey}"));

        result.WasExecuted.Should().BeTrue();
        controller.ReviewCalls.Should().Be(1);
        controller.RequestChangesCalls.Should().Be(1);
        controller.ConfirmCalls.Should().Be(0);
    }

    [Fact]
    public async Task 版番号を照会できなければ差し戻しを実行しない()
    {
        // 版番号を推測して楽観排他を素通しさせない。
        var controller = new FakeReportReviewController { ReviewFails = true };

        var result = await Handler(controller, FullyConfigured())
            .HandleAsync(Context($"/report request-changes {PeriodKey}"));

        result.WasExecuted.Should().BeFalse();
        controller.RequestChangesCalls.Should().Be(0);
    }

    // ---- ④ 入力補完（#834。候補は補助であって統制ではない） ----

    [Fact]
    public async Task 入力補完は会話キーの候補を新しい順のまま返す()
    {
        // FR-14, #834: 利用者は会話キーを覚えていられず、日付だけを入れて 404 になった（実測）。
        // 並びは一覧（報告書サービス）が決めた順のまま返す（窓口で並べ替え直さない）。
        var controller = new FakeReportReviewController();
        controller.PeriodKeys.AddRange(["daily-2026-09-18", "weekly-2026-W38", "daily-2026-09-17"]);

        var suggestions = await Handler(controller, FullyConfigured())
            .SuggestPeriodsAsync(Context("/report autocomplete"), input: null);

        suggestions.Should().Equal("daily-2026-09-18", "weekly-2026-W38", "daily-2026-09-17");
    }

    [Fact]
    public async Task 入力補完は利用者の入力で絞り込む()
    {
        // #834 決定5: 推測補正はしない。打った文字で絞るだけである。
        var controller = new FakeReportReviewController();
        controller.PeriodKeys.AddRange(["daily-2026-09-18", "weekly-2026-W38", "monthly-2026-09"]);

        var suggestions = await Handler(controller, FullyConfigured())
            .SuggestPeriodsAsync(Context("/report autocomplete"), "weekly");

        suggestions.Should().Equal("weekly-2026-W38");
    }

    [Fact]
    public async Task 許可外の利用者には候補を返さない()
    {
        // 否定形（受け入れ基準）: 補完は「どの会話キーが存在するか」を漏らす経路であり、
        // 窓口の認可水準（kill switch と同水準）を下げない。**一覧 API も呼ばない。**
        var controller = new FakeReportReviewController();
        controller.PeriodKeys.Add("daily-2026-09-18");

        var suggestions = await Handler(controller, FullyConfigured())
            .SuggestPeriodsAsync(Context("/report autocomplete", user: "intruder"), input: null);

        suggestions.Should().BeEmpty();
        controller.ListCalls.Should().Be(0, "許可外の着信では報告書サービスを呼ばない");
    }

    [Fact]
    public async Task DM_からの補完要求にも候補を返さない()
    {
        // 否定形: 多層認証の層1（DM は無条件拒否）は補完でも効く。
        var controller = new FakeReportReviewController();
        controller.PeriodKeys.Add("daily-2026-09-18");

        var suggestions = await Handler(controller, FullyConfigured())
            .SuggestPeriodsAsync(Context("/report autocomplete", isDm: true), input: null);

        suggestions.Should().BeEmpty();
        controller.ListCalls.Should().Be(0);
    }

    [Fact]
    public async Task 候補が空でも_report_は従来どおり動く()
    {
        // 否定形（fail-safe）: 一覧の取得に失敗するとアダプタは空を返す（例外を投げない）。
        // **補完が引けないことを理由に `/report` 自体を壊さない。**
        var controller = new FakeReportReviewController();
        var handler = Handler(controller, FullyConfigured());

        var suggestions = await handler.SuggestPeriodsAsync(Context("/report autocomplete"), input: null);
        var show = await handler.HandleAsync(Context($"/report show {PeriodKey}"));

        suggestions.Should().BeEmpty();
        show.WasExecuted.Should().BeTrue("補完の失敗は照会に影響しない");
    }
}
