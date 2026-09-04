---
title: AI レビューの許可リストにシェルテストの実行を足し、CI との非対称を検査で固定する
issue: "#683"
plan_refs:
  - NFR
adr_refs:
  - IADR-0299
status: done
created: 2026-09-04
---

# 作業仕様書: AI レビューの許可リストにシェルテストの実行を足し、CI との非対称を検査で固定する（#683）

## 背景

PR [#678](https://github.com/endazon/ai-stock-trading/pull/678)（`scripts/k8s-local-deploy.sh` の
前回値引き継ぎ拡張）で `claude-review` ジョブが赤くなった。原因は
`scripts/check-permission-denials.js` が拾った **6 件の許可拒否**であり、その **6 件すべて**が
「レビュワーがリポジトリ自身のシェルテストを実走しようとして拒否された」ものだった。

```
Bash(bash scripts/k8s-local-deploy.test.sh)          ×2
Bash(bash /home/runner/work/.../scripts/k8s-local-deploy.test.sh)
Bash(bash scripts/k8s-local-deploy.test.sh | tail -30)
Bash(sh scripts/k8s-local-deploy.test.sh)
```

`.github/workflows/claude-code-review.yml` の `--allowedTools` は 56 件あるが、
**`bash` / `sh` で始まる許可が 1 件も無かった**。git・rg・cat・node・dotnet・npm・helm・yq・gh は
あるのに、シェルスクリプトの実行だけが抜けている。

### 同型かどうかの確認（利用者の依頼 1）

`*.test.sh` を触った直近の PR を調べた。

| PR | 拒否件数 | うちシェルテスト起因 |
| --- | --- | --- |
| [#678](https://github.com/endazon/ai-stock-trading/pull/678) | 6 | **6（全件）** |
| [#647](https://github.com/endazon/ai-stock-trading/pull/647) | 8 | **5** |

#647 の 5 件は `bash scripts/k8s-local-deploy.test.sh` / `./scripts/k8s-local-deploy.test.sh` /
素の `bash` / `bash … | tail` / `bash … | head` である。**同じ穴を、別々の PR で、別々の
エージェントが、それぞれ 3 通り以上の呼び方で踏んでいる。** 偶発ではなく系統的な穴である。

CLAUDE.md の「検査器・規約の追加は同型事故 2 回から」の条件を満たす（#678 と #647 の 2 回）。

### 影響の性質（利用者の依頼 3 の確認）

`develop` のルールセット `18662050` が要求する check は **`pr-title` の 1 本だけ**である
（`gh api repos/endazon/ai-stock-trading/rulesets/18662050` で実測）。
したがって `claude-review` の赤はマージを止めない。**止めない代わりに、シェルテストを含む変更が
「テストを実走しないまま」レビュー通過し続ける。** #678 の PR 本文は「78 件緑」を主張していたが、
レビュワーはそれを一度も追試できていない。**赤くならないぶんだけ悪い**種類の劣化である。

## 決めたこと

詳細と根拠は [IADR-0299](../adr/IADR-0299_review-allowlist-shell-tests.md)。要点のみ。

1. **CI が `bash <path>` で実走している 3 本だけを、そのままの形で許可する**（glob にしない）。
2. **実装用（`claude-coding.yml`）と `.claude/settings.json` にも同じ 3 件を足す**（3 系統一致）。
3. **`shellTestDrift()` を `scripts/check-ai-workflow-config.js` へ新設**し、CI の `ci.yml` から
   `run: bash *.test.sh` を抽出して両ワークフローの許可と突き合わせる。
4. **レビュープロンプトに呼び方を明記する**（`bash <path>` の 1 形。`sh` / `./` / `cd &&` /
   `VAR=1` は前方一致に掛からない）。

## 変更したファイル

| ファイル | 変更 |
| --- | --- |
| `.github/workflows/claude-code-review.yml` | `--allowedTools` へ 3 件追加（56 → 59）／【検証の実行】節へ呼び方の明記を追加 |
| `.github/workflows/claude-coding.yml` | `--allowedTools` へ同じ 3 件追加 |
| `.claude/settings.json` | `permissions.allow` へ同じ 3 件追加（74 → 77） |
| `scripts/check-ai-workflow-config.js` | `CI_WORKFLOW_PATH` 定数・`shellTestDrift()` 新設・main へ配線・自己試験 6 件追加 |

## 検証

- `node scripts/check-ai-workflow-config.js` → `✓ AI ワークフローのツール許可設定に問題なし`
  （3 ファイルを検査。parity 警告なし）
- `node scripts/check-ai-workflow-config.js --self-test` → **30 件すべて合格**（新設 6 件を含む）
- **変異試験**: `claude-code-review.yml` から `Bash(bash scripts/k8s-local-deploy.test.sh:*)` を
  1 件だけ削ると
  `✗ 設定の不備 2 件: claude-code-review.yml: CI が実走しているシェルテストの実行が許可されていない: …`
  で赤くなることを実測し、復元して緑を再確認した。**検査が load-bearing であることの証跡**である。
- YAML の妥当性（`yaml.safe_load`）を両ワークフローで確認。
- **3 本のシェルテストをローカルで実走**し、許可の形（`bash <path>`）で動くことを確認した。

## 引いた母集合と、除外したものと理由

**軸 1（拒否の実測）**: `check-permission-denials` が赤くした直近の run から拒否文字列を全数取得。
**軸 2（許可の側）**: `--allowedTools` を分解し `bash` / `sh` で始まる要素を数える（0 件）。
**軸 3（CI の側）**: `ci.yml` の `run: bash *.test.sh` を全数取得（3 件）。

| 除外 | 理由 |
| --- | --- |
| `scripts/*.sh`（テストでない実体スクリプト） | 配備・イメージ投入など**副作用のある**スクリプトである。レビュワーが実走してよい対象ではない |
| `sh <path>` / `./<path>` の許可 | **CI と同じ 1 形へ寄せる**。呼び方が増えるほど、許可の前方一致と実際の呼び出しがずれる面が増える |
| `Bash(bash scripts/*.test.sh:*)` のような glob | PR が追加した任意の `*.test.sh` に自動で実行権が付く。**IADR-0299 決定 2** |
| `frontend/` 配下の shell | 存在しない（`npm run` 経由） |
