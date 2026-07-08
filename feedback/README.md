# feedback — 計画へのフィードバック記録

実装中に判明した**計画書（`project-planning`）の誤り・不足・新たな制約**を計画側へ環流（フィードバック）するための記録置き場である。

## いつ使うか

- 計画書（要求・UC・画面）の記述が実態と異なる、または不足している。
- 実装で新たな技術・設計上の制約が判明し、ADR を起こすべき。
- 計画書に未登録の用語が頻出する。

## 手順

1. `/plan-feedback <FR-xx|topic>` を実行する（`plan-feedbacker` エージェントが起票を補助）。
2. `feedback/TEMPLATE.md` を雛形に、`feedback/<YYYYMMDD>_<概要>.md` に記録が作成される。
3. 計画リポジトリへ伝達する（両経路に対応）。
   - **記録ファイル経路**: 作成した記録を計画リポジトリの `draft/feedback/` にコピー（submodule/隣接クローン経由）。
     計画側で `/triage-feedback` がトリアージする。
   - **GitHub Issue 経路**: `/plan-feedback` が生成した Issue 本文を、`endazon/project-planning` の
     「計画へのフィードバック」Issue テンプレートに貼り付けて起票する（GitHub MCP / `gh` でも可）。
4. 計画側でトリアージされ、要求更新 / 新 ADR / 用語追加などに反映される。

## 注意

- 記録は事実と提案を分けて書く。確定は計画側（人間 + `/triage-feedback`）が判断する。
- この `feedback/` ディレクトリは実装リポジトリ側の控えである。原典の反映先は計画リポジトリ。
