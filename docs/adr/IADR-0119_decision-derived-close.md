---
title: IADR-0119 判断由来の建玉効果は保有建玉から決め、保有なし・不明の売りは見送る
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-05, FR-10, FR-19, UC-01, UC-02, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-30
updated: 2026-07-30
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# IADR-0119: 判断由来の建玉効果は保有建玉から決め、保有なし・不明の売りは見送る

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-30
- 決定者: endazon（利用者・#292 起票と設計承認）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-04（AI の売買判断）、FR-05（発注）、**FR-10**（「いずれも手仕舞い（Close）と損切りは止めない」）、
  FR-19（取引ガード）、UC-01 / UC-02、
  [ADR-0003](../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md)（不確実なら Hold）
- 対象 Issue: [#292](https://github.com/endazon/ai-stock-trading/issues/292)
- 関連する実装仕様書: [20260730_292_decision-derived-close](../specs/20260730_292_decision-derived-close.md)
- 関連 IADR: [IADR-0004](IADR-0004_position-effect-entry-scoping.md)（エントリー判定は `PositionEffect`）、
  [IADR-0038](IADR-0038_order-decomposition-position-effect.md)（符号付きゼロ跨ぎ分割・反転は Close+Open）、
  [IADR-0035](IADR-0035_stop-loss-authoritative.md)（損切り価格の権威）、
  [IADR-0076](IADR-0076_trade-decision-profitability-gate.md)（採算ゲート）、
  [IADR-0117](IADR-0117_owner-position-close-path.md)（owner 決済経路）

## 背景・課題

`TradeDecisionService` は発注意図の `PositionEffect` を **`Open` でリテラル固定**していた（IADR-0004 は「必ず設定する」
とだけ定め、判断由来は新規建てと決めていた）。したがって LLM が `Sell` を出しても、それは「保有ロングの決済」ではなく
**新規ショート建て**として扱われる。実害は 4 つ。

1. **統制が逆向きに効く。** `RiskEvaluator` の `isEntry = (PositionEffect == Open)` が真になるため、AI の売却が
   kill switch・pause・日次損失ロックアウト・段階資金上限・同日再エントリー禁止で**ブロックされる**。
   FR-10 本文の「いずれも手仕舞い（Close）と損切りは止めない」と正反対の挙動である。
2. **数量が保有数と無関係。** `PositionSizer` が新規建てサイズを算出するため「保有 4072 株を全部売る」が構造的に出せない。
3. **裸の新規売り。** 保有ゼロで `Sell` が出ると、現物のみ有効な段階でもショート建ての注文がブローカへ飛ぶ
   （取引ガードは `ProductType`／`Market`／禁止銘柄を見るだけで**方向を見ない**）。
4. **存在しない建玉の損切り価格**が算出され台帳へ載る。

結果、自動運転の出口は損切りラインだけで、利確・時間切れ・AI 判断による撤退が 1 つも無い。

## 検討した選択肢

1. **保有建玉を照会し、反対売買なら `Close`・数量＝保有数（全量）で発行する**（採用）。
2. **LLM に `positionEffect` を出力させる**。プロンプトと Parser の拡張で済むが、統制の向きを決める値を
   生成 AI に委ねることになる（ADR-0003 の「AI は統制を上書きできない」に反する）。
3. **リスク管理側で受け取った `Open` を建玉から `Close` へ読み替える。** 判断サービスは無改修で済むが、
   判断ログ・`TradeDecisionMade` と実際の効果が食い違い、監査で「AI は何を意図したのか」が追えなくなる。

## 決定

**選択肢 1 を採る。** 具体的には次の 4 点を決める。

### 決定 1: 建玉効果は純関数 `PositionEffectResolver` で決める

| 保有（符号付き） | 判断 | 効果 | 数量 |
| --- | --- | --- | --- |
| ロング（>0） | `Sell` | **Close** | 保有数（全量） |
| ショート（<0） | `Buy` | **Close** | 保有数（全量） |
| ロング（>0） | `Buy` | Open | サイジング |
| ショート（<0） | `Sell` | Open | サイジング |
| なし（0） | `Buy` | Open | サイジング |
| なし（0） | `Sell` | **見送り** | — |
| 不明（null） | `Buy` | Open | サイジング |
| 不明（null） | `Sell` | **見送り** | — |

**決済は常に全量。** LLM は数量を出力せず、サイジング（リスク基準の新規建てサイズ）は出口の量ではない。
部分利確には LLM 出力の拡張が要り、本 ADR の範囲外とする。

**ゼロ跨ぎは起こらない**（Close は保有数ちょうど）。IADR-0038 の「反転は Close+Open の 2 意図」はゼロを跨ぐ
場合の規約であり、本 ADR は Close レグのみを出す（Open は次サイクルで独立に判断される）。

### 決定 2: 保有なし・不明の売りは見送る（挙動の変更）

従来は裸の新規ショート建てがブローカへ飛んでいた。ADR-0003（「方針の範囲外・不確実な場合は必ず Hold」）に従い
見送る。**これは既存挙動の意図的な変更**であり、現物のみ有効な現段階で成立しない注文を止める安全側の是正である。

`Buy` は不明でも従来どおり `Open`（買いは裸になり得ず、金額系上限がそのまま効くため、不明を理由に取引機会を落とさない）。

### 決定 3: 建玉照会は既存エンドポイントの再利用・失敗は「不明」

新ポート `IHeldPositionProvider` の実体はリスク管理の既存 `GET /risk-controls/open-positions`（`OwnerOrService`）。
**新規エンドポイント・新規イベント・新規構成キーを作らない**（`RiskManagement:BaseUrl` と `risk` HttpClient を再利用）。

契約の中核は **空配列＝0（保有なし）／失敗＝null（不明）の厳格な区別**。市場監視の `HttpPositionStore` は失敗を
空列へ倒す（損切り検知対象なし＝そちらの安全側）が、ここで同じことをすると「保有していない」と誤断定して
裸の新規売りを通す。安全側の向きがサービスごとに異なる例として明記しておく。

### 決定 4: 決済はサイジング・採算ゲート・損切り検証を通さない

| 飛ばす処理 | 理由 |
| --- | --- |
| `PositionSizer` | 出口の数量は保有数であって新規建てのリスク基準サイズではない |
| 採算ゲート（IADR-0076） | **最小期待利益で撤退を止めてはならない**（損失を止めるための決済が採算未達で通らなくなる） |
| 損切り幅の妥当性検証 | 決済注文に損切り価格は無い（`StopLossPrice = null`・建玉側の損切りは IADR-0035 の射影が保持する） |

残す検証は `referencePrice > 0` のみ（価格 0 の注文を投げない）。

発注前スクリーニング（`RiskEvaluator`）は**通す**。`isEntry = (PositionEffect == Open)` により Close は kill switch・
pause・ロックアウト・段階資金上限・同日再エントリーを構造的に素通りする（既存の不変量。回帰テストで固定済み）。
禁止銘柄・市場・商品種別・相場操縦検知は決済にも効いたままだが、これらは「その銘柄を扱えるか」の恒久設定であり、
手仕舞いを妨げる想定の統制ではない。

## 根拠

### なぜ LLM に `positionEffect` を出させないのか（選択肢 2 を採らない理由）

`PositionEffect` は**どの統制が効くかを決める値**である（IADR-0004）。生成 AI がこれを出力できるということは、
「Close と申告すれば kill switch を回避できる」経路を作るのと同じで、ADR-0003 の「生成AIはこれらを上書きできない」に反する。
保有建玉という**観測事実**から決定的に導けば、AI の申告に依存せず同じ結果が得られる。

### なぜリスク管理側で読み替えないのか（選択肢 3 を採らない理由）

`TradeDecisionMade` は監査に載る「AI が何を意図したか」の記録である。判断側が `Open` と言い、統制側が `Close` として
処理すると、監査ログと実際の効果が恒久的に食い違う。効果の決定は、保有建玉を知り得る判断側で行うのが素直である。

## 影響・追随

- **既存挙動の変更が 1 点ある**（決定 2）。保有なし・不明の売り判断は発注に至らなくなる。
  `RiskManagement:BaseUrl` 未設定の環境では **AI は売れなくなる**（従来は裸の新規売りが飛んでいた）。
  経路 B の `values-local.yaml` は trade-decision に既に `RiskManagement__BaseUrl` を与えているため追加設定は不要。
- `Shared.Contracts`・リスク管理・DB スキーマ・Helm / values / compose・実弾ゲート（閂 0〜4）はすべて**不変**。
- 建玉照会は判断 1 件ごとに 1 回の HTTP 往復を追加する（`risk` クライアントのタイムアウト 5 秒・失敗は不明に縮退）。
  判断は既に LLM 往復を含むため、レイテンシ目標（[#203](https://github.com/endazon/ai-stock-trading/issues/203)）への影響は相対的に小さい。
- **部分利確・時間切れ・トレーリングストップは依然として無い。** 出口は「損切りライン」「AI の全量撤退」「owner の手動決済」の 3 つ。
- 信用取引の有効化後は、ショートエントリー（`Sell` × `Open`）が正当な経路として増える。決定 1 の表はその場合も
  そのまま成り立つ（保有ショートへの建て増しは Open）。

## 代替案を採らなかった理由

- 選択肢 2（LLM が効果を出力）: 統制の向きを決める値を生成 AI に委ねることになり、ADR-0003 に反する。
- 選択肢 3（リスク管理で読み替え）: 監査ログ（AI の意図）と実際の効果が恒久的に食い違う。
