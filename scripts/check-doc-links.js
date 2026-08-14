#!/usr/bin/env node
'use strict';
/*
 * check-doc-links.js
 * docs/ 配下の Markdown 仕様書に含まれる相対リンクの実在を検査する（リンク切れ再発防止）。
 * 検査対象:
 *   - フロントマター（先頭 --- ... ---）のリスト項目パス（plan_refs / related_specs / related など）
 *   - 本文の Markdown リンク [text](path)（`/` を含まない同一ディレクトリ内の裸ファイル名も含む。#399）
 *   - 本文のインラインコード内の相対パス表記 `../path.ext`
 * 対象外（誤検知回避）:
 *   - 外部 URL（http/https/mailto ほかスキーム付き）・アンカー(#...)・ルート絶対パス(/...)
 *   - テンプレ変数（${...} / {{...}} / <...>）
 *   - planning/ サブモジュール未チェックアウト時の planning/ 配下リンク
 *   - 拡張子（LINK_EXT）を持たない裸の語（本文中の README 等をリンク扱いしない）
 *   - インラインコード内の裸ファイル名（第 3 経路は ./ ../ 始まりのみを拾う）
 * 外部依存ゼロ（Node 標準モジュールのみ）。破損リンクがあれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-doc-links.js [--dir docs]
 *   node scripts/check-doc-links.js --self-test  # 検査ロジック自体の自己試験。
 *   # CI 例: - run: node scripts/check-doc-links.js
 */
const fs = require('fs');
const path = require('path');
const { notice } = require('./lib/ci-annotate.js');

// 既定はリポジトリルート。テストで未 populate 状態を再現するため DOC_LINKS_ROOT で上書き可能にする。
const REPO_ROOT = process.env.DOC_LINKS_ROOT
  ? path.resolve(process.env.DOC_LINKS_ROOT)
  : path.resolve(__dirname, '..');

// 参照として実在検査を行う拡張子（仕様書・図・スキーマ・**コードファイル**）。
// コード拡張子（js/ts/cs/csproj/props/slnx/sh ほか）が抜けていた間、仕様書からコードへの
// live link は一切検査されず、破損したまま「OK: 384 件」と報告された（MSP#470 / planning#167。
// 検査器を作る PR が、検査器の穴で自分の参照切れを見逃した型）。
// `txt` / `log` / `lock` 等の汎用拡張子は誤検知リスクのため**意図的に対象外**とし、その方針を
// 下の自己試験（--self-test）で固定してある。スタック固有分（cs/csproj/props/targets/slnx は
// .NET、ts/tsx は TS）もキット既定に含める（在っても他スタックで誤検知しない拡張子のみ）。
// 増減するときは self-test の正例・負例を必ず対で更新すること。
const LINK_EXT = /\.(md|ya?ml|json|puml|mmd|png|jpe?g|svg|drawio|js|mjs|cjs|ts|tsx|cs|csproj|props|targets|slnx|sh)$/i;

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

// 解決済み絶対パスが未 populate（空プレースホルダ）な submodule 配下にあれば、その submodule の
// パスを返す（無ければ null）。トークン不要の PR CI は submodule を populate しないため、
// その配下のリンクは検査対象外にする（populate 済みなら通常どおり実在検査する）。
// かつては `planning/` 固定で判定していたが、それでは planning 以外の submodule
// （ユニットを submodule で取り込む構成等）配下のリンクが PR CI で破損と誤検知された。
// .gitmodules 由来の一般則へ拡張してある。
//
// 真偽値ではなく**対象を返す**のは、どの submodule を何件飛ばしたかを報告するためである。
// 黙って除外すると「検査していない範囲があること」が出力から読み取れない（issue #139）。
function unpopulatedSubmoduleOf(resolvedAbs, root = REPO_ROOT) {
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
      if (!populated) return sub;
    }
  }
  return null;
}

