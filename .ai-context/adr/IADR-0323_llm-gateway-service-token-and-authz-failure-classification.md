---
title: IADR-0323 LLM ゲートウェイ呼び出しは MSP レルムの s2s トークンを inline ハンドラで付け、認可の失敗（401/403）をモデル不可から独立した分類にする
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-06, FR-11, FR-16, NFR-05, ADR-0017, IADR-0051, IADR-0061, IADR-0071, IADR-0093, IADR-0216]
author: endazon (with Claude Code)
created: 2026-09-10
updated: 2026-09-10
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0017_llm-fallback-policy.md
---

# IADR-0323: LLM ゲートウェイ呼び出しは MSP レルムの s2s トークンを inline ハンドラで付け、認可の失敗（401/403）をモデル不可から独立した分類にする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-04**（生成AIの売買判断と判断根拠の記録）、**FR-11**（監査ログ）、
  **FR-06 / FR-16**（報告書）、**NFR-05**（認証情報の保管）、**`AST/ADR-0017`**（LLM フォールバック方針。
  決定3 の「429＝再試行 / 400 系＝モデル不可」の 2 分割へ 3 つ目の軸を足す）
- 対象 Issue: [#724](https://github.com/endazon/ai-stock-trading/issues/724)。基盤側は MSP#1364（`ServiceCaller` で
  REST 3 口を締める）。基盤側の実装 ADR（MSP `IADR-0424`）決定 4 が「本リポジトリが壊れること」と
  「**認可を緩めない**こと」を先に記録している
- 関連する実装仕様書: [20260910_724_llm-gateway-service-token](../specs/20260910_724_llm-gateway-service-token.md)
- 関連 IADR:
  [IADR-0093](IADR-0093_kb-writer-cross-realm-s2s.md)（KB のクロスレルム s2s。**本 IADR が踏襲する型**）、
  [IADR-0051](IADR-0051_service-to-service-auth.md)（client_credentials 基盤。**無改修で再利用**）、
  [IADR-0061](IADR-0061_llm-production-wiring.md) 決定3/4・
  [IADR-0071](IADR-0071_report-service-remaining.md)（「`/complete` は匿名ゆえトークンを付けない」＝
  **本 IADR が置き換える**判断）、
  [IADR-0216](IADR-0216_trade-decision-fallback-ban-enforcement.md)（429 とモデル不可の分割。本 IADR はその隣に認可を足す）

> **参照上の注意**: 本文の `MSP/ADR-00xx` `MSP#NNNN` は基盤リポジトリ（microservices-platform）側の
> ADR / issue を指す。**裸の `ADR-0010` は AST の `ADR-0010_dotnet-10-follow` であって LLM ゲートウェイの
> ADR ではない**（LLM ゲートウェイは `MSP/ADR-0010`）。既存コードに残る裸の表記は別 issue で是正する。

## 背景・課題

基盤は LLM ゲートウェイの REST 3 口（`/complete`・`/complete/stream`・`/embed`）へ端点単位の認可を掛ける
（MSP#1364）。認可の主体は既存の `ServiceCaller`＝**realm ロール `platform-service`** である
（基盤 `PlatformAuthPolicies.ServiceCaller` は `RequireRole(ServiceRole)` の 1 行）。

本リポジトリの 2 サービスは `/complete` を**匿名で**呼んでいる。

| サービス | 呼び出し口 | 現状 |
| --- | --- | --- |
| `ReportService` | `HttpReportNarrativeDrafter` → `POST /complete` | 資格情報なし |
| `TradeDecisionService` | `HttpLlmCompletionClient` → `POST /complete` | 資格情報なし |

発火条件は `LlmGateway__BaseUrl` の設定であり、`values-local.yaml`（経路B・ローカル k3s）が
`http://llmgateway-service.microservices-platform:8080` を指すため**ローカルでは実際に発火する**。

### 🔴 同格の第 2 の欠陥: 401 の誤帰属

`LlmFailureClassification.Classify` は 429 を除く 400 系を `ModelUnavailable` へ倒す。したがって **401 も
「モデル不可」**になり、`HttpLlmCompletionClient` は `TradeDecisionSkipped(Reason=model-unavailable)` を
publish する。結果、監査台帳・月報の内訳・Discord 通知に
**「割当モデルが利用できません」という誤った原因**が残る。原因は認可であってモデルの可用性ではない。

**誤帰属の波及先は実測で 3 つある**（`TradeDecisionSkipped` の購読者）。とくに
`NotificationService.NotificationFormatter` は**通知の題名に「割当モデルが利用できません」を焼き込んでいる**
ため、「事由の文字列を増やして同じイベントを出す」対処では**誤帰属が別の層で再生産される**。

## 決定

### 1. トークンの機構は再利用し、**MSP レルム × inline ハンドラ**の型を採る

`ServiceAuthOptions` / `ClientCredentialsTokenProvider` / `ServiceTokenHandler`（[IADR-0051]）を**無改修で**再利用し、
セクション名で一般化した登録点 `PlatformRealmAuthExtensions.AddAiStockTradingPlatformRealmToken(builder, config,
sectionName, tokenClientName)` を `AiStockTrading.TestSupport.PlatformShim.Foundation.Auth` へ置く。

- **AST レルムの `AddAiStockTradingServiceToken` は使えない。** LLM ゲートウェイは MSP のサービスであり、
  AST レルム発行トークンは issuer 不一致で 401 になる（[IADR-0093] が KB で実測した故障と同型）。
- **DI へ provider を登録せず inline 生成する**（[IADR-0093] 決定2 と同じ理由）。同一プロセスは
  `AddAiStockTradingServiceToken` も呼び `IServiceAccessTokenProvider` を `TryAddSingleton` する。DI へ登録すると
  TryAdd 衝突の先勝ちで**レルムを跨いでトークンが漏れる**。
- **KB 側（`KnowledgeBaseAuthExtensions`）は本 PR では触らない。** 同じ形を持つが、共通化のための書き換えは
  本 issue の射程外である（KB の挙動を変える理由が無い）。

### 2. 設定セクションは `LlmGateway:Auth`。AST の `Auth:Authority` へフォールバックしない

`TokenEndpoint`（明示・最優先）／`Authority`（未指定時の導出元）／`ClientId`／`ClientSecret`／`Scope`／
`RefreshSkewSeconds`。**KB とは別セクション**にする——同じ資格情報を与える運用でも、将来 LLM 専用の
confidential client へ分けるときに **values の 2 行の差し替えだけで済み、コード変更が要らない**。

**未設定なら何も付けない**（`IsEnabled` false）。基盤がまだ認可を掛けていない間は本変更前とバイト等価であり、
AST 側を先にマージしても壊れない（issue #724 の「順序」の要請を満たす）。

### 3. 認可の失敗は独立した分類にし、**見送りイベントを出さない**

- `LlmFailureKind` に `Unauthorized` を足し、`Classify` が **401 / 403** をここへ倒す。
  400 系の内側にありながらモデル不可ではない例外は、これで **401 / 403 / 429 の 3 つ**になる。
- `HttpLlmCompletionClient` は `Unauthorized` のとき **`TradeDecisionSkipped` を publish しない**。
  理由は上記のとおり、通知の題名が誤帰属を再生産するためである。Hold の理由は専用の定数
  `HoldUnauthorized`（「LLM ゲートウェイの認可が拒否されたため…」）とし、`HoldModelUnavailable`
  （割当モデル不可）とも `HoldFallback`（伝送の失敗）とも区別する。
- `HttpReportNarrativeDrafter` は 401/403 のとき資格情報を名指しする警告を出す。倒れ先は
  プレースホルダ散文のまま（報告書は発注を伴わない＝安全側。変更しない）。

**変えないこと**: フォールバック禁止・Hold へ倒す方針そのもの（`AST/ADR-0017` 決定2）。新しい契約イベント・
新しい通知種別は足さない。

### 4. 資格情報は既存の MSP レルム客体を再利用する

本リポジトリは既に MSP レルムへ到達する service account を持つ（confidential client
**`ai-stock-trading-kb-writer`**。`ast-secrets` の `kb-auth-client-id` / `kb-auth-client-secret`）。
**新しい secret 鍵を作らず**、この鍵を `LlmGateway__Auth__*` にも与える。

- 利点: 運用者が投入する秘密が増えない。取り違え（KB の秘密で LLM を叩く）の事故面も増えない。
- 負債（明記する）: `ast-secrets` の鍵名が `kb-` 始まりのまま「AST ユニットの MSP レルム資格情報」を指す。
  分けるときは values の 2 行の差し替えで済む（決定 2）。

### 5. realm 側の変更は基盤リポジトリの管掌であり、本リポジトリでは行わない（申し送り）

隣接クローンの**読み取りのみ**で実測した現況:

| 実測項目 | 値 |
| --- | --- |
| realm id | `platform` |
| `ServiceCaller` の中身 | `RequireRole("platform-service")` |
| AST の既存 client | `ai-stock-trading-kb-writer`（confidential・service account 有効） |
| その service account のロール | **`platform-operator` のみ**（`platform-service` **なし**） |

🔴 したがって MSP#1364 の着地後、本リポジトリの呼び出しは**トークンを付けても 403** になる。
**AST 側の配線だけでは疎通しない。**

**依頼する変更（1 行）**: `service-account-ai-stock-trading-kb-writer` の realmRoles へ
`platform-service` を足す（`["platform-operator", "platform-service"]`）。あわせて当該 client の
名称・説明を「AST ユニットの MSP レルム s2s 客体（KB 書き込み＋LLM ゲートウェイ呼び出し）」へ広げる。

**代替案（基盤が最小権限を優先する場合）**: 専用 client `ai-stock-trading-llm-caller`
（service account の realmRoles は `platform-service` のみ）。この場合 AST 側は
`LlmGateway__Auth__ClientId` / `ClientSecret` に別の `ast-secrets` 鍵を割り当てるだけでよく、**コード変更は不要**。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A. AST レルムの `AddAiStockTradingServiceToken` を LLM クライアントへ足す | 既存の 1 行を再利用 | **不可**。issuer 不一致で 401（[IADR-0093] 背景で実測済み）。KB で同じ誤りが起きている |
| B. KB の `AddAiStockTradingKnowledgeBaseAuth` をそのまま LLM にも使う | 新規コードほぼゼロ | 不採用。`KnowledgeBase:Auth` セクションが LLM の設定点になり、**KB を無効化すると LLM も落ちる**という隠れた結合ができる。将来 client を分けられない |
| C. セクション名で一般化した登録点を PlatformShim へ置く（**採用**） | 既存 3 型を無改修再利用し、セクションだけ引数化 | 新規は 1 メソッド。KB と LLM は別セクション・別 token クライアントで独立する |
| D. 認可用の `TradeDecisionSkipReasons` を足して同じイベントを publish | 語彙は増やせる設計になっている | **不採用**。通知の題名が「割当モデルが利用できません」で固定されており、**誤帰属が通知層で再生産される**。題名を可変にするのは通知契約の変更で射程外 |
| E. 401 を `Other`（5xx と同じ）へ倒す | 変更が最小 | 不採用。「呼び出し先の不調」と「こちらの資格情報の不足」は運用の打ち手が違う。原因を名指しできる分類にする |

## 影響・結果

- **良い影響**: 基盤の認可が入っても両サービスが疎通する（realm 反映後）。反映前も安全側へ倒れ、かつ
  **記録される原因が正しくなる**（本 issue の主目的）。
- **注意**: `LlmGateway:Auth` は**登録時に読む**（既存の `ServiceAuth` / `KnowledgeBase:Auth` と同じ eager read）。
  テストで構成を与えるときは `UseSetting`（ホスト構成）を使う。`ConfigureAppConfiguration` は
  `builder.Build()` の時点で適用されるため**登録時には見えない**（実測で 1 度踏んだ）。

## 残余リスク

- **realm 反映までは 403 のままである**（決定 5）。AST 側だけでは疎通しない。
- **`ast-secrets` の鍵名が `kb-` のまま**である（決定 4 の負債）。
- **`/complete/stream`・`/embed` は本リポジトリから呼んでいない**ため対象外である。将来呼ぶ場合は
  同じ `LlmGateway:Auth` に乗る（名前付きクライアントへ同じ登録点を足すだけ）。
- **既存の裸の `ADR-0010`（MSP の ADR を指す）が本文・values に残る**。別 issue で是正する。
