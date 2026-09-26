using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Domain;
using NotificationService.Features.Notifications;
using NotificationService.Features.Notifications.RevisePolicy;
using Xunit;

namespace NotificationService.Tests;

// FR-07, FR-14, UC-03〜05, ADR-0003, #1016, IADR-0431: `/policy`（方針の改訂）の閂（T-10-1322〜1328）。
// 多層認証 → 解析 → 指示の検証 → 報告書サービス。**ここでは確定しない**・**監視銘柄は変えない**・**失敗と不明を区別する**。
public class PolicyRevisionCommandHandlerTests
{
    private const string Guild = "guild-1";
    private const string Channel = "channel-1";
    private const string OwnerUser = "discord-owner-1";

    private static DiscordBotOptions Options()
    {
        var options = new DiscordBotOptions { GuildId = Guild, ChannelId = Channel, KillSwitchConfirmationPhrase = "STOP TRADING" };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = "developer";
        return options;
    }

    private static DiscordCommandContext Context(string raw = "/policy", string user = OwnerUser, bool dm = false) =>
        new(Guild, Channel, user, dm, raw);

    private static PolicyRevisionProposalView Proposal(
        bool presented = true, string periodKey = "daily-2026-09-27", int version = 1, string policy = "押し目買いを優先する",
        IReadOnlyList<WatchlistChangeSuggestionView>? changes = null, string? rationale = "指示どおり") =>
        new(periodKey, version, Created: true, presented, "保存しました",
            policy,
            changes ?? [new WatchlistChangeSuggestionView("add", "NVDA", "AI 需要")],
            rationale);

    private static (PolicyRevisionCommandHandler Handler, FakePolicyRevisionController Controller) Create(
        PolicyRevisionCommandOutcome? outcome = null, FakeWatchlistController? watchlist = null)
    {
        var controller = new FakePolicyRevisionController(outcome ?? new PolicyRevisionCommandOutcome(true, false, "保存しました", Proposal()));
        return (new PolicyRevisionCommandHandler(
            controller, watchlist ?? new FakeWatchlistController(), Options(), NullLogger<PolicyRevisionCommandHandler>.Instance), controller);
    }

    // T-10-1322: 案が承認待ちになれば、表示文と確認ボタンの会話キー・版を返す。操作者を代理として渡す。
    [Fact]
    public async Task 案が承認待ちなら確認ボタンの版を返し操作者を代理で渡す()
    {
        var (handler, controller) = Create();

        var result = await handler.HandleAsync(Context(), "  もっと積極的に  ");

        result.WasExecuted.Should().BeTrue();
        (result.PeriodKey, result.Version).Should().Be(("daily-2026-09-27", 1));
        result.Message.Should().Contain("押し目買いを優先する").And.Contain("追加 NVDA（米国）: AI 需要")
            .And.Contain(PolicyRevisionMessage.WatchlistWillApplyNotice).And.Contain("確定するまで取引には適用されません");
        var call = controller.Calls.Should().ContainSingle().Subject;
        (call.PeriodKey, call.Instruction, call.OnBehalfOf).Should().Be(((string?)null, "もっと積極的に", "developer"));
    }

    // T-10-1323: 会話キーを指定すればそれを渡す（大小文字を保つ）。
    [Fact]
    public async Task 会話キーを指定すればそのまま渡す()
    {
        var (handler, controller) = Create();

        await handler.HandleAsync(Context("/policy weekly-2026-W39"), "指示");

        controller.Calls.Single().PeriodKey.Should().Be("weekly-2026-W39");
    }

    // T-10-1324: 承認待ちにできなかった案には確認ボタンを出さない（未提示の版は確定できない）。
    [Fact]
    public async Task 未提示の案には確認ボタンを出さない()
    {
        var (handler, _) = Create(new PolicyRevisionCommandOutcome(true, false, "保存", Proposal(presented: false)));

        var result = await handler.HandleAsync(Context(), "指示");

        result.WasExecuted.Should().BeTrue();
        result.Version.Should().BeNull();
        result.PeriodKey.Should().BeNull();
    }

