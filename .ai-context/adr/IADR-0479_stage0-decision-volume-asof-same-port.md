---
title: IADR-0479 Stage 0 の記録へも判断時点の前営業日までの確定足から出来高と 20 日平均比を渡す。as-of の日足は判断と同じ口・同じ切り替え・同じ期間と切り方で取り、キャッシュしない。無効なら要求 0 回でプロンプトは従来と同じ
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-02, FR-15, UC-01, ADR-0048, ADR-0033, ADR-0036, ADR-0044, IADR-0467, IADR-0451, IADR-0442, IADR-0387, IADR-0318, IADR-0397]
author: claude (Claude Code)
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0048_decision-volume-from-daily-kline-within-existing-source.md (決定 2: Stage 0 では判断時点の前営業日までの確定足から同じ値を計算して渡す)
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md (決定 2: その時点までの情報だけ)
  - planning:projects/ai-stock-trading/07_adr/ADR-0036_stage0-input-completeness-and-split-fixation.md (決定 1: 復元できない項目)
---

# IADR-0479: Stage 0 の記録へ出来高と 20 日平均比を渡す（#1139）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-02
- 決定者: Claude Code（実装）。渡す値と Stage 0 で計算すること自体は計画 ADR-0048 決定 2 が決めており、本 IADR は経路・切り替え・取得の形を決める。

## 起点・関連

