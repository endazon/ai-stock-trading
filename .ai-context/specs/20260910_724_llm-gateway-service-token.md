---
title: LLM ゲートウェイ呼び出しへ MSP レルムの s2s トークンを載せ、401/403 を ModelUnavailable へ倒さない
type: spec
status: done
related_ids: [FR-04, FR-06, FR-11, FR-16, NFR-05, IADR-0323]
author: endazon (with Claude Code)
created: 2026-09-10
updated: 2026-09-10
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0017_llm-fallback-policy.md
---

# 仕様書: LLM ゲートウェイ呼び出しへ s2s トークンを載せ、認可失敗をモデル不可へ倒さない（#724）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）:
  - **FR-04**（生成AIの売買判断と**判断根拠の記録**）——(2) の誤帰属は判断根拠の記録そのものを誤らせる。
  - **FR-11**（監査ログ）——見送りの事由が監査台帳・月報の内訳へ流れる。
  - **FR-06 / FR-16**（報告書の階層管理・テンプレート）——報告書散文の LLM 呼び出しが 401 で全件
    プレースホルダへ縮退する。
- 非機能要件（NFR）: **NFR-05**（証券口座・API 認証情報の保管＝コード・リポジトリに含めない）。本作業は
  s2s の client secret を 1 つ増やすため、空既定＋`secretKeyRef` 経由という既存の扱いに揃える。
  - 🔴 「認可を要求する上流へ資格情報を載せる」こと自体に当たる NFR 番号は計画側に**無い**（NFR-06 は
    「発注機能へのアクセス＝利用者本人のみ」であり s2s の軸ではない）。無理に近い番号を当てない
    （`traceability.md`「無採番の NFR を許す 2 場合」の②）。よって NFR-05 は**秘密の取り扱いの面だけ**を指す。
- ユースケース（UC）: UC-01（定時取引サイクル）が影響を受けるが、本作業は UC のフローを変えない。
- 画面（SC）: なし
- 関連 ADR:
  - `AST/ADR-0017`（LLM フォールバック方針。決定3 が「429＝再試行 / 400 系＝モデル不可」の 2 分割を定めた。
    本作業はこの分割へ**認可失敗の 3 つ目**を足す）
  - `AST/ADR-0011`・`AST/ADR-0014`（モデルのピン留め・用途別割当。フォールバック禁止は変えない）
  - `MSP/ADR-0010`（platform LLM ゲートウェイ）、`MSP/ADR-0084`（端点単位の認可判定）、
    `MSP/ADR-0004`（Keycloak OIDC/JWT）
  - 🔴 **裸の `ADR-0010` は AST の `ADR-0010_dotnet-10-follow` を指す。** LLM ゲートウェイの ADR は
    **MSP 側の ADR-0010** であり、既存コード・values のコメントに残る裸の `ADR-0010` は誤りである
    （本作業では**新たに書く箇所だけ** `MSP/ADR-0010` と修飾し、既存の全置換は射程外＝下の除外表）。
- 起点 issue: #724（基盤側は MSP#1364・その実装 ADR は MSP 側 `IADR-0424`）

## 目的・背景

基盤（microservices-platform）は LLM ゲートウェイの REST 3 口（`/complete`・`/complete/stream`・`/embed`）へ
端点単位の認可を掛ける（MSP#1364。PR 済み・未マージ）。現在この 3 口は認可を 1 つも持たないため、本リポジトリの
2 サービスは**匿名で** `/complete` を呼んでいる。基盤側が入ると 401 になる。

| サービス | 呼び先 | 現状 |
| --- | --- | --- |
| `ReportService`（`HttpReportNarrativeDrafter`） | `POST /complete` | 資格情報なし |
| `TradeDecisionService`（`HttpLlmCompletionClient`） | `POST /complete` | 資格情報なし |

発火条件は `LlmGateway__BaseUrl` が設定されていることである。`values.yaml`（本番既定）は空、
`values-local.yaml`（経路B・ローカル k3s）は `http://llmgateway-service.microservices-platform:8080` を
指すため、**ローカル k3s では発火する**。

### 🔴 第 2 の欠陥（同格）: 401 の誤帰属

