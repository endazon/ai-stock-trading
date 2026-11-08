---
title: 情報収集の日次要求見積りの肯定形の試験を Meter で隔離する（並行する結線の試験の発火を拾う揺らぎ）
type: spec
status: accepted
related_ids: [NFR, NFR-07, FR-01, ADR-0031, IADR-0255, IADR-0294, IADR-0437]
author: claude (Claude Code)
created: 2026-11-08
updated: 2026-11-08
---

# 作業仕様書 — 情報収集の業務メトリクスの試験の Meter 隔離（NFR）

> 着手 2026-11-08（develop `e1ee28a70`）。

## 起点

- MSP の run 42244797219 / job 127984074031（AST を submodule として走らせた結合の CI）で、
  `InformationCollectionService.Tests.InformationSourceFactoryDailyVolumeTests.暫定上限内の銘柄数は警告を出さないがメトリクスは記録する`
  が 1 回落ちた（180 ms。普段は 1 ms 未満）。
- 失敗の形（ログの原文）: `Expected capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate) to contain a single item
  matching (m.Value == 48), but 2 such items were found.`（78 行の `ContainSingle(m => m.Value == 48)`）。
- 起点 ID: 計画の非機能要件表に「試験の安定性」に当たる番号は無い（メタ作業。`traceability.md` の無採番 NFR の場合 2）。
  試験が固定している性質は FR-01・ADR-0031 決定2〜4・IADR-0294（日次要求見積りの記録）。

## 原因

- 当該試験は `new MeterCapture(BusinessMetricNames.MeterName)` と `new BusinessMetrics()`（既定の Meter 名）を使っていた。
  `MeterListener` はプロセス全体の同名の Meter を購読するため、**同じアセンブリで並行に走る別クラスの発火も拾う**。
- 混入元: `WatchlistFollowingCompositionTests.結線時は監視銘柄の米国の銘柄をトークン付きの照会で決めて収集する`。
  本番の組み立て（`Program.cs`）を起こし、`WatchlistFollowingSourceFetcher` の巡回ごとの記録
  `InformationSourceFactory.RecordCycleDailyVolumeEstimate` が DI の `BusinessMetrics`（既定の Meter 名）へ
  `ast.finnhub.daily_request_estimate` を刻む。同試験の構成は `Collection:PollIntervalSeconds = 3600`（24 巡回/日）・
  provider `finnhub`（1 要求/銘柄）・監視銘柄の米国 2 銘柄（AAPL・MSFT）なので **2 × 1 × 24 = 48**。当該試験の期待値 48
  （1 銘柄 × 1 要求 × 48 巡回）と偶然一致し、`ContainSingle` が 2 件になった。
- 同じクラスの否定形の 2 試験は #695 で `MeterCapture.NewIsolatedMeterName()` ＋ `BusinessMetrics.WithMeterName` に隔離済み
  だった。肯定形の 3 試験が残ったのは、`MeterCapture` の XML コメントが「肯定形は既定名のままでよい——『含む』の表明は壊れない」
  と書き、**`ContainSingle` のような件数を数える表明を肯定形に数えていた**ためである（`ContainSingle` は「含む」ではない）。
- 製品コードの誤りではない（巡回ごとに記録し直すのは IADR-0437 の設計どおり）。IADR は書かない（実装の判断を伴わない）。

## 母集合（規則 9・6）

`git grep -n "new MeterCapture(" backend`（定義を除く）を全件引き、Meter 名が既定（`BusinessMetricNames.MeterName`）の箇所を
選り分け、表明の形で分類した。隔離済み（`NewIsolatedMeterName` または独自の一意名）は対象外。

| 既定名の箇所 | 表明 | 判定 |
| --- | --- | --- |
| `InformationSourceFactoryDailyVolumeTests`（3 試験: 暫定上限内／finnhub_news／上限超過） | `ContainSingle(...)` | **件数を数える → 直す** |
| `BusinessMetricsTests`（Shared.Contracts.Tests。20 試験ほど） | `Equal` / `HaveCount` / `ContainSingle` / `BeEmpty` / `SumOf(...).Be(...)` | 除外: クラス冒頭の注記のとおり**本アセンブリで BusinessMetrics を使うのは本クラスだけ**（同アセンブリの他ファイルに `BusinessMetrics` の利用が無いことを grep で確認）。xUnit はクラス内を直列に走らせるため混入元が無い |
| `PositionRowToleranceCompositionTests` / `AuditEventConsumersTests`（2） / `CollectionPollingServiceTests`（2） / `StopOutReentryWiringTests` / `DecisionExitReentryWiringTests`（表明なし） / `TradeDecisionMadeConsumerTests`（2） / `InformationCollectedConsumerTests` / `PriceMovementDetectedConsumerTests` / `ProtectiveStopDriftAdopterCompositionTests` / `OrderApprovedConsumerTests`（4） / `LlmCostIncurredConsumerTests` | `Contain` / `NotBeEmpty` / `BeGreaterThan(0)` | 除外: 「含む」・下限だけの表明で、他人の測定値が混ざっても壊れない |

## 直し方

- `InformationSourceFactoryDailyVolumeTests` の肯定形 3 試験を、否定形と同じ形（`MeterCapture.NewIsolatedMeterName()` を
  `MeterCapture` と `BusinessMetrics.WithMeterName` の両方へ渡す）に揃える。期待値（48・96・`> 100`）と表明（`ContainSingle`）は
  そのまま。
- `MeterCapture.NewIsolatedMeterName` の XML コメントに「件数・合計を数える表明（`ContainSingle` / `HaveCount` / `Equal` /
  `SumOf(...).Be(...)`）も隔離する」を足す（誤りの源の是正。規則 10）。

## 再現の実測

- `dotnet test`（当該 2 クラスに絞る）30 回・全件 10 回・1 CPU に絞った全件 15 回: いずれも直す前で 0 赤（窓が数 µs で稀）。
- 一時ハーネス `TmpMeterFlakeStress`（コミットしない）: `WatchlistFollowingCompositionTests` の当該試験を別タスクで走らせ、
  それが終わるまで `InformationSourceFactoryDailyVolumeTests` の肯定形 3 試験の本体を同一プロセスで回し続ける。これを 20 回。

| 形 | 回 | 本体の実行数 | 赤（暫定上限内） | 赤（finnhub_news・上限超過） |
| --- | --- | --- | --- | --- |
| 直す前 | 1 | 73,143 | **6** | 0 |
| 直す前 | 2 | 75,792 | **5** | 0 |
| 直す前 | 3 | 76,101 | **3** | 0 |
| 直した後 | 1 | 82,092 | 0 | 0 |
| 直した後 | 2 | 81,621 | 0 | 0 |
| 直した後 | 3 | 84,339 | 0 | 0 |

## 受け入れ基準

- [x] 期待値を緩めない・skip しない（48 / 96 / `> 100` と `ContainSingle` を保つ）。
- [x] 直す前に並行で落ち、直した後に同じ条件で落ちないことを示す（上表）。
- [x] `dotnet build backend/backend.slnx` が警告 0・エラー 0。
- [x] `InformationCollectionService.Tests` 全件（695 件）を 20 回連続で緑。
- [x] `dotnet format backend/backend.slnx --verify-no-changes` が差分なし。
- [x] 文書の検査（trace ブロック・リンク・知識グラフ・計画 ID の修飾・クロスリポ参照）が通る。

## 計画書との差異

- 差異: なし（試験の観測方法だけを変える。製品コードは変えない）。
