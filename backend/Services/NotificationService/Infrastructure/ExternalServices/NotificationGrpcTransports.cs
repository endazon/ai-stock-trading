using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitorProto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace NotificationService.Infrastructure.ExternalServices;

// NFR, FR-14, MSP:ADR-0029, MSP:ADR-0075, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）, IADR-0328, IADR-0449 決定 4, IADR-0450 決定 4, #753:
// Discord ボットの east-west gRPC の**輸送** 3 つ（リスク管理・報告書・市場監視）。構成 `<提供側>:Grpc`（宛先）を宣言したときだけ
// DI に載り、載っていれば各ポートの工場が `Grpc*` 実装を選ぶ（**既定は REST**）。宣言すれば、そのポートの読み取りと書き込みの**両方**が gRPC になる
// （段 5 の後半で書き込みも移した。REST へ黙って落とさない）。
//
// 🔴 **資格情報はボットの owner マップ機密クライアントのトークン**（`DiscordOwnerGrpcCredentials`。REST の `AddDiscordOwnerToken` と同じ構成）。
// ボットのトークンはサービスの身元であり（ADR-0047 決定 1）、手引き §4 の「呼び出し側サービス自身の JWT」にあたる（決定 2）。
// s2s（trading-service）のトークンへは替えない。提供側の門（`GrpcOwnerOrService` / `GrpcOwnerOnly`）は azp がボットであることを確かめる（IADR-0448 / 0449）。
// 🔴 **チャネルは各輸送が所有する**（`GrpcChannel` を DI へ裸で登録しない。IADR-0427 決定 4）。
public sealed class RiskManagementGrpcTransport : IDisposable
{
    /// <summary>gRPC の宛先（例 <c>http://risk-management-service:8081</c>）。**未設定なら REST**。</summary>
    internal const string AddressKey = "RiskManagement:Grpc";

    /// <summary>
    /// 試行ごとの deadline（秒）。未設定は REST の "risk-kill-switch" / "risk-pause" / "risk-stage-gate" / "risk-good-faith-violations" /
    /// "risk-position-drift" の HttpClient.Timeout と同値（読み取りと書き込みで共通）。
    /// </summary>
    internal const string TimeoutKey = "RiskManagement:GrpcTimeoutSeconds";

    /// <summary>試行回数（**読み取りだけ**に効く。書き込みは再試行しない＝IADR-0450 決定 4）。未設定は 1（＝再試行しない）。</summary>
    internal const string MaxAttemptsKey = "RiskManagement:GrpcMaxAttempts";

    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly GrpcChannel _channel;

    public RiskManagementGrpcTransport(
        GrpcChannel channel, TimeSpan timeout, int maxAttempts, ILogger<RiskManagementGrpcTransport> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        Calls = new NotificationGrpcCalls(timeout, maxAttempts, logger ?? throw new ArgumentNullException(nameof(logger)));
        Read = new RiskProto.RiskControlsRead.RiskControlsReadClient(channel);
        OwnerRead = new RiskProto.RiskControlsOwnerRead.RiskControlsOwnerReadClient(channel);
        OwnerWrite = new RiskProto.RiskControlsOwnerWrite.RiskControlsOwnerWriteClient(channel);
    }

    internal NotificationGrpcCalls Calls { get; }

    /// <summary>段階ゲートの現況（`GrpcOwnerOrService` の面）。</summary>
    internal RiskProto.RiskControlsRead.RiskControlsReadClient Read { get; }

    /// <summary>稼働状態（`GrpcOwnerOnly` の面）。</summary>
    internal RiskProto.RiskControlsOwnerRead.RiskControlsOwnerReadClient OwnerRead { get; }

    /// <summary>kill switch・一時停止/再開・GFV 解除・段階遷移・撤退評価・乖離の取り込み（`GrpcOwnerOnly` の面）。</summary>
    internal RiskProto.RiskControlsOwnerWrite.RiskControlsOwnerWriteClient OwnerWrite { get; }

    public void Dispose() => _channel.Dispose();
}

public sealed class ReportsGrpcTransport : IDisposable
{
    /// <summary>gRPC の宛先（例 <c>http://report-service:8081</c>）。**未設定なら REST**。</summary>
    internal const string AddressKey = "Reports:Grpc";

    /// <summary>試行ごとの deadline（秒）。未設定は REST の "report-review" の HttpClient.Timeout と同値（照会・確定・差し戻し）。</summary>
    internal const string TimeoutKey = "Reports:GrpcTimeoutSeconds";