`TradeDecisionService` は非 2xx を `LlmFailureClassification.Classify` に通す。現在の実装は
`>= 400 and < 500`（429 を除く）を **`ModelUnavailable`** へ倒すため、**401 もモデル不可**になる。結果:

- すべての取引判断が Hold になる（安全側ではある）
- **「割当モデルが利用できない」という誤った理由が記録される**（`TradeDecisionSkipped`
  → 監査台帳・Discord 通知・月報の `SkipsByReason`）

通知本文は `NotificationFormatter.From(TradeDecisionSkipped)` が
「取引判断の見送り: **割当モデルが利用できません**」と**題名に焼き込んでいる**ため、
`TradeDecisionSkipReasons` に認可用の事由を足して同じイベントを publish すると
**誤帰属を別の層で再生産する**。よって「新しい事由で同じイベントを出す」案は採らない（下の決定 3）。

## 母集合の引き直し（着手時に自分で引いた・[[IADR-0141]] 決定1 / `traceability.repo.md` 規則 9・10）

**issue 本文の「対象は次の 2 つ」を母集合として使わない。** 誤りの側（「匿名」「トークンは付けない」）から
引き直し、パスで引いた（拡張子で絞らない・行フィルタで絞らない）。

### 軸 1: `git grep -n "匿名" -- . ':!CHANGELOG.md'`（全 78 行）

LLM ゲートウェイの匿名性を主張しているのは次の 5 件（他 73 行はバックテストの**銘柄匿名化**・BFF の
**匿名 401**・「秘匿名」であり別事象）。

| # | 箇所 | 扱い |
| --- | --- | --- |
| 1 | `backend/Services/ReportService/Infrastructure/ExternalServices/HttpReportNarrativeDrafter.cs:16` | **是正する**（live なコード） |
| 2 | `backend/Services/TradeDecisionService/Program.cs:75-76` | **是正する**（live なコード。issue 本文が挙げていない・引き直しで増えた 1 件） |
| 3 | `.ai-context/adr/IADR-0061_llm-production-wiring.md:64`（決定3/4 の根拠） | **日付つき追記**で後継を併記 |
| 4 | `.ai-context/adr/IADR-0071_report-service-remaining.md:62` | **日付つき追記**で後継を併記 |
| 5 | `.ai-context/adr/IADR-0284_east-west-grpc-scope-and-order-ruling.md:104` | **除外**（下表） |

### 軸 2: `git grep -n "トークンは付けない\|トークンを付けない\|トークン付与は不要\|認可を持たない"`（全 16 行）

軸 1 の 1・2・4 に加え、`.ai-context/specs/20260717_11_llm-production-wiring.md:59` が出た（除外・下表）。
残りは KB / Discord owner auth の **fail-safe の説明**であり、本件とは逆向き（正しい記述）。

### 軸 3: `git grep -ln "LlmGateway" -- . ':!CHANGELOG.md'`（全 57 ファイル・パスで引く）

設定面の露出先を漏らさないために引いた。**書き換える必要があるのは次の 6 ファイル**である。

| ファイル | 理由 |
| --- | --- |
| `deploy/helm/ai-stock-trading/values.yaml` | 本番既定（空）の設定点を置く |
| `deploy/helm/ai-stock-trading/values-local.yaml` | 経路B で実際に有効化する。**`helm.yml` の「values-local drops no env」検査があるため values.yaml と同時に足さないと CI が赤くなる** |
| `deploy/helm/ai-stock-trading/README.md` | ②実 LLM の説明に認可の前提を足す |
| `.env.example` | docker-compose 経路の設定点（KB と同型） |
| `docker-compose.yml` | 同上（report / trade-decision の 2 サービス） |
| `docs/security/security.md` | **導出値の引き直し**（規則 10）。行番号 `values.yaml:338,381` / `values-local.yaml:120,151` を引用しており、本作業で行が動く。**引く前から既に古い**（実測 420/491・165/223）ため、動いた先の実測値へ直す |

`scripts/validate-runtime-scaffold.js` / `scripts/k8s-local-deploy.sh` / `.github/workflows/helm.yml` は
**引いたが変更しない**（下の除外表）。

### 軸 4: `git grep -ln "KnowledgeBase__Auth\|KnowledgeBase:Auth"`（全 18 ファイル）

