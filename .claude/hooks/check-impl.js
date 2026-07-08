#!/usr/bin/env node
/*
 * PostToolUse(Edit|Write) フック: 実装規約チェック（警告のみ・ブロックしない）。
 * - docs 以外のソースを編集したのに作業仕様書（docs/specs/）が無い場合に警告。
 * - docs 配下の .md でフロントマター（先頭 ---）が無い場合に警告。
 * エラー時も常に exit 0。依存ゼロ・安全弁つき。
 */
'use strict';
const fs = require('fs');

const CODE_EXT = /\.(c|cc|cpp|h|hpp|cs|java|kt|go|rs|rb|php|py|js|jsx|ts|tsx|vue|swift|scala|sql)$/i;

function hasWorkSpec() {
  try {
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
