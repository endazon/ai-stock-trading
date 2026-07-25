---
title: 段階ゲートへの実DD（観測最大ドローダウン）供給ドライバ（#164 の in-repo 残作業）
type: spec
status: In progress
related_ids: [FR-15, FR-20, FR-10, UC-06, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-26
updated: 2026-07-26
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# 仕様書: 段階ゲートへの実DD供給ドライバ（#164 の in-repo 残作業）

> Issue [#164](https://github.com/endazon/ai-stock-trading/issues/164)。**バックテスト verdict の供給経路は
> [PR #198](https://github.com/endazon/ai-stock-trading/pull/198)（[IADR-0089](../adr/IADR-0089_backtest-verdict-supply.md)）で
> 配線済み**。本作業は、そこで「別ドライバの供給源」として意図的に据え置かれた**運用系フィールドのうち、
> go-live（実弾・実コンテナ E2E）に依存せず Risk 専有データだけで供給できる実DD
> （`StagePerformance.ObservedMaxDrawdownRatio`）の供給ドライバ**を実装する。
>
> **実弾には一切触れない。** 実弾 triple-latch（`Broker__Provider=paper` / `Broker:Moomoo:TrdEnv=simulate` /
> 起動時 real 拒否・[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)）は不変。本作業で追加する
> ドライバは **opt-in 既定無効**であり、既定構成の実行時挙動は変わらない。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-20（段階ゲート＝合格・撤退基準の運用）、FR-15（バックテスト＝Stage 0 verdict）、
  FR-10（リスク統制＝kill switch・時価評価による DD 算出）
- ユースケース（UC）: UC-06（緊急停止・段階操作）
- ADR: [ADR-0008](../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md)
  （段階ゲートとバックテスト。撤退基準＝実DD がバックテスト最大DD の 1.5 倍で自動停止・再検証）
- 計画書（技術検討）: [06_daytrading-review.md](../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md) §4
  段階ゲート運用の表（Stage 2/3 撤退基準）
- 関連 IADR:
  - [IADR-0070](../adr/IADR-0070_stage-gate-persistence-and-approval.md)（段階ゲートの運用系結線・`IStagePerformanceStore` の受け口）
  - [IADR-0089](../adr/IADR-0089_backtest-verdict-supply.md)（バックテスト verdict のイベント射影供給・フィールド所有権の分離）
  - [IADR-0083](../adr/IADR-0083_withdrawal-evaluation-driver.md)（撤退の定期評価ドライバ `WithdrawalEvaluationService`・opt-in）
  - [IADR-0085](../adr/IADR-0085_paper-withdrawal-notification-dedup.md)（非停止経路の降格提案通知・durable 重複排除）
  - [IADR-0066](../adr/IADR-0066_market-valuation-supply-and-gate.md)（時価評価＝`DrawdownRatio` の算出。既定無効）
  - 本作業で新規 [IADR-0103](../adr/IADR-0103_observed-drawdown-supply.md)
- 対象 Issue: [#164](https://github.com/endazon/ai-stock-trading/issues/164)（`Refs #164`）

## 現状（develop で実装済み／未実装の確定）

`develop`（`4fe0d02`）の実コードを走査して確定した。

### 実装済み（PR #198・IADR-0089）

| 経路 | 実体 |
| --- | --- |
| 契約イベント | `AiStockTrading.Shared.Contracts/Events/BacktestEvaluated.cs`（primitive・Domain 非依存） |
| 発行側 mapper | `BacktestService.Application/BacktestEvaluatedFactory.cs`（`Stage0Decision` → イベント・純関数） |
| 受信側射影 | `RiskManagementService.Worker/Composable/Steps/BacktestEvaluatedProjectionConsumer.cs` |
| 永続化 | `EfStagePerformanceStore` / `StagePerformanceRow`（7 フィールドすべて列あり） |
| 消費点 | `StageGate.AssessPromotion` / `RequestTransition` / `AssessWithdrawal` |
| 中央監査 | `BacktestEvaluatedAuditConsumer` + `AuditEntryFactory` |
| 撤退ドライバ | `WithdrawalEvaluationService`（#166・IADR-0083・opt-in 既定無効） |

### in-repo に残っていた穴（本作業の対象）

`IStagePerformanceStore.Save` を呼ぶ本番コードは `BacktestEvaluatedProjectionConsumer` **1 箇所だけ**であり、
`ObservedMaxDrawdownRatio` を書く実装が develop に**存在しない**。コード自身がそれを自認している。

- `BacktestEvaluatedProjectionConsumer.cs`: 「運用系フィールド（`ObservedMaxDrawdownRatio` …）は**別ドライバの供給源**」
- `WithdrawalEvaluationService.cs`: 「有効化しても既定 `StagePerformance`（**実 DD 未供給**…）では発火しないため
  **完全に不活性**。**実 DD 供給（別 issue）が結線されて初めて自動停止が作動する**」

結果として **ADR-0008 の撤退基準（Stage 2/3 の実DD 自動停止）が構造的に死んでいる**。これが本作業で塞ぐ穴である。

### go-live 依存で本作業の対象外（#164 / #82 に残す）

| フィールド | 段階ゲート上の役割 | 対象外の理由 |
| --- | --- | --- |
| `SlippageAndCostWithinExpected` | Stage 2→3 合格 | 実効スリッページは板を経ない paper 約定では構造的に発生しない。**Stage 2＝最小実弾**の観測を要する（go-live） |
| `DailyLossLimitRespected` | Stage 2→3 合格 | 同上。「日次損失上限の**運用実績**」は実弾運用の証拠であり、実弾 OFF 下では採取できない |
| `ControlViolationCount` | Stage 1→2 合格 | **計画側で定義が未確定**。06_daytrading-review §4 は「統制違反0件」とのみ記す。「統制が拒否した発注の件数」と読むとゲートが恒久ブロック、「統制を突破した件数」と読むと構造上常に 0 で無意味。実装側で勝手に定義しない（計画環流の候補として #164 に残す） |
| `PaperDeviationExplained` | Stage 1→2 合格／Stage 1 撤退 | 「乖離が説明可能か」は人間の質的判断であり、供給経路は承認 UI／Discord 側（別 issue） |
| 実 publish ホスト（`BacktestEvaluated` を実際に発行する BacktestService のホスト） | Stage 0→1 合格 | `BacktestService` は Domain＋Application のライブラリのみで API も publish も持たない。実 publish と実コンテナ E2E は #82／go-live |

## 目的

1. Risk 専有データだけから **実DD を段階別実績へ供給する**ドライバを設け、ADR-0008 の撤退基準を作動可能にする。
2. その際、[IADR-0089](../adr/IADR-0089_backtest-verdict-supply.md) が確立した**フィールド所有権の分離**
   （backtest 由来／運用系）を鏡像として守り、単一行の複数供給源を両立させる。
3. **fail-safe を一切崩さない**: 未供給時は既定（`ObservedMaxDrawdownRatio=0`）＝撤退非発火・`BacktestPassed=false`＝昇格拒否。
4. 既定構成の実行時挙動を**変えない**（ドライバ opt-in 既定無効・時価評価も既定無効で DD=0）。

## スコープ

### 対象

1. **純関数 `StagePerformanceProjection`**（`RiskManagementService.Application/Services`）
   - `WithObservedDrawdown(current, sampledDrawdownRatio)`: 実DD を**単調非減少**（`Math.Max`）で更新し、
     それ以外の 6 フィールドは温存する。負値・NaN 相当（負の比率）は 0 として無視する。
   - `WithoutObservedDrawdown(current)`: 実DD の観測窓をリセット（0 に戻す）。他フィールドは温存。
2. **`ObservedDrawdownRefreshService`**（`RiskManagementService.Worker/Composable/StageGate`）
   - `WithdrawalEvaluationService`（IADR-0083）と**同型**の `BackgroundService`。`PeriodicTimer` で定時、
     巡回ごとに DI スコープを作り scoped な `IPortfolioStateProvider` / `IStagePerformanceStore` を解決する。
   - 1 巡回: 休場日はスキップ → `IPortfolioStateProvider.GetCurrent().DrawdownRatio` をサンプリング →
     `StagePerformanceProjection.WithObservedDrawdown` で read-modify-write → 更新がある場合のみ `Save`。
   - 例外は捕捉して次周期へ縮退（1 巡回の失敗で常駐を落とさない）。多重起動は逐次 `await` で防ぐ。
   - `RunOnceAsync` を public にして単体テスト可能にする。
3. **`ObservedDrawdownRefreshOptions`**（`ObservedDrawdownRefresh` セクション）
   - `Enabled`（既定 `false`＝opt-in）、`IntervalSeconds`（既定 300）。
4. **降格受理時の観測窓リセット**（`StageGateService.RequestTransition`）
   - 承認による**差し戻し（Demotion）が受理されたときだけ**実DD をリセットする。
   - 理由: 実DD は単調非減少で累積するため、リセット経路が無いと「撤退→降格→再昇格」の直後に
     過去の実DD で撤退が**恒久的に再発火**する。差し戻し＝再検証のやり直しであり、観測窓もそこで区切るのが
     ADR-0008 の意図に合う。昇格側ではリセットしない（証拠を消さない＝厳しい側）。
5. **Program.cs 登録**（opt-in ゲート・`WithdrawalEvaluationService` と同型）。
   `appsettings*.json` には節を置かない（既定＝節不在＝`Enabled=false`）。これは兄弟の `WithdrawalEvaluation`
   （IADR-0083）と同じ扱いで、有効化は環境変数 `ObservedDrawdownRefresh__Enabled=true` による明示操作に限る。
6. **テスト**（受け入れ基準へ写像。後述）。

### 対象外

- 上記「go-live 依存で本作業の対象外」の 4 フィールドと実 publish ホスト（#164／#82 に残す）。
- `Shared.Contracts` の変更（新規イベント無し。本作業は Risk 内で完結する）。
- DB スキーマ変更（`StagePerformanceRow.ObservedMaxDrawdownRatio` は既存列。**EF Migration 不要**）。
- 段階ゲートの判定ロジック（`StageGate` 純ドメイン）の変更。

## 設計

### 供給方式: 同期照会でも s2s でもなく「Risk 内の定時サンプリング」

実DD は Risk 自身が所有するデータ（取引台帳＋時価評価）から導出できる（[IADR-0066](../adr/IADR-0066_market-valuation-supply-and-gate.md)
の `LedgerPortfolioStateProvider` が `PortfolioState.DrawdownRatio` として既に算出している）。したがって
**他サービスからの供給（s2s／イベント）は不要**であり、Database per Service（ADR-0001）を跨がない。

`ObservedMaxDrawdownRatio` は「**観測した**最大ドローダウン比率」であって、台帳の約定点だけから再計算できる値ではない。
約定と約定の間に発生する**含み損の谷**は、時価評価つきの `DrawdownRatio` を**定時サンプリングして最大値を latch する**
ことでしか捉えられない。よって台帳からの純再計算（stateless）ではなく、ストアへの単調 latch を採る。

### フィールド所有権（IADR-0089 の鏡像）

| フィールド | 供給源 |
| --- | --- |
| `BacktestPassed` / `BacktestMaxDrawdownRatio` | `BacktestEvaluatedProjectionConsumer`（backtest 由来） |
| `ObservedMaxDrawdownRatio` | **`ObservedDrawdownRefreshService`（本作業）** |
| `PaperDeviationExplained` / `ControlViolationCount` / `SlippageAndCostWithinExpected` / `DailyLossLimitRespected` | 未供給（#164／#82） |

双方向とも **read-modify-write** で自分の所有フィールドだけを更新し、他は `with` で温存する。

### 三重の安全（既定挙動はバイト等価）

1. `ObservedDrawdownRefresh:Enabled` 既定 `false` → ドライバの `HostedService` 自体が登録されない。
2. 仮に有効化しても `MarketData:EnableMarkToMarket` 既定 `false` では `DrawdownRatio` が常に 0
   （[IADR-0066](../adr/IADR-0066_market-valuation-supply-and-gate.md)）→ `Math.Max(0, 0) = 0` で `Save` も走らない。
3. 撤退の実行側 `WithdrawalEvaluation:Enabled` も既定 `false`（IADR-0083）。
   さらに `AssessWithdrawal` は `BacktestMaxDrawdownRatio > 0`（＝verdict 供給済み）でなければ発火しない。

実弾には一切触れない（`Broker` 系の設定・コードは不変）。

## 受け入れ基準 → テスト写像

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 実DD は単調非減少で更新される（小さい観測値で下がらない） | `StagePerformanceProjectionTests.実DDは単調非減少で更新する` |
| 2 | 負の観測値は 0 として無視する | `StagePerformanceProjectionTests.負の観測値は無視する` |
| 3 | 実DD 更新で backtest 由来・他運用系フィールドを上書きしない | `StagePerformanceProjectionTests.実DD以外のフィールドは温存する` |
| 4 | 観測窓リセットは実DD だけを 0 に戻す | `StagePerformanceProjectionTests.観測窓リセットは実DDのみを戻す` |
| 5 | ドライバが DD をサンプリングして段階別実績へ供給する | `ObservedDrawdownRefreshServiceTests.実DDを段階別実績へ供給する` |
| 6 | ドライバ経由でも回復で最大値が下がらない（無変化時は保存しない） | `ObservedDrawdownRefreshServiceTests.回復しても実DDの最大値は下がらない` |
| 7 | 供給で backtest 由来フィールドを上書きしない | `ObservedDrawdownRefreshServiceTests.実DD以外のフィールドは供給で温存する` |
| 8 | 休場日は評価しない（台帳・時価に触れない） | `ObservedDrawdownRefreshServiceTests.休場日はサンプリングしない` |
| 9 | 変化が無ければ `Save` しない（既定 DD=0 では不活性） | `ObservedDrawdownRefreshServiceTests.更新が無ければ保存しない` |
| 10 | 取得失敗時は既存値を維持し部分更新を残さない（fail-safe） | `ObservedDrawdownRefreshServiceTests.サンプリング失敗時は段階別実績を書き換えない_failsafe` |
| 11 | **実DD 未供給では撤退非発火 → 供給で撤退基準到達 → 自動停止**の通し（in-repo） | `ObservedDrawdownRefreshServiceTests.実DD供給で撤退基準に到達し自動停止する_通し` |
| 12 | **verdict 供給 → Stage 0→1 昇格受理**の通し（in-repo・#164 受け入れ基準 2） | `BacktestEvaluatedProjectionConsumerTests.合格verdict供給後にStage0から1への昇格が受理される` |
| 13 | 承認による差し戻し受理で観測窓がリセットされる（他フィールドは温存） | `StageGateServiceTests.差し戻し受理で実DDの観測窓をリセットする` |
| 14 | 昇格受理では観測窓をリセットしない（証拠を消さない） | `StageGateServiceTests.昇格受理では実DDの観測窓を保持する` |
| 15 | 受理されない遷移要求では観測窓を変更しない | `StageGateServiceTests.受理されない遷移では実DDの観測窓を変更しない` |

## 完了条件

- `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` が緑。
- `dotnet format` 適用済み・警告ゼロ。
- 既定構成での実行時挙動が不変（ドライバ既定無効・DD=0・実弾 OFF／SIMULATE 不変）。
- `docs/DEFINITION_OF_DONE.md` を満たす。
- [IADR-0103](../adr/IADR-0103_observed-drawdown-supply.md) に決定を記録する。

## 残課題（本 PR 外）

- `SlippageAndCostWithinExpected` / `DailyLossLimitRespected`（実弾観測が要る）→ #82／go-live。
- `ControlViolationCount`（「統制違反」の定義が計画側で未確定）→ #164 に残す（計画環流の候補）。
- `PaperDeviationExplained`（人間の質的判断の入力経路）→ 別 issue。
- `BacktestEvaluated` の実 publish ホストと実コンテナ E2E（供給→昇格の通し）→ #82。
