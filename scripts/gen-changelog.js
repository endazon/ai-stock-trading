#!/usr/bin/env node
'use strict';
/*
 * gen-changelog.js
 * git のコミット履歴（Conventional Commits 風 `種別(起点ID): 要約`）から CHANGELOG.md を生成する。
 * 外部依存ゼロ（Node 標準モジュールのみ）。タグがあればタグ単位、無ければ Unreleased にまとめる。
 *
 * 使い方:
 *   node scripts/gen-changelog.js [--out CHANGELOG.md]
 *   --out 省略時は標準出力。
 */
const { execSync } = require('child_process');
const fs = require('fs');

const TYPE_LABEL = {
  feat: '新機能',
  fix: '不具合修正',
  perf: '性能',
  refactor: 'リファクタ',
  docs: 'ドキュメント',
  test: 'テスト',
  build: 'ビルド',
  ci: 'CI',
  style: 'スタイル',
  chore: 'その他',
};
const TYPE_ORDER = ['feat', 'fix', 'perf', 'refactor', 'docs', 'test', 'build', 'ci', 'style', 'chore'];

function git(args) {
  return execSync(`git ${args}`, { encoding: 'utf8' }).trim();
}

function isGitRepo() {
  try { git('rev-parse --is-inside-work-tree'); return true; } catch (e) { return false; }
}

function parseArgs(argv) {
  const a = { out: null };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--out') a.out = argv[++i];
    else if (argv[i].startsWith('--out=')) a.out = argv[i].slice(6);
  }
  return a;
}

/** タグを作成日時の降順で返す */
function tags() {
  try {
    const out = git('tag --sort=-creatordate');
    return out ? out.split('\n').filter(Boolean) : [];
  } catch (e) { return []; }
}

/** 範囲のコミットを {type, scope, desc, hash} で返す */
function commits(range) {
  let raw = '';
  try {
    raw = execSync(`git log ${range} --no-merges --pretty=format:%h%x1f%s`, { encoding: 'utf8' });
  } catch (e) { return []; }
  if (!raw.trim()) return [];
  return raw.split('\n').map((line) => {
    const [hash, subject = ''] = line.split('\x1f');
    const m = subject.match(/^(\w+)(?:\(([^)]*)\))?(!)?:\s*(.+)$/);
    if (m) return { hash, type: m[1].toLowerCase(), scope: m[2] || '', desc: m[4] };
    return { hash, type: 'other', scope: '', desc: subject };
  });
}

function renderSection(title, list) {
  const lines = [`## ${title}`, ''];
  const byType = new Map();
  for (const c of list) {
    const key = TYPE_LABEL[c.type] ? c.type : 'chore';
    if (!byType.has(key)) byType.set(key, []);
    byType.get(key).push(c);
  }
  let any = false;
  for (const t of TYPE_ORDER) {
    const items = byType.get(t);
    if (!items || !items.length) continue;
    any = true;
    lines.push(`### ${TYPE_LABEL[t]}`, '');
    for (const c of items) {
      const scope = c.scope ? `**${c.scope}**: ` : '';
      lines.push(`- ${scope}${c.desc} (${c.hash})`);
    }
    lines.push('');
  }
  if (!any) lines.push('- （変更なし）', '');
  return lines.join('\n');
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  if (!isGitRepo()) {
    process.stderr.write('git リポジトリではないため CHANGELOG を生成できない。\n');
    process.exit(0);
  }
  // 注: 生成日時などの可変値は出力に含めない（内容無変更なら差分ゼロとし、CI の無限自動コミットを防ぐ）。
  const out = ['# 変更履歴 (CHANGELOG)', ''];
  const tagList = tags();

  // Unreleased（最新タグ以降）
  const unreleasedRange = tagList.length ? `${tagList[0]}..HEAD` : 'HEAD';
  const unreleased = commits(unreleasedRange);
  if (unreleased.length || !tagList.length) {
    out.push(renderSection('Unreleased', unreleased));
  }

  // 各タグ
  for (let i = 0; i < tagList.length; i++) {
    const range = i + 1 < tagList.length ? `${tagList[i + 1]}..${tagList[i]}` : tagList[i];
    let date = '';
    try { date = git(`log -1 --pretty=format:%ad --date=short ${tagList[i]}`); } catch (e) { /* noop */ }
    out.push(renderSection(`${tagList[i]}${date ? ` - ${date}` : ''}`, commits(range)));
  }

  const content = out.join('\n').replace(/\n{3,}/g, '\n\n').replace(/\s+$/, '') + '\n';
  if (args.out) {
    fs.writeFileSync(args.out, content, 'utf8');
    process.stderr.write(`出力しました: ${args.out}\n`);
  } else {
    process.stdout.write(content);
  }
}

main();
