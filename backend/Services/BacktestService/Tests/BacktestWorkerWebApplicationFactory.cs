using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace BacktestService.Tests;

// #208, IADR-0105: バックテストホストの WebApplicationFactory（他 Worker テスト準拠）。
// 本ホストは DB を持たないため差し替えは不要。構成だけを注入し、安全既定
// （Backtest:BarData:Provider 未設定＝no-op）のまま起動できることも併せて担保する。
// ADR-0013, IADR-0129, #688, IADR-0310: **メッセージバスは持つ**ため、実 RabbitMQ へ接続しないよう
// 外部トランスポートを無効化する（他 Worker テストと同型）。
public sealed class BacktestWorkerWebApplicationFactory(IDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // UseSetting（ホスト構成）で与える。ConfigureAppConfiguration の追加分は Program.cs が
        // builder.Configuration を**登録時に**読む箇所（実効構成の自己申告）へは間に合わないため、
        // 実運用（プロセス起動時に構成が揃っている状態）と同じ見え方にならない。
        builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            builder.UseSetting(key, value);

        // ADR-0013, IADR-0129, #354: 実 RabbitMQ へ接続しない（配線そのものは Program.cs 側が担う）。
        builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
    }
}