    // T-10-1325: 多層認証を通らなければ報告書サービスを呼ばない（DM・許可外・別チャンネル）。
    [Theory]
    [InlineData("stranger", false, Channel)]
    [InlineData(OwnerUser, true, Channel)]
    [InlineData(OwnerUser, false, "other-channel")]
    public async Task 多層認証を通らなければ呼ばない(string user, bool dm, string channel)
    {
        var (handler, controller) = Create();

        var result = await handler.HandleAsync(new DiscordCommandContext(Guild, channel, user, dm, "/policy"), "指示");

        result.IsDenied.Should().BeTrue();
        controller.Calls.Should().BeEmpty();
    }

    // T-10-1326: `/policy` 以外・余分な引数・書式外の会話キー・空や長すぎる指示では呼ばない（LLM の費用を使わない）。
    [Theory]
    [InlineData("/report show daily-2026-09-27", "指示")]
    [InlineData("/policy", "")]
    [InlineData("/policy", "   ")]
    [InlineData("/policy", null)]
    public async Task 解析と指示の検証を通らなければ呼ばない(string raw, string? instruction)
    {
        var (handler, controller) = Create();

        var result = await handler.HandleAsync(Context(raw), instruction);

        result.WasExecuted.Should().BeFalse();
        controller.Calls.Should().BeEmpty();
    }

