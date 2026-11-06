#!/usr/bin/env node
'use strict';
/*
 * pr-trust-gate.js — AI レビュー（claude-code-review.yml）を走らせてよい PR かを、ジョブの中で判定する（NFR, IADR-0625）。
 * 外部依存ゼロ（Node 標準モジュールと git）。
 *
 * なぜジョブの中で判定するか:
 *   `claude-review` は必須チェックである。ジョブ単位の `if:` で外すと、**skipped は必須チェック上「合格」**として
 *   扱われる。外したい PR（フォークから・外部の作成者・ブランチ名だけを `automation/` に似せた PR）まで合格になる。
 *   ジョブは常に走らせ、最初の step で判定し、**走らせない理由が「信頼できる自動化の PR」のときだけ合格**にする。
 *   それ以外で AI を走らせない PR は**赤**にする（書き込み権限を持つ者が差分を読み、本リポジトリのブランチから出し直す）。
 *
 * 判定（すべて GitHub が認証した値で見る。ブランチ名だけでは決めない）:
 *   - 同一リポジトリ = `head.repo.full_name == github.repository`（本リポジトリへ push できるのは書き込み権限を持つ者だけ）
 *   - dependabot: 同一リポジトリ かつ 作成者のログインが `dependabot[bot]` → skip（合格。AI は走らせない）
 *   - 生成物の自動 PR: 同一リポジトリ かつ head が `automation/` かつ 作成者が `github-actions[bot]` または
 *     OWNER / MEMBER / COLLABORATOR かつ **変更ファイルがすべて生成物**（`GENERATED`）→ skip（合格）
 *   - 同一リポジトリ かつ（作成者が OWNER / MEMBER / COLLABORATOR、またはログインが `[bot]` で終わる）→ run
 *   - それ以外 → refuse（exit 1。赤）
 *
 * 入力は環境変数（ワークフローの `env:` から渡す。値は式で run へ展開しない）:
 *   EVENT_NAME / REPOSITORY / HEAD_REPO / HEAD_REF / PR_AUTHOR / PR_ASSOCIATION / BASE_SHA / HEAD_SHA
 * 出力: `GITHUB_OUTPUT` へ `run=true|false`。
 *
 * 使い方:
 *   node scripts/pr-trust-gate.js
 *   node scripts/pr-trust-gate.js --self-test
 */
const fs = require('fs');
const { execFileSync } = require('child_process');

const TRUSTED = new Set(['OWNER', 'MEMBER', 'COLLABORATOR']);
/** 自動 PR が変えてよい生成物（changelog.yml / openapi.yml が作る PR の add-paths と同じ）。 */
const GENERATED = new Set(['CHANGELOG.md', 'docs/api/openapi.yaml']);
const SHA_RE = /^[0-9a-f]{40}$/;

/**
 * 判定の本体（純関数）。`files` は変更ファイルの配列（取れなければ null）。
 * @returns {{ decision: 'run'|'skip'|'refuse', reason: string }}
 */
function decide({ eventName, repository, headRepo, headRef, author, association, files }) {
  if (eventName !== 'pull_request') return { decision: 'refuse', reason: `pull_request 以外のイベント（${eventName || '不明'}）では判定しない` };
  const sameRepo = Boolean(repository) && headRepo === repository;
  if (!sameRepo) return { decision: 'refuse', reason: `本リポジトリのブランチから出た PR ではない（head: ${headRepo || '不明'}）` };
  const login = String(author || '');
  const trustedAuthor = TRUSTED.has(String(association || ''));
  if (login === 'dependabot[bot]') return { decision: 'skip', reason: 'dependabot の PR（AI レビューは走らせない）' };
  if (String(headRef || '').startsWith('automation/') && (login === 'github-actions[bot]' || trustedAuthor)) {
    const generatedOnly = Array.isArray(files) && files.length > 0 && files.every((f) => GENERATED.has(f));
    if (generatedOnly) return { decision: 'skip', reason: `生成物だけの自動 PR（${files.join(', ')}）` };
    // 生成物以外を含む・差分が取れない automation/ の PR は、通常の PR として扱う（ブランチ名では外さない）。
  }
  if (trustedAuthor || login.endsWith('[bot]')) return { decision: 'run', reason: `作成者 ${login}（${association}）` };
  return { decision: 'refuse', reason: `作成者 ${login || '不明'}（${association || '不明'}）は AI レビューの自動起動の対象外` };
}

