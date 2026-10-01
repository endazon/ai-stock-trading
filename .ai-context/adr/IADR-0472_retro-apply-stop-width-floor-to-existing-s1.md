---
title: IADR-0472 損切り幅の下限を、下限の導入前に建てた Active・未到達の S1 の損切りラインへ、取得単価を基準に広げる向きだけ遡及する（常駐ガードの巡回の先頭で冪等に当て、監査に残し、取引台帳のラインも追随させる）
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-11, UC-02, ADR-0049, ADR-0040, ADR-0003, IADR-0465, IADR-0344, IADR-0389, IADR-0396, IADR-0461, IADR-0466, IADR-0393, IADR-0399, IADR-0397]
author: claude (Claude Code)
created: 2026-10-01
updated: 2026-10-01
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0049_stop-width-floor-atr14-widen-no-ceiling.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 の S1)
---

# IADR-0472: 損切り幅の下限を既存の S1 の建玉へ遡及する（#1136）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-01
- 決定者: Claude Code（実装）。遡及すること・基準・向き・対象・監査の項目は #1136 のオーナー裁定（2026-10-01）が決め、本 IADR は適用の位置と時機・競合の塞ぎ方・監査の形・台帳の追随を決める

## 起点・関連

- 関連する計画書 ID: FR-10（損切りラインの既定値・生成 AI は上書きできない）・FR-11（監査）・UC-02
- 計画 ADR: ADR-0049（損切り幅の下限。決定 1「損切りの実行機構は下限を掛けた後のラインを使う」）・ADR-0040 決定 1（S1）・ADR-0003
- 関連する実装仕様書: [`.ai-context/specs/20261001_1136_retro-stop-floor.md`](../specs/20261001_1136_retro-stop-floor.md)
- 前提: [IADR-0465](IADR-0465_stop-width-floor-fallback-2pct-widen-and-audit.md)（新規建ての下限）・[IADR-0344](IADR-0344_s1-software-stop-loss.md)（S1）・
  [IADR-0389](IADR-0389_rearm-software-stop-on-confirmed-unfilled-close.md)（再武装は到達の記録を残す）・[IADR-0396](IADR-0396_protective-stop-optimistic-concurrency.md)（楽観並行）・
  [IADR-0461](IADR-0461_close-quantity-subtracts-in-flight-closes.md) / [IADR-0466](IADR-0466_s1-close-cancels-in-flight-decision-close.md)（処理中の決済の見分け方）・
  [IADR-0393](IADR-0393_most-protective-stop-line-per-entry-lot.md)（台帳のラインは保有中のエントリーのうち最も保護的な 1 本）・[IADR-0397](IADR-0397_composition-wiring-guard.md)（省略可能な依存の配線）

## 背景

IADR-0465 は新規建ての幅に下限（今は参照価格の 2%）を掛けた。それより前に建てた建玉の S1 のラインは下限を割ったままである
（9/30: NVDA 1.84%・AMZN 1.16%・MSFT 約 1.5%）。オーナーは遡及を裁定した:
新しいライン ＝ min(今のライン, 取得単価 × (1 − 下限))（空売りは max(今のライン, 取得単価 × (1 ＋ 下限))）、広げる向きだけ、
Active な S1 の保護記録だけ（到達済み・決済が処理中は触らない）、監査に旧ライン・新ライン・出所。

調査（作業仕様書）で次が分かった。

- S1 の行（`ProtectiveStopOrder.TriggerPrice`）は武装後に書き換える経路が無い。行は取得単価を持たず、エントリーの発注記録の平均約定価格が持つ。
- 到達は 2 段で判定する。市場監視が**取引台帳のライン**で `StopLossTriggered` を出し、発注執行が**行自身のライン**で判定し直して武装する。
  ただし武装は**候補を読んだ写し**で判定しており、`Update` の中（最新の行）では判定し直していなかった。
- 取引台帳の承認行のライン（`approved_orders.StopLossPrice`）を書き換える経路も無い。発注執行の行だけを広げると、旧ラインと新ラインの間の価格で
  市場監視が毎巡回（60 秒）到達の Critical を出すのに、発注執行は決済しない。

## 決定

