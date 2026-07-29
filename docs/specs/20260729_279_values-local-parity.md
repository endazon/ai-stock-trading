---
title: 経路B（ローカル SIMULATE）の本番パリティ — 実DD 供給と公式情報源（SEC EDGAR / FRED）を values-local で結線する
type: spec
status: review
related_ids: [FR-01, FR-10, FR-15, FR-20, UC-01, UC-06, ADR-0004, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-29
updated: 2026-07-29
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# 仕様書: 経路B の休眠機能のうち「外部資格情報が不要なもの」を values-local で結線する

> Issue [#279](https://github.com/endazon/ai-stock-trading/issues/279)（経路B SIMULATE の本番パリティ未達＝実運用機能の休眠の集約追跡）。
> **デプロイ構成（values プロファイル）の設定変更**であって機能追加・実弾化ではない。実装コード（C#）は 1 行も変えない。
> 本番 `values.yaml`・`templates/`・`Chart.yaml` は不変＝`helm template ast <chart>`（既定＝ArgoCD が描画する本番形）は
> **バイト等価**を厳守する。実弾の閂（[IADR-0111](../adr/IADR-0111_broker-tier-selection.md) の `broker.tier` 既定 paper・
> [IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)）には一切触れない。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-01（情報収集）、FR-10（リスク統制・時価評価/ドローダウン）、FR-15（バックテスト＝段階実績の入力）、FR-20（段階ゲート・撤退基準）
- ユースケース（UC）: UC-01（定時サイクル）、UC-06（統制状態の参照・段階運用）
- ADR: ADR-0004（情報源の選定＝案A+ の公式ソース）、ADR-0008（段階ゲートと撤退基準＝実DD の消費者）
- 関連 IADR:
  - [IADR-0064](../adr/IADR-0064_official-source-connectors.md)（公式コネクタ SEC EDGAR / EDINET / 日銀 / FRED・ソース単位の縮退）
  - [IADR-0103](../adr/IADR-0103_observed-drawdown-supply.md)（実DD 供給ドライバ `ObservedDrawdownRefresh`・単調 latch・3 段の opt-in）
  - [IADR-0083](../adr/IADR-0083_withdrawal-evaluation-driver.md)（撤退の定期評価ドライバ `WithdrawalEvaluation`・本作業で有効化する）
  - [IADR-0055](../adr/IADR-0055_llm-cost-metering-event.md)（LLM 費用計測・単価適用・`LlmCostIncurred` による月次計上）
  - [IADR-0097](../adr/IADR-0097_killswitch-disengage-confirmation-phrase.md)（kill switch の解除は確認フレーズ必須）
  - [IADR-0100](../adr/IADR-0100_route-b-values-local-standing-config.md)（経路B の恒常設定＝本作業が拡張する土台）
  - [IADR-0109](../adr/IADR-0109_deploy-secret-preservation.md)（`ast-secrets` の差分パッチ同期＝新規キーの追加先）
  - [IADR-0058](../adr/IADR-0058_helm-chart-ci-gate.md)（Helm chart の CI ゲート＝派生描画の検査）
  - 本作業で新規 [IADR-0114](../adr/IADR-0114_route-b-parity-observed-drawdown-and-official-sources.md)
- 対象 Issue: [#279](https://github.com/endazon/ai-stock-trading/issues/279)（傘）、[#164](https://github.com/endazon/ai-stock-trading/issues/164)（実DD 供給）、[#9](https://github.com/endazon/ai-stock-trading/issues/9)（情報収集の案A+）

## 背景 — 何が休眠しているか

経路B（ローカル k8s / SIMULATE）は [IADR-0100](../adr/IADR-0100_route-b-values-local-standing-config.md) で
①時価②実LLM③実KB＋Discord＋価格文脈まで恒常有効化した。しかし監査（#279）で、**実装済みだが現環境で未結線／無効化のまま**の
機能が残っていることが判明した。本作業はそのうち **外部資格情報が新たに要らないもの** を結線する。

| 休眠していた機能 | 現状 | 実効しない理由（実コードでの裏取り） |
| --- | --- | --- |
| 実DD（観測最大ドローダウン）の供給 | `ObservedDrawdownRefresh:Enabled` 既定 false | ドライバが `AddHostedService` されず、`IStagePerformanceStore` の `ObservedMaxDrawdownRatio` を**書く本番コードがバックテスト射影 1 箇所しか無い**（IADR-0103 の起点）。結果 ADR-0008 の撤退基準が構造的に発火し得ない |
| SEC EDGAR 収集 | `Collection__Source__Provider="finnhub"` のみ | `InformationSourceFactory` は列挙されていないソースを生成しない。加えて UserAgent／CIK の**両方**が非空でないと当該ソースを除外する |
| FRED 収集 | 同上 | 同上（`Fred:ApiKey` と `Fred:SeriesIds` の両方が要る。鍵は Fx 換算で既に投入済み） |

## 決定範囲（本作業で入れるもの）

`deploy/helm/ai-stock-trading/values-local.yaml` と `scripts/k8s-local-deploy.sh` のみを変更する。

### 1. 実DD 供給ドライバの有効化（FR-20 / FR-10 / ADR-0008）

`services.risk-management.extraEnv` へ `ObservedDrawdownRefresh__Enabled="true"` を追加する。

- **前提の充足**: IADR-0103 は「時価評価が既定無効のうちは `DrawdownRatio` が常に 0 で不活性」と述べるが、
  values-local では `MarketData__EnableMarkToMarket="true"` ＋ `MarketData__Provider="finnhub"` が既に真であり、
  本トグルを入れて初めて実DD がサンプリングされ台帳へ latch される。
- **台帳依存**: サンプリング元は `IPortfolioStateProvider.GetCurrent().DrawdownRatio`＝建玉台帳。
  経路B の既定ブローカは **paper**（`broker.tier` 既定・`values.yaml:54` 相当）で、擬似約定が台帳へ反映されるため実効する。
  **moomoo SIMULATE 経路では約定が台帳へ伝播しないため [#270](https://github.com/endazon/ai-stock-trading/issues/270) が入るまで不活性**（DD は 0 のまま＝安全側）。
- **撤退の実行側も有効化する（利用者承認済み）**: `WithdrawalEvaluation__Enabled="true"` を併せて投入し、
  ADR-0008 の撤退基準が実際に評価される状態にする。⚠️ 条件成立時は自動で kill switch が起動し、
  解除には確認フレーズが要る（[IADR-0097](../adr/IADR-0097_killswitch-disengage-confirmation-phrase.md)）＝
  **dogfood は人手で解除するまで停止する**。この代償を提示したうえで利用者が「入れる」と判断した（IADR-0114 決定3）。
- **ただし自動停止の発火範囲は実弾段階に限られる**（`StageGate.AssessWithdrawal` の実測）。`HaltNewEntries: true`＝
  kill switch 起動は **Stage 2/3 で「実DD ≥ バックテスト最大DD × 倍率」** のときだけで、Stage 1（ペーパー）は
  `Triggered: true` でも `HaltNewEntries: false`（降格提案＋通知のみ・IADR-0085）、Stage 0 は `Triggered: false`。
  現在の段階は Stage 0 で、Stage 2/3 は実弾未解禁（`LiveTradingReleased=false`・IADR-0111 閂0）のため到達不能＝
  **現構成では自動停止は起きない**。実利は「Stage 1 到達後の乖離検出」と「将来 Stage 2/3 で最初から効いていること」。

### 2. SEC EDGAR 収集の結線（FR-01 / ADR-0004 / IADR-0064）

`services.information-collection.extraEnv` を次のとおり変更する。

- `Collection__Source__Provider` を `"finnhub,sec-edgar,fred"` へ（カンマ区切り＝複数ソースの合成。`CompositeInformationSource`）
- `Collection__Source__SecEdgar__Ciks__0` を `"0000320193"`（Apple Inc. の CIK）へ。既存の watchlist／収集銘柄（AAPL）と揃える
- `Collection__Source__SecEdgar__UserAgent` を **`ast-secrets` の新規キー `sec-edgar-user-agent`（`optional: true`）由来**へ変更する

**UserAgent を values に直書きしない理由**: SEC は規約で**連絡先（実在のメールアドレス）入りの User-Agent** を必須とする
（IADR-0064 決定4 が構成必須化としてコード側に固定済み）。これは環境固有の個人情報であり、リポジトリへコミットすべきでない。
Discord の環境固有 ID（[IADR-0102](../adr/IADR-0102_discord-env-ids-via-values.md)）と同じ「運用者がデプロイ時に与える」性質だが、
本件は **`ast-secrets` 経由**にする（テンプレート改修が不要＝本番描画のバイト等価が自明に保たれる。IADR-0114 決定2）。

- fail-safe: キー未設定／空 → env が空 → `InformationSourceFactory` が当該ソースだけを警告つきで除外する
  （他ソース＝finnhub・fred は有効なまま。IADR-0064 決定1）。

### 3. FRED 収集の結線（FR-01 / ADR-0004 / IADR-0064）

- `Collection__Source__Fred__SeriesIds__0` = `"DEXJPUS"`（円/ドル為替レート）
- `Collection__Source__Fred__SeriesIds__1` = `"DGS10"`（米 10 年国債利回り・定数満期）
- API キーは既存の `ast-secrets/fred-api-key` を再利用する。**新規の資格情報を要求しない**
  （FRED キーは [#262](https://github.com/endazon/ai-stock-trading/issues/262) / IADR-0107 により**米国株取引の必須前提**として既に投入済み）。

### 4. LLM 費用の単価（NFR / IADR-0055 / IADR-0114 決定6）

`services.trade-decision.extraEnv` へ次を追加する。

- `LlmPricing__InputPer1kTokens` = `"0.819"`
- `LlmPricing__OutputPer1kTokens` = `"4.093"`

未設定（既定 0）だと `PublishingLlmUsageReporter` が毎回 ¥0 を計上し、月次費用上限（¥15,000）が構造的に発火しない。

**スキーマの実コード確認（投入前の必須事項）**: `LlmPricing` は **global 単一ペア**（per-model ではない）で、
単位は **円 / 1,000 トークン**（`LlmPricing.Compute` のコメント「いずれも円」）。`ParsePricePer1k` は
`InvariantCulture` で解析し正値でなければ 0 に倒す。したがって主用途 = trade-decision の実効モデル 1 つ分を投入する。

**値の根拠（2026-07 時点・恒久値ではない）**: opus 系 = 入力 $5 / 出力 $25（1M トークン）→ 1k 換算 $0.005 / $0.025
→ USD→JPY 換算 163.71（FRED `DEXJPUS`＝システムの為替源と同一系列）→ **0.81855 ≒ 0.819** / **4.09275 ≒ 4.093**（切り上げ側＝統制に安全）。
実効モデルは `Decision:PrimaryModel` 未設定＝ゲートウェイ既定 `claude-opus-5`（IADR-0101 / MSP ADR-0025）だが、
ADR-0011 が意図する `claude-opus-4-8` も同単価のため投入値は変わらない。sonnet-5 の $2/$10 は 2026-08-31 までの導入価格。

### 5. デプロイ手順（`scripts/k8s-local-deploy.sh`）

`AST_SECRET_KEYS` へ `sec-edgar-user-agent|SEC_EDGAR_USER_AGENT|`（空既定）を 1 行追加する。
[IADR-0109](../adr/IADR-0109_deploy-secret-preservation.md) の差分パッチ同期に自動的に従うため、
「export し忘れ＝現在値を保持」「明示的な空で既存値を消す場合だけ中断」という既存の不変条件はそのまま新キーにも適用される。

## 本作業で**入れない**もの（除外の根拠）

監査候補のうち、以下は「values を変えても実効しない」または「結線すると退行する」ことを実コードで確認したため除外する。
詳細な根拠は [IADR-0114](../adr/IADR-0114_route-b-parity-observed-drawdown-and-official-sources.md) 決定5 に記録する。

| 候補 | 除外理由（要約） |
| --- | --- |
| `KnowledgeBase__Search__BaseUrl`（RAG 検索） | 三重に不活性。①AST の検索クライアントが ABAC `Scope` を送らず platform 側 `HybridSearchService` が deny-by-default で常に空を返す ②`POST /documents` はカタログ登録のみで**本文が KB に無い** ③RAG 文脈のサニタイズが [#252](https://github.com/endazon/ai-stock-trading/issues/252) 未着手 |
| `MarketMonitor__BaseUrl`（watchlist 権威源） | market-monitor の watchlist は**空でシード**され、`HttpWatchlistProvider` は 200＋空配列を正常応答として `[]` を返す（fallback しない）。結線すると判断対象ゼロで取引サイクルが沈黙する |
| `Reconciliation__Enabled` | 経路B の既定 paper では no-op プローブ＝phase-4 自己修復のみ。巡回間隔は下限 1 時間にクランプされ短周期検証もできない。領域が [#270](https://github.com/endazon/ai-stock-trading/issues/270) と重複する |
| CronJob（`tradingCycle.cronjob.enabled`） | 既定 disabled が正。in-process ポーリング（IADR-0023）が現行の正経路で実害なし。有効化は run-once（#121）実装が前提 |
| Prometheus scrape target | AST chart に metrics/scrape 設定が**そもそも存在しない**。AST 側の values で是正できるものが無い（platform 側の課題） |

## 受け入れ基準

1. `helm template ast deploy/helm/ai-stock-trading`（既定＝本番形）の出力が**変更前後でバイト等価**である。
2. `helm template ast deploy/helm/ai-stock-trading -f values-local.yaml` に次が描画される。
   - `ObservedDrawdownRefresh__Enabled` = `"true"`（risk-management）
   - `Collection__Source__Provider` に `sec-edgar` と `fred` が含まれる
   - `Collection__Source__SecEdgar__Ciks__0` = `"0000320193"`
   - `Collection__Source__SecEdgar__UserAgent` が `ast-secrets/sec-edgar-user-agent` の `secretKeyRef`（`optional: true`）である
   - `Collection__Source__Fred__SeriesIds__0` = `"DEXJPUS"` / `__1` = `"DGS10"`
3. 既定描画に上記の有効化痕跡が**現れない**（本番へ漏れていない）。
4. values-local 描画で実弾／危険既定が OFF のまま（`Broker__Provider=paper`・`kind: ExternalSecret` 不在・`name: opend` 不在）。
5. values-local 描画に `WithdrawalEvaluation__Enabled` = `"true"` と `LlmPricing__InputPer1kTokens` = `"0.819"` /
   `LlmPricing__OutputPer1kTokens` = `"4.093"` が現れ、既定描画には**いずれも現れない**。
6. values-local の `extraEnv` が本番 `values.yaml` の env 名を**1 つも落としていない**（Helm のリスト置換による欠落の防止）。
7. `scripts/k8s-local-deploy.test.sh` が緑で、`SEC_EDGAR_USER_AGENT` について IADR-0109 の不変条件（保持・上書き・空中断・新規作成）が成り立つ。
8. `dotnet build` / `dotnet test` が緑（コード無改修のため回帰が無いことの確認）。

## テスト方針

- **Helm 描画検査**（`.github/workflows/helm.yml`）: 既存の 2 ステップ
  「Assert prod default excludes route-B activations」「Assert values-local activates route-B features」へ
  受け入れ基準 2〜5 の検査を追加する。加えて **env 欠落検出**（基準 6）のステップを新設する。
  単価は値そのもの（`0.819` / `4.093`）を照合し、桁誤り・単位取り違え（1M 単価の直投入・USD のまま投入）を落とす。
- **Bash テスト**（`scripts/k8s-local-deploy.test.sh`）: `SEC_EDGAR_USER_AGENT` の保持／上書き／空中断／新規作成を追加する
  （実クラスタ不要。`kubectl` スタブ＋`AST_DEPLOY_LIB=1` の既存ハーネス）。
- **C# テスト**: コード無改修のため新規は書かない。既存の全テストが緑であることを回帰確認に用いる。

## 影響範囲

| 対象 | 変更 |
| --- | --- |
| `deploy/helm/ai-stock-trading/values-local.yaml` | risk-management に 1 キー追加、information-collection の 4 キーを変更／追加 |
| `scripts/k8s-local-deploy.sh` | `AST_SECRET_KEYS` に 1 行、先頭コメントに新変数の説明 |
| `scripts/k8s-local-deploy.test.sh` | 新キーのテストケース追加 |
| `.github/workflows/helm.yml` | 描画検査の追加 |
| `deploy/helm/ai-stock-trading/README.md` | 運用手順（`SEC_EDGAR_USER_AGENT`・実DD 供給）の追記 |
| バックエンド（C#） | **変更なし** |

## 未決事項（本 PR の対象外）

1. LLM 単価は **2026-07 時点の公開単価と為替 163.71** に基づく点推定であり恒久値ではない。
   為替・公開単価の変動、および `Decision:PrimaryModel` を sonnet 等へ切り替える場合は再計算が要る（#243）。
   sonnet-5 の $2/$10 は 2026-08-31 までの導入価格。
2. report-service の実 LLM 散文費用は計上経路自体が無いため、単価を入れても**実消費より少なく**見積もられる（#282）。
3. 日銀（BOJ）収集は**資格情報不要**だが、`Boj:Db`（統計分類）の値を一次ソースで確認できていないため見送る
   （IADR-0064 決定5「推測実装をしない」に従う）。
4. `LlmPricing` は global 単一ペアのため、複数モデルを使い分けるなら計上側が応答の `Model` を見て単価を引く改修が要る（本作業の範囲外）。
