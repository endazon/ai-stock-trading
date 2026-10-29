---
title: IADR-0592 新規建ての可否の口は、基準資金の直前の暦日の観測の欠け（ADR-0073）と鮮度切れのときに CapitalBaselineUnavailable を返す。審査の拒否理由のすべてを口の対象・全注文の口・除外のどれかへ置く表を試験で閉じる
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-10, ADR-0003, ADR-0041, ADR-0073, IADR-0354, IADR-0463, IADR-0471, IADR-0495, IADR-0521, IADR-0572, IADR-0581]
author: claude (Claude Code)
created: 2026-10-29
updated: 2026-10-29
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0073_capital-baseline-missing-previous-day-observation-rejects-entry.md（決定 1・2）
related_specs:
  - ../specs/20261029_1483_entry-blockers-capital-baseline-observation-missing.md
---

# IADR-0592: 新規建ての可否の口に基準資金の観測の欠けを足し、拒否理由の分類を試験で閉じる

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-29
- 決定者: Claude Code（#1483 の求めに沿う実装の形。計画の規則と強制の場所は変えない）

## 起点・関連

- 起点: #1483（稼働 PoC 2026-10-26。ET 日曜の観測が無く審査がすべての新規建てを拒否する日に、口がこの理由を返さず LLM を一次 458 回・二次 94 回呼んだ。約 288 円）。
- 計画: ADR-0073 決定 1・2（直前の ET 暦日に成功した観測が無ければ新規建てを拒否・解除は自動）、ADR-0041 決定 2、ADR-0003。
- 前提（覆さない）: IADR-0463（口。審査と同じ述語・判断側は読むだけ・審査は残す・**不明は返さない**＝裁定 3）、IADR-0572（口の 10 理由）、IADR-0521（全注文の口）、
  IADR-0354（基準資金の行は観測時刻の ET 暦日に畳む）、IADR-0581（欠けの判定と読み出しの帰結 `CapitalBaselineReading`）。

## コンテキスト

IADR-0463 は `CapitalBaselineUnavailable` を「照会できていない＝不明」として口の対象から外した（母集合の表の 1 行目）。ADR-0073 の後、未供給の大半は
**直前の ET 暦日の観測の欠け**であり、これは不明ではない —— 当日の観測は直前の暦日の行を書かないので、欠けはその暦日のあいだ確定し、読み出しの帰結
（`ObservationMissing`）が既知の事実として持つ。口が返さないので、審査が必ず拒否する日に判断は LLM を呼び続けた。

もう一つの原因は、**審査に理由を足したとき口へ足すかを決める場所が無い**ことである（#1433 は締め切り・テーマ、#1483 は欠けで、どちらも足し忘れ）。

## 決定

### 決定 1: 口は観測の欠け・鮮度切れのときだけ `CapitalBaselineUnavailable` を両方向に返す

- `PortfolioSnapshot.CapitalObservationMissing` を足す。`PortfolioSnapshotBuilder` は従来どおり `GetCurrent()`（計器・ログを伴う審査の読み）で `Capital` を入れ、
  **`Capital` が null のときだけ** `Read()`（計器を動かさない）の `ObservationMissing` を入れる。審査と口は同じ組み立てを通る（同じ読み）。
- 述語 `EntryStateBlockers.CapitalUnavailable(snapshot)`（判定コアが同じ位置で呼ぶ。振る舞いは変えない）と `CapitalObservationMissing(snapshot)`（その上で欠け。口が呼ぶ）を置く。
  `Determinable` の先頭（審査の到達順）に `CapitalBaselineUnavailable` を置き 10 → 11 理由。資金は方向を問わないので両方向に返す。
- 線上: proto `EntryBlocker` に `CAPITAL_BASELINE_UNAVAILABLE = 13`（非破壊）。REST は数値の往復で変わらない。受け手が古い間は未知の値として不明に倒し LLM を呼ぶ（従来どおり）。
- 判断側は規則を持たない。保有 0 の見送りと保有中の買い増しの塞がり（プロンプト）が口の答えを読む。日本語名「基準資金（前日の口座照会）が無く新規建てを止めている」を足す。
- 🔴 **審査は変えない**（両端で止める）。

### 決定 2: 審査の拒否理由のすべてを分類する表を試験に持つ

- `EntryStateBlockersTests` に `RejectionReason` の全値を「口の対象（`Determinable`）／全注文の口（`OrderStateBlockers`）／除外（理由つき）」へ置く表を持ち、
  (a) 表に無い値があれば赤、(b) 表の「口の対象」と `Determinable` が一致しなければ赤、(c) 全注文の口は実際にその理由を返す、を確かめる。
- 審査に理由を足した者は、口へ足すか除外の理由を書くかを必ず決める（列挙に値を足すとこの試験が赤になる）。

### 決定 3: 行なし・0 以下の未供給は返さない（残余）

- 行なし（初回の配備・Runbook の投入待ち）は運用の手順で直ちに解ける状態であり、0 以下は人手の意図した停止（Runbook が認める経路）である。どちらも審査は止める。
  口が返さない場合の損は LLM の費用だけで、統制は外れない。返す側へ広げるなら本 IADR を改める。

## 却下した案

- **欠けを区別する新しい拒否理由を足す**: ADR-0073 決定 2 は既存の `CapitalBaselineUnavailable` を使ってよいとし、審査・監査・通知の語彙が既にこれで揃っている。口だけ別の名前にすると「審査と同じ理由」の一致の試験が成り立たない。
- **口が `Read()` だけを別に呼ぶ**: 審査の読み（`GetCurrent()`）と 2 回の読みが食い違うと、口が「資金があるのに欠け」と答え得る。連言にしてスナップショットへ入れれば、食い違っても口は審査より多く止めない。
- **未供給のすべてで返す**: 決定 3 のとおり、行なし・0 以下は IADR-0463 の「不明」の区分の外へ出す根拠（暦日のあいだ確定する）が欠けと同じ形では成り立たない。

## 結果

- 欠けの日（週末の観測なしの月曜など）は、保有 0 の銘柄の新規建ての判断が LLM を呼ばずに見送られる（`EntryBlockedByRiskControls`）。保有中の銘柄は決済の判断のために LLM を呼び続ける（買い増しの塞がりはプロンプトに出る）。
- 🔴 計器の移動: 該当の新規建ての一部が、審査の拒否理由の計器（`CapitalBaselineUnavailable`）から LLM の前の見送り（`EntryBlockedByRiskControls`）へ移る（IADR-0463 と同じ）。
- 配備: リスク管理と取引判断の両方の版を上げる（順序は問わない）。設定の追加は無い。
