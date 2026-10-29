---
title: IADR-0604 east-west gRPC 段 5 の追補 3 —— 発注執行の停止の状態（rpc を新設）と取引判断の LLM 費用の停止（既存 rpc）を段 1〜5 と同じ切り方で並走させる
type: impl-adr
status: Accepted
related_ids: [NFR, FR-10, NFR-13, UC-02, ADR-0009, ADR-0074, MSP:ADR-0029, MSP:ADR-0075, IADR-0284, IADR-0427, IADR-0446, IADR-0489, IADR-0557, IADR-0559, IADR-0567, IADR-0582]
author: endazon (with Claude Code)
created: 2026-10-30
updated: 2026-10-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# IADR-0604: east-west gRPC 段 5 の追補 3（停止の状態・LLM 費用の停止）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-30
- 決定者: Claude Code（実装）／ endazon（追補 3 を 1 PR・既定 REST・rpc の新設はオーケストレーター経由の指示。#753）

## 起点・関連

- 関連する計画書 ID: **`MSP/ADR-0029`**（同期通信の使い分け基準）、**`MSP/ADR-0075`**（移行順序）。経路の起点は FR-10・ADR-0009（停止の状態）、
  NFR-13・UC-02・ADR-0074（LLM 費用の停止）。
- 関連する実装仕様書: [`.ai-context/specs/20261030_1510_grpc-addendum-3-trading-halt-cost-halt.md`](../specs/20261030_1510_grpc-addendum-3-trading-halt-cost-halt.md)
- 前提: [IADR-0284](IADR-0284_east-west-grpc-scope-and-order-ruling.md) 決定 5（段 6 の完了は呼び出し箇所で数える）、
  [IADR-0427](IADR-0427_risk-read-grpc-stage2.md)（原則 A・解釈の共有・チャネルの所有）、[IADR-0559](IADR-0559_grpc-order-execution-read-surface.md)（直前の追補）、
  [IADR-0567](IADR-0567_order-dispatch-final-halt-check-fail-closed.md)（停止の状態の照会・fail-closed）、[IADR-0582](IADR-0582_llm-cost-cap-halt-skips-decisions-before-llm.md)（LLM の前の費用の停止）。
- issue: #1510（受け皿 #753）

## 背景

#1388・#1391 で段 1〜5 の同期の読み取りを揃えた後に、REST だけの同期の読み取りが 2 本足された（停止の状態・LLM 費用の停止）。
段 6（REST の撤去）の着手条件は「呼び出し箇所が全部 gRPC でも呼べる」ことであり、2 本が残ると数えが合わない。

## 決定

1. **停止の状態は `RiskControlsRead` に rpc `GetTradingHalt` を新設する。** 別の service を立てない（読み取りの門・認可・チャネルが同じであり、
   リスク管理の読み取りは 1 つの service に揃っている）。proto は追加だけ（`GetTradingHaltRequest {}`・`GetTradingHaltResponse { optional bool ×2 }`）。
   提供側は REST と同じ `TradingHaltService.Build()` を通り、写しは `RiskReadWireMapping.ToProto(TradingHaltView)`（在る false も設定する）。
2. **LLM 費用の停止は既存の `CostStateRead/GetCostState` を使う**（情報収集と同じ rpc。運ぶ項目は足さない）。
3. **既定は REST。** `RiskManagement:Grpc`（発注執行）・`CostControl:Grpc`（取引判断）を宣言したときだけ輸送を DI に載せ、照会口が gRPC 実装を選ぶ。
   宣言は `*:BaseUrl` より優先、不正な宛先は起動時に落とす。deadline の既定は REST と同じ 3 秒、既定は再試行しない。
4. **倒す向きは REST と同じで、gRPC の失敗で REST へ落とさない。** 停止の状態は欠落・失敗・時間切れ・呼び出し元の取り消しを不明（新規建てを送らない）とし、
   解釈は `HttpTradingHaltSource.Interpret` を共有する。LLM 費用の停止は `is_halted` が明示的に true のときだけ停止。
5. **発注執行の輸送の規則は `OrderExecutionGrpcCalls` に置く**（他サービスの `*GrpcCalls` の写し。サービスごとに 1 つ。発注執行が呼び出し側になるのは初めて）。
6. **計測の overlay へ 2 経路を `true` で足す**（どちらも本番既定で REST が結線済み＝輸送の切り替えであり、#6・#17 のような「初めての結線」ではない）。
   helm.yml の期待表は呼び出し側 15 → 17、Runbook の経路表は 17 → 19 宣言。

## 却下した案

- **停止の状態を `RiskControlsOwnerRead` に置く**: 門が所有者向けで、発注執行の s2s（trading-service）が通らない。
- **REST へのフォールバック**: 段 1〜5 と同じく採らない。gRPC へ切り替えたつもりで切り替わっていないことが見えなくなる。

## 結果・残余リスク

- 既定の配備は変わらない（PoC 配備への影響は無し）。計測の窓で #18 が落ちると、その間の新規建ては発注執行で止まる（fail-closed のまま）。Runbook に注意を書いた。
- 母集合の引き方 1 はファイル名の突き合わせであり、同じファイルに gRPC 実装を置く `HttpDecisionReferenceSource` を毎回偽陽性として拾う。今回も仕様書で除いた。
