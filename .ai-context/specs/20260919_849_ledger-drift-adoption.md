---
title: システム外の売買で生じた台帳とブローカーの乖離を、利用者の承認つきで台帳へ取り込む（自動では直さない）
type: spec
status: accepted
related_ids: [FR-10, FR-11, FR-06, FR-05, FR-09, FR-16, FR-20, UC-06, ADR-0003, IADR-0018, IADR-0033, IADR-0107, IADR-0117, IADR-0118, IADR-0124, IADR-0159, IADR-0210, IADR-0346, IADR-0350]
author: endazon (with Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10「生成AIはこれらを上書きできない」・FR-11 監査)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: 台帳とブローカーの乖離を利用者の承認つきで台帳へ取り込む（#849）

## 起点

- #849（bug・**最優先**）。稼働中の PoC で、利用者が moomoo アプリから直接全株を売却した（#847・#848 で手仕舞い API が
  使えなかった）。ブローカーの建玉は 0、**台帳は 3,381 株を保有したまま**で、段階資金の残枠が 332.47 USD まで尽き、
  **新規建てが 1 本も出ない**（`サイジングで数量 0 のため見送り`）。
- 先行: [IADR-0118](../adr/IADR-0118_broker-position-reconciliation.md)（乖離は検知・記録・通知のみ。**自動では是正しない**）。
  本作業はこの原則を**覆さない**。足すのは「利用者が承認したときだけ、観測値へ台帳を合わせる」経路である。
- 新規 IADR: **IADR-0350**（本作業の設計）。

## 射程

| 含む | 含まない（別 issue） |
| --- | --- |
| 最新の建玉観測の耐久保持（単一行） | 発注執行側の保護記録（`protective_stop_orders`・S0 / S1）の追随 —— **#858 へ分離**（本 PR はイベントで事実だけを流す） |
| 取り込み API（OwnerOnly・理由必須）と取り込み行（追記専用） | 報告書（日報・週報・月報）・統制状態の画面での「システム外の決済・損益不明」の明示 —— **#859 へ分離** |
| 射影（`PortfolioProjection`・`PortfolioValuation`）が取り込み行を数量だけで畳むこと | 台帳に**無い**建玉（`BrokerOnly`）・数量の**増加**・方向の反転の取り込み —— 取得単価も損切りも無い建玉を作るため**拒否する** |
| 監査イベント `PositionDriftAdopted`・監査台帳・Discord 通知 | 取り込みの取り消し（追記専用のため、誤りは逆向きの約定か次の観測で顕在化させる） |

## 母集合: 台帳の射影を読んでいる箇所（自分で引いた結果）

引き方: `grep -rn "GetFills()\|ProjectOpenPositions(\|PortfolioProjection.Project(\|IPortfolioStateProvider" backend --include=*.cs`
（テスト・obj を除外）。**台帳の読み口は `IPortfolioLedgerStore.GetFills()` の 1 点**であり、取り込み行をここへ
合流させれば下流は同じ列を読む。以下が全件である。

| # | 読み手 | 経路 | 取り込みで変わる値 | 対応 |
| --- | --- | --- | --- | --- |
| 1 | `LedgerPortfolioStateProvider` → `PortfolioProjection.Project` | 発注前スクリーニング・`sizing-context`・`status`・実DD サンプリング | `InvestedCapital`（段階資金）・`OpenPositionCount`・`UnrealizedPnl` が減る。`DailyRealizedPnl`・`Capital`・`ConsecutiveLosses`・`DailyOrderedAmount`・`SymbolsTradedToday` は**動かさない** | 取り込み行は**その時点の平均取得単価で在庫だけ減らす**（実現損益 0 を構造的に保証） |
| 2 | `PortfolioValuation.EquityHighWaterMark` | DD のピーク再計算 | 変えない（実現 0） | 同じ畳み込み規則 |
| 3 | `PortfolioProjection.ProjectOpenPositions` | 下の 4〜10 の共通入力 | 建玉が減る／消える（全量なら損切り価格も消える） | 同じ畳み込み規則 |
| 4 | `OpenPositionsService`（`GET /open-positions`。市場監視の損切り検知） | 3 | 実在しない建玉を監視しなくなる | 3 で足りる |
| 5 | `PositionCloseService` | 3 | 実在しない建玉の手仕舞い要求が 404 になる | 3 で足りる |
| 6 | `ShortSellingStatusService` | 3 | 空売り建玉の現況 | 3 で足りる |
| 7 | `MaintenanceMarginReductionService` | 3 | 自動縮小の対象 | 3 で足りる |
| 8 | `QuoteRefreshService` | 3 | 現在値を取りに行く銘柄 | 3 で足りる |
| 9 | `BuyInInferenceService` / `BuyInInference.CoveringFillsFor` | 3 ＋ 約定列 | 突合の根拠に「自らの決済指示」として**取り込み行を並べない** | `CoveringFillsFor` で除外（在庫の畳み込みには入れる） |
| 10 | `BrokerPositionsObservedHandler`（乖離検知自身） | 3 | 次の観測で乖離が解消し、追跡状態が初期化される | 3 で足りる（テストで固定） |
| 11 | `GET /risk-controls/fills`（`PeriodFillQuery`）→ ReportService（日報・週報・月報・三者比較・為替差損益） | 約定列そのもの | **取り込み行は約定ではない**。返すと「平均取得単価で売った約定（損益 0・勝率の分母）」として確定値のように集計される | **返さない**（除外）。報告書側の明示は #859 |
| 12 | 段階ゲート（`StagePerformance`） | 1 の `DrawdownRatio` を定時サンプリング | 取り込み後は含み損が消えるため、以後のサンプルが下がる。**過去に latch した最大 DD は消えない** | 変更なし（IADR に記載） |