**既存のクロスレルム s2s（IADR-0093）が露出している面の一覧**＝本作業が揃えるべき面の正解表として引いた。
コード 3 ファイル・設定 4 ファイル（values.yaml / values-local.yaml / docker-compose.yml / appsettings）・
chart README。本作業の追加面は上の軸 3 の表と一致する（`appsettings.Development.json` は
`InformationCollectionService` にしか無く、report / trade-decision には KB Auth の記述が無いため足さない）。

### 軸 5: `git grep -n "TradeDecisionSkipped\|TradeDecisionSkipReasons" -- backend`（非テスト 20 行）

誤帰属の**波及先**を引いた。`AuditService`（台帳）・`NotificationService`（通知の題名に
「割当モデルが利用できません」を焼き込み）・`ReportService`（`SkipsByReason` の月報内訳）の 3 つ。
→ 決定 3（新しい事由で同じイベントを publish しない）の根拠。

### 除外したものとその理由（黙って落とさない）

| 除外 | 理由 |
| --- | --- |
| `.ai-context/specs/20260717_11_llm-production-wiring.md`・`20260903_584_grpc-scope-decision.md`・`20260718_report-service-remaining.md` | **確定済みの作業仕様書は point-in-time の記録**であり、後から本文を書き換えない（`traceability.repo.md`「凍結の射程」）。当時の判断は当時の基盤の実態として正しい |
| `.ai-context/adr/IADR-0284`（east-west gRPC の射程裁定） | 匿名性を**決めていない**（`IADR-0061` を引用しているだけ）。是正は引用元（IADR-0061）の追記で伝わる。表セル 1 個への追記は、決定の所在を二重化して次の読み手を迷わせる |
| `scripts/validate-runtime-scaffold.js` | ヒットは「AST は LLM **プロバイダ鍵**を持たない」の記述であり、本作業で変わらない（増えるのは s2s の client secret であってプロバイダ鍵ではない） |
| `scripts/k8s-local-deploy.sh` | **`ast-secrets` に新しい鍵を作らない**（決定 5）ため変更不要。既存の `kb-auth-client-id` / `kb-auth-client-secret` を再利用する |
| `.github/workflows/helm.yml` | 追加する env は既定空であり、既存の検査（本番バイト等価・values-local の env 欠落）に**そのまま乗る**。新しい assert は足さない（同型の事故が 2 回起きたら足す、が本リポの条件） |
| `backend/Services/*/appsettings.Development.json` | report / trade-decision には KB Auth の記述が無く、開発既定は「空＝トークンを付けない」で成立する（新設しても値を書けない） |
| 既存の裸の `ADR-0010`（MSP の LLM ゲートウェイ ADR を指す。実測 8 箇所） | 本作業の起点ではない**別の是正**である。混ぜると本 PR の受け入れ基準がぼやける。**新たに書く箇所だけ `MSP/ADR-0010` と修飾**し、既存の是正は別 issue へ切り出す |
| `deploy/keycloak/` 相当（realm 定義） | **本リポジトリに存在しない。** realm は MSP が所有する（決定 6 の申し送り） |

## 決定（実装方針）

### 1. トークン取得の機構は**再利用**する（新しい機構を書かない）

本リポジトリには既に client_credentials の s2s 基盤がある。

- `AiStockTrading.TestSupport.PlatformShim.Foundation.Auth`:
  `ServiceAuthOptions` / `ClientCredentialsTokenProvider` / `ServiceTokenHandler` / `ServiceAuthExtensions`
  （`AddAiStockTradingServiceToken`＝**AST レルム**・`ServiceAuth` セクション固定・DI に provider を
  `TryAddSingleton` 登録）
- `AiStockTrading.Shared.KnowledgeBase.Foundation.Extensions.KnowledgeBaseAuthExtensions`
  （`AddAiStockTradingKnowledgeBaseAuth`＝**MSP レルム**・`KnowledgeBase:Auth` セクション・
  **DI へ登録せず inline 生成**）

**LLM ゲートウェイは MSP のサービスである**（`llmgateway-service.microservices-platform`）。したがって
AST レルムの `AddAiStockTradingServiceToken` は使えない（issuer 不一致で 401。IADR-0093 が実測した故障と同型）。
**KB と同じ「MSP レルム × inline ハンドラ」型**を採る。

