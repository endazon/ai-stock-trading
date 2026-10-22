---
title: IADR-0572 新規建ての可否の口に閉場前の締め切りとテーマの上限を審査と同じ関数・同じ解決で足す。判断由来の決済の約定直後は、発注執行が観測したブローカーの決済方向の建玉 0 で出口の判断の LLM を省く
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-10, FR-19, ADR-0003, ADR-0050, ADR-0070, ADR-0071, IADR-0119, IADR-0351, IADR-0427, IADR-0463, IADR-0471, IADR-0535, IADR-0561, IADR-0562, IADR-0564]
author: claude (Claude Code)
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0070_pre-close-entry-cutoff-and-flatten.md（決定 1・検討した選択肢「締め切りの強制の場所」）
  - planning:projects/ai-stock-trading/07_adr/ADR-0071_theme-concentration-limit.md（決定 1）
  - planning:projects/ai-stock-trading/04_workflows/01_scheduled-trading-cycle.md（§補足・例外処理 閉場前の統制・テーマの上限）
related_specs:
  - ../specs/20261023_1433_1434_llm-precheck-cutoff-theme-and-closed-position.md
---

# IADR-0572: LLM の前の見送りを 2 か所で足す（締め切り・テーマの上限を口へ／決済の約定直後の建玉 0）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-23
- 決定者: Claude Code（#1433・#1434 の求めに沿う実装の形。計画の規則と強制の場所は変えない）

## 起点・関連

- 起点: #1433（新規建ての可否の口が閉場前の締め切り・テーマの上限を見ない）、#1434（判断由来の決済の約定直後の巡回で、出口の判断の LLM を呼ぶ）。
  どちらも稼働 PoC（2026-10-21 の米国セッション）の費用の無駄であり、安全上の穴ではない（審査・LLM の後の関所が止めている）。
- 計画: ADR-0070 決定 1（締め切り）、ADR-0071 決定 1（テーマの上限）、ADR-0003（判断入力）、ADR-0050 決定 1。
- 前提（覆さない）: IADR-0463（口。審査と同じ述語・判断側は読むだけ・審査は残す）、IADR-0471（保有中の買い増しにも口を当てる）、
  IADR-0535（処理中の決済が覆うとき LLM を省く・不明は LLM を呼ぶ）、IADR-0561（締め切りの審査）、IADR-0562（テーマの審査）、IADR-0564（保存値＞構成）、
  IADR-0119（保有 0 の売りは裸の新規売りとして見送る）、IADR-0351 決定 6（決済の数量は発注直前に読み直す）、IADR-0427（gRPC は REST と同じ解釈）。

## コンテキスト

1. 口（`GET /risk-controls/entry-blockers`）は審査と同じ述語を掲げるが、閉場前の締め切り（`PreCloseEntryCutoff`）とテーマの上限（`ThemeConcentrationExceeded`）を
   持たない。2026-10-21 の 04:00〜05:00 JST に一次スクリーニング 54 回（約 10 円）が、審査が必ず拒否する保有 0 の銘柄に使われた。
2. 判断の保有（プロンプト）と LLM の後の Sell の関所は**同じ読み元**（台帳＝リスク管理の open-positions）を別の時点で読む。台帳は約定追跡（30 秒周期）が
   約定を書いて射影が届くまで遅れる。HOOD の決済の約定直後の巡回では、冒頭の読みが保有中（2339 株）、LLM の前の処理中の決済の照会（IADR-0535）で発注執行が
   ブローカーの決済方向の建玉 0 を観測（`NotCovered`）、取引判断は `NotCovered` を「LLM を呼ぶ」と読み、LLM の後の読み直しで台帳が追いついて held=0 で見送った。
   照会の応答はブローカーの建玉（`ClosableQuantity`）を運んでいたが、取引判断の受け皿は読まず（REST）、gRPC は運ばなかった。

## 決定

### 決定 1: 口に締め切りとテーマの上限を足す（#1433）

- `EntryStateBlockers` に述語 `PreCloseCutoff(supply)`・`ThemeConcentration(themes, symbol, market, snapshot)` を置き、**判定コア（`RiskEvaluator`）と口の両方がこの関数を呼ぶ**。
  `Determinable` は 8 → 10 理由（審査の到達順＝判断由来の決済の後・GFV の前）。`Determine` の追加の引数は省略可能（省略すればその理由は判定しない＝判定コアと同じ規律）。
- 締め切りの供給の組み立て（`MarketHours.SessionCloseOn` と効く分数）は `PreCloseEntryCutoffSupply.For` 1 つにし、審査（通常の審査・予約からの組み直し）と口が呼ぶ。
- 口（`EntryBlockersService`）は審査と同じ構成の値（`ConfiguredSessionAndThemeControls`）と同じ設定行から `SessionAndThemeControlResolution` で効く値を解決する（保存値＞構成）。
  テーマはスナップショットの保有中 ∪ 未約定の新規建て（予約を含む）の銘柄で数える。**両方向**に同じ答えを返す（締め切りもテーマも方向を問わない）。
