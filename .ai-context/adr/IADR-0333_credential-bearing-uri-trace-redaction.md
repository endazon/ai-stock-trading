---
title: IADR-0333 資格情報を含む URI のトレース秘匿は、計装ではなくエクスポータ手前のプロセッサでパスの形を見て落とす
type: impl-adr
status: Accepted
related_ids: [FR-09, NFR]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0333: トレース側の資格情報 URI 秘匿は「エクスポータ手前のプロセッサ＋パス形の判定」で行う

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-11
- 決定者: endazon（利用者・[#751](https://github.com/endazon/ai-stock-trading/issues/751) 起票）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-09（通知）、NFR（セキュリティ）
- 対象 Issue: [#751](https://github.com/endazon/ai-stock-trading/issues/751)
  （[#313](https://github.com/endazon/ai-stock-trading/issues/313) が後継なしで `DUPLICATE` クローズされた分の再起票。
  [#318](https://github.com/endazon/ai-stock-trading/issues/318) の前提）
- 関連する実装仕様書: [20260911_751_trace-uri-redaction](../specs/20260911_751_trace-uri-redaction.md)
- 関連 IADR: [IADR-0121](IADR-0121_credential-bearing-uri-log-redaction.md)（**ログ側**の同じ判断。本 ADR はその
  「残存リスク」節が名指しでトレース側へ残した宿題を閉じる）、
  [IADR-0011](IADR-0011_foundation-min-port.md)（本 ADR が触る共有 shim の可観測性配線）、
  [IADR-0062](IADR-0062_discord-bot-gateway-and-authorization.md)（Discord Bot・秘密は URL ではなくヘッダにある）、
  [IADR-0227](IADR-0227_audit-payload-secret-exposure-guard.md)（監査台帳側の同型の防御）

## コンテキストと課題

Discord Webhook URL は**それ自体が資格情報**である（知っていれば認証なしで当該チャンネルへ投稿でき、
失効手段は再発行のみ）。IADR-0121 がログ経路（Serilog → OTLP → Loki）を塞いだ一方、同 ADR は
「残存リスク」節に**トレース経路（Tempo）には残る**と明記して #313 へ送っていた。**#313 は 2026-08-02 に
後継なしで `DUPLICATE` クローズされ、緩和は 1 行も入らないまま 40 日間出力が続いた。**

実測（本 PR の陰性対照テスト）——秘匿が無い状態で 1 回送信すると、アクティビティのタグはこうなる。

```text
POST | http.request.method=POST | server.address=localhost | server.port=21705
     | url.full=http://localhost:21705/api/webhooks/wh-test-id/SUPER-SECRET-WEBHOOK-TOKEN
     | error.type=connection_error
```

**トークンはクエリではなくパスに載る。** .NET 9 以降の `url.full` はクエリ値を既定で伏せるが、パスは伏せない。
OTel の既定のクエリ秘匿も同様である。

構造上の制約が 1 つある。`AddHttpClientInstrumentation()` は**共有 shim
（`ObservabilityExtensions`）の 1 箇所にしかなく、11 サービス全部のトレース計装を兼ねる**。したがって
IADR-0121 のように「notification-service の中だけ」へ閉じた対策は採れない。

## 検討した選択肢

| 案 | 評価 |
| --- | --- |
| A. `FilterHttpRequestMessage` でスパンごと落とす | **棄却。** 送信の成否・所要時間まで消える。IADR-0121 が定めた向き（「目的は URL を出さないことであって**ログを消すことではない**」）に反し、#313 の受け入れ基準 2（可観測性を不必要に落とさない）にも反する |
| B. `EnrichWithHttpRequestMessage` で `SetTag` する | **棄却。** .NET 8 以降の HTTP 計装は**ランタイム内蔵の `System.Net.Http` ActivitySource** を購読する形であり、`url.full` を書くのは OTel ではなく**ランタイムの `DiagnosticsHandler`**（アクティビティ**開始時**）である。enrich の発火順が計装の内部実装に依存するため、**計装の版が上がった瞬間に無言で失効し得る**——秘匿が黙って外れる形は採らない |
| C. **`BaseProcessor<Activity>.OnEnd` で書き換える（採用）** | 計装の実装に依存しない。**タグを誰が書いたかを問わず、出ていく直前の最終状態を見て落とす**。ASP.NET Core 計装や将来足す別の計装が同じ形の URL を載せても効く |
| D. Webhook をやめて Bot Gateway 送信へ寄せる（#313 の案 3） | **棄却（射程外）。** 根本解決だが FR-09 の送信手段そのものの変更であり、影響が大きく本 issue の射程を超える |

判定条件も 2 案あった。

| 案 | 評価 |
| --- | --- |
| ホスト（`discord.com`）で引く | **棄却。** Discord Bot Gateway（FR-14 / IADR-0062・`discord-owner-token` クライアント）の API パスまで消える。**あちらの秘密はヘッダにあり、パスは障害切り分けに要る情報である。** IADR-0121 決定 4「抑止は当該クライアントに閉じる」の向きに反する |
| **パスの形（`/api/webhooks/<id>/<token>`）で引く（採用）** | ホストが何であれ資格情報だけが落ちる。試験のループバック宛（`http://localhost:18289/api/webhooks/...`）も同じ規則で覆える |

## 決定

1. **共有 shim のトレースパイプラインに `CredentialBearingUriRedactionProcessor` を 1 段入れる。**
   `OnEnd` で `url.full` タグを見て、資格情報を含む形なら書き換える。
2. **判定は「ホスト」ではなく「パスの形」で行う。** `/api/webhooks/` の先に 2 セグメント以上あるものだけを
   資格情報とみなす（`/api/webhooks/<id>` だけの形は資格情報ではないので触らない）。
3. **落とす先は `scheme://host/***`。** ログ側（`RedactedUriHttpClientLogger`）と**同じ出力形に揃える**。
   部分開示（トークンだけ伏せて id は出す）は**しない**（IADR-0121 決定 5 と同じ理由——どこまでが秘密かは
   送信先の実装に依存し、アプリ側が正しく知り続けられる保証がない）。userinfo は `Uri.Host` を使うことで落ちる。
   🔴 **対象は http/https に限る。** `Uri.TryCreate(…, UriKind.Absolute)` の結果は**プラットフォームで違う**——
   Unix では先頭が `/` の相対パスが `file:///…` として**絶対 URI と見なされ**、scheme を見ないと `file:///***` を
   書き戻す。**Windows では緑・Linux の CI でだけ赤**になった（本 PR の初回 CI で実測。回帰は `[Theory]` で固定）。
4. 🔴 **登録位置は `AddOtlpExporter()` より前とする。** OTel のプロセッサは**登録順**に `OnEnd` が走り、
   `AddOtlpExporter()` は末尾にバッチ処理プロセッサを足す。**秘匿がその後ろへ回ると、バッチへ積まれてから
   書き換えることになり間に合わない。** テストは exporter と同じ「後ろ」の位置から観測することで、
   この順序を実質的に固定する。
5. **メトリクス側（`:51` の `AddHttpClientInstrumentation()`）は変更しない。** `url.full` はメトリクスの
   属性にならない（次元は `server.address` 等）ため、対象が存在しない。
6. **ログ側（IADR-0121）は 1 行も変更しない。** 別経路であり、片方の変更が他方を代替しない。

## 理由

- **秘匿を計装の内部実装に依存させない。** 案 B の危険は「効かなくなること」ではなく「**効かなくなったことに
  誰も気付かないこと**」である。IADR-0121 が構成（`Logging:LogLevel`）による秘匿を棄却したのと同じ論法で、
  **無言で失効し得る位置には置かない。**
- **可観測性を落とさない。** 落ちるのは資格情報を含む送信の URL パスだけで、スパン自体・ステータス・
  所要時間・宛先ホストは残る。Webhook 形でない送信の `url.full` は丸ごと残る（回帰テストで固定）。
- **陰性対照を必ず対で置く。** 「トークンが出ていない」は**計装がそもそも動いていないときにも緑になる**。
  同じ入力の Activity を「プロセッサを積む／積まない」の 2 通りへ流し、**差が builder の 1 行だけ**である
  A/B を同じテストファイルに置いて、緑の意味を確定させる。
  🔴 **陰性対照は実送信では書けなかった（実測）。** `Activity` はプロセス全体で共有され、`ActivityListener` は
  **登録順**に停止コールバックが走る。同アセンブリの `FoundationRegistrationTests` は **`TracerProvider` を
  解決したまま破棄しない**ため、並列実行中は**他クラスの秘匿プロセッサが生きている**——実送信の陰性対照は
  クラス単体では緑・アセンブリ全体では**赤**になった（秘匿が先に効いていた）。専用の `ActivitySource` へ
  切り替えて外乱を消した。**「ランタイムが `url.full` にパスを丸ごと書く」ことの実証は、
  巻き添えなしの試験（実送信・非 Webhook 形の URL がパス込みで一致する）が担う。**

## 結果

- 良い影響: #313 が指摘した経路が閉じ、**#318（Webhook の再発行）の実施順序の前提が解ける**
  （#318 は「#313 の対策より先に再発行すると新しい URL がまた漏れる」と明記していた）。
  11 サービス全部に一度に効く。
- 悪い影響・トレードオフ:
  - Webhook 送信スパンの `url.full` からパスが消えるため、**トレースだけでは Webhook の id を区別できない**
    （複数 Webhook を使い分ける運用になった場合の切り分けは、送信元サービス・ログ側の情報に頼る）。
  - **判定語は網羅ではない。** `/api/webhooks/` という Discord の形だけを見ている。**別の「URI 自体が
    資格情報」な送信先を足したら、判定条件を足す必要がある**（IADR-0227 の残余リスクと同型の性質）。
  - 全アクティビティの `OnEnd` で 1 回のタグ参照が走る（`url.full` を持たないスパンは即 return）。
- フォローアップ:
  - **既に Tempo / Loki に蓄積された分の後始末と Webhook の再発行は #318**（稼働環境の操作を含む）。
    本 ADR の着地はその前提であって、失効そのものではない。
  - `docs/security/security.md` の脅威表への「テレメトリへの資格情報混入」の追加は本 PR では見送った。

## 関連

- Supersedes: なし（IADR-0121 を覆さない。同 ADR が残した残存リスクを閉じる）
- Superseded by: なし
