---
title: LLM の根拠文が株数に言及してもサイジング結果と食い違う記録を残さない —— 数量はシステムが決めるとプロンプトで明示し、発行する記録では不一致に注記する
type: spec
status: accepted
related_ids: [FR-04, FR-10, FR-11, ADR-0003, ADR-0033, ADR-0040, IADR-0003, IADR-0029, IADR-0119, IADR-0343]
author: endazon (with Claude Code)
created: 2026-09-17
updated: 2026-09-17
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定 5・フォローアップ 4)
  - planning:projects/ai-stock-trading/00_vision/00_vision.md (「米国株（1株単位）」＝売買単位)
---

# 仕様書: 根拠文の数量言及とサイジング数量の突合（#822）

## 起点

- #822（enhancement）。計画 ADR-0040 決定 5「方針文の散文は数量を拘束しない」・フォローアップ 4「根拠文の数量とサイジングの結果を突合する経路を作る（方式は定めない）」。
- 実測（2026-09-16〜17・稼働クラスタのログ）: 本判断の根拠文「…リスク制約内で1株単位の新規買いが可能と判断…」に対し、`定時判断: … AAPL Buy 数量=849`。

## 方式（IADR-0343）

1. **プロンプト（本判断 `TradeDecisionPromptBuilder.Build`）**: `# リスク制約` 節に 2 文を足す。
   - 発注数量は判断の後にシステムが統制値から算出する。rationale で株数に言及しない。
   - 方針文の「1株単位」等は売買単位であり数量の上限ではない。
   - **一次スクリーニング（`BuildScreening`）には足さない** —— リスク制約節を持たず、根拠文は記録（`TradeDecisionMade`）へ載らない（ログのみ）。費用統制（IADR-0039）で文言を最小に保つ。
2. **記録（発行する判断）**: 純関数 `RationaleQuantityReconciler`（`Domain/`）が根拠文中の株数言及を検出し、**サイジング後の数量と異なる言及が 1 つでもあれば、LLM の文言は変えずに末尾へ決定的なシステム注記を追記する**。一致・言及なしは原文のまま。不一致時は WARN ログを出す。
   - 適用点: `TradeDecisionAppService` の `TradeDecisionMade` 生成 2 か所（新規建て＝サイジング数量／決済＝保有全量）と `Stage0DecisionRecorder` の多数決根拠（`|SignedQuantity|`。Hold は対象外）。
   - サイジング・契約・リスク統制・発注執行は変更しない。

## 母集合（根拠文の生成点と消費点）

走査: `grep -rn "\.Rationale\b" backend --include=*.cs`（テスト除外）と `grep -rln "rationale\|Rationale" frontend/src backend`（2 軸）。

| 箇所 | 種別 | 扱い |
| --- | --- | --- |
| `TradeDecisionAppService.cs` 決済経路の `new TradeDecisionMade(… decision.Rationale …)` | 生成（発行） | **突合を適用**（数量＝`effect.CloseQuantity`） |
| `TradeDecisionAppService.cs` 新規建て経路の `new TradeDecisionMade(… decision.Rationale …)` | 生成（発行） | **突合を適用**（数量＝サイジング結果） |
| `TradeDecisionAppService.cs` `LLM 判断:` ログ | ログ（サイジング前） | 対象外 —— 数量確定前の生出力の記録であり、突合する数量がまだ無い |
| `DecisionOrchestrator.cs` スクリーニング根拠のログ | ログ | 対象外（方向のみ・記録に載らない） |
| `Stage0DecisionRecorder.cs` 多数決根拠 `MajorityRationale` | 生成（記録） | **突合を適用**（`|SignedQuantity|`、Hold 除外） |
| `Stage0DecisionRecorder.cs` 生の判断 `Stage0RawDecision.Rationale` | 生成（記録） | 対象外 —— 各票の生出力の記録（数量を持たない票単位） |
| `DecisionAggregator.cs` / `TradeDecisionParser.cs` | 解析・多数決 | 対象外（数量確定前） |
| `AuditService/Domain/AuditEntryFactory.cs` | 消費（監査要約・Detail JSON） | イベントの `Rationale` をそのまま使う＝**発行時の注記が届く**（改修不要） |
| `ReportService` `HttpTradeRationaleSource` → `FillPnlAttribution` / `TradeHistoryViewBuilder` / `ReportRenderer` | 消費（報告書） | 監査台帳の `TradeDecisionMade.Rationale` を引く＝**注記が届く**（改修不要） |
| `NotificationService`（Discord） | — | 根拠文を扱わない（走査で 0 件） |
| `frontend/src` | — | 根拠文を扱わない（走査で 0 件） |
| `PlaceholderProviders.cs` | 固定 Hold 出力 | 対象外 |
| `BacktestService/Domain/Stage0Promotion.cs` | 別概念（昇格判定の理由） | 対象外（名前が同じだけ） |

除外の理由は表の「扱い」列のとおり。

## 受け入れ基準 → テスト

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| AC1 | 本判断プロンプトが「数量はシステムが決める・根拠文で株数に言及しない」「1株単位は売買単位であり上限ではない」を含む | `TradeDecisionPromptBuilderTests.本判断プロンプトは数量をシステムが決めると明示する` |
| AC2 | 根拠文の株数言及がサイジング数量と異なれば、原文を保ったまま注記が追記される（発行される `TradeDecisionMade`） | `TradeDecisionServiceTests.根拠文の株数がサイジング数量と異なれば注記を追記する` |
| AC3 | 言及が一致・言及なしなら原文のまま | `RationaleQuantityReconcilerTests`（Theory）＋既存 `Buy判断は…`（`押し目` のまま） |
| AC4 | 検出規則: `1株`/`１株`/`一株`/`849 株`/`1,000株`/`N shares`/`one share` を拾い、`1株あたり`/`per share`/`株価` 等の単価表現は拾わない | `RationaleQuantityReconcilerTests` |
| AC5 | 決済経路も保有全量で突合する | `TradeDecisionServiceTests.決済の根拠文も保有全量と突合する` |
| AC6 | Stage 0 記録の多数決根拠にも同じ注記が入る | `Stage0DecisionRecorderTests.多数決根拠の株数がサイジング数量と異なれば注記を追記する` |
| AC7 | サイジング結果は変わらない | 既存 `発注意図の数量は必ずPositionSizer経由で確定される` が緑のまま |

## 検証

`dotnet build backend/backend.slnx -c Release`・TradeDecisionService.Tests・`dotnet format --verify-no-changes`・文書系検査器（PR 本文に実出力）。

## 付随

- `.claude/rules/traceability.repo.md` の計画 ADR レンジを `ADR-0001..0040` へ更新する（計画リポ `gen-plan-ranges.js --check` 実測 2026-09-17: ADR [1, 40]・40 件・欠番なし）。コミット件名のスコープに `ADR-0040` を置くため。