- 線上: proto `EntryBlocker` に `PRE_CLOSE_ENTRY_CUTOFF = 11`・`THEME_CONCENTRATION_EXCEEDED = 12` を足す（非破壊）。REST は `RejectionReason` の数値で往復するので変わらない。
  受け手が古い間に送り手が新しい値を返すと、受け手は未知の理由として不明へ倒し LLM を呼ぶ（従来どおり。見送る向きへ誤らない）。
- 判断側は規則を持たない。保有 0 の見送り（`EntryBlockedByRiskControls`）と保有中の買い増しの Hold（`AddOnBlockedByRiskControls`）は口の答えをそのまま読む。
  プロンプトの塞がりの文言に 2 理由の日本語名を足す（名前のままだと LLM に意味が届かない）。
- 🔴 **審査は変えない**（両端で止める）。計画 ADR-0070 の「締め切りの強制の場所」は審査のままである。

### 決定 2: 決済の約定直後は、発注執行が観測したブローカーの建玉 0 で LLM を省く（#1434）

- 取引判断の `InFlightCloseCoverage` に `BrokerClosableQuantity`（null＝不明）を足し、REST は既存の `ClosableQuantity` を、gRPC は新設の `optional int32 closable_quantity = 8` を読む
  （送り手は照会したときだけ書く。負は契約外として null）。
- IADR-0535 の LLM の前の見送りの条件（保有中・`AddOnKnownForgone`・照会）を変えずに、照会の答えが `Covered` **または** `BrokerClosableQuantity == 0` のときに省く。
  後者の理由は新しい値 `BrokerPositionClosed`（`DecisionForgoneBeforeLlmReason`・`DecisionSkipReason` の末尾。名前は一致）。
- 建玉 0 なら、決済の結論は発注執行の決済のゲート（`BrokerHeldPositionGate` → `NoPosition`）が見送り、買い増し・売り増しは `AddOnKnownForgone` のとおり見送られ、Hold は何も出さない。
  どの結論も発注にならない。
- 不明（null・照会の失敗・Unknown・旧い送り手）・建玉が正は従来どおり LLM を呼ぶ（決済〔損切り〕の判断を止める向きへは倒さない）。

## 検討した選択肢

### #1434 の直し方

| 案 | 内容 | 判定 |
| --- | --- | --- |
| A | LLM の前に台帳の保有を読み直す（読み元と時点を揃える） | **不採用**。HOOD の時系列では LLM の前（04:15:38）の時点で台帳はまだ約定を知らない（発注執行の記録も非終端だった）。読み直しても止まらない |
| B | プロンプトと関所を同じ 1 回の読みにする | **不採用**。IADR-0351 決定 6（決済の数量は発注直前の事実で決める）を崩す。LLM の間に逆指値が約定し得る |
| C | 照会の応答が既に運ぶブローカーの決済方向の建玉を読み、0 なら LLM を省く | **採用**。LLM の前に約定を知っている唯一の読み元であり、照会の追加も規則の複製も要らない（ゲートと同じ関数の値） |
| D | 当日に判断由来の決済をした銘柄を一律に省く | **不採用**。部分約定や再建玉の保有の出口を塞ぐ |

### #1433 の置き場

| 案 | 内容 | 判定 |
| --- | --- | --- |
| A | 口の `EntryStateBlockers.Determine` に足し、判定コアも同じ述語を呼ぶ | **採用**（IADR-0463 決定 2 の「述語は 1 か所」） |
| B | 取引判断が時刻とテーマを自分で判定する | **不採用**。規則の複製（IADR-0463 が退けた形） |
| C | 審査から外して口へ移す | **不採用**。強制の場所を変える（計画 ADR-0070 の採用案に反する） |

## 結果

- 締め切りの窓（既定は閉場の 60 分前〜その日の終わり）の間、保有 0 の監視銘柄は LLM を呼ばずに `EntryBlockedByRiskControls`（reasons=`PreCloseEntryCutoff`）で見送る。
  テーマの上限に達している集まりの他銘柄も同じ。保有中の銘柄は決済の判断のため LLM を呼ぶ（買い増しの結論は Hold に倒れる）。
- 判断由来の決済の約定直後の巡回は `BrokerPositionClosed` で見送る。観測の移動: 従来の LLM の後の `NakedShortOpen`（と一部の `LlmHold`）から移る。

## 残余リスク

- ブローカーの建玉 0 の観測は、発注執行に非終端の決済の記録（候補）があるときだけ得られる（候補が無ければブローカーへ照会しない設計。IADR-0535）。
  約定追跡が記録を終端にした後で台帳がまだ遅れている短い窓は、従来どおり LLM を呼び、LLM の後の関所が止める。
- 口の締め切りは審査と同じく規則計算の暦で閉場を求める（臨時の半日は IADR-0561 の残余リスクのまま）。
- 計画 ADR-0070 の検討した選択肢 3「LLM の前の口に足す（不採用）」は強制の場所としての判断であり、本決定は強制を審査に残したまま口にも足す。
  計画の記述が「口には足さない」と読める余地があるので、計画への注記を環流案として報告した（起票は利用者の判断）。
