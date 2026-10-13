---
title: 判断由来の決済が処理中で建玉を覆い、どの結論も発注にならない銘柄は LLM を呼ぶ前に見送り、見送りの通知を処理中の決済ごとに 1 回へ畳む（#1338）
type: spec
status: done
related_ids: [FR-04, FR-10, FR-09, FR-11, UC-01, UC-02, ADR-0003, ADR-0050, IADR-0461, IADR-0463, IADR-0471, IADR-0500, IADR-0527, IADR-0495, IADR-0462, IADR-0520, IADR-0420, IADR-0535]
author: claude (Claude Code)
created: 2026-10-14
updated: 2026-10-14
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04 取引判断・FR-10 リスク統制)
  - planning:projects/ai-stock-trading/07_adr/ADR-0050_decision-close-nets-in-flight-closes-and-stop-line-exit-only-without-mechanical-stop.md (決定 1)
---

# 仕様書: 処理中の決済が建玉を覆う銘柄は LLM を呼ぶ前に見送る（#1338）

> 本仕様書は実装着手前に作成した（着手 2026-10-14）。基点は AST `origin/develop` `03753282`。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-04**（取引判断の費用）、**FR-10**（手仕舞いは止めない）、FR-09（通知）、FR-11（監査）、UC-01 / UC-02。
- 計画 ADR: ADR-0003（リスク管理の権威）、ADR-0050 決定 1（判断の手仕舞いは処理中の決済を引いて残りだけを送る）。
- 起票: #1338（PoC 2026-10-13 米国セッション。AAPL の判断由来の決済〔Sell 13 株の指値・13:36:44Z 発注〕が約定しない間、
  5.5 分ごとのサイクルで一次・二次の LLM が同じ決済を 34 回判断し、うち 33 回を発注執行が `InFlightCloseCoversPosition` で見送った。
  sonnet 約 62 円・haiku 約 2.6 円・リスク審査 33 件・監査 33 行・**Discord の見送り通知 33 件**）。
- 関連 IADR: IADR-0461（処理中の決済の数え方。決定 1・2・4）、IADR-0463 / IADR-0500 / IADR-0527 / IADR-0495（LLM の前の見送りの線引き）、
  IADR-0471（保有中の銘柄の買い増しの可否）、IADR-0462 決定 4（LLM の前の見送りの記録）、IADR-0520 決定 4（通知の抑止の前例）、
  IADR-0420（跨サービスの読み取りの契約）。
- 新規 IADR: **IADR-0535**。

## 原因（コードで特定した経路）

1. LLM の前の見送り（`TradeDecisionAppService.DecideAsync`）は、保有が既知で 0・未約定の新規建てが既知で空の銘柄だけを対象にする
   （#1113 / #1174 / #1176。`TradeDecisionAppService.cs:399-487`）。保有中の銘柄は「決済の判断を必ず残す」ため常に LLM を呼ぶ。
2. 発注執行の決済のゲート（`OrderExecutionAppService.CountInFlightClosesAsync`・`:814-860`）は、同じ建玉を売る処理中の決済
   （非終端の Close の記録で、ブローカーが生きていると答えた分。S0 / S3 の保護レグを除く）が決済方向の建玉を覆えば、
   判断の決済を `InFlightCloseCoversPosition` で見送る。判断の側はこの状態を知らない（処理中の決済の照会口が無い）。
3. 見送りの通知（`OrderDispatchForgoneNotificationHandler`）は抑止を持たず、見送り 1 件ごとに Discord へ出る。

## 方針

### 1. LLM の前の見送り（判断）

**どの結論も発注にならないと LLM の前に分かる銘柄だけ**を省く（IADR-0463 と同じ線引き。省くのは費用の最適化であって統制ではない）。
保有中（ロング）の銘柄で LLM が返し得る結論は 3 つで、次の全部が成り立つときだけ LLM を呼ばない。

| 結論 | 発注にならない条件（LLM の前に分かる） |
| --- | --- |
| Hold | 常に発注しない |
| 決済（ロングなら Sell・ショートなら Buy） | 発注執行が「処理中の決済が決済方向の建玉を覆う」と答えた（ゲートと同じ判定。下の 2） |
| 買い増し・売り増し（処理中の決済と**逆方向**の判断） | LLM の後で必ず見送られることが分かっている: 出口専用（`ExitOnly`）／換算レートが新規建てに使えない（鮮度切れ）／未約定の新規建てが不明（実結線）／その方向の新規建てが審査で必ず拒否される（`addOnBlockers` が空でない）／残枠が現在値 × 1 株に満たない／新規建てに使える金額の上限が最小の名目額に届かない |

