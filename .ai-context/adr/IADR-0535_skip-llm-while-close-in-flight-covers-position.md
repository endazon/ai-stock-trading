---
title: IADR-0535 判断由来の決済が処理中で建玉を覆い、どの結論も発注にならない保有銘柄は LLM を呼ぶ前に見送る。判定は発注執行の決済のゲートと共有し、照会の失敗は LLM を呼ぶ側へ倒す。見送りの通知は処理中の決済ごとに 1 回へ畳む
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-10, FR-09, FR-11, UC-01, UC-02, ADR-0003, ADR-0050, IADR-0461, IADR-0463, IADR-0471, IADR-0495, IADR-0500, IADR-0527, IADR-0462, IADR-0520, IADR-0420, IADR-0397, IADR-0521]
author: claude (Claude Code)
created: 2026-10-14
updated: 2026-10-14
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04 取引判断・FR-10 手仕舞いは止めない)
  - planning:projects/ai-stock-trading/07_adr/ADR-0050_decision-close-nets-in-flight-closes-and-stop-line-exit-only-without-mechanical-stop.md (決定 1)
---

# IADR-0535: 処理中の決済が建玉を覆い、どの結論も発注にならない保有銘柄は LLM を呼ぶ前に見送る（#1338）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-14
- 決定者: Claude Code（実装）。IADR-0463 と同じ「結論が発注にならない LLM 呼び出しを省く」費用の最適化であり、統制（決済のゲート・審査）は変えない。
  省く条件は LLM の後の見送りの条件の論理積に限るため、発注される注文の集合は変わらない（裁定は要らない）

## 起点・関連

