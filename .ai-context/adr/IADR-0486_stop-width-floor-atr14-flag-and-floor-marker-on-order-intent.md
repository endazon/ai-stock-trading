---
title: IADR-0486 損切り幅の下限を 1.0 × ATR(14, 日足) にする（独立した設定で既定は無効・日足の口は出来高と共有・Stage 0 も同じ値）。下限を掛けてラインを引いた印を発注意図に載せ、発注執行が発注結果と予約の行に残して既存の S1 への遡及から外す
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-04, FR-11, FR-15, ADR-0049, ADR-0048, ADR-0003, ADR-0023, ADR-0033, ADR-0040, IADR-0465, IADR-0472, IADR-0467, IADR-0479, IADR-0460, IADR-0397, IADR-0057, IADR-0074]
author: claude (Claude Code)
created: 2026-10-03
updated: 2026-10-03
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0049_stop-width-floor-atr14-widen-no-ceiling.md (決定1〜4)
  - planning:projects/ai-stock-trading/07_adr/ADR-0048_decision-volume-from-daily-kline-within-existing-source.md (決定3: 日足を判断へ流す条件)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5「損切り幅の下限」)
---

# IADR-0486: 損切り幅の下限を 1.0 × ATR(14, 日足) にし、下限を掛けた印で遡及から外す（#1122）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-03
- 決定者: Claude Code（実装）。統制の値（1.0 × ATR(14)・2% の退避・広げる・1 注文上限は緩めない）は計画 ADR-0049 が決めている。
  遡及との食い違いの塞ぎ方（案 A・予約の経路も閉じる）は **オーナー裁定 2026-10-03**。本 IADR は経路・設定・印の形を決める。

## 起点・関連

