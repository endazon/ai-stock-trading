---
title: MSP の submodule として置かれた AST で .claude/hooks/*.js が ES module と見なされて落ち、ガードが素通りになる（#1075）
type: spec
status: accepted
related_ids: [NFR]
author: claude (Claude Code)
created: 2026-09-28
updated: 2026-09-28
plan_refs: []
---

# MSP の submodule として置かれた AST で .claude/hooks/*.js が ES module と見なされて落ちる

## 背景

#1075（#1074 の監査で再現。#1073 と同じ原因）: MSP の submodule（`microservices-platform/src/ai-stock-trading`）を
Claude Code のプロジェクトとして開くと、`.claude/settings.json` が `node ${CLAUDE_PROJECT_DIR}/.claude/hooks/<name>.js` で
起動する hook が、親の MSP の `src/package.json`（`"type": "module"`）によって ES module として読まれる。
`require(` を使う hook は `require is not defined in ES module scope` で exit 1 になり、Claude Code では exit 1 は
「止める」扱いではないため、破壊的コマンドのガードが**何も言わずに素通り**になる。
NFR（運用補助・安全装置）の無採番メタ作業である。計画 ID・計画 ADR の新たな制約は無い。

## 再現（修正前・実測）

一時ディレクトリに `src/package.json`（`{"type":"module"}`）を置き、`src/ai-stock-trading/.claude/hooks/` へ hooks を**コピー**して起動:

| hook | 無害な入力 | `git push --force` を渡す |
| --- | --- | --- |
| `guard-bash.js` | exit 1（ReferenceError） | **exit 1（本来は 2＝ブロック）** |
| `check-impl.js` | exit 1（ReferenceError） | — |
| `guard-secrets.js` | exit 0（`require(` を持たないため ES module として読まれても動く） | — |

`.claude/hooks/package.json`（`{"type": "commonjs"}`）を置くと 3 本とも exit 0、force push は exit 2、PEM 見出しは exit 2（実測）。
MSP の `src/package.json` が `"type": "module"` であることは `/home/user/microservices-platform/src/package.json` で確認した（MSP は変更しない）。

## 母集合（規則 9: 誤りの側で走査する）

走査（`origin/develop` `a1870048`）: `git ls-files | grep -E '\.(js|mjs|cjs)$'`、`find .claude -type f`、`git ls-files '**/package.json'`。

| 範囲 | 件数 | module の形 | 本 PR |
| --- | --- | --- | --- |
| `.claude/hooks/*.js` | 3（`guard-bash.js`・`check-impl.js` が `require(`、`guard-secrets.js` は require なし） | CommonJS | `.claude/hooks/package.json` で止める |
| `.claude/hooks/` の ES module の `.js`（静的 `import`/`export`・メタプロパティ `import`＋`.meta`） | **0** | — | 改名不要。試験で 0 を固定 |
| `.claude/` の hooks/ の外（`agents/`・`commands/`・`rules/`・`settings.json`） | `.js`/`.mjs`/`.cjs` は **0**（`.md` と `settings.json` だけ） | — | 対象外。試験で 0 を固定 |
| `scripts/` | #1074 で `scripts/package.json` 済み | CommonJS | 変更なし（ES module 走査の関数だけ共有化） |
| `frontend/eslint.config.js` | 1 | ES module（`frontend/package.json` が止める） | 変更なし |

hook の起動は `settings.json` の 3 経路（PreToolUse Bash／PreToolUse Edit|Write／PostToolUse Edit|Write）だけで、いずれも `.claude/hooks/` 配下。
SessionStart は `bash scripts/setup.sh`（node ではない。`grep -n "node " scripts/setup.sh` は 0 件）。

## 案の比較

| 案 | 判断 |
| --- | --- |
| **A. `.claude/hooks/package.json` に `{"type": "commonjs"}`** | **採る**（#1074 と同形・最小。効く範囲は hooks/ だけ） |
| B. ルート `package.json` | 採らない（#1073 の作業仕様書 `20260928_1073_scripts-commonjs-package-scope.md` の比較のとおり、ルートが npm パッケージの根に見える等、影響が issue を越える） |
| C. hook を `.cjs` に改名 | 採らない（`settings.json` の変更が要る。本作業では `settings.json` を変えない） |

## 変更

1. `.claude/hooks/package.json` を新設（`{"type": "commonjs"}` だけ）。hook の中身・`settings.json` は変えない。
2. `scripts/scripts.repo.test.js`:
   - ES module の走査を関数 `scanCommonJsScope` に寄せ、#1073 節と本節で共有する。#1074 監査の非ブロッキング指摘
     「走査がメタプロパティ（`import`＋`.meta`）を拾わない」をここで直す（動的 `import(` は CommonJS でも書けるので数えない）。正規表現の単体試験 1 件。
     🔴 本ファイル自体が `scripts/` の走査対象なので、メタプロパティを字面で書かない（試験入力は連結で組む）。
   - #1075 節 3 件: 内容が `{ type: 'commonjs' }` と一致／hooks/ に ES module の `.js`・別の `package.json` が無く、`.claude/` の hooks/ の外に `.js` が無い／
     親に `"type": "module"` がある配置へ hooks/ を**コピー**して、3 本が無害な入力で exit 0、`guard-bash` が force push を exit 2、
     `guard-secrets` が PEM 見出しを exit 2。**陽性対照**として `package.json` だけを外すと `guard-bash`・`check-impl` が exit 1（ReferenceError）、force push も exit 1（素通り）。
     一時ディレクトリは `finally` で消す（試験が作ったものに限る）。
3. `docs/ai-workflow.md` の「暴走防止（ローカル）」行に `.claude/hooks/package.json` の役割を 1 文足す。

## 必読の予算

`scripts/check-reading-budget.js` の Claude Code の集合は `CLAUDE.md` ＋ `.claude/rules/*.md` だけ（`globDirs: ['.claude/rules']`）。
`.claude/hooks/package.json` は計測対象に入らない。実測 44,167 バイトで変更前後とも同じ。

## 受け入れ基準

1. 親に `"type": "module"` の `package.json` がある配置で、3 つの hook が無害な入力で exit 0、ガードの判定が exit 2。
2. `.claude/hooks/package.json` を外すと同じ配置で落ちる（試験が本物の症状を捉えている）。
3. `node scripts/scripts.test.js`・`check-commit-messages`・`check-trace-blocks`・`check-reading-budget` が通る。
