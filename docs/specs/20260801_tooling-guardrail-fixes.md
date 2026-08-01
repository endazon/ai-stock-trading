---
title: 実装ツールのガードレール形骸化・誤検知累積の是正（check-impl / IADR 実在性 / gitleaks / plan-feedback）
type: spec
status: done
related_ids:
  - NFR
author: claude
created: 2026-08-01
updated: 2026-08-01
related_specs:
  - "./20260731_303_per-model-llm-pricing.md"
  - "./20260731_289_webhook-url-log-redaction.md"
---

# 仕様書: 実装ツールのガードレール形骸化・誤検知累積の是正

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#319](https://github.com/endazon/ai-stock-trading/issues/319)
  （コミット件名の起点 ID が IADR の実体と乖離しても CI が検知しない。PR #314 の実害が根拠）。
- 関連 issue: [#280](https://github.com/endazon/ai-stock-trading/issues/280) /
  [#308](https://github.com/endazon/ai-stock-trading/issues/308)（報告書期間キーの gitleaks 誤検知が再発）。
- 計画根拠: 本作業は計画書由来の機能実装ではなく、CLAUDE.md「自動化・検証・安全」のガードレール
  （hooks / CI ゲート / トレーサビリティ機械チェック）自体の保守である（起点 ID は NFR 扱い）。

## 背景と問題（フィードバック・過去 PR からの棚卸し）

1. **`.claude/hooks/check-impl.js` の形骸化**: 「作業仕様書なし実装」の警告が
   「`docs/specs/` に .md が 1 つでもあれば合格」という判定のため、仕様書が 79 件以上
   蓄積した現在は恒久的に無警告となっている。CLAUDE.md の粒度（作業/PR 単位で着手前に必須）
   を検査できていない。
2. **起点 ID の実在性未検証**: `scripts/check-commit-messages.js` は起点 ID の**書式**のみを
   検査し、実体の存在を見ない。PR #314 ではコミット件名・ブランチ名が実体（IADR-0122）と
   異なる IADR-0121 を名乗ったまま develop に載った（issue #319）。
3. **gitleaks 誤検知の fingerprint 累積**: 報告書の期間キー（`weekly-2026-W31` 等）が
   generic-api-key ルールに繰り返し誤検知され（#280 / #308）、`.gitleaksignore` への
   fingerprint 事後追記が直近 1 週間で 4 件累積した。事後対応では再発を止められない。
4. **`/plan-feedback` の経路記述が実態と乖離**: コマンド定義は「記録ファイルを
   `draft/feedback/` へコピー」を第一経路として記述するが、実運用の環流は
   project-planning への GitHub Issue 起票（planning#50〜91 の実績）が主経路になっている。

## 対応方針（To-Be）

1. `check-impl.js` の作業仕様書判定を「現在のブランチで追加された仕様書があるか」
   （`origin/develop` との差分＋未追跡ファイル）へ強化する。base が解決できない環境では
   従来判定へ退避する（fail-open・警告のみの性格は維持）。
2. `check-commit-messages.js` に **ADR / IADR 実在性検査**を追加する（issue #319 の「やること」）。
   スコープの `IADR-xxxx` は `docs/adr/IADR-xxxx_*.md`、`ADR-xxxx` は planning submodule の
   `07_adr/ADR-xxxx_*.md` の実在を照合し、無ければ CI を失敗させる。範囲検査（PR コミット）と
   単一件名検査（PR タイトル）の両方に適用する。`docs/adr/` を読めない環境・planning 未 populate の
   環境では該当検査をスキップする（fail-open。`check-doc-links.js` と同じ扱い）。あわせて
   採番衝突時の改番手順（先着尊重・欠番を作らない・**PR タイトルも直す**）を
   `.claude/rules/traceability.md` へ明文化する。FR/UC/SC の実在性検証は定義箇所が
   ファイル名に現れず走査コストが高いため対象外とし、issue #319 の判断に委ねる。
3. `.gitleaks.toml` を新設し（既定ルールは `extend.useDefault` で維持）、報告書期間キーの
   値形（`daily|weekly|monthly` + 年）だけを狙い撃ちで allowlist する。既存の
   `.gitleaksignore`（履歴 fingerprint）は維持する。
4. `/plan-feedback` コマンド定義の伝達経路を「Issue 起票＝主、記録ファイル＝従」へ更新する。

## 受け入れ基準

- [ ] `node scripts/scripts.test.js` が既存テストと追加テスト（IADR 実在性）を含め合格する。
- [ ] `node scripts/check-commit-messages.js` が本ブランチのコミットで合格する。
- [ ] `check-impl.js` が「ブランチに仕様書追加なし＋コード編集」で警告を出し、本仕様書の
  追加後は警告を出さない（手動確認）。
- [ ] gitleaks の既定ルールが無効化されていない（`extend.useDefault = true`）。

## 影響範囲

- CI（`ci.yml` の commit-messages ジョブ・`pr-title.yml`・`security.yml` の gitleaks）と
  ローカル hooks のみ。アプリケーションコード・計画書への影響はない。