除外したもの（台帳の射影を読まない）: Stage 1 の約定件数（`OrderExecuted` を直接数える）・GFV 計数（同）・
注文アクティビティ（相場操縦検知）・`IWorkingEntryOrderSource`（承認行のみ）。取り込みは `OrderExecuted` を発行しないため、
これらは動かない。

## 設計（詳細は [IADR-0350](../adr/IADR-0350_owner-approved-ledger-drift-adoption.md)）

1. **観測の保持**: `IBrokerPositionObservationStore`（単一行・最新のみ・逆行する観測は無視）。
   `BrokerPositionsObservedHandler` が観測のたびに記録する。**照会不能のとき観測は発行されない**ため、行が無い／古いことが
   そのまま「不明」を表す。
2. **API**: `POST /risk-controls/position-drift/adopt`（OwnerOnly）。本文は `{ symbol, market, reason }` のみ。
   **数量は受け取らない** —— 目標数量は最新の観測から決まる。
3. **拒否条件**（いずれも台帳を書かない）:
   観測が無い／古い（60 分超）・当該銘柄に乖離が無い（取り込み済みを含む）・その乖離がまだ報告されていない
   （連続観測条件を満たしていない＝一過性の未反映かもしれない）・増加／反転／台帳に無い建玉・観測より後に台帳が動いた・
   処理中の決済がある。
4. **実現損益**: **記録しない**（選択肢 a）。取り込み行は数量だけを持ち、射影が平均取得単価で在庫を減らす。
   参考として現在値から求めた**推定損益**を監査イベントと通知に載せるが、`推定・台帳へ未記録` と明示し、台帳の数値には入れない。
5. **冪等**: 目標は絶対値（観測の数量）であり差分ではない。2 回目は「乖離が無い」で拒否される。並行の二重投入は
   取り込み行の一意キー（銘柄・市場・取り込み前数量・観測数量・観測時刻）で 1 件に絞る。
6. **監査・通知**: `PositionDriftAdopted`（誰が・なぜ・取り込み前後の数量・観測値と観測時刻・実現損益を記録していないこと・
   推定の有無）。監査台帳へ記録し、Discord へ Critical で通知する。発注執行側が将来購読できるよう、保護記録の追随に要る
   情報（銘柄・市場・取り込み後の数量）を運ぶ。

## 受け入れ基準

- [ ] 利用者が乖離を確認したうえで、観測値へ台帳を合わせられる。取り込み後に `sizing-context` の段階資金残枠が回復する。
- [ ] サービストークンでは 403。理由なしは 400。観測が無い・古いときは 422。二重に取り込んでも台帳は壊れない。
- [ ] 取り込みは実現損益・当日発注累計・連敗・同日売買銘柄を動かさない。推定は推定と分かる形でしか残らない。
- [ ] 取り込みは監査台帳と通知に残る。自動では台帳を書き換えない（観測の購読は記録するだけ）。
- [ ] `GET /risk-controls/fills` は取り込み行を返さない。

## テスト

`T-10-440`〜（`docs/tests/FR-10_risk-controls-tests.md`）。否定形（サービストークン・観測なし／古い・理由なし・二重取り込み）
と、結果の assert（`sizing-context` の残枠の回復）を必ず含める。

## 検証

`dotnet build backend/backend.slnx` / `dotnet test`（RiskManagementService・AuditService・NotificationService・Shared.Contracts）/
`dotnet format --verify-no-changes` / `dotnet ef migrations has-pending-model-changes` / 文書系検査器。