- 起票: [#1139](https://github.com/endazon/ai-stock-trading/issues/1139)（#1127 の監査 medium。IADR-0467 の残余リスク「Stage 0 の記録は出来高を『未提供』のまま」の追跡）
- 関連する計画書 ID: FR-04（判断の材料）・FR-02（取引サイクル）・FR-15（Stage 0 の記録）・UC-01（手順 3）
- 計画 ADR: ADR-0048 決定 2・ADR-0033 決定 2・ADR-0036 決定 1
- 関連する実装仕様書: [`.ai-context/specs/20261001_1139_stage0-decision-volume.md`](../specs/20261001_1139_stage0-decision-volume.md)（母集合・窓の表・試験・自己変異）
- 前提: [IADR-0467](IADR-0467_decision-volume-from-daily-kline-via-order-execution.md)（本番の出来高の経路）・
  [IADR-0451](IADR-0451_scheduled-decision-intraday-price-context.md) 決定 5（Stage 0 の値動きの行）・
  [IADR-0442](IADR-0442_stage0-asof-watchlist-reconstruction.md)（as-of の監視銘柄のデコレータ）・
  [IADR-0387](IADR-0387_asof-input-reconstructability-and-population-exclusion.md)（再構成可否の申告）・[IADR-0318](IADR-0318_stage0-ai-decision-record-and-replay.md)（記録器）

## 背景

記録器は `TradeDecisionPromptBuilder.Build` に `volume:` を渡さず、`DecisionVolume:Enabled=true` でも Stage 0 は常に従来の「出来高: 未提供」の行で記録していた。
有効化した本番の判断とは入力（指紋・戦略 ID）が食い違い、記録が「本番で走る判断」を表さない。ADR-0048 決定 2 は「Stage 0 では、判断時点の前営業日までの確定足から
同じ値を計算して渡す。当日の足は使わない」と定める（issue 本文は「決定 1」と書くが、原文の位置は決定 2）。

## 決定

### 決定 1: as-of の日足は判断と同じ口 `IDailyBarsProvider` に足し、同じ期間と切り方で取る（キャッシュしない）

- `IDailyBarsProvider.GetConfirmedBarsAsOfAsync(symbol, market, tradingDay)` を足す。`tradingDay` は Stage 0 の AsOf（市場ローカルの日付）。
- `CachedDailyBarsProvider` は本番の取得と**同じ静的関数**で期間（前営業日の 45 暦日前〜取引日の前日）を求め、応答を切る（取引日以降を捨てる・重複は先の 1 本・昇順）。
  2 つのメソッドが同じ関数を呼ぶので、片方だけ窓がずれることは無い。
- 🔴 **as-of の取得は本番のキャッシュを読まない・書かない**（鍵は今の取引日。過去日の取得で今日の取得を置き換えない／今日の取得を過去日へ返さない）。
  銘柄 × 記録の平日ごとに 1 回撃つ。失敗も覚えない（記録は 1 判断時点を 1 回しか組まない）。
- 米国株だけ（日本株は要求せず null）。例外は null（ログ）、キャンセルは伝える。`NoOpDailyBarsProvider` は要求しない。
- 理由: 別の口・別の設定にすると 2 か所目の切り替えができ、本番と Stage 0 が食い違い得る（#1140 で設定が 2 か所あることの扱いに手間を払ったのと同型）。
  本番のキャッシュを共有すると取引日の鍵が衝突する。

### 決定 2: `AsOfDecisionInput` が出来高を持ち、型の側で as-of を守る

- コンストラクタの引数 `volume`（`DailyVolumeContext?`）と `Volume`、`WithVolume`。**`volume.PreviousDay >= AsOf` は例外**（前日終値 `previousClose` と同じ規律。
  AsOf ちょうどの足も判断時点では確定していない）。
- null は「判断の出来高が無効」であり、プロンプトは従来の `VolumeNotProvidedLine`。取得できない日は `DailyVolumeContext.Unavailable`（`VolumeUnavailableLine`）。
- `WithWatchlist` は出来高を保ち、`WithVolume` は監視銘柄とその理由を保つ（デコレータの順に依らない）。
- 記録器は `Build(... volume: input.Volume)` を渡す（本番と同じ構築・同じ表示）。

### 決定 3: 供給はデコレータ `DailyVolumeAsOfDecisionInputProvider`。切り替えは判断サービスと同じ singleton の口の `IsEnabled`

- 内側が入力を返し、口が有効で入力にまだ出来高が無いときだけ `GetConfirmedBarsAsOfAsync` を引き、`DailyVolumeContext.From`（本番と同じ純関数）で計算して `WithVolume` する。
- 無効（既定）は**要求 0 回・入力は変えない**＝プロンプト・指紋・戦略 ID は develop と一字一句同じ。
- 口の例外は「未提供」にして記録を続ける（本番の `GetDailyBarsSafeAsync` と同じ）。キャンセルは伝える。
- 組み立て（`Program.cs`）: `Watchlist(Volume(No…))`。出来高のデコレータには判断サービスと同じ singleton の `IDailyBarsProvider` を渡す
  （`DecisionVolume:Enabled` と `OrderExecution:BaseUrl` の 1 か所の選択。IADR-0397 の明示的な登録）。

### 決定 4: 出来高を再構成可否の申告（as-of 入力の種別）に足さない

- ADR-0048 決定 2 は「前日までの値は復元できるため、ADR-0036 決定 1 の『復元できない項目』には当たらない」とする。取得できない日は本番と同じく
  「未提供」と書いて記録し、合否の母集団からは外さない（本番の判断も取れない日は同じ入力で動く）。

## 採らなかった案

| 案 | 採らなかった理由 |
| --- | --- |
| Stage 0 用に別の口・別の設定キー | 2 か所目の切り替えができ、本番と Stage 0 が食い違い得る |
| as-of でも本番のキャッシュを使う | 鍵は今の取引日。過去日を書くと今日の取得を奪い、逆も起きる |
| 記録期間の全体を 1 回で取って日ごとに切る | 取得は減り前復権の基準も揃うが、送り手の範囲の上限（400 日）と分割の扱いが増える。既定は無効で、銘柄 × 平日に 1 回で足りる |
| 取れない日を (b)〜(e) と並ぶ「再構成不可」として申告する | ADR-0048 決定 2 と食い違う。本番も取れない日は「未提供」で動く |
| 供給側の注意に委ね、型で AsOf を検査しない | 供給側の実装に依らず先読みを止める（ADR-0033 決定 2 の規律を型に置く既存の作法） |

## 結果

- 有効化すると、Stage 0 の記録は本番と同じ出来高の行（前営業日の確定値・20 日平均比）を持ち、同じ入力なら指紋が一致する。無効（既定）は従来どおり。
- 試験: T-10-2030〜T-10-2038（`docs/tests/FR-10_risk-controls-tests.md`）。自己変異 12 個がすべて赤（作業仕様書）。

## 残余リスク

- 本番の as-of 入力の実供給（方針・価格・参考情報）は依然として無い（`NoAsOfDecisionInputProvider`）。記録が作られるのは実供給が入ってから。
- Stage 0 の記録を走らせると、日足の取得が銘柄 × 平日の回数だけ加わる。取得枠は銘柄単位で増えない（#1117 の実測）が、発注執行の自制（60 秒に 25 回）に掛かって記録が遅くなる。
  記録の対象銘柄が監視銘柄の外なら、その分だけ枠の使用中の銘柄が増える。
- 過去日の足も「今の時点の」前復権で取る。比は分割の基準に依らないが、出来高の絶対値は当時の表示と違い得る（本番の値も前復権）。
- 臨時休場（構成の `TradeCycle:Holidays`）の翌営業日は本番と同じく「未提供」になる。
