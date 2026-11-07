using System.Collections.Concurrent;
using System.Globalization;
using AiStockTrading.Shared.Contracts.Observability;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;

/// <summary>NFR, IADR-0627 決定 3: 確かめの結果。</summary>
public enum MessageOriginCheck
{
    /// <summary>発行元が許され、印が合い、時刻が窓の中で、初めて届いた。</summary>
    Valid = 0,

    /// <summary>印（または発行元・ID・時刻のどれか）が無い。</summary>
    Missing = 1,

    /// <summary>名乗った発行元は、この型を発行してよいサービスではない。</summary>
    ForbiddenIssuer = 2,

    /// <summary>印が合わない（鍵が違う・本文が違う・形が違う）。</summary>
    Mismatch = 3,

    /// <summary>発行の時刻が古すぎる。</summary>
    Stale = 4,

    /// <summary>発行の時刻が受け手の時計より先にありすぎる。</summary>
    Future = 5,

    /// <summary>同じメッセージ ID を既に処理し終えている（再送）。</summary>
    Replayed = 6,

    /// <summary>受け手にその発行元の鍵が無く、確かめられない（段 1 だけで起こる）。</summary>
    Unverifiable = 7,
}

/// <summary>
/// 🔴 NFR, FR-10, IADR-0627 決定 3・4: 受け手で、表（<see cref="MessageOriginCatalog"/>）の型の発行元を確かめる。
/// <list type="bullet">
/// <item>発行元の許可（名乗った発行元がその型を発行してよいサービスか）と、その発行元の鍵による印の両方を確かめる。</item>
/// <item>時刻の窓（古すぎ・先にありすぎ）と、処理し終えた ID の再到達（再送）を拒む。ID は処理が成功したときにだけ記録する
///   （<see cref="MarkHandled"/>。失敗からの再試行は再送に数えない。同じ配信〔同じ封筒〕の再試行も数えない）。</item>
/// <item>段 1（Report）: 合わないものは構造化した Warning とメトリクスに残して<b>通す</b>。段 2（Enforce）: <b>処理しない</b>。</item>
/// </list>
/// 鍵の値も印の値もログに出さない。
/// </summary>
public sealed class MessageOriginVerifier
{
    /// <summary>段 2 で拒否したメッセージの記録（監査の記録。構造化した Warning）。</summary>
    public static readonly EventId RejectedEvent = new(16260, "MessageOriginRejected");

    /// <summary>段 1 で確かめられなかったメッセージの記録。</summary>
    public static readonly EventId UnverifiedEvent = new(16261, "MessageOriginUnverified");

    private readonly MessageOriginMode _mode;
    private readonly MessageOriginSignature _signature;
    private readonly TimeSpan _maxAge;
    private readonly TimeSpan _maxFutureSkew;
    private readonly TimeProvider _time;
    private readonly ILogger<MessageOriginVerifier> _logger;
    private readonly BusinessMetrics? _metrics;
    private readonly ConcurrentDictionary<Guid, HandledEntry> _handled = new();
    private long _nextPruneTicks;

