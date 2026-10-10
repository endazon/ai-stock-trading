---
title: IADR-0527 高ボラティリティ銘柄の区分と 1 注文上限（equity の 5%）を 1 つの純関数に置き、統制値（上限・明示指定）はリスク管理の設定に持ち、審査は発注意図が運ぶ ATR(14) で同じ区分を判定する
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-04, FR-11, UC-06, ADR-0063, ADR-0049, ADR-0042, ADR-0018, IADR-0130, IADR-0486, IADR-0495, IADR-0500, IADR-0003, IADR-0151, IADR-0161, IADR-0134]
author: claude (Claude Code)
created: 2026-10-11
updated: 2026-10-11
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0063_high-volatility-symbol-order-cap.md (決定 1〜6・フォローアップ 1〜4)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5 の 2 行)
related_specs:
  - ../specs/20261011_1291_high-volatility-order-cap.md
---

# IADR-0527: 高ボラティリティ銘柄の 1 注文上限を 3 か所で同じ関数にする（#1291）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-11
- 決定者: 計画 ADR-0063（利用者裁定 2026-10-10・planning#782 / planning#787）を実装する判断。実装は Claude Code。

## 起点・関連

- 起票: [#1291](https://github.com/endazon/ai-stock-trading/issues/1291)。裁定: 計画 ADR-0063・§5 の 2 行
- 前提（覆さない）: IADR-0130（equity 比で保持し判定時に解決）・IADR-0486（ATR(14) の経路・既定無効）・IADR-0495（最小の名目額と LLM の前の見送り）・IADR-0500
- 仕様書: `.ai-context/specs/20261011_1291_high-volatility-order-cap.md`

## コンテキスト

ADR-0063 は区分（ATR(14) ÷ 参照価格 ≥ 4% と利用者の明示指定の併用）と区分の上限（equity の 5%）を定め、フォローアップ 3 で「サイジング・審査・LLM の前の見送りの 3 か所が食い違うと、サイジングの数量が審査で拒否される（#29 の形）か、見送りの判定が実際の数量と合わなくなる」と書いた。実装では次が問題になる。

- ATR(14) は取引判断だけが持つ（IADR-0486）。審査（リスク管理）には ATR の経路が無い。
- 明示指定の置き場。計画のフォローアップ 1 は「監視銘柄の設定に明示指定の項目を足す」と書くが、監視銘柄（市場監視）は AI の入れ替え案（ADR-0042）からも書かれる。

## 決定

### 決定 1: 区分の判定と上限は 1 つの純関数 `HighVolatilityOrderCap` に置く

- 置き場はリスク管理の Domain（`TradingDefaults`・`MinimumEntryNotional` と同じ。取引判断は extern alias で同じ型を呼ぶ）。
- 呼ぶのは **サイジング・LLM の前の見送り・審査・Stage 0 の記録**の 4 か所。式を呼び出し側に書かない。
- 上限 ＝ 区分外は既存の `RiskLimitSettings.MaxOrderAmountFor`、区分は `min(区分外, equity × 区分の比率)`。区分外の上限を 5% より下げた構成でも、区分が区分外より緩くならない（決定 5「常に厳しい方」）。
- 既定値 `HighVolatilityMaxOrderAmountRatio = 0.05`・`HighVolatilityAtrRatioThreshold = 0.04` は `TradingDefaults` に置き、試験で固定する。しきい値 4% は構成で変えない（計画は構成可と定めていない）。
- 拒否理由は区分外と同じ `PerOrderAmountExceeded`。同じ「1 注文あたりの発注金額上限」の統制であり、理由を分けると監査・画面・gRPC の写像の追随が要るのに判定の意味は変わらない。

### 決定 2: 統制値（区分の上限・明示指定）はリスク管理の設定に持つ

- `RiskManagementSettings.HighVolatility`（`MaxOrderAmountRatio`・`DesignatedSymbols`）。変更は `PUT /risk-controls/settings/high-volatility`（OwnerOnly・理由必須・値域外 400・前後値つき履歴 `HighVolatilityChanged`＝序数 10）。
- 値域は「最小の名目額の比率の既定（1%）以上 〜 25% 以下」。下端は取引判断の構成値（`Sizing:MinEntryNotionalRatio`）ではなく既定値で持つ（構成値はリスク管理から見えない。構成値を上げた場合は区分の銘柄が LLM の前に見送られる＝厳しい側）。
- 監視銘柄（市場監視の `MonitoredSymbol`）には置かない。理由:
  1. ADR-0063 決定 1「AI の入れ替え案からは指定させない」を構造で守れる（リスク管理の設定は利用者だけが変え、AI の経路が無い）。
  2. 審査（最終防衛線）が他サービスの照会なしに自分の設定だけで判定できる。照会が落ちても明示指定は効く。
  3. §5 の統制値と同じ変更手続き（UC-06・理由必須・履歴）に乗る。
- 計画のフォローアップ 1 の「監視銘柄の設定に」は置き場の例示と読み、計画の決定（監視銘柄ごとに利用者が指定できる）は満たす。監視銘柄に無い銘柄も指定できるが、厳しい側にしか効かない。
- 永続化は設定行 JSON のキー `highVolatility`。旧行は既定（5%・明示指定なし）で読み、値域外の比率は既定へ倒す（IADR-0161 決定 2 と同じく既存行を書き換えない）。

### 決定 3: 審査は発注意図が運ぶ ATR(14) で同じ区分を判定する

- `OrderIntent.Atr14`（ローカル通貨・null 許容）を足す。取引判断は新規建ての判断で読んだ ATR をそのまま載せる。審査は `Atr14 ÷ Price`（Price はアンカー後の参照価格＝サイジングと同じ値）で自動判定する。
- null（ATR が得られない・無効・決済・保護レグ）は明示指定だけで判定する（ADR-0063 決定 1）。
- 取引判断が ATR を偽れば審査の自動判定を外せるが、取引判断は同じ ATR でサイジングしており、審査で ATR を独立に求める経路（日足）はリスク管理に無い。明示指定は審査が自分の設定で判定するため、ここは外れない。
- サイジング文脈（REST `SizingContextView.HighVolatility`・gRPC `HighVolatilityControls`）で統制値を取引判断へ渡す。未供給（旧応答・安全既定）は既定（5%・明示指定なし）で効かせる。

### 決定 4: LLM の前の見送りは、必要なときだけ ATR をその場で読む

- 明示指定は手元で分かるので、そのまま区分の上限で `CapacityCannotReach` を判定する。
- 自動判定は「区分外の上限なら最小の名目額に届くが、区分の上限では届かない」とき、かつ ATR の経路が有効で現在値があるときだけ、その場で ATR を 1 回読む。読んだ値は以後の下限の適用・プロンプト・発注意図にそのまま使う（判断ごとに 1 回の規律＝IADR-0486 決定 2 を保つ）。
- 現在値が無い（LLM の参照価格を使う構成）・ATR が得られないときは明示指定だけで判定する。見送らないだけで、サイジングが同じ上限を掛ける（LLM の費用の最適化であって統制ではない）。

## 結果

- 3 か所（＋Stage 0 の記録）の上限が一致する。サイジングの数量は審査を通り、1 株多ければ落ちることを試験で固定した（T-10-2553・T-10-2554）。
- 既定の構成（ATR 無効・明示指定なし）では挙動は変わらない。ATR を有効にした構成では ATR 比 ≥ 4% の銘柄の数量が下がる（既存試験 T-10-2196 の 1 行を改めた）。

## 残る制約

- 判断のプロンプトの「1 注文上限」の行は区分外の値のまま（数量は統制が決める）。
- SC-02 の表示・入力・BFF の経路は無い（ADR-0063 フォローアップ 5）。それまでの指定は owner 権限の API で行う。
- 同一銘柄の複数回の新規建ての累計（ADR-0063 決定 4 の未決）は塞がない。