    /// <summary>
    /// 方針の改訂と入れ替えの適用の内訳の記録の deadline（秒）。未設定は REST の "report-policy-revision" の HttpClient.Timeout（120 秒）と同値
    /// （LLM の所要時間を見込む。REST では 2 つの書き込みがこのクライアントを共用している）。
    /// </summary>
    internal const string PolicyRevisionTimeoutKey = "Reports:GrpcPolicyRevisionTimeoutSeconds";

    /// <summary>
    /// FR-06, 計画 ADR-0052, #1156, IADR-0491 決定 1: 報告書の作り直しの deadline（秒）。未設定は REST の "report-regeneration" の
    /// HttpClient.Timeout（300 秒）と同値（期間の入力の取得と散文の LLM を待つ）。
    /// </summary>
    internal const string RegenerationTimeoutKey = "Reports:GrpcRegenerationTimeoutSeconds";

    /// <summary>試行回数（**読み取りだけ**に効く。書き込みは再試行しない）。</summary>
    internal const string MaxAttemptsKey = "Reports:GrpcMaxAttempts";

    // 🔴 入れ替え案の照会は REST では方針の改訂用の 120 秒のクライアントを共用していたが、照会そのものは台帳の読み取りであり
    // LLM を待たない。gRPC ではレビューの照会と同じ 5 秒にする（IADR-0449 決定 4）。
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    // FR-14, #243, IADR-0522 の 2026-10-10 追記: 90 → 120 秒。報告書サービスの方針の改訂の LLM 上限（既定 95 秒）＋建玉の照会（10 秒）
    // より長く置く（外側＞内側。内側が先に切れて「案なし」を確定的に返す）。REST の名前付き HttpClient も同じ値を使う。
    internal static readonly TimeSpan DefaultPolicyRevisionTimeout = TimeSpan.FromSeconds(120);

    internal static readonly TimeSpan DefaultRegenerationTimeout = TimeSpan.FromSeconds(300);

    private readonly GrpcChannel _channel;

    public ReportsGrpcTransport(
        GrpcChannel channel, TimeSpan timeout, int maxAttempts, ILogger<ReportsGrpcTransport> logger, TimeSpan? policyRevisionTimeout = null,
        TimeSpan? regenerationTimeout = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        ArgumentNullException.ThrowIfNull(logger);
        Calls = new NotificationGrpcCalls(timeout, maxAttempts, logger);
        PolicyRevisionCalls = new NotificationGrpcCalls(policyRevisionTimeout ?? DefaultPolicyRevisionTimeout, 1, logger);
        RegenerationCalls = new NotificationGrpcCalls(regenerationTimeout ?? DefaultRegenerationTimeout, 1, logger);
        OwnerRead = new ReportProto.ReportOwnerRead.ReportOwnerReadClient(channel);
        OwnerWrite = new ReportProto.ReportOwnerWrite.ReportOwnerWriteClient(channel);
    }

    /// <summary>照会・確定・差し戻しの規則（deadline は <see cref="TimeoutKey"/>）。</summary>
    internal NotificationGrpcCalls Calls { get; }

    /// <summary>方針の改訂・適用の内訳の記録の規則（deadline は <see cref="PolicyRevisionTimeoutKey"/>。書き込みだけなので再試行しない）。</summary>
    internal NotificationGrpcCalls PolicyRevisionCalls { get; }

    /// <summary>報告書の作り直しの規則（deadline は <see cref="RegenerationTimeoutKey"/>。冪等でないので再試行しない）。</summary>
    internal NotificationGrpcCalls RegenerationCalls { get; }

    internal ReportProto.ReportOwnerRead.ReportOwnerReadClient OwnerRead { get; }

    internal ReportProto.ReportOwnerWrite.ReportOwnerWriteClient OwnerWrite { get; }

    public void Dispose() => _channel.Dispose();
}

public sealed class MarketMonitorGrpcTransport : IDisposable
{
    /// <summary>gRPC の宛先（例 <c>http://market-monitor-service:8081</c>）。**未設定なら REST**。</summary>
    internal const string AddressKey = "MarketMonitor:Grpc";

    /// <summary>試行ごとの deadline（秒）。未設定は REST の監視銘柄の名前付き HttpClient の Timeout と同値（照会・適用で共通）。</summary>
    internal const string TimeoutKey = "MarketMonitor:GrpcTimeoutSeconds";

