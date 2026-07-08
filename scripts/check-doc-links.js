#!/usr/bin/env node
'use strict';
/*
 * check-doc-links.js
 * docs/ 配下の Markdown 仕様書に含まれる相対リンクの実在を検査する（リンク切れ再発防止）。
 * 検査対象:
 *   - フロントマター（先頭 --- ... ---）のリスト項目パス（plan_refs / related_specs / related など）
 *   - 本文の Markdown リンク [text](path)
 *   - 本文のインラインコード内の相対パス表記 `../path.ext`
 * 対象外（誤検知回避）:
 *   - 外部 URL（http/https/mailto ほかスキーム付き）・アンカー(#...)・ルート絶対パス(/...)
 *   - テンプレ変数（${...} / {{...}} / <...>）
 *   - planning/ サブモジュール未チェックアウト時の planning/ 配下リンク
 * 外部依存ゼロ（Node 標準モジュールのみ）。破損リンクがあれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-doc-links.js [--dir docs]
 *   # CI 例: - run: node scripts/check-doc-links.js
 */
const fs = require('fs');
const path = require('path');

// 参照として実在検査を行う拡張子（仕様書・図・スキーマ等）
const LINK_EXT = /\.(md|ya?ml|json|puml|mmd|png|jpe?g|svg|drawio)$/i;

function parseArgs(argv) {
  const a = { dir: 'docs' };
  for (let i = 0; i < argv.length; i++) {
    const x = argv[i];
    if (x === '--dir') a.dir = argv[++i];
    else if (x.startsWith('--dir=')) a.dir = x.slice(6);
  }
  return a;
}

function mdFiles(dir) {
  let out = [];
  let ents;
  try { ents = fs.readdirSync(dir, { withFileTypes: true }); } catch (e) { return out; }
  for (const ent of ents) {
    const p = path.join(dir, ent.name);
    if (ent.isDirectory()) out = out.concat(mdFiles(p));
    else if (ent.isFile() && ent.name.endsWith('.md')) out.push(p);
  }
  return out;
}

// 相対リンク候補を1つ検査。実在しなければ true（＝リンク切れ）。判定不能・対象外は false。
function isBrokenRef(ref, baseDir) {
  if (!ref) return false;
  let t = String(ref).trim().replace(/^["'`]|["'`]$/g, '').trim();
  if (!t) return false;
  if (/^(https?:|mailto:|#|\/|[a-z]+:\/\/)/i.test(t)) return false; // 外部/アンカー/絶対
  if (t.startsWith('<') || t.includes('${') || t.includes('{{')) return false; // テンプレ変数
  t = t.split('#')[0].split('?')[0].trim();
  if (!t) return false;
  const looksRelative = t.startsWith('./') || t.startsWith('../') || (t.includes('/') && !t.startsWith('/'));
  if (!looksRelative) return false;
  if (!LINK_EXT.test(t)) return false;
  // planning/ サブモジュール未チェックアウト時は planning 配下リンクを検査しない。
  // CI の actions/checkout（サブモジュール取得なし）は planning/ を「空のプレースホルダ
  // ディレクトリ」として作るため、存在チェックだけでは未チェックアウトを判別できない。
  // 中身が空（＝未 populate）の場合も検査対象外とする。
  if (/(^|\/)planning\//.test(t)) {
    const idx = t.indexOf('planning/') + 'planning'.length;
    const subRoot = path.resolve(baseDir, t.slice(0, idx));
    let populated = false;
    try { populated = fs.existsSync(subRoot) && fs.readdirSync(subRoot).length > 0; } catch (e) { populated = false; }
    if (!populated) return false;
  }
  const resolved = path.resolve(baseDir, t);
  try { return !fs.existsSync(resolved); } catch (e) { return false; }
}

// 1ファイルの破損リンクを収集。
function collectBroken(fp) {
  let content = '';
  try { content = fs.readFileSync(fp, 'utf8'); } catch (e) { return []; }
  const baseDir = path.dirname(fp);
  const broken = new Set();
  let m;
  // 1) フロントマターのリスト項目パス
  const fm = content.match(/^---\n([\s\S]*?)\n---/);
  if (fm) {
    const re = /^\s*-\s*(.+)$/gm;
    while ((m = re.exec(fm[1]))) {
      // 引用符（"..." / '...' / `...`）を外し、末尾の注記（例: 「... .md (FR-01)」）も除去してから判定する
      const val = m[1].trim()
        .replace(/^["'`]|["'`]$/g, '').trim()
        .replace(/\s*\([^)]*\)\s*$/, '').trim();
      if (LINK_EXT.test(val) && isBrokenRef(val, baseDir)) broken.add(val);
    }
  }
  // 2) 本文の Markdown リンク [text](path)
  const linkRe = /\]\(([^)]+)\)/g;
  while ((m = linkRe.exec(content))) {
    if (isBrokenRef(m[1], baseDir)) broken.add(m[1].trim());
  }
  // 3) 本文のインラインコード内の相対パス `./ ../`
  const codeRe = /`([^`]+)`/g;
  while ((m = codeRe.exec(content))) {
    const v = m[1].trim();
    if ((v.startsWith('./') || v.startsWith('../')) && LINK_EXT.test(v) && isBrokenRef(v, baseDir)) broken.add(v);
  }
  return Array.from(broken);
}

function main() {
  const a = parseArgs(process.argv.slice(2));
  const files = mdFiles(a.dir);
  let total = 0;
  const report = [];
  for (const fp of files) {
    const b = collectBroken(fp);
    if (b.length) {
      total += b.length;
      report.push({ fp, links: b });
    }
  }
  if (total === 0) {
    console.log(`[check-doc-links] OK: ${files.length} 件の Markdown に破損した相対リンクはありません。`);
    process.exit(0);
  }
  console.error(`[check-doc-links] 破損リンク ${total} 件を検出しました:`);
  for (const r of report) {
    console.error(`\n  ${r.fp}`);
    for (const l of r.links) console.error(`    - ${l}`);
  }
  console.error('\n相対パスの綴り・階層（例: docs/functional/ からは ../../planning/... ）を確認してください。');
  process.exit(1);
}

main();