1. **常駐ガード（`ProtectiveStopGuard.RunOnceAsync`）の巡回の先頭で、毎回冪等に当てる。**
   Active 行を読み、エントリーの約定を確定した直後、建玉照会と評価の前に `SoftwareStopFloorRetrofitter.ApplyAsync` を呼ぶ。
   ガードは起動直後に遅延なく初回を回すので、再起動・取り込み・遅れて約定が確定した行も最初の巡回で覆う。min / max は下限のラインで安定し、
   2 回目以降は何も書かない。建玉照会が不明（null）で巡回を据え置く回でも当てる（ラインの是正は建玉に依らない）。遡及の失敗は巡回を止めない。
   一回だけの移行（Migration・管理コマンド）は採らない（後から現れる行を取りこぼし、手順が人に依存する）。
   本番は Program.cs（moomoo 構成）が遡及の口をガードへ明示的に渡す（IADR-0397。T-10-1933 が本番の組み立てで固定）。
2. **規則**（純関数 `StopWidthFloorRetrofitPolicy`）: 下限 ＝ 取得単価 × 2%（出所 `Fallback2Pct`）。下限のライン ＝ 取得単価 ∓ 下限。
   買い建ては下限のラインが今のラインより低いときだけ、売り建ては高いときだけ書き換える（等しい・狭める向きは変えない）。端数は丸めない
   （S1 は数値で比べる。丸めた発火価格を持つ S0 / S3 は対象外）。取得単価はエントリーの発注記録（`Open`・約定 1 株以上・平均価格 ＞ 0）。
   無ければ当てない（次の巡回で当て直す）。
3. **対象と除外**: `IsSoftwareStop`・`Active`・`TriggeredAt == null` だけ。到達済み（再武装した行を含む。IADR-0389 は到達の記録を残す）・完了・
   `AwaitingEntry`・S0・S3 は触らない。**決済が処理中の群**（同じ銘柄・市場・方向）は、その巡回では触らない。見分け方は IADR-0461 決定1・4 と同じ
   （非終端の Close の記録から Active な S0 / S3 の保護レグ〔`StopOrderId` / `StopDecisionId`〕を除き、ブローカーが非終端と答えたもの）。
   確かめられない（照会 null・例外）ものは数えない —— 恒久に照会できない古い記録で遡及が永遠に止まらないようにするためで、
   実際に処理中でも広げる向きなので損切りを早めない（IADR-0466 決定3 と同じ倒し方）。
4. **競合を 2 か所で塞ぐ。**
   - 遡及の書き込みは `stops.Update`（保存先の最新の行で対象の条件と規則を判定し直し、版が一致したときだけ書く）。到達の購読が先に武装していれば書かない
     （到達の記録を巻き戻さない）。衝突し続けたら書かずにエラーログで次の巡回へ回す。
   - 🔴 **武装（`SoftwareStopExecutor.OnTriggeredAsync` の `Update`）は、最新の行のラインで `Reached` を判定し直す。** 候補を読んだ後に遡及が広げていたら、
     古いラインでの到達で武装しない（新ラインが正）。到達済みの行のラインは遡及が動かさないので、既存の挙動は変わらない。
   - 作業仕様書の窓の表（前の端だけ／後の端だけ／両端 × 増える側・減る側・対照）で、前の端だけの形が両側で破れることを示し、両端を採った
     （判定の権威は後の端。前の端は書き込みを起こさないための絞り込み）。
5. **監査は新しい事実 `SoftwareStopLineWidened` を広げた行ごとに 1 件出す。** 項目: `EntryDecisionId`・銘柄・市場・`EntrySide`・`EntryPrice`（取得単価）・
   `PreviousStopLossPrice`（旧ライン）・`StopLossPrice`（新ライン）・`FloorPerShare`・`FloorSource`（共有契約の `StopWidthFloorSource`。今は常に `Fallback2Pct`）・`OccurredAt`。
   既存の事実で行のラインの変化を表すものは無い（`SoftwareStopArmed` は武装、`SoftwareStopExecuted` は発動の結果で、旧ラインを持たない）。
   追加のみ（`event-schemas.baseline.json` に登録）。監査台帳の要約は「旧 → 新（取得単価・下限・出所・広げる向きだけ）」。通知はしない。
   Information ログ「ソフトウェア逆指値の損切りラインを下限まで広げました（遡及）」を出す。
