using System.Globalization;
using AiStockTrading.Shared.Contracts.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;

/// <summary>
/// 🔴 NFR, FR-10, IADR-0627 決定 2: 送る側。表の型を、このサービスが発行元として許されているときだけ印つきで送る。
/// 送り先のすべて（RabbitMQ の exchange・永続の outbox を経るものも）に掛かる（<c>Policies.AllSenders</c>）。
/// 鍵が無ければ印を付けない（段 1 の既定。段 2 では起動で止まるのでここに来ない）。
/// </summary>
public sealed class MessageOriginSigningRule : IEnvelopeRule
{
    private readonly string _issuerId;
    private readonly MessageOriginSignature _signature;
    private readonly TimeProvider _time;

    public MessageOriginSigningRule(string issuerId, MessageOriginSignature signature, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuerId);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(time);
        _issuerId = issuerId;
        _signature = signature;
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
        if (signature is null)
            return;

        envelope.Headers[MessageOriginSignature.IssuerHeader] = _issuerId;
        envelope.Headers[MessageOriginSignature.IdHeader] = id.ToString("D");
        envelope.Headers[MessageOriginSignature.TimeHeader] = ticks.ToString(CultureInfo.InvariantCulture);
        envelope.Headers[MessageOriginSignature.SignatureHeader] = signature;
    }
}

/// <summary>
/// 🔴 NFR, FR-10, IADR-0627 決定 3: 受ける側。表の型のハンドラの前で確かめ、段 2 で合わなければ処理しない。
/// 処理が成功したら ID を記録する（再送の検出）。
/// </summary>
public static class MessageOriginMiddleware
{
    public static HandlerContinuation Before(Envelope envelope, MessageOriginVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return verifier.Admit(envelope) ? HandlerContinuation.Continue : HandlerContinuation.Stop;
    }

    public static void After(Envelope envelope, MessageOriginVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        verifier.MarkHandled(envelope);
    }
}

/// <summary>NFR, FR-10, IADR-0627 決定 3・4: 配線。全サービスの Program.cs が <c>UseAiStockTradingRabbitMq</c> の直後に呼ぶ。</summary>
public static class MessageOriginWiringExtensions
{
    /// <summary>
    /// 構成 <c>MessageOrigin</c> を読み、発行元なら送る側の印を、受け手なら表の型のハンドラに確かめを付ける。
    /// 🔴 段 2 で要る鍵（自分の発行の鍵・確かめる型の発行元の鍵）が無ければ、ここで例外（起動で止める）。
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

        var signature = MessageOriginSignature.Create(Normalize(settings.Keys), Normalize(settings.PreviousKeys));
        var clock = time ?? TimeProvider.System;
        var issued = MessageOriginCatalog.IssuedBy(issuerId);
        var verified = MessageOriginCatalog.VerifiedBy(issuerId);
        var missing = RequiredKeys(issuerId).Where(k => !signature.HasKey(k)).ToArray();
        if (missing.Length > 0 && settings.Mode == MessageOriginMode.Enforce)
        {
            throw new InvalidOperationException(
                $"MessageOrigin:Mode=Enforce には鍵 MessageOrigin:Keys:{{{string.Join(",", missing)}}}（Secret）が要ります（{serviceName}）。");
        }

        if (issued.Count > 0)
        {
            var rule = new MessageOriginSigningRule(issuerId, signature, clock);
            options.Policies.AllSenders(s => s.CustomizeOutgoing(rule.Modify));
        }

        if (verified.Count > 0)
        {
            var types = verified.Select(r => r.MessageType).ToHashSet();
            options.Services.AddSingleton(sp => new MessageOriginVerifier(
                settings.Mode, signature, settings.MaxAge, settings.MaxFutureSkew, clock,
                sp.GetRequiredService<ILogger<MessageOriginVerifier>>(), sp.GetService<BusinessMetrics>()));
            options.Policies.AddMiddleware(typeof(MessageOriginMiddleware), chain => types.Contains(chain.MessageType));
        }

        if (missing.Length > 0)
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

    /// <summary>このサービスに要る鍵（自分が発行元なら自分の鍵・確かめる型の発行元の鍵）。</summary>
    public static IReadOnlyList<string> RequiredKeys(string issuerId)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        if (MessageOriginCatalog.IssuedBy(issuerId).Count > 0)
            keys.Add(issuerId);
        foreach (var rule in MessageOriginCatalog.VerifiedBy(issuerId))
            keys.UnionWith(rule.Issuers);
        return [.. keys];
    }
}

/// <summary>NFR, IADR-0627 決定 4: 段 1 で鍵が欠けていることを起動時に 1 回だけ残す（メッセージごとには出さない）。</summary>
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
