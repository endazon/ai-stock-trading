using System.Net;
using AiStockTrading.Shared.KnowledgeBase;
using AwesomeAssertions;
using ReportService.Domain;
using ReportService.Features.Reports.ReingestKnowledgeBase;
using Xunit;
using static ReportService.Tests.ReportKnowledgeReingestTestKit;

namespace ReportService.Tests;

// FR-08, FR-11, #1028, IADR-0436: 確定済みの報告書を KB へ入れ直す（T-10-1493〜T-10-1498・T-10-1500・T-10-1501・T-10-1508）。
// 本番の Program.cs の組み立て（エンドポイント・サービス・EF ストア・Wolverine の発行）を通し、KB の台帳だけを模造に差し替える。
// 模造は基盤の意味（作成は毎回新しい文書・本文の投入は所有者だけ・一覧は全件）を持つ。
public class ReportKnowledgeReingestServiceTests
{
    private static readonly DateOnly D1 = new(2026, 7, 10);

    private static void SeedThree(IServiceProvider services)
    {
        Seed(services, "daily-2026-07-10", ReportKind.Daily, D1, "# 日報 07-10");
        Seed(services, "weekly-2026-W28", ReportKind.Weekly, new DateOnly(2026, 7, 6), "# 週報 W28");
        Seed(services, "monthly-2026-07", ReportKind.Monthly, new DateOnly(2026, 7, 1), "# 月報 07");
    }