6. **取引台帳のラインを追随させる。** リスク管理が `SoftwareStopLineWidened` を購読し（`SoftwareStopLineWidenedLedgerHandler`）、承認行
   （DecisionId ＝ `EntryDecisionId`・`Open`・同じ方向）の `StopLossPrice` を広げる向きのときだけ書き換える（`IPortfolioLedgerStore.WidenStopLoss`。EF・インメモリ）。
   null（不明のライン）・決済の承認・方向違い・狭める向き・同じ値・承認なしは何もしない。再配送・順序の入れ替わりでも広い方に収束する。
   **並行しても狭い値で上書きしない**: 関係 DB では判定と書き込みを 1 文の条件付き UPDATE（`ExecuteUpdate`。WHERE が広げる向きのときだけ一致）にまとめる。読んでから書く形だと、2 通の追随が同じ旧ラインを読んで両方「広げる向き」と判定し、後勝ちが広い値を狭い値で上書きし得る（PR の AI レビューの指摘）。InMemory プロバイダ（単体試験）は `ExecuteUpdate` を持たないので読んでから書く経路のまま。実 PostgreSQL での固定は T-10-1934（統合試験）。
   市場監視と建玉の照会は台帳から読むので、到達もこのラインで出る。承認行は追記専用だったが、この列だけは広げる向きの書き換えを許す例外とする。
7. **下限の比率の単一情報源を共有契約へ移す。** `StopWidthFloorDefaults.FallbackRatio = 0.02`（`AiStockTrading.Shared.Contracts.Trading`）を置き、
   `TradingDefaults.StopWidthFloorFallbackRatio` はそれを指す（`StopLossApproximation.DefaultRatio` と同じ作法。IADR-0399）。発注執行はリスク管理を参照しない。
   ATR の供給口 `IStopWidthFloorSource` は取引判断の中にあり、発注執行からは使えないため、遡及の出所は今は常に `Fallback2Pct` である。

## 採らなかった案

- **一回だけの移行（Migration で列を書き換える・管理コマンド）**: 取り込み・遅れて約定が確定した行・再起動の後に現れる行を取りこぼす。冪等な巡回で足りる。
- **到達の購読の中（`OnTriggeredAsync`）で当てる**: 到達が来るまで当たらず、台帳のラインも追随しない（Critical が出続ける）。
- **ブローカーの建玉の平均取得単価を基準にする**: 銘柄単位の純額の平均で、同じ銘柄の別のエントリー・外部の売買と混ざる。行ごとのエントリーの約定価格の方が裁定の「その建玉の取得単価」に近い。
- **処理中が確かめられないときは触らない**: 照会できない古い記録が残る環境で遡及が永遠に止まる。広げる向きなので損切りを早めない。
- **発注執行の行だけを広げる（台帳を追随させない）**: 旧ラインと新ラインの間で、決済しない到達の Critical が毎分出続ける。
- **既存の事実（`SoftwareStopArmed` / `SoftwareStopExecuted`）に結果の種類を足す**: 旧ラインを持たず、購読側（台帳・通知・報告書）の分岐に新しい値が流れ込む。

## 結果

- 稼働中の 3 建玉は、配備後の最初のガードの巡回で NVDA 226.52 → 226.2036（取得 230.82 のとき）、AMZN 245.14 → 243.0498（248.01）、MSFT 503.98 → 501.6718（511.91）になる
  （取得単価は裁定の「約」の値から逆算。実際の値はエントリーの発注記録の平均約定価格による）。
- 配備後に確かめるログ（発注執行）: `ソフトウェア逆指値の損切りラインを下限まで広げました（遡及）: 銘柄=NVDA 建玉方向=Buy 旧ライン=226.52 → 新ライン=226.2…`
  （銘柄ごとに 1 回。2 巡目以降は出ない）。リスク管理: `台帳の損切りラインを発注執行の遡及に追随させました`。監査台帳: 種別 `SoftwareStopLineWidened`。
  見送ったとき: `ソフトウェア逆指値の損切りラインの遡及を見送ります（同じ建玉の決済が処理中です…）`（毎巡回出る）。
- 広げた分だけ 1 回の損切りの損失は大きくなるが、幅 2% は 1 注文上限（25%）の効く 4% より狭いため、ラインでの損失は資金の 0.5% 以内に収まる（ADR-0049 決定 3 の計算）。
- **残余**:
  - ATR(14) は発注執行から得られず、遡及の下限は一律 2%。ATR の供給（#1122）で新規建ての下限が 2% より広くなったとき、遡及も ATR を使うかは見直す。
  - 取得単価は行ごとのエントリーの平均約定価格であり、ブローカーの建玉全体の平均取得単価とは違い得る。新規建てでも、約定が参照価格より有利だった行は
    取得単価基準の下限が参照価格基準より外側になり、遡及がさらに広げる（損失はサイジングで想定した幅の内側に留まる）。
  - 発注執行が広げてから台帳が追随するまで（メッセージの往復）、市場監視は旧ラインで到達を出し得る（Critical の通知が出るが、発注執行は武装しない）。
  - 決済が処理中の群は、処理中のあいだ毎巡回見送りのログが出る。
  - 実機（SIMULATE）での確認は配備後。
