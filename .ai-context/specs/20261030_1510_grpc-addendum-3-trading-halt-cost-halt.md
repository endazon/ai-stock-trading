---
title: east-west gRPC 段 5 の追補 3 —— REST だけで呼ばれている同期の読み取り 2 本（発注執行 → 停止の状態・取引判断 → 費用統制の判定）を gRPC でも呼べるようにする
type: spec
status: done
related_ids: [NFR, FR-10, NFR-13, UC-02, ADR-0009, ADR-0074, IADR-0284, IADR-0427, IADR-0446, IADR-0489, IADR-0557, IADR-0559, IADR-0567, IADR-0582, MSP:ADR-0029, MSP:ADR-0075]
author: endazon (with Claude Code)
created: 2026-10-30
updated: 2026-10-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# 仕様書: east-west gRPC 段 5 の追補 3 —— 停止の状態と LLM 費用の停止（#1510）

## 起点

- issue #1510（受け皿は #753・`Refs`。#753 の再検証コメント 2026-10-30 で段 6 は blocked のまま・再検証期限 2026-11-09）。
  射程と段の切り方は [IADR-0284](../adr/IADR-0284_east-west-grpc-scope-and-order-ruling.md) 決定 5。直前の追補は
  [IADR-0557](../adr/IADR-0557_grpc-late-routes-held-symbols-asof-policy.md)（#1388）・[IADR-0559](../adr/IADR-0559_grpc-order-execution-read-surface.md)（#1391）。
- 2 本はいずれも **#1388・#1391 の後に REST で足された同期の読み取り**である:
  発注執行 → リスク管理 `GET /risk-controls/trading-halt`（#1427 系・[IADR-0567](../adr/IADR-0567_order-dispatch-final-halt-check-fail-closed.md)）、
  取引判断 → 費用統制 `GET /costs/state`（#1423 M-5・[IADR-0582](../adr/IADR-0582_llm-cost-cap-halt-skips-decisions-before-llm.md)）。
- 本 PR の判断は [IADR-0604](../adr/IADR-0604_grpc-addendum-3-trading-halt-cost-halt.md)（develop の最大 0602〔#1508〕と、開いている #1509 の 0603 を確かめて＋1）。
- 計画 ID: NFR（輸送の差し替え。振る舞いは変えない）。経路の起点は FR-10・ADR-0009（停止の状態）、NFR-13・UC-02・ADR-0074（LLM 費用の停止）。

## 母集合（規則 9。worktree の基点 `1a38717c`・2026-10-30・自分で引き直した）

引き方 1（軸 1・アダプタ）: `backend/Services` と `backend/Shared` の `Http*.cs`（テスト以外）について、同じディレクトリに `Http` を `Grpc` に
置き換えた名前のファイルがあるかを突き合わせた。**対の無いもの 8 件**:

| ファイル | 呼び先 | 分類 |
| --- | --- | --- |
| `OrderExecutionService/.../HttpTradingHaltSource.cs` | リスク管理 `GET /risk-controls/trading-halt` | **本 PR**（rpc を新設） |
| `TradeDecisionService/.../HttpLlmCostHaltGate.cs` | 費用統制 `GET /costs/state` | **本 PR**（既存の `CostStateRead/GetCostState` を使う） |
| `ReportService/.../HttpDecisionReferenceSource.cs` | 監査 `GET /audit/events/by-type` | **既知の偽陽性**（下記） |
| `ReportService/.../HttpReportNarrativeDrafter.cs` | MSP LlmGateway | 段の外（基盤の系列。共有の gRPC 輸送が対） |
| `TradeDecisionService/.../HttpLlmCompletionClient.cs` | MSP LlmGateway | 段の外（同上） |
| `Shared.KnowledgeBase/Adapters/HttpKnowledgeBaseSearch.cs` | MSP KB | 段の外（基盤の系列） |
| `Shared.KnowledgeBase/Adapters/HttpKnowledgeBaseWriter.cs` | MSP KB | 段の外（同上） |
| `Shared.KnowledgeBase/Adapters/HttpKnowledgeDocumentCatalog.cs` | MSP KB | 段の外（同上） |

**`HttpDecisionReferenceSource` を除いた理由**: gRPC 実装 `GrpcDecisionReferenceSource` が**同じファイルの中に**ある（ファイル名で突き合わせる
引き方 1 では対が見えない）。監査 `AuditEventsRead/GetEventsByType` を使い、組み立ては段 3 の `Audit:Grpc` で切り替わる。#1388・#1391 の
母集合表でも同じ扱いであり、2026-10-17 の棚卸しで既知の偽陽性として記録済み。🔴 今回も同じ理由であることを、ファイルの中身で確かめた
（`grep -n DecisionReference backend/Services/ReportService/Infrastructure/ExternalServices/HttpDecisionReferenceSource.cs` に gRPC 実装がある）。