    public MessageOriginVerifier(
        MessageOriginMode mode,
        MessageOriginSignature signature,
        TimeSpan maxAge,
        TimeSpan maxFutureSkew,
        TimeProvider time,
        ILogger<MessageOriginVerifier> logger,
        BusinessMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _mode = mode;
        _signature = signature;
        _maxAge = maxAge;
        _maxFutureSkew = maxFutureSkew;
        _time = time;
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>段。</summary>
    public MessageOriginMode Mode => _mode;

    /// <summary>確かめる（記録も段の判断もしない。試験と <see cref="Admit"/> が使う）。</summary>
    public MessageOriginCheck Check(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var message = envelope.Message;
        if (message is null)
            return MessageOriginCheck.Mismatch;

        var rule = MessageOriginCatalog.Find(message.GetType());
        if (rule is null)
            return MessageOriginCheck.Valid; // 表の外の型は対象外（middleware は表の型にだけ付く）

        var headers = envelope.Headers;
        headers.TryGetValue(MessageOriginSignature.IssuerHeader, out var issuer);
        headers.TryGetValue(MessageOriginSignature.IdHeader, out var idText);
        headers.TryGetValue(MessageOriginSignature.TimeHeader, out var timeText);
        headers.TryGetValue(MessageOriginSignature.SignatureHeader, out var signature);
        if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(idText) || string.IsNullOrEmpty(timeText)
            || string.IsNullOrEmpty(signature))
            return MessageOriginCheck.Missing;

        if (!rule.Issuers.Contains(issuer, StringComparer.Ordinal))
            return MessageOriginCheck.ForbiddenIssuer;

        if (!Guid.TryParseExact(idText, "D", out var id)
            || !long.TryParse(timeText, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            return MessageOriginCheck.Mismatch;

        if (!_signature.HasKey(issuer))
            return MessageOriginCheck.Unverifiable;

        if (!_signature.Matches(rule.MessageType, id, issuer, ticks, message, signature))
            return MessageOriginCheck.Mismatch;

        var now = _time.GetUtcNow().UtcTicks;
        if (ticks < now - _maxAge.Ticks)
            return MessageOriginCheck.Stale;
        if (ticks > now + _maxFutureSkew.Ticks)
            return MessageOriginCheck.Future;

        // 同じ配信の中の再試行（後処理が処理の記録の後に失敗した場合）は再送に数えない。判定は「記録したときと同じ封筒の
        // 実体」かつ「2 回目以降の試行」の両方（Wolverine はプロセス内の呼び出しで封筒の実体を使い回すので、実体だけでは足りない。
        // 試行の回数は見出しから読まれ得るので、回数だけでも足りない）。ブローカから届き直したもの（再送）は別の実体になる。
        return _handled.TryGetValue(id, out var handled)
            && !(envelope.Attempts > 1 && handled.Delivery.TryGetTarget(out var delivered) && ReferenceEquals(delivered, envelope))
            ? MessageOriginCheck.Replayed
            : MessageOriginCheck.Valid;
    }

    /// <summary>処理へ進めてよいか。段 1 では常に true（記録だけ）。</summary>
    public bool Admit(Envelope envelope)
    {
        var result = Check(envelope);
        if (result == MessageOriginCheck.Valid)
            return true;

        var type = envelope.Message?.GetType().Name ?? "-";
        envelope.Headers.TryGetValue(MessageOriginSignature.IssuerHeader, out var issuer);
        var rejected = _mode == MessageOriginMode.Enforce;
        _metrics?.RecordMessageOriginCheck(type, ResultName(result), rejected);

        if (rejected)
        {
            // 🔴 IADR-0627 決定 4: 拒否は構造化した Warning（監査の記録）で残す。項目名は固定（検索の鍵）。
            _logger.LogWarning(
                RejectedEvent,
                "メッセージの発行元を確かめられないため処理しません（段 2）: 型={MessageType} 名乗った発行元={Issuer} メッセージID={EnvelopeId} 送り元={Source} 結果={Result}",
                type, Truncate(issuer), envelope.Id, Truncate(envelope.Source), result);
            return false;
        }

        _logger.LogWarning(
            UnverifiedEvent,
            "メッセージの発行元を確かめられませんでした（段 1・記録だけで処理は続けます）: 型={MessageType} 名乗った発行元={Issuer} メッセージID={EnvelopeId} 送り元={Source} 結果={Result}",
            type, Truncate(issuer), envelope.Id, Truncate(envelope.Source), result);
        return true;
    }

    /// <summary>処理が成功したメッセージの ID を記録する（以後の再到達は再送として拒む）。</summary>
    public void MarkHandled(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!envelope.Headers.TryGetValue(MessageOriginSignature.IdHeader, out var idText)
            || !Guid.TryParseExact(idText, "D", out var id))
            return;

        var now = _time.GetUtcNow().UtcTicks;
        _handled[id] = new HandledEntry(now + _maxAge.Ticks + _maxFutureSkew.Ticks, new WeakReference<Envelope>(envelope));
        Prune(now);
    }

    // 窓を過ぎた記録を捨てる（窓の外の ID は時刻の検査が拒むので、記録が無くても再送は通らない）。
    private void Prune(long now)
    {
        if (now < Interlocked.Read(ref _nextPruneTicks))
            return;
        Interlocked.Exchange(ref _nextPruneTicks, now + TimeSpan.FromMinutes(10).Ticks);
        foreach (var (id, entry) in _handled)
        {
            if (entry.Expires < now)
                _handled.TryRemove(id, out _);
        }
    }

    internal static string ResultName(MessageOriginCheck result) => result switch
    {
        MessageOriginCheck.Valid => "valid",
        MessageOriginCheck.Missing => "missing",
        MessageOriginCheck.ForbiddenIssuer => "forbidden_issuer",
        MessageOriginCheck.Mismatch => "mismatch",
        MessageOriginCheck.Stale => "stale",
        MessageOriginCheck.Future => "future",
        MessageOriginCheck.Replayed => "replayed",
        MessageOriginCheck.Unverifiable => "unverifiable",
        _ => "unknown",
    };

    private static string? Truncate(string? value) => value is { Length: > 64 } ? value[..64] : value;

    private sealed record HandledEntry(long Expires, WeakReference<Envelope> Delivery);
}
