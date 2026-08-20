---
title: 判断由来の決済（AI の出口）— 保有中の反対売買を Close に写し、裸の新規売りを止める
type: spec
status: accepted
related_ids: [FR-04, FR-05, FR-10, FR-19, UC-01, UC-02, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-30
updated: 2026-07-30
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: 判断由来の決済（AI の出口）

> 利用者指示・設計承認（2026-07-30）。[#292](https://github.com/endazon/ai-stock-trading/issues/292) の
> **PR 3/3**（PR 1/3 = owner 決済経路、PR 2/3 = ブローカ突合 の上に積む）。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-04（AI の売買判断）／FR-05（発注）／**FR-10**（「いずれも手仕舞い（Close）と損切りは止めない」）／FR-19（取引ガード）
- ユースケース: UC-01（定時サイクル）・UC-02（価格変動起点）
- ADR: ADR-0003（計画リポ）（不確実なら Hold）
- 関連 IADR: [IADR-0004](../adr/IADR-0004_position-effect-entry-scoping.md)（エントリー判定は `PositionEffect`）／
  [IADR-0038](../adr/IADR-0038_order-decomposition-position-effect.md)（符号付きゼロ跨ぎ分割・反転は Close+Open）／
  [IADR-0035](../adr/IADR-0035_stop-loss-authoritative.md)（損切り価格の権威）／
  [IADR-0076](../adr/IADR-0076_trade-decision-profitability-gate.md)（採算ゲート）／
  [IADR-0099](../adr/IADR-0099_current-price-context-for-decision.md)（現在値アンカリング）／
  [IADR-0117](../adr/IADR-0117_owner-position-close-path.md)（owner 決済経路）／
  本作業で新規 [IADR-0119](../adr/IADR-0119_decision-derived-close.md)
- 対象 Issue: [#292](https://github.com/endazon/ai-stock-trading/issues/292)（`Refs #292`）

## 現状（この変更の直前・実コードで確定）

`TradeDecisionService.cs:208` が `PositionEffect.Open` を**リテラル固定**している（IADR-0004）。
結果、LLM が `Sell` を出しても「保有中のロングの決済」ではなく**新規ショート建て**として扱われる。

| 実害 | 具体 |
| --- | --- |
| 統制が逆向きに効く | `RiskEvaluator.cs:20` の `isEntry = (PositionEffect == Open)` が真になり、AI の売却が kill switch・pause・日次損失ロックアウト・段階資金上限・同日再エントリー禁止で**ブロックされる**。FR-10 の「手仕舞いは止めない」と正反対 |
| 数量が保有数と無関係 | `PositionSizer` が新規建てサイズを算出するため「保有 4072 株を全部売る」が構造的に出せない |
| 裸の新規売り | 保有ゼロで `Sell` が出ると、現物のみ有効な段階でも**ショート建ての注文がブローカへ飛ぶ**（ガードは `ProductType.Cash` を見るだけで方向を見ない） |
| 存在しない建玉の損切り | `stopLossPrice` が反対方向で計算され台帳へ載る |

**自動取引の出口は損切りラインだけ**で、利確・時間切れ・AI 判断による撤退が 1 つも無い。

## 目的

1. LLM の判断が**保有建玉の反対売買**なら、`PositionEffect.Close`・数量＝保有数で発行する。
2. 決済が統制（kill switch・pause・ロックアウト・同日再エントリー・段階資金上限）で**止まらない**（FR-10）。
3. **裸の新規売りを構造的に止める**（保有が無い／不明な `Sell` は Hold）。
4. 建玉が不明なときに誤った効果で発注しない（fail-safe）。

## 設計

### 1. 建玉の取得（新ポート `IHeldPositionProvider`）

```csharp
/// (Symbol, Market) の符号付き建玉数量（+ ロング / − ショート / 0 保有なし）。
/// 取得できない場合は **null（＝不明）**。0（保有なし）と厳格に区別する。
Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken cancellationToken = default);
```

- 実装は `HttpHeldPositionProvider` → リスク管理の既存 `GET /risk-controls/open-positions`（`OwnerOrService`）。
  新規エンドポイントは作らない。配線は既存の `RiskManagement:BaseUrl` ＋ s2s トークン（`ISizingContextProvider` と同型）。
- **空配列（保有なし＝0）と照会失敗（null＝不明）を厳格に区別する。** `HttpPositionStore`（市場監視）は失敗を
  空列へ倒すが、ここで同じことをすると「保有なし」と誤断定して裸の新規売りを通してしまう。
- 既定は `NoOpHeldPositionProvider`（常に null＝不明）。

### 2. 建玉効果の決定（純関数 `PositionEffectResolver`・`TradeDecisionService.Domain`）

`held`（符号付き・null=不明）と `side` から、発行すべき効果を決める。

| 保有 | 判断 | 効果 | 数量 |
| --- | --- | --- | --- |
| ロング（>0） | `Sell` | **Close** | 保有数（全量） |
| ロング（>0） | `Buy` | Open | サイジング |
| ショート（<0） | `Buy` | **Close** | 保有数（全量） |
| ショート（<0） | `Sell` | Open | サイジング |
| なし（0） | `Buy` | Open | サイジング |
| なし（0） | `Sell` | **Hold** | — |
| 不明（null） | `Buy` | Open | サイジング |
| 不明（null） | `Sell` | **Hold** | — |

- **決済は常に全量**。LLM は数量を出さず、サイジング（リスク基準の新規建てサイズ）は出口の量ではない。
  部分利確が要るなら別途 LLM 出力の拡張が要り、本 PR の範囲外。
- **ゼロ跨ぎは起こらない**（Close は保有数ちょうど）。IADR-0038 の「反転は Close+Open の 2 意図」は
  ゼロを跨ぐ場合の規約であり、本 PR は Close レグのみを出す（次サイクルで Open は独立に判断される）。
- **不明（null）で `Sell` を Hold に倒すのは既存挙動の変更**である。従来は裸の新規売りが飛んでいた。
  ADR-0003（「方針の範囲外・不確実な場合は必ず Hold」）に沿う安全側の是正として意図的に行う。
- `Buy` 側は不明でも Open のまま（現行挙動）。買いは裸になり得ず、金額系上限がそのまま効く。

### 3. 決済経路で飛ばす処理

`PositionEffect.Close` のときは以下を**通さない**。

| 処理 | 理由 |
| --- | --- |
| `PositionSizer` | 出口の数量は保有数であって新規建てサイズではない |
| 採算ゲート（IADR-0076） | 最小期待利益で**撤退を止めてはならない**（損失を止めるための決済が採算未達で通らなくなる） |
| 損切り幅の妥当性検証 | 決済注文に損切り価格は無い（`StopLossPrice = null`） |

残す検証は `referencePrice > 0` のみ（価格 0 の注文を投げない）。

発注前スクリーニング（`RiskEvaluator`）は**通す**。`isEntry = (PositionEffect == Open)` により、Close は
kill switch・pause・ロックアウト・段階資金上限・同日再エントリーの各判定を**構造的に素通りする**
（＝ FR-10 の要求どおり手仕舞いが止まらない）。禁止銘柄・市場・商品種別のガードは決済にも効いたままだが、
これらは「その銘柄を扱えるか」の恒久設定であり、手仕舞いを妨げる想定の統制ではない。

### 4. 構成

| キー | 既定 | 意味 |
| --- | --- | --- |
| `RiskManagement:BaseUrl` | 既存（未設定なら NoOp） | 建玉照会の権威源。**新規キーは足さない** |

Helm / values / compose / `.env.example` は**不変**（`values-local.yaml` は trade-decision に既に
`RiskManagement__BaseUrl` を与えているため、経路 B では追加設定なしで有効になる）。

## 影響範囲

| 対象 | 変更 |
| --- | --- |
| `TradeDecisionService.Domain` | `PositionEffectResolver`（新規・純関数） |
| `TradeDecisionService.Application` | `IHeldPositionProvider`（新規ポート）、`NoOpHeldPositionProvider`、`TradeDecisionService` の分岐 |
| `TradeDecisionService.Worker` | `HttpHeldPositionProvider`、DI 配線（既存 `risk` HttpClient を再利用） |
| `Shared.Contracts` | **不変**（新規イベント無し） |
| `RiskManagementService` | **不変**（既存 `GET /risk-controls/open-positions` を使う） |
| DB スキーマ / Migration | **無し** |
| Helm / values / compose | **不変** |
| 実弾ゲート（閂 0〜4） | **不変** |

## テスト（受け入れ基準の写像）

| # | 観点 | テスト |
| --- | --- | --- |
| 1 | 効果の決定（純関数） | 8 通りの組み合わせ（保有 ロング/ショート/なし/不明 × Buy/Sell）を固定 |
| 2 | 決済は全量 | ロング 4072 保有で `Sell` → `Close` / 数量 4072（サイジング結果に依らない） |
| 3 | ショートの手仕舞い | ショート 100 保有で `Buy` → `Close` / 数量 100 |
| 4 | 建て増し | ロング保有で `Buy` → `Open` / サイジング数量 |
| 5 | **裸の新規売りを止める** | 保有なしで `Sell` → 発行しない（Hold） |
| 6 | **不明は Hold** | 建玉照会が null で `Sell` → 発行しない／`Buy` は従来どおり Open |
| 7 | 採算ゲートを飛ばす | 採算ゲート有効・採算不成立でも Close は発行される |
| 8 | サイジングを飛ばす | 残枠 0（新規建てなら数量 0 で見送り）でも Close は発行される |
| 9 | 損切り価格 | Close の `StopLossPrice` は null／Open は従来どおり算出される |
| 10 | 換算レート | Close にも `FxRateToBase` が載る |
| 11 | 統制を素通りする | `RiskEvaluator` が Close を kill switch・pause・ロックアウト・段階資金上限・同日再エントリーで拒否しない（既存の不変量の回帰固定） |
| 12 | 照会の fail-safe | 非 2xx・例外・タイムアウト・不正応答は null（不明）／空配列は 0（保有なし） |
| 13 | 既定 | `RiskManagement:BaseUrl` 未設定なら NoOp（常に不明）で、Buy のみ従来どおり動く |

## 受け入れ基準（`docs/DEFINITION_OF_DONE.md` と併せて）

- [x] AI が保有建玉を自分で手仕舞える（`PositionEffect.Close`・全量）
- [x] 決済が kill switch・pause・ロックアウト・同日再エントリー・段階資金上限で止まらない
- [x] 保有なし／不明の `Sell` が発注に至らない（裸の新規売りの根絶）
- [x] 採算ゲート・サイジングが撤退を妨げない
- [x] Helm / values / Migration / 実弾ゲートが不変
- [x] `dotnet build` / `dotnet test` / `dotnet format` が green・CI / gitleaks が green

## スコープ外

- **部分利確**（LLM に数量を出させる拡張が必要）
- ゼロ跨ぎの反転注文（IADR-0038 の Close+Open 2 意図。信用有効化後の課題）
- 時間切れ・トレーリングストップ等の機械的な出口ルール
- 判断材料の実効化（KB/RAG・ニュース本文の供給＝[#288](https://github.com/endazon/ai-stock-trading/issues/288) の管轄）
