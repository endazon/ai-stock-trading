---
title: テンプレート由来の存在しない Issue/PR/SHA 参照の除去
type: spec
status: review
related_ids: [P3]
author: endazon (with Claude Code)
created: 2026-07-09
updated: 2026-07-09
plan_refs: []
---

# 仕様書: テンプレート由来の存在しない Issue/PR/SHA 参照の除去

> Issue #32 の対応。`.claude/rules/traceability.md` や `scripts/` の文書・設定・コメントが、上流テンプレート
> impl-handoff-kit 由来の**本リポジトリに存在しない** Issue 番号（#60/#71/#95/#118/#125）・PR 番号・コミット SHA
> （3d8852f/b421761）・IADR-0014 を参照している問題を是正する。

## 起点・課題

- 起点 ID: P3（リポジトリ整備。特定 FR/UC に紐づかないハウスキーピング）
- 対象 Issue: #32
- 課題: 読者（人間・AI）が根拠を辿れず誤った文脈を与える。参照先の機構（コミット件名チェック・PR タイトル
  チェック・CHANGELOG 補正）自体は実在するスクリプト/ワークフローで、Issue/SHA 番号のみが幻の参照。

## 対象範囲

- `.claude/rules/traceability.md`: 存在しない Issue/PR/SHA 参照を除去し、機構の説明は実在ファイル名で行う。
- `scripts/changelog-overrides.json`: 実在しないコミット（b421761/3d8852f）の remap エントリを削除して空配列にし、
  `_note` を書き換える（本リポに補正対象コミットは無い）。
- `scripts/gen-changelog.js`: `applyOverride` に override 一覧を注入できる引数を追加（テストを実データ非依存に）。
- `scripts/scripts.test.js`: 幻の SHA に依存したテストを合成 override 注入に置き換え、コメントの Issue 参照を除去。
- `scripts/check-commit-messages.js` / `scripts/commit-allowlist.json` / `.github/workflows/{ci,openapi}.yml`:
  コメント・注記中の Issue 番号参照を機構説明に置換。
- `scripts/README.md`: 「出典」節を追加し、これらの機構がテンプレート由来で、テンプレート側の番号は本リポに
  存在しないことを明記（`または出典明記` の要件を満たす）。

## 受け入れ基準

- [ ] リポ内文書・設定・スクリプトが参照する Issue/PR/コミットが、すべて本リポで解決できる（または出典明記）
- [ ] `node scripts/scripts.test.js` が全緑
- [ ] `node scripts/check-commit-messages.js` / `node scripts/gen-changelog.js` / `node scripts/check-doc-links.js` が正常動作
- [ ] `changelog-overrides.json` / `commit-allowlist.json` が有効な JSON

## 計画書との差異

- 差異なし（計画書に紐づかないリポ整備）。
