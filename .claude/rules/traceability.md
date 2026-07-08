---
paths:
  - "**/*"
---

# トレーサビリティ（追跡可能性）の規約

計画書と実装を相互に追跡できるよう、実装の起点となる計画書の ID を以下に残す。コミット・PR・コードを編集する際は本ルールに従う。

## 起点 ID の種別

- `FR-xx`: 機能要求（計画リポ `02_requirements/`）
- `NFR`: 非機能要件
- `UC-xx`: ユースケース（`03_usecases/`）
- `SC-xx`: 画面（`05_screens/`）
- `ADR-xxxx`: 計画ADR（計画リポ `07_adr/`）
- `IADR-xxxx`: 実装ADR（本リポ `docs/adr/`。計画ADR とは別系統）

## 残す箇所と書式

- **ブランチ名**: `<種別>/<起点ID>-<概要のケバブケース>`。例 `feat/FR-012-login-validation`。
- **コミットメッセージ**: 先頭に `<種別>(<起点ID>): <要約>`。例 `feat(FR-012): ログイン画面のバリデーションを実装`。
  - 種別: `feat` / `fix` / `refactor` / `test` / `docs` / `chore` 等。
  - 1 コミット = 1 論理変更。複数 ID にまたがる場合は `feat(FR-012,UC-03): ...` のように併記する。
- **コード内コメント**: 計画書由来の実装箇所に ID を残す。例 `// FR-012, UC-03: 入力バリデーション`。
- **テスト**: テスト名またはコメントに起点 ID を残す。
- **PR**: PR テンプレートの該当欄に、実装した FR/UC・関連 ADR・受け入れ基準のチェックを記入する。

## 守ること

- 起点 ID を持たない大きな変更を作らない（雑多な変更は理由を明記する）。
- 計画書に存在しない ID を参照しない（誤記・廃止に注意）。
- ADR の制約に反する実装をしない。逸脱が必要なら新 ADR の起票を提案する。

## コミットメッセージの機械チェック（CI・再発防止）

PR で追加されるコミット（`base..HEAD`）の件名を `scripts/check-commit-messages.js` が検査し、規約
`種別(起点ID): 要約` に違反していれば CI を失敗させる（Issue #60）。既存履歴は書き換えず、再発防止のみを目的とする。

- **許可する種別**: `feat` / `fix` / `perf` / `refactor` / `docs` / `test` / `build` / `ci` / `style` / `chore`。
- **起点 ID の書式**: `FR-\d+` / `NFR` / `UC-\d+` / `SC-\d+` / `ADR-\d{3,4}` / `IADR-\d{3,4}` / `P0`〜`P3`。
  複数 ID はカンマ区切りで併記。スコープ `()` は省略可。
- **末尾の PR 番号**: 半角スペース + `(#123)` はスカッシュマージ既定件名として許容。

### PR タイトル（スカッシュ後件名）の検査（Issue #125・再発防止）

スカッシュマージで develop に載る件名は「**PR タイトル + 半角スペース + `(#番号)`**」であり、PR コミット
（`base..HEAD`）の範囲に含まれないため、上記のコミット件名チェックだけではすり抜ける
（`3d8852f`／PR #95 の生載りが該当）。これを防ぐため `pr-title.yml` が PR タイトルを規約
`種別(起点ID): 要約` に照合し、違反ならマージ前に CI を失敗させる。

- 検査ロジックは `check-commit-messages.js` の単一件名モード（`--title` 引数 / `PR_TITLE` 環境変数）で、
  `validateSubject` を再利用する（規約の単一情報源）。
- `pull_request` の `opened/edited/reopened/synchronize` で起動し、タイトル後編集も再検査する。
- bot 作成 PR（`pull_request.user.type == 'Bot'`）・Revert・`[skip ci]` は除外する。

**事前防止と事後補正の使い分け**: `pr-title.yml` は**マージ前**の予防（規約外件名を止める）。
万一すり抜けて develop に規約外件名が入った場合は、履歴不変の原則（force push 禁止）に従い、
下記 CHANGELOG 生成時の `changelog-overrides.json`（`action: "remap"`）で**生成物のみ**是正する。
前者が第一防衛線、後者が最終手段という役割分担。

### 検査対象から除外する自動コミット

- **自動コミットの著者**: `dependabot[bot]` / `renovate[bot]` / `github-actions[bot]` 等の bot 著者。
- **マージコミット**: `--no-merges` により除外。
- **自動生成・リバート**: 件名に `[skip ci]` を含むコミット、および `Revert "..."`。

除外リストは `scripts/check-commit-messages.js` の `BOT_AUTHORS` と同時に更新する。

## CHANGELOG 生成時の誤記補正・除外規定（Issue #60）

トレーサビリティは**既存の git 履歴を書き換えないこと**を大前提とする。過去コミットの起点 ID
やスコープに誤記があっても `rebase`／force push で修正してはならない。代わりに、`CHANGELOG.md`
を生成する `scripts/gen-changelog.js` が生成時のみ補正／除外を適用する。補正内容は
`scripts/changelog-overrides.json` の `overrides` 配列で宣言的に管理する（`hash` は短縮 SHA 前方一致）。

- **誤記補正（`action: "remap"`）**: 誤った起点 ID・種別・要約を、CHANGELOG 上でのみ差し替える。
  `type` / `scope` / `desc` を任意に指定でき、省略した項目は元コミットの値を保つ。
  - 例: `b421761`（件名 `feat(FR-10): ...`）は FR-10（Dashboard）とは無関係な P0 基盤スケルトン
    実装であり、`scope` を `FR-10` → `P0` へ補正する。実体は大規模実装のため `type` は `feat` の
    まま保持し、`docs` へは remap しない（実装をドキュメントとして過小計上する新たな誤帰属を避ける）。
- **除外（`action: "exclude"`）**: CHANGELOG に載せるべきでないコミット（試験的・巻き戻し前提の
  作業等）を生成物から除外する。git 履歴には残るため追跡可能性は失われない。
- 未知の `action`（タイプミス等）は `gen-changelog.js` が警告を出して補正を無視する（黙って
  remap 扱いにしない）。許可値は `remap` / `exclude` の 2 種のみ。

補正・除外はいずれも「履歴は不変・生成物のみ是正」という原則に従い、その根拠を各エントリの
`reason` に必ず残す。CI（`changelog.yml`）は `develop` / `main` への push で `fetch-depth: 0` の
全履歴から CHANGELOG を再生成し、本補正を含む差分を PR 経由で反映する。