引き方 2（軸 2・呼び出し箇所）: `backend` の本番コードで `risk-controls/trading-halt` と `costs/state` を読む箇所を走査した。
発注執行（`HttpTradingHaltSource`）・取引判断（`HttpLlmCostHaltGate`）・情報収集（`HttpCostControlGate`。段 4 で gRPC 済み）の 3 か所で、
REST だけのものは本 PR の 2 本だけ。

除外: BFF（north-south は REST のまま）・MSP 宛の 5 件（段の外）・`HttpDecisionReferenceSource`（既知の偽陽性）。

## 方針

1. **停止の状態は rpc を新設する**（`RiskControlsRead/GetTradingHalt`）。proto は**追加だけ**（rpc 1・message 2・`optional bool` 2）。
   `scripts/check-proto-contracts.js --update` で基準を更新し、差分が追加だけであることを確かめる。提供側は REST と同じ `TradingHaltService.Build()` を通り、
   写しは `RiskReadWireMapping.ToProto(TradingHaltView)`（在る false も設定する）。門は他の読み取りと同じ（クラスの属性）。
2. **LLM 費用の停止は既存の `CostStateRead/GetCostState` を使う**（提供側は変えない）。
3. 呼び出し側は段 1〜5 と同じ切り方: `RiskManagement:Grpc`・`CostControl:Grpc` を宣言したときだけ輸送を DI に載せ、載っていれば照会口が gRPC 実装を
   選ぶ（`*:BaseUrl` より優先）。**既定は REST**。不正な宛先は起動時に落とす。deadline の既定は REST の `HttpClient.Timeout` と同値（どちらも 3 秒）、
   既定は再試行しない。輸送の規則は発注執行に新設する `OrderExecutionGrpcCalls`（他サービスの `*GrpcCalls` の写し）と、取引判断の既存の `TradeDecisionGrpcCalls`。
4. **倒す向きは REST と同じ**: 停止の状態は欠落・失敗・時間切れ・呼び出し元の取り消しのどれも不明（新規建てを送らない。解釈は
   `HttpTradingHaltSource.Interpret` を共有）。LLM 費用の停止は `is_halted` が明示的に true のときだけ停止、他は停止していない。gRPC の失敗で REST へは落とさない。
5. helm: 既定は描画を変えない。計測の overlay（`values-grpc-measurement.yaml`）に 2 経路を `true` で足す（どちらも本番既定で REST が結線済み＝輸送の切り替え）。
   helm.yml の期待表を 2 行足す（呼び出し側 15 → 17）。Runbook の経路表に #18・#19 を足す。

## 受け入れ基準

- [x] 既定（宣言なし）の組み立ては REST のまま（T-10-3084・T-10-3086 の陰性対照）。helm の既定描画に gRPC の宣言が無い（helm.yml ②）。
- [x] 宣言すれば gRPC で読み、REST の宛先より優先し、組み立てた経路が実際に偽の提供側を呼ぶ（T-10-3084・T-10-3086）。
- [x] 提供側は REST と同じ値を返し、false も在る値として運ぶ。門は既定で閉じる（T-10-3082）。
- [x] 受け手は送り手の型から REST と同じ答えを返す。欠落・失敗・時間切れ・取り消しは REST と同じ向きへ倒す（T-10-3083・T-10-3085）。
- [x] proto の差分は追加だけ（`check-proto-contracts.js` OK）。
- [x] 計測の overlay の描画検査（helm.yml の同じ手順を手元で実行）が緑。期待表を足す前は 2 行の差で赤。

## 変異注入の実測（2026-10-30。1 つずつ入れて実行し、書き戻した）

| 変異 | 結果 | 殺したテスト |
| --- | --- | --- |
| 発注執行の組み立て: 宣言があっても gRPC を選ばない | 6 件中 3 件赤 | T-10-3084 |
| 発注執行の受け手: kill switch の欠落を false と読む | 13 件中 1 件赤 | T-10-3083 |
| 取引判断の組み立て: 宣言があっても gRPC を選ばない | 4 件中 1 件赤 | T-10-3086 |
| 取引判断の受け手: 応答なし（失敗）を停止と読む | 8 件中 5 件赤 | T-10-3085 |
| リスク管理の写し: kill switch に一時停止を写す | 6 件中 2 件赤 | T-10-3082 |
| helm.yml: 期待表に 2 行を足さない | 赤（2 行の差） | 計測 overlay の描画検査 |

## PoC 配備への影響

無し（既定 REST）。values.yaml・values-local.yaml の描画は変わらない。計測の overlay を重ねたときだけ 2 経路が足される。