- 関連する計画書 ID: FR-04（判断の費用）・FR-10（手仕舞いは止めない）・FR-09（通知）・FR-11（監査）・UC-01 / UC-02。計画 ADR-0003・ADR-0050 は変えない。
- 起票: [#1338](https://github.com/endazon/ai-stock-trading/issues/1338)
- 関連する実装仕様書: [`.ai-context/specs/20261014_1338_skip-llm-while-close-in-flight.md`](../specs/20261014_1338_skip-llm-while-close-in-flight.md)（母集合・窓の表・試験・変異の実測）
- 前提:
  - [IADR-0461](IADR-0461_close-quantity-subtracts-in-flight-closes.md): 処理中の決済の数え方（決定 1・4）と、覆えば見送る（決定 2）。同 IADR は判断の側を範囲外としていた。
  - [IADR-0463](IADR-0463_entry-blockers-before-llm.md) 決定 1・4: LLM の前の見送りは「どの結論も発注にならない」ときだけ。照会の失敗・不明は LLM を呼ぶ側へ倒す。
  - [IADR-0471](IADR-0471_held-add-on-blockers-prompt-and-hold.md): 保有中の銘柄の買い増し・売り増しの可否（同じ口）。
  - [IADR-0500](IADR-0500_pre-llm-skip-when-capacity-below-one-share.md) / [IADR-0495](IADR-0495_min-entry-notional-and-decision-exit-same-day-reentry.md) / [IADR-0527](IADR-0527_high-volatility-order-cap-single-function-three-sites.md): 残枠・最小の名目額の判定（同じ関数）。
  - [IADR-0462](IADR-0462_ledger-position-query-status-and-pre-llm-skips.md) 決定 4: LLM の前の見送りは `TradeDecisionForgoneBeforeLlm` を 1 回ごとに出す。
  - [IADR-0520](IADR-0520_stop-loss-arrival-publish-once-at-detection.md) 決定 4: 通知だけを抑止する前例（`StopLossNotificationSuppressor`）。
  - [IADR-0420](IADR-0420_cross-service-read-contract-convention-and-guard.md): 跨サービスの読み取りの契約（送り手の本物の型を受け手の試験で読む）。

## 背景

PoC（2026-10-13 米国セッション）で、AAPL の判断由来の決済（Sell 13 株・347.93 の指値・13:36:44Z 発注）が値の下落で約定しなかった。
その間、5.5 分ごとの定時サイクルで一次（haiku）・二次（sonnet）の LLM が同じ決済を 34 回判断し、うち 33 回は発注執行が
`InFlightCloseCoversPosition` で見送った。sonnet 約 62 円・haiku 約 2.6 円・リスク審査 33 件・監査 33 行・**Discord の見送り通知 33 件**。

LLM の前の見送りは保有 0 の銘柄だけが対象で（IADR-0463 / 0500 / 0527）、保有中の銘柄は「決済の判断を残す」ため常に LLM を呼ぶ。
判断の側には処理中の決済を知る口が無い（記録と OpenD の接続は発注執行にだけある）。

## 決定

1. **省く条件（LLM の前・一次スクリーニングの前）**: 保有が既知で 0 でない銘柄で、次の両方が成り立つときだけ LLM を呼ばずに見送る。
   - (a) 発注執行が「今この方向（ロング → Sell・ショート → Buy）の決済を送れば、処理中の決済が決済方向の建玉を覆うため見送る」と答えた（下の 2）。
   - (b) 買い増し・売り増し（処理中の決済と**逆方向**の判断）が LLM の後で**必ず**見送られると LLM の前に分かる: 出口専用／換算レートが新規建てに使えない／
     実結線で未約定の新規建てが不明／その方向の新規建てが審査で必ず拒否される（IADR-0471 の口）／段階残枠・日次残枠の小さい方が現在値 × 1 株に満たない
     （`PositionSizer.CannotAffordOneShare`）／使える金額が最小の名目額に届かない（`MinimumEntryNotional.CapacityCannotReach`。1 注文上限は区分外の大きい方で比べる）。
     どれも LLM の後の見送りと同じ条件・同じ関数であり、新しい規則を作らない。
   - Hold は発注しない。(a) で決済の結論は発注にならない。(b) で新規建ての結論も発注にならない。よって**発注される注文の集合は変わらない**。
   - 🔴 (b) を条件に含めるのは、逆方向の判断（買い増し）を塞がないためである。(b) が成り立たなければ (a) を照会しない（ブローカーへの往復を伴い得るため）。
   - (a) が「覆わない」（建玉が処理中の決済より多い＝建玉を超える残りの決済が出せる）なら LLM を呼ぶ。
   - 記録は既存の口へ新しい理由 `InFlightCloseCoversPosition` を末尾に足して載せる（`DecisionForgoneBeforeLlmReason`・`DecisionSkipReason`。同名）。
2. **判定の共有と照会の口**: 発注執行の数え方（`CountInFlightClosesAsync` の本体）と「覆う」の判定（`sendable <= 0`）を
   `Features/OrderExecution/InFlightCloses.cs` へ移し、決済のゲートと新しい照会の口 `GET /order-execution/in-flight-closes?symbol&market&closeSide`
   （OwnerOrService・読み取り専用・`InFlightClosesQueryService`）の両方がそれを呼ぶ。
   - 照会の口は、建玉照会の能力・保護記録ストアが無い構成（内蔵 paper）では Unknown を返す（ゲートもその構成では処理中の決済を引かない）。
   - 記録に処理中の候補が無ければブローカーへ照会せず NotCovered。候補があれば建玉を照会し（不明 → Unknown）、**照会の後に候補を読み直して**生死を確かめて数える
     （IADR-0461 決定 4 と同じ順序）。決済方向の建玉が 0 なら NotCovered（ゲートは別の理由 `BrokerPositionAbsent` で扱う。判断の側は省かない）。
   - 判断の側はアダプタ（`HttpInFlightCloseProvider`。名前付きクライアント `order-execution`）越しに呼ぶ。DB を直接読まない（#1324 の前例）。
     `OrderExecution:BaseUrl` が無ければ NoOp（常に不明）。受け手の DTO は全項目 nullable で、別銘柄・別方向・欠落・未知の状態は不明へ倒す。
3. 🔴 **照会の失敗の倒し方: 不明（LLM を呼ぶ）へ倒す。** 照会の失敗・タイムアウト・未結線・不正な応答・発注執行の Unknown はすべて「覆うとは言えない」として LLM を呼ぶ。
   - IADR-0463 決定 4（新規建ての前段の見送り）と同じ向きである。
   - 逆（不明なら見送る）は、照会が落ちている間すべての保有銘柄の決済の判断を止める。損切りの判断が止まり、FR-10「手仕舞いは止めない」に反する。
     こちらの向きの害は「是正前と同じだけ LLM を呼ぶ」費用だけである。
4. **見送りの通知を 1 回に畳む**: `OrderDispatchForgone` に任意の末尾項目 `InFlightCloseOrderIds`（覆っている処理中の決済の注文 ID。既定 null）を足し、
   ゲートが `InFlightCloseCoversPosition` で見送るときに載せる。通知サービスの `InFlightCloseForgoneNotificationSuppressor`（singleton）が、
   (銘柄・市場・決済の方向・注文 ID の集合) を鍵に同じ見送りを 1 回だけ通知する。記憶は 24 時間で捨てる。送信に失敗したら記憶を戻す。
   注文 ID の無い見送り（旧い送り手・ほかの理由）は畳まない。監査は別のキューで全件を受ける（変えない）。IADR-0520 決定 4 と同じ形である。

## 窓（規則 11）

窓＝処理中の区間。前の端＝発注（記録が非終端で保存された）、後の端＝終端（約定・取消・失効、または約定追跡の打ち切り）。
仕様書の表のとおり、(a) 記録だけ（前の端）は約定・失効の直後と打ち切りの後を処理中と読み、損切りの判断を止め得る。
(b) ブローカーの生きている注文だけ（後の端）は保護レグや人手の注文まで数え、S0 の建玉を常に覆うと読む。
**(c) 両端（記録が非終端かつブローカーが生きている）だけがすべてのプローブを満たす。** ゲートと同じ関数を使うので形も同じになる。

## 採らなかった案

- **覆えば常に省く（買い増しの可否を見ない）**: 逆方向の判断（買い増し）を塞ぐ。発注される注文の集合が変わり、統制の変更になる。
- **プロンプトに処理中の決済を書いて Hold を促す**: LLM の費用が減らない（本件の主因）。
- **判断の側で処理中の決済を数える（記録の読み取り・ブローカー照会）**: 規則が 2 か所になる。記録と OpenD は発注執行にだけある。
- **照会の不明で見送る**: 決定 3。
- **通知そのものを止める**: 処理中の決済が替わったとき（約定・取消の後の新しい決済）を利用者が知れなくなる。注文 ID を鍵にして替わったら改めて知らせる。

## 結果

- 処理中の決済が建玉を覆い、買い増しも必ず見送られる保有銘柄では、一次・二次の LLM・リスク審査・監査の `OrderDispatchForgone` が出なくなり、
  代わりに LLM の前の見送り（`TradeDecisionForgoneBeforeLlm(InFlightCloseCoversPosition)`）が 1 回ごとに残る。
- 買い増しが発注になり得る保有銘柄では従来どおり LLM を呼ぶ。そのときの決済の見送りの通知は、同じ処理中の決済について 1 回だけ届く。
- **残余**:
  - 処理中の決済が終わってから次のサイクル（定時なら最大 5.5 分）まで判断しない。その間の損切りは保護の機構（S0 の逆指値・S1）が担い、
    判断の決済が出ても同じ間はゲートで見送られていた（是正前より悪くならない）。
  - 照会は処理中の候補があるとき OpenD へ建玉照会 1 回と注文照会（候補ごとに 1 回）を送る。頻度制限（IADR-0144 決定 5）に当たれば不明になり、LLM を呼ぶ側へ倒れる。
  - 照会の状態（成否）は保有照会のような区間の報告（IADR-0462 決定 2）を持たない。失敗は判断側の警告ログに残る。
  - 本番の既定（`values.yaml`）は trade-decision に `OrderExecution__BaseUrl` を置かないため NoOp（従来どおり）。PoC（`values-local.yaml`）は設定済みで有効になる。
  - 通知の畳み込みの記憶はプロセス内だけ（再起動の直後は 1 回重なり得る）。