    // T-10-1493（受け入れ基準 1）: 基盤の切替の後（KB が空）、1 回で確定済みの報告書が**本文つきで** KB に入る。
    // ドラフトは入れない。監査に操作者と件数が残る。
    [Fact]
    public async Task 切替の後の1回で確定済みの報告書が本文つきでKBに入り_ドラフトは入らない()
    {
        var kb = new FakeKnowledgeCatalog();
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithCatalog(baseFactory, kb);
        SeedThree(factory.Services);
        Seed(factory.Services, "daily-2026-07-11", ReportKind.Daily, D1.AddDays(1), "# 日報 07-11（未確定）", confirm: false);

        var (response, result, audits) = await RunAsync(factory, new { all = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        result!.Status.Should().Be("Completed");
        result.Targeted.Should().Be(3);
        result.Created.Should().Be(3);
        result.Sent.Should().Be(3);
        kb.Docs.Should().HaveCount(3);
        kb.Docs.Select(d => d.Attributes["periodKey"]).Should().BeEquivalentTo("daily-2026-07-10", "weekly-2026-W28", "monthly-2026-07");
        kb.Docs.Should().OnlyContain(d => d.Body != null && d.Body.StartsWith('#'), "本文が検索でヒットすることが FR-08 の受け入れ基準");
        kb.Docs.Single(d => d.Attributes["periodKey"] == "weekly-2026-W28").Body.Should().Be("# 週報 W28");
        kb.Docs.Should().NotContain(d => d.Attributes["periodKey"] == "daily-2026-07-11", "未確定の報告書は入れない");
        // 期間の昇順（月報 07-01 → 週報 07-06 → 日報 07-10）で処理する。
        result.Items.Select(i => i.PeriodKey).Should().Equal("monthly-2026-07", "weekly-2026-W28", "daily-2026-07-10");

        var audit = audits.Should().ContainSingle().Subject;
        audit.Actor.Should().Be("owner");
        audit.Scope.Should().Be("all");
        audit.Status.Should().Be("Completed");
        audit.Created.Should().Be(3);
        audit.Breakdown.Should().BeEmpty();
        result.AuditPublished.Should().BeTrue();
    }

    // T-10-1494（受け入れ基準 2）: 2 回実行しても KB の件数が変わらない。2 回目は何も書かない。
    [Fact]
    public async Task 二回実行してもKBの件数は変わらない()
    {
        var kb = new FakeKnowledgeCatalog();
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithCatalog(baseFactory, kb);
        SeedThree(factory.Services);

        await RunAsync(factory, new { all = true });
        var countAfterFirst = kb.Docs.Count;
        var (response, second, audits) = await RunAsync(factory, new { all = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        kb.Docs.Count.Should().Be(countAfterFirst).And.Be(3);
        kb.CreateCalls.Should().Be(3, "2 回目は作成を呼ばない");
        kb.PutCalls.Should().Be(0);
        second!.AlreadyPresent.Should().Be(3);
        second.Sent.Should().Be(0);
        second.Items.Should().OnlyContain(i => i.Outcome == ReportKnowledgeReingestOutcome.AlreadyPresent && i.DocumentId != null);
        audits.Should().ContainSingle().Which.AlreadyPresent.Should().Be(3);
    }

    // T-10-1495（受け入れ基準 3）: 本文が空・上限超は送らず、件数と理由を返す（#565 と同じ扱い）。監査の内訳にも残る。
    [Fact]
    public async Task 本文が空と上限超は送らず件数と理由を返す()
    {
        var kb = new FakeKnowledgeCatalog();
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithCatalog(baseFactory, kb);
        Seed(factory.Services, "daily-2026-07-10", ReportKind.Daily, D1, string.Empty);
        Seed(factory.Services, "daily-2026-07-13", ReportKind.Daily, D1.AddDays(3), new string('a', KnowledgeBodyLimits.MaxBytes + 1));
        Seed(factory.Services, "daily-2026-07-14", ReportKind.Daily, D1.AddDays(4), new string('a', KnowledgeBodyLimits.MaxBytes));

        var (response, result, audits) = await RunAsync(factory, new { all = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        result!.Skipped.Should().Be(2);
        result.SkippedEmptyBody.Should().Be(1);
        result.SkippedBodyTooLarge.Should().Be(1);
        result.Created.Should().Be(1, "上限ちょうどは送る");
        var empty = result.Items.Single(i => i.PeriodKey == "daily-2026-07-10");
        empty.Outcome.Should().Be(ReportKnowledgeReingestOutcome.SkippedEmptyBody);
        empty.Reason.Should().Contain("本文が空");
        result.Items.Single(i => i.PeriodKey == "daily-2026-07-13").Reason.Should().Contain(KnowledgeBodyLimits.MaxBytes.ToString());
        kb.Docs.Should().ContainSingle().Which.Attributes["periodKey"].Should().Be("daily-2026-07-14");

        var audit = audits.Should().ContainSingle().Subject;
        audit.SkippedEmptyBody.Should().Be(1);
        audit.SkippedBodyTooLarge.Should().Be(1);
        audit.Breakdown.Select(b => (b.PeriodKey, b.Outcome)).Should().BeEquivalentTo(new[]
        {
            ("daily-2026-07-10", "SkippedEmptyBody"),
            ("daily-2026-07-13", "SkippedBodyTooLarge"),
        });
    }

    // T-10-1496（受け入れ基準 4）: 本文なしで入った写し（#565）には本文を入れる（文書は増えない）。
    // 所有者でない写しは直せない——失敗として理由を返し、**別の文書を作らない**（重複を作らない）。
    [Fact]
    public async Task 本文なしの写しには本文を入れ_直せなければ失敗として作らない()
    {
        var kb = new FakeKnowledgeCatalog();
        var owned = kb.AddExisting("daily-2026-07-10", "Daily", body: null);
        var notOwned = kb.AddExisting("daily-2026-07-13", "Daily", body: null, ownedByAst: false);
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithCatalog(baseFactory, kb);
        Seed(factory.Services, "daily-2026-07-10", ReportKind.Daily, D1, "# 日報 07-10");
        Seed(factory.Services, "daily-2026-07-13", ReportKind.Daily, D1.AddDays(3), "# 日報 07-13");

        var (_, result, audits) = await RunAsync(factory, new { all = true });

        kb.Docs.Should().HaveCount(2, "本文の投入でも失敗でも文書を増やさない");
        kb.CreateCalls.Should().Be(0);
        owned.Body.Should().Be("# 日報 07-10");
        var attached = result!.Items.Single(i => i.PeriodKey == "daily-2026-07-10");
        attached.Outcome.Should().Be(ReportKnowledgeReingestOutcome.BodyAttached);
        attached.DocumentId.Should().Be(owned.Id);
        var failed = result.Items.Single(i => i.PeriodKey == "daily-2026-07-13");
        failed.Outcome.Should().Be(ReportKnowledgeReingestOutcome.Failed);
        failed.Reason.Should().Contain("別の主体が所有").And.Contain("管理者が写しを削除");
        failed.DocumentId.Should().Be(notOwned.Id);
        notOwned.Body.Should().BeNull();
        result.Sent.Should().Be(1);
        result.BodyAttached.Should().Be(1);

        var audit = audits.Should().ContainSingle().Subject;
        audit.BodyAttached.Should().Be(1);
        audit.Failed.Should().Be(1);
        audit.Breakdown.Should().ContainSingle().Which.DocumentId.Should().Be(notOwned.Id);
    }

    // T-10-1497（受け入れ基準 5・原則 A）: 送った／失敗（理由）／不明（タイムアウト）を分ける。不明は成功にも失敗にも数えない。
    // 不明のまま KB に入っていた写しは、次の実行が一覧で見つけるので**重複しない**。失敗したものは次の実行で作られる。
    [Fact]
    public async Task 送った失敗不明を分け_不明は次の実行で重複しない()
    {
        var kb = new FakeKnowledgeCatalog();
        kb.CreateBehavior["daily-2026-07-10"] = "saved-unknown";
        kb.CreateBehavior["daily-2026-07-13"] = "failed";
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithCatalog(baseFactory, kb);
        Seed(factory.Services, "daily-2026-07-10", ReportKind.Daily, D1, "# A");
        Seed(factory.Services, "daily-2026-07-13", ReportKind.Daily, D1.AddDays(3), "# B");
        Seed(factory.Services, "daily-2026-07-14", ReportKind.Daily, D1.AddDays(4), "# C");

        var (response, first, audits) = await RunAsync(factory, new { all = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK, "個別の失敗は実行の失敗ではない");
        first!.Items.Single(i => i.PeriodKey == "daily-2026-07-10").Outcome.Should().Be(ReportKnowledgeReingestOutcome.Unknown);
        first.Items.Single(i => i.PeriodKey == "daily-2026-07-10").Reason.Should().Contain("タイムアウト");
        first.Items.Single(i => i.PeriodKey == "daily-2026-07-13").Outcome.Should().Be(ReportKnowledgeReingestOutcome.Failed);
        first.Items.Single(i => i.PeriodKey == "daily-2026-07-13").Reason.Should().Contain("HTTP 400");
        first.Items.Single(i => i.PeriodKey == "daily-2026-07-14").Outcome.Should().Be(ReportKnowledgeReingestOutcome.Created);
        first.Sent.Should().Be(1, "不明は送ったに数えない");
        first.Failed.Should().Be(1, "不明は失敗に数えない");
        first.Unknown.Should().Be(1);
        var audit = audits.Should().ContainSingle().Subject;
        (audit.Created, audit.Failed, audit.Unknown).Should().Be((1, 1, 1));
        audit.Breakdown.Select(b => b.Outcome).Should().BeEquivalentTo("Unknown", "Failed");

        kb.CreateBehavior.Clear();
        var (_, second, _) = await RunAsync(factory, new { all = true });

        kb.Docs.Count(d => d.Attributes["periodKey"] == "daily-2026-07-10").Should().Be(1, "不明のまま入っていた写しを作り直さない");
        second!.Items.Single(i => i.PeriodKey == "daily-2026-07-10").Outcome.Should().Be(ReportKnowledgeReingestOutcome.AlreadyPresent);
        second.Items.Single(i => i.PeriodKey == "daily-2026-07-13").Outcome.Should().Be(ReportKnowledgeReingestOutcome.Created);
        kb.Docs.Should().HaveCount(3);
    }

    // T-10-1498（受け入れ基準 6）: KB の一覧を引けなければ 1 件も書かない（既存の写しが見えないまま作ると重複する）。
    // 中止の理由は応答と監査に残る。未構成は 503、失敗・不明は 502。
    [Theory]
    [InlineData("failed", 502)]
    [InlineData("unknown", 502)]
    public async Task 一覧を引けなければ1件も書かず中止を監査に残す(string listOutcome, int expectedStatus)
    {
        var kb = new FakeKnowledgeCatalog
        {
            ListOverride = listOutcome == "failed"
                ? KnowledgeCatalogListResult.Failed("KB の文書一覧を読めませんでした（HTTP 401）。")
                : KnowledgeCatalogListResult.Unknown("KB の文書一覧の取得がタイムアウトしました（応答が来ていません）。"),
        };
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithCatalog(baseFactory, kb);
        SeedThree(factory.Services);

        var (response, result, audits) = await RunAsync(factory, new { all = true });

        ((int)response.StatusCode).Should().Be(expectedStatus);
        kb.CreateCalls.Should().Be(0);
        kb.PutCalls.Should().Be(0);
        result!.Status.Should().Be("Aborted");
        result.AbortReason.Should().Be(kb.ListOverride.Reason);
        result.Targeted.Should().Be(3);
        result.NotAttempted.Should().Be(3);
        result.Sent.Should().Be(0);
        var audit = audits.Should().ContainSingle().Subject;
        audit.Status.Should().Be("Aborted");
        audit.AbortReason.Should().Be(kb.ListOverride.Reason);
        audit.Targeted.Should().Be(3);
        audit.NotAttempted.Should().Be(3, "中止では全件が試していない（Targeted と件数の合計が一致する）");
    }

    [Fact]
    public async Task KBが未構成なら503で何も送らず中止を監査に残す()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithCatalog(baseFactory, catalog: null);
        SeedThree(factory.Services);

        var (response, result, audits) = await RunAsync(factory, new { all = true });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        result!.Status.Should().Be("Aborted");
        result.AbortReason.Should().Contain("構成されていません");
        audits.Should().ContainSingle().Which.Status.Should().Be("Aborted");
    }

    // T-10-1500: KB 上に同じ報告書の写しが複数あれば本文のあるほうを採り（更新が新しくても本文なしは採らない）、件数を返す。
    // 他のプロジェクトの文書・属性の欠けた文書は一致に数えない。
    [Fact]
    public async Task 重複した写しは本文のあるほうを採り_他のプロジェクトの文書は一致に数えない()
    {
        var kb = new FakeKnowledgeCatalog();
        var withBody = kb.AddExisting("daily-2026-07-10", "Daily", "# 旧", updatedAt: DateTimeOffset.UtcNow.AddDays(-2));
        kb.AddExisting("daily-2026-07-10", "Daily", body: null, updatedAt: DateTimeOffset.UtcNow);
        kb.AddExisting("daily-2026-07-13", "Daily", "# 他", project: "other-project");
        kb.AddExisting("daily-2026-07-14", "Weekly", "# 種別違い");
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithCatalog(baseFactory, kb);
        Seed(factory.Services, "daily-2026-07-10", ReportKind.Daily, D1, "# 日報 07-10");
        Seed(factory.Services, "daily-2026-07-13", ReportKind.Daily, D1.AddDays(3), "# 日報 07-13");
        Seed(factory.Services, "daily-2026-07-14", ReportKind.Daily, D1.AddDays(4), "# 日報 07-14");

        var (_, result, audits) = await RunAsync(factory, new { all = true });

        var chosen = result!.Items.Single(i => i.PeriodKey == "daily-2026-07-10");
        chosen.Outcome.Should().Be(ReportKnowledgeReingestOutcome.AlreadyPresent);
        chosen.DocumentId.Should().Be(withBody.Id);
        result.DuplicatesInKb.Should().Be(1);
        var dupAudit = audits.Should().ContainSingle().Subject;
        dupAudit.DuplicatesInKb.Should().Be(1);
        dupAudit.DuplicatePeriodKeys.Should().Equal("daily-2026-07-10");
        chosen.MatchedCopies.Should().Be(2, "どの報告書の写しが重複しているかを行で返す");
        result.Items.Where(i => i.PeriodKey != "daily-2026-07-10").Should().OnlyContain(i => i.MatchedCopies == 0);
        kb.PutCalls.Should().Be(0);
        result.Items.Single(i => i.PeriodKey == "daily-2026-07-13").Outcome.Should().Be(ReportKnowledgeReingestOutcome.Created);
        result.Items.Single(i => i.PeriodKey == "daily-2026-07-14").Outcome.Should().Be(ReportKnowledgeReingestOutcome.Created);
    }

    // T-10-1509（PR #1038 の監査 1）: project 属性（#665・2026-09-03）より前の保存で作られた写しは project を持たない
    // （#665 より前に本文なしで入った写しはこの形。以降の手動確定の本文なしの写しは project を持つ）。表題が確定時の写像の表題と完全に一致すれば写しとして扱い、**隣に 2 つ目を作らない**。
    // AST が所有していれば本文を入れ、所有していなければ（owner=system 等で基盤が 404）失敗として管理者の削除を案内する。
    [Fact]
    public async Task 旧い形の写しは表題で見つけ_本文を入れるか失敗にして作らない()
    {
        var kb = new FakeKnowledgeCatalog();
        var legacyOwned = kb.AddExisting("daily-2026-07-10", "Daily", body: null, project: null);
        var legacyOther = kb.AddExisting("daily-2026-07-13", "Daily", body: null, ownedByAst: false, project: null);
        var legacyWithBody = kb.AddExisting("daily-2026-07-14", "Daily", "# 旧い本文", project: null);
        kb.AddExisting("daily-2026-07-15", "Daily", body: null, project: null, title: "確定報告書 Daily daily-2026-07-15（利用者の複製）");
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithCatalog(baseFactory, kb);
        foreach (var day in new[] { 10, 13, 14, 15 })
            Seed(factory.Services, $"daily-2026-07-{day}", ReportKind.Daily, new DateOnly(2026, 7, day), $"# 日報 07-{day}");

        var (_, first, _) = await RunAsync(factory, new { all = true });

        var attached = first!.Items.Single(i => i.PeriodKey == "daily-2026-07-10");
        attached.Outcome.Should().Be(ReportKnowledgeReingestOutcome.BodyAttached);
        attached.DocumentId.Should().Be(legacyOwned.Id);
        attached.MatchedCopies.Should().Be(1);
        legacyOwned.Body.Should().Be("# 日報 07-10");

        var notOwned = first.Items.Single(i => i.PeriodKey == "daily-2026-07-13");
        notOwned.Outcome.Should().Be(ReportKnowledgeReingestOutcome.Failed);
        notOwned.DocumentId.Should().Be(legacyOther.Id);
        notOwned.Reason.Should().Contain("別の主体が所有").And.Contain("新しい写しは作りません").And.Contain("管理者が写しを削除");

        var present = first.Items.Single(i => i.PeriodKey == "daily-2026-07-14");
        present.Outcome.Should().Be(ReportKnowledgeReingestOutcome.AlreadyPresent);
        present.DocumentId.Should().Be(legacyWithBody.Id);

        // 表題の違う project なしの文書は AST の写しと言い切れない（作る）。
        first.Items.Single(i => i.PeriodKey == "daily-2026-07-15").Outcome.Should().Be(ReportKnowledgeReingestOutcome.Created);
        kb.CreateCalls.Should().Be(1, "旧い形の写しの隣には作らない");
        kb.Docs.Count(d => d.Attributes["periodKey"] == "daily-2026-07-13").Should().Be(1);

        var (_, second, _) = await RunAsync(factory, new { all = true });
        kb.CreateCalls.Should().Be(1, "何度実行しても所有者でない写しの隣に作らない");
        second!.Items.Single(i => i.PeriodKey == "daily-2026-07-13").Outcome.Should().Be(ReportKnowledgeReingestOutcome.Failed);
        second.Items.Single(i => i.PeriodKey == "daily-2026-07-10").Outcome.Should().Be(ReportKnowledgeReingestOutcome.AlreadyPresent);
    }

    // T-10-1500（PR #1038 の監査 2）: 本文なしの写しが複数あれば、project を持つ写しを先に試し、所有者でない（404）なら
    // 次の写しへ進んで AST が所有する写しに本文を入れる。404 以外の失敗はそこで止める（次を試さず・作らない）。
    [Fact]
    public async Task 本文なしの写しが複数なら所有する写しを採り_404以外の失敗では止まる()
    {
        var kb = new FakeKnowledgeCatalog();
        var now = DateTimeOffset.UtcNow;
        // A: 新しいが所有者でない → B: 古いが AST の所有。
        var a = kb.AddExisting("daily-2026-07-10", "Daily", body: null, ownedByAst: false, updatedAt: now);
        var b = kb.AddExisting("daily-2026-07-10", "Daily", body: null, updatedAt: now.AddDays(-3));
        // project を持つ写し（古い）を旧い形（新しい）より先に試す。
        var modern = kb.AddExisting("daily-2026-07-13", "Daily", body: null, updatedAt: now.AddDays(-3));
        var legacy = kb.AddExisting("daily-2026-07-13", "Daily", body: null, project: null, updatedAt: now);
        // 先に試す写しが 413（404 以外）で拒否される。
        var rejected = kb.AddExisting("daily-2026-07-14", "Daily", body: null, updatedAt: now);
        var untouched = kb.AddExisting("daily-2026-07-14", "Daily", body: null, updatedAt: now.AddDays(-3));
        kb.PutOverride[rejected.Id] = KnowledgeCatalogWriteResult.Failed("本文の投入を拒否されました（HTTP 413）。", 413);
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithCatalog(baseFactory, kb);
        foreach (var day in new[] { 10, 13, 14 })
            Seed(factory.Services, $"daily-2026-07-{day}", ReportKind.Daily, new DateOnly(2026, 7, day), $"# 日報 07-{day}");

        var (_, result, _) = await RunAsync(factory, new { all = true });

        var first = result!.Items.Single(i => i.PeriodKey == "daily-2026-07-10");
        first.Outcome.Should().Be(ReportKnowledgeReingestOutcome.BodyAttached);
        first.DocumentId.Should().Be(b.Id);
        first.MatchedCopies.Should().Be(2);
        a.Body.Should().BeNull();
        b.Body.Should().Be("# 日報 07-10");

        var second = result.Items.Single(i => i.PeriodKey == "daily-2026-07-13");
        second.DocumentId.Should().Be(modern.Id);
        modern.Body.Should().Be("# 日報 07-13");
        legacy.Body.Should().BeNull("project を持つ写しに入れられたら旧い形は試さない");

        var third = result.Items.Single(i => i.PeriodKey == "daily-2026-07-14");
        third.Outcome.Should().Be(ReportKnowledgeReingestOutcome.Failed);
        third.Reason.Should().Contain("HTTP 413");
        untouched.Body.Should().BeNull("404 以外の失敗では次の写しを試さない");

        kb.PutCalls.Should().Be(2 + 1 + 1);
        kb.CreateCalls.Should().Be(0);
        result.DuplicatesInKb.Should().Be(3);
    }

    // T-10-1501: refreshExisting は本文のある写しにも本文を入れ直す（索引の作り直し）。文書は増えない。
    [Fact]
    public async Task 入れ直しの指定は本文を入れ直すが文書を増やさない()
    {
        var kb = new FakeKnowledgeCatalog();
        var existing = kb.AddExisting("daily-2026-07-10", "Daily", "# 古い本文");
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithCatalog(baseFactory, kb);
        Seed(factory.Services, "daily-2026-07-10", ReportKind.Daily, D1, "# 日報 07-10");

        var (_, result, audits) = await RunAsync(factory, new { all = true, refreshExisting = true });

        result!.Items.Should().ContainSingle().Which.Outcome.Should().Be(ReportKnowledgeReingestOutcome.BodyRefreshed);
        result.RefreshExisting.Should().BeTrue();
        result.Sent.Should().Be(1);
        kb.Docs.Should().ContainSingle();
        existing.Body.Should().Be("# 日報 07-10");
        var audit = audits.Should().ContainSingle().Subject;
        audit.BodyRefreshed.Should().Be(1);
        audit.RefreshExisting.Should().BeTrue();
    }

    // T-10-1501（PR #1038 の差分監査 M4）: 入れ直しの指定で本文ありの写しがすべて別の主体の所有（404）でも、段を跨いで
    // 本文なしの写しへは入れない（Failed・作らない）。本文なしの写しは AST の所有でも触らない。
    [Fact]
    public async Task 入れ直しで本文ありの写しがすべて所有外でも本文なしの写しへは入れない()
    {
        var kb = new FakeKnowledgeCatalog();
        var now = DateTimeOffset.UtcNow;
        var withBodyA = kb.AddExisting("daily-2026-07-10", "Daily", "# 旧い本文 A", ownedByAst: false, updatedAt: now);
        var withBodyB = kb.AddExisting("daily-2026-07-10", "Daily", "# 旧い本文 B", ownedByAst: false, updatedAt: now.AddDays(-1));
        var bodylessOwned = kb.AddExisting("daily-2026-07-10", "Daily", body: null, updatedAt: now.AddDays(1));
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithCatalog(baseFactory, kb);
        Seed(factory.Services, "daily-2026-07-10", ReportKind.Daily, D1, "# 日報 07-10");

        var (_, result, _) = await RunAsync(factory, new { all = true, refreshExisting = true });

        var item = result!.Items.Should().ContainSingle().Subject;
        item.Outcome.Should().Be(ReportKnowledgeReingestOutcome.Failed);
        item.Reason.Should().Contain("別の主体が所有");
        item.DocumentId.Should().Be(withBodyA.Id);
        item.MatchedCopies.Should().Be(3);
        kb.PutCalls.Should().Be(2, "本文ありの 2 件だけを試す");
        bodylessOwned.Body.Should().BeNull("段を跨いで本文なしの写しへ入れない");
        withBodyA.Body.Should().Be("# 旧い本文 A");
        withBodyB.Body.Should().Be("# 旧い本文 B");
        kb.CreateCalls.Should().Be(0);
    }

    // T-10-1497（PR #1038 の差分監査 M1）: 本文の投入の結果が不明（タイムアウト・5xx）なら行は Unknown で、次の写しへ進まない
    // （最初の写しに入ったかもしれない。2 つの写しへ書かない）。写しが 1 件でも不明は Unknown。作らない。
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task 本文の投入が不明なら行は不明で次の写しへ進まない(int copies)
    {
        var kb = new FakeKnowledgeCatalog();
        var now = DateTimeOffset.UtcNow;
        var firstCopy = kb.AddExisting("daily-2026-07-10", "Daily", body: null, updatedAt: now);
        var secondCopy = copies == 2 ? kb.AddExisting("daily-2026-07-10", "Daily", body: null, updatedAt: now.AddDays(-1)) : null;
        kb.PutOverride[firstCopy.Id] = KnowledgeCatalogWriteResult.Unknown(
            "KB の文書への本文の投入がタイムアウトしました（結果が分かりません。保存された可能性があります）。");
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithCatalog(baseFactory, kb);
        Seed(factory.Services, "daily-2026-07-10", ReportKind.Daily, D1, "# 日報 07-10");

        var (_, result, audits) = await RunAsync(factory, new { all = true });

        var item = result!.Items.Should().ContainSingle().Subject;
        item.Outcome.Should().Be(ReportKnowledgeReingestOutcome.Unknown);
        item.DocumentId.Should().Be(firstCopy.Id);
        item.Reason.Should().Contain("タイムアウト");
        kb.PutCalls.Should().Be(1, "不明の後に次の写しを試さない");
        secondCopy?.Body.Should().BeNull("2 つ目の写しは触らない");
        kb.CreateCalls.Should().Be(0);
        result.Unknown.Should().Be(1);
        result.Sent.Should().Be(0);
        audits.Should().ContainSingle().Which.Unknown.Should().Be(1);
    }

    // T-10-1508: 監査の内訳は 200 件まで。超えた件数は BreakdownOmitted に残す（件数そのものは全数）。
    [Fact]
    public async Task 監査の内訳は上限までで超過は件数で残す()
    {
        var kb = new FakeKnowledgeCatalog();
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithCatalog(baseFactory, kb);
        var total = ReportKnowledgeReingestService.MaxAuditBreakdown + 5;
        for (var i = 0; i < total; i++)
        {
            var day = new DateOnly(2025, 1, 1).AddDays(i);
            Seed(factory.Services, $"daily-{day:yyyy-MM-dd}", ReportKind.Daily, day, string.Empty);
        }

        var (_, result, audits) = await RunAsync(factory, new { all = true });

        result!.SkippedEmptyBody.Should().Be(total);
        result.Items.Should().HaveCount(total, "応答は全件を返す");
        var audit = audits.Should().ContainSingle().Subject;
        audit.SkippedEmptyBody.Should().Be(total);
        audit.Breakdown.Should().HaveCount(ReportKnowledgeReingestService.MaxAuditBreakdown);
        audit.BreakdownOmitted.Should().Be(5);
    }
}