    // T-10-1344（監査 nit）: `/policy` の period が書式外なら、「許可されていない」ではなく形式を案内する（呼ばない）。
    [Theory]
    [InlineData("/policy a b")]
    [InlineData("/policy ../x")]
    [InlineData("/policy daily_2026")]
    public async Task 書式外の会話キーは形式を案内し呼ばない(string raw)
    {
        var (handler, controller) = Create();

        var result = await handler.HandleAsync(Context(raw), "指示");

        (result.WasExecuted, result.IsDenied).Should().Be((false, false));
        result.Message.Should().Contain("形式が不正").And.Contain("daily-2026-09-28");
        controller.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task 長すぎる指示では呼ばない()
    {
        var (handler, controller) = Create();

        var result = await handler.HandleAsync(Context(), new string('a', PolicyRevisionCommandHandler.MaxInstructionLength + 1));

        result.WasExecuted.Should().BeFalse();
        result.Message.Should().Contain("1000");
        controller.Calls.Should().BeEmpty();
    }

    // T-10-1327: 報告書サービスの失敗・不明はそのまま見せ、確認ボタンを出さない（失敗と不明を混ぜない）。
    [Theory]
    [InlineData(false, "AI の案を作れませんでした（AI の呼び出しに失敗しました）。方針は変わっていません。")]
    [InlineData(true, "方針の改訂の結果が分かりません（応答が届きませんでした）。案が保存された可能性があるため、/report show で確認してください。")]
    public async Task 失敗と不明はそのまま見せボタンを出さない(bool indeterminate, string message)
    {
        var (handler, _) = Create(new PolicyRevisionCommandOutcome(false, indeterminate, message));

        var result = await handler.HandleAsync(Context(), "指示");

        result.WasExecuted.Should().BeFalse();
        result.IsDenied.Should().BeFalse();
        result.Message.Should().Be(message);
        result.Version.Should().BeNull();
    }

    // T-10-1328: どの通も上限に収まり、入れ替え案は表示のみと添え、入れ替えが無ければ「なし」と書く。
    [Fact]
    public void どの通も上限に収まり監視銘柄は表示のみと添える()
    {
        var messages = PolicyRevisionMessage.Build(
            "daily-2026-09-27", 3, presented: true, created: false, "保存しました",
            new string('方', 5000),
            [.. Enumerable.Range(0, 10).Select(i => ("add", $"SYM{i}", new string('理', 200)))],
            new string('説', 5000));

        messages.Should().OnlyContain(m => m.Length <= PolicyRevisionMessage.MaxLength);
        messages.Should().Contain(m => m.Contains(PolicyRevisionMessage.WatchlistWillApplyNotice));

        PolicyRevisionMessage.Build("daily-2026-09-27", 1, true, false, "保存しました", "方針", [], null)[^1]
            .Should().Contain("【監視銘柄の入れ替え案】").And.Contain("- なし");
    }

    // T-10-1345（監査 BLOCKING 1・ADR-0003）: **確認ボタンが出るときは、確定される方針の全文が必ず届いている。**
    // 方針は切り詰めず、1 通に収まらなければ `【方針案 i/n】` の通に分けて、ボタンの付く最後の通より前に送る。
    // 入れ替え案 10 件×理由 200 文字でも、方針 2000 文字（上限）でも、サロゲートペアを含んでも同じ。
    [Theory]
    [MemberData(nameof(Policies))]
    public async Task 確認ボタンが出るときは方針の全文が届いている(string policy, int changeCount, int reasonLength)
    {
        var changes = Enumerable.Range(0, changeCount)
            .Select(i => new WatchlistChangeSuggestionView(i % 2 == 0 ? "add" : "remove", $"SY{(char)('A' + i)}", new string('理', reasonLength)))
            .ToList();
        var (handler, _) = Create(new PolicyRevisionCommandOutcome(
            true, false, "保存", Proposal(policy: policy, changes: changes, rationale: new string('説', 1000))));

        var result = await handler.HandleAsync(Context(), "指示");

        result.Version.Should().NotBeNull("承認待ちの案には確認ボタンを出す");
        result.Messages.Should().OnlyContain(m => m.Length <= PolicyRevisionMessage.MaxLength);

        // ボタンは最後の通に付く。方針の通はそれより前にすべて並び、見出しを除いて連結すると全文に一致する。
        var policyMessages = result.Messages.Take(result.Messages.Count - 1)
            .Where(m => m.StartsWith("【方針案 ", StringComparison.Ordinal))
            .ToList();
        policyMessages.Should().NotBeEmpty();
        var count = policyMessages.Count;
        policyMessages.Select((m, i) => m.StartsWith(PolicyRevisionMessage.PolicyHeading(i + 1, count) + "\n", StringComparison.Ordinal))
            .Should().OnlyContain(ok => ok, "見出しは 1/n から順に並ぶ");
        string.Concat(policyMessages.Select(m => m[(m.IndexOf('\n') + 1)..])).Should().Be(policy);
        // 各通は単独で表示されるため、割り目で絵文字（サロゲートペア）を割らない。
        policyMessages.Select(m => m[(m.IndexOf('\n') + 1)..]).Where(c => c.Length > 0)
            .Should().OnlyContain(c => !char.IsHighSurrogate(c[c.Length - 1]) && !char.IsLowSurrogate(c[0]));
        result.Messages[^1].Should().NotContain("【方針案", "ボタンの付く通に方針の一部を載せない（全文はその前に届いている）");
    }

    public static TheoryData<string, int, int> Policies() => new()
    {
        { "押し目買いを優先する", 0, 0 },
        { "押し目買いを優先する", 10, 200 },
        { new string('方', 2000), 0, 0 },
        { new string('方', 2000), 10, 200 },
        { string.Concat(Enumerable.Range(0, 1000).Select(_ => "😀")), 10, 200 },
        { string.Concat(Enumerable.Range(0, 1800).Select(i => (char)('あ' + (i % 80)))), 3, 50 },
        // 🔴 T-10-1346（再監査 nit 3）: サロゲートペア（絵文字）の上位が 1 通目の割り目（奇数の位置 1787＝1 通の本文 1788 文字の最後）に
        // ちょうど掛かる。割り目のガードが無いと絵文字が 2 通に割れ、連結は一致しても各通に孤立したサロゲートが残る。
        { new string('あ', PolicyRevisionMessage.PolicyChunkSize - 1) + "😀" + new string('い', 100), 0, 0 },
    };

    // T-10-1346: 割り目にサロゲートペアが掛かるとき、どの通も孤立したサロゲートで始まらず終わらない（絵文字を壊さない）。
    [Fact]
    public void 割り目のサロゲートペアを割らない()
    {
        var size = PolicyRevisionMessage.PolicyChunkSize;
        (size % 2).Should().Be(0, "1 通の本文の長さが偶数なので、上位サロゲートは奇数の位置 size-1 に来る");
        var policy = new string('あ', size - 1) + "😀" + new string('い', 100);
        char.IsHighSurrogate(policy[size - 1]).Should().BeTrue("前提: 割り目の直前が上位サロゲート");

        var chunks = PolicyRevisionMessage.Split(policy, size);

        chunks.Should().HaveCount(2);
        chunks[0].Should().HaveLength(size - 1);
        chunks[1].Should().StartWith("😀");
        chunks.Should().OnlyContain(c => !char.IsHighSurrogate(c[c.Length - 1]) && !char.IsLowSurrogate(c[0]));
        string.Concat(chunks).Should().Be(policy);
    }

    // T-10-1347（再監査 nit 5）: 承認待ちにできなかった案では、報告書サービスの案内をそのまま見せ、確認ボタンを出さない。
    [Fact]
    public async Task 承認待ちにできなかった案は報告書サービスの案内を見せる()
    {
        const string guidance = "方針の改訂案を保存しましたが、承認待ちにできませんでした（/report show で状態を確認してください）。";
        var view = Proposal(presented: false) with { Message = guidance };
        var (handler, _) = Create(new PolicyRevisionCommandOutcome(true, false, guidance, view));

        var result = await handler.HandleAsync(Context(), "指示");

        result.Version.Should().BeNull();
        result.Messages[0].Should().Contain("未提示").And.Contain(guidance);
    }

    // T-10-1405（ADR-0042 決定 1・#1025）: 案を作る前に現在の監視銘柄を照会して報告書サービスへ運ぶ。確定で入れ替えを適用する旨と件数を返す。
    [Fact]
    public async Task 現在の監視銘柄を運び確定で適用する旨を見せる()
    {
        var watchlist = new FakeWatchlistController();
        var (handler, controller) = Create(watchlist: watchlist);

        var result = await handler.HandleAsync(Context(), "指示");

        watchlist.Gets.Should().Be(1);
        controller.Calls.Single().Watchlist.Should().Equal(watchlist.Snapshot.Items);
        result.WatchlistChangeCount.Should().Be(1);
        result.Messages.Should().Contain(m => m.Contains(PolicyRevisionMessage.WatchlistWillApplyNotice));
    }

    // T-10-1406: 監視銘柄を照会できなければ「空」ではなく null を運び、確定しても入れ替えは適用しない旨を見せる（ボタンの件数は 0）。
    [Fact]
    public async Task 監視銘柄を照会できなければ分からないとして運ぶ()
    {
        var watchlist = new FakeWatchlistController { Snapshot = new WatchlistSnapshotResult(false, [], "HTTP 503") };
        var (handler, controller) = Create(watchlist: watchlist);

        var result = await handler.HandleAsync(Context(), "指示");

        controller.Calls.Single().Watchlist.Should().BeNull();
        result.WatchlistChangeCount.Should().Be(0);
        result.Messages.Should().Contain(m => m.Contains(PolicyRevisionMessage.WatchlistUnknownNotice));
        result.Messages.Should().NotContain(m => m.Contains(PolicyRevisionMessage.WatchlistWillApplyNotice));
    }

    // T-10-1407（ADR-0042 決定 1「確認ボタンの前に、銘柄とその理由を表示する」）: 入れ替え 10 件×理由 200 文字でも理由を切り詰めず、
    // すべての理由がボタンの付く最後の通までに届く。どの通も上限内。
    [Fact]
    public async Task 入れ替えの理由は切り詰めず全文を見せる()
    {
        var changes = Enumerable.Range(0, 10)
            .Select(i => new WatchlistChangeSuggestionView(i % 2 == 0 ? "add" : "remove", $"SY{(char)('A' + i)}", new string((char)('あ' + i), 200)))
            .ToList();
        var (handler, _) = Create(new PolicyRevisionCommandOutcome(true, false, "保存", Proposal(changes: changes, rationale: new string('説', 1000))));

        var result = await handler.HandleAsync(Context(), "指示");

        result.Messages.Should().OnlyContain(m => m.Length <= PolicyRevisionMessage.MaxLength);
        var all = string.Join('\n', result.Messages);
        foreach (var c in changes)
            all.Should().Contain($"{c.Symbol}（米国）: {c.Reason}");
    }
}