`KnowledgeBaseAuthExtensions.ReadOptions` は `internal`・KB アセンブリ所属で再利用できないため、
**同じ形をセクション名で一般化した 1 メソッドを PlatformShim 側へ置く**
（`PlatformRealmAuthExtensions.AddAiStockTradingPlatformRealmToken(builder, config, sectionName, tokenClientName)`）。
既存 3 型（Options / Provider / Handler）は**無改修で再利用**する。KB 側は本 PR では触らない
（KB の挙動は本件と無関係であり、共通化のための書き換えは射程外）。

### 2. 設定セクションは `LlmGateway:Auth`（KB と別セクション・AST レルムへフォールバックしない）

- `LlmGateway:Auth:TokenEndpoint`（明示・最優先） / `LlmGateway:Auth:Authority`（MSP レルム。未指定時の導出元）
  / `ClientId` / `ClientSecret` / `Scope` / `RefreshSkewSeconds`
- **AST の `Auth:Authority` へはフォールバックしない**（IADR-0093 決定3 と同じ理由。フォールバックすると
  AST レルムのトークンを MSP へ出して 401 になる＝直したい故障の再生産）
- **未設定なら何も付けない**（`IsEnabled` false → ハンドラを付けない）。現行挙動＝匿名呼び出しと**バイト等価**であり、
  基盤側がまだ認可を掛けていない間も壊れない。

### 3. 認可失敗は**独立した分類**にする（`ModelUnavailable` へ倒さない）

- `LlmFailureKind` に **`Unauthorized`** を足し、`Classify` が **401 / 403** をここへ倒す。
  429（`Retryable`）と同じく「400 系の内側だがモデル不可ではない」例外である。
- `HttpLlmCompletionClient` は `Unauthorized` のとき:
  - `TradeDecisionSkipped` を **publish しない**（軸 5 のとおり、通知の題名が
    「割当モデルが利用できません」で固定されており、事由を足しても誤帰属が別の層で再生産される）
  - Hold の理由を専用の定数 `HoldUnauthorized`（「LLM ゲートウェイの認可が拒否された（資格情報の不足）ため見送り」）
    にする。既存の `HoldModelUnavailable`（割当モデル不可）とも `HoldFallback`（伝送の失敗）とも区別する。
  - 警告ログで「資格情報 / 認可」を名指しする。
- `HttpReportNarrativeDrafter` は非 2xx のログへ分類（`kind`）を載せ、`Unauthorized` は
  資格情報を名指しする専用の警告にする。倒れ先はプレースホルダ散文のまま（安全側・変更しない）。

**上限（やらないこと）**: フォールバックの可否・Hold へ倒す方針そのものは変えない（`AST/ADR-0017` 決定2）。
新しい通知種別・新しい契約イベントは足さない。

### 4. 付与箇所は 2 つの名前付き `HttpClient` に閉じる

- `ReportService/Program.cs` の `"report-llm"`
- `TradeDecisionService/Program.cs` の `"llm"`

いずれも `.AddAiStockTradingPlatformRealmToken(cfg, "LlmGateway:Auth", "llm-gateway-token")` を鎖に足す。
provider は DI へ登録しない（`ServiceAuth` の `TryAddSingleton` と衝突させない＝IADR-0093 決定2 と同じ理由）。

### 5. 資格情報は**既存の MSP レルム客体を再利用**する

本リポジトリは既に MSP レルムへ到達するサービスアカウントを持っている
（confidential client **`ai-stock-trading-kb-writer`**。`ast-secrets` の `kb-auth-client-id` /
`kb-auth-client-secret`）。**新しい secret 鍵を作らず、この鍵を `LlmGateway__Auth__*` にも与える。**

- 利点: 運用者が投入する秘密が増えない。realm 側の差分も最小になる。
- 負債（明記する）: `ast-secrets` の鍵名が `kb-` 始まりのまま「AST ユニットの MSP レルム資格情報」を指す。
  **構成セクションは分けてある**ため、将来 LLM 専用クライアントへ分けるときは values の 2 行を差し替えるだけで済む
  （コード変更不要）。

### 6. realm 側に必要な変更（**MSP が所有する。本リポジトリからは行えない**）

隣接クローン `../microservices-platform` を**読み取りのみ**で実測した結果:

