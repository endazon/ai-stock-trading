using System.Globalization;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-04, FR-15, #1308, IADR-0528 決定7: Stage 0 の記録器の as-of 入力の**基底**の組み立て（Program.cs の連鎖の最も内側）。
//
// - `Reports:BaseUrl` と `OrderExecution:BaseUrl` がともに絶対 URL のときだけ基底の供給（BaseAsOfDecisionInputProvider）。
//   そうでなければ従来の `NoAsOfDecisionInputProvider`（記録は 0 件・LLM 0 回）。
//   ［2026-10-17 追記 / #1388・IADR-0557 決定 2］報告書の口は `Reports:Grpc`（east-west gRPC の宣言）でも満たす。宣言があれば当時の方針は
//   gRPC（GrpcAsOfDailyPolicySource）で読み、`Reports:BaseUrl` より優先する（段 1〜5 と同じ規則。既定は REST）。
//   ［2026-10-17 追記 / #1391・IADR-0559 決定 3］日足の口は `OrderExecution:Grpc` でも満たす。宣言があれば当時の価格の日足は
//   gRPC（GrpcDailyBarsSource）で読み、`OrderExecution:BaseUrl` より優先する。
// - (b) の 3 源は個別に構成する（`Stage0AsOf:Finnhub:ApiKey`・`Stage0AsOf:SecEdgar:UserAgent`＋`Ciks`・`Stage0AsOf:Fred:ApiKey`＋`SeriesIds`）。
//   構成の無い源は組み立てに入れない（AsOfReferenceAssembler が「未構成」として (b) を再構成不可にする）。
// - 上位 K 件と新しさの足切りは本番の取得と同じ構成（`Retrieval:TopK`・`Retrieval:MaxAgeHours`）を読む。
// - 🔴 記録を走らせるかはこの外側の `Stage0Recording`（無効・未承認・期間なし・出力先なし）が決める。ここは入力を組むだけ。
public static class Stage0AsOfComposition
{
    public const string SectionName = "Stage0AsOf";

    /// <summary>外部の 3 源（Finnhub・SEC EDGAR・FRED）へ出る HttpClient の名前。</summary>
    public const string ExternalHttpClientName = "stage0-asof-external";

    /// <summary>Finnhub の自制の既定（回/分）。同じ鍵の他の用途の合計（57 回/分）と足して 60 回/分に収まる値。</summary>
    public const int DefaultFinnhubRequestsPerMinute = 3;

    /// <summary>
    /// 基底の供給の口（方針と日足）の接続先がそろっているか。方針の口は gRPC の宣言（<c>Reports:Grpc</c>）でも、
    /// 日足の口は gRPC の宣言（<c>OrderExecution:Grpc</c>。#1391・IADR-0559 決定 3）でも満たす。
    /// </summary>
    /// <remarks>宣言の値の検証（絶対 URL・http）は輸送の登録（<see cref="ReportsGrpcExtensions"/>・<see cref="OrderExecutionGrpcExtensions"/>）が起動時に行う。</remarks>
    public static bool HasBaseEndpoints(IConfiguration configuration) =>
        (IsAbsolute(configuration["Reports:BaseUrl"]) || !string.IsNullOrWhiteSpace(configuration[ReportsGrpcTransport.AddressKey]))
        && (IsAbsolute(configuration["OrderExecution:BaseUrl"])
            || !string.IsNullOrWhiteSpace(configuration[OrderExecutionGrpcTransport.AddressKey]));

    /// <summary>連鎖の最も内側（基底）を組む。</summary>
    public static IAsOfDecisionInputProvider CreateBase(IServiceProvider sp)
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        var loggers = sp.GetRequiredService<ILoggerFactory>();
        if (!HasBaseEndpoints(configuration))
        {
            loggers.CreateLogger(typeof(Stage0AsOfComposition).FullName!).LogWarning(
                "Stage 0 の as-of 入力の基底を組めません（Reports:BaseUrl〔または Reports:Grpc〕と OrderExecution:BaseUrl〔または OrderExecution:Grpc〕がそろっていない）。"
                    + "記録は 1 件も作られません（NoAsOfDecisionInputProvider）。");
            return new NoAsOfDecisionInputProvider();
        }

        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var time = sp.GetRequiredService<TimeProvider>();
        var feeds = Feeds(configuration, factory, time, loggers);
        // 🟢 #1326 の AI レビュー: 構成された源と未構成の源を出す（鍵が欠けて黙って (b) が再構成不可になるのを、通し試験のログで追えるように）。
        // 基底の供給は記録の実行の scope ごとに組まれる（記録器は起動時に 1 回だけ走る）。
        var configured = feeds.Select(f => f.SourceName).ToList();
        loggers.CreateLogger(typeof(Stage0AsOfComposition).FullName!).LogInformation(
            "Stage 0 の as-of 入力の基底を組みました。参考情報の源: 構成あり [{Configured}] / 未構成 [{Unconfigured}]（未構成の源があると (b) は再構成不可）。",
            string.Join(", ", configured),
            string.Join(", ", AsOfReferenceAssembler.RequiredSources.Where(r => !configured.Contains(r))));

