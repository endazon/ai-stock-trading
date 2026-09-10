---
title: NFR-01/02（5 分・10 分）実測検証の追跡先の是正（#637 の後継明示）
type: spec
status: done
related_ids: [NFR-01, NFR-02]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 仕様書: NFR-01/02 実測検証の追跡先の是正（#637）

## 起点

issue [#637](https://github.com/endazon/ai-stock-trading/issues/637)。#204（実環境構築前 実装監査）の
初版（2026-07-19）が G-1 として起票した [#203](https://github.com/endazon/ai-stock-trading/issues/203) が
2026-08-02 に `DUPLICATE` でクローズされたが後継 issue が無く、NFR-01（価格変動検知→発注完了 5 分以内）・
NFR-02（定時サイクル 1 周 10 分以内）の実測検証が実装側バックログから見えなくなっていた、という
**文書・トレーサビリティの欠落**（測定コードの実装ではない）。

## 事前調査（実測）

- `gh issue view 203` → `state: CLOSED, stateReason: DUPLICATE`。後継への言及なし。
- `gh issue view 637 --comments`: 2 本の追加コメントで判明した経緯——
  1. 補足コメント（#204 監査の続き）: 計器自体も「判断 1 回分」しか測っておらず、端点間（サービスを跨ぐ区間）の計器が
     1 つも無いことが判明（`BusinessMetricNames.cs:32` の `ast.trade_cycle.decision_duration_ms` のみ）。
  2. 分割コメント: #637 を**性質の異なる 2 段階**に分割した。
     - (1) 端点間計器の新設（実環境不要）→ [#689](https://github.com/endazon/ai-stock-trading/issues/689)
     - (2) 実 LLM＋開場中の実測（実環境・API キー待ち）→ [#690](https://github.com/endazon/ai-stock-trading/issues/690)
     - **「本 issue（#637）は親として残す（クローズしない）」と明記されている。**
- `gh issue view 689` → `state: CLOSED`（計器新設が完了済み）。
- `gh issue view 690` → `state: OPEN`・label `blocked:env`（実 LLM・実開場時間が前提で AI 単独では完結しない）。
- リポジトリ内の実装（`git grep -n "NFR-01\|NFR-02" backend`）で、`IADR-0307`（端点間レイテンシ計器の
  設計決定）と #689 由来のコメント・テスト・契約フィールド（`CycleTrigger` / `CycleStartedAt`）・
  メトリクス（`ast_trade_cycle_order_completion_latency_ms` 等）が既に多数実装済みであることを確認した。
  → **#689 の分は実装済みで、#637 が指摘した「計器不足」は解消している。残るのは #690（実測そのもの）。**

## 結論（分岐判定）

#690 は既に #637 の後継として機能している（分割コメントに明記済み）。**新たな issue 起票は不要**。
欠けていたのは「#637 の指摘を受けて実装側の生きた文書（`docs/`）を #690 側へ向け直す」作業のみ。
計測コードの実装・実測の実行はいずれも本作業の範囲外（#690 が実環境・実 LLM 待ちのため着手できない）。

## 走査した母集合（規則 1・2・9。誤りの側の文字列 `#203` と、その別形から引いた）

`issues/203|issue 203|#203\b` で追跡下の全ファイル（`.git` 除く）を走査した。ヒット 4 件:

| ファイル | 種別 | 対応 |
| --- | --- | --- |
| `docs/tests/README.md`（trace ブロック `issues:` 行・本文表の 1 行） | **生きた文書**（`docs/`） | **変更**——#203 を #689（計器・完了済み）/ #690（実測・追跡中）へ張り替え |
| `.ai-context/adr/IADR-0119_decision-derived-close.md` | 凍結記録（実装ADR） | **不変**——「当時 #203 を意識してこう判断した」という史実の記述であり、`.ai-context/README.md` の凍結原則（本文プロズを書き換えない）に従い残す |
| `.ai-context/specs/20260803_343_regression-test-foundation.md` | 凍結記録（作業仕様書） | **不変**——同上（2026-08-03 時点で「#203 を受け入れゲートとして接続する枠組みのみ規定」と書いた史実） |
| `scripts/check-cross-repo-refs.js` | 検査器の自己試験データ | **不変**——`#203` はテストの literal な入力文字列であり、issue への言及ではない |

`docs/` 配下で他に NFR-01/NFR-02・レイテンシ目標へ言及する文書（`docs/observability/observability.md`）も
`git grep -n "NFR-01\|NFR-02\|レイテンシ"` で確認したが、同文書は #689 の実装結果（計器の仕様）のみを記述し
#203 への言及は無かったため対象外（変更不要）。`docs/migration/20260903_cutover-and-retention.md` は
#637 を「未結線の統制」の open 項目として正しく列挙しているのみで是正不要。`docs/blocked-tasks.md` は
NFR-01/02・#203/#689/#690 のいずれにも触れておらず、本 issue が指す「追跡先」の欠落はこの文書には
存在しない（新規セクション追加は本 issue の射程外の拡張のため見送った）。

## 変更

1. `docs/tests/README.md`
   - trace ブロック `issues:` から `#203` を除き `#689` `#690` を追加。`iadrs:` に `IADR-0307`、
     `specs:` に `20260904_689_nfr-01-02-end-to-end-latency-metrics` を追加（#689 の記録を辿れるように）。
     `updated:` を `2026-09-11` へ前進。
   - 「6. 未整備」表の性能ゲート行: `#337（実測は #203 を接続）` → `#337（計器は #689 で新設済み。実測は #690 を接続）`。
   - 変更履歴表に本件の 1 行を追加。
2. `.ai-context/specs/` へ本仕様書を新設（本ファイル）。

## 受け入れ基準

- [x] `docs/tests/README.md` が #203 ではなく #689（完了）/ #690（追跡中）を指す
- [x] trace ブロックが `check-trace-blocks.js` の書式・値域検査を通る
- [x] #637 へ、上記調査結果（#690 が既に後継であること）をコメントで残す
- [x] 測定コード・実測は実施しない（#690 の前提〔実環境・実 LLM〕が未整備のため対象外）

## 計画書との差異

- 差異: なし。本作業は実装側の文書内トレーサビリティの是正であり、計画書（NFR-01/02 の目標値・定義）を
  変更するものではない。

## 未決事項

- `#637` を最終的にクローズするかどうかは、#637 自身の分割コメントが「親として残す（クローズしない）」と
  明記しているため、本 PR ではクローズしない（`Closes` キーワードを使わない）。#690 の実測完了後に
  親issue をどう扱うかは、実測を行う側（人間・API キー確保後）の判断に委ねる。
