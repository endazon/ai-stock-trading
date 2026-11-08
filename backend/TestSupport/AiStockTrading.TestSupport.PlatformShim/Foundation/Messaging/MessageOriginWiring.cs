using System.Globalization;
using AiStockTrading.Shared.Contracts.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;

/// <summary>
/// 🔴 NFR, FR-10, IADR-0627 決定 2, IADR-0633 決定 2・4: 送る側。表の型を、このサービスが発行元として許されているときだけ署名つきで送る。
/// 送り先のすべて（RabbitMQ の exchange・永続の outbox を経るものも）に掛かる（<c>Policies.AllSenders</c>）。
/// 秘密の鍵があれば新しい署名（<see cref="MessageOriginSignature.SignatureHeader"/>）を、段 1 の移行の間に旧い共有の鍵があれば
/// 旧い印（<see cref="MessageOriginLegacyHmac.SignatureHeader"/>）も付ける（旧い受け手が読めるように）。鍵が無ければ何も付けない。
/// </summary>
public sealed class MessageOriginSigningRule : IEnvelopeRule
{
    private readonly string _issuerId;
    private readonly MessageOriginSignature _signature;
    private readonly MessageOriginLegacyHmac _legacy;
    private readonly TimeProvider _time;

    public MessageOriginSigningRule(
        string issuerId, MessageOriginSignature signature, TimeProvider time, MessageOriginLegacyHmac? legacy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuerId);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(time);
        _issuerId = issuerId;
        _signature = signature;
        _legacy = legacy ?? MessageOriginLegacyHmac.None;
        _time = time;
    }

    public void Modify(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Message is not { } message)
            return;

        var rule = MessageOriginCatalog.Find(message.GetType());
        if (rule is null || !rule.Issuers.Contains(_issuerId, StringComparer.Ordinal))
            return;

        var id = Guid.NewGuid();
        var ticks = _time.GetUtcNow().UtcTicks;
        var signature = _signature.Sign(rule.MessageType, id, _issuerId, ticks, message);
        var legacy = _legacy.Sign(rule.MessageType, id, _issuerId, ticks, message);
        if (signature is null && legacy is null)
            return;

        envelope.Headers[MessageOriginSignature.IssuerHeader] = _issuerId;
        envelope.Headers[MessageOriginSignature.IdHeader] = id.ToString("D");
        envelope.Headers[MessageOriginSignature.TimeHeader] = ticks.ToString(CultureInfo.InvariantCulture);
        if (signature is not null)
            envelope.Headers[MessageOriginSignature.SignatureHeader] = signature;
        if (legacy is not null)
            envelope.Headers[MessageOriginLegacyHmac.SignatureHeader] = legacy;
    }
}

/// <summary>
/// 🔴 NFR, FR-10, IADR-0627 決定 3: 受ける側。表の型のハンドラの前で確かめ、段 2 で合わなければ処理しない。
/// 処理が成功したら ID を記録する（再送の検出）。
/// 🔴 IADR-0645 決定 2: 再送の記録の置き場（受け手の DB）を読み書きするので非同期。置き場が読めなければ例外（処理しない・再試行）。
/// </summary>
public static class MessageOriginMiddleware
{
    public static async Task<HandlerContinuation> BeforeAsync(
        Envelope envelope, MessageOriginVerifier verifier, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return await verifier.AdmitAsync(envelope, cancellation).ConfigureAwait(false)
            ? HandlerContinuation.Continue
            : HandlerContinuation.Stop;
    }

    public static Task AfterAsync(Envelope envelope, MessageOriginVerifier verifier, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return verifier.MarkHandledAsync(envelope, cancellation).AsTask();
    }
}

