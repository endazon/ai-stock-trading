using System.Globalization;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReportService.Features.Reports;
using Proto = AiStockTrading.Shared.Grpc.Audit.V1;

namespace ReportService.Infrastructure.ExternalServices;

// NFR, FR-06, FR-11, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 3）, IADR-0328, IADR-0331, IADR-0352, IADR-0427,
// IADR-0445 決定 3〜5, #1059 (#753):
// 監査台帳の読み取り（`aistocktrading.audit.v1.AuditEventsRead`）を呼ぶ**本サービスの**輸送。
// 構成 `Audit:Grpc`（宛先）を宣言したときだけ DI に載り、載っていれば監査台帳の 6 つの供給元が `Grpc*` 実装を選ぶ
// （**既定は REST**。宣言しなければ本ファイルは何も登録しない＝振る舞いは 1 つも変わらない）。
//
// 🔴 **REST の `audit-ledger` が持つ依存先の門と観測（#840 / IADR-0352 決定 1・2）を gRPC でも同じに行う**
// （段 2 の `RiskManagementGrpcTransport` と同じ規則を `ReportGrpcCalls` で共有する。依存先の名前は REST と同じ `audit-ledger`）。
// deadline の既定は REST の `audit-ledger` の HttpClient.Timeout と同値（10 秒）。再試行の既定は 1 試行。
//
// 🔴 **チャネルは本型が所有する（`GrpcChannel` を DI へ裸で登録しない。IADR-0427 決定 4）。**
public sealed class AuditGrpcTransport : IDisposable
{
    /// <summary>gRPC の宛先（例 <c>http://audit-service:8081</c>）。**未設定なら REST**。</summary>
    internal const string AddressKey = "Audit:Grpc";

    /// <summary>試行ごとの deadline（秒）。未設定は REST の HttpClient.Timeout と同値。</summary>
    internal const string TimeoutKey = "Audit:GrpcTimeoutSeconds";

    /// <summary>試行回数。未設定は 1（＝再試行しない）。</summary>
    internal const string MaxAttemptsKey = "Audit:GrpcMaxAttempts";

    /// <summary>観測に載せる依存先の名前（REST の名前付き HttpClient と同じ）。</summary>
    internal const string Dependency = "audit-ledger";

    /// <summary>REST の "audit-ledger" HttpClient.Timeout と同値。</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly GrpcChannel _channel;
    private readonly ReportGrpcCalls _calls;
    private readonly Proto.AuditEventsRead.AuditEventsReadClient _client;

    public AuditGrpcTransport(
        GrpcChannel channel,
        TimeSpan timeout,
        int maxAttempts,
        ReportDependencyProbe probe,
        IServiceAccessTokenProvider? tokenProvider,
        ILogger<AuditGrpcTransport> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _calls = new ReportGrpcCalls(Dependency, timeout, maxAttempts, probe, tokenProvider, logger);
        _client = new Proto.AuditEventsRead.AuditEventsReadClient(channel);
    }

    internal TimeSpan Timeout => _calls.Timeout;

    internal int MaxAttempts => _calls.MaxAttempts;

