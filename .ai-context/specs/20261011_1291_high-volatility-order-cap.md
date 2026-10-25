---
title: 高ボラティリティ銘柄の区分（ATR(14) ÷ 参照価格 ≥ 4% と利用者の明示指定の併用）に、1 注文あたりの発注金額上限 equity の 5% をサイジング・LLM の前の見送り・審査の 3 か所で同じ関数で掛ける（#1291）
type: spec
status: accepted
related_ids: [FR-10, FR-04, FR-11, UC-06, ADR-0063, ADR-0049, ADR-0018, ADR-0016, ADR-0040, ADR-0042, IADR-0527, IADR-0130, IADR-0486, IADR-0495, IADR-0500, IADR-0003, IADR-0151, IADR-0161]
author: claude (Claude Code)
created: 2026-10-11
updated: 2026-10-11
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0063_high-volatility-symbol-order-cap.md (決定 1〜6・フォローアップ 1〜4)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5「高ボラティリティ銘柄の区分」「高ボラティリティ銘柄の 1 注文あたりの発注金額上限」「1 注文あたりの発注金額上限」「新規建ての最小の名目額」)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10「常に厳しい方が効く」)
---

# 高ボラティリティ銘柄の 1 注文上限（#1291）

## 背景

- PoC（2026-10-09 US・SIMULATE）で TSLA が 614 株 @384.94（$236,353＝equity 971,017 の 24.3%）で建ち、新規建ての残枠が $237 になった。以後 6 時間・527 回の判断が LLM の前に `EntryCapacityBelowMinimumNotional` で見送られた。
- 方針の文「高ボラティリティ銘柄は数量を控えめに」は数量に効かない（数量は統制が決める）。
- オーナー裁定（2026-10-10）を planning#782 へ環流し、計画 ADR-0063（Accepted・2026-10-10）と §5 の 2 行として裁定された（planning#787）。

## 裁定（ADR-0063 の原文で確認）

1. 区分は **自動判定（ATR(14, 日足) ÷ 参照価格 ≥ 4%。ちょうど 4% は入る）と利用者の明示指定の併用**。明示指定は厳しい側へだけ効く。指定できるのは利用者だけで、AI の監視銘柄の入れ替え案からは指定させない。**ATR が得られないときは明示指定だけで判定する**（fail-closed にしない）。
2. 区分の 1 注文上限は **equity の 5%**（既定・確定単一値）。構成可・範囲は「新規建ての最小の名目額の比率（既定 1%）以上 〜 25%」。
3. 区分外の 25% は据え置く。
4. 遡及しない。新規建て（買い増し・売り増し・空売りの新規建てを含む）の発注時に注文単位で判定する。空売りの銘柄ごとの累計 10% は残す。
5. 名目上限は損切り幅と独立の集中上限で、「常に厳しい方が効く」に加わる。
6. フォローアップ 3: サイジング（`PositionSizer.CalculateCappedQuantity` への上限）・審査（`RiskEvaluator`）・LLM の前の見送り（`TradeDecisionAppService.cs:401-404`）の 3 か所を揃える。

## 実測（origin/develop c7458ab6）

- 1 注文上限は `RiskLimitSettings.MaxOrderAmountFor(equity)` の 1 値で、サイジング（`TradeDecisionAppService` のサイジング）・審査（`RiskEvaluator` の `PerOrderAmountExceeded`）・LLM の前の見送り（`MinimumEntryNotional.CapacityCannotReach` への引数）・Stage 0 の記録（`Stage0DecisionRecorder.SignedQuantity`）の 4 か所が呼ぶ。
- ATR(14) は取引判断だけが持つ（`IStopWidthFloorSource`。既定 `StopWidthFloor:Atr14:Enabled=false`）。審査側には ATR の経路が無い。
- `MonitoredSymbol(Symbol, Market)` は市場監視の型で、AI の入れ替え案（ADR-0042）からも書かれる。
- 取引判断はリスク管理の設定を `GET /risk-controls/sizing-context`（REST・既定）または gRPC `GetSizingContext` で読む。
- 設定は 1 行 JSON（`RiskSettingsSerialization`）。旧行はキーを持たない。

## 母集合（規則 9・10）

- 「`MaxOrderAmountFor(`」を全文書で走査: 上の 4 か所＋`RiskStatusService`（SC-03 の区分外の上限の表示。銘柄に依らないので変えない）＋`TradeDecisionPromptBuilder`（プロンプトの上限の行。残余リスクへ）。
- 「§5 にはまだ行が無い」を走査: `TradingDefaults.cs`・`TradingDefaultsTests.cs` の最小の名目額の注記（planning#739 で §5 に行が入ったため改める）。docs の該当箇所は FR-10 機能仕様の既存節が「計画に行が無い」と書いていない（確認済み）。
- 新たに誤りになる自分の記述: `PositionSizer.cs` の「1 注文金額上限（equity の 25%）」は区分外の値として残る（区分は呼び出し側が渡す）。`RiskEvaluator` のコメントに区分を書き足した。

