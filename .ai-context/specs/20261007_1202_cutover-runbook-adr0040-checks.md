---
title: 実弾解禁 Runbook の解禁前確認へ ADR-0040 決定 4 の 2 行（保護逆指値の受理・約定・失効時の再発注／GTC の残存）を写す（#1202）
type: spec
status: accepted
related_ids: [FR-10, FR-20, ADR-0040, ADR-0050, ADR-0016, IADR-0210, IADR-0342, IADR-0482]
author: claude (Claude Code)
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定 4・§結果 フォローアップ 3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0050_decision-close-nets-in-flight-closes-and-stop-line-exit-only-without-mechanical-stop.md (決定 3)
---

# 実弾解禁 Runbook に ADR-0040 決定 4 の 2 行を写す（#1202）

## 起点

- 第 4 回全体監査（2026-10-07）の指摘 B-6（#1202）。
- 計画 ADR-0040 決定 4 の「実弾解禁前の確認」の表は 2 行（①保護逆指値そのものの挙動〔受理・損切りラインでの約定・取消／失効の検知と再発注〕 ②有効期限が当日限りであること〔GTC 不可〕）を持ち、ADR-0050 決定 3 が ③判断の手仕舞いと保護逆指値の併存 を足した。verdict の形式は ADR-0016 決定 14 の援用（利用者承認・段階ゲートの承認記録と同じ経路・有効期限 30 日・再検証の 3 契機〔情報源の変更／戦略の変更／期限切れ〕）。
- 実装の `docs/operations/live-trading-cutover-runbook.md` は ③ だけを行 9 に持ち、①② が無い。ADR-0040 フォローアップ 3（GTC 不可なら日次で置き直す経路）の受け皿 issue も無い。

## 受け入れ基準

1. Runbook の解禁前チェックリストに ①② の 2 行が、確かめ方（実弾口座での読み取り専用の照会で足りるか・SIMULATE で代替できるか）と verdict の記録先つきで並ぶ。
2. 確認結果が「GTC 不可」のとき、フォローアップ 3 の起票先（issue）が Runbook から辿れる。
3. 否定形: `LiveTradingGate` の閂・ブローカの受理語彙は変えない（文書の是正のみ）。

## 母集合（規則 9・10）

誤りの側の文字列で走査した（`origin/develop` = `1692de1f`）。

| 走査 | 結果 | 扱い |
| --- | --- | --- |
| `docs/` で `GTC` / `TimeInForce` | 0 件 | 本 PR で Runbook・運用仕様書・閉塞一覧へ足す |
| `docs/` で `当日限り` | Runbook 0 件（通知文の説明に他所で既出） | 同上 |
| `チェックリスト #9` / `前提条件 #13` の参照 | 運用仕様書 #13・閉塞一覧 A-18 | 番号は変えない（新しい行は末尾 #11・#12／#14・#15 に足す）。既存の参照は無改変 |
| 実装の確認項目の告知（`LiveTradingGate` の例外文） | 両層の Stage 0 合格のみ列挙 | 受け入れ基準 3 により変えない |
| open issue（タイトル・本文）で `GTC` / `当日限り` / `TimeInForce` / `置き直` | #1202 と #1204（台帳。ADR-0040 フォローアップ 3 を含まない）のみ | 重複なし。#1214 を新規起票 |

## 実装の実測（確かめ方を正確にするため）

| # | 事実 | 場所 |
| --- | --- | --- |
| 1 | 保護逆指値は `OrderType_Stop`（発火価格は `AuxPrice`）で発注する | `MMApiMoomooTradeClient.PlaceOrderAsync` |
| 2 | **`SetTimeInForce` を呼んでいない**。SDK `moomoo-api` 10.8.6808 は `TimeInForce_DAY` / `_GTC` / `_GTD` / `_IOC` を持つ。現行の保護逆指値は有効期限を指定しない＝ブローカー既定（当日限り）で出ている | 同上・SDK の型 |
| 3 | 失効の検知と再発注は `ProtectiveStopGuardService`（IADR-0210 決定 4・既定 30 秒）。取消・拒否・失効を観測すると建玉が残っていれば再発注する。照会不能（`Unknown`）は据え置く | `ProtectiveStopGuard.cs` |
| 4 | OpenD の `OrderStatus` の写像: 14/15/24 → `Cancelled`、3/21/22/23 → `Failed`、4（TimeOut）・未知 → `Unknown` | `MMApiMoomooTradeClient.MapState` |
| 5 | 本システムの実弾口座の読み取り専用照会は借株可否と維持率の束（`TrdGetMarginRatio`）だけで、注文一覧を照会しない | `MMApiRealMarginQueryClient`（IADR-0482） |
| 6 | 承認記録の verdict 種別は空売りの解禁（`ShortSellReleaseVerdict`）だけで、①②③ 用の種別は無い | `StageTransitionKind`・`StageGateLedger` |

## やること

1. Runbook の解禁前チェックリストに #11（①）・#12（②）を足す。確かめ方に「SIMULATE では代替できない」「読み取り専用の照会で足りる部分／人の発注が要る部分」を分けて書く。
2. 表の直後に verdict の記録先・有効期限 30 日・再検証 3 契機の節を足す（#9・#11・#12 共通）。承認記録に種別が無い事実と暫定の記録先（運用仕様書の前提条件の表）を併記する。
3. 運用仕様書の前提条件の表に #14・#15 を足す（記録先）。
4. フォローアップ 3 の受け皿として #1214 を起票し、Runbook #12 から辿れるようにする。
5. 閉塞一覧（`docs/blocked-tasks.md`）に A-19 を足す（A-18 と同型。実弾口座でしか確かめられない）。

## 対象外

- `LiveTradingGate` の例外文・閂・ブローカの受理語彙（受け入れ基準 3）。
- 承認記録への verdict 種別の追加（解禁 IADR が決める）。
- 保護レグへの `TimeInForce` の指定（#1214 が確認結果を受けて決める）。
- IADR-0342 への追記: 同 IADR の射程は SIMULATE の手法選択であり、実弾の確認項目は持たない。本件は文書の是正のみで実装判断を含まないため追記しない。
