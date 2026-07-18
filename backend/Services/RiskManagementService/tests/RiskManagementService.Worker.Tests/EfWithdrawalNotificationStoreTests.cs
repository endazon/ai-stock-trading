using AiStockTrading.RiskManagement.Worker.Foundation.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiStockTrading.RiskManagement.Worker.Tests;

// FR-20, FR-09, IADR-0085, #189: 撤退の非停止降格提案の通知重複排除ストア（単一行）の EF 永続化を InMemory DB で検証する。
// durable な「通知済みシグネチャ」を別コンテキスト（＝再起動の代理）から読めること・クリアで再通知可能になることを担保する。
public class EfWithdrawalNotificationStoreTests
{
    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    [Fact]
    public void 未記録時は_null_を返す_未通知のfail_safe()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfWithdrawalNotificationStore(db);

        store.GetNotifiedSignature().Should().BeNull();
    }

    [Fact]
    public void 保存したシグネチャは別コンテキストからも読める_durable()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var db = NewContext(dbName))
        {
            new EfWithdrawalNotificationStore(db).SaveNotifiedSignature("PaperDeviationUnexplained:0");
        }

        using var db2 = NewContext(dbName);
        new EfWithdrawalNotificationStore(db2).GetNotifiedSignature()
            .Should().Be("PaperDeviationUnexplained:0", "再起動をまたいでも重複排除できる");
    }

    [Fact]
    public void 保存は単一行_upsert_で上書きする()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var db = NewContext(dbName))
        {
            new EfWithdrawalNotificationStore(db).SaveNotifiedSignature("A:0");
        }
        using (var db2 = NewContext(dbName))
        {
            new EfWithdrawalNotificationStore(db2).SaveNotifiedSignature("B:1");
        }

        using var db3 = NewContext(dbName);
        new EfWithdrawalNotificationStore(db3).GetNotifiedSignature().Should().Be("B:1");
    }

    [Fact]
    public void クリアで未通知に戻り再通知可能になる()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var db = NewContext(dbName))
        {
            new EfWithdrawalNotificationStore(db).SaveNotifiedSignature("PaperDeviationUnexplained:0");
        }
        using (var db2 = NewContext(dbName))
        {
            new EfWithdrawalNotificationStore(db2).ClearNotifiedSignature();
        }

        using var db3 = NewContext(dbName);
        new EfWithdrawalNotificationStore(db3).GetNotifiedSignature().Should().BeNull();
    }

    [Fact]
    public void 未記録のクリアは無害_例外を投げない()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfWithdrawalNotificationStore(db);

        var act = () => store.ClearNotifiedSignature();

        act.Should().NotThrow();
        store.GetNotifiedSignature().Should().BeNull();
    }
}