- 起票: [#1122](https://github.com/endazon/ai-stock-trading/issues/1122)（planning#703 の ADR-0049 の本体。#1120 で入れた差し込み口へ ATR(14) を供給する）
- 関連する計画書 ID: FR-10（ATR 連動・銘柄別損切りラインの既定値）・FR-04（判断の材料）・FR-11（監査）・FR-15（Stage 0）
- 計画 ADR: ADR-0049 決定1〜4・ADR-0048 決定3・ADR-0003・ADR-0023 決定5
- 関連する実装仕様書: [`.ai-context/specs/20261003_1122_atr14-stop-floor.md`](../specs/20261003_1122_atr14-stop-floor.md)（母集合・窓の表・試験・自己変異）
- 前提: [IADR-0465](IADR-0465_stop-width-floor-fallback-2pct-widen-and-audit.md)（新規建ての下限・監査）・[IADR-0472](IADR-0472_retro-apply-stop-width-floor-to-existing-s1.md)（既存の S1 への遡及）・
  [IADR-0467](IADR-0467_decision-volume-from-daily-kline-via-order-execution.md)（日足の口・前復権・当日の足の除外・有効化の設定）・[IADR-0479](IADR-0479_stage0-decision-volume-asof-same-port.md)（Stage 0 は同じ口・同じ計算）・
  [IADR-0057](IADR-0057_order-dispatch-idempotency.md) / [IADR-0074](IADR-0074_reservation-reconciliation.md)（発注前の予約と突合）・[IADR-0397](IADR-0397_composition-wiring-guard.md)
- 改める: [IADR-0465](IADR-0465_stop-width-floor-fallback-2pct-widen-and-audit.md) 決定1（供給口の形）・決定2（「`OrderIntent` には載せない」）・決定4（「数値は書かない」）・決定5（Stage 0 は 2%）。
  [IADR-0472](IADR-0472_retro-apply-stop-width-floor-to-existing-s1.md) 追記 2 の残余（ATR の行の取り違え）を塞ぐ。いずれも日付つき追記を置いた。

## 背景

- #1120（IADR-0465）は下限の差し込み口 `IStopWidthFloorSource` を置き、本番は ATR を返さない `NoAtrStopWidthFloorSource` を登録した。
- #1118（IADR-0467）は判断側から日足（前復権・前営業日までの確定足・銘柄 × 取引日のキャッシュ）を読む口 `IDailyBarsProvider` を作った（`DecisionVolume:Enabled`・既定無効）。
  #1117 の実測で、前復権は価格も出来高も分割に合わせて調整し（ATR の分割日の True Range も歪まない）、当日を含めると未確定の当日足が返る。取得枠は銘柄単位、回復周期は未測定。
- #1136（IADR-0472）の遡及は「ラインを引いた価格（`PlannedPrice`）から 2% 未満のライン」を下限の導入前の行と見分ける。**ATR の下限は参照価格の 2% より狭いことがあり、
  その行を導入前の行と取り違えて 2% まで広げる**（サイジングの想定より広い損切り＝ADR-0049 決定1 に反する）。IADR-0472 追記 2 の残余として、#1122 の前提に置かれていた。
- 🔴 **オーナー裁定（2026-10-03）: 案 A、予約の経路も閉じる。** 発注意図に「下限を掛けてラインを引いた」印を末尾の既定 null の項目で足し、取引判断が新規建てで立て、
  リスク管理はそのまま運び、発注執行が発注結果の記録と予約の行に残す（突合が組み直す記録にも残る）。遡及は印のある行を外す（null の行は従来の判定のまま）。

## 決定

1. **ATR の下限は独立した設定 `StopWidthFloor:Atr14:Enabled`（既定 false）で有効にする。日足の口は出来高と 1 つの singleton を共有する。**
   - 読みは既存の `DecisionVolume:Enabled` と同じく `bool.TryParse`（キーなし・読めない値〔`yes` 等〕は無効）。一貫性を優先し、読みの規則を 2 つ持たない（`DailyBarsComposition.IsOn`）。
   - 共有の口（DI キー `daily-bars-shared`）は、どちらかの設定が有効で `OrderExecution:BaseUrl` が絶対 URL のときだけ `CachedDailyBarsProvider`（Http）を作る。どちらも無効なら NoOp（要求 0 回）。
     キャッシュを共有するので、同じ銘柄の同じ取引日の取得は 1 回で、取得枠を増やさない。
   - 判断と Stage 0 の出来高の口（`IDailyBarsProvider`）は、出来高の設定が無効なら NoOp（ATR だけ有効でも出来高は従来の「未提供」の行）。有効なら共有の口。
   - `IStopWidthFloorSource` は ATR の設定が有効で共有の口が要求を出せる（`IsEnabled`）ときだけ `Atr14StopWidthFloorSource`、それ以外は `NoAtrStopWidthFloorSource`。明示的に登録する（IADR-0397）。
   - 🔴 **有効化は出来高と同じ条件**（ADR-0048 決定3 の確認 1＝取得枠の回復周期を IADR に記録した後に利用者が行う。ADR-0049 決定2）。本 PR では有効にしない。
2. **下限は判断ごとに 1 回だけ、プロンプトの前に読み、同じ値をプロンプト・適用・発注意図の印・監査・観測ログへ使う。**
   - ATR は 1 株あたりの値幅で価格に依らないので、供給口の問い合わせは参照価格を受け取らない（`GetFloorAsync(symbol, market)`）。2% の退避はこれまでどおりアンカー後の参照価格で求める（`StopWidthFloorPolicy.Resolve`）。
   - 読む位置は出来高と同じ（LLM を呼ぶ前の見送りの判定の後）。無効（`IsEnabled=false`）なら読まない（要求 0 回）。例外（キャンセルを除く）は null＝2%。キャンセルは伝える。
   - 🔴 IADR-0465 の「決済・壊れた出力では供給口を読まない」は改める（プロンプトに値を出すため、判断の前に読む）。日足はキャッシュされ、取得枠は銘柄単位なので増えない。
3. **ATR(14) の計算と下限（純関数 `AverageTrueRange` と `StopWidthFloorPolicy.FromAtr`）。**
   - True Range ＝ max(高値 − 安値, |高値 − 前日終値|, |安値 − 前日終値|)。ATR(14) ＝ 前営業日で終わる**最後の 15 本**の足から 14 本の True Range の単純平均（Wilder の平滑化は使わない）。
   - 得られないとき null（→ 2%）: 足が無い・最後の足が期待する前営業日でない（古い・公開の遅れ・臨時休場の翌日）・15 本未満・窓の 15 本に壊れた足（高値 ＜ 安値・高値 / 安値 / 終値が 0 以下）・ATR が 0 以下。
   - 当日の未確定の足は口（`CachedDailyBarsProvider.Confirm`）が捨てる。分割は前復権の足（1 回の取得で揃える）に依り、関数は補正しない。端数は丸めない。
   - 下限 ＝ `TradingDefaults.StopWidthFloorAtrMultiple`（1.0）× ATR、出所 `Atr14`。本数は `TradingDefaults.StopWidthFloorAtrPeriod`（14）。両値は `TradingDefaultsTests` で固定する（§5）。
   - 🔴 **ATR の下限が参照価格の 2% より狭くても、そのまま下限にする**（2% と比べて広い方を採らない。ADR-0049 決定2 の定義どおり）。
   - 🔴 **1 注文上限（25%）は緩めない**（ADR-0049 決定4）。広げた幅が参照価格以上なら見送る（IADR-0465 決定1 ④）も変えない。
   - プロンプト（本判断のリスク制約節。一次スクリーニングには出さない）: 有効で得られたら「損切り幅の下限: <下限>（1 株あたり。ATR(14, …) <ATR> の 1.0 倍）…下回ると系が下限まで広げる」、
     得られなければ「ATR(14, 日足) は未提供…下限は参照価格の 2%」。値は小数 4 桁で四捨五入して見せる（適用は丸めない値）。**無効なら行を出さない**（従来と一字一句同じ）。
     IADR-0465 決定4 の「数値は書かない」を改める（#1122 が ATR と下限をプロンプトに出すことを求めた）。
   - 観測ログ（IADR-0460 決定3）に `atr14`（出所が ATR のときの値。それ以外は「不明」）を足す。監査（`TradeDecisionMade.StopWidth`）の形は変えない（下限・出所が既に載る）。
4. **Stage 0 も判断時点の前営業日までの確定足から同じ値を計算する**（ADR-0049 決定2。IADR-0479 の同じ口の原則）。
   - 供給口に `GetFloorAsOfAsync(symbol, market, tradingDay)` を足す（`IDailyBarsProvider.GetConfirmedBarsAsOfAsync`＝本番と同じ期間と切り方・AsOf 以降を捨てる・本番のキャッシュに触れない）。
   - as-of 入力に `StopFloor`（`StopWidthFloorContext?`。null＝無効）を足し、デコレータ `StopWidthFloorAsOfDecisionInputProvider` が埋める（監視銘柄 → 下限 → 出来高 → 入力なし の順）。
     無効なら要求 0 回でプロンプト・指紋・サイジングは従来と同じ。得られない・例外は `Unavailable`（2%）。内側が渡していれば上書きしない。再構成可否の申告には入れない（前日までの値は復元できる）。
   - 記録器のサイジングは `StopWidthFloorPolicy.Resolve(input.StopFloor?.Supplied, 記録の参照価格)`、プロンプトは本番と同じ行（IADR-0465 決定5 を改める）。
5. **発注意図に印 `OrderIntent.StopFloorSource`（`StopWidthFloorSource?`・末尾・既定 null）を足す。** 取引判断は新規建てで `StopWidth.FloorSource`（`Fallback2Pct` / `Atr14`）を立てる。
   決済・保護レグ・利用者の手仕舞い・自動縮小は null。リスク管理は審査で書き換えずに運ぶ（`OrderApproved.Intent`。取引台帳は明示写像で持たない）。発注には使わない。
   - 🔴 **IADR-0465 決定2 の「`OrderIntent` には載せない」を改める。** 同決定は「発注に使わない値を発注執行・台帳へ運ぶ」ことを避けた。遡及（IADR-0472）が「下限を掛けて建てた行」を
     状態から見分ける手段が発注執行の側に要り、判断の記録（`TradeDecisionMade`）は発注執行が購読しない。印は 1 値の列挙で、ブローカーへ送る内容・台帳の列は変わらない。
   - イベント契約の基準（`event-schemas.baseline.json`）は**型の最上位のプロパティ**だけを持ち、入れ子の `OrderIntent` の項目は持たない。再生成しても差分は出ない（実測）。
     契約は旧い本文が null で読めること・往復で保たれること・判断の記録と承認の本文を通して残ることを契約試験（T-10-2201）で固定する。
6. **発注執行は印を発注結果の記録と予約の行に残す（予約の経路も閉じる）。**
   - 通常の発注: 予約（`TryReserve(..., stopFloorSource)`。送る前）と発注結果の記録（`ExecutionRecord.StopFloorSource`。送った後）の両方に承認の発注意図の印を書く。
   - 突合（`OrderReservationReconciler`）: 送信結果が不明のまま滞留した予約を、照会で発注済みと確定したとき、ブローカーの注文（印を持たない）から組み直す記録へ**予約の行の印**を写す。
   - 列は `executed_orders.StopFloorSource`・`order_dispatch_reservations.StopFloorSource`（integer NULL）。マイグレーション 2 本（`AddExecutedOrderStopFloorSource`・`AddReservationStopFloorSource`）。
     **列の追加だけ**で、既存行は null（分からない）として読まれる。既存行を埋める移行はしない。
7. **遡及（`SoftwareStopFloorRetrofitter`）は印のある行を外す。** 判定は `StopWidthFloorRetrofitPolicy.WasFloorAppliedAtSizing`（`Fallback2Pct` / `Atr14` なら true）。
   印の無い（null・`Unspecified`）行は従来どおり `WasSizedBelowFloor`（ラインを引いた価格から 2% 未満）で判定する。前の端（写し）と後の端（最新の行）で同じ判定を使う。

## 採らなかった案

- **遡及を止める**（案 B）: 裁定は案 A。#1120 より前に建てた建玉（印の無い行）を遡及から外すことになる。
- **遡及も ATR で判定する**: 発注執行は日足を持たず、建てたときの ATR は後から変わる（足が増える）。建てた時点の事実（印）で見分けるほうが状態で決まる。
- **時刻の切れ目（#1122 の配備時刻）で見分ける**: IADR-0472 追記 2 と同じ理由（配備時刻をデータが持たず、取り違えると導入前の行を取りこぼす）。
- **印を判断の記録（`TradeDecisionMade`）からだけ読む**: 発注執行は判断の記録を購読しない。新しい購読と保存先が要る。
- **印を保護記録（S1 の行）に置く**: 裁定は発注結果の記録と予約の行。保護記録は S1 以外の手法・再武装・取り込みでも作られ、印の写し先が増える。
- **ATR と 2% の広い方を下限にする**: ADR-0049 決定2 の定義（1.0 × ATR）と違う。2% は ATR が得られないときの退避である。
- **ATR を下限の設定と出来高の設定の 1 つで有効にする**: 出来高（プロンプトの材料）と下限（統制）は有効化の判断が別である。日足の口だけを共有する。
- **設定の読みを厳しくする（読めない値で起動を止める）**: 既存の `DecisionVolume:Enabled` と読みが分かれる。両方を変えるなら別 issue で揃える。
- **Wilder の平滑化**: ADR-0049 決定2 が再現性のために単純平均と定めた。

## 結果

- 既定の構成（両方の設定が無効）では、判断・Stage 0 の挙動・プロンプト・要求の回数は変わらない。新規建ての発注意図・発注結果・予約の行に印（`Fallback2Pct`）が付くことだけが変わる。
- 有効にすると、新規建ての下限は 1.0 × ATR(14)（得られなければ 2%）になり、プロンプトに ATR と下限が出る。2% より狭い ATR の下限で建てた S1 の行は遡及で広がらない。
- 🔴 **残余**:
  - 有効化は未実施（取得枠の回復周期の記録の後に利用者が行う）。実機（SIMULATE）での確認は未実施。
  - 足の欠け（途中の取引日の足が無い）は検めない（最後の足が前営業日で 15 本あれば、欠けをまたいだ True Range で計算する）。
  - 印の無い既存行（#1122 の配備より前の記録）は従来の判定のまま。配備の後、ATR を有効にするまでの新規建ては `Fallback2Pct` の印で遡及から外れる（2% で建てた行なので遡及の対象ではない）。
  - 1 注文上限（25%）を緩める検討は本件の後に別途行う（ADR-0049 決定4 の前提条件）。描画の検査（`check-decision-volume-parity.js`）は出来高の 2 か所の一致だけを見る。ATR の設定は取引判断の 1 か所なので一致の検査は無い。
  - プロンプトの値は判断ごとに 1 回読んだ値で、同じ判断の中では適用と一致する。読んだ後に日足のキャッシュが入れ替わっても、その判断には影響しない。
  - Stage 0 の as-of 経路（`GetConfirmedBarsAsOfAsync`）はキャッシュを読み書きしない。出来高と ATR の両方を有効にすると、同じ（銘柄・as-of）の日足を出来高のデコレータと下限のデコレータがそれぞれ 1 回ずつ取得し、取得枠の消費が 2 倍になる。現状は最内が as-of 入力を返さない（`NoAsOfDecisionInputProvider`）ので要求は 0 回だが、実際の as-of 入力を差し込む段では（銘柄・as-of）単位のメモ化を入れる（独立監査の指摘）。
