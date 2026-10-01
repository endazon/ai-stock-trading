---
title: 出来高の設定の 2 か所の一致を描画で検査し、日足の自制レートの同時運用と急変の基準値の注記を残し、夜間の要約に LLM 費用の円と月次上限に対する使用率を出す（#1140）
type: spec
status: accepted
related_ids: [FR-04, FR-07, FR-09, FR-02, FR-03, FR-15, NFR, UC-02, ADR-0048, ADR-0023, IADR-0478, IADR-0467, IADR-0452, IADR-0462, IADR-0463, IADR-0218, IADR-0065, IADR-0058, IADR-0439]
author: claude (Claude Code)
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0048_decision-volume-from-daily-kline-within-existing-source.md (決定 4)
  - planning:projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md (決定 5)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§6・§6.1 月次 LLM 費用上限)
---

# 出来高の設定・日足の自制レート・急変の基準値・夜間の LLM 費用（#1140）

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-04（判断の材料）・FR-07（方針の改訂）・FR-09 / NFR（費用の統制と可視化）・FR-02 / FR-03 / UC-02（急変の判断）・FR-15（バックテストの過去データ源）
- 計画 ADR: ADR-0048 決定 4（方針の改訂 LLM へ判断へ渡る材料を示す）・ADR-0023 決定 5（日足の過去データ源）
- 関連する実装ADR: IADR-0467（出来高。決定 6・7 の 2 か所の設定）・IADR-0452（急変の基準値）・IADR-0463（LLM を呼ぶ前の見送り）・IADR-0462（夜間の要約）・IADR-0218（費用の対象範囲）・IADR-0065（上限は前提条件から）
- 新規 IADR: IADR-0478（項目 1・4）。項目 2 は IADR-0467、項目 3 は IADR-0452 への日付つき追記
- 起票: [#1140](https://github.com/endazon/ai-stock-trading/issues/1140)（監査 low・4 項目）

## 項目と対応

| # | 指摘 | 対応 | 置き場所 |
| --- | --- | --- | --- |
| 1 | `DecisionVolume__Enabled` を trade-decision と report の 2 か所で揃える運用に機械検査が無い | 描画済みの manifest で 2 つの実効値の一致（両方 true なら trade-decision の `OrderExecution__BaseUrl` が絶対 URL）を検査するスクリプトを足し、`helm.yml` が既定・values-local・全フラグ ON と正例・負例へ当てる | `scripts/check-decision-volume-parity.js`・`.github/workflows/helm.yml`・IADR-0478 決定 1 |
| 2 | 日足の自制レートが判断側（60 秒に 25 回）とバックテスト（既定 30 回/分）で共有されず同じ OpenD へ向く | 共有の自制は作らない（バックテストは既定で無効）。IADR-0467 に「同時運用は未解決」と追記し、バックテストを `moomoo` にするときの運用条件を Runbook に置く | IADR-0467 追記・`docs/operations/kline-quota-probe-runbook.md` |
| 3 | LLM を呼ぶ前の見送りが毎回続く銘柄は、基準値が古いまま急変が繰り返し発火し得る | 意図どおり。IADR-0452 に注記（コードは変えない） | IADR-0452 追記 |
| 4 | 夜間の要約が `LlmCostIncurred` の件数しか出さず、円の合計と月次上限の残りが読めない | §12（窓の円。用途 × モデル別と合計）と §13（当月の対象・対象外の累計、月次上限、使用率）を足す。上限は設定サービスの前提条件から読み、値を複写しない | `scripts/nightly-ledger-summary.sh`・Runbook・IADR-0478 決定 2 |

夜間の要約の生成場所は grep で特定した（`git grep -n "LlmCostIncurred" -- scripts docs` と `git grep -ln nightly-ledger-summary`）: **スクリプト `scripts/nightly-ledger-summary.sh`**（psql で監査台帳を読む。サービスや Runbook のクエリではない）。

月次上限の出所（単一情報源）: 実行時の値は**設定サービスの前提条件** `configuration_svc.assumptions`（単一行の JSON。`costLimits.llm`）。費用統制は `AssumptionsCostLimitsProvider` 経由で同じ行を読み、しきい値を判定する。
既定値 `TradingAssumptionsDefaults`（15,000）は行が無いときのシードであり、利用者が変えた後の値ではない —— **スクリプトは前提条件の行を読み、既定値も上限の数値も書かない**。
当月の累計は**費用統制の台帳** `cost_control_svc.cost_entries`（`CostGovernor.EvaluateLlm` が使う `GetMonthlyTotal(month, Llm)` と同じ行）。

## 母集合（規則 9。origin/develop 9441bee5。`.ai-context/specs/` は凍結記録のため除外）

### 項目 1: `git grep -n -i "DecisionVolume" -- ':!.ai-context/specs'`（15 ファイル）

| ファイル | 件 | 扱い |
| --- | --- | --- |
| `deploy/helm/ai-stock-trading/values.yaml` | 4 | 追随: 2 か所の注記に「一致は helm.yml の描画検査が止める」を足す（描画は変えない） |
| `docs/operations/kline-quota-probe-runbook.md` | 3 | 追随: 有効化の手順 1 に描画検査と手元での当て方を足す |
| `.ai-context/adr/IADR-0467_…md` / `.ai-context/adr/README.md` | 5 / 1 | 追随: 残余リスク「設定が 2 か所」に日付つき追記・索引行に追記 |
| `.ai-context/adr/IADR-0451_…md` | 1 | 除外: 出来高の行の切り替えの記録（2 か所の一致に触れない） |
| `backend/Services/TradeDecisionService/Program.cs`・`…/TradeDecisionAppService.cs`・`…/Tests/DailyBarsProviderSelectionTests.cs` | 4・1・2 | 除外: 読み手の挙動は変えない（検査は描画側） |
| `backend/Services/ReportService/Program.cs`・`PolicyRevisionPromptBuilder.cs`・`LlmReportPolicyReviser.cs`・テスト 3 本 | 2・3・4・2・2・4 | 除外: 同上 |
| `backend/Services/OrderExecutionService/Program.cs` | 1 | 除外: 注記だけ（日足の口は判断側の設定で呼ばれる） |
| appsettings*.json | 0 | どちらのサービスもこのキーを持たない（描画の env だけを見れば足りる根拠） |

値の出所の 2 軸目（規則 5）: chart のプロファイルは `deploy/helm/ai-stock-trading/values.yaml` と `values-local.yaml` の 2 つだけ（`ls deploy/helm/ai-stock-trading/`）。どちらも `DecisionVolume__Enabled` を置かない（既定＝両方 false）。

### 項目 2: `git grep -n "BudgetPerWindow\b\|Moomoo.*RequestsPerMinute\|BarData__Moomoo"` と `git grep -n "BarData__Provider\|BarData:Provider" -- docs deploy .ai-context/adr`

| 箇所 | 値 | 扱い |
| --- | --- | --- |
| `OrderExecutionService/…/DailyBarsQueryService.cs` `BudgetPerWindow = 25`・`BudgetWindow = 60 秒` | 25 回/60 秒 | 変えない（注記の「BacktestService の自制レート 30 回/分より内側」は事実のまま） |
| `BacktestService/…/BarDataOptions.cs` `MoomooBarDataOptions.RequestsPerMinute = 30` | 30 回/分 | 変えない |
| `values.yaml:298`・`values-local.yaml:469` `Backtest__BarData__Provider ""` | 空（none） | 現状の実害なしの根拠 |
| `docs/functional/FR-15_backtest.md`・`docs/blocked-tasks.md`・IADR-0105 / 0156 / 0157 / 0329 | — | 除外: 過去データ源の選定と既定の記録（同時運用に触れない） |
| `docs/operations/kline-quota-probe-runbook.md` | — | 追随: 「バックテストの日足を同じ OpenD から取るとき（同時運用は未解決）」を足す |

### 項目 3: `git grep -n "基準値" -- .ai-context/adr docs backend/Services/TradeDecisionService/Features`（LLM を呼ぶ前・見送りに触れる行）

| 箇所 | 扱い |
| --- | --- |
| IADR-0452 範囲の案 C（LLM を呼ぶ前の見送りで進めない理由） | 追随: 残余に日付つき追記（IADR-0463 の `EntryBlockedByRiskControls` も進めない帰結） |
| IADR-0471 `TradeDecisionHeld` は基準値を進める（保有中の銘柄は LLM を呼ぶ） | 除外: 保有中の銘柄は LLM を呼ぶので本件に当たらない（追記で範囲を「保有 0・未約定なし」と書く） |
| IADR-0463 | 除外: 基準値に触れていない（追記は IADR-0452 側に置き、IADR-0463 から引かれる必要はない） |
| クールダウン既定 | `MarketMonitorService/Domain/MonitorDefaults.cs` `Cooldown = 15 分`（追記の「既定 15 分」の出所） |

### 項目 4: `git grep -n "LlmCostIncurred" -- scripts docs`・`git grep -ln "nightly-ledger-summary" -- ':!.ai-context/specs'`・`git grep -n "台帳だけから\|監査台帳だけ" -- ':!.ai-context/specs'`

| 箇所 | 扱い |
| --- | --- |
| `scripts/nightly-ledger-summary.sh` | 実装: §12・§13・上限の読み（`nightly_cost_limit_sql`・`nightly_cost_sql`）。冒頭の「監査台帳だけから」「DB は audit_svc」を改める（規則 10） |
| `scripts/nightly-ledger-summary.test.sh` / `.pg.test.sh` | 試験: スタブを DB ごとに記録する形へ改め、§12・§13 を足す |
| `scripts/scripts.repo.test.js` | 試験: 序数の突き合わせ（T-10-2027）。nightly の 2 本の起動は既存 |
| `docs/operations/nightly-ledger-summary-runbook.md` | 追随: 冒頭の注記・前提（3 つの DB）・読み方 12・13・失敗の分岐 |
| `docs/operations/operations.md:534` | 追随（規則 10）: 「監査台帳だけから」→ LLM の費用を含む記述へ |
| `scripts/README.md:47` | 追随（規則 10）: 「だけから要約」の改め・試験件数（下の導出値） |
| `.ai-context/adr/IADR-0462_…md` | 除外: 凍結記録（自分の範囲〔§10・§11〕を正しく述べている） |
| `docs/tests/FR-10_risk-controls-tests.md:4090`「台帳だけから数えられる」 | 除外: §10・§11 の説明で、本件の後も正しい |
| `docs/operations/wolverine-queue-cleanup-runbook.md`・`check-consumer-endpoint-names.js`・`check-tracked-session-timeout.js`・`scripts.repo.test.js:291` の `LlmCostIncurred` | 除外: キュー名・テスト時間の記録（要約に関係しない） |
| `IADR-0461`・契約の注記の「監査台帳だけが購読する」 | 除外: 購読者の話で別の意味（語の一致だけ） |

## 規則 10（この変更で新たに誤りになる自分の記述・導出値）

- 「夜間の要約は監査台帳だけから」: スクリプト冒頭 2 か所・`scripts/README.md`・`operations.md`・Runbook 冒頭を改めた（上表）。
- 試験件数の導出値: `scripts/README.md` の「Bash テスト 20 件・実 PostgreSQL 15 件」は**着手前から古かった**（実測 21 / 36）。本件の後の実測（`bash scripts/nightly-ledger-summary.test.sh` → 29、`bash scripts/nightly-ledger-summary.pg.test.sh` → 43）へ計算し直した。
- 「合わせて 1 分に 55 回」: 25（60 秒＝1 分）＋ 30（既定）。
- 試験 ID T-10-2020〜T-10-2027 は IADR-0478・索引・`docs/tests/FR-10_risk-controls-tests.md`・`helm.yml` の注記・`scripts/README.md` で同じ範囲を書く（2020〜2023 が判定、2024〜2026 が要約、2027 が序数）。

## 窓（規則 11。項目 4 の §13 の「当月の累計」）

§13 は「当月の頭から窓の終端まで」の累計であり、時間の窓を持つ。プローブは実 PostgreSQL の試験（`nightly-ledger-summary.pg.test.sh`）に置いた。

| 形 \ プローブ | 増える側: 窓の終端ちょうど・後の計上（500。数えない） | 減る側: 当月の頭〜窓の前の計上（1,000。数える） | 前の月の計上（9,999。数えない） |
| --- | --- | --- | --- |
| **後の端だけ**（月は終端の UTC 月・`RecordedAt < 終端`）＝**採用** | ✅ | ✅ | ✅（月で切る） |
| 前の端だけ（`RecordedAt >= 窓の頭`） | ❌（終端後も数える） | ❌（月の頭からの分を落とす） | ✅ |
| 両端（`[窓の頭, 終端)`） | ✅ | ❌（月次の累計にならない） | ✅ |

月の境界: 費用統制は `MonthKey(clock.UtcNow)`（UTC の `yyyy-MM`）で計上する。§13 は窓の終端（現在時刻より後なら現在時刻）の 1 マイクロ秒前を UTC で月にする（終端ちょうどが月の頭でも前の月を指す）。項目 1〜3 は時間の窓を扱わない（規則 11 は当たらない）。

## 設計（要約。正は IADR-0478）

- 項目 1: 判定は `scripts/check-decision-volume-parity.js`（YAML の読みは `helm-release-drift.js` の `parseManifest` を再利用）。実効値は `bool.TryParse` と同じ読み。
  重複・平文でない値・Deployment の欠けは赤。`helm.yml` に 1 ステップ（起動条件は変えない）。
- 項目 4: §12 は監査の SQL の末尾。上限は `psql -d configuration_svc -A -t` で 1 行読み、数値でなければ空として警告。§13 は `psql -d cost_control_svc` に `to`・`llm_limit` を渡す。
  `NULLIF(:'llm_limit','')::numeric` で空を NULL にする（CASE の分岐の中でも定数の型変換は計画時に評価されるため、空文字を直に `::numeric` しない）。

## 試験（T-10-2020〜T-10-2027）

| ID | 置き場所 | 内容 |
| --- | --- | --- |
| T-10-2020〜T-10-2023 | `scripts/scripts.repo.test.js` | 判定（一致・片方だけ true・BaseUrl・読めない描画・main の終了コード） |
| T-10-2024〜T-10-2026 | `scripts/nightly-ledger-summary.test.sh`・`.pg.test.sh` | §12・§13（読み先・渡す変数・読み取り専用・上限の複写なし・読めない上限・上限 0） |
| T-10-2027 | `scripts/scripts.repo.test.js` | `CostCategory` の並びと SQL の序数 |
| `helm.yml` | 実 chart | 既定・values-local・全フラグ ON・正例 2・負例 4 |

## 自己変異（いずれも赤になること）

`docs/tests/FR-10_risk-controls-tests.md` の本件の節の表に結果を載せた（変異は 9 個。退避は作業ツリーの外へ複写して戻した）。

## 追随する文書

- `docs/operations/kline-quota-probe-runbook.md`（項目 1・2）・`docs/operations/nightly-ledger-summary-runbook.md`（項目 4）・`docs/operations/operations.md`・`scripts/README.md`
- `docs/tests/FR-10_risk-controls-tests.md`（T-10-2020〜T-10-2027）
- `.ai-context/adr/IADR-0467`・`IADR-0452` の追記と索引行、`IADR-0478` の新設と索引行

## 残余

- 描画だけを見る（稼働中の Pod の env は見ない）。`helm.yml` は検査のスクリプトの変更では起動しない。
- 日足の自制レートの共有は未解決のまま（バックテストを `moomoo` にするまでは当たらない）。
- 急変の繰り返しは意図どおりとして残す（回数が問題になったら市場監視側の抑制を別途検討）。
- §13 は計上時刻で切るため、窓の終端の近くで §12 と食い違い得る。3 つの DB を読める利用者が要る。