- **処理中の決済と逆方向の判断（買い増し）を塞がない**: 上の 3 行目が成り立たないとき（買い増しが発注になり得るとき）は LLM を呼ぶ。
- **建玉を超える判断（覆われていない残りの決済）を塞がない**: 処理中の決済が決済方向の建玉を覆わない（部分）なら LLM を呼ぶ。
- 決済の方向は保有の符号で決める（ロング → Sell の処理中だけを見る。ショート → Buy）。逆方向の処理中の決済は見ない。
- 記録は既存の口（`TradeDecisionForgoneBeforeLlm`・`decision_skips`）に新しい理由 `InFlightCloseCoversPosition` を末尾へ足して載せる。

### 2. 判定の共有（発注執行）と照会の口

- 発注執行の「処理中の決済の数え方」（`CountInFlightClosesAsync`）と「覆うかの判定」（`sendable <= 0`）を
  `Features/OrderExecution/InFlightCloses.cs`（静的クラス）へ移し、**決済のゲートと照会の口の 2 か所から同じ関数を呼ぶ**（2 か所に書かない）。
- 照会の口: `GET /order-execution/in-flight-closes?symbol&market&closeSide`（OwnerOrService・読み取り専用。前例は `short-permit` / `daily-bars`）。
  応答 `InFlightClosesView`（Symbol・Market・CloseSide・Status〔Unknown=0 / Covered=1 / NotCovered=2〕・UnknownReason・
  ClosableQuantity・InFlightQuantity・OrderIds・DecisionIds）。
  - 建玉照会の能力が無い（内蔵 paper）・保護記録ストアが無い → Unknown（ゲートもその構成では見送らない。答えをゲートに揃える）。
  - 処理中の決済の候補が記録に 1 件も無い → NotCovered（ブローカーへは照会しない）。
  - 候補があれば、ブローカーの建玉を照会し（**照会の後に**候補を読み直す。IADR-0461 決定 4）、1 件ごとに注文の生死を確かめて数える。
    建玉照会が不明 → Unknown。決済方向の建玉 0 → NotCovered（ゲートは別の理由 `BrokerPositionAbsent` で扱う。判断の側は省かない）。
- 判断の側はアダプタ（`HttpInFlightCloseProvider`。名前付きクライアント `order-execution`）越しに呼ぶ。直接の DB 参照はしない（#1324 の前例）。
  `OrderExecution:BaseUrl` が無ければ NoOp（常に不明＝LLM を呼ぶ＝従来どおり）。

### 3. 照会の失敗の倒し方

- 照会の失敗・未結線・不正な応答・Unknown は **null（不明）＝LLM を呼ぶ**（IADR-0463 決定 4 の新規建ての前段の見送りと同じ向き）。
- この向きは決済（損切りを含む）の判断を必ず残す。逆（不明なら見送る）は、照会が落ちている間すべての保有銘柄の決済判断を止め、
  損切りを止める（FR-10「手仕舞いは止めない」に反する）ため採らない。

### 4. 見送りの通知を 1 回に畳む

- `OrderDispatchForgone` に任意の末尾項目 `InFlightCloseOrderIds`（覆っている処理中の決済の注文 ID。既定 null）を足し、ゲートが
  `InFlightCloseCoversPosition` で見送るときに載せる。
- 通知サービスに `InFlightCloseForgoneNotificationSuppressor` を足す（IADR-0520 の `StopLossNotificationSuppressor` と同じ形）。
  鍵は (銘柄・市場・決済の方向・注文 ID の集合)。同じ鍵は 1 回だけ通知する（記憶は 24 時間で捨てる。日中の注文はセッションを越えない）。
  送信に失敗したら記憶を戻す。注文 ID が無い（旧い送り手・他の理由）なら抑止しない。監査は別キューで全件を受ける（変えない）。

## 窓（規則 11）

窓＝「処理中」の区間。**前の端＝発注**（記録が非終端で保存され、ブローカーが受けた）、**後の端＝終端**（約定・取消・失効、または約定追跡の打ち切り）。

プローブ:

- **P+（増える側・前の端の直後）**: 決済を発注した直後（記録は Accepted・ブローカーも生きている）。期待＝覆う（省く）。
- **P−1（減る側・約定の直後）**: ブローカーは約定（終端）と答えるが、約定追跡（30 秒周期）がまだ記録を書いていない（記録は Accepted）。期待＝覆わない（LLM を呼ぶ）。
- **P−2（減る側・追跡の打ち切りの後）**: 追跡の打ち切りで記録は非終端のまま残るが、ブローカーは失効と答える。期待＝覆わない。
- **P0（終端で記録も書かれた後）**: 記録も終端。期待＝覆わない（ブローカーへは照会もしない）。

| 形 | P+ | P−1 | P−2 | P0 |
| --- | --- | --- | --- | --- |
| (a) 前の端だけ（記録が非終端なら数える） | ○ | **×**（約定済みを覆うと読み、保有が残る間 LLM を止める） | **×**（打ち切り後は永久に止める＝損切りの判断を止める） | ○ |
| (b) 後の端だけ（ブローカーの生きている注文を数える。記録を見ない） | ○ | ○ | ○ | ○ だが **S0 / S3 の保護レグや人手の注文まで数える**（S0 の建玉は常に覆うと読む） |
| (c) 両端（記録が非終端 **かつ** ブローカーが生きていると答えた分だけ） | ○ | ○ | ○ | ○ |

採るのは (c)（IADR-0461 決定 4 と同じ。ゲートと同じ関数を使うので形も同じになる）。表は実装後に変異で実測して確かめる（下の「実測」）。

残る窓（受容）: 判断が見送った直後に処理中の決済が終端しても、次のサイクル（定時なら 5.5 分後）まで判断しない。この間の損切りは
保護の機構（S0 の逆指値・S1）が担い、判断の決済が出ても同じ間はゲートで見送られていた（是正前より悪くならない）。

## 母集合（規則 9・10。着手前に引いた）

### 規則 9: 誤りの側の語で走査

`grep -rn "InFlightCloseCoversPosition\|CountInFlightClosesAsync\|FindPendingCloses\|OrderDispatchForgoneNotificationHandler\|ExitOnlyWithoutHolding" --include=*.cs --include=*.md .`（`.ai-context/specs` を除く）

| 場所 | 扱い |
| --- | --- |
| `OrderExecutionAppService.CountInFlightClosesAsync`・ゲートの `sendable <= 0` | **共有関数へ移す**（挙動は変えない） |
| `SoftwareStopExecutor`（`FindPendingCloses` 2 か所）・`SoftwareStopFloorRetrofitter` | 目的が違う（S1 が判断の手仕舞いを取り消す・下限の遡及）。**変えない** |
| `OrderDispatchForgone`（契約） | 末尾に任意項目を足す |
| `NotificationFormatter`（`InFlightCloseCoversPosition` の文面） | 変えない（抑止はハンドラ） |
| `OrderDispatchForgoneLifecycle`（リスク管理の在庫解放） | 変えない |
| `DecisionForgoneBeforeLlmReason` / `DecisionSkipReason` | 末尾へ `InFlightCloseCoversPosition` を足す。件数・末尾を固定する試験（`DecisionSkipReasonTests`・`LedgerGapEventsTests`・`DecisionHeldReportTests`）を追随 |
| `AuditEntryFactory`（LLM の前の見送りの要約） | 名前をそのまま出す実装なら変えない（試験で確かめる） |
| `docs/data/audit-events.md`・`docs/api/events-and-ports.md`・`docs/functional/FR-10_risk-controls.md`・`docs/tests/FR-10_risk-controls-tests.md`・`docs/observability/observability.md`・`deploy/observability/README.md`・`docs/operations/nightly-ledger-summary-runbook.md` | 理由の列挙・照会の口・試験の行を足す |
| `deploy/helm/ai-stock-trading/values-local.yaml` の `OrderExecution__BaseUrl` の注記 | 「判断の出来高・ATR を有効にしない」の注記に、本件の照会の口が有効になることを足す |

### 規則 10: この変更で新たに誤りになる自分の記述

- `TradeDecisionAppService` の「保有中・未約定あり・不明の銘柄では照会もしない（決済の判断は必ず残す）」→ 処理中の決済が覆い、どの結論も
  発注にならないときだけ例外とする旨を足す。
