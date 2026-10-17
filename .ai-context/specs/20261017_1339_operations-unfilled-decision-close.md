---
title: 判断由来の決済の指値が約定しないときの運用手順（暫定手段・ログの読み方・系による取消の有効化の前提と手順）を運用仕様書へ置き、「実装済みだが発動しない機能」へ載せる（計画 ADR-0067 決定 7・#1339）
type: spec
status: done
related_ids: [FR-05, FR-10, FR-11, FR-19, ADR-0040, ADR-0050, ADR-0067, IADR-0357, IADR-0461, IADR-0535, IADR-0536, IADR-0539]
author: claude (Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0067_decision-exit-limit-unfilled-cancel-only.md (決定 2・6・7・フォローアップ 3)
---

# 仕様書: 判断由来の決済の指値が約定しないときの運用手順（計画 ADR-0067 決定 7・#1339）

> 本仕様書は着手前に作成した（着手 2026-10-17）。基点は AST `origin/develop` `31b1a087`。
> 計画は project-planning `origin/main` `55e464a` で ADR-0067 を読んだ。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-10**（手仕舞いは止めない）・FR-05（失注）・FR-11（観測）・FR-19（取消も検知の適用範囲）。
- 計画 ADR: **ADR-0067 決定 7**（統制と実現手段。**配備までの暫定手段＝「15 分の WRN を見て、利用者が証券会社の画面で手で取り消す」**）・決定 2（既定は無効・値の変更の手続き）・決定 6（有効化は #1214 の後）・フォローアップ 3（有効化は利用者が決める。そのとき N を PoC の実績で見直す）。
- 起点 issue: **#1339**。#1341（IADR-0536）と #1355（IADR-0539）の後の残り。
- 新規 IADR: なし（文書だけの変更で、実装判断を含まない）。

## 残っている作業の洗い出し（2026-10-17）

issue 本文・全コメント（最後は 2026-10-16 の PoC 配備・確認手順）、ADR-0067 のフォローアップ、IADR-0536・IADR-0539 の残余、#1341・#1355 の差分を読んで分けた。

| 分類 | 項目 | 根拠 |
| --- | --- | --- |
| (a) AI だけで完結 | **運用者が読む文書に、判断由来の決済の未約定の扱いが 1 行も無い。** ADR-0067 決定 7 の暫定手段は「WRN を見た利用者の手での取消」だが、その WRN の意味・人がすること・系による取消の有効化の前提と手順は、issue のコメントにしか無い | `docs/operations/` を `未約定の決済`・`処理中の決済を解放`・`DecisionCloseUnfilledCancel`・`失注` で走査して 0 件（下の母集合） |
| (a) AI だけで完結 | **`docs/blocked-tasks.md` §補足「実装済みだが実際には発動しない機能」に、既定無効の取消が無い** | 同表を `1339`・`ADR-0067`・`未約定` で走査して 0 件 |
| (b) オーナーの判断待ち | 系による取消を有効にするか、そのときの N（暫定 15 分）・回数の見直し | ADR-0067 決定 2・6・フォローアップ 3。値の変更は生成 AI と自動処理は行えない |
| (b) オーナーの判断待ち | 未約定の決済を Discord へも送るか（論点 D。運用の好み） | issue コメント 2026-10-15 の論点表 D |
| (b) 計画の未決 | §5 に相場操縦の検知の値を置くか。`Disabled` 以外の失敗の状態（`SubmitFailed`・`Failed`・`Deleted`）の扱い | ADR-0067 フォローアップ 4（計画 INDEX §未決事項） |
| (c) PoC での確認待ち | #1214（保護逆指値の GTC の実弾確認）。有効化の前提 | ADR-0067 決定 6・blocked-tasks A-19 |
| (c) PoC での確認待ち | 配備後の失注表示（`Expired（失注…）`）・移行の適用・既定無効で取消のログが 0 件であること | issue コメント 2026-10-16 手順 1〜3 |
| (c) PoC での確認待ち | 失効した注文が翌日の当日一覧に現れるか（現れなければ履歴照会を足すか判断する） | IADR-0536 残余 1。10-13 の注文は通常の追跡の中で終端化したので未確定のまま |
| (c) PoC での確認待ち | 有効化したときの SIMULATE での取消・回数の上限・Npgsql での候補の問い合わせ | IADR-0539 残余 3・issue コメント 2026-10-16 手順 4 |

本仕様書は (a) の 2 項目だけを扱う。**既定値・Helm values は変えない**（有効化はオーナーの判断）。

## 母集合（規則 9・10）

- 誤りの側の文字列（「運用文書に無い」ことの確認）: `git grep -n -E '判断由来の決済|未約定の決済|失注|DecisionCloseUnfilledCancel|処理中の決済を解放' -- docs deploy README.md`。
  - 当たり: `docs/functional/FR-10_risk-controls.md`・`FR-19_trading-guard.md`・`FR-15_backtest.md`・`docs/tests/*`・`docs/tech/system-architecture.md`・`docs/api/events-and-ports.md`・`docs/data/audit-events.md`・`docs/operations/nightly-ledger-summary-runbook.md`（LLM の前の見送りの理由の件数）・`deploy/observability/README.md`・`deploy/helm/ai-stock-trading/values-local.yaml`（見送りの説明）。
  - **除外の理由**: どれも機能・試験・事象の定義か、見送りの理由の集計である。運用者の手順（WRN を見たときにすること・有効化の手順）を書いた文書は無い。今回の追記で誤りになる記述も無い（失効の表示を `Rejected` と書いた運用文書は無い。`失効.{0,40}Rejected` の走査で `docs/functional/FR-10`・`docs/tests/` 以外 0 件）。
- `docs/operations/broker-execution-paths-runbook.md` §2 の「moomoo 経路だけが出す行」の表にも載せない——同表は**経路の識別**の表であり、本件の行は運用の手順として運用仕様書に 1 箇所で持つ（2 箇所に置くと片方が古くなる）。
- 規則 10（新たに誤りになる自分の記述）: 運用仕様書の新節は「既定は無効」「Helm values に置かない」と書く。将来の有効化で古くなるので、**有効化するときはこの節と blocked-tasks の行を同じ変更で直す**と節に書く。
- 規則 11（窓）: 本件は文書だけで、窓の形を新たに決めない（窓は IADR-0539 の作業仕様書で確定済み）。

## 変更

1. `docs/operations/operations.md` に節「判断由来の決済の指値が約定しないとき（#1339）」を足す（「障害対応（Runbook）」の前）。
   - 何が起きるか（DAY の指値・その間は建玉の判断が止まる・損切りは止まらない・失効の後は次の巡回で判断し直す）。
   - ログと通知の読み方（WRN「未約定の決済が残っています」・INF「処理中の決済を解放しました」・失注と拒否の表示・Discord「手仕舞いが約定せず終了」）。
   - **人がすること（配備までの暫定手段）**: WRN が続き、当日中に判断し直したいときは、証券会社の画面でその注文を手で取り消す。
   - 系による取消（既定は無効）: キー・既定値・有効時の検証範囲・前提（#1214 の後・利用者の判断）・入れ方（order-execution の `extraEnv` に足して `helm upgrade`。`--set`・`kubectl set env` を使わない）・停止・ログ。
   - 確認クエリ（処理中の判断由来の決済）。
   - trace ブロック・`updated:` を更新する。
2. `docs/blocked-tasks.md` §補足 の表に行を足し、`最終更新` を 1 行足す。

## 受け入れ基準

| # | 基準 | 確かめ方 |
| --- | --- | --- |
| A1 | 節の値（N=15・回数 1・検証範囲 6〜390 / 1〜3・WRN 15 分・照会し直し 10 分）がコードの既定と一致する | `DecisionCloseUnfilledCancelOptions.cs`・`DecisionCloseUnfilledCancelDefaults.cs`・`FillPollingOptions.cs` と突き合わせる |
| A2 | ログの文言がコードの文字列と一致する（grep で引ける） | `OrderFillPollingService.cs`・`DecisionCloseUnfilledCanceller.cs` と突き合わせる |
| A3 | 確認クエリの列名・列挙値がスキーマと一致する（`PositionEffect` Close=1・`ApprovalOrigin` TradeDecision=1・`Status` Accepted=0 / PartiallyFilled=1） | 列挙の定義と移行のスナップショット（integer）で確かめる |
| A4 | docs の trace ブロック規約・リンク・計画 ID の修飾・クロスリポ参照・知識グラフの検査が通る | `check-trace-blocks`・`check-doc-links`・`check-plan-id-qualification`・`check-cross-repo-refs`・`gen-knowledge-graph --check`・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` |
| A5 | 既定値・Helm values を変えていない | `git diff --stat` に `backend/`・`deploy/` が無い |

## 残余

- 本仕様書は issue を閉じない。(b)・(c) が残る（上の表）。
