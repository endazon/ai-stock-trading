---
title: FRED_API_KEY を US 株取引の必須前提として明記し、k8s-local-deploy.sh の ast-secrets 無言破壊を止める
type: spec
status: review
related_ids: [FR-10, FR-17, NFR, ADR-0004, IADR-0052, IADR-0107]
author: endazon (with Claude Code)
created: 2026-07-28
updated: 2026-07-28
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
---

# 仕様書: 運用フットガン 2 件の是正（`FRED_API_KEY` の位置づけ明記・`ast-secrets` の保持）

> Issue [#262](https://github.com/endazon/ai-stock-trading/issues/262)（docs）/
> [#263](https://github.com/endazon/ai-stock-trading/issues/263)（fix）。いずれも live 検証（経路B・SIMULATE）で
> **実際にブロッカー化した運用上の落とし穴**であり、#260 / #261（[IADR-0107](../adr/IADR-0107_base-currency-conversion.md) /
> [IADR-0108](../adr/IADR-0108_simulator-risk-profile.md)）の後続。
>
> **実弾は撃たない。** 本作業は docs と**ローカル向けデプロイスクリプト**に閉じ、アプリケーションコード・
> chart の描画・実弾 triple-latch（`Broker__Provider=paper` / `Broker:Moomoo:TrdEnv=simulate` / 起動時 real 拒否・
> [IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)）には一切触れない。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-10（リスク統制＝統制上限は基準通貨で判定する）、FR-17（全体前提条件）、NFR（運用・再現性）
- 計画書: 05_trading-assumptions.md（計画リポ） §3
  （基準通貨 = JPY／レート取得元 = 日銀API または FRED）、
  ADR-0004（計画リポ）（案A+ の無料ソース）
- 関連 IADR: [IADR-0052](../adr/IADR-0052_k8s-helm-chart-shared-infra.md)（k8s/Helm chart・`ast-secrets` 手動投入）、
  [IADR-0068](../adr/IADR-0068_live-quote-feed-finnhub-extraction.md)（provider 選択・構成不備は警告して no-op）、
  [IADR-0078](../adr/IADR-0078_config-info-self-report.md)（`GET /internal/introspection` の自己申告）、
  [IADR-0094](../adr/IADR-0094_local-infra-observability-gitops.md)（`ast-secrets` の Vault 同期・opt-in）、
  [IADR-0100](../adr/IADR-0100_route-b-values-local-standing-config.md)（経路B の恒常設定 `values-local.yaml`）、
  [IADR-0102](../adr/IADR-0102_discord-env-ids-via-values.md)（環境固有 ID は values 経路）、
  [IADR-0107](../adr/IADR-0107_base-currency-conversion.md)（基準通貨換算・レート無しは見送り）。
  本作業で新規 [IADR-0109](../adr/IADR-0109_deploy-secret-preservation.md)（#263 の方式決定）
- 対象 Issue: #262 / #263（起票の起点は #257・IADR-0052）

## 目的・背景

### #262: `FRED_API_KEY` が「任意の収集ソース鍵」として書かれている

[IADR-0107](../adr/IADR-0107_base-currency-conversion.md) 以降、統制の金額判定は基準通貨（JPY）で行い、
非基準通貨の銘柄は換算レートが解決できないと**新規建てを見送る**（決定3の fail-safe）。レート源は FRED の
`DEXJPUS` であり、`Fx:Provider=fred` ＋ `Fx:Fred:ApiKey`（＝`ast-secrets/fred-api-key` ＝ env `FRED_API_KEY`）が
揃わなければ `FxRateSourceFactory` は `NoOpFxRateSource` へ倒れる（起動は落とさない・警告のみ）。

すなわち **`FRED_API_KEY` は US 株を取引するための必須前提**になったが、
`deploy/helm/ai-stock-trading/README.md` L58 は `EDINET_SUBSCRIPTION_KEY` と同じ行で
「収集ソース（任意）」としか記していない。症状は「米国株だけ何も起きない」（日本株は定義上レート 1 のため無影響）
という**沈黙**の形で出るため、live 検証では原因特定に時間を要した。

### #263: `k8s-local-deploy.sh` が投入済みの鍵を無言で破壊する

現行の `scripts/k8s-local-deploy.sh` は `ast-secrets` を **env から毎回まるごと再作成**する
（`kubectl create secret ... --from-literal=...=${VAR:-} | kubectl apply -f -`）。このため鍵を export せずに
再実行すると、投入済みの `FINNHUB_API_KEY` / `FRED_API_KEY` / `KB_AUTH_CLIENTSECRET` / `DISCORD_*` 等が
**空で上書きされ、無言で失われる**。

空値でも Secret 自体は存在し `secretKeyRef.optional: true` で解決されるため Pod は起動し、各アダプタは
安全既定（no-op）へフォールバックする。結果は「デプロイは成功するのに、次のサイクルから外部連携（実市況・為替・
KB・Discord）が静かに止まる」＝**有効化したつもりで効いていない**状態である。live 検証ではこれを避けるために
`helm upgrade` を直叩きしており、**標準手順が使えない**状態になっていた。

## 範囲

### 対象（In scope）

| # | 対象 | 内容 |
| --- | --- | --- |
| 1 | `deploy/helm/ai-stock-trading/README.md` | `FRED_API_KEY` を独立行にし「US 株取引の必須前提」として説明。系列/鮮度/設定キー/観測ログ例/切り分け手順を追記（#262）。`ast-secrets` の再実行時の挙動も明記（#263 受け入れ基準4） |
| 2 | `docs/operations/operations.md` | 障害対応 Runbook に「米国株だけ約定も発注も起きない」「再デプロイ後に外部連携が静かに止まる」の 2 行を追加 |
| 3 | `docs/operations/vault-secrets-runbook.md` | `fred-api-key` が US 株取引の必須前提である旨を注記（鍵の供給元の単一情報源との整合） |
| 4 | `scripts/k8s-local-deploy.sh` | ast-secrets の同期を**再作成から差分パッチへ**変更。env 未設定キーは触らない／明示的な空指定で非空を消す場合は列挙して中断（`--force-empty-secrets` で許可） |
| 5 | `scripts/k8s-local-deploy.test.sh` | 上記挙動の自動テスト（`kubectl` スタブ・平文非出力の検査を含む） |
| 6 | `.github/workflows/ci.yml` | 上記テストを CI で実行するジョブ |
| 7 | `docs/adr/IADR-0109_deploy-secret-preservation.md` ＋ `docs/adr/README.md` | #263 の方式決定の記録 |

### 対象外（Out of scope）

- **アプリケーションコード（`backend/`）の変更**。FX 換算・統制・見送りの挙動は #260 / IADR-0107 のまま不変。
- **chart の描画変更**（`values.yaml` / `values-local.yaml` / templates）。本番描画はバイト等価。
- **`FRED_API_KEY` の実値の投入・鍵の配布**。値は利用者が端末外に出さずに与える（リポジトリ・ログに残さない）。
- **Vault（ESO）経路の既定変更**。`externalSecrets.appSecrets.enabled` は既定オフのまま（IADR-0094）。
- **1 注文金額上限に対する AAPL の数量 0 問題**（IADR-0107「判明した帰結」）。運用判断として #257 に残置。

## 方式

### #262: ドキュメントの是正

`README.md` の鍵一覧で `FRED_API_KEY` を `EDINET_SUBSCRIPTION_KEY` から分離し、`values-local.yaml` の節へ
「為替換算（US 株取引の必須前提）」の小節を新設する。記載する事実は**実コードを単一情報源**とする。

| 記載事項 | 実装上の根拠（single source of truth） |
| --- | --- |
| 系列 `DEXJPUS`（円/ドル・営業日次） | `FredFxRateSource.DefaultSeriesId` / `FredFxOptions.SeriesId` |
| 鮮度上限 7 日 | `FxOptions.MaxRateAgeDays = 7`（0 以下は既定へ丸め） |
| キャッシュ TTL 6 時間 | `FxOptions.CacheTtlSeconds = 21_600` |
| 設定キー `Fx__Provider` / `Fx__Fred__ApiKey` | `values-local.yaml`（`fred` ＋ `secretKeyRef: fred-api-key`） |
| 未設定時の警告ログ | `NoOpFxRateSource`（初回 1 回のみ）／`FxRateSourceFactory`（キー無し・未知 provider） |
| 見送りログ | `TradeDecisionService`「基準通貨への換算レートが解決できないため見送り」 |
| 検知点 `fx-rate` | `Program.cs` の `AddPort("fx-rate", FxRateSourceFactory.ResolveProvider(...))`（不備時は `none` を申告） |
| 日本株は無影響 | `NoOpFxRateSource`（`MarketCurrency.Base` は `FxRate.Identity`） |

### #263: `ast-secrets` を「差分パッチ」で同期する

キーごとに **env の指定有無**と**既存 Secret の値の有無**から決定する（詳細と代替案は
[IADR-0109](../adr/IADR-0109_deploy-secret-preservation.md)）。

| env の状態 | 既存 Secret の当該キー | 挙動 |
| --- | --- | --- |
| 未設定 | 非空 | **触らない（保持）**＝#263 の本丸。キー名のみ「保持」として表示 |
| 未設定 | 空/不在 | 従来どおり既定値で作成（多くは空・dev 既定を持つキーは dev 既定） |
| 非空を指定 | 任意 | 指定値で上書き（利用者の明示指定が権威） |
| **空を明示指定** | 非空 | **キー名を列挙して中断**。`--force-empty-secrets` を付けたときだけ空で上書き |
| 空を明示指定 | 空/不在 | 空のまま（実質 no-op） |

実装上の要点:

- 既存値の**読み出しはしない**。`kubectl get secret -o go-template` で「非空の値を持つキー名」だけを列挙する
  （平文は一度も変数へ載せない＝ログ・`ps`・シェル履歴のいずれにも出ない）。
- 書き込みは `kubectl patch --type=merge --patch-file`（**base64 の `data`**）で行う。パッチに載せないキーは
  API サーバ側で保持される。base64 化により値のエスケープ問題（`"` `\` 改行・Discord のフレーズ等）も消える。
- パッチファイルは `umask 077` の一時ディレクトリに置き、`trap` で確実に削除する（コマンドライン引数に
  平文が載る `--from-literal` を廃する副次効果）。
- Secret が存在しない新規環境では空の Secret を作ってからパッチする（後方互換）。
- テスト容易性のため、同期処理を関数へ切り出し `AST_DEPLOY_LIB=1` で source したときは手順を実行しない。

## 受け入れ基準（Issue の基準に対応）

### #262

- [ ] README の鍵一覧で `FRED_API_KEY` が「任意（収集ソース）」ではなく **US 株取引の必須前提**として説明されている
- [ ] 系列（`DEXJPUS`）・鮮度上限（7 日）・設定キー（`Fx__Provider` / `Fx__Fred__ApiKey`）が記載されている
- [ ] 未設定時の観測ログ例と `introspection` の `fx-rate=none` による切り分け手順が記載されている
- [ ] 日本株（基準通貨）は本キー無しでも従来どおり取引できる旨が明記されている
- [ ] 障害対応 Runbook（運用仕様書）から同じ切り分けへ辿れる

### #263

- [ ] 鍵を export せずに再実行しても、既に投入済みの `ast-secrets` の値が失われない（自動テストで固定）
- [ ] 空上書きが避けられない場合は対象キー名を列挙して警告し、明示フラグ無しでは中断する（自動テストで固定）
- [ ] 新規環境（Secret 未作成）では従来どおり作成できる（自動テストで固定）
- [ ] 手順書（chart README）に鍵の供給元と再実行時の挙動が明記されている
- [ ] 平文の鍵をリポジトリ・ログへ出力しない（キー名のみ表示・自動テストで固定）

### 共通

- [ ] `dotnet build` / `dotnet test` は**変更なしで緑**（`backend/` に一切触れないため）
- [ ] CI（ci / security(gitleaks) / helm / doc-links / pr-title）が緑
- [ ] 実弾 OFF・SIMULATE 固定・chart の本番描画バイト等価が不変

## テスト方針

`scripts/k8s-local-deploy.test.sh`（Bash・`kubectl` スタブ・外部依存なし）で `sync_ast_secrets` の挙動を固定する。
CI（ubuntu-latest）は Bash を持つため追加の依存インストールは不要。`bats` は未導入・追加コストに見合わないため
標準 Bash のアサーションで書く。

| ID | ケース | 期待 |
| --- | --- | --- |
| T-263-01 | env 未設定・既存に非空値 | パッチに当該キーが**含まれない**（保持）・「保持」に列挙 |
| T-263-02 | env に非空値を指定 | パッチに当該キーが含まれ、値が base64 一致 |
| T-263-03 | env に空を明示指定・既存が非空 | **非ゼロ終了**（中断）・stderr にキー名・パッチは適用されない |
| T-263-04 | T-263-03 ＋ `--force-empty-secrets` | 空で上書きされる（明示的な意思表示） |
| T-263-05 | Secret 未作成（新規環境） | Secret を作成し、dev 既定を持つキーが既定値で入る |
| T-263-06 | 平文の非出力 | stdout/stderr に鍵の値が現れない（キー名のみ） |
| T-263-07 | env 空指定・既存も空/不在 | 中断しない（失うものが無い） |

## リスク・留意点

- **`kubectl patch --patch-file`** は kubectl 1.21+ で利用可。ローカル k3d / Rancher Desktop の同梱版は十分新しい。
- 旧経路（`kubectl apply`）で作られた Secret には `last-applied-configuration` 注釈が残るが、merge patch は
  これを参照しないため機能影響は無い（次回以降のパッチは注釈を更新しないだけ）。
- **明示的な空指定を中断にする**ため、「鍵を消したい」運用は 1 手増える（`--force-empty-secrets`）。
  無言破壊の再発コストの方が大きいという判断（IADR-0109）。
- ESO（Vault）同期を有効化した環境では `ast-secrets` は ExternalSecret が所有するため、本スクリプトの
  パッチは競合し得る。既定オフであり、経路B の手動 Secret 直運用が対象であることを README に明記する。
