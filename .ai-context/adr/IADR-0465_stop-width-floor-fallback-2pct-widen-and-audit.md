---
title: IADR-0465 新規建ての損切り幅に下限（ATR が得られない間は参照価格の 2%）を掛け、割った幅は下限まで広げ、結果を判断の記録（TradeDecisionMade）に載せて監査に残す
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-04, FR-11, FR-15, ADR-0003, ADR-0018, ADR-0040, ADR-0048, ADR-0049, IADR-0460, IADR-0003, IADR-0030, IADR-0035, IADR-0099, IADR-0107, IADR-0318, IADR-0397]
author: claude (Claude Code)
created: 2026-09-30
updated: 2026-10-03
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0049_stop-width-floor-atr14-widen-no-ceiling.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# IADR-0465: 新規建ての損切り幅に下限を掛け、監査に残す（#1120）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-09-30
- 決定者: Claude Code（実装）。統制の値と振る舞いは計画 ADR-0049（利用者裁定 2026-09-30・planning#703）が決め、本 IADR は適用位置・監査の形・暫定の供給口を決める

## 起点・関連

- 関連する計画書 ID: FR-10（1 取引あたりリスク・銘柄別損切りラインの既定値）・FR-04（取引判断）・FR-11（監査）・FR-15（Stage 0）
- 計画 ADR: ADR-0049（本件の統制）・ADR-0003（AI は判断し、制約は決定的なコードで強制する）・ADR-0018 決定 1（§5 を `TradingDefaults` で固定）・
  ADR-0048 決定 3（日足を判断へ流す条件）・ADR-0040（損切りの実行機構は下限を掛けた後のラインを使う）
- 関連する実装仕様書: [`.ai-context/specs/20260930_1120_stop-width-floor.md`](../specs/20260930_1120_stop-width-floor.md)
- 前提: [IADR-0003](IADR-0003_position-sizing-responsibility.md)（サイジングは取引判断）・[IADR-0099](IADR-0099_current-price-context-for-decision.md) 決定 2（アンカー）・
  [IADR-0107](IADR-0107_base-currency-conversion.md)（幅はローカル通貨・サイジングは基準通貨）・[IADR-0318](IADR-0318_stage0-ai-decision-record-and-replay.md)（Stage 0 は本番と同じ `PositionSizer`）・
  [IADR-0397](IADR-0397_composition-wiring-guard.md)（省略可能な依存は明示的に登録する）
- 改める: [IADR-0460](IADR-0460_stop-width-observability-log-only.md) 決定 1（監査イベントの項目は足さない）・決定 4（プロンプトへの指針は足さない）

## 背景

計画 ADR-0049 は、損切り幅を AI が提案し系が下限を掛けること、下限を 1.0 × ATR(14, 日足)（得られないときは参照価格の 2%）とすること、
下限を割った幅は下限まで広げて見送らないこと、監査に AI の幅・下限とその出所・適用した幅を残すことを定めた。配備までの暫定手段は
「退避の 2% だけで下限を先に強制する」である（ATR の経路は ADR-0048 決定 3 の条件の後）。

実装（origin/develop 79abb596）は LLM の幅をそのまま使い、検証は `0 < 幅 < 参照価格` だけだった。PoC（2026-09-29）で META の幅約 0.6% が
約 50 分後に通常の値動きで刈られた。IADR-0460 は観測ログだけを足し、監査イベントの項目は「下限を強制するときに改めて決める」としていた。

## 決定

