---
title: MSP の submodule として置かれた AST で scripts/*.js が ES module と見なされて落ちる（#1073）
type: spec
status: accepted
related_ids: [NFR]
author: claude (Claude Code)
created: 2026-09-28
updated: 2026-09-28
plan_refs: []
---

# MSP の submodule として置かれた AST で scripts/*.js が ES module と見なされて落ちる

## 背景

#1073: MSP の submodule（`microservices-platform/src/ai-stock-trading`、pin `1465f3e`）の中で
`node scripts/helm-release-drift.js ...` を実行すると、起動時に
`ReferenceError: require is not defined in ES module scope` で落ちた（配備手順が指定する場所）。
NFR（運用・CI 補助スクリプト）の無採番メタ作業である。計画 ID・計画 ADR の新たな制約は無い。

**原因**: AST のルートにも `scripts/` にも `package.json` が無い。Node は `.js` の module 種別を
「最も近い祖先の `package.json` の `"type"`」で決めるため、親を遡って MSP の `src/package.json`
（`"type": "module"`）を見つけ、AST の CommonJS スクリプトを ES module として読む。

## 再現（修正前）

一時ディレクトリに `src/package.json`（`{"type":"module"}`）を置き、その下の `src/ast/` へ `scripts/` を**コピー**して
`node scripts/helm-release-drift.js --help` → exit 1・上記の ReferenceError（issue と同じ）。

- 🔴 **シンボリックリンクでは再現しない**。Node は主モジュールを実体パスへ解決してから package scope を探すため、
  リンク元の親にある `package.json` は見えない。試験はコピーで組む。
- MSP の実物: `/home/user/microservices-platform/src/package.json` は `"type": "module"`（確認済み。MSP は変更しない）。

## 母集合（規則 9: 誤りの側で走査する）

走査（`origin/develop` `03f5d69b`）: `git ls-files | grep -E '\.(js|mjs|cjs)$'`、`git ls-files -- '**/package.json'`。

| 範囲 | 件数 | module の形 | 親の `"type": "module"` の影響 | 本 PR |
| --- | --- | --- | --- | --- |
| `scripts/*.js`（直下） | 38 本すべてが `require(` を使う（issue の 38 と一致。自分で数え直した） | CommonJS | **受ける**（本件） | `scripts/package.json` で止める |
| `scripts/lib/*.js`（4）・`scripts/fixtures/**/*.js`（1） | 5（うち 3 は `require(` を持たないが `module.exports` を使う） | CommonJS | 受ける（`require` される側。`module.exports` が ESM では未定義） | 同上（`scripts/` 配下なので同じ package scope） |
| `scripts/` 配下の ES module の `.js`（`import`/`export` 文） | **0**（`grep -E '^\s*(import |export )'`。動的 `import(`・`import.meta` も 0） | — | — | 改名不要。試験で 0 を固定 |
| `scripts/` 配下の `.mjs` / `.cjs` | 0 | — | — | — |
| `.claude/hooks/*.js`（3） | `check-impl.js`・`guard-bash.js` が `require(`。`guard-secrets.js` は require なし | CommonJS | **受けうる**（後述「残余」） | **対象外** |
| `frontend/`（`frontend/package.json` を持つ） | `eslint.config.js` 1 本 | ES module | **受けない**。`frontend/package.json` が最も近い祖先で、しかも自身が `"type": "module"`。探索はそこで止まる | 変更なし |
| 上記以外（`backend/`・`deploy/`・`infra/`・`docs/`・ルート） | `.js`/`.mjs`/`.cjs` は 0 | — | — | — |
| 追跡下の `package.json` | `frontend/package.json` の 1 つだけ（ルート・`scripts/` には無い） | — | — | `scripts/package.json` を足して 2 つ |

`scripts/*.sh`（`k8s-local-deploy.sh` など）が `node scripts/...` を叩く経路も、同じ `scripts/` の package scope に入るため本変更で直る。

## 案の比較

| 案 | 効く範囲 | 影響 | 判断 |
| --- | --- | --- | --- |
| **A. `scripts/package.json` に `{"type": "commonjs"}`** | `scripts/` 配下（本件の 43 本） | `scripts/` の外の解決は変わらない。npm/pnpm のプロジェクトとしては振る舞わない（依存・`scripts`・`name` を持たない） | **採る**（最小） |
| B. ルート `package.json` に `{"type": "commonjs"}` | `scripts/` と `.claude/hooks/`（`frontend/` は自前の `package.json` で止まるので不変） | ルートが npm パッケージの根に見える（ルートで `npm`/`pnpm` を叩くと本ファイルを拾う・`npm prefix` が変わる・Dependabot の npm エコシステムを `/` で有効化すると対象になる）。将来ルート近傍へ置く ES module の `.js` まで一括で CommonJS になる。MSP 側では `src/ai-stock-trading/package.json` が現れる（MSP の pnpm workspace は `*/frontend` なのでメンバにはならないが、ユニット直下に package.json があるという新しい形を持ち込む） | 採らない（影響が issue の範囲を越える） |
| C. CommonJS のスクリプトを `.cjs` に改名 | 改名した分 | 43 本の改名と、ワークフロー・README・文書・`.sh`・`require('./x.js')` の参照追随が参照数に比例して肥大化する | 採らない |

## 変更

1. `scripts/package.json` を新設（内容は `{"type": "commonjs"}` だけ）。
2. `scripts/scripts.repo.test.js` に試験 3 件（#1073）:
   - `scripts/package.json` の内容が `{ type: 'commonjs' }` と一致する。
   - `scripts/` 配下に ES module の構文の `.js` が無い／`scripts/` の中に別の `package.json` が無い（探索を途中で止めて `"type"` を変えうるため）。
   - `os.tmpdir()` の下に `src/package.json`（`"type": "module"`）＋`src/ai-stock-trading/scripts/`（コピー）を作り、
     `helm-release-drift.js --help`・`helm-release-drift.js --self-test`・`check-review-verdict.js --self-test`（兄弟スクリプトと `lib/` を require する）が exit 0。
     **陽性対照**として同じ配置からコピー側の `scripts/package.json` だけを外すと `require is not defined in ES module scope` で落ちることも確かめる。
     一時ディレクトリは `finally` で `fs.rmSync(root, { recursive: true, force: true })`（試験が自分で作ったものに限る）。
3. `scripts/README.md` の本リポ固有の表に `package.json` の行を足す。

必読の予算（`CLAUDE.md`・`.claude/rules/*.md`）は変えない。

## 残余（本 PR の対象外）

- **`.claude/hooks/*.js`**: AST の submodule の中を Claude Code のプロジェクトディレクトリにしてセッションを開くと、
  `guard-bash.js`・`check-impl.js` も同じ理由で ES module として読まれうる（`${CLAUDE_PROJECT_DIR}/.claude/hooks/...` を `node` で起動する）。
  配備手順が指定する経路（`node scripts/<name>.js`）ではないため本件の射程外とし、報告に残す。直すなら `.claude/hooks/package.json` の同形か案 B。

## 受け入れ基準

1. 親に `"type": "module"` の `package.json` がある配置で、`node scripts/helm-release-drift.js --help` / `--self-test` が exit 0。
2. `scripts/package.json` を外すと同じ配置で落ちる（試験が本物の症状を捉えている）。
3. `node scripts/scripts.test.js`・`check-commit-messages`・`check-trace-blocks`・`check-reading-budget` が通る。
