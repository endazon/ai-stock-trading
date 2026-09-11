---
title: 資格情報を含む URI をトレース（url.full）からも伏せる（#751・IADR-0121 の残存リスクの解消）
type: spec
status: done
related_ids: [FR-09, NFR]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 仕様書: トレース側の資格情報 URI 秘匿（#751）

## 起点

issue [#751](https://github.com/endazon/ai-stock-trading/issues/751)。
[#313](https://github.com/endazon/ai-stock-trading/issues/313)（2026-08-02 に `DUPLICATE` でクローズ・**後継なし**）が
指摘した経路が未着地のまま残っていた、という**実装の欠落**である。

- 計画 ID: FR-09（通知）、NFR（セキュリティ）
- 関連 IADR: [IADR-0121](../adr/IADR-0121_credential-bearing-uri-log-redaction.md)（**ログ側**の秘匿。本作業はその
  「残存リスク」節が名指しでトレース側へ残した宿題を閉じる）、
  [IADR-0020](../adr/IADR-0020_notification-safe-outbound.md)（Discord Webhook 送信）、
  [IADR-0227](../adr/IADR-0227_audit-payload-secret-exposure-guard.md)（監査台帳側の同型の防御）、
  [IADR-0011](../adr/IADR-0011_foundation-min-port.md)（本作業が触る共有 shim の可観測性配線）
- 前提を解く先: [#318](https://github.com/endazon/ai-stock-trading/issues/318)（Webhook URL の失効・再発行）。
  **同 issue は「#313 の対策より先に再発行すると新しい URL がまた漏れる」と実施順序を明記しており、
  本作業が着地するまで構造的に進められない。**

## 事前調査（実測）

- `ObservabilityExtensions.cs:46`（トレース）と `:51`（メトリクス）が `AddHttpClientInstrumentation()` を
  **引数なし**で呼んでいる。`git grep -n "FilterHttpRequestMessage\|EnrichWithHttpRequest\|url.full"` は
  `backend/` で **0 件**（言及はすべて `.ai-context/` の記録）。つまり秘匿は 1 箇所も入っていない。
- Discord Webhook のトークンは**パス**（`/api/webhooks/<id>/<token>`）に載る。.NET 9 以降の
  `url.full` はクエリ値を既定で伏せるが、**パスは伏せない**。したがって OTel 既定のクエリ秘匿では効かない
  （IADR-0121 の根拠節と同じ事実）。
- ログ側は `RedactedUriHttpClientLogger`（`scheme://host/***`）で塞がっており、**本作業では触らない**。
- ~~shim のテスト資産（`AiStockTrading.TestSupport.PlatformShim.Tests`）に `TracerProvider` /
  `ActivityListener` を使うテストは **1 件も無い**（`git grep` で確認）。プロセス全体で共有される
  `ActivityListener` の相互干渉を考慮する必要があるのは**本作業が新設するクラス内だけ**である。~~
  🔴 **［着手後に判明・この事前調査は誤りであった］** `FoundationRegistrationTests` が
  `TracerProvider` を解決している（しかも破棄していない）。**走査の出力を `head -30` で切ったために見落とした**
  ——`.claude/rules/traceability.md` の規則 7「走査の出力を加工して読まない」を、まさにこの調査で破っていた。
  誤りの代償は陰性対照の設計やり直しであった（下記「テスト方針」）。

## 走査した母集合（規則 1・2・3・6。誤りの側＝「秘匿されていない計装」と「秘密の形」から引いた）

軸を 3 本引いた（規則 5）。パスの除外のみ・拡張子で絞らない（規則 3）。

| 軸 | 検索語 | ヒット | 判断 |
| --- | --- | --- | --- |
| 1 | `AddHttpClientInstrumentation` | 6 件（コード 2 / 記録 4） | コード 2 件は**同一ファイル**（`ObservabilityExtensions.cs:46` トレース・`:51` メトリクス）。**変更はトレース側 1 箇所のみ**——メトリクスは URL を次元に持たない（`url.full` はメトリクスの属性にならず、`server.address` 等に落ちる）ため対象外 |
| 2 | `url\.full\|FilterHttpRequestMessage\|EnrichWithHttpRequest` | 4 件（すべて `.ai-context/` の記録） | **コードに秘匿は存在しない**ことの確認。記録側は凍結（下表） |
| 3 | `api/webhooks` | 9 件 | 実装 2（`RedactedUriHttpClientLogger` のコメント・`DiscordWebhookHttpClientExtensions` は該当なし）／テスト 4（`Notification` 側のダミー URL）／記録 3。**ダミー URL の形が `/api/webhooks/<id>/<token>` で統一されている**ことを確認し、判定条件をこの形に合わせた |

除外したものと理由（規則 6）:

| 対象 | 種別 | 対応 | 理由 |
| --- | --- | --- | --- |
| `.ai-context/adr/IADR-0121_...md` 「残存リスク」節 | 凍結記録 | **本文は不変**。末尾へ `［2026-09-11 追記］` を足して後継（IADR-0333）を指すだけにする | `.ai-context/README.md` の凍結原則。ただし**残存リスクが閉じたことを旧記録から辿れないと、次の読者が「まだ残っている」と読む**ため、追記は行う（同ディレクトリの既存慣行。`IADR-0170` / `IADR-0269` ほか多数） |
| `.ai-context/specs/20260731_289_...md` | 凍結記録 | **不変** | 2026-07-31 時点で「トレース側は #313 で別途」と書いた史実 |
| `.ai-context/adr/IADR-0227_...md` | 凍結記録 | **不変** | 「#313 / #318 で同型の事故が 2 回」という当時の論拠。台帳側の決定であり本作業と競合しない |
| `.ai-context/specs/20260814_483_blocked-tasks-remeasure.md` | 凍結記録 | **不変** | #318 を `blocked:human` と数えた時点の記録 |
| `backend/Services/NotificationService/**`（ログ側 4 ファイル） | 実装・テスト | **不変** | issue が「ログ側の秘匿は変えない」と明示。**ログ側とトレース側は別経路であり、片方の変更が他方を代替しない** |
| `docs/security/security.md` の脅威表 | 生きた文書 | **不変（見送り）** | T-13 として「テレメトリへの資格情報混入」を起こす価値はあるが、issue の受け入れ基準の外であり、脅威表は並行作業と衝突しやすい。**見送りを明示して残す**（下記「未決事項」） |
| `CHANGELOG.md` | 生成物 | **不変** | 生成物。コミット件名から自動更新される |

`docs/` 配下は `Tempo|トレース` で 6 ファイルがヒットしたが、**トレース属性の中身に踏み込んでいるのは
`docs/observability/observability.md` の「ログ・トレース」節だけ**であった（他は datasource 名・
stand-up 手順・運用手順への言及）。同節のみ 1 行を足す。

## 対象範囲

- 対象: 共有 shim の**トレース**パイプライン（11 サービス全部が通る唯一の可観測性配線）。
- 対象外: ログ側の秘匿（IADR-0121・不変）／メトリクス側／実際の Webhook 再発行と Tempo 蓄積分の後始末
  （#318。稼働環境の操作を含むため本 PR では触らない）。

## 設計

**`BaseProcessor<Activity>` で、エクスポータより手前に置いた 1 段の書き換えを入れる。**

```csharp
.WithTracing(tracing => tracing
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddProcessor(new CredentialBearingUriRedactionProcessor())  // ← ここ
    .AddOtlpExporter(...))
```

判定と置換（`RedactedUriHttpClientLogger` と**同じ向き・同じ出力形**）:

- `url.full` タグが `/api/webhooks/<seg>/<seg>…` の形のパスを持つとき、**`scheme://host/***` へ落とす**。
- 部分開示（トークンだけ伏せて ID は残す）は**しない**（IADR-0121 決定 5 と同じ理由）。
- userinfo（`https://user:pass@host/...`）も `Uri.Host` を使うことで自動的に落ちる。
- 🔴 **対象は http/https に限る。** `Uri.TryCreate(…, UriKind.Absolute)` の結果は**プラットフォームで違う**——
  Unix では先頭が `/` の相対パスが `file:///…` として**絶対 URI と見なされる**（Windows では見なされない）。
  scheme を見ないと `file:///***` を書き戻す。**手元の Windows では 101 件すべて緑・Linux の CI でだけ 1 件が赤**
  になって判明した（本 PR の初回 CI・`backend-test (4)`）。回帰は `[Theory]`（相対パス／`file://`／`ftp://`）で固定した。
  **手元の全緑は「CI でも緑」を意味しない**——`Uri` の解釈のように OS に依存する API では特にそうである。

### なぜ enrich / filter ではなくプロセッサか

| 案 | 判断 |
| --- | --- |
| `FilterHttpRequestMessage` でスパンごと落とす | **棄却**。送信の成否・所要時間まで消える。IADR-0121 が「目的は URL を出さないことであってログを消すことではない」と定めた向きに反する |
| `EnrichWithHttpRequestMessage` で `SetTag` | **棄却**。.NET 8 以降の `AddHttpClientInstrumentation()` は**ランタイム内蔵の `System.Net.Http` ActivitySource** を購読する形であり、`url.full` を書くのは OTel ではなく**ランタイムの `DiagnosticsHandler`**（アクティビティ開始時）である。enrich の発火順が計装の内部実装に依存し、**計装の版が上がった瞬間に無言で失効し得る** |
| **`BaseProcessor<Activity>.OnEnd`（採用）** | 計装の実装に依存しない。**タグが誰に書かれたかを問わず、出ていく直前の状態を見て落とす**。ASP.NET Core 計装や将来の別計装が同じ形の URL を載せても効く |

🔴 **プロセッサの登録位置が意味を持つ。** OTel のプロセッサは**登録順**に `OnEnd` が走り、
`AddOtlpExporter()` は末尾にバッチ処理プロセッサを足す。**秘匿は `AddOtlpExporter()` より前に
登録しなければ、バッチへ積まれてから書き換えることになり間に合わない。**

### 判定を「ホスト」ではなく「パスの形」で行う理由

`discord.com` 宛てを一律に伏せると、**Discord Bot Gateway（FR-14・`discord-owner-token` クライアント）の
API パスまで消える**。Bot 側の秘密は URL ではなくヘッダにあり（IADR-0062）、パスは障害切り分けに要る情報である。
IADR-0121 決定 4 が「抑止は当該クライアントに閉じる」とした向きをトレース側でも守る。
パスの形で引けば、**ホストが何であれ**（`discord.com` でも、試験の `localhost` でも）資格情報だけが落ちる。

## 受け入れ基準

- [x] Webhook URL がトレースの `url.full`・**スパン名・他のどのタグ値**にも平文で現れない（#313 の受け入れ基準）
- [x] **陰性対照**が用意されている——秘匿が無い経路では同じ送信でトークンが `url.full` に**載る**ことを
      同じテストファイルで示す（緑が「そもそも計装が動いていない」ことによる緑でないと示す）
- [x] Webhook 形でない URL の `url.full` は**そのまま残る**（可観測性を不必要に落としていない）
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が通る
- [x] ログ側（`RedactedUriHttpClientLogger`）に差分が無い

## 対照実験（実測）

新設 17 件（`[Fact]` 4 ＋ `[Theory]` 13 ケース）を含め、当アセンブリ **101 件が緑**（変更前の母数は 84 件）。
3 回連続で緑を確認した（`ActivityListener` の共有に起因する不安定さが無いことの確認）。

**秘匿の 1 行（`.AddProcessor(new CredentialBearingUriRedactionProcessor())`）を外して同じ 101 件を走らせると、
肯定形 1 件だけが赤になり、残り 100 件は緑のまま**であった。

```text
失敗 …CredentialBearingUriTraceRedactionTests.共有の可観測性配線を通した_Webhook_送信のトレースにトークンが現れない
  Expected ObservableStrings(activity)
    {"POST", "http.request.method=POST", "server.address=localhost", "server.port=28524",
     "url.full=http://localhost:28524/api/webhooks/wh-test-id/SUPER-SECRET-WEBHOOK-TOKEN",
     "error.type=connection_error"}
  to not have any items matching s.Contains("SUPER-SECRET-WEBHOOK-TOKEN", Ordinal) …
  but found {"url.full=http://localhost:28524/api/webhooks/wh-test-id/SUPER-SECRET-WEBHOOK-TOKEN"}.

失敗!  -失敗: 1、合格: 100、スキップ: 0、合計: 101
```

（上の失敗出力のタグ列は初回計測時のもの。ポート番号は実行ごとに変わる。）

**失敗メッセージ自体が #313 の指摘した漏洩をそのまま出力している**（`url.full` にトークンが平文で載る）。
重要なのは**赤が 1 件で止まったこと**である——陰性対照（A/B）と巻き添えなしの試験は緑のままであり、
赤の原因が**秘匿の欠落**であって計装の停止ではないことを示す。

## テスト方針

`backend/TestSupport/AiStockTrading.TestSupport.PlatformShim.Tests/` に 1 クラスを新設する。
**1 クラスに閉じる**のは、`ActivityListener` がプロセス全体で共有され、xUnit がクラスを跨いで並列実行するため
（同一クラス内のテストは直列に走る＝相互干渉しない）。

| # | 形 | 内容 |
| --- | --- | --- |
| 1 | 肯定形（配線・**実送信**） | `AddAiStockTradingObservability` で組んだ実パイプラインを通し、捕捉した Activity の `url.full` が `scheme://host/***` で、**DisplayName と全タグ値**のどこにもトークン文字列が無い |
| 2 | **陰性対照（A/B）** | 同じ入力の Activity を、**プロセッサを積んだ／積まない**の 2 通りのパイプラインへ流す。積まない側はトークンが `url.full` に**残ったまま出ていく**／積む側は落ちる。**差は builder の 1 行だけ** |
| 3 | 巻き添えなし（**実送信**） | Webhook 形でないパスは `url.full` が丸ごと残る。**同時に「ランタイムが `url.full` にパスを丸ごと書く」ことの実証**でもある（2 が合成 Activity を使うため、漏洩の実在の根拠はここが担う） |
| 4 | 純関数 | 判定・置換の `[Theory]`（webhook 形／非 webhook／相対 URI／userinfo つき） |

実送信は**待ち受けの無いループバックポート**へ行う（接続拒否）。アクティビティはリクエスト開始時に
`url.full` を刻むため、**サーバを立てなくても観測できる**——外部依存も Docker も要らない。

🔴 **陰性対照を実送信で書くことはできなかった（実測）。** `Activity` はプロセス全体で共有され、
`ActivityListener` は**登録順**に停止コールバックが走る。本アセンブリの `FoundationRegistrationTests`
（可観測性の登録が解決できることの試験）は **`TracerProvider` を解決したまま破棄しない**ため、
**並列実行中は他クラスの秘匿プロセッサが生きている**。初版の陰性対照（素の `ActivityListener` で実送信を聴く）は
クラス単体では緑・**アセンブリ全体では赤**になった——秘匿が先に効いて `url.full` が既に落ちていたためである。
**専用の `ActivitySource`（誰も listen していない）へ切り替えて外乱を消した。**

## 計画書との差異

- 差異: なし。計画の FR-09（通知）・NFR（セキュリティ）の範囲内で、送信仕様は変えない。

## 未決事項

1. **`docs/security/security.md` の脅威表に T-13（テレメトリへの資格情報混入）を起こすか。** 本 PR では見送った
   （上記母集合の表）。起こす場合は別 issue。
2. **既に Tempo / Loki に蓄積された分の後始末と Webhook の再発行**は #318 の担当であり、本 PR の対象外。
   本 PR の着地が #318 の前提を解く（#318 の `blocked:human` は残る＝稼働環境の操作を含むため）。
3. **`FoundationRegistrationTests.可観測性の登録は例外なく解決できる` が `TracerProvider` を解決したまま
   破棄していない**（`using` が無い）。本作業ではその試験を書き換えず、こちら側を外乱に強くして回避した
   （上記「テスト方針」）。**プロセス全体へ影響する listener が試験終了後も残る**のは、同種の試験を書く人が
   次も踏む形であり、別途の是正が望ましい。本 PR の射程外。
