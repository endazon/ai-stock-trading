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

    /// <summary>
    /// IADR-0633 決定 4: 新しい署名が無く、旧い共有の鍵の HMAC の印だけが合う（移行の間）。段 1 は記録して通し、段 2 は処理しない。
    /// </summary>
    Legacy = 8,

    /// <summary>
    /// 🔴 IADR-0645 決定 2: 再送の記録の置き場が無い受け手で、起動より前に（猶予を超えて）発行された（処理し終えたかを確かめられない）。
    /// 段 1 は記録して通し、段 2 は処理しない。
    /// </summary>
    Unrecorded = 9,
}

/// <summary>
/// 🔴 NFR, FR-10, IADR-0627 決定 3・4, IADR-0633 決定 2・4・5: 受け手で、表（<see cref="MessageOriginCatalog"/>）の型の発行元を確かめる。
/// <list type="bullet">
/// <item>発行元の許可（名乗った発行元がその型を発行してよいサービスか）と、その発行元の<b>公開の鍵</b>による署名の両方を確かめる。
///   新しい署名（<see cref="MessageOriginSignature.SignatureHeader"/>）があればそれだけで判定し、旧い印へ倒さない。
///   新しい署名が無く旧い HMAC の印だけが合うものは <see cref="MessageOriginCheck.Legacy"/>（段 2 では処理しない）。</item>
/// <item>時刻の窓（古すぎ・先にありすぎ）と、処理し終えた ID の再到達（再送）を拒む。ID は処理が成功したときにだけ記録する
///   （<see cref="MarkHandled"/>。失敗からの再試行は再送に数えない。同じ配信〔同じ封筒〕の再試行も数えない）。</item>
/// <item>段 1（Report）: 合わないものは構造化した Warning とメトリクスに残して<b>通す</b>。段 2（Enforce）: <b>処理しない</b>。</item>
/// <item>記録だけの受け手（監査。<see cref="RecordOnly"/>）: 段に依らず、合わないものを記録して<b>通す</b>（証跡を欠かさない）。</item>
/// <item>🔴 IADR-0645 決定 2: 再送の記録は、置き場（<see cref="IMessageOriginReplayStore"/>。受け手の DB）があればそこにも残し、再起動の後も再送を拒む。
///   置き場が読めなければ例外で処理を止める（拒否として捨てない）。置き場が無い受け手は、起動より前に猶予を超えて発行されたものを
///   <see cref="MessageOriginCheck.Unrecorded"/> とする（処理し終えたかを確かめられない）。</item>
/// </list>
/// 鍵の値も印の値もログに出さない。
/// </summary>
public sealed class MessageOriginVerifier
{
    /// <summary>段 2 で拒否したメッセージの記録（監査の記録。構造化した Warning）。</summary>
    public static readonly EventId RejectedEvent = new(16260, "MessageOriginRejected");

    /// <summary>段 1 で確かめられなかったメッセージの記録。</summary>
    public static readonly EventId UnverifiedEvent = new(16261, "MessageOriginUnverified");

    /// <summary>IADR-0645 決定 2: 再送の記録の置き場の読み書きの失敗。</summary>
    public static readonly EventId ReplayStoreFailedEvent = new(16262, "MessageOriginReplayStoreFailed");

    private readonly MessageOriginMode _mode;
    private readonly MessageOriginSignature _signature;
    private readonly MessageOriginLegacyHmac _legacy;
    private readonly bool _recordOnly;
    private readonly TimeSpan _maxAge;
    private readonly TimeSpan _maxFutureSkew;
    private readonly TimeProvider _time;
    private readonly ILogger<MessageOriginVerifier> _logger;
    private readonly BusinessMetrics? _metrics;
    private readonly ConcurrentDictionary<Guid, HandledEntry> _handled = new();
    private readonly IMessageOriginReplayStore? _store;
    private readonly long? _unrecordedBeforeTicks;
    private long _nextPruneTicks;

