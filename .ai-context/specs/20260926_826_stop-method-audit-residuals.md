---
title: 損切り手法の選択（#819）の監査で残った論点 5 件の再検証 —— 文書と試験で閉じられる残りを閉じ、設計判断が要る残りは PR 本文の問いに回す（#826 の 2 回目）
type: spec
status: accepted
related_ids: [FR-10, FR-11, FR-12, UC-02, ADR-0040, IADR-0342, IADR-0344, IADR-0413, IADR-0347]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1・決定3)
related_specs:
  - 20260925_826_stop-method-audit-followups.md
---

# 仕様書: 損切り手法の選択の監査の残り（#826 の 2 回目）

## 起点

- #826（PR #825 / #819 の監査で出た、ブロックしない指摘 5 件）。1 回目の是正は PR #978（IADR-0413・仕様書 `20260925_826_stop-method-audit-followups`。
  コミットは `Refs #826` で、issue は開いたまま）。
- 本 PR の割り当て: テスト ID `T-10-1575`〜`T-10-1589`（`T-10-1570`〜`T-10-1574` は同時に進める #1044 の PR が使う）。新しい IADR は起こさない
  （IADR-0342 へ日付つきの追記をする）。
- 衝突回避: #856（発注執行の予約の突合。IADR-0441）と #1041（取引判断のプロンプト）の領域には触れない。発注執行の試験は**新しいファイル**に置き、
  既存の `ProtectiveStopGuardTests` 等は編集しない。

## 着手前の再検証（`origin/develop` = `f23f2fa3`。`git rev-parse --is-shallow-repository` → `false`）

| # | 判定 | 根拠 |
| ---: | --- | --- |
| 1 設定側の判定が発注側より緩い | **決定済み（IADR-0413 決定 1）・文書あり** | `StopLossMethodChange.IsPermittedOn` は今も設定上の発注先で判定する。`docs/functional/FR-10_risk-controls.md`「設定上の発注先と実際の発注先（運用で揃える）」に運用が書かれている。**残っているのは運用の操作**（稼働の設定上の発注先を実態に揃えるか）で、コード・文書の作業は無い → PR 本文の問い Q1 |
| 2 到達通知の文面 | **是正済み** | `NotificationFormatter.cs:43-55`・`StopLossTriggeredHandler.cs:20-27`・FR-10 の注意に S0〜S3 の 4 つがある。**リスク管理の検知ログの列挙（S3 を含む）は、どの試験でも固定されていない**（下の変異 R1 が生き残る）→ 試験を足す |
| 3 同一銘柄の S0 と S2 の併存 | **残（設計判断が要る）** | `ProtectiveStopNetting.RemainingPositionFor` は他の機構の有効な記録（S1）を差し引くが、S2 は記録を持たない（`OrderExecutionAppService` の S2 分岐）ので差し引けない。**FR-10 の機能仕様書の「S0 の建玉に対するガードの挙動は、同じ銘柄に S1 の建玉が無い限り変わらない」は不正確**（S2 の建玉も S0 の挙動を変える）。S0 と S1 の併存は表の行にあるが、S0 と S2 の併存は機能仕様書に無い（IADR-0342 の追記と残余リスクにだけある）→ 文書を直し、現状の挙動を**既知の制約として**試験で固定する。直し方は PR 本文の問い Q2 |
| 4 文書の不足 | **是正済み** | IADR-0342 の［2026-09-25 追記 / #826］と FR-10 の「注意（S2 から S0 へ戻したとき）」 |
| 5 免除イベントの誤表示 | **是正済み（IADR-0413 決定 2）・試験の穴あり** | `ProtectiveStopWaiverSettlement`（派生記録 `ProtectiveStopWaiverSettled`）。変異注入で生き残る分岐がある（下の W2〜W4）→ 試験を足す。**残余**（約定追跡の期間を過ぎて非終端のまま残った注文には打ち消しが付かない）→ PR 本文の問い Q3 |

## 変更

### 文書

- `docs/functional/FR-10_risk-controls.md`:
  - 「常駐ガードとの関係」の文を「同じ銘柄に S1 または S2 の建玉が無い限り変わらない」に直し、S2 は差し引けないことを書く。
  - S1 の表の「同じ銘柄に S0 と S1 の建玉が併存」の直後に「同じ銘柄に S0 と S2 の建玉が併存」の行を足す（差し引けない・起きること・今は起きない理由・
    運用上の回避〔同じ銘柄に S0 の建玉が残っている間は S2 へ切り替えない／切り替えた後にその銘柄の S0 の建玉を手動で決済する〕）。
