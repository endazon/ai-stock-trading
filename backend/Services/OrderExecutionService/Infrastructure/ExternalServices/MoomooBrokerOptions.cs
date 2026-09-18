using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Configuration;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// #13, #132, ADR-0002: moomoo アダプタ（OpenD 接続）の構成。既定は常駐 OpenD の k8s Service（opend:11111）。
// SIMULATE 固定（実弾は撃たない・IADR-0016 / IADR-0056）。実弾解禁は別 IADR＋明示 config で。
//
// RsaPrivateKeyPath: OpenD の暗号化通信用 RSA 秘密鍵（PKCS#1・PEM）のファイルパス。
// moomoo は cross-network（別 Pod 間）の trade 接続に暗号化を要求するため、in-cluster では必須。
// OpenD 側の <rsa_private_key> と同一鍵を指す（k8s Secret をマウント）。未設定なら非暗号（loopback 用）。
//
// #132 / IADR-0060: 本番切替に向けて接続パラメータを外部化した（既定値は現行挙動と同一）。
public sealed record MoomooBrokerOptions(string OpenDHost, ushort OpenDPort, string? RsaPrivateKeyPath = null)
{
    // 実弾（TrdEnv_Real）は撃たない。構成で受理する唯一の取引環境（IADR-0016 / IADR-0056）。
    public const string SimulateTrdEnv = "simulate";

    // 現行のハードコード値。外部化しても既定は変えない（挙動中立）。
    public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromSeconds(15);

    // 応答待ちの上限（10 分）。これを超える設定は事実上の無期限待ちで、発注が宙に浮く。
    private static readonly TimeSpan MaxReplyTimeout = TimeSpan.FromMinutes(10);

    // OpenD への要求ごとの応答待ち時間。
    public TimeSpan ReplyTimeout { get; init; } = DefaultReplyTimeout;

    // 取引環境。常に "simulate"（FromConfiguration が他の値を拒否する）。
    public string TrdEnv { get; init; } = SimulateTrdEnv;

    // FR-10, #821, IADR-0347: 損切りの実行機構 S3（他のブローカー側注文種別）で試す設定。
    // **手法そのもの（S0〜S3）の選択ではない**——それは利用者の設定（risk-management）である（IADR-0342 決定2）。
    public MoomooAlternativeStopSettings AlternativeStop { get; init; } = new();

    public static MoomooBrokerOptions FromConfiguration(IConfiguration config)
    {
        var host = config["Broker:Moomoo:OpenD:Host"];
        var portStr = config["Broker:Moomoo:OpenD:Port"];
        var rsaPath = config["Broker:Moomoo:OpenD:RsaPrivateKeyPath"];
        return new MoomooBrokerOptions(
            OpenDHost: string.IsNullOrWhiteSpace(host) ? "opend" : host,
            OpenDPort: ushort.TryParse(portStr, out var p) ? p : (ushort)11111,
            RsaPrivateKeyPath: string.IsNullOrWhiteSpace(rsaPath) ? null : rsaPath)
        {
            ReplyTimeout = ParseReplyTimeout(config["Broker:Moomoo:OpenD:ReplyTimeoutSeconds"]),
            TrdEnv = EnsureSimulate(config["Broker:Moomoo:TrdEnv"]),
            AlternativeStop = new MoomooAlternativeStopSettings(
                ParseAlternativeStopOrderType(config["Broker:Moomoo:AlternativeStopOrderType"]),
                ParseStopLimitOffsetRatio(config["Broker:Moomoo:StopLimitOffsetRatio"])),
        };
    }

