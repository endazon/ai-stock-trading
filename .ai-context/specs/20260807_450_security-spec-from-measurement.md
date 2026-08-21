---
title: セキュリティ仕様書を実態の横断調査に基づいて記入する（空欄・未実装・未確認を書き分ける）
type: spec
status: review
related_ids: [NFR, FR-02, FR-08, FR-11, FR-14, FR-19, UC-06, UC-07, ADR-0003, ADR-0004, ADR-0012, IADR-0011, IADR-0019, IADR-0051, IADR-0056, IADR-0059, IADR-0060, IADR-0062, IADR-0072, IADR-0111, IADR-0164, IADR-0169, IADR-0171, IADR-0174, IADR-0175]
author: endazon (with Claude Code)
created: 2026-08-07
updated: 2026-08-07
---

# 仕様書: セキュリティ仕様書の記入（実測ベース）

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 非機能要件: **NFR（セキュリティ）** —— 発注機能へのアクセスは本人のみ・外部公開しない
- 計画 ADR: **ADR-0012（計画リポ）**（取引データの MCP 非公開）／ADR-0003（AI 判断のガードレール）
- 起点 issue: [#450](https://github.com/endazon/ai-stock-trading/issues/450)（由来: [#348](https://github.com/endazon/ai-stock-trading/issues/348) / [IADR-0171](../adr/IADR-0171_mcp-non-exposure-structural-guard.md)）

## 目的・背景

`CLAUDE.md` は `security` を**必須仕様書**（リポ単位・原則 1 つ）に挙げ、雛形自身も「**未記入のまま放置しない**」と書いている。しかし本書は **#348 で追加した「MCP への公開」節を除き雛形のまま**であった。

**#348 で埋めなかったのは意図的である。** 推測で埋めると、セキュリティ仕様書が「**書いてあるが実態と違う**」という最も悪い状態になる。暫定措置として冒頭に「**書かれていない ＝ 対策が無い、ではない**」という注意書きを置いた（実装には Keycloak / ABAC・TLS・シークレット管理が入っているため、空欄を「無対策」と読まれる害を避けた）。

**本作業はその横断調査を行い、実測で記入する。**

## 着手前の実測（2026-08-07・`677f2d6`）

**issue 本文の「やること」を鵜呑みにせず、コードと構成を自分で引いた。**

### 認証・認可 — **サービスによって適用状況が違う**

| サービス | 認証の登録 | 実測 |
| --- | --- | --- |
| MarketMonitor / Audit / RiskManagement / Report / Configuration / CostControl | `AddAiStockTradingAuth` | ✅ Keycloak OIDC/JWT。エンドポイントは `OwnerOnly` / `OwnerOrService` |
| Backtest | `AddAuthentication()` / `AddAuthorization()`（素） | **意図的**。認可を要する API が無く、共通ミドルウェアの依存だけ満たす（Program.cs にコメント有）。公開は health / introspection のみ |
| Notification / TradeDecision / OrderExecution | 登録なし | HTTP エンドポイントは health / introspection のみ |
| **InformationCollection** | **登録なし** | 🔴 **`POST /internal/collection/run-once` が無認証**である |

- ポリシーは 2 種（[IADR-0011](../adr/IADR-0011_foundation-min-port.md) / [IADR-0051](../adr/IADR-0051_service-to-service-auth.md)）。**書き込み系は `OwnerOnly` 据え置き＝サービスへ書き込み権限を与えない**（最小権限）。
- Keycloak の `realm_access.roles` は標準ハンドラが `ClaimTypes.Role` へ展開しないため `KeycloakRolesClaimsTransformation` で補う。`NameClaimType` を `preferred_username` にしないと**監査ログの subject が `anonymous` へ潰れる**。
- 🟡 **`RequireHttpsMetadata = false` / `ValidateAudience = false`** である。**書く。**

### データ保護 — **本リポジトリにはほぼ無い**

| 区分 | 実測 |
| --- | --- |
| 保存時暗号化 | **コードにも Helm にも記述が無い**（インフラ管掌・[#24](https://github.com/endazon/ai-stock-trading/issues/24)） |
| 通信時暗号化 | `UseHttpsRedirection` なし／接続文字列に `sslmode` なし。**チャートに Ingress も NetworkPolicy も無い**（Service はすべて ClusterIP） |

### 秘密情報管理 — **受け口はあるが Vault 化は未充足**

- 実体は `ast-secrets`（Kubernetes Secret）を `secretKeyRef`（`optional: true`＝fail-safe）で注入。
- `ExternalSecret`（Vault → k8s Secret）の**テンプレートはあるが既定 `enabled: false`**。テンプレート自身が「**受け口の用意は Vault 化の充足ではない**」「[IADR-0056](../adr/IADR-0056_moomoo-simulate-poc-complete-real-gated.md) §3 が実弾解禁の前提に挙げる『秘匿情報の Vault 化』は**未充足のまま**」と書いている。
- moomoo のパスワードは**平文を置かず MD5** を Vault へ格納する。
- CI: gitleaks／ローカル: `.claude/hooks/guard-secrets.js`（6 パターンをブロック・1 パターンを警告）。

### 監査ログ — **記録項目は実装済み・保持期間は未実装**

- `AuditEntry`（追記専用）: `Id`（冪等キー＝Wolverine `Envelope.Id`）／`EventType`／`CorrelationId`／`Symbol`／`Summary`／`Detail`（イベント全量 JSON）／`OccurredAt`／`RecordedAt`。
- `SettingsChangeEntry`: `Before` / `After` / `Actor` / `Reason` / `ChangedAt` / `SettingsChangeType`（`RequireActorAndReason` で Actor・Reason 必須）。
- 🔴 **保持期間を担保する仕組みが無い。** パージ（[IADR-0059](../adr/IADR-0059_dedupe-retention-purge.md)）の対象は**重複排除ストア 2 つだけ**であり、`audit_events` は明示的に対象外である。**「対象外」は「7 年保持が担保されている」ことを意味しない** —— 保持側の実装は [#346](https://github.com/endazon/ai-stock-trading/issues/346) の管掌である。

## 対象範囲

### 対象

| # | 変更 | 内容 |
| --- | --- | --- |
| 1 | `docs/security/security.md` | 5 節（認証・認可／データ保護／秘密情報管理／監査ログ／脅威と対策）を**実測で**記入する |
| 2 | 同 冒頭の暫定注意書き | **外す**（#450 の完了条件） |
| 3 | 同 `status` | `draft` → `review` |
| 4 | [IADR-0175](../adr/IADR-0175_security-spec-absence-notation.md)（新設） | **空欄・未実装・未確認・対象外の書き分け**を決める |

### 対象外（意図的にやらない）

- **見つかった不備の是正そのもの。** `/internal/collection/run-once` の無認証・保持期間の未実装・Vault 化の未充足は、いずれも**別 issue の担当**である。**本作業は「可視化」であって「是正」ではない** —— 混ぜると仕様書の記入が実装 PR に引きずられて止まる。
- **`docs/authz/` の新設**（現在 `.gitkeep` のみ）。認可モデルは 2 ポリシーで足りており、独立文書にする分量が無い。**本書の「認証・認可」節に書く**。
- **インフラ側の対策**（保存時暗号化・TLS 終端・NetworkPolicy）→ **#24**。

## 実装上の判断

| # | 判断 | 内容 |
| --- | --- | --- |
| 1 | **「空欄」を作らない** | 記入できない項目は**空欄にせず**「**未実装**」「**未確認**」「**対象外（管掌先）**」と書き分ける。空欄は「調べたが無い」と「調べていない」を区別できず、**読み手が前者だと誤読する**（#450 の要求そのもの） |
| 2 | **不備も書く** | `RequireHttpsMetadata = false` / `ValidateAudience = false`・無認証の `run-once`・保持期間の未実装は**そのまま書く**。**セキュリティ仕様書が「できていることの一覧」になると、できていないことを探す場所が無くなる** |
| 3 | **根拠をコードの所在で示す** | 各項目に実装ファイル／IADR を添える。#380 の教訓（**手順書の API パスを推測で書いて 🔴 を出された**）を、仕様書にも適用する |
| 4 | **新 IADR を 1 本起こす** | 判断 1 は「本書をどう読むか」を決める規約であり、**次に書く人が同じ書き分けをしなければ意味が消える**。IADR に残す |

## 受け入れ基準

- [x] 5 節が**実測に基づいて**記入されている（推測で埋めていない）
- [x] 記入できない項目が「**未実装**」「**未確認**」「**対象外**」で書き分けられ、**空欄が 1 つも無い**
- [x] **見つかった不備が本書に書かれている**（`RequireHttpsMetadata=false` / `ValidateAudience=false`・無認証の `run-once`・監査の保持期間未実装・Vault 化未充足）
- [x] 各項目に**コードの所在または IADR** が添えられている（**実測**: 「未実装／未確認／対象外」を含む全 22 箇所を `grep` で洗い、担当 issue・コードの所在・IADR のいずれも無い行が 0 件であることを確認した）
- [x] 冒頭の暫定注意書きが**外れている**
- [x] `status: draft` → `review`
- [x] 関連仕様書（`docs/operations/operations.md`）と相互リンクしている（**実測**: `security.md` → `operations.md` と `operations.md` → `security.md` が**双方向 1 件ずつ**。当初は片方向で、`operations.md` 側に「関連文書」節を新設して成立させた）
- [x] **不備は別 issue として起票**され、本書からリンクされている
- [x] `check-doc-links.js` / `dotnet build` / `dotnet test` が通る

## テスト方針

**本作業はコードを変更しないため、xUnit のテストは追加しない。**

**ただし「テストが無い＝検証していない」ではない。** 本書の主張は**すべて実測で裏を取った**（コード・Helm テンプレート・CI ワークフロー・フックを直接読んだ）。裏取りの経路は各節に**ファイル名で**書く —— **読み手が自分で再実測できることが、本書における「テスト」に相当する**。

**機械検査は導入しない。** 「仕様書の記述が実装と一致しているか」を機械で見るには実装側に検査点が要り、本作業の範囲（記入）を超える。**代わりに、陳腐化しやすい箇所を本書に明記する**（下記 残余リスク 1）。

## 残余リスク

1. **本書は 2026-08-07 時点の実測であり、実装が動けば陳腐化する。** とくに**認証を登録していない 5 サービス**は、後から HTTP エンドポイントを足したときに本書の記述が嘘になる。**機械検査は無い。**
2. **「未確認」と書いた項目は本当に未確認である。** 保存時暗号化・TLS 終端位置は本リポジトリからは決められず、**インフラ（#24）が入って初めて確定する**。**「未確認」を「たぶん大丈夫」と読まないこと。**
3. **不備を書いたことは、不備が直ったことではない。** 本作業は可視化であり、是正は各 issue の担当である。