| 実測項目 | 値 | 出典 |
| --- | --- | --- |
| realm id | `platform` | `deploy/keycloak/microservices-platform-realm.json:2` |
| `ServiceCaller` ポリシーの中身 | `policy.RequireRole("platform-service")` | `src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Extensions/AuthExtensions.cs:103`（`PlatformAuthPolicies.ServiceRole = "platform-service"`・同 `:31`） |
| AST の既存 client | `ai-stock-trading-kb-writer`（confidential・`serviceAccountsEnabled: true`） | 同 realm json `:612` |
| その service account のロール | **`["platform-operator"]` のみ**（`platform-service` **なし**） | 同 realm json `:923-926` |

🔴 したがって、MSP#1364 が着地すると本リポジトリの呼び出しは
**「トークン無し → 401」だけでなく、上記クライアントで取ったトークンを付けても「role 不足 → 403」**になる。
**AST 側の配線だけでは通らない。**

**MSP へ依頼する変更（申し送り・1 行）**:

```
deploy/keycloak/microservices-platform-realm.json
  users[] の "username": "service-account-ai-stock-trading-kb-writer"
    "realmRoles": ["platform-operator"]
  →  "realmRoles": ["platform-operator", "platform-service"]
```

あわせて同 client の `name` / `description` が「KB writer」限定の記述になっているため、
**「AST ユニットの MSP レルム s2s 客体（KB 書き込み＋LLM ゲートウェイ呼び出し）」**へ広げてもらう。

- **なぜ新しい client を作らないか**: 本リポジトリは既に MSP レルムに到達する service account を 1 つ持ち、
  同じユニットの同じ信頼境界からの呼び出しである。client を増やすと運用者が投入する秘密が 2 つになり、
  取り違え（KB の秘密で LLM を叩く）の事故面が増える。
- **専用 client を選ぶ場合の代替案（MSP が最小権限を優先するならこちら）**:
  `ai-stock-trading-llm-caller`（confidential・`serviceAccountsEnabled: true`・service account の
  realmRoles は **`["platform-service"]` のみ**）。この場合 AST 側は
  **`LlmGateway__Auth__ClientId` / `ClientSecret` に別の `ast-secrets` 鍵を割り当てるだけ**でよく、
  **コード変更は要らない**（決定 2 でセクションを分けてあるのはこのためである）。
- **順序**: 本 issue（AST 側）を先に着地させる。AST 側は「未設定なら何も付けない」ため、
  realm 反映前にマージしても現行挙動と等価である。

## 実装対象（ファイル）

| # | ファイル | 変更 |
| --- | --- | --- |
| 1 | `backend/TestSupport/AiStockTrading.TestSupport.PlatformShim/Foundation/Auth/PlatformRealmAuthExtensions.cs` | 新規。セクション名で一般化した inline ハンドラ登録 |
| 2 | `backend/Shared/AiStockTrading.Shared.Contracts/Llm/LlmFailureClassification.cs` | `Unauthorized` を追加し 401/403 を分岐 |
| 3 | `backend/Services/TradeDecisionService/Infrastructure/ExternalServices/HttpLlmCompletionClient.cs` | `Unauthorized` の分岐・`HoldUnauthorized`・skip を出さない |
| 4 | `backend/Services/TradeDecisionService/Program.cs` | `"llm"` へトークン付与。匿名前提のコメントを是正 |
| 5 | `backend/Services/ReportService/Infrastructure/ExternalServices/HttpReportNarrativeDrafter.cs` | 非 2xx ログへ分類。`:16` のコメントを是正 |
| 6 | `backend/Services/ReportService/Program.cs` | `"report-llm"` へトークン付与 |
| 7 | `deploy/helm/.../values.yaml` / `values-local.yaml` / `README.md` | `LlmGateway__Auth__*` の配線 |
| 8 | `.env.example` / `docker-compose.yml` | 同上（compose 経路） |
| 9 | `docs/security/security.md` | 行番号の引き直し（規則 10） |
| 10 | `.ai-context/adr/IADR-0323_*.md` 新規 ＋ `IADR-0061` / `IADR-0071` へ日付つき追記 ＋ `README.md` 索引 | 決定の記録 |

## テスト（陰性対照つき）

