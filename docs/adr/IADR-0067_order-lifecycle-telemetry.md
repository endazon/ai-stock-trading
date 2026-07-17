---
title: IADR-0067 注文履歴テレメトリは「イベント追加＋Risk 専有 DB への射影」で供給し、訂正・取消の口はペーパー専用ポートに閉じる
type: impl-adr
status: Accepted
related_ids:
  - FR-19
  - FR-05
  - FR-11
  - UC-01
  - UC-02
  - ADR-0001
  - ADR-0002
  - ADR-0003
  - ADR-0007
  - IADR-0006
  - IADR-0016
  - IADR-0018
  - IADR-0019
  - IADR-0040
  - IADR-0057
author: claude
created: 2026-07-17
updated: 2026-07-17
plan_refs:
  - "../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md (FR-19, FR-05, FR-11)"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md (取引ガード)"
  - "../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md (Database per Service)"
---

# IADR-0067: 注文履歴テレメトリは「イベント追加＋Risk 専有 DB への射影」で供給し、訂正・取消の口はペーパー専用ポートに閉じる

- 状態: Accepted
- 日付: 2026-07-17
- 決定者: endazon / claude
- Issue: #154（Refs #49）

## 起点・関連

- 関連する計画書 ID: FR-19（相場操縦とみなされ得る発注パターンの禁止）／FR-05（発注執行）／FR-11（全イベントの時系列記録）／ADR-0007／ADR-0003／ADR-0002
- 関連する実装仕様書: [20260717_154_order-lifecycle-telemetry.md](../specs/20260717_154_order-lifecycle-telemetry.md)
- 関連 IADR: IADR-0040（検知アルゴリズム・`IOrderActivitySource` の契約）／IADR-0018（台帳射影の先行事例）／IADR-0016（安全既定ペーパー）／IADR-0019（監査台帳）

## コンテキストと課題

#49（FR-19）の相場操縦検知アルゴリズムは IADR-0040 で実装済みだが本番 DI 登録されていない。入力 `IOrderActivitySource` に実データを供給できないためである。供給には注文ライフサイクル（発注・訂正・取消・終端）の履歴が要るが、`Shared.Contracts.Events` には**訂正・取消のイベント契約が存在しない**。

決める必要があるのは次の3点である。

1. **供給経路**: Risk はどこから注文アクティビティを読むか
2. **訂正・取消の口**: `IBrokerAdapter` に生やすか、別ポートにするか（実弾を撃たない fail-safe をどう担保するか）
3. **ペーパー経路で訂正・取消をどう成立させるか**: `PaperBrokerAdapter` は常に即時終端（`Filled`/`Rejected`）を返すため、取消も訂正も構造的に成立しない

なお本決定の適用範囲は**配管**（発生したら発行・永続化・供給される）までであり、**取消・訂正を起こすトリガ（実ユースケース）は含まない**（#141 の自動リコンサイル基点・#152 の pause 強制取消・時限取消は各 issue に残す）。

## 検討した選択肢

### 1. 供給経路

| 選択肢 | 評価 |
| --- | --- |
| **(A) Risk 専有 DB への射影**（採用） | `IOrderActivitySource` の同期契約に自然に適合。ADR-0001（Database per Service）・IADR-0018 の先行事例と同型。バス経由のため OrderExecution の可用性に審査が縛られない |
| (B) OrderExecution への同期 HTTP 照会 | **不可**: `GetRecentActivity` は同期契約（`RiskEvaluator` が同期純関数）で、かつ発注審査のホットパス。同期 HTTP は sync-over-async か契約破壊を招き、他サービスの可用性が発注審査を止める |
| (C) OrderExecution の DB を Risk が直読み | **不可**: ADR-0001（Database per Service）違反 |

### 2. 訂正・取消の口

| 選択肢 | 評価 |
| --- | --- |
| **(A) 新ポート `IOrderAmendmentBroker`・ペーパーのみ実装**（採用） | `IBrokerAdapter` を変更しない。moomoo が実装しない＝**実ブローカー選択時は訂正・取消の口が型として存在しない**。fail-safe をコンパイル時に担保できる |
| (B) `IBrokerAdapter` に `ModifyOrderAsync` を追加 | 全実装者に波及。moomoo 側は `TrdModifyOrder` 配線がスコープ外のため `NotSupportedException` を置くことになり、**実行時に初めて落ちる地雷**が実弾経路に残る |

### 3. ペーパーの非終端状態

| 選択肢 | 評価 |
| --- | --- |
| **(A) `immediateFill` フラグ（既定 `true`＝現挙動）**（採用） | 既定挙動が完全に不変。`false` のときだけ `Accepted` に留まり訂正・取消が成立する。最小の仕組み |
| (B) 常に `Accepted` にして別途約定させる | ペーパーの既定挙動（即時全量約定）を破壊する。FR-12・Stage 0/1 の検証価値に影響 |
| (C) ペーパーでは訂正・取消を成立させない | 配管が production でも test でも一度も通らない。#141/#152 が未検証の配管を踏むことになる |

