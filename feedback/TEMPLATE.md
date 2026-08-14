---
title: <フィードバック概要>
type: plan-feedback
status: open
category: <要求の誤り | 要求の不足 | UC/画面の差異 | 新たな制約(ADR要) | 用語追加 | その他>
related_ids: []
source_repo: <この実装リポジトリ名>
source_ref: <ブランチ / コミット / PR / 仕様書パス>
author: <作成者>
created: <YYYY-MM-DD>
dispatched: false
planning_issue:
---

<!--
  status —— 計画側の裁定がどこまで進んだかを表す。**「伝達したか」は表さない**（それは下の
  dispatched / planning_issue が担う）。値域と遷移は `feedback/README.md`「status の語彙」を読む。

    open → awaiting-decision → accepted / rejected  （一方向。awaiting-decision は飛ばしてよい）

  open のまま置くのは「まだ裁定が下りていない」という意味である。伝達し終えても、裁定が
  下りるまでは open のままでよい —— **伝達の事実は dispatched: true などで書く。**

  dispatched / planning_issue —— 計画リポジトリへ伝達したかの記録である（`scripts/check-feedback-dispatched.js`）。
  記録を作っただけでは計画へ届かない。伝達し終えたら必ずどちらかを更新すること。

  - GitHub Issue 経路 … `planning_issue: <計画リポの issue 番号または URL>` を書く
  - 記録ファイル経路 … 本文にコピーを載せた計画リポの PR URL を書く（`dispatched: true` でもよい）

  どちらか一方を書けば足りる（`dispatched: true` は両経路で使える）。
  **どちらも書かないまま `dispatched: false` を残すと、`status` に関わらず CI が警告する**（自己申告として扱う）。
  **`dispatched:` に書けるのは `true` / `false` だけである。** `no` / `off` と書くと
  「解釈できない値」として警告される（YAML 1.1 ではこれらも偽だが、鍵の意味を 2 通りに割らないため）。
-->

# フィードバック: <概要>

## 種別

<!-- 上のメタ情報 category と対応。何についてのフィードバックか -->

## 起点となる計画書

- 機能要求（FR）:
- ユースケース（UC）:
- 画面（SC）:
- 関連 ADR:
- 計画書リンク:

## 現状（計画書の記述 / As-Is）

<!-- 計画書に現在どう書かれているか。誤り・不足・未記載などを具体的に -->

## 問題点 / あるべき姿（To-Be）

<!-- 実装の観点から、何が問題で、どうあるべきか -->

## 実装で判明した経緯

<!-- どの作業（仕様書・コミット）で、なぜこの差異/問題に気づいたか -->

## 提案（計画への反映案）

<!-- 計画側でどう反映すべきかの案。反映先候補を1つ以上挙げる -->

- 反映先候補: 要求更新 / 新 ADR / UC・画面更新 / 用語追加（glossary）/ その他
- 提案内容:

## 影響範囲

<!-- この変更が他の要求・UC・ADR・実装に与える影響 -->