## 設計

- **区分の判定と上限は 1 か所の純関数** `HighVolatilityOrderCap`（リスク管理の Domain。取引判断は extern alias で同じ型を使う）。
  - `IsAutoClassified(atr, referencePrice)`: ATR ＞ 0・価格 ＞ 0・ATR ÷ 価格 ≥ 0.04。
  - `IsHighVolatility(settings, symbol, market, atr, referencePrice)`: 明示指定 OR 自動判定。
  - `MaxOrderAmountFor(limits, settings, equity, isHv)`: 区分外は既存の `MaxOrderAmountFor`、区分は `min(区分外, equity × 区分の比率)`。
- **統制値の置き場はリスク管理の設定**（`RiskManagementSettings.HighVolatility`。上限と明示指定の一覧）。変更は `PUT /risk-controls/settings/high-volatility`（OwnerOnly・理由必須・値域外 400・履歴 `HighVolatilityChanged`）。→ IADR-0527 決定 2。
- **サイジング文脈で取引判断へ渡す**（REST は `SizingContextView.HighVolatility`、gRPC は `HighVolatilityControls`）。未供給は既定（5%・明示指定なし）で効かせる。
- **審査は発注意図が運ぶ ATR**（`OrderIntent.Atr14`）で自動判定する。null は明示指定だけ。→ IADR-0527 決定 3。
- **LLM の前**: 明示指定は手元で分かる。自動判定は「区分外なら届くが区分なら届かない」ときだけ ATR をその場で読み、以後の下限・プロンプトに同じ値を使う（判断ごとに 1 回）。→ IADR-0527 決定 4。
- Stage 0 の記録も同じ関数で掛ける（本番と同じサイジング）。
- 拒否理由は区分外と同じ `PerOrderAmountExceeded`（同じ「1 注文あたりの発注金額上限」の統制）。

## 受け入れ基準 → 試験

| 基準 | 試験 |
| --- | --- |
| 既定値 5%・4%・明示指定なしを `TradingDefaults` で固定し §5 と一致 | T-10-2552 |
| ちょうど 4% は区分・直下は区分外 | T-10-2540・T-10-2554 |
| ATR 欠損は明示指定だけ | T-10-2541・T-10-2546・T-10-2555 |
| 明示指定と自動判定の両方に当たっても 5% が 1 回 | T-10-2542 |
| 区分外の 25% は変わらない／区分は区分外より緩くならない | T-10-2544・T-10-2547 |
| 審査は 5% 境界で切り替わり、決済は止めない | T-10-2545・T-10-2548・T-10-2549 |
| サイジングの数量は審査を通り、1 株多ければ落ちる（3 か所一致） | T-10-2553・T-10-2554 |
| LLM の前の見送りも同じ上限（ATR は 1 回だけ読む） | T-10-2556・T-10-2557 |
| 未供給は既定で効く（REST・gRPC の両経路） | T-10-2558・T-10-2559・T-10-2564・T-10-2565 |
| 逆シリアル化できない応答は安全既定 | T-10-2566 |
| 永続値の下端未満は下端へ丸める（緩い側へ倒さない） | T-10-2560 |
| Stage 0 の記録も区分の上限で切る | T-10-2567 |
| 値域（1%〜25%）・明示指定の検証・利用者のみ・履歴 | T-10-2550・T-10-2551・T-10-2560〜T-10-2563 |

## 配備で変わる挙動

- ATR の経路が既定で無効のため、配備直後に効くのは**明示指定だけ**（指定が空なら挙動は変わらない）。PoC で効かせるには `PUT /risk-controls/settings/high-volatility` で指定する。
- `StopWidthFloor:Atr14:Enabled=true` の構成では、ATR 比 ≥ 4% の銘柄の数量が equity の 5% に下がる（既存試験 T-10-2196 の ATR 5 の行を 6 株 → 1 株へ改めた）。

## 残余リスク

- 判断のプロンプトの「1 注文上限」の行は区分外の値のまま（LLM は数量を決めないため判断は変わらない。数量は統制が決める）。
- SC-02 の表示・入力と BFF の経路は無い（ADR-0063 フォローアップ 5・人間）。
- 同一銘柄の複数回の新規建てで累計 5% を超え得る（ADR-0063 決定 4。未決）。
- SC-03 の「1 注文上限」は区分外の値を出す。

［2026-10-11 追記 / #1291（独立監査の指摘への対応）］
- 永続行の比率が下端未満のときは、下端（0.01）へ丸めて読むよう改めた。改める前は既定の 0.05 として読んでいた（保存値より緩い側に倒れていた）。上端超と比率の欠けた行は、既定の 0.05 のまま。
- 受け手側の試験を足した。
  - REST 経路の往復と、逆シリアル化に失敗したときの安全既定（T-10-2565・T-10-2566）。
  - Stage 0 の記録の区分の経路（T-10-2567）。
- PoC の既定構成ではマージしただけでは何も変わらないこと、明示指定は検証されず書き誤ると黙って効かないことを、IADR-0527 に追記した。