    /// <summary>試行回数（**読み取りだけ**に効く。書き込みは再試行しない）。</summary>
    internal const string MaxAttemptsKey = "MarketMonitor:GrpcMaxAttempts";

    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly GrpcChannel _channel;

    public MarketMonitorGrpcTransport(GrpcChannel channel, TimeSpan timeout, int maxAttempts, ILogger<MarketMonitorGrpcTransport> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        Calls = new NotificationGrpcCalls(timeout, maxAttempts, logger ?? throw new ArgumentNullException(nameof(logger)));
        Read = new MonitorProto.WatchlistRead.WatchlistReadClient(channel);
        OwnerWrite = new MonitorProto.WatchlistOwnerWrite.WatchlistOwnerWriteClient(channel);
    }

    internal NotificationGrpcCalls Calls { get; }

    internal MonitorProto.WatchlistRead.WatchlistReadClient Read { get; }

    /// <summary>入れ替え案の適用（`GrpcOwnerOnly` の面）。</summary>
    internal MonitorProto.WatchlistOwnerWrite.WatchlistOwnerWriteClient OwnerWrite { get; }

    public void Dispose() => _channel.Dispose();
}

// NFR, IADR-0449 決定 4, IADR-0450 決定 4: 構成から輸送を組む登録点（段 2〜4 の登録点と同じ規則。宣言が無ければ**何もしない**＝既定は REST）。
public static class NotificationGrpcExtensions
{
    public static IServiceCollection AddNotificationGrpc(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var risk = NotificationGrpcCalls.ResolveAddress(config, RiskManagementGrpcTransport.AddressKey);
        var reports = NotificationGrpcCalls.ResolveAddress(config, ReportsGrpcTransport.AddressKey);
        var monitor = NotificationGrpcCalls.ResolveAddress(config, MarketMonitorGrpcTransport.AddressKey);
        if (risk is null && reports is null && monitor is null)
            return services;

        services.AddDiscordOwnerGrpcCredentials(config);

        if (risk is not null)
        {
            var timeout = NotificationGrpcCalls.ReadTimeout(config, RiskManagementGrpcTransport.TimeoutKey, RiskManagementGrpcTransport.DefaultTimeout);
            var attempts = NotificationGrpcCalls.ReadMaxAttempts(config, RiskManagementGrpcTransport.MaxAttemptsKey);
            services.AddSingleton(sp => new RiskManagementGrpcTransport(
                Channel(sp, risk), timeout, attempts, sp.GetRequiredService<ILogger<RiskManagementGrpcTransport>>()));
        }

        if (reports is not null)
        {
            var timeout = NotificationGrpcCalls.ReadTimeout(config, ReportsGrpcTransport.TimeoutKey, ReportsGrpcTransport.DefaultTimeout);
            var attempts = NotificationGrpcCalls.ReadMaxAttempts(config, ReportsGrpcTransport.MaxAttemptsKey);
            var revisionTimeout = NotificationGrpcCalls.ReadTimeout(
                config, ReportsGrpcTransport.PolicyRevisionTimeoutKey, ReportsGrpcTransport.DefaultPolicyRevisionTimeout);
            var regenerationTimeout = NotificationGrpcCalls.ReadTimeout(
                config, ReportsGrpcTransport.RegenerationTimeoutKey, ReportsGrpcTransport.DefaultRegenerationTimeout);
            services.AddSingleton(sp => new ReportsGrpcTransport(
                Channel(sp, reports), timeout, attempts, sp.GetRequiredService<ILogger<ReportsGrpcTransport>>(), revisionTimeout,
                regenerationTimeout));
        }

        if (monitor is not null)
        {
            var timeout = NotificationGrpcCalls.ReadTimeout(config, MarketMonitorGrpcTransport.TimeoutKey, MarketMonitorGrpcTransport.DefaultTimeout);
            var attempts = NotificationGrpcCalls.ReadMaxAttempts(config, MarketMonitorGrpcTransport.MaxAttemptsKey);
            services.AddSingleton(sp => new MarketMonitorGrpcTransport(
                Channel(sp, monitor), timeout, attempts, sp.GetRequiredService<ILogger<MarketMonitorGrpcTransport>>()));
        }

        return services;
    }

    // ボットの owner トークン（3 つの輸送で 1 つ）をメタデータに載せる平文 h2c のチャネル。
    private static GrpcChannel Channel(IServiceProvider sp, Uri address) =>
        GrpcClientExtensions.CreateAiStockTradingChannel(
            address.AbsoluteUri, sp.GetRequiredService<DiscordOwnerGrpcCredentials>().Provider);
}