function underUnpopulatedSubmodule(resolvedAbs, root = REPO_ROOT) {
  return unpopulatedSubmoduleOf(resolvedAbs, root) !== null;
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
function isBrokenRef(ref, baseDir, onSkip) {
  if (!ref) return false;
  let t = String(ref).trim().replace(/^["'`]|["'`]$/g, '').trim();
  if (!t) return false;
  if (/^(https?:|mailto:|#|\/|[a-z]+:\/\/)/i.test(t)) return false; // 外部/アンカー/絶対
  if (t.startsWith('<') || t.includes('${') || t.includes('{{')) return false; // テンプレ変数
  t = t.split('#')[0].split('?')[0].trim();
  if (!t) return false;
  // NFR (#399): `/` を含まない同一ディレクトリ内の裸ファイル名（例: `IADR-0119_xxx.md`）も対象にする。
  // docs/adr/ の IADR 相互参照はこの形で書くのが通例であり、**最も壊れやすい箇所がまるごと検査対象外**
  // だった（実在しないファイルへのリンクが CI で `OK` を返し続けた。発見は PR #395 の AI レビュー）。
  // 拡張子（LINK_EXT）を要求するのは、本文中の普通の語（`README` 等）をリンク扱いしないため。
  // `!t.includes('/')` を明示して従来の節と互いに素にしてある（何が新たに対象へ入ったかを読めるように）。
  //
  // **本節はキットに無い（本リポが進んでいる分）。** 環流するまで分類 B である（#494）。
  // **キット版で上書きすると本節が消える** —— 追随のたびにマージが要ることを分類表へ書いてある。
  const bareFileName = !t.includes('/') && LINK_EXT.test(t);
  const looksRelative =
    t.startsWith('./') || t.startsWith('../') || (t.includes('/') && !t.startsWith('/')) || bareFileName;
  if (!looksRelative) return false;
  if (!LINK_EXT.test(t)) return false;
  const resolved = path.resolve(baseDir, t);
  // 未チェックアウトの submodule 配下は検査しない。CI の actions/checkout（サブモジュール
  // 取得なし）は submodule を「空のプレースホルダディレクトリ」として作るため、存在チェック
  // だけでは未チェックアウトを判別できない。中身が空（＝未 populate）なら対象外とする。
  const skippedSub = unpopulatedSubmoduleOf(resolved);
  if (skippedSub) {
    // 除外したことを呼び出し側へ知らせる。件数を報告しないと「破損リンクはありません」が
    // 検査していない範囲まで含んだ断定になる（issue #139）。
    if (onSkip) onSkip(skippedSub);
    return false;
  }
  try { return !fs.existsSync(resolved); } catch (e) { return false; }
}

// 1ファイルの破損リンクを収集。
function collectBroken(fp, onSkip) {
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
      if (LINK_EXT.test(val) && isBrokenRef(val, baseDir, onSkip)) broken.add(val);
    }
  }
  // 2) 本文の Markdown リンク [text](path)
  const linkRe = /\]\(([^)]+)\)/g;
  while ((m = linkRe.exec(content))) {
    if (isBrokenRef(m[1], baseDir, onSkip)) broken.add(m[1].trim());
  }
  // 3) 本文のインラインコード内の相対パス `./ ../`
  const codeRe = /`([^`]+)`/g;
  while ((m = codeRe.exec(content))) {
    const v = m[1].trim();
    if ((v.startsWith('./') || v.startsWith('../')) && LINK_EXT.test(v) && isBrokenRef(v, baseDir, onSkip)) broken.add(v);
  }
  return Array.from(broken);
}

// --- 自己試験 -------------------------------------------------------------------
//
// 検査対象の拡張子を広げるたび、正例（実在 → OK）と負例（不在 → 検出）を対で足す。
// 「検査しているつもりで何も見ていない」状態（planning#167）を回帰させないための最小の歯止め。

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });
  const os = require('os');

  // LINK_EXT: 既存の対象（仕様書・図・スキーマ）は従来どおり。
  t('LINK_EXT: .md / .yaml / .json / .svg は対象', ['a.md', 'a.yaml', 'a.yml', 'a.json', 'a.svg']
    .every((x) => LINK_EXT.test(x)));
  // LINK_EXT: コードファイル（MSP#470 / planning#167 で追加）。
  for (const ext of ['js', 'mjs', 'cjs', 'ts', 'tsx', 'cs', 'csproj', 'props', 'targets', 'slnx', 'sh']) {
    t(`LINK_EXT: .${ext} は対象（planning#167）`, LINK_EXT.test(`a.${ext}`));
  }
  t('LINK_EXT: 対象外の拡張子は素通し（誤検知しない）',
    !LINK_EXT.test('a.txt') && !LINK_EXT.test('a.tsv') && !LINK_EXT.test('a'));

  // isBrokenRef の正例／負例。baseDir は scripts/ 自身（実在する .js が確実にある）。
  const here = __dirname;
  t('正例: 実在する .js への相対リンクは破損でない',
    isBrokenRef('./check-doc-links.js', here) === false);
  t('正例: 一段上がる .js リンクも解決する',
    isBrokenRef('../scripts/check-doc-links.js', here) === false);
  t('負例: 実在しない .js への相対リンクは破損として検出する',
    isBrokenRef('./__no_such_script__.js', here) === true);
  for (const ext of ['mjs', 'cjs', 'ts', 'tsx', 'cs', 'csproj', 'props', 'targets', 'slnx', 'sh']) {
    t(`負例: 実在しない .${ext} も検出する`, isBrokenRef(`./__no_such__.${ext}`, here) === true);
  }
  t('対象外: 拡張子が対象外なら実在しなくても検出しない',
    isBrokenRef('./__no_such__.txt', here) === false);
  t('対象外: 外部 URL・アンカー・ルート絶対パスは検出しない',
    ['https://example.com/a.js', '#section', '/etc/a.js'].every((x) => isBrokenRef(x, here) === false));
  t('対象外: テンプレ変数を含む表記は検出しない',
    isBrokenRef('${DIR}/a.js', here) === false && isBrokenRef('<path>/a.js', here) === false);
  t('アンカー・クエリ付きでも本体パスで判定する',
    isBrokenRef('./check-doc-links.js#L30', here) === false
      && isBrokenRef('./__no_such_script__.js#L1', here) === true);

  // collectBroken: Markdown リンク／インラインコード／フロントマターの 3 経路で .js を拾う。
  {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'doclinks-selftest-'));
    const okJs = path.join(dir, 'real.js');
    fs.writeFileSync(okJs, '// fixture\n');
    const md = path.join(dir, 'a.md');
    fs.writeFileSync(
      md,
      '---\nrelated_specs:\n  - ./real.js\n  - ./fm-missing.js\n---\n\n' +
        '# A\n\n[ok](./real.js) と [ng](./missing.js)。\n\n' +
        'インラインコードの `./inline-missing.js` も拾う。\n'
    );
    const broken = collectBroken(md).sort();
    t('collectBroken: 実在する .js リンクは報告しない（正例）', !broken.includes('./real.js'), broken);
    t('collectBroken: 本文の .js リンク切れを検出（負例）', broken.includes('./missing.js'), broken);
    t('collectBroken: フロントマターの .js も検出', broken.includes('./fm-missing.js'), broken);
    t('collectBroken: インラインコードの .js も検出', broken.includes('./inline-missing.js'), broken);
    fs.rmSync(dir, { recursive: true, force: true });
  }

  // --- #399: 同一ディレクトリ内の裸ファイル名（本リポ固有。キットに無い） ---------------
  //
  // **キットの自己試験はこの機能を知らない。** 本リポが進んでいる分は、こちらで回帰を固定する
  // （固定しないと、次のキット追随で黙って消える。追跡: #494）。
  {
    const dir = fs.mkdtempSync(path.join(require('os').tmpdir(), 'doclinks-bare-'));
    fs.writeFileSync(path.join(dir, 'IADR-0001_real.md'), '# real\n');
    const md = path.join(dir, 'a.md');
    fs.writeFileSync(
      md,
      '# A\n\n[実在](IADR-0001_real.md) と [不在](IADR-9999_missing.md)。\n\n' +
        '本文中の README や AGENTS という語はリンクではない。\n',
    );
    const broken = collectBroken(md).sort();
    t('#399 正例: 実在する裸ファイル名は報告しない', !broken.includes('IADR-0001_real.md'), broken);
    t('#399 負例: 実在しない裸ファイル名を検出する', broken.includes('IADR-9999_missing.md'), broken);
    t('#399 対象外: 拡張子の無い裸の語はリンク扱いしない',
      !broken.some((b) => b === 'README' || b === 'AGENTS'), broken);
    fs.rmSync(dir, { recursive: true, force: true });
  }

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed++; if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) {
    console.error(`[check-doc-links] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-doc-links] 自己試験 ${cases.length} 件 OK。`);
}

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }
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
  // 未 populate な submodule 配下として除外したリンクを submodule 別に数える。
  const skipped = new Map();
  const onSkip = (sub) => skipped.set(sub, (skipped.get(sub) || 0) + 1);
  for (const fp of files) {
    const b = collectBroken(fp, onSkip);
    if (b.length) {
      total += b.length;
      report.push({ fp, links: b });
    }
  }

  // 検査対象外にした範囲を必ず知らせる（issue #139）。
  // これを黙っていると「破損した相対リンクはありません」が、実際には検査していない範囲まで
  // 含んだ断定になる。実際に ai-stock-trading では PR CI が planning 配下 753 件を毎回飛ばし、
  // その隙間で破損 20 件が蓄積した（夜間の doc-links-planning は PR に紐づかず、
  // PLANNING_REPO_TOKEN 未登録なら動かない）。
  const skippedTotal = [...skipped.values()].reduce((n, v) => n + v, 0);
  let skipNote = '';
  if (skippedTotal > 0) {
    const detail = [...skipped.entries()].map(([sub, n]) => `${sub}: ${n} 件`).join(', ');
    skipNote = `（未 populate の submodule 配下 ${skippedTotal} 件は対象外 — ${detail}）`;
    notice(
      `未 populate の submodule 配下 ${skippedTotal} 件のリンクを検査対象外にした（${detail}）。` +
        'この範囲は本実行では検査されていない。PR 段階で検査するには checkout に ' +
        'submodules とトークンを付けるか、定期ジョブ（doc-links-planning）の結果を確認すること'
    );
  }

  if (total === 0) {
    console.log(
      `[check-doc-links] OK: ${files.length} 件の Markdown に破損した相対リンクはありません${skipNote}。`
    );
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
  unpopulatedSubmoduleOf,
  isBrokenRef,
  collectBroken,
  selfTest,
  LINK_EXT,
};
