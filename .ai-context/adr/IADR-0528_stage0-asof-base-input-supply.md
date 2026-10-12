---
title: IADR-0528 Stage 0 の記録器の as-of 入力の基底を、判断日の終値の時刻で切った内部の記録（確定日報・発注執行の日足）と既存の鍵で取れる過去の API（Finnhub・SEC EDGAR・FRED の ALFRED）で組み、backtest の日足は発注執行の日足の照会から取る
type: impl-adr
status: Accepted
related_ids: [FR-15, FR-04, FR-07, FR-08, FR-01, ADR-0033, ADR-0036, ADR-0044, ADR-0046, ADR-0023, ADR-0048, ADR-0054, ADR-0064, ADR-0016, ADR-0011, IADR-0318, IADR-0387, IADR-0442, IADR-0451, IADR-0467, IADR-0479, IADR-0486, IADR-0498, IADR-0157, IADR-0105, IADR-0454, IADR-0474, IADR-0351]
author: claude (Claude Code)
created: 2026-10-13
updated: 2026-10-13
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md (決定 2・§残るもの・フォローアップ 4)
  - planning:projects/ai-stock-trading/07_adr/ADR-0036_stage0-input-completeness-and-split-fixation.md (決定 1)
  - planning:projects/ai-stock-trading/07_adr/ADR-0044_watchlist-in-decision-prompt-and-stage0-asof.md (決定 3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md (決定 5)
related_specs:
  - ../specs/20261013_1308_stage0-asof-base-input.md
---

# IADR-0528: Stage 0 の as-of 入力の基底の供給と backtest の日足（#1308・A-3）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-13
- 決定者: 利用者裁定 2026-10-13（下の裁定 1〜3）を実装する判断。実装は Claude Code。

## 起点・関連

- 起票: [#1308](https://github.com/endazon/ai-stock-trading/issues/1308)。記録の再実施: #1219・#1299。値の欄: #1320
- 前提（覆さない）: IADR-0318（記録・再生）・IADR-0387（再構成可否の申告と母集団からの除外）・IADR-0442（当時の監視銘柄）・IADR-0451（前日終値と当日の値は不明）・IADR-0479／IADR-0486（出来高・ATR の as-of）・IADR-0498（二段の記録）・IADR-0467（発注執行の日足の照会）・IADR-0105／IADR-0157（backtest の過去データ源のポート）
- 仕様書: `.ai-context/specs/20261013_1308_stage0-asof-base-input.md`

## コンテキスト

- 計画 ADR-0033 §残るもの は、過去時点の情報をどこから復元するかを「実装が記録器を作る過程で判明する」とした。復元できない入力は申告して合否から外す（ADR-0036 決定 1）。
- 再生は、数量を持つ判断を 1 件でも外したら判定を組まない（IADR-0387 決定 3 の 2026-09-24 追記）。
  したがって、(b)(c)(d)(e) を**当時の本番と同じ答え**で組める日以外は、Buy/Sell を出せば評価が成立しない。
- 承認済みの窓（2026-07-01〜10-09）の前半は、システム稼働前で (c) 確定日報と (e) 監視銘柄が存在しない（PoC の `SeededAt`＝2026-09-15 16:30Z）。
- backtest の日足は ADR-0023 決定 5 の確認 2 点（取得枠の回復周期・前復権と費用モデルの整合）が済むまで流さないとされ、backtest の Pod には OpenD の接続先も鍵も無い（IADR-0467 の実測）。

### 裁定（利用者裁定 2026-10-13。記録: [#1219 のコメント](https://github.com/endazon/ai-stock-trading/issues/1219#issuecomment-6134235872)）

1. **窓は 2026-09-15 以降に縮める（〜2026-10-09）。監視銘柄と確定日報が無い期間は対象外にする。代替入力の計画 ADR は起こさない。** 承認額 1,700 円は上限として据え置き、新しい窓の見積りは値の PR（#1320）で計算し直す。値の PR は #1308 の後にマージする。
2. **(b) は、Finnhub の company-news・SEC EDGAR・FRED（ALFRED）の過去分を、本番と同じ規則（新しい順・168h・上位 K 件）で組めば「再構成できた」とみなしてよい。欠けた源（google-news・確定報告書の RAG）は、記録に「欠けた源」として残す。**
3. **A-3 は、Stage 0 では前復権と費用モデルの不整合を受け入れる。借株料が抜ける点は残余リスクとして記録する。backtest の足は、発注執行の `/daily-bars` 経由で取る（backtest の Pod に OpenD は配線しない）。**

## 決定

### 決定 1: 判断時点は「判断日の通常取引の終値の時刻」とし、すべての入力をそこで切る

- 判断時点 t ＝ AsOf の通常取引の終値の時刻（市場の現地時刻 → UTC。米国は 16:00 ET。`MarketSessions.RegularClose`・`MarketHours.ZoneOf`）。
- 理由: 再生は判断日の終値の情報で判断し、翌営業日の始値で約定する（`BacktestSimulator`）。参照価格は判断日の終値である（IADR-0451 決定 5）。入力を終値の時刻で切れば、価格・方針・参考情報が同じ時点に揃う。
- 型の側の規律（`AsOfDecisionInput` が AsOf より後を例外・除外にする）は維持し、供給側でも t で切る（二重に守る）。

### 決定 2: (a) 価格は発注執行の日足（前復権）の判断日の足から取る。休場は null、照会の失敗は記録を止める

- `[AsOf − 10 暦日, AsOf]` を照会し、日付が AsOf の足の終値を参照価格、AsOf より前の最後の足の終値を前日終値にする。
- AsOf の足が無い（`Available` で足が無い＝休場）なら null（判断を発明しない。#1308 受け入れ基準 3）。
- 照会の失敗（`Unavailable`・非 2xx・例外）は間を置いて撃ち直し、なお取れなければ**例外で記録を止める**。
  「判断しない」に倒すと、その日の判断が記録から黙って消え、再生の経路が AI の実際の経路から変わる（記録が無い日は見送りと同じに再生される）。止めれば記録集合は書き出されず、合格へは倒れない。

### 決定 3: (c) 方針は「t までに確定した日報のうち期間の始まりが最も新しいもの」。無ければ null

- 報告書サービスに `GET /reports/daily-policy/as-of?at=<t>`（`OwnerOrService`）を足す。`State=Confirmed` かつ `ConfirmedAt ≤ t` の日報のうち `PeriodStart` 最大を返す。無ければ 404。
- 確定済みの報告書は不変である（`UpsertDraft` が拒否する）。したがって答えは**時刻 t に本番の `GET /reports/daily-policy` が返した値と一致する**（再構成ではなく当時の値そのもの）。
- 無い日は null（判断しない）。本番も確定日報が無ければ取引しない（FR-07）。裁定 1 の「確定日報が無い期間は対象外」をこの形で守る。
- `Date > AsOf` の答えは null（警告）。型が未来の方針を例外にするため、受け取る前に落とす。
- 照会の失敗は決定 2 と同じく記録を止める。

### 決定 4: (b) 参考情報は 3 源を本番の取得規則で組み、欠けた源を申告の理由欄に残す

- 銘柄の源: finnhub-news（`/company-news?from&to`。`datetime`）・sec-edgar（submissions の `acceptanceDateTime`。構成した CIK だけ。銘柄は submissions の最初の ticker）。
- 市場の源: fred（ALFRED。`realtime_start = realtime_end = AsOf − 1 日` の版。観測日を発行時刻にする本番の扱いのまま）。版を前日にするのは、当日の公表が t より後かもしれないため（先読みしない側へ寄せる）。
- 規則（本番の `KnowledgeBaseRetrievalContextProvider` と同じ）: 銘柄の文書と市場の文書を別々に、`publishedAt ∈ [t − MaxAge, t]`・新しい順・上位 `TopK`。`TopK`・`MaxAge` は本番の構成（`Retrieval:TopK`・`Retrieval:MaxAgeHours`）を読む。出所タグは収集と同じ `[種別, 源]` とし、`RetrievalSourcePolicy` で限定する。
- 申告:
  - 3 源がすべて取れた日は、残った件数に応じて `Reconstructed`／`AbsentAtAsOf`（従来の導出）。**申告の理由欄に欠けた源（google-news・finnhub の現在値の文書・確定報告書の RAG）を残す**（裁定 2）。
  - 3 源のどれかが失敗・未構成なら (b) は `NotReconstructable`（理由欄に失敗した源と欠けた源）。合格へは倒れない。
- 本番との差（KB の並びは更新時刻・finnhub の現在値の文書が銘柄の上位を占め得る）は裁定 2 で受け入れた。

### 決定 5: (d) 為替は基準通貨の市場だけを扱う

米国株は基準通貨（USD）の市場でレートは定義から 1 である。基準通貨でない市場は当時のレートを供給しないため null（未対応。警告）とする。記録の対象は 12 銘柄とも米国株であり、裁定の範囲で欠けるものは無い。

### 決定 6: サイジング文脈は固定値で与える

当時の残枠・連敗・DD は再構成できない（記録は保有なしで固定し、数量は再生で再計算しない。IADR-0318・IADR-0351）。次の固定値にする。

| 項目 | 値 | 理由 |
| --- | --- | --- |
| 資金 | 1,000,000 | 再生の初期資金（`Stage0EvaluationService.InitialCapital`）と同じ。数量と再生の資金の尺度を揃える |
| 段階残枠 | 資金と同じ | 保有なし |
| 当日の発注残枠 | 資金 × 1 日の発注上限比（`TradingDefaults`） | 当日まだ発注していない |
| 連敗・DD | 0 | 保有なし・再生の始まり |
| モード | `MoomooSimulate` | Stage 0 の本番（経路B）のブローカ |
| 統制値 | `TradingDefaults.CreateRiskLimits()` | 計画の既定値（シミュレータのプロファイルも比率は同じ） |
| 損切りの実行機構 | null（未供給） | 当時の設定を再構成しない。プロンプトは「不明」と書く |

### 決定 7: 配線は基底の供給の口がそろったときだけ。記録の実行は従来の閂が止める

- `Reports:BaseUrl` と `OrderExecution:BaseUrl` がともに絶対 URL のときだけ基底の供給を使う。そうでなければ従来の `NoAsOfDecisionInputProvider`（警告つき）。
- (b) の 3 源は個別に構成する（未構成の源があれば決定 4 により (b) は再構成不可）。
- 記録を走らせるかは従来どおり `Stage0Recording`（無効・未承認・期間なし・出力先なしのいずれでも LLM を呼ばない）が決める。基底の供給はそれより内側でしか呼ばれない。

### 決定 8: backtest の日足は発注執行の日足の照会から取る（`order-execution`）

- 過去データ源に `order-execution` を足す（`Backtest:BarData:Provider=order-execution`・`Backtest:BarData:OrderExecution:BaseUrl`・サービスの資格）。銘柄ごとに `GET /order-execution/daily-bars` を呼び、`Status=Available` の足を使う。それ以外は欠測（`Gaps`）として残す。期間が照会の上限（400 暦日）を超えるときは分けて呼ぶ。
- backtest の Pod に OpenD は配線しない（裁定 3）。既存の `moomoo`（直結）は残すが、values では選ばない。
- ADR-0023 決定 5 の確認 2 点の扱い（裁定 3）:
  - 前復権と費用モデル: 費用モデルは配当・借株料を持たない。前復権の値動きは配当を暗に含む（二重計上は起きない）。**借株料は抜ける**（空売りの成績を良く見せる）。Stage 0 ではこれを受け入れ、残余リスクとして残す。
  - 取得枠: 単位は銘柄単位・総量 300（IADR-0467 の実測）。Stage 0 の 12 銘柄は収まる。回復周期は未測定のまま。
  - 取得の経路は判断の出来高と同じ（実 OpenD での疎通は #1117 の検証口で確かめた経路）。

### 決定 9: 評価の窓の終端を構成で固定できるようにする（記録の `To` と揃えるため）

- 今の backtest は評価の窓を `[今日 − LookbackDays, 今日]` で取り、再生は「記録の `To` ≥ 窓の終端」を求める（`Stage0ReplayEvaluation.Validate`）。記録の窓は 2026-10-09 で終わる（裁定 1）ため、2026-10-10 以降のどの日に評価しても `RecordingMismatch` になる（#1320 の AI レビューの指摘）。
- backtest の PR で、評価の窓の終端を固定する任意の構成（`Backtest:Stage0:EvaluationEnd`。日付）を足す。空なら従来どおり「今日」。値を入れると窓は `[終端 − LookbackDays, 終端]` になる。経路B では `2026-10-09` を入れる想定（値は #1320 か PoC の手順で入れる）。
- 窓の終端を過去に固定しても先読みにはならない（記録も足も窓の中だけを使う）。固定しないまま記録の `To` を延ばす案は、見積りと承認の出し直しが毎日要るので採らない。

## ADR 制約からの逸脱の受容（ADR-0023 決定 5）

- **逸脱の内容**: 計画 ADR-0023 決定 5 は「実装側で確認を要する 2 点（取得枠の単位と回復周期／前復権と費用モデルの整合）を確認するまで、本番のバックテストへ流さない」と定める。本 IADR の決定 8 は、**回復周期の実測と、費用モデルの整合（借株料）を済ませないまま**、Stage 0 の評価へ日足を流す。
- **適用範囲**: **Stage 0 の記録と評価に限る**（取引判断の記録器が読む as-of の価格と、backtest の Stage 0 の駆動が読む日足）。判断の出来高・ATR の有効化条件（ADR-0048 決定 3）と、Stage 0 以外のバックテストの扱いは変えない。
- **根拠**: 利用者裁定 2026-10-13（[#1219 のコメント](https://github.com/endazon/ai-stock-trading/issues/1219#issuecomment-6134235872) の裁定 3）。受け入れた残余リスクは次の 2 つである。
  1. **借株料の欠落**: 費用モデルに借株料の項が無く、空売りを含む判断の成績が借株料のぶん良く見える（料率の単位は planning#740 で未確定）。
  2. **回復周期の未測定**: 取得枠（銘柄単位・総量 300）の回復周期を測っていない。Stage 0 の 12 銘柄は総量に収まるが、銘柄を入れ替えて繰り返すと枠を使い切り得る。
- **計画への環流は不要と判断した**: CLAUDE.md の禁止事項は「計画書に反する実装。差異が必要な場合は、新 IADR または planning への issue 起票で根拠を残す」と定める。本件は**利用者自身が裁定済み**で、計画の値や原則を変えるものではない（適用範囲を Stage 0 に限った受け入れ）。したがって根拠は本 IADR に残し、planning への issue は起こさない。決定 5 の確認 2 点が済んだ時点で、本節の受け入れは不要になる。

## 結果

- 記録器は、裁定 1 の窓（2026-09-15〜）で (c)(e) を当時の本番と同じ答えで組める。(b) は裁定 2 の 3 源で組み、欠けた源が記録に残る。
- backtest は日足を得て、記録の再生まで進む（評価の窓の終端は決定 9 の構成で記録の `To` に揃える）。

## 配備で変わる点

- trade-decision に `OrderExecution__BaseUrl`（と (b) の 3 源の構成）を入れると、基底の供給が有効になる。**`Stage0Recording__Enabled=true` で配備した起動から記録が LLM を呼び始める**（#1320 の値）。配備は窓（`Stage0Recording__From=2026-09-15`）と承認額の出し直しが済んでから行う。
- backtest に `Backtest__BarData__Provider=order-execution` を入れると、Stage 0 の駆動がバーを取るようになる（取れなければ従来どおり `NoHistoricalBars`）。

## 残る制約

- 前復権は取得時点の基準である。窓の後に分割があると、過去の価格の水準が変わる（先読みの手掛かりになり得る）。記録の前に `split-compare` で確かめる（仕様書「分割の確認」）。
- 借株料が費用モデルに無い（裁定 3）。
- 本番の参考情報と記録の参考情報は同じではない（KB の並び・欠けた源）。差は裁定 2 で受け入れ、欠けた源は記録に残る。
- Finnhub の過去分の深さ（無料枠で約 1 年）は未実測。取れなければ (b) は再構成不可になる。
- サイジングは固定値であり、当時の本番の残枠ではない。
- 取得枠の回復周期は未測定（ADR-0048 決定 3 の判断の出来高の有効化条件は変えない。出来高・ATR は無効のまま）。
