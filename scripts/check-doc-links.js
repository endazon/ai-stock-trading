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

// 既定はリポジトリルート。テストで未 populate 状態を再現するため DOC_LINKS_ROOT で上書き可能にする。
const REPO_ROOT = process.env.DOC_LINKS_ROOT
  ? path.resolve(process.env.DOC_LINKS_ROOT)
  : path.resolve(__dirname, '..');

// 参照として実在検査を行う拡張子（仕様書・図・スキーマ等）
const LINK_EXT = /\.(md|ya?ml|json|puml|mmd|png|jpe?g|svg|drawio)$/i;

function parseArgs(argv) {
  const a = { dir: 'docs', requirePlanning: false };
  for (let i = 0; i < argv.length; i++) {
    const x = argv[i];
    if (x === '--dir') a.dir = argv[++i];
    else if (x.startsWith('--dir=')) a.dir = x.slice(6);
    // --require-planning: planning サブモジュールが未チェックアウトなら fail する（microservices-platform #232 と同根）。
    // トークン付きで submodule を取得する定期ジョブから使い、取得漏れ（＝planning リンクの検査漏れ）を
    // 黙って通さず可視化する。
    else if (x === '--require-planning') a.requirePlanning = true;
  }
  return a;
}

// planning サブモジュールが populate 済みか（projects/ の実在で判定）。CI が submodule なしで
// checkout した場合は planning/ が空プレースホルダになるため、存在チェックだけでは判別できない。
// `--require-planning` 用の判定であり、リンク検査の対象外判定は下の一般則を使う。
function planningPopulated(root = REPO_ROOT) {
  try {
    return fs.existsSync(path.join(root, 'planning', 'projects'));
  } catch (e) {
    return false;
  }
}

// .gitmodules から submodule の path 一覧を得る。
function submodulePaths(root = REPO_ROOT) {
  try {
    const txt = fs.readFileSync(path.join(root, '.gitmodules'), 'utf8');
    const out = [];
    const re = /^\s*path\s*=\s*(.+?)\s*$/gm;
    let m;
    while ((m = re.exec(txt))) out.push(m[1].replace(/\\/g, '/'));
    return out;
  } catch (e) {
    return [];
  }
}

// 解決済み絶対パスが、未 populate（空プレースホルダ）な submodule 配下にあるか。
// トークン不要の PR CI は submodule を populate しないため、その配下のリンクは検査対象外にする
// （populate 済みなら通常どおり実在検査する）。
// かつては `planning/` 固定で判定していたが、それでは planning 以外の submodule
// （ユニットを submodule で取り込む構成等）配下のリンクが PR CI で破損と誤検知された。
// .gitmodules 由来の一般則へ拡張してある。
function underUnpopulatedSubmodule(resolvedAbs, root = REPO_ROOT) {
  const rel = path.relative(root, resolvedAbs).replace(/\\/g, '/');
  for (const sub of submodulePaths(root)) {
    if (rel === sub || rel.startsWith(sub + '/')) {
      const subAbs = path.join(root, sub);
      let populated = false;
      try {
        populated = fs.existsSync(subAbs) && fs.readdirSync(subAbs).length > 0;
      } catch (e) {
        populated = false;
      }
      if (!populated) return true;
    }
  }
  return false;
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
  const resolved = path.resolve(baseDir, t);
  // 未チェックアウトの submodule 配下は検査しない。CI の actions/checkout（サブモジュール
  // 取得なし）は submodule を「空のプレースホルダディレクトリ」として作るため、存在チェック
  // だけでは未チェックアウトを判別できない。中身が空（＝未 populate）なら対象外とする。
  if (underUnpopulatedSubmodule(resolved)) return false;
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
  // 定期ジョブでは planning が populate されている前提を検証する。未 populate なら
  // planning リンクは（isBrokenRef により）検査対象外となり破損を見逃すため、ここで明示的に fail する。
  if (a.requirePlanning && !planningPopulated()) {
    console.error(
      '[check-doc-links] --require-planning: planning サブモジュールが未チェックアウトです。\n' +
        '  submodules を取得（例: actions/checkout の submodules: recursive + PLANNING_REPO_TOKEN）してから実行してください。',
    );
    process.exit(1);
  }
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

if (require.main === module) main();

module.exports = {
  parseArgs,
  planningPopulated,
  submodulePaths,
  underUnpopulatedSubmodule,
  isBrokenRef,
  collectBroken,
};
