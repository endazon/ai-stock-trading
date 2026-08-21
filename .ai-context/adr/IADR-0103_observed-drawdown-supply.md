---
title: IADR-0103 実DD（観測最大ドローダウン）は Risk 内の定時サンプリングで段階別実績へ供給する
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-15, FR-20, UC-06, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-26
updated: 2026-07-26
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
  - planning:projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# IADR-0103: 実DD（観測最大ドローダウン）は Risk 内の定時サンプリングで段階別実績へ供給する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-26
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-20（段階ゲート）、FR-15（バックテスト verdict）、FR-10（リスク統制・時価評価）、UC-06（緊急停止・段階操作）、
  ADR-0008（計画リポ）（段階ゲートと撤退基準）
- 対象 Issue: [#164](https://github.com/endazon/ai-stock-trading/issues/164)
- 関連する実装仕様書: [20260726_observed-drawdown-supply-for-stage-gate](../specs/20260726_observed-drawdown-supply-for-stage-gate.md)
- 関連 IADR: [IADR-0089](IADR-0089_backtest-verdict-supply.md)（backtest verdict のイベント射影供給・フィールド所有権の分離）、
  [IADR-0070](IADR-0070_stage-gate-persistence-and-approval.md)（段階ゲートの永続化と承認）、
  [IADR-0083](IADR-0083_withdrawal-evaluation-driver.md)（撤退の定期評価ドライバ・opt-in）、
  [IADR-0085](IADR-0085_paper-withdrawal-notification-dedup.md)（非停止経路の降格提案通知）、
  [IADR-0066](IADR-0066_market-valuation-supply-and-gate.md)（時価評価＝`DrawdownRatio` の算出・既定無効）、
  [IADR-0018](IADR-0018_portfolio-ledger-projection.md)（取引台帳からの純射影）

## 背景・課題

[PR #198](https://github.com/endazon/ai-stock-trading/pull/198)（IADR-0089）で backtest verdict の供給経路
（`BacktestEvaluated` → Risk の射影 → `IStagePerformanceStore`）は配線された。しかし段階別実績 `StagePerformance` の
**運用系フィールドは「別ドライバの供給源」として意図的に据え置かれた**ままであり、`develop` で
`IStagePerformanceStore.Save` を呼ぶ本番コードは backtest の射影 1 箇所しか無い。

結果として `ObservedMaxDrawdownRatio`（実DD）が恒久的に 0 であり、**ADR-0008 の撤退基準
（実DD ≥ バックテスト最大DD × 1.5 で自動停止・Stage 0 再検証）が構造的に発火し得ない**。
`WithdrawalEvaluationService`（#166/IADR-0083）自身がそれを自認している。

> 有効化しても既定 `StagePerformance`（実 DD 未供給…）では発火しないため完全に不活性。
> **実 DD 供給（別 issue）が結線されて初めて自動停止が作動する。**

実DD の供給は、他の運用系フィールドと違って **go-live（実弾・実コンテナ）に依存しない**。DD は Risk が自ら所有する
データ（取引台帳＋時価評価）から導出でき、`LedgerPortfolioStateProvider` が `PortfolioState.DrawdownRatio` として
既に算出している（IADR-0066）。したがって本 issue の in-repo 残作業として先に閉じられる。

## 決定

### 1. 供給方式は「Risk 内の定時サンプリング＋単調 latch」とする（s2s・イベントを使わない）

`ObservedDrawdownRefreshService`（`BackgroundService`）が定時に `IPortfolioStateProvider.GetCurrent().DrawdownRatio`
を読み、`IStagePerformanceStore` の `ObservedMaxDrawdownRatio` を `Math.Max` で単調 latch する。

**なぜサンプリングか（台帳からの純再計算を採らない理由）**: `ObservedMaxDrawdownRatio` は「**観測した**最大 DD」であり、
取引台帳の約定点だけからは再計算できない。約定と約定の間に生じる**含み損の谷**こそが DD 監視の対象であり、
それは時価評価つきの現在 DD を定時に読んで最大値を latch することでしか捉えられない。`PortfolioValuation.EquityHighWaterMark`
のような台帳ベースの stateless 再計算は、約定と約定の間の谷を構造的に取りこぼす。

**なぜ s2s・イベントを使わないか**: 供給に必要なデータはすべて Risk 専有（取引台帳・時価評価）であり、
他サービスへの同期照会もイベント購読も不要。Database per Service（ADR-0001）を跨がない。

### 2. フィールド所有権を分離する（IADR-0089 の鏡像）

単一行 `StagePerformance` は複数の供給源が更新する。各供給源は **read-modify-write** で自分の所有フィールドだけを
`with` 更新し、他は温存する。射影は純関数 `StagePerformanceProjection` に単一情報源として置く。

| フィールド | 所有する供給源 |
| --- | --- |
| `BacktestPassed` / `BacktestMaxDrawdownRatio` | `BacktestEvaluatedProjectionConsumer`（IADR-0089） |
| `ObservedMaxDrawdownRatio` | **`ObservedDrawdownRefreshService`（本 ADR）** |
| `PaperDeviationExplained` / `ControlViolationCount` / `SlippageAndCostWithinExpected` / `DailyLossLimitRespected` | 未供給（後述の「対象外」） |

### 3. 実DD は単調非減少（latch）とし、リセットは「受理された差し戻し」のみとする

資金が回復して現在 DD が下がっても、過去に到達した谷は撤退基準の実測値として消さない（安全側）。
負値（異常入力）は 0 として無視する。

ただしリセット経路が皆無だと「撤退 → 承認による降格 → 再昇格」の直後に過去の実DD で撤退が**恒久的に再発火**する。
そこで `StageGateService.RequestTransition` が**差し戻し（`Demotion`）を受理したときだけ**観測窓をリセットする。

- 差し戻しは ADR-0008 の「Stage 0 で再検証」そのものであり、観測窓もそこで区切るのが意図に合う。
- **昇格側ではリセットしない**（撤退の証拠を消さない＝厳しい側）。
- リセットは台帳追記の後（＝遷移確定後）に行い、拒否された遷移要求では観測を変更しない。
- リセットは利用者承認（OwnerOnly の遷移要求）を経た操作のみに紐づき、自動処理が観測を消すことはない。

### 4. 既定は無効（opt-in）とし、既定構成の実行時挙動を変えない

`ObservedDrawdownRefresh:Enabled` は既定 `false` で、`appsettings*.json` に節を置かない（節不在＝無効）。
これは兄弟の `WithdrawalEvaluation`（IADR-0083）と同じ扱いで、有効化は環境変数
`ObservedDrawdownRefresh__Enabled=true` による明示操作に限る。

**自動停止までに 3 つの明示的な有効化を要する**（多重の安全弁）。

1. `ObservedDrawdownRefresh:Enabled=true` — 本ドライバの起動（既定 false）
2. `MarketData:EnableMarkToMarket=true` — DD の算出そのもの（既定 false・IADR-0066。無効なら DD は常に 0 で書き込みも起きない）
3. `WithdrawalEvaluation:Enabled=true` — 撤退の定期評価（既定 false・IADR-0083）

加えて `AssessWithdrawal` は `BacktestMaxDrawdownRatio > 0`（＝verdict 供給済み）でなければ発火しない。
実弾 triple-latch（IADR-0060）には一切触れず、SIMULATE 固定は不変である。

### 5. 取得失敗時は既存値を維持する（fail-safe）

サンプリングが例外を投げた巡回では**書き込みを行わない**（部分更新を残さない）。例外は `ExecuteAsync` のループが
捕捉して次周期へ縮退する（IADR-0083 と同型・1 巡回の失敗で常駐を落とさない）。単調 latch なので落とした巡回の
観測は次周期以降で回復する。休場日はサンプリングしない（`IBusinessCalendar` ガード・#21 と同型）。

無変化の巡回では `Save` を呼ばない（単一行への無用な更新を出さない）。

### 6. 本 issue の残りフィールドは供給しない（go-live 依存・仕様未確定）

| フィールド | 残す先 | 理由 |
| --- | --- | --- |
| `SlippageAndCostWithinExpected` | #82／go-live | 実効スリッページは板を経ない paper 約定では構造的に発生しない。Stage 2＝最小実弾の観測を要する |
| `DailyLossLimitRespected` | #82／go-live | 「日次損失上限の**運用実績**」は実弾運用の証拠であり、実弾 OFF 下では採取できない |
| `ControlViolationCount` | #164（計画環流の候補） | 計画（06_daytrading-review §4）は「統制違反0件」とのみ記す。「統制が拒否した発注の件数」と読むとゲートが恒久ブロック、「統制を突破した件数」と読むと構造上常に 0。**実装側で定義を発明しない** |
| `PaperDeviationExplained` | 別 issue | 「乖離が説明可能か」は人間の質的判断であり、供給経路は承認 UI／Discord 側 |
| `BacktestEvaluated` の実 publish ホスト | #82／go-live | `BacktestService` は Domain＋Application のライブラリのみ。実 publish と実コンテナ E2E は #82 |

### 7. 単一行への複数ライタは「担当列の分離」で整合させ、楽観排他トークンは導入しない

本 ADR で単一行 `StagePerformance` へのライタが 1 つ（backtest 射影）から 3 つ（＋実DD ドライバ・差し戻しリセット）に増える。
`StagePerformanceRow` には楽観排他トークンが無いため、read-modify-write のロスト・アップデートを検討した。

**担当列が異なるライタ間では起きない（実測で確認）。** 各ライタは担当外フィールドを「読んだ値のまま」書き戻すため、
EF Core の変更追跡が**実際に値の変わったプロパティだけを UPDATE に含める**。古いスナップショットを持つライタが
他ライタの確定値を巻き戻すことはない。この契約は回帰テスト
`EfStageGateStoreTests.別供給源の同時_read_modify_write_で担当外フィールドを失わない` で固定した
（A が T0 に読む → B が verdict を確定 → A が実DD だけ保存 → B の確定が残ることを検証）。

**前提条件（新しいライタを足すときの注意）**: この保証は「**読みと書きを同一 DI スコープ（同一 `DbContext`）で行う**」
ことに依存する。`EfStagePerformanceStore.Save` の `Find` が識別マップにヒットして `GetCurrent()` 時点の
オリジナル値をスナップショットとして保つためである。別スコープで読んだ値を持ち込んで保存すると `Find` が DB を
再読し、差分が**全列 Modified** になって担当外列まで UPDATE に載る。現行のライタ（`ObservedDrawdownRefreshService.RunOnceAsync`・
scoped な `StageGateService`・`BacktestEvaluatedProjectionConsumer`）はいずれも同一スコープ内で読んで書いている。
`EfStagePerformanceStore.Save` にこの前提をコメントとして残した。

**同一列（実DD）の競合＝「差し戻しリセット」と「サンプリング」の交錯のみが残る。** これは**安全側に倒れる**ため許容する。

- 実DD ドライバは `Math.Max` で**決して値を下げない**。よってリセットが打ち消される最悪ケースでも、
  残るのは「実際より高い（古いピークを含む）実DD」であり、撤退はより起こりやすくなる＝保守側。
  逆向き（撤退が起きにくくなる方向）の取りこぼしは構造的に生じない。
- **打ち消しは自然回復しない**（重要）。latch は毎巡回 DB の現在値を読んで `Math.Max` を取るため、
  打ち消された古いピークは**次に受理される差し戻しまで残り続ける**。すなわちこの競合が起きた場合、
  決定3 が避けようとした「再昇格直後の恒久再発火」がその周回では実際に起こり得る。回復には**再度の差し戻し**
  （Stage 0 まで戻っている場合は一度昇格してから差し戻す）を要する。方向は保守側のため安全性は損なわれないが、
  運用上は「撤退が想定より早く出た場合は差し戻しをやり直す」という手当てが要る点を明記しておく。
  リセットが競合せず永続した通常経路は
  `ObservedDrawdownRefreshServiceTests.差し戻しリセット後は現在DDから観測しなおす` で固定している
  （打ち消し経路は本テストの対象外＝許容する制約であることの明示）。
- 影響が現れる時期は**差し戻し先の段階による**。`AssessWithdrawal` の実DD 判定は Stage 2/3 でしか評価されないため
  （`StageGate.cs`）、差し戻し先が Stage 0／Stage 1 なら打ち消しが起きても影響は再び Stage 2 へ昇格するまで現れない。
  ただし `RequestTransition` は差し戻し方向に段数制約を持たない（昇格のみ 1 段ずつ）ため **Stage 3 → Stage 2 の
  差し戻しが可能**であり、この場合の着地先 Stage 2 は実DD 判定の対象段階なので、打ち消しが起きると**再昇格を待たず
  着地直後に撤退が再発火し得る**（回復には Stage 2 → 1/0 の再差し戻しを要する）。方向は保守側のままである。
- 差し戻しは利用者承認を要する稀な手動操作で、ドライバは既定 opt-in 無効。同時実行の機会自体が小さい。

**楽観排他トークン（`RowVersion`/`IsConcurrencyToken`）を追加しない理由**: `EfRiskSettingsStore`／`EfStageGateStore` は
利用者の設定変更同士の競合を 409 で返すために採用しているが、本行では**新たな失敗モードを作る**。差し戻しの
リセットは `ledgerStore.Append`（＝遷移の確定）の**後**に走るため、ここで `DbUpdateConcurrencyException` を投げると
「段階遷移は確定済みなのに API が 409 を返す」という誤った応答になる。上記のとおりロスト・アップデートの実害は
保守側に限定されるため、トークン追加のコスト（スキーマ変更＋確定済み操作の誤エラー化）に見合わない。
既に 0 の実DD はリセットで書き込まない（未記録の実績行を差し戻しだけで作らない）ことで書き込み自体も減らしている。

## 検討した代替案

- **台帳からの stateless 再計算**（`EquityHighWaterMark` の拡張で最大 DD を算出）: 約定間の含み損の谷を取りこぼす。
  DD 監視の目的に対して系統的に過小評価する（＝撤退が遅れる）ため不採用。
- **`WithdrawalEvaluationService` にサンプリングを同居させる**: 「観測（供給）」と「判定（自動停止・通知）」を
  1 つの常駐に混ぜると、片方だけを有効化できず opt-in の粒度を失う。責務も寿命も異なるため別ドライバとした。
- **OwnerOnly の供給エンドポイント（利用者が実DD を手入力）**: 撤退基準の実測値を人手申告にすると統制が形骸化する。
  自動計測できる値を手入力に落とさない。
- **昇格時も観測窓をリセット**: 撤退の証拠を消して緩む方向。安全側（厳しい側）に倒し、差し戻しのみに限定した。
- **リセットを一切しない**: 「撤退 → 降格 → 再昇格」で撤退が恒久再発火し、段階ゲートが運用不能になる。

## 影響・トレードオフ

- **良い点**: ADR-0008 の撤退基準が初めて作動可能になる。供給が Risk 内で閉じるため可用性結合・s2s 認証・
  スキーマ追加（EF Migration）のいずれも不要。fail-safe（未供給＝撤退非発火）は保たれる。
- **トレードオフ**: 観測は巡回間隔（既定 300 秒）の粒度に量子化され、巡回と巡回の間の瞬間的な谷は捉えられない。
  撤退は「緩やかな安全チェック」であり過頻度のポーリングは避ける方針（IADR-0083）に沿う判断だが、
  実運用で粒度が不足するなら `IntervalSeconds` を下げる（構成のみで調整可能）。
- **トレードオフ**: 実DD の観測窓は「差し戻しで区切る」という運用規約に依存する。段階ごとの観測窓を独立に持つ
  設計（`StagePerformance` に段階を持たせる）は列追加＝スキーマ変更を伴うため採らなかった。
- `Shared.Contracts` は不変（新規イベント無し）。DB スキーマも不変（`ObservedMaxDrawdownRatio` は既存列）。