**ハッピーパスだけの試験は今日でも通るため証拠にならない。** 各対象に「付いていること」と
「付いていなければ落ちること」の対を置く。

| # | 試験 | 置き場 |
| --- | --- | --- |
| T-1 | `LlmGateway:Auth` 設定時、`/complete` に `Bearer` が付く（trade-decision） | `TradeDecisionService.Tests/LlmGatewayAuthTests.cs` |
| T-2 | **陰性対照**: `LlmGateway:Auth` 未設定なら、同一コンテナに AST レルム `ServiceAuth` があっても `/complete` に `Authorization` が付かない（レルム取り違えの防止） | 同上 |
| T-3 | `LlmGateway:Auth` 設定時、`/complete` に `Bearer` が付く（report） | `ReportService.Tests/LlmGatewayAuthTests.cs` |
| T-4 | **陰性対照**: 同上（report・未設定なら付かない） | 同上 |
| T-5 | **陰性対照**: 401 は `ModelUnavailable` に分類されない（`Unauthorized`）。403 も同じ。429 / 400 / 404 の既存境界は不変 | `Shared.Contracts.Tests/LlmFailureClassificationTests.cs` |
| T-6 | **陰性対照**: 401 応答で `TradeDecisionSkipped` が **publish されない**・Hold の理由に「割当モデル」が現れない | `TradeDecisionService.Tests/.../HttpLlmCompletionClientTests.cs` |
| T-7 | 403 も同じ扱い（境界の対） | 同上 |
| T-8 | **陰性対照**: 401 応答で report の警告が「資格情報 / 認可」を名指しし、倒れ先はプレースホルダのまま | `ReportService.Tests/.../HttpReportNarrativeDrafterTests.cs` |
| T-9 | **突然変異検査**: 付与の 1 行を外すと T-1 / T-3 が落ちることを実走で確かめ、出力を PR に貼る | 手動（報告に出力を残す） |

## 受け入れ基準

- [ ] `LlmGateway:Auth` が設定されているとき、両サービスの `/complete` 要求が `Authorization: Bearer` を持つ
- [ ] 未設定なら現行どおり何も付けない（既定の非破壊）。AST レルムのトークンが漏れない
- [ ] 401 / 403 が `ModelUnavailable` に分類されない。Hold の理由に「割当モデルが利用できない」が現れない
- [ ] `TradeDecisionSkipped` が認可失敗で publish されない（通知の題名による誤帰属の再生産を防ぐ）
- [ ] 匿名前提のコメント 2 箇所（`HttpReportNarrativeDrafter.cs` / `TradeDecisionService/Program.cs`）が是正されている
- [ ] `values.yaml` / `values-local.yaml` / `.env.example` / `docker-compose.yml` / chart README が配線されている
- [ ] realm 側の申し送り（決定 6）が仕様書と issue の双方に書かれている
- [ ] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が通る

## 残余リスク

- **realm 反映までは 403 のままである。** AST 側だけでは疎通しない（決定 6）。反映前は本 PR 前と同じく
  安全側（Hold / プレースホルダ）へ倒れるが、**理由の記録は「認可」に変わる**——これが本 PR の主目的である。
- **`ast-secrets` の鍵名が `kb-` のままである**（決定 5 の負債）。将来 LLM 専用 client へ分けるときは
  values の 2 行の差し替えで済む。
- **既存の裸の `ADR-0010`（MSP の ADR を指す）は残る。** 別 issue へ切り出す（除外表）。

## ［2026-09-10 追記 / #724］着地と、基盤側の受け皿

PR AST#725 で着地した。**基盤側の受け皿も揃っている** —— 本仕様書が引き継ぎとして挙げた
「サービスアカウントにロールが足りず 403 になる」件は、基盤側で**専用クライアント
`ai-stock-trading-llm-caller`（`platform-service`）を新設**して解決した（MSP#1368）。

既存の `ai-stock-trading-kb-writer` へロールを足す案は採られていない。`platform-service` は
基盤内部の 10 サービスが持つロールで、あの主体へ足すと**文書 API 以外の東西端点すべてへ届く**ためである。
**用途ごとに主体を分けたので、片方の失効がもう片方を巻き込まない。**

マージ順序は realm（MSP#1368）→ 本 PR（AST#725）→ 認可（MSP#1365）で守られた。
