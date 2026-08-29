---
title: セキュリティ仕様書
type: security-spec
status: review
created: 2026-08-07
updated: 2026-08-21
author: endazon (with Claude Code)
---
<!-- trace:
ids: [FR-02, FR-08, FR-10, FR-11, FR-14, FR-19, FR-20, NFR-05, NFR-06, NFR-10, UC-06, UC-07]
adrs: [ADR-0003, ADR-0004, ADR-0012, MSP:ADR-0004, MSP:ADR-0024]
iadrs: [IADR-0011, IADR-0019, IADR-0051, IADR-0056, IADR-0059, IADR-0060, IADR-0062, IADR-0072, IADR-0111, IADR-0164, IADR-0169, IADR-0171, IADR-0174, IADR-0175, IADR-0176]
specs: [20260807_450_security-spec-from-measurement]
issues: [#24, #346, #450, #456, MSP#445]
-->


# セキュリティ仕様書

> 必須ドキュメント（リポジトリ単位）。本リポジトリのセキュリティを定める。雛形は `docs/templates/security_spec_template.md`。

## 本書が受け持つ範囲

- 非機能要件（セキュリティ）: 発注機能へのアクセスは本人のみ・外部公開しない
- 関連する計画 ADR: **取引データの MCP 非公開**／生成AIの売買判断のガードレール／情報源の採用方針

## 本書の読み方 — **「無いこと」を 4 語で書き分ける**

**本書に空欄は無い。** 記述が無い＝未記入、ではない。以下の 4 語で区別する。

| 表記 | 意味 |
| --- | --- |
| （記述あり） | **対策がある**。コードの所在または実装 ADR を添えてある |
| **未実装** | 調べた結果、**対策が無い**。担当 issue を添えてある |
| **未確認** | **調べていない**／本リポジトリからは決められない。何を見れば分かるかを添えてある |
| **対象外** | 本リポジトリの**管掌外**。管掌先を添えてある |

> ⚠️ **「未確認」を「たぶん大丈夫」と読まないこと。**「未実装」と「未確認」を分けてあるのは、**残余リスクの大きさが違う**からである。
>
> ⚠️ **「対象外」は「担保されている」を意味しない。** 誰が担保するのかを同じ行に書いてある。

**本書は 2026-08-07 時点の実測である**（初版 `677f2d6`／T-2 は #456 で解消したため更新済み）。 実装が動けば陳腐化するが、**それを検知する機械検査は無い**（書き分けの実装 ADR の §悪い影響）。

## MCP（外部 AI エージェント）への公開

**本ユニットのデータは MCP（Model Context Protocol）経由で外部 AI エージェントへ公開しない。**
根拠は取引データの MCP 非公開を定めた計画 ADR（Accepted・2026-07-23）である。実装側の担保は
「MCP 非公開の維持」の実装 ADR に記録した。

### 現状（2026-08-07 実測）

**本リポジトリには MCP 関連の構成・コードが 1 件も存在しない。** `backend/` と `deploy/` を
大文字小文字を問わず部分一致で走査して 0 件であった。基盤の MCP サーバー（基盤側の計画 ADR）は
**既定非公開＝許可リスト方式**であるため、**本ユニットが自分を登録しない限り公開されない**。

### 閉じているのは MCP という経路だけである

**この統制を「AST のデータは誰も検索・参照できない」と読み違えないこと。** MCP 非公開の計画 ADR が閉じているのは
**外部 AI エージェント向けの MCP 経路のみ**である。

| 経路 | 可否 |
| --- | --- |
| 利用者本人による基盤チャット UI・RAG 検索（**ナレッジベース・RAG の要求**） | **従来どおり可能**（本人権限・Keycloak / ABAC） |
| Discord からの参照（**Discord 対話の要求**） | **従来どおり可能** |
| ナレッジベースへの保存そのもの（**同上**） | **行う**（保存はする。MCP へ出さないだけである） |
| MCP ツール（`retrieval.*` / `document.*`）経由の外部エージェント | **公開しない** |

### 将来公開する場合の手続き — **新 ADR が必須**

公開が必要になった場合、**構成を足すだけで実施してはならない。** MCP 非公開の計画 ADR を Superseded する
新 ADR で、次の 4 点を**個別に**定めてから行う（同 ADR の §決定）。

1. 対象文書（どのコレクション・どの retrieval スコープまでか）
2. ABAC 属性（基盤の認可モデル）
3. データ越境ティア判定（基盤のデータ越境ポリシー）
4. **無人エージェント（Client Credentials）の権限スコープ**

### 退行の防止

`backend/Tests/AiStockTrading.Architecture.Tests/McpExposureNotDeclaredTests.cs` が
`backend/` と `deploy/` を走査し、**MCP 公開の宣言が入り込むとテストが落ちる**。
「実装していない」と「実装してはならない」は別であり、**後者はコードに書かれない限り誰も守れない**
（退行防止の実装 ADR の決定 1 と同じ論法）。

**このテストは本リポジトリ内しか見ない。** 基盤側の許可リストへ基盤側の PR で追加された場合は
検出できない —— **基盤 MCP 再実装後の結合確認**が別途必要であり、
[`docs/blocked-tasks.md`](../blocked-tasks.md) の A-10 に登録してある
（基盤側の対応待ち）。

## 認証・認可

**独立した権限・認可仕様書（`docs/authz/`）は置かない。** 認可モデルはポリシー 2 種で足りており、分けると本書と二重管理になる。**本節が単一情報源である。**

### 方式 — Keycloak OIDC / JWT（基盤ランタイム Foundation を最小移植し、基盤の認可モデルに揃える）

登録は `AddAiStockTradingAuth`（`backend/TestSupport/AiStockTrading.TestSupport.PlatformShim/Foundation/Extensions/AuthExtensions.cs`）。

| 項目 | 値 |
| --- | --- |
| Authority | `Auth:Authority`（既定 `http://keycloak:8080/realms/ai-stock-trading`） |
| ロールの取り出し | Keycloak の `realm_access.roles` を `KeycloakRolesClaimsTransformation` で `ClaimTypes.Role` へ展開する。**標準ハンドラは展開しない**ため、これが無いと `RequireRole` が実トークンにマッチしない |
| 名前クレーム | `preferred_username`。**既定マップ（`unique_name`）のままだと実トークンで `Name` が null になり、監査ログの subject が `anonymous` へ潰れる** |

### 認可ポリシー — **2 種。書き込みはサービスへ渡さない**

| ポリシー | 要求ロール | 用途 |
| --- | --- | --- |
| `OwnerOnly` | `trading-owner` | **kill switch・リスク設定変更・段階昇格・監査照会** |
| `OwnerOrService` | `trading-owner` **または** `trading-service` | **読み取り系の同期照会のみ**（sizing-context / open-positions / daily-policy。サービス間同期照会の s2s 認証による） |

> **書き込み系は `OwnerOnly` 据え置きである** —— サービス間 s2s トークンに書き込み権限を与えない（最小権限）。

### 適用状況 — **サービスごとに違う。表のとおりである**

| サービス | 状態 |
| --- | --- |
| MarketMonitor / Audit / RiskManagement / Report / Configuration / CostControl | **Keycloak 認証を登録**し、エンドポイントを `OwnerOnly` / `OwnerOrService` で保護している |
| Backtest | **意図的に素の `AddAuthentication()` / `AddAuthorization()`**。認可を要する API を持たず、共通ミドルウェアの依存だけを満たす（`Program.cs` に明記）。公開は `/health/*` と `/internal/introspection` のみ。**認可を要する API を足す際は `AddAiStockTradingAuth` へ差し替えること** |
| Notification / TradeDecision / OrderExecution | **認証の登録なし**。HTTP は `/health/*` と `/internal/introspection` のみで、業務操作は Wolverine メッセージング経由である |
| **InformationCollection** | 🔴 **認証の登録なし。かつ `POST /internal/collection/run-once` を無認証で公開している**（下記「脅威と対策」T-2） |

### Discord からの操作 — **6 層。すべて既定拒否**（Discord 対話の要求と設定変更・緊急停止のユースケース。Bot は Gateway 常駐＋多層認証とし、既定 no-op・owner トークンで kill switch を呼ぶ）

`DiscordCommandAuthorizer`（`backend/Services/NotificationService/Features/Notifications/DiscordCommandAuthorizer.cs`）。**1 層でも不成立なら以降を評価せず拒否する。**

| 層 | 内容 | 未設定時 |
| --- | --- | --- |
| 1 | DM は**無条件で拒否**（なりすまし・誤送信防止） | — |
| 2 | 専用サーバー（`GuildId`）一致 | **拒否** |
| 3 | 専用チャンネル（`ChannelId`）一致 | **拒否** |
| 4 | ユーザー ID 許可リスト | **拒否**（空＝全拒否） |
| 5 | Keycloak マッピング（actor を特定できなければ拒否） | **拒否** |
| 6 | 高リスク操作の確認ステップ（`KillSwitchConfirmation`） | — |

> **「設定が空＝全許可」にしないことが要である** —— 発注機能を持つシステムの操作窓口であり、**設定漏れを全開放にしない**。

## データ保護

| 区分 | 対象 | 方式 |
| --- | --- | --- |
| **保存時暗号化** | PostgreSQL（監査証跡・業務台帳・設定） | **未確認** —— 本リポジトリのコードにも Helm チャートにも記述が無い。**インフラの管掌**（[#24](https://github.com/endazon/ai-stock-trading/issues/24)）。ディスク暗号化の有無は Hetzner k3s の構築時に決まる |
| **通信時暗号化（クラスタ内）** | サービス間 HTTP・PostgreSQL・RabbitMQ | **未実装**（担当 [#24](https://github.com/endazon/ai-stock-trading/issues/24)）。`UseHttpsRedirection` は無く、接続文字列に `sslmode` 指定も無い。Service はすべて **ClusterIP** であり、**クラスタ内は平文**である |
| **通信時暗号化（外部公開）** | — | **対象外**。**本チャートは Ingress を持たない**（`deploy/helm/ai-stock-trading/templates/` に Ingress テンプレートが存在しない）。TLS 終端は基盤側の管掌（[#24](https://github.com/endazon/ai-stock-trading/issues/24)） |
| **ネットワーク分離** | Pod 間通信 | **未実装**（担当 [#24](https://github.com/endazon/ai-stock-trading/issues/24)）。**NetworkPolicy テンプレートが無い**。名前空間内からは全 Pod が相互に到達できる。**#456 以前は、これが T-2 の唯一の緩和を無効にしていた** —— 現在は T-2 側がアプリの認可で閉じたため、本項が単独で危険を作ることは無くなった（他の平文経路の露出は残る） |
| **通信時暗号化（外部 API）** | FRED / Finnhub / SEC EDGAR / EDINET / Stooq / 日銀 / Discord | ✅ **すべて HTTPS**（実測: `api.stlouisfed.org`・`fred.stlouisfed.org`・`finnhub.io`・`data.sec.gov`・`www.sec.gov`・`api.edinet-fsa.go.jp`・`stooq.com`・`www.stat-search.boj.or.jp`・`discord.com`） |
| **通信時暗号化（LLM ゲートウェイ）** | 基盤の LLM ゲートウェイ | **本番は未結線**（`values.yaml:338,381` の `LlmGateway__BaseUrl` は**空文字列**＝Placeholder LLM・**呼ばない**ため、現状プロンプトは流れていない）。🔴 **ローカル（経路B）では平文 HTTP** —— `values-local.yaml:120,151` が `http://llmgateway-service.microservices-platform:8080` を実値で設定している。**本番結線時に同じ書式を使えば平文になる**（`values.yaml:380` のコメント例がその書式である）。流れる中身は**プロンプトと LLM 応答＝判断根拠・保有銘柄**であり、**基盤の名前空間を跨ぐ**ため露出面は上記「クラスタ内」より広い。**結線時の TLS はインフラの管掌**（[#24](https://github.com/endazon/ai-stock-trading/issues/24)） |
| **個人情報** | — | **本システムは単独利用者運用であり、第三者の個人情報を扱わない**（認可は単層である） |
| **機微情報** | ブローカー資格情報・建玉・発注履歴・API キー | 下記「秘密情報管理」および「監査ログ」を参照 |

## 秘密情報管理

### 保管 — **k8s Secret 経由。Vault は受け口のみで未充足**

| 経路 | 状態 |
| --- | --- |
| 実際の注入 | `ast-secrets`（Kubernetes Secret）を `secretKeyRef` で環境変数へ。**`optional: true`＝鍵が無くても起動は落とさず、機能側が no-op へ倒れる**（fail-safe） |
| Vault（External Secrets Operator） | 🔴 **受け口のテンプレートはあるが既定 `externalSecrets.enabled: false`。ストア（Vault / ESO）は本リポジトリに無く [#24](https://github.com/endazon/ai-stock-trading/issues/24) の管掌である。** テンプレート自身が「**受け口の用意は Vault 化の充足ではない**」と明記している |
| 実弾解禁との関係 | 実アダプタ実装の実装 ADR §3 が実弾解禁の前提に挙げる「**秘匿情報の Vault 化**」は**未充足のまま**である |
| moomoo のパスワード | **平文を置かない。MD5（小文字 hex）を格納**し、entrypoint が `OpenD.xml` へ書く |
| ローテーション | **未実装**（担当 [#24](https://github.com/endazon/ai-stock-trading/issues/24)）。`refreshInterval: 1h` は Vault → Secret の**同期間隔**であって、**鍵そのものの更新ではない**。鍵の再発行手順は文書化されていない |

### コミット防止 — **2 段**

| 段 | 仕組み |
| --- | --- |
| ローカル（書き込み時） | `.claude/hooks/guard-secrets.js`（PreToolUse）。**秘密鍵 PEM・AWS・GitHub・Slack・Google・`sk-` 形式の 6 パターンをブロック**、`secret=` 等 1 パターンを警告 |
| CI（push / PR） | **gitleaks**（`.github/workflows/security.yml`） |

> ⚠️ `deploy/helm/ai-stock-trading/values.yaml` の `rabbitmqConnectionString: amqp://guest:guest@rabbitmq:5672` は**開発既定の資格情報がリポジトリに入っている**。本番で差し替えることは [#24](https://github.com/endazon/ai-stock-trading/issues/24) の管掌である。

## 監査ログ

### 記録項目 — **実装済み**

| 対象イベント | 記録項目 | 保管期間 |
| --- | --- | --- |
| **全ドメインイベント**（監査・時系列記録の要求と取引履歴の参照。専用サービスが全ドメインイベントを購読し追記専用台帳へ記録する）<br>`AuditService/Features/AuditEvents/AuditEntry.cs` | `Id`（冪等キー＝Wolverine `Envelope.Id`）／`EventType`／`CorrelationId`（注文系は `DecisionId`・市場系は `EventId`）／`Symbol`／`Summary`（人間可読 1 行）／`Detail`（**イベント全量 JSON**）／`OccurredAt`（イベント時刻）／`RecordedAt`（記録時刻）。**追記専用** | 🔴 **未実装**（下記） |
| **設定変更**<br>`RiskManagementService/Features/RiskManagement/SettingsChangeEntry.cs` | `Before` / `After`（前後値の全量）／`Actor`／`Reason`／`ChangedAt`／`SettingsChangeType`。**`RequireActorAndReason` により Actor と Reason は必須**（`RiskSettingsService`） | 🔴 **未実装**（下記） |

**照会経路**: `GET /audit`（`OwnerOnly`・`AuditService/Features/AuditEvents/AuditQueryEndpoints.cs`）／設定変更履歴は設定変更・取引履歴参照の各ユースケースが定める画面。

### 保管期間 — 🔴 **未実装**（担当 [#346](https://github.com/endazon/ai-stock-trading/issues/346)）

**計画は監査証跡・業務台帳の 7 年保持を求めるが、それを担保する仕組みは実装されていない。**

パージ（終端行のみ・保持期間 90 日・下限クランプ付き）の対象は**重複排除ストア 2 つ**（`processed_messages` / `order_dispatch_reservations`）だけであり、`audit_events`・`cost_entries`・`executed_orders` は**明示的に対象外**である。

> ⚠️ **「パージ対象外」は「7 年保持が担保されている」ことを意味しない。** 除外の記述は「消さない」しか言っておらず、**バックアップ・保全・可用性の担保は別の実装**である。
> 担当は [#346](https://github.com/endazon/ai-stock-trading/issues/346)（再実装版への切替計画 — 監査証跡・業務台帳の 7 年保持の保全）。

## 脅威と対策

| ID | 脅威 | 影響 | 対策 |
| --- | --- | --- | --- |
| T-1 | **取引データが外部 AI エージェントへ流出する** | 建玉・発注履歴・判断根拠の第三者流出 | ✅ **MCP へ公開しない**。`McpExposureNotDeclaredTests` が `backend/` と `deploy/` を走査し、**宣言が入り込むとテストが落ちる**（MCP 非公開の維持）。上記「MCP への公開」節を参照 |
| T-2 | **無認証の内部エンドポイントを踏まれ、収集サイクルを任意に起動される** | LLM 費用の消費・レート制限の枯渇（**発注はしない**。判断は下流の統制を通る） | ✅ **対策済み**（[#456](https://github.com/endazon/ai-stock-trading/issues/456)。run-once の認可と CronJob のトークン取得）。`POST /internal/collection/run-once` は **`OwnerOrService`** を要求する。**渡しているのは「サイクルを起こす権限」であって「発注する権限」ではない**（決定1）。CronJob は **client_credentials でトークンを取ってから**叩き、**資格情報が無い・token 取得に失敗した場合は Job が赤くなる**（fail-closed・決定2）。**退行は構造テストが止める** —— `UnauthenticatedEndpointsNotAllowedTests` が**認可メタデータを持たないエンドポイントを許可リスト以外に許さない**（決定4） |
| T-3 | **JWT の検証が緩く、別クライアント向けトークンが通る** | 認可の迂回 | 🔴 **未対策（判断済み・値は据え置き）**。`RequireHttpsMetadata = false`（メタデータ取得が平文 HTTP を許す）・`ValidateAudience = false`（**aud を検証しない**）。**基盤（microservices-platform）の Keycloak クライアント構成と揃っている必要があり、片側だけ厳しくすると全サービスが 401 になる**ため据え置いた（run-once の認可を定めた実装 ADR の決定 3）。**再判断する条件**: ①Ingress が入り外部から到達可能になる ②マルチテナント化する ③基盤側の構成が確認できるようになる。担当: [#24](https://github.com/endazon/ai-stock-trading/issues/24) |
| T-4 | **収集した外部テキストが LLM への指示として作用する**（プロンプトインジェクション） | AI が意図しない判断・発注を行う | ✅ **RAG 取得文脈を「データ」として構造分離し、出典で限定する**（RAG 取得文脈のプロンプトインジェクション対策）。文脈は**参考情報として本判断プロンプトのみ**に注入し、**既定 no-op・取得失敗は文脈なしへ縮退**する（RAG 文脈は Application の抽象ポートで受ける） |
| T-5 | **AI の暴走・誤判断が実弾の発注になる** | 実損 | ✅ **多重。** 実弾 triple-latch（OpenD 本番化の切替ゲート）／ブローカー階層の閂 0〜4（provider × environment の 2 軸表現）／**アダプタは `TrdEnv_Simulate` 固定**（`Mode=Live` でも SIMULATE を用いる）／`BrokerFactory` の安全既定はペーパー／kill switch は `OwnerOnly` |
| T-6 | **設定で統制を実質無効化される** | ガードの空洞化 | ✅ **構造クランプ。** 保持期間は**下限** 7 日（重複排除ストアのパージ方針）、為替の鮮度上限は**上限** 30 日（為替レートの鮮度＝警告と停止の分離）。**設定値ではなく構造で担保する** |
| T-7 | **Discord から第三者が統制操作を行う** | kill switch・段階昇格の乗っ取り | ✅ **6 層すべて既定拒否**（上記「Discord からの操作」）。**設定が空＝全許可にしない** |
| T-8 | **秘密情報がリポジトリへ混入する** | 資格情報の流出 | ✅ **2 段**（`guard-secrets.js` ＋ gitleaks。上記「秘密情報管理」） |
| T-9 | **依存パッケージの脆弱性** | 任意コード実行等 | ✅ **3 種**（`.github/workflows/`）。**CodeQL**（`codeql.yml`）／**Dependency Review**（PR 差分）／**`dotnet list package --vulnerable`**（推移的依存を含む） |
| T-10 | **クラスタ内の平文通信を傍受される** | 資格情報・取引データの露出。**LLM ゲートウェイ経路はプロンプトと応答（＝判断根拠・保有銘柄）が名前空間を跨ぐため露出面が広いが、本番は未結線であり現状は流れていない**（ローカル経路B のみ平文） | 🔴 **未対策**（上記「データ保護」）。mTLS・NetworkPolicy はいずれも無い。**インフラの管掌**（[#24](https://github.com/endazon/ai-stock-trading/issues/24)）。**LLM ゲートウェイの結線時に TLS を前提にすること**（未結線の今が是正の好機である） |
| T-11 | **監査証跡が失われる** | 事後追跡の不能 | 🔴 **保管期間・バックアップは未実装**（上記「監査ログ」）。記録項目とパージ除外は実装済み。担当 [#346](https://github.com/endazon/ai-stock-trading/issues/346) |

## 未決事項

| # | 項目 | 担当 |
| --- | --- | --- |
| 1 | **保存時暗号化の有無**（**未確認** —— Hetzner k3s の構築時に決まる） | [#24](https://github.com/endazon/ai-stock-trading/issues/24) |
| 2 | **TLS 終端位置・Ingress・NetworkPolicy**（**未実装**） | [#24](https://github.com/endazon/ai-stock-trading/issues/24) |
| 3 | **Vault 化**（受け口のみ。**実弾解禁の前提が未充足**） | [#24](https://github.com/endazon/ai-stock-trading/issues/24) |
| 4 | **監査証跡の 7 年保持の担保**（**未実装**） | [#346](https://github.com/endazon/ai-stock-trading/issues/346) |
| 5 | **JWT の `ValidateAudience=false` / `RequireHttpsMetadata=false`**（**判断済み・値は据え置き**。再判断の条件は T-3 と、run-once の認可を定めた実装 ADR の決定 3）。**`/internal/collection/run-once` の無認証は #456 で解消した** | [#24](https://github.com/endazon/ai-stock-trading/issues/24) |
| 6 | **秘密情報のローテーション手順**（**未実装**。同期間隔とは別物） | [#24](https://github.com/endazon/ai-stock-trading/issues/24) |
| 7 | **基盤 MCP 再実装後の結合確認**（本リポジトリのテストは基盤側の許可リストを見ない） | [`blocked-tasks.md`](../blocked-tasks.md) A-10 |

## 関連

- 実装 ADR: run-once の認可と CronJob のトークン取得（**run-once の認可・CronJob のトークン・無認証経路の構造固定**）／セキュリティ仕様書における「無いこと」の書き分け（**本書の書き分けの規約**）／MCP 非公開の維持／
  基盤ランタイム Foundation の最小移植とサービス間同期照会の s2s 認証（認証・認可）／Discord Bot は Gateway 常駐＋多層認証とし、既定 no-op・owner トークンで kill switch を呼ぶ（Discord 多層認可）／
  RAG 取得文脈のプロンプトインジェクション対策
- 運用仕様書: [operations.md](../operations/operations.md)（データ保持・パージ／トラブルシュート）
- 運用 Runbook: [banned-symbol-unlock-runbook.md](../operations/banned-symbol-unlock-runbook.md)（統制の一時解除と監査への記録）
- 作業仕様書: 仕様書: セキュリティ仕様書の記入（実測ベース）