        // NFR, #1391, IADR-0559 決定 3: 日足の口。gRPC の輸送（`OrderExecution:Grpc`）があれば gRPC、無ければ REST（判断の共有口と同じ選択）。
        // HasBaseEndpoints を満たしているので null にはならない。
        var dailyBars = DailyBarsComposition.Source(sp, configuration)!;

        return new BaseAsOfDecisionInputProvider(
            new DailyBarsAsOfPriceSource(
                dailyBars,
                time,
                loggers.CreateLogger<DailyBarsAsOfPriceSource>()),
            PolicySource(sp, configuration, factory, time, loggers),
            new AsOfReferenceAssembler(
                feeds,
                ParsePositiveInt(configuration["Retrieval:TopK"], 5),
                KnowledgeBaseRetrievalContextProvider.ParseMaxAge(configuration["Retrieval:MaxAgeHours"])),
            loggers.CreateLogger<BaseAsOfDecisionInputProvider>());
    }

    // NFR, #1388, IADR-0557 決定 2: 当時の方針の口。gRPC の輸送が登録されていれば（`Reports:Grpc` の宣言）gRPC、無ければ REST の "reports"。
    internal static IAsOfDailyPolicySource PolicySource(
        IServiceProvider sp, IConfiguration configuration, IHttpClientFactory factory, TimeProvider time, ILoggerFactory loggers)
    {
        if (sp.GetService<ReportsGrpcTransport>() is { } reportsGrpc)
            return new GrpcAsOfDailyPolicySource(reportsGrpc, time, loggers.CreateLogger<GrpcAsOfDailyPolicySource>());

        var reports = factory.CreateClient("reports");
        reports.BaseAddress = new Uri(configuration["Reports:BaseUrl"]!);
        return new HttpAsOfDailyPolicySource(reports, time, loggers.CreateLogger<HttpAsOfDailyPolicySource>());
    }

    /// <summary>構成された (b) の源（構成の無い源は入れない）。</summary>
    public static IReadOnlyList<IAsOfReferenceFeed> Feeds(
        IConfiguration configuration, IHttpClientFactory factory, TimeProvider time, ILoggerFactory loggers)
    {
        var section = configuration.GetSection(SectionName);
        var feeds = new List<IAsOfReferenceFeed>(3);

        if (section["Finnhub:ApiKey"] is { Length: > 0 } finnhubKey && !string.IsNullOrWhiteSpace(finnhubKey))
        {
            var perMinute = ParsePositiveInt(section["Finnhub:RequestsPerMinute"], DefaultFinnhubRequestsPerMinute);
            feeds.Add(new FinnhubAsOfCompanyNewsFeed(
                factory.CreateClient(ExternalHttpClientName),
                finnhubKey.Trim(),
                new DelayingRateLimiter(new TokenBucket(perMinute, TimeSpan.FromMinutes(1)), time),
                time,
                loggers.CreateLogger<FinnhubAsOfCompanyNewsFeed>()));
        }

        var ciks = section.GetSection("SecEdgar:Ciks").Get<string[]>() ?? [];
        if (section["SecEdgar:UserAgent"] is { } userAgent && !string.IsNullOrWhiteSpace(userAgent)
            && ciks.Any(c => SecEdgarAsOfFilingFeed.NormalizeCik(c) is not null))
        {
            feeds.Add(new SecEdgarAsOfFilingFeed(
                factory.CreateClient(ExternalHttpClientName), userAgent.Trim(), ciks, loggers.CreateLogger<SecEdgarAsOfFilingFeed>()));
        }

        var series = section.GetSection("Fred:SeriesIds").Get<string[]>() ?? [];
        if (section["Fred:ApiKey"] is { } fredKey && !string.IsNullOrWhiteSpace(fredKey) && series.Any(s => !string.IsNullOrWhiteSpace(s)))
        {
            feeds.Add(new FredAsOfObservationFeed(
                factory.CreateClient(ExternalHttpClientName), fredKey.Trim(), series, loggers.CreateLogger<FredAsOfObservationFeed>()));
        }

        return feeds;
    }

    private static bool IsAbsolute(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : fallback;
}