- `.ai-context/adr/IADR-0342`: ［2026-09-26 追記 / #826］2 回目の再検証の結果（1・2・4・5 は閉じた。3 は残り、機能仕様書を正した。試験で現状を固定した）。

### 試験（T-10-1575〜T-10-1579）

| ID | 内容 | 置き場所 |
| --- | --- | --- |
| T-10-1575 | 既知の制約: 同じ銘柄・方向に S2 の建玉があると、S0 の行から見た建玉残に S2 の数量が入る（S1 の行なら差し引く＝対照）。S0 の建玉が消えても S0 の逆指値は取り消されない | `OrderExecutionService.Tests`（新しいファイル） |
| T-10-1576 | リスク管理の検知ログは S0〜S3 の 4 手法の帰結を列挙し、「リスク管理は発注しない」を書く | `RiskManagementService.Tests`（新しいファイル） |
| T-10-1577 | 打ち消しの時刻: 終端の約定記録の時刻が免除より前なら免除の時刻を使う | `ProtectiveStopWaiverSettlementTests` |
| T-10-1578 | 終端の約定記録が複数あれば、最大の約定数を使う（並びの順に依らない） | 同 |
| T-10-1579 | 読めない免除の行は無視し、読める行を使う。読める免除が無ければ作らない（捏造しない） | 同 |

T-10-1580〜T-10-1589 は欠番（予約した帯の未使用分）。

## 変異注入（着手前に実測し、試験を足した後に再実測する）

| ID | 変異 | 対象 |
| --- | --- | --- |
| R1 | 検知ログから S3 を外す | `StopLossTriggeredHandler` |
| W1 | `IsTerminal` に PartiallyFilled を含める | `ProtectiveStopWaiverSettlement` |
| W2 | 時刻をいつも終端の約定記録の時刻にする | 同 |
| W3 | 終端の記録を約定数の小さい順に選ぶ | 同 |
| W4 | 読める免除の行を探さず、先頭の行を読む | 同 |
| W5 | 全量約定の判定 `>=` を `>` にする | 同 |
| W6 | 約定 0 の文言の分岐を外す | 同 |
| W7 | 終端の約定記録の側で判定しない | `OrderExecutedAuditHandler` |
| W8 | 免除の側で判定しない | `ProtectiveStopWaivedAuditHandler` |
| F1 | 到達通知の列挙から S3 を外す | `NotificationFormatter` |
| C1 | `IsPermittedOn` が実弾でも S0 以外を許す | `StopLossMethodChange` |
| C2 | 逆方向（S0 以外のまま実弾へ）の拒否を外す | `BrokerProviderChange` |
| N1 | S0 の建玉残から S1 の行を差し引かない | `ProtectiveStopNetting` |

## 母集合（規則 9・10）

- 項目 3 の文書: `git grep -n "S1 の建玉が無い限り\|併存"`（`docs/`・`.ai-context/adr/`）→ FR-10 の 2 か所（常駐ガードとの関係・S1 の表）、IADR-0342 の追記と残余リスク、
  IADR-0344（S1 と S2 の混在）。凍結記録（IADR-0342 の本文・IADR-0344）は書き換えない。直すのは FR-10 の 2 か所で、IADR-0342 には日付つき追記を足す。
- 項目 2 の列挙: `git grep -n "S2＝\|S2="`（`.ai-context/specs` を除く）→ 通知の整形・その試験 2 つ・リスク管理の検知ログ（2 行）・FR-10・テスト仕様書の T-10-350。
  すべて S3 を含むことを確かめた（IADR-0344:95 は凍結記録として除外）。
- 規則 10: FR-10 の文を直すと、テスト仕様書で同じ文を言い直した箇所が無いか → `git grep -n "S1 の建玉が無い限り"` は FR-10 の 1 か所だけ。

## 完了の定義

- `dotnet test`（監査・通知・リスク管理・発注執行の該当テスト）と `dotnet format --verify-no-changes` が通る。
- 変異注入で、足した試験が対象の変異を殺す。等価な変異・到達できない入力のための変異は理由を書いて残す。
- 静的検査（trace ブロック・試験のトレーサビリティ・コミット規約・文書リンク）が通る。
