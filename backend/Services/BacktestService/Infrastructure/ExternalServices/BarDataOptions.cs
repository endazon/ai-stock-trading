namespace BacktestService.Infrastructure.ExternalServices;

// FR-15, ADR-0004, #208, IADR-0105: バックテストの過去データ源の構成（セクション "Backtest:BarData"）。
public sealed class BarDataOptions
{
    public const string SectionName = "Backtest:BarData";

    /// <summary>
    /// 過去データ源の選択。既定・空・"none" は no-op＝**外部へ 1 リクエストも出さない**。
    /// 実装は "stooq"（現在取得不能）と "moomoo"（ADR-0023 決定5 で採用）の 2 つ。
    /// 未知の値は安全既定の no-op へ倒す（IADR-0068 と同形の allow-list）。
    /// <para>
    /// ADR-0023 / IADR-0156 / IADR-0157（#382）: "stooq" は**取得不能**（ボット検知チャレンジ。回避実装は計画が禁止）。
    /// **"moomoo" は 2026-08-06 の裁定（ADR-0023 決定5）で米国株日足 OHLC の履歴源として採用され、本リポジトリに
    /// アダプタがある。ただし決定5 は「実装側で確認を要する 2 点」（取得枠の単位と回復周期／前復権と
    /// ADR-0016 決定14 の費用モデルの整合）を本決定の前提とし、確認するまで本番のバックテストへ流さないと定めた。
    /// いずれも実 OpenD でしか確認できず未了である**（詳細は <see cref="MoomooBarDataOptions"/> と IADR-0157）。
    /// </para>
    /// <para>
    /// **既定値をここで変更しないこと**（既定が none であることは HistoricalBarSourceFactoryTests が固定する）。
    /// 既定を "moomoo" にすると、構成していない環境が起動しただけで実 OpenD を叩き始める。
    /// </para>
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>Stooq（Provider="stooq"）の構成。</summary>
    public StooqBarDataOptions Stooq { get; set; } = new();

    /// <summary>moomoo（Provider="moomoo"）の構成。ADR-0023 決定5。</summary>
    public MoomooBarDataOptions Moomoo { get; set; } = new();
}

// #208, IADR-0105: Stooq は登録不要（API キーが無い）ため、opt-in の閂は Provider の明示指定そのものである。
public sealed class StooqBarDataOptions
{
    /// <summary>ベース URL。既定で足りるため通常は設定不要（テスト・将来の移行用）。不正な URL は no-op へ倒す。</summary>
    public string BaseUrl { get; set; } = StooqHistoricalBarSource.DefaultBaseUrl;

    /// <summary>
    /// レート予算（回/分）。既定 10。Stooq は上限を公表しておらず個人運営で SLA も無いため、
    /// 保守的な既定で自制する（IADR-0064 と同じ「送信前に自制する」方針）。0 以下は 1 回/分へクランプする。
    /// </summary>
    public int RequestsPerMinute { get; set; } = 10;
}

// FR-15, ADR-0023 決定5, IADR-0157, #382: moomoo（Provider="moomoo"）の履歴 K 線取得の構成。
//
// ⚠️ **本 provider を構成しても、次の 2 点を実 OpenD で確認するまで本番のバックテストへ流してはならない**
// （ADR-0023 決定5 が「本決定の前提」と明記した確認事項。いずれも本実装では解決していない）。
//   1. **取得枠の単位と回復周期**（`remainQuota: 300` が銘柄数かリクエスト数か。多数銘柄を遡ると枠を使い切り得る。
//      枠が銘柄数単位ならバックテスト対象銘柄数の上限が制約になる）
//   2. **前復権（RehabType_Forward）の調整方式が ADR-0016 決定14 の費用モデル（借株料・配当相当額）と
//      二重計上・欠落を起こさないか**
// 実行時にも `MoomooHistoricalBarSource` が取得のたびに同じ警告を出す。
public sealed class MoomooBarDataOptions
{
    /// <summary>常駐 OpenD のホスト。既定は k8s Service 名（発注経路 Broker:Moomoo:OpenD:Host と同じ既定）。</summary>
    public string OpenDHost { get; set; } = "opend";

    /// <summary>OpenD の API ポート。既定 11111。</summary>
    public ushort OpenDPort { get; set; } = 11111;

    /// <summary>
    /// OpenD の暗号化通信用 RSA 秘密鍵（PKCS#1・PEM）のファイルパス。未設定なら非暗号で接続する。
    /// 相場（Qot）系は暗号化必須ではないが、OpenD 側が暗号化を要求する構成では必要になる。
    /// </summary>
    public string? RsaPrivateKeyPath { get; set; }

    /// <summary>要求ごとの応答待ち（秒）。既定 30（履歴 1,000 件の応答は現在値の照会より重い）。</summary>
    public int ReplyTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// レート予算（回/分）。既定 30。**取得枠の単位・回復周期が未確認である**ため保守的に自制する
    /// （IADR-0064「送信前に自制する」）。0 以下は 1 回/分へクランプする。
    /// </summary>
    public int RequestsPerMinute { get; set; } = 30;
}

// FR-15, ADR-0023 決定5, IADR-0060 決定5, IADR-0157, #382: moomoo 履歴経路の起動時検査。
//
// **構成不備は起動時に落とす。** 発注経路の `MoomooPreflight`（OrderExecutionService.Infrastructure）と
// 同じ規律であり、実体を共有しないのはサービス境界を跨ぐためである（横断参照はしない）。
// ここで検査しないと、鍵のマウント漏れは**初回の履歴取得まで表面化せず**、しかも素の
// `FileNotFoundException` として出るため「なぜ落ちたか」が読み取れない。
//
// **暗号化なしへ黙ってフォールバックしない**ことが要点である（IADR-0060）。相場（Qot）系は暗号化必須では
// ないが、鍵パスが**設定されているのにファイルが無い**のは構成の誤りであり、黙って非暗号で接続すると
// 「接続はできるのに OpenD 側が要求する構成では取得だけが失敗する」形になる。
// 鍵パスが**未設定**であることは正当な構成なので落とさない。
public static class MoomooBarDataPreflight
{
    public static void Validate(MoomooBarDataOptions options, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fileExists);

        if (string.IsNullOrWhiteSpace(options.OpenDHost))
        {
            throw new InvalidOperationException(
                "Backtest:BarData:Moomoo:OpenDHost が空です。常駐 OpenD のホスト"
                + "（in-cluster では Service 名 'opend'）を指定してください。");
        }

        if (options.OpenDPort == 0)
        {
            throw new InvalidOperationException(
                "Backtest:BarData:Moomoo:OpenDPort が 0 です。OpenD の API ポート（既定 11111）を指定してください。");
        }

        if (!string.IsNullOrWhiteSpace(options.RsaPrivateKeyPath) && !fileExists(options.RsaPrivateKeyPath))
        {
            throw new InvalidOperationException(
                $"Backtest:BarData:Moomoo:RsaPrivateKeyPath '{options.RsaPrivateKeyPath}' にファイルがありません。"
                + "OpenD と同一の RSA 秘密鍵（Secret moomoo-rsa）がマウントされているか確認してください。"
                + "**鍵パスが設定されている以上、黙って非暗号へ倒さない**（IADR-0060 決定5）。");
        }
    }
}