1. **下限は取引判断の新規建ての経路（`TradeDecisionAppService`）で、AI の幅の検証の後・サイジングの前に掛ける。**
   - 順序: ① `0 < AI の幅 < アンカー後の参照価格`（壊れた出力は従来どおり `StopLossDistanceInvalid` で見送る。下限で救わない）→
     ② 下限を求める → ③ 適用する幅 ＝ max(AI の幅, 下限)（広げたか ＝ AI の幅 ＜ 下限。ちょうど下限は広げない）→
     ④ 適用する幅 ≧ アンカー後の参照価格なら見送る（`StopLossDistanceInvalid`。ロングのラインが 0 以下になる。2% では起こらず、将来の ATR の極端な値だけに当たる。
     価格未満へ縮めると下限を割るため縮めない）→ ⑤ サイジング・採算・ライン・発注意図・観測ログ・監査はすべて適用した幅を使う。
   - **下限はアンカー後の参照価格で求める**（ラインを引く価格と同じ。LLM の参照価格で求めると、窓の間に上がった分だけ下限を割る。作業仕様書の窓の表）。
   - 下限の供給口 `IStopWidthFloorSource`（銘柄・市場・アンカー後の価格 → 下限と出所、得られなければ null）を置く。本番は
     `NoAtrStopWidthFloorSource`（常に null）を明示的に登録する（IADR-0397）。null・0 以下・出所未指定・例外（キャンセルを除く）は
     **参照価格 × 2%**（出所 `Fallback2Pct`。`TradingDefaults.StopWidthFloorFallbackRatio`）。下限が得られないことを理由に見送らない。
   - 下限の端数は丸めない。純関数 `StopWidthFloorPolicy`（`Fallback` / `Resolve` / `Apply`）に置く。
2. **監査は判断の記録 `TradeDecisionMade` に既定 null の末尾項目 `StopWidth`（`StopWidthFloorApplication`）を足して残す。**
   - 項目: `AiWidthPerShare`・`FloorPerShare`・`FloorSource`（共有契約の列挙 `StopWidthFloorSource`: `Unspecified = 0`・`Fallback2Pct = 1`・`Atr14 = 2`。0 を有効値にしない）・
     `AppliedWidthPerShare`・`Widened`。値はすべて 1 株あたり・ローカル通貨。
   - 新規建てだけが値を持つ。決済・owner 手仕舞い・自動縮小は null（0 で埋めない）。
   - 監査の本文はイベント全量なので自動で載る。要約には値があるときだけ `・損切り幅 <適用>（AI <AI>・下限 <下限> <出所>[・下限まで拡大]）` を根拠文の前に足す。
   - **IADR-0460 決定 1 を改める。** 同決定は「観測量は統制の入力ではない」を理由に契約へ足さなかった。下限は統制であり、ADR-0049 決定 3 が
     「広げた事実が読めなければ AI の提案の傾向を評価できない」として監査に残すことを求めた（ログの保持期間は監査台帳の 7 年より短い）。
   - `OrderIntent` には載せない（発注執行・台帳〔approved_orders の明示写像〕へ流れ、発注に使わない）。新イベントにしない
     （判断 1 件に 1 行で `DecisionId`・数量・ラインと同じ行に載る。新イベントは購読・キュー・型名・監査の網羅を増やす）。
   - 追加のみ（`event-schemas.baseline.json` に登録）。購読側（リスク管理・市場監視・報告書）は項目を読まない。
3. **観測ログ（IADR-0460 決定 3）に下限・出所・適用した幅・広げたかを足す。** `stopWidth`・比率・倍率は **AI の幅**のまま
   （AI の提案の傾向を測るという IADR-0460 の意味を変えない）。`stopLossPrice` は適用した幅から引いたライン。
4. **本判断のプロンプトのリスク制約節に「損切り幅は当日の値動きより広く取る。系が下限を掛け、下回る幅は下限まで広げる」の案内を足す**
   （ADR-0049 決定 5 の暫定運用。数値は書かない＝ATR の供給で変わる。一次スクリーニングは幅を決めないので足さない）。
   **IADR-0460 決定 4 を改める**（裁定が出て、案内を暫定運用として求めた）。
5. **Stage 0 の記録（`Stage0DecisionRecorder`）も同じ純関数で下限（記録の参照価格 × 2%）を掛けてからサイジングする**（本番と同じ
   `PositionSizer` を使う規律。ADR-0049 決定 2 は Stage 0 でも同じ値を計算すると定める）。記録の型は変えない（各票の生の幅は AI の値のまま）。
6. **「ATR 連動」の自称を ADR-0049 の定義へ揃える。** コメント・docs は「下限 ＝ 1.0 × ATR(14)。ATR は未供給で参照価格の 2% が効いている」と書く。
   凍結記録（IADR-0003・IADR-0030・IADR-0035・IADR-0460）は本文を残し、追記ブロック `［2026-09-30 追記 / #1120］` を足す。

## 採らなかった案

