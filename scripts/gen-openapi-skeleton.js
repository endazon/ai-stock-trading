#!/usr/bin/env node
'use strict';
/*
 * gen-openapi-skeleton.js
 * 通信仕様書（docs/api/*.md の「エンドポイント一覧」表）から OpenAPI(3.0) の雛形を生成する。
 * 外部依存ゼロ（Node 標準モジュールのみ・YAML は手組み）。コードからの本生成が未整備な段階の足場。
 *
 * 使い方:
 *   node scripts/gen-openapi-skeleton.js [--src docs/api] [--out docs/api/openapi.yaml] [--force]
 *   既定 --src docs/api、--out docs/api/openapi.yaml。--force 無しで既存があれば上書きしない。
 */
const fs = require('fs');
const path = require('path');

function parseArgs(argv) {
  const a = { src: 'docs/api', out: 'docs/api/openapi.yaml', force: false };
  for (let i = 0; i < argv.length; i++) {
    const x = argv[i];
    if (x === '--src') a.src = argv[++i];
    else if (x === '--out') a.out = argv[++i];
    else if (x === '--force') a.force = true;
    else if (x.startsWith('--src=')) a.src = x.slice(6);
    else if (x.startsWith('--out=')) a.out = x.slice(6);
  }
  return a;
}

function mdFiles(dir) {
  let out = [];
  let ents;
  try { ents = fs.readdirSync(dir, { withFileTypes: true }); } catch (e) { return out; }
  for (const e of ents) {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) out = out.concat(mdFiles(full));
    else if (e.isFile() && e.name.endsWith('.md')) out.push(full);
  }
  return out;
}

function splitRow(line) {
  let s = line.trim();
  if (s.startsWith('|')) s = s.slice(1);
  if (s.endsWith('|')) s = s.slice(0, -1);
  return s.split('|').map((c) => c.trim());
}
function isRow(l) { return l.trim().startsWith('|') && l.includes('|'); }
function isSep(l) { return /^\s*\|?[\s:|-]+\|?\s*$/.test(l) && l.includes('-'); }

/** エンドポイント一覧表（ヘッダにメソッド・パスを含む）から {method, path, summary} を集める */
function extractEndpoints(body) {
  const lines = body.split('\n');
  const eps = [];
  for (let i = 0; i < lines.length; i++) {
    if (isRow(lines[i]) && i + 1 < lines.length && isSep(lines[i + 1])) {
      const header = splitRow(lines[i]);
      const mi = header.findIndex((h) => /メソッド|method/i.test(h));
      const pi = header.findIndex((h) => /パス|path/i.test(h));
      const si = header.findIndex((h) => /概要|summary|説明/i.test(h));
      let j = i + 2;
      if (mi !== -1 && pi !== -1) {
        for (; j < lines.length && isRow(lines[j]); j++) {
          const row = splitRow(lines[j]);
          const method = (row[mi] || '').toLowerCase().replace(/[^a-z]/g, '');
          const p = (row[pi] || '').trim();
          const summary = si !== -1 ? (row[si] || '').trim() : '';
          if (method && p.startsWith('/')) eps.push({ method, path: p, summary });
        }
      }
      i = j;
    }
  }
  return eps;
}

function yamlEscape(s) {
  const v = String(s || '');
  return /[:#{}\[\],&*?|<>=!%@`"']/.test(v) ? JSON.stringify(v) : v;
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  const files = mdFiles(args.src);
  const byPath = new Map();
  for (const f of files) {
    if (path.basename(f).startsWith('openapi')) continue;
    const body = fs.readFileSync(f, 'utf8');
    for (const ep of extractEndpoints(body)) {
      if (!byPath.has(ep.path)) byPath.set(ep.path, []);
      byPath.get(ep.path).push(ep);
    }
  }

  const title = path.basename(process.cwd()) + ' API';
  const out = [];
  out.push('openapi: 3.0.3');
  out.push('info:');
  out.push(`  title: ${yamlEscape(title)}`);
  out.push('  version: 0.0.0');
  out.push('  description: 通信仕様書（docs/api/）から自動生成した雛形。詳細はコードからの生成または手動で補完する。');
  out.push('paths:');
  if (byPath.size === 0) {
    out.push('  # 通信仕様書のエンドポイント一覧表が見つからなかった。docs/api/ に通信仕様書を作成すること。');
    out.push('  {}');
  } else {
    for (const [p, eps] of byPath) {
      out.push(`  ${yamlEscape(p)}:`);
      const seen = new Set();
      for (const ep of eps) {
        if (seen.has(ep.method)) continue;
        seen.add(ep.method);
        out.push(`    ${ep.method}:`);
        out.push(`      summary: ${yamlEscape(ep.summary || '')}`);
        out.push('      responses:');
        out.push("        '200':");
        out.push('          description: OK');
      }
    }
  }
  const content = out.join('\n') + '\n';

  if (!args.force && fs.existsSync(args.out)) {
    process.stderr.write(`既存のため上書きしない（--force で上書き）: ${args.out}\n`);
    process.stdout.write(content);
    return;
  }
  fs.mkdirSync(path.dirname(path.resolve(args.out)), { recursive: true });
  fs.writeFileSync(args.out, content, 'utf8');
  process.stderr.write(`出力しました: ${args.out}（${byPath.size} パス）\n`);
}

main();