/// <summary>NFR, FR-10, IADR-0627 決定 3・4: 配線。全サービスの Program.cs が <c>UseAiStockTradingRabbitMq</c> の直後に呼ぶ。</summary>
public static class MessageOriginWiringExtensions
{
    /// <summary>
    /// 構成 <c>MessageOrigin</c> を読み、発行元なら送る側の署名を、受け手なら表の型のハンドラに確かめを付ける。
    /// 🔴 段 2 で要る鍵（自分の秘密の鍵・確かめる発行元の公開の鍵）が無い、または旧い共有の鍵が構成されていれば、ここで例外（起動で止める）。
    /// 🔴 発行しないサービスに秘密の鍵があれば段に依らず止める（秘密を要らない Pod に置かない）。
    /// 🔴 ハンドラに付ける確かめ（生成コードの形）は段にも鍵にも依らない（稼働イメージの生成コードと一致させる）。
    /// </summary>
    public static WolverineOptions UseAiStockTradingMessageOrigin(
        this WolverineOptions options, string serviceName, IConfiguration configuration, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        var issuerId = MessageOriginCatalog.IssuerIdFor(serviceName);
        var section = configuration.GetSection(MessageOriginOptions.SectionName);
        var settings = section.Get<MessageOriginOptions>() ?? new MessageOriginOptions();
        var modeText = section["Mode"];
        if (!string.IsNullOrEmpty(modeText) && !Enum.IsDefined(settings.Mode))
            throw new InvalidOperationException($"MessageOrigin:Mode は Report か Enforce です（値: {modeText}）。");
        if (settings.MaxAge <= TimeSpan.Zero || settings.MaxFutureSkew < TimeSpan.Zero)
            throw new InvalidOperationException("MessageOrigin:MaxAge は正、MaxFutureSkew は 0 以上です。");
        if (settings.UnrecordedGrace < TimeSpan.Zero)
            throw new InvalidOperationException("MessageOrigin:UnrecordedGrace は 0 以上です。");
        var storeText = section["ReplayStore"];
        if (!string.IsNullOrEmpty(storeText) && !Enum.IsDefined(settings.ReplayStore))
            throw new InvalidOperationException($"MessageOrigin:ReplayStore は Memory か Database です（値: {storeText}）。");

        var issued = MessageOriginCatalog.IssuedBy(issuerId);
        var verified = MessageOriginCatalog.VerifiedBy(issuerId);
        if (issued.Count == 0 && !string.IsNullOrWhiteSpace(settings.SigningKey))
            throw new InvalidOperationException(
                $"MessageOrigin:SigningKey が構成されていますが、{serviceName} は表の型の発行元ではありません（秘密の鍵を置かない。値は表示しません）。");

        var signature = MessageOriginSignature.Create(
            Normalize(settings.VerifyKeys), Normalize(settings.PreviousVerifyKeys), issuerId, settings.SigningKey);
        var legacy = MessageOriginLegacyHmac.Create(Normalize(settings.Keys), Normalize(settings.PreviousKeys));
        if (settings.Mode == MessageOriginMode.Enforce && legacy.Any)
            throw new InvalidOperationException(
                $"MessageOrigin:Mode=Enforce では旧い共有の鍵（MessageOrigin:Keys・PreviousKeys）を受け付けません（{serviceName}。値は表示しません）。");

        if (legacy.Any && MessageOriginCatalog.IsRecordOnly(issuerId))
            throw new InvalidOperationException(
                $"記録だけの受け手（{serviceName}）には旧い共有の鍵（MessageOrigin:Keys）を渡しません（共有の鍵から全発行元を名乗れるため。値は表示しません）。");

        var clock = time ?? TimeProvider.System;
        // 起動の時刻（配線の時点）。受け手の検証器は最初のメッセージで作られるので、そこで時刻を取ると起動より遅れる。
        var startedAt = clock.GetUtcNow();
        var missing = new List<string>();
        if (issued.Count > 0 && !signature.CanSign(issuerId))
            missing.Add("MessageOrigin:SigningKey");
        missing.AddRange(RequiredVerifyKeys(issuerId).Where(k => !signature.HasVerifyKey(k)).Select(k => $"MessageOrigin:VerifyKeys:{k}"));
        if (missing.Count > 0 && settings.Mode == MessageOriginMode.Enforce)
        {
            throw new InvalidOperationException(
                $"MessageOrigin:Mode=Enforce には鍵 {{{string.Join(",", missing)}}}（Secret）が要ります（{serviceName}）。");
        }

        if (issued.Count > 0)
        {
            var rule = new MessageOriginSigningRule(issuerId, signature, clock, legacy);
            options.Policies.AllSenders(s => s.CustomizeOutgoing(rule.Modify));
        }

        if (verified.Count > 0)
        {
            var types = verified.Select(r => r.MessageType).ToHashSet();
            var recordOnly = MessageOriginCatalog.IsRecordOnly(issuerId);
            // 🔴 IADR-0645 決定 2: 再送の記録の置き場。Database は受け手のサービスの DB（接続文字列が無ければ起動で止める）。
            //   Memory（既定）は起動より前に猶予を超えて発行されたものを Unrecorded とする（処理し終えたかを確かめられない）。
            PostgresMessageOriginReplayStore? store = null;
            if (settings.ReplayStore == MessageOriginReplayStoreKind.Database)
            {
                var connectionString = configuration.GetConnectionString("DefaultConnection");
                if (string.IsNullOrWhiteSpace(connectionString))
                    throw new InvalidOperationException(
                        $"MessageOrigin:ReplayStore=Database には ConnectionStrings:DefaultConnection が要ります（{serviceName}。値は表示しません）。");
                store = new PostgresMessageOriginReplayStore(connectionString);
            }

            options.Services.AddSingleton(sp => new MessageOriginVerifier(
                settings.Mode, signature, settings.MaxAge, settings.MaxFutureSkew, clock,
                sp.GetRequiredService<ILogger<MessageOriginVerifier>>(), sp.GetService<BusinessMetrics>(), legacy, recordOnly,
                store, store is null ? startedAt - settings.UnrecordedGrace : null));
            options.Policies.AddMiddleware(typeof(MessageOriginMiddleware), chain => types.Contains(chain.MessageType));
        }

        if (missing.Count > 0)
            options.Services.AddHostedService(sp => new MessageOriginMissingKeyReport(
                serviceName, missing, sp.GetRequiredService<ILogger<MessageOriginMissingKeyReport>>()));

        return options;
    }