    /// <summary>
    /// 期間 <paramref name="window"/>（半開区間）の <paramref name="types"/> の記録を引き、<paramref name="build"/>（REST と共有する
    /// 各供給元の解釈）で組み立てる。**照会できなければ・応答が契約と食い違えば <c>null</c>（未供給）**。呼び出し元自身のキャンセルは伝播させる。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>原則 A</b>（IADR-0445 決定 3）: id・種別・本文のどれかが欠けた記録、読めない id は既定値（空の GUID・空文字）で作らず、
    /// <b>応答全体を未供給</b>にする。空の応答（記録 0 件）は未供給ではなく「事象なし」として <paramref name="build"/> へ渡す。
    /// </remarks>
    internal async Task<T?> ReadAsync<T>(
        string subject,
        (DateTimeOffset From, DateTimeOffset To) window,
        IReadOnlyList<string> types,
        Func<IReadOnlyList<AuditLedgerEntry>, T> build,
        ILogger logger,
        CancellationToken cancellationToken)
        where T : class
    {
        var request = new Proto.GetEventsByTypeRequest
        {
            // REST と同じ往復書式（`o`）。提供側は REST のクエリの束縛と同じく瞬間として読む。
            From = window.From.ToString("o", CultureInfo.InvariantCulture),
            To = window.To.ToString("o", CultureInfo.InvariantCulture),
        };
        request.EventTypes.AddRange(types);

        var response = await _calls.CallAsync(
            subject,
            "**未供給として扱います**。",
            options => _client.GetEventsByTypeAsync(request, options),
            cancellationToken).ConfigureAwait(false);
        if (response is null)
            return null;

        var entries = new List<AuditLedgerEntry>(response.Records.Count);
        foreach (var record in response.Records)
        {
            if (ToEntry(record) is not { } entry)
            {
                logger.LogError(
                    "{Subject}の gRPC 応答に id・種別・本文の欠けた（または記録時刻の読めない）記録がありました（{Records} 件中）。"
                        + "送り手との契約の食い違いとみなし、**未供給として扱います**。",
                    subject, response.Records.Count);
                return null;
            }

            entries.Add(entry);
        }

        try
        {
            return build(entries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // REST のアダプタは応答の組み立て中の例外を外側の catch で未供給へ倒している。同じ向きにする。
            logger.LogWarning(ex, "{Subject}の gRPC 応答を組み立てられませんでした。**未供給として扱います**。", subject);
            return null;
        }
    }

    /// <summary>線上の記録 → REST と同じ受け皿。欠落・読めない id は <c>null</c>（原則 A）。</summary>
    /// <remarks>
    /// FR-06, IADR-0516（2026-10-08 追記）, #1255: 記録時刻 <c>occurred_at</c> は<b>無ければ時刻なし</b>（旧版の提供側。従来どおり照会の範囲で数える）、
    /// <b>在るのに読めなければ</b> id と同じく契約の食い違い（<c>null</c>＝応答全体を未供給）。
    /// </remarks>
    internal static AuditLedgerEntry? ToEntry(Proto.LedgerRecord record)
    {
        if (!(record.HasId && Guid.TryParseExact(record.Id, "D", out var id)
            && record.HasEventType && !string.IsNullOrEmpty(record.EventType)
            && record.HasDetail))
        {
            return null;
        }

        if (!record.HasOccurredAt)
            return new AuditLedgerEntry(id, record.EventType, record.Detail);

        return DateTimeOffset.TryParseExact(
            record.OccurredAt, "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out var occurredAt)
            ? new AuditLedgerEntry(id, record.EventType, record.Detail, occurredAt)
            : null;
    }

    public void Dispose() => _channel.Dispose();
}

// NFR, IADR-0445 決定 5: 構成から輸送を組む登録点（段 2 の RiskManagementGrpcExtensions と同じ規則）。
public static class AuditGrpcExtensions
{
    /// <summary>
    /// <c>Audit:Grpc</c> が宣言されていれば輸送を singleton で登録する。宣言が無ければ**何もしない**（既定は REST）。
    /// </summary>
    public static IServiceCollection AddAiStockTradingAuditGrpc(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var address = ReportGrpcCalls.ResolveAddress(config, AuditGrpcTransport.AddressKey);
        if (address is null)
            return services;

        var timeout = ReportGrpcCalls.ReadTimeout(config, AuditGrpcTransport.TimeoutKey, AuditGrpcTransport.DefaultTimeout);
        var maxAttempts = ReportGrpcCalls.ReadMaxAttempts(config, AuditGrpcTransport.MaxAttemptsKey);
        services.AddSingleton(sp =>
        {
            // REST の audit-ledger（AddReportDependencyGate ＋ AddAiStockTradingServiceToken）と同じ AST レルムの供給元。
            var tokenProvider = sp.GetService<IServiceAccessTokenProvider>();
            return new AuditGrpcTransport(
                GrpcClientExtensions.CreateAiStockTradingChannel(
                    address.AbsoluteUri, tokenProvider ?? NoServiceAccessTokenProvider.Instance),
                timeout,
                maxAttempts,
                sp.GetRequiredService<ReportDependencyProbe>(),
                tokenProvider,
                sp.GetRequiredService<ILogger<AuditGrpcTransport>>());
        });
        return services;
    }
}
