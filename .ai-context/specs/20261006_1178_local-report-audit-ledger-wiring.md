---
title: 経路B（values-local）で報告書を監査台帳へ REST で結線し、日報の未供給 6 項目を解消する（#1178）
type: spec
status: accepted
related_ids: [FR-06, FR-11, FR-16, FR-10, NFR, IADR-0445, IADR-0199, IADR-0254, IADR-0269, IADR-0422, IADR-0429, IADR-0051, IADR-0100, IADR-0489]
author: claude (Claude Code)
created: 2026-10-06
updated: 2026-10-06
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-06, FR-11, FR-16)
---

# 仕様書: 経路B で報告書を監査台帳へ結線する（#1178）

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-06**（日報・週報・月報の生成）。供給元の出所は FR-11（監査台帳）・FR-16（判断根拠）・FR-10（損切りの実行機構）
- 関連 IADR: IADR-0199（為替の情報源）・IADR-0254（LLM 利用実績・借株料）・IADR-0269（判断根拠）・IADR-0422（承認の手法）・
  IADR-0429（発注執行の解決結果）・IADR-0445（段 3。既定 REST）・IADR-0051（s2s の OwnerOrService）・IADR-0100（values-local）・IADR-0489（env 配列の置き換え）
- 起票: [#1178](https://github.com/endazon/ai-stock-trading/issues/1178)（所有者の判断 2026-10-06 は issue 本文に記録済み。**新しい IADR は起こさない**＝新しい設計判断は無く、
  既存の設定点へ値を入れる構成変更である。IADR-0445 の「稼働中の配備は未構成」の記述へ日付つき追記を足す）
- ブランチ: `chore/FR-06-1178-local-report-audit-wiring`
- 基点コミット: `origin/develop` `b52dddcd`

## 目的・背景

日報で毎回「未供給」と出る 6 項目（為替の情報源の状態・LLM 利用実績・借株料の記録・判断根拠・損切りの実行機構〔承認の記録／発注執行の解決結果〕）は、
`backend/Services/ReportService/Program.cs:371-497` の供給元が `Audit:BaseUrl` 未設定で Unsupplied 実装を選ぶためである。chart は本番既定で
意図的に `Audit__BaseUrl` を置いていない（`deploy/helm/ai-stock-trading/values.yaml:594-595`）。PoC（経路B）では所有者が結線に同意した。

## 調査（基点コミット）

### 1. env 配列の合成

| 事実 | 出典 |
| --- | --- |
| env はテンプレートが `$svc.extraEnv` を順に描くだけ（マージの仕組みは無い） | `deploy/helm/ai-stock-trading/templates/deployment.yaml:222-244` |
| helm は values の配列を丸ごと置き換える。values-local の report の配列は既に本番既定の全 env を写し持つ | `values-local.yaml:238-` ／ helm.yml「Assert values-local drops no env from prod default」 |
| `Audit__BaseUrl` は Authority の導出（`$baseUrls`）に関与しない | `deployment.yaml:65-76` |

→ values-local の report の配列へ 1 行足すだけで足りる（既存の行を写し直す必要は無い）。

### 2. s2s の認可

| 事実 | 出典 |
| --- | --- |
| 報告書の `audit-ledger` クライアントは門＋サービストークンの鎖を持つ | `ReportService/Program.cs:363-365` |
| サービストークンは `ServiceTokenHandler` が Bearer で付ける（`ServiceAuth:ClientId/Secret`＋token エンドポイントが揃うとき） | `TestSupport/AiStockTrading.TestSupport.PlatformShim/Foundation/Auth/ServiceAuthExtensions.cs:14-37`・`ServiceTokenHandler.cs:10-20` |
| token エンドポイントは `global.authAuthority` から描画が導出（audit の `Auth__Authority` と同じ源＝issuer 一致） | `deployment.yaml:211-220` |
| `GET /audit/events/by-type` は **OwnerOrService**（他 2 本は OwnerOnly のまま） | `AuditService/Features/AuditEvents/GetAuditEventsByType/Endpoint.cs:39` |
| OwnerOrService＝ロール `trading-owner` **または** `trading-service`（azp の制限は REST に無い） | `PlatformShim/Foundation/Extensions/AuthExtensions.cs:83-84`・`:33-36` |
| 同じ client で報告書は既にリスク管理の OwnerOrService 群（建玉・約定・強制買戻し）を引いている（経路B で供給済み） | `RiskManagementService/Features/RiskManagement/RiskControlEndpoints.cs:70` ／ `values-local.yaml` report の `RiskManagement__BaseUrl` |
| サービスロールで by-type が引けることの試験は既にある | `AuditService/Tests/Features/AuditEvents/AuditQueryEndpointsTests.cs:120` |

→ コードの変更は不要。

### 3. 供給元と事象の生産者

| 供給元 | 引く種別 | 生産者（経路B） |
| --- | --- | --- |
| 為替の情報源の状態 | `FxRateSourceUsed`・`FxRateSourceFellBack`・`FxRateSourcePrimaryRestored`・`FxRateStale`・`PositionClosedWithStaleFxRate` | trade-decision（`FallbackFxRateSource` の使用記録・`FxSourceStatusTracker` の遷移・`PublishingFxSourceStatusNotifier`） |
| LLM 利用実績 | `LlmCostIncurred`・`LlmFallbackFired`・`TradeDecisionSkipped` | trade-decision・report（`PublishingLlmUsageReporter`／`PublishingLlmGovernanceReporter`） |
| 借株料 | `BorrowFeeAccrued`・`BorrowFeeAccrualUnavailable` | risk-management `BorrowFeeAccrualService`（**呼び出し元が無い**＝日次の計上は未結線。0 件） |
| 判断根拠 | `TradeDecisionMade` | trade-decision `TradeDecisionAppService` |
| 損切りの実行機構（承認の記録） | `OrderApproved` | risk-management `OrderScreeningService` ほか |
| 損切りの実行機構（発注執行の解決結果） | `StopLossMethodResolved` | order-execution（`StopLossMethodPolicy`・承認ごと） |

## 母集合（規則 9・10。`origin/develop` `b52dddcd`）

`git grep -n 'Audit__BaseUrl\|Audit:BaseUrl'`（`backend/` を除く）と `git grep -n '未結線\|chart に無'` で引いた。

| 箇所 | 扱い |
| --- | --- |
| `deploy/helm/ai-stock-trading/values.yaml:594-595` | 本番既定は不変。経路B だけ結線したことを追記 |
| `deploy/helm/ai-stock-trading/values-grpc-measurement.yaml:32-35` | 「REST も未結線だった」を profile 別に改める |
| `docs/operations/grpc-h2c-measurement-runbook.md:91-94`・`:137`・`:248` | #6 の性質（values-local では輸送の切り替え・基準窓あり）を改める |
| `deploy/helm/ai-stock-trading/README.md`（経路B の有効化の一覧） | 1 項目を足す |
| `.ai-context/adr/IADR-0445_audit-read-grpc-stage3.md:132` | 凍結。日付つき追記 |
| IADR-0199:166・IADR-0225:183・IADR-0429:115・IADR-0422:65 | 「未設定なら未供給」は条件つきの記述で今も正しい。変えない |
| `.ai-context/specs/` の該当行 | point-in-time の記録。変えない |
| `docs/api/east-west-grpc.md:258-264` | gRPC の既定の記述で今も正しい。変えない |

## 対象範囲

- `deploy/helm/ai-stock-trading/values-local.yaml`: report の `extraEnv` に `Audit__BaseUrl=http://audit-service:8080` を 1 行足す
- `.github/workflows/helm.yml`: 「本番既定に `Audit__BaseUrl` が無い／values-local では report-service だけが同値を持つ」の描画検査を足す
- `backend/Services/ReportService/Tests/AuditLedgerLocalProfileWiringTests.cs`: 経路B の構成で 6 供給元が監査台帳の実装になり、Bearer つきで by-type を引くことの試験
- 上の母集合の文書

範囲外: 本番既定の変更・gRPC の宣言・借株料の日次計上の結線。

## 受け入れ基準

- [x] values-local の描画で、report-service の env に `Audit__BaseUrl=http://audit-service:8080` が 1 本だけ増え、他の差が無い
- [x] 本番既定の描画はバイト等価
- [x] values-local ＋ 計測 overlay の描画でも差は同じ 1 本だけ（overlay の検査は緑のまま）
- [x] `Audit:BaseUrl` を経路B の値にすると 6 つの供給元が Http 実装になり、`audit-service:8080/audit/events/by-type` を `Bearer` つきで引いて非 null を返す
- [x] helm.yml の全ステップが緑（新しい検査は values-local の 1 行を外すと赤）

## 残余リスク

- 認可が通る前提は、`ast-secrets` の `service-auth-client-id` の client が realm ロール `trading-service` を持つこと（同じ client でリスク管理の照会が経路B で通っていることが傍証。クラスタでは未確認）。
- 借株料は結線後も 0 件（「記録なし」）。記録側の日次計上が未結線のためで、本件の範囲外。
- 為替の遷移・LLM のフォールバック・判断スキップは発生した日にだけ載る（無い日は「事象なし」で正しい）。

## 配備メモ（PoC）

- `scripts/k8s-local-deploy.sh`（`helm upgrade -f values-local.yaml`）で入る。差は report-service の env 1 本だけで、再起動するのは report-service だけ。
- 配備の前後に `node scripts/helm-release-drift.js --release ast --namespace ai-stock-trading --values deploy/helm/ai-stock-trading/values-local.yaml`。
- 確認: 次の日報で 6 項目の「未供給」警告が消えること。401/403 が report のログに出たら client のロールを疑う。
- 切り戻し: 1 行を消して再配備（6 項目が未供給へ戻るだけ）。