    // #821, IADR-0347: S3 で試す注文種別。未設定は StopLimit（計画 ADR-0040 決定1 が先に挙げた種別）。
    // **未知の値を既定へ黙って倒さない**——「TrailingStop を選んだつもりで StopLimit が飛ぶ」を作らない。
    private static AlternativeProtectiveOrderType ParseAlternativeStopOrderType(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return AlternativeProtectiveOrderType.StopLimit;
        }
        return configured.Trim().ToLowerInvariant() switch
        {
            "stoplimit" => AlternativeProtectiveOrderType.StopLimit,
            "trailingstop" => AlternativeProtectiveOrderType.TrailingStop,
            _ => throw new InvalidOperationException(
                $"Broker:Moomoo:AlternativeStopOrderType '{configured}' は受理しません。"
                + "'stoplimit'（既定）または 'trailingstop' を指定してください"
                + "（損切りの実行機構 S3 で試すブローカー側注文種別。#821）。"),
        };
    }

    // #821, IADR-0347: StopLimit の指値を発火価格から**不利側**へずらす比率。0 にすると急落時に約定せず
    // 保護にならないため既定を 1% に置く。0〜10% の範囲外は起動時に停止する（10% を超える指値は保護の体を成さない）。
    private static decimal ParseStopLimitOffsetRatio(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return MoomooAlternativeStopSettings.DefaultStopLimitOffsetRatio;
        }
        if (!decimal.TryParse(configured, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var ratio)
            || ratio < 0m || ratio > 0.1m)
        {
            throw new InvalidOperationException(
                $"Broker:Moomoo:StopLimitOffsetRatio '{configured}' が不正です。0〜0.1（＝0〜10%）の小数を"
                + $"指定してください（未設定なら既定 {MoomooAlternativeStopSettings.DefaultStopLimitOffsetRatio}）。");
        }
        return ratio;
    }

    private static TimeSpan ParseReplyTimeout(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultReplyTimeout;
        }
        if (!int.TryParse(configured, out var seconds) || seconds <= 0 || seconds > MaxReplyTimeout.TotalSeconds)
        {
            throw new InvalidOperationException(
                $"Broker:Moomoo:OpenD:ReplyTimeoutSeconds '{configured}' が不正です。"
                + $"1〜{MaxReplyTimeout.TotalSeconds:0} 秒の整数を指定してください"
                + $"（未設定なら既定 {DefaultReplyTimeout.TotalSeconds:0} 秒）。");
        }
        return TimeSpan.FromSeconds(seconds);
    }

    // #132, IADR-0060 決定 5: config で実弾（TrdEnv_Real）を要求されたら「黙って SIMULATE で流す」のではなく停止する。
    // これは実弾を可能にする設定ではない（TrdHeader は TrdEnv_Simulate 固定のまま）。運用者の誤認を防ぐ閂である。
    private static string EnsureSimulate(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return SimulateTrdEnv;
        }
        var normalized = configured.Trim().ToLowerInvariant();
        if (normalized != SimulateTrdEnv)
        {
            throw new InvalidOperationException(
                $"Broker:Moomoo:TrdEnv '{configured}' は受理しません。本実装は SIMULATE 固定です（IADR-0016 / IADR-0056）。"
                + "実弾（TrdEnv_Real）の解禁には、別の実装 ADR と前提条件（リスク統制・監査・上限の再確認、"
                + "秘匿情報の Vault 化、発注予約 Reserved 滞留の自動リコンサイル #141）の充足が要ります（IADR-0056 §3）。");
        }
        return SimulateTrdEnv;
    }
}

// FR-10, #821, IADR-0347: 損切りの実行機構 S3（他のブローカー側注文種別）の設定。
// OrderType: 試す注文種別（既定 StopLimit）。StopLimitOffsetRatio: StopLimit の指値を発火価格から不利側へ
// ずらす比率（既定 1%。0 だと急落時に約定せず保護にならない）。TrailingStop はトレール幅を
// 「エントリーの判断価格 − 発火価格」の絶対額で送るため、本設定を使わない。
public sealed record MoomooAlternativeStopSettings(
    AlternativeProtectiveOrderType OrderType = AlternativeProtectiveOrderType.StopLimit,
    decimal StopLimitOffsetRatio = MoomooAlternativeStopSettings.DefaultStopLimitOffsetRatio)
{
    public const decimal DefaultStopLimitOffsetRatio = 0.01m;
}

// #132, IADR-0060 決定 5: moomoo 発注経路の起動時 preflight。本番切替で踏みやすい構成ミスを、
// 「接続はするが trade だけ落ちる」形ではなく起動時の明示エラーとして表面化させる。
// 実 OpenD 非依存（ファイル存在判定を注入する）＝単体テスト可能。
public static class MoomooPreflight
{
    public static void Validate(MoomooBrokerOptions options, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(options.OpenDHost))
        {
            throw new InvalidOperationException(
                "Broker:Moomoo:OpenD:Host が空です。常駐 OpenD のホスト（in-cluster では Service 名 'opend'）を指定してください。");
        }
        if (options.OpenDPort == 0)
        {
            throw new InvalidOperationException(
                "Broker:Moomoo:OpenD:Port が 0 です。OpenD の API ポート（既定 11111）を指定してください。");
        }
        // cross-network（worker→opend）の trade は RSA 暗号化が必須（#13 で確定）。鍵の構成漏れを黙って
        // 非暗号化へ倒すと、接続は成立するのに発注だけが失敗する。Secret のマウント漏れは起動時に落とす。
        if (!string.IsNullOrWhiteSpace(options.RsaPrivateKeyPath) && !fileExists(options.RsaPrivateKeyPath))
        {
            throw new InvalidOperationException(
                $"Broker:Moomoo:OpenD:RsaPrivateKeyPath '{options.RsaPrivateKeyPath}' にファイルがありません。"
                + "OpenD と同一の RSA 秘密鍵（Secret moomoo-rsa）がマウントされているか確認してください。"
                + "cross-network の trade 接続には暗号化が必須です（非暗号は OpenD が loopback で listen する構成のみ）。");
        }
    }
}