- `TradeDecisionForgoneBeforeLlm` の「Reason: 8 値」→ 9 値。
- IADR-0461 の残余・IADR-0463 の線引きの記述は凍結記録として触らず、IADR-0535 から引く。

## 受け入れ基準 → テスト

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 保有ロング・処理中の Sell が建玉を覆い・買い増しが審査で必ず拒否される → 一次スクリーニングを含め LLM を 0 回呼び、`TradeDecisionForgoneBeforeLlm(InFlightCloseCoversPosition)` を 1 件出す | T-10-2642 |
| 2 | 買い増しが発注になり得る（逆方向の判断が残る）なら、覆っていても LLM を呼ぶ（照会もしない） | T-10-2643 |
| 3 | 覆わない（部分・建玉を超える判断が残る）・照会の失敗・不明・未結線なら LLM を呼ぶ | T-10-2644 |
| 4 | 買い増しが必ず見送られる各条件（出口専用・鮮度切れ・未約定不明・審査拒否・1 株・最小名目額）で省く。ショート（Buy の処理中）も対称 | T-10-2645 |
| 5 | 照会の口: 発注の直後（P+）は覆う。約定の直後（P−1）・打ち切りの後（P−2）・終端（P0）は覆わない。保護レグは数えない | T-10-2646 |
| 6 | 照会の口: 建玉照会の能力が無い・建玉照会が不明 → Unknown。候補が無ければブローカーへ照会しない。部分は NotCovered | T-10-2647 |
| 7 | ゲートと照会の口が同じ判定（共有関数）を使う: ゲートの見送りは従来どおりで、見送りに処理中の注文 ID が載る | T-10-2648 |
| 8 | 判断側のアダプタ: 応答を読む・別銘柄／別方向／欠落／Unknown／非 2xx／例外は null。配線（BaseUrl なし → NoOp） | T-10-2649 |
| 9 | 通知: 同じ建玉・同じ処理中の決済の見送りは 1 回だけ通知。注文 ID が変われば通知。ID なしは抑止しない。送信失敗で記憶を戻す | T-10-2650 |
| 10 | 語彙: 両列挙の末尾に同名で足し、既存の値の位置を変えない。監査の要約に名前が載る | T-10-2651 |

変異（実測を下に記録）: M1 前段の見送りを外す → T-10-2642 が赤。M2 買い増しの判定を外す（覆えば常に省く）→ T-10-2643 が赤。
M3 照会の口で生死の確認を外す（形 a）→ T-10-2646 が赤。M4 通知の抑止を外す → T-10-2650 が赤。

## 実測

基点 `03753282` の上で、変異を 1 つずつ入れて対象の試験を走らせた。

| 変異 | 内容 | 赤になる試験 |
| --- | --- | --- |
| M1 | 🔴 前段の見送りを外す（常に LLM を呼ぶ） | T-10-2642・T-10-2645（6 条件すべて）・T-10-2644（照会の回数） |
| M2 | 買い増しの判定を外す（覆えば常に省く） | T-10-2643 |
| M3 | 🔴 窓の前の端だけ（ブローカーの生死を確かめず、記録が非終端なら数える） | T-10-2646（失効・取消・照会できない）と既存の決済のゲートの試験 3 件。約定の直後は約定数の差し引きで覆わない側へ倒れ、赤にならない |
| M4 | 通知を畳まない | T-10-2650（2 件） |
| M5 | 照会の例外を「覆う」へ倒す | T-10-2644（例外） |
| M6 | 建玉照会の後に候補を読み直さない | T-10-2646（照会の最中に保存された決済） |

窓の表の (a)（前の端だけ）は M3 で実測した: 失効・取消・照会できない記録を処理中と数え、覆うと答える（赤）。
(b)（後の端だけ）は記録を見ないので保護レグを数える。保護レグの除外は T-10-2646（保護レグ）が固定する。
(c)（両端）の実装ですべて緑。

検証（変更したサービスは `--filter` なしで全件）:

- `dotnet build backend/backend.slnx -warnaserror`: 0 警告・0 エラー
- `dotnet test backend/backend.slnx`: 全プロジェクト緑（TradeDecision 1,898・OrderExecution 1,776・Notification 896・Shared.Contracts 583 ほか）
- `dotnet format backend/backend.slnx --verify-no-changes`: 差分なし