/** base...head の変更ファイル（取れなければ null）。SHA は形を確かめてから引数の配列で渡す。 */
function changedFiles(baseSha, headSha) {
  if (!SHA_RE.test(String(baseSha)) || !SHA_RE.test(String(headSha))) return null;
  const run = (args) => execFileSync('git', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
  try {
    try { run(['fetch', '--no-tags', '--depth=1', 'origin', baseSha, headSha]); } catch { /* 取得済みなら不要 */ }
    return run(['diff', '--name-only', `${baseSha}...${headSha}`]).split('\n').map((s) => s.trim()).filter(Boolean);
  } catch {
    return null;
  }
}

function selfTest() {
  const assert = require('assert');
  let n = 0;
  const ok = (name, fn) => { fn(); n++; console.log(`  ok  ${name}`); };
  const base = { eventName: 'pull_request', repository: 'o/r', headRepo: 'o/r', headRef: 'feat/x', author: 'alice', association: 'COLLABORATOR', files: ['backend/a.cs'] };
  const d = (o) => decide({ ...base, ...o }).decision;
  ok('同一リポジトリの信頼できる作成者は run', () => { assert.strictEqual(d({}), 'run'); assert.strictEqual(d({ association: 'OWNER' }), 'run'); });
  ok('同一リポジトリの bot（ログインが [bot] で終わる）は run', () => assert.strictEqual(d({ author: 'copilot-swe-agent[bot]', association: 'NONE' }), 'run'));
  ok('フォークからの PR は作成者にかかわらず refuse', () => {
    assert.strictEqual(d({ headRepo: 'someone/r' }), 'refuse');
    assert.strictEqual(d({ headRepo: 'someone/r', association: 'OWNER' }), 'refuse');
  });
  ok('🔴 ブランチ名だけを automation/ に似せても skip にならない（フォーク・外部の作成者・生成物以外を含む）', () => {
    assert.strictEqual(d({ headRef: 'automation/changelog-update-develop', headRepo: 'someone/r', author: 'github-actions[bot]', files: ['CHANGELOG.md'] }), 'refuse');
    assert.strictEqual(d({ headRef: 'automation/x', author: 'mallory', association: 'NONE', files: ['CHANGELOG.md'] }), 'refuse');
    assert.strictEqual(d({ headRef: 'automation/x', files: ['CHANGELOG.md', 'backend/a.cs'] }), 'run');
    assert.strictEqual(d({ headRef: 'automation/x', author: 'github-actions[bot]', association: 'NONE', files: null }), 'run');
  });
  ok('生成物だけの自動 PR（同一リポジトリ・信頼できる作成者）は skip', () => {
    assert.strictEqual(d({ headRef: 'automation/changelog-update-develop', author: 'github-actions[bot]', association: 'NONE', files: ['CHANGELOG.md'] }), 'skip');
    assert.strictEqual(d({ headRef: 'automation/openapi-update-develop', association: 'OWNER', files: ['docs/api/openapi.yaml'] }), 'skip');
  });
  ok('dependabot は同一リポジトリのときだけ skip。外部の作成者は refuse', () => {
    assert.strictEqual(d({ author: 'dependabot[bot]', association: 'NONE' }), 'skip');
    assert.strictEqual(d({ author: 'dependabot[bot]', headRepo: 'x/r' }), 'refuse');
    assert.strictEqual(d({ author: 'bob', association: 'CONTRIBUTOR' }), 'refuse');
    assert.strictEqual(d({ author: 'bob', association: 'NONE' }), 'refuse');
  });
  ok('pull_request 以外のイベントは refuse', () => assert.strictEqual(d({ eventName: 'pull_request_target' }), 'refuse'));
  ok('changedFiles: SHA の形でない値は git へ渡さない', () => {
    assert.strictEqual(changedFiles('HEAD;touch x', 'a'.repeat(40)), null);
    assert.strictEqual(changedFiles('a'.repeat(40), '$(id)'), null);
  });
  console.log(`[pr-trust-gate] 自己試験 ${n} 件 OK。`);
}

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }
  const e = process.env;
  const files = changedFiles(e.BASE_SHA, e.HEAD_SHA);
  const r = decide({ eventName: e.EVENT_NAME, repository: e.REPOSITORY, headRepo: e.HEAD_REPO, headRef: e.HEAD_REF, author: e.PR_AUTHOR, association: e.PR_ASSOCIATION, files });
  console.log(`[pr-trust-gate] ${r.decision}: ${r.reason}`);
  if (e.GITHUB_OUTPUT) fs.appendFileSync(e.GITHUB_OUTPUT, `run=${r.decision === 'run'}\n`);
  if (r.decision === 'refuse') {
    console.log('::error::この PR では AI レビューを自動では走らせない。書き込み権限を持つ者が差分を読み、本リポジトリのブランチから出し直すこと。');
    process.exit(1);
  }
}

if (require.main === module) main();

module.exports = { decide, changedFiles, GENERATED };