- **下限を割ったら見送る**: ADR-0049 決定 3 に反する（狭い幅を好む限り取引が減る）。
- **下限を LLM の参照価格、または max(LLM, アンカー後) で求める**: 前者は価格が上がる窓で下限を割り、後者は下がる窓で定義より広げる（作業仕様書の窓の表）。
- **`OrderIntent` に載せる**: 発注執行・台帳の列（明示写像）を巻き込み、発注に使わない値を運ぶ。
- **新イベント（例: 幅を広げた事実）**: 広げなかった判断の幅が残らず、AI の提案の傾向（広げた割合）を分母つきで数えられない。
- **広げた幅が参照価格以上のとき、価格未満へ縮めて発注する**: 下限を割る。ラインが成立しない注文を出すより見送る。
- **下限を呼値へ切り上げる**: 丸めは発注執行（`MoomooPriceRounding`）の責務であり、刻みの規則を 2 か所に持つことになる（残余に記す）。

## 結果

- 新規建ての幅は下限（今は参照価格の 2%）を割らない。PoC の META（アンカー 724.85・幅 4.50）は 14.497 まで広がる。
- 幅が 4% 以下なら 1 注文上限（25%）が株数を決めるため、既定の構成では株数は変わらない（縮小係数が掛かると広げた幅が株数を減らす＝安全側）。
- 監査台帳で、判断ごとに AI の幅・下限・出所・適用した幅・広げたかが読める。
- **残余**:
  - ATR(14) は未供給で、一律 2% が効く（値動きの小さい銘柄では ATR より広くなり得る。ADR-0049 の結果）。ATR の実装は `IStopWidthFloorSource` を差し替える（別 issue）。
  - ブローカーへ送る発火価格の丸め（`MoomooPriceRounding.RoundTrigger`。早く発火する側）は、ラインを最大 1 刻み未満だけ内側へ寄せ得る（S0 / S3。S1 は丸めないラインを使う）。
  - ギャップ・約定のずれでラインを越えた分は 1% の計算の外にある（ADR-0049 決定 3）。
  - 決済の判断（`StopWidth` は null）と、LLM を経ない経路（owner 手仕舞い・自動縮小）は対象外。

## ［2026-10-03 追記 / #1122］ATR(14) の供給と、決定1・2・4・5 の改め（[IADR-0486](IADR-0486_stop-width-floor-atr14-flag-and-floor-marker-on-order-intent.md)）

本文は当時の決定として残す。#1122（計画 ADR-0049 の本体・オーナー裁定 2026-10-03 の案 A）で次のとおり改めた。

- **決定1（供給口）**: `IStopWidthFloorSource` は価格を受け取らない問い合わせ（`GetFloorAsync(symbol, market)`）・as-of の問い合わせ（`GetFloorAsOfAsync`）・`IsEnabled` を持つ。
  本番は `StopWidthFloor:Atr14:Enabled`（既定 false）で `Atr14StopWidthFloorSource`（1.0 × ATR(14)・日足の口は出来高と共有）と `NoAtrStopWidthFloorSource` を選ぶ。
  下限は判断ごとに**プロンプトの前に 1 回だけ**読む（決済・壊れた出力の判断でも読む。無効なら読まない）。2% の退避をアンカー後の参照価格で求めることは変わらない。
- **決定2（`OrderIntent` には載せない）を改める**: 発注意図に末尾の既定 null の印 `StopFloorSource`（下限を掛けてラインを引いた出所）を足す。発注執行が発注結果の記録と予約の行に残し、
  既存の S1 への遡及（IADR-0472）が印のある行を広げない。判断の記録 `StopWidth` の形は変えない。
- **決定4（数値は書かない）を改める**: ATR の下限が有効な構成では、本判断のリスク制約節に ATR と下限の値（得られなければ「未提供・参照価格の 2%」）を書く。無効なら従来と同じ。
- **決定5（Stage 0 は 2%）を改める**: Stage 0 も判断時点の前営業日までの確定足から本番と同じ ATR を求めて下限にする（無効なら 2% のまま）。
- 残余の「ATR(14) は未供給」は、供給の実装が入った（既定は無効のまま。有効化は取得枠の回復周期の記録の後に利用者が行う）。