    public MessageOriginVerifier(
        MessageOriginMode mode,
        MessageOriginSignature signature,
        TimeSpan maxAge,
        TimeSpan maxFutureSkew,
        TimeProvider time,
        ILogger<MessageOriginVerifier> logger,
        BusinessMetrics? metrics = null,
        MessageOriginLegacyHmac? legacy = null,
        bool recordOnly = false,
        IMessageOriginReplayStore? replayStore = null,
        DateTimeOffset? unrecordedBefore = null)
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
        _legacy = legacy ?? MessageOriginLegacyHmac.None;
        _recordOnly = recordOnly;
        _store = replayStore;
        _unrecordedBeforeTicks = unrecordedBefore?.UtcTicks;
    }

    /// <summary>段。</summary>
    public MessageOriginMode Mode => _mode;

    /// <summary>記録だけの受け手か（段に依らず拒まない）。</summary>
    public bool RecordOnly => _recordOnly;

    /// <summary>
    /// 再送の記録を受け手の DB に残すか（IADR-0645）。<c>unrecordedBefore</c>（置き場が無いとき）: これより前に発行されたものは
    /// 処理し終えたかを確かめられない（配線が「起動の時刻 − 猶予」を渡す）。
    /// </summary>
    public bool HasDurableReplayRecord => _store is not null;

    /// <summary>確かめる（記録も段の判断もしない。試験が使う。置き場があれば読みを待つ）。</summary>
    public MessageOriginCheck Check(Envelope envelope) =>
        CheckAsync(envelope, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <summary>確かめる（記録も段の判断もしない）。置き場が読めなければ <see cref="MessageOriginReplayStoreUnavailableException"/>。</summary>
    public async ValueTask<MessageOriginCheck> CheckAsync(Envelope envelope, CancellationToken cancellationToken)
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
        headers.TryGetValue(MessageOriginLegacyHmac.SignatureHeader, out var legacySignature);
        if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(idText) || string.IsNullOrEmpty(timeText)
            || (string.IsNullOrEmpty(signature) && string.IsNullOrEmpty(legacySignature)))
            return MessageOriginCheck.Missing;

        if (!rule.Issuers.Contains(issuer, StringComparer.Ordinal))
            return MessageOriginCheck.ForbiddenIssuer;

        if (!Guid.TryParseExact(idText, "D", out var id)
            || !long.TryParse(timeText, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            return MessageOriginCheck.Mismatch;

        var legacyOnly = false;
        if (!string.IsNullOrEmpty(signature))
        {
            // 新しい署名があれば、それだけで判定する（合わなければ旧い印へ倒さない）。
            if (!_signature.HasVerifyKey(issuer))
                return MessageOriginCheck.Unverifiable;
            if (!_signature.Matches(rule.MessageType, id, issuer, ticks, message, signature))
                return MessageOriginCheck.Mismatch;
        }
        else
        {
            if (!_legacy.HasKey(issuer))
                return MessageOriginCheck.Unverifiable;
            if (!_legacy.Matches(rule.MessageType, id, issuer, ticks, message, legacySignature!))
                return MessageOriginCheck.Mismatch;
            legacyOnly = true;
        }

        var now = _time.GetUtcNow().UtcTicks;
        if (ticks < now - _maxAge.Ticks)
            return MessageOriginCheck.Stale;
        if (ticks > now + _maxFutureSkew.Ticks)
            return MessageOriginCheck.Future;

        // 同じ配信の中の再試行（後処理が処理の記録の後に失敗した場合）は再送に数えない。判定は「記録したときと同じ封筒の
        // 実体」かつ「2 回目以降の試行」の両方（Wolverine はプロセス内の呼び出しで封筒の実体を使い回すので、実体だけでは足りない。
        // 試行の回数は見出しから読まれ得るので、回数だけでも足りない）。ブローカから届き直したもの（再送）は別の実体になる。
        if (_handled.TryGetValue(id, out var handled))
        {
            if (!(envelope.Attempts > 1 && handled.Delivery.TryGetTarget(out var delivered) && ReferenceEquals(delivered, envelope)))
                return MessageOriginCheck.Replayed;
        }
        else if (_store is not null)
        {
            // 🔴 IADR-0645 決定 2: プロセスの中に記録が無ければ置き場を見る（再起動の前・別のレプリカが処理し終えたもの）。
            bool seen;
            try
            {
                seen = await _store.ContainsAsync(id, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _metrics?.RecordMessageOriginReplayStoreFailure("lookup");
                _logger.LogError(
                    ReplayStoreFailedEvent,
                    "発行元の確かめの再送の記録の置き場を読めませんでした（処理せずに再試行します）: 型={MessageType} 例外の型={ExceptionType}",
                    message.GetType().Name, ex.GetType().Name);
                throw new MessageOriginReplayStoreUnavailableException(ex);
            }

            if (seen)
                return MessageOriginCheck.Replayed;
        }
        else if (_unrecordedBeforeTicks is { } before && ticks < before)
        {
            // 🔴 IADR-0645 決定 2: 置き場が無い受け手は、起動より前（猶予を超えて）に発行されたものを処理し終えたか確かめられない。
            return MessageOriginCheck.Unrecorded;
        }

        // 旧い印は、ほかの確かめをすべて通っても段 2 では受けない（段の判断は Admit）。
        return legacyOnly ? MessageOriginCheck.Legacy : MessageOriginCheck.Valid;
    }

    /// <summary>処理へ進めてよいか。段 1 では常に true（記録だけ）。試験が使う。</summary>
    public bool Admit(Envelope envelope) =>
        AdmitAsync(envelope, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <summary>処理へ進めてよいか。段 1 では常に true（記録だけ）。置き場が読めなければ例外（処理しない・再試行）。</summary>
    public async ValueTask<bool> AdmitAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var result = await CheckAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (result == MessageOriginCheck.Valid)
            return true;

        var type = envelope.Message?.GetType().Name ?? "-";
        envelope.Headers.TryGetValue(MessageOriginSignature.IssuerHeader, out var issuer);
        var rejected = _mode == MessageOriginMode.Enforce && !_recordOnly;
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
            "メッセージの発行元を確かめられませんでした（段 1 か記録だけの受け手のため、記録だけで処理は続けます）: 型={MessageType} 名乗った発行元={Issuer} メッセージID={EnvelopeId} 送り元={Source} 結果={Result}",
            type, Truncate(issuer), envelope.Id, Truncate(envelope.Source), result);
        return true;
    }

    /// <summary>処理が成功したメッセージの ID を記録する（以後の再到達は再送として拒む）。試験が使う。</summary>
    public void MarkHandled(Envelope envelope) =>
        MarkHandledAsync(envelope, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// 処理が成功したメッセージの ID を記録する（以後の再到達は再送として拒む）。置き場があればそこにも残す。
    /// 🔴 置き場への書きに失敗しても例外にしない（処理は終わっている。やり直すと副作用が重なる）。計器と Error に残し、プロセスの中の記録で続ける。
    /// </summary>
    public async ValueTask MarkHandledAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!envelope.Headers.TryGetValue(MessageOriginSignature.IdHeader, out var idText)
            || !Guid.TryParseExact(idText, "D", out var id))
            return;

        var nowAt = _time.GetUtcNow();
        var now = nowAt.UtcTicks;
        var expires = now + _maxAge.Ticks + _maxFutureSkew.Ticks;
        _handled[id] = new HandledEntry(expires, new WeakReference<Envelope>(envelope));
        if (_store is not null)
        {
            try
            {
                await _store.RecordAsync(id, new DateTimeOffset(expires, TimeSpan.Zero), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _metrics?.RecordMessageOriginReplayStoreFailure("record");
                _logger.LogError(
                    ReplayStoreFailedEvent,
                    "発行元の確かめの再送の記録を置き場に残せませんでした（プロセスの中の記録だけで続けます。再起動の後はこの ID の再送を拒めません）: 型={MessageType} 例外の型={ExceptionType}",
                    envelope.Message?.GetType().Name ?? "-", ex.GetType().Name);
            }
        }

        await PruneAsync(nowAt, cancellationToken).ConfigureAwait(false);
    }

    // 窓を過ぎた記録を捨てる（窓の外の ID は時刻の検査が拒むので、記録が無くても再送は通らない）。
    private async ValueTask PruneAsync(DateTimeOffset nowAt, CancellationToken cancellationToken)
    {
        var now = nowAt.UtcTicks;
        if (now < Interlocked.Read(ref _nextPruneTicks))
            return;
        Interlocked.Exchange(ref _nextPruneTicks, now + TimeSpan.FromMinutes(10).Ticks);
        foreach (var (id, entry) in _handled)
        {
            if (entry.Expires < now)
                _handled.TryRemove(id, out _);
        }

        if (_store is null)
            return;
        try
        {
            await _store.PruneAsync(nowAt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 捨て損ねても確かめは正しい（期限は読みの条件にも入っている）。次の機会に捨てる。
            _logger.LogWarning(ReplayStoreFailedEvent, "再送の記録の期限切れを捨てられませんでした: 例外の型={ExceptionType}", ex.GetType().Name);
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
        MessageOriginCheck.Legacy => "legacy",
        MessageOriginCheck.Unrecorded => "unrecorded",
        _ => "unknown",
    };

    private static string? Truncate(string? value) => value is { Length: > 64 } ? value[..64] : value;

    private sealed record HandledEntry(long Expires, WeakReference<Envelope> Delivery);
}
