#!/usr/bin/env node
/*
 * PostToolUse(Edit|Write) フック: 実装規約チェック（警告のみ・ブロックしない）。
 * - docs 以外のソースを編集したのに作業仕様書（docs/specs/）が無い場合に警告。
 * - docs 配下の .md でフロントマター（先頭 ---）が無い場合に警告。
 * エラー時も常に exit 0。依存ゼロ・安全弁つき。
 */
'use strict';
const fs = require('fs');
const { execSync } = require('child_process');

const CODE_EXT = /\.(c|cc|cpp|h|hpp|cs|java|kt|go|rs|rb|php|py|js|jsx|ts|tsx|vue|swift|scala|sql)$/i;

// 統合ブランチの候補（先に解決できたものを基準にする）。develop 運用・main 運用の双方に対応する。
const BASE_CANDIDATES = ['origin/develop', 'develop', 'origin/main', 'main'];

function tryGit(args) {
  try {
    // 最悪 6 回呼ばれる（rev-parse ×4 → diff → ls-files）ため、合計が全体の
    // 安全弁（5 秒）を超えないよう 1 回あたりを 0.8 秒に保つ（6 × 800ms = 4800ms < 5000ms）。
    return execSync(`git ${args}`, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'], timeout: 800 }).trim();
  } catch (e) { return null; }
}

function hasWorkSpec() {
  try {
    // 作業仕様書は「作業/PR 単位・着手前に必須」（CLAUDE.md）。docs/specs/ に過去の仕様書が
    // 蓄積しても判定が形骸化しないよう、現在のブランチで追加された仕様書を数える
    // （統合ブランチとの差分＋未追跡の新規ファイル）。base を解決できない環境では
    // 「1 つでもあれば合格」の従来判定へ退避する（誤検知より見逃しを選ぶ・警告のみのため）。
    const base = BASE_CANDIDATES.find((r) => tryGit(`rev-parse --verify --quiet ${r}`) !== null);
    if (base) {
      const diffed = tryGit(`diff --name-only --diff-filter=A ${base} -- docs/specs`) || '';
      const untracked = tryGit('ls-files --others --exclude-standard -- docs/specs') || '';
      return `${diffed}\n${untracked}`.split('\n').some((f) => f.trim().endsWith('.md'));
    }
    const files = fs.readdirSync('docs/specs');
    return files.some((f) => f.endsWith('.md'));
  } catch (e) { return true; } // ディレクトリが無い等は判定しない（誤検知回避）
}

function run(raw) {
  let fp = '';
  try {
    const d = JSON.parse(raw || '{}');
    const ti = (d && d.tool_input) || {};
    fp = ti.file_path || ti.path || '';
  } catch (e) { return; }
  if (!fp) return;

  const norm = String(fp).replace(/\\/g, '/');
  const warns = [];

  // docs 配下の Markdown はフロントマターを確認
  if (/(^|\/)docs\//.test(norm) && norm.endsWith('.md')) {
    const base = norm.split('/').pop();
    if (base !== 'README.md' && base !== 'DEFINITION_OF_DONE.md' && base !== 'ai-workflow.md') {
      let content = '';
      try { content = fs.readFileSync(fp, 'utf8'); } catch (e) { /* noop */ }
      if (content && !content.startsWith('---')) {
        warns.push('docs 配下の仕様書に YAML フロントマターがありません。`docs/templates/` のひな形に従ってください。');
      }
    }
  }

  // ソース編集だが作業仕様書が無い
  const isDocs = /(^|\/)docs\//.test(norm);
  if (!isDocs && CODE_EXT.test(norm) && !hasWorkSpec()) {
    warns.push('作業仕様書（`docs/specs/`）が見つかりません。実装着手前に `/new-spec` で作業仕様書を作成してください（CLAUDE.md の最優先ルール）。');
  }

  if (warns.length) {
    process.stdout.write(JSON.stringify({ systemMessage: '【実装規約チェック】\n- ' + warns.join('\n- ') }));
  }
}

let chunks = [];
process.stdin.on('data', (c) => chunks.push(c));
process.stdin.on('end', () => {
  try { run(Buffer.concat(chunks).toString('utf8')); } catch (e) { /* noop */ }
  process.exit(0);
});
process.stdin.on('error', () => process.exit(0));
setTimeout(() => process.exit(0), 5000); // 安全弁
