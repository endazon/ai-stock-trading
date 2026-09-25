using AiStockTrading.TestSupport.Composition;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 NFR, #947, IADR-0397, IADR-0163 決定2: **本番の組み立て（Program.cs）に配線の抜けが無い**ことを機械的に表明する。
// 規則（W0 組めない / W1 省略可能依存の未解決 / W2 渡し忘れ / W3 偽物の陰の本物）と所見への対処は IADR-0397。
//
// PR #919 の形（`AddSingleton<IDecisionSkipReporter, MetricsDecisionSkipReporter>()` を消しても 716 件すべて緑）は、
// 本ガードでは W1 `TradeDecisionAppService(skipReporter)` と W2 `TradeDecisionAppService._skipReporter` で赤になる
// （IADR-0397 の変異再注入の実測）。個別の DecisionSkipReporterRegistrationTests は残す（実装型まで固定している）。
// 母集団の実測（develop 3d9b91c2）: roots=37 handlers=2 fields=71。下限はその半分に置く。
public class CompositionWiringGuardTests(ITestOutputHelper output)
{
    // 🔴 無視リストではなくラチェットである（所見が消えた行も赤）。1 行ごとに理由・外す条件・issue 番号を書く。
    private static readonly IReadOnlyDictionary<string, string> Allowlist =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["W1 TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService(retrievalSourcePolicy)"] =
                "#252 / IADR-0169 決定2: RAG 出典限定は本番でも登録せず、未指定を安全側の RetrievalSourcePolicy.Default "
                + "（収集側の許可リストと同一語彙）で組む設計である。既定値そのものが本番値であり、NoOp へ落ちる形ではない"
                + "（語彙の一致は RetrievalSourceVocabularyTests が固定）。外す条件: 出典の許可を構成から注入するようになったとき。",
        };

    [Fact]
    public void 本番の組み立てに配線の抜けが無い()
    {
        using var factory = new Factory();

        var report = factory.InspectComposition(typeof(CompositionWiringGuardTests).Assembly);

        output.WriteLine(report.Describe("TradeDecisionService"));
        report.AssertPopulation(minRoots: 18, minFields: 35, minHandlers: 1);
        report.AssertNoUnexpectedFindings(Allowlist);
    }

    // 判断サービスは DB を持たない。外界は RabbitMQ（伝送を無効化）だけで、LLM・KB・市場データは
    // 構成が無ければ Program.cs 自身が安全既定を選ぶ（その選択も本番の組み立ての一部として検査する）。
    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