    // env の名前はシェルの識別子に限る（chart は発行元の識別子の - を _ で書く）。構成のキーの _ を - に戻す。
    private static Dictionary<string, string?> Normalize(Dictionary<string, string?> source)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (k, v) in source)
            result[k.Replace('_', '-')] = v;
        return result;
    }

    /// <summary>このサービスが自分の秘密の鍵で署名するか（表の型の発行元か）。</summary>
    public static bool RequiresSigningKey(string issuerId) => MessageOriginCatalog.IssuedBy(issuerId).Count > 0;

    /// <summary>このサービスに要る公開の鍵（確かめる型の発行元。記録だけの受け手は表の発行元すべて）。</summary>
    public static IReadOnlyList<string> RequiredVerifyKeys(string issuerId)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var rule in MessageOriginCatalog.VerifiedBy(issuerId))
            keys.UnionWith(rule.Issuers);
        return [.. keys];
    }
}

/// <summary>NFR, IADR-0627 決定 4・IADR-0633: 段 1 で鍵が欠けていることを起動時に 1 回だけ残す（メッセージごとには出さない）。</summary>
public sealed class MessageOriginMissingKeyReport(
    string serviceName, IReadOnlyList<string> missing, ILogger<MessageOriginMissingKeyReport> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "メッセージの発行元の鍵が構成されていません（段 1。印を付けない・確かめられないまま処理します。段 2 では起動しません）: サービス={Service} 欠けている鍵={Missing}",
            serviceName, string.Join(",", missing));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