## 決定

1. **イベント契約は追加のみ**。`Shared.Contracts/Events` に `OrderModified`・`OrderCancelled` を追加し、既存イベント（`OrderApproved`/`OrderExecuted`）の契約は変更しない。相関キーは既存注文系と同じ `DecisionId` とし、銘柄・方向は `OrderApproved` から補完する（`OrderExecuted` と同じ設計・IADR-0018）。訂正は前後の値を両方持たせ、監査（FR-11）でイベント単体から差分が読めるようにする。
2. **供給は Risk 専有 DB への射影**（選択肢 1-A）。新テーブル `order_activity` に承認・約定・訂正・取消を射影し、`EfOrderActivitySource` が窓を読む。`approved_orders`/`trade_fills`（#63 台帳）は**再利用しない**——台帳は `Filled` のみを載せる設計で、本用途の母集団である「約定ゼロで取り消された注文」を構造的に捨てているため。
3. **訂正・取消の口は新ポート `IOrderAmendmentBroker`**（選択肢 2-A）。`PaperBrokerAdapter` のみが実装し、`MoomooBrokerAdapter` は実装しない。実 OpenD への `TrdModifyOrder` 配線は後続・E2E（#82 系）へ分離する。
4. **ペーパーの非終端状態は `immediateFill` フラグ**（選択肢 3-A）。既定 `true` で現挙動（即時全量約定）と完全に同一。本 PR で `false` を使うのはテストのみ。
5. **発行は Worker 層**（`OrderAmendmentDispatcher`）。Application 層は MassTransit を参照しない既存レイヤリングを維持し、`OrderAmendmentService` はイベントを返すに留める。
6. **本決定はトリガを含まない**。`OrderAmendmentDispatcher` は #141/#152 の呼び出し口として提供し、本 PR では呼び出し元を実装しない。

## 理由

- **同期契約が供給経路を決めている**。`IOrderActivitySource.GetRecentActivity` は同期であり、発注審査という取引の最短経路上にある。ここに他サービスへの同期 HTTP を挟むと、OrderExecution が落ちた瞬間に発注審査が止まる（fail-safe に反する）。射影なら OrderExecution が落ちても審査は最後に射影された窓で継続でき、行が無ければ空窓＝最小標本ガードで無嫌疑に倒れる。
- **fail-safe は型で担保するのが最も強い**。実弾経路に訂正・取消の口を生やさなければ、実装ミスや将来の誤配線で実注文を訂正・取消してしまう事故が構造的に起きない。`NotSupportedException` は「実行して初めて分かる」ため、実弾を扱う経路の安全策としては弱い。
- **既定不変を最優先した**。`immediateFill` の既定を `true` に置くことで、本番配線・既存テスト・Stage 0/1 の検証価値のいずれにも影響しない。非終端状態は「配管を検証可能にするための最小の仕組み」に閉じている。
- **`PlacedAt` は `ApprovedAt` で近似する**。承認から発注までは同期的に連続しており、窓長（分〜時間）に対して誤差は無視できる。厳密な発注時刻はどのイベントにも無く、そのために新契約を足すのは本 PR の境界（配管まで）を越える。

## 結果

- 良い影響:
  - #49 の前提（実 `IOrderActivitySource` 供給）が解錠され、相場操縦判定が本番経路で有効になる
  - 注文ライフサイクルが監査台帳（FR-11）に揃う（訂正・取消がこれまで記録されていなかった）
  - #141/#152 は `OrderAmendmentDispatcher` を呼ぶだけでよく、発行・永続化・供給を再実装しない
- 悪い影響・トレードオフ:
  - `OrderAmendmentDispatcher` は本 PR の時点で**呼び出し元を持たない**（#141/#152 が呼ぶ）。意図的な先行提供であり、境界を守った結果である
  - `order_activity` は #63 台帳と発注情報が一部重複する。関心と寿命が異なるため許容する
  - 実ブローカー（moomoo）経路では訂正・取消が行えない。実弾を撃たない現段階では意図どおりで、実配線は #82 系で扱う
  - `PlacedAt` が近似（`ApprovedAt`）である
- フォローアップ:
  - 実 OpenD の `TrdModifyOrder` 配線と実コンテナ E2E → #82 系
  - 取消・訂正のトリガ → #141（自動リコンサイル）／#152（pause 強制取消）
  - `order_activity` の保持期間パージ（IADR-0059 と同型の論点）→ 窓長を大きく超える行は検知に不要。行数の実績を見て判断する
  - `ManipulationDetectionSettings` の設定ストア化 → IADR-0040 のフォローアップのまま
  - コード中の陳腐化した `#13/#17` 参照の全面是正 → #155

## 関連

- Supersedes: なし
- Superseded by: なし
