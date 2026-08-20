#!/usr/bin/env node
'use strict';
/*
 * check-doc-links.js
 * docs/ ・ .ai-context/ 配下の Markdown 仕様書に含まれる相対リンクの実在を検査する
 * （リンク切れ再発防止）。
 * 検査対象:
 *   - フロントマター（先頭 --- ... ---）のリスト項目パス（plan_refs / related_specs / related など）
 *   - 本文の Markdown リンク [text](path)
 *   - 本文のインラインコード内の相対パス表記 `../path.ext`
 * 対象外（誤検知回避）:
 *   - 外部 URL（http/https/mailto ほかスキーム付き）・アンカー(#...)・ルート絶対パス(/...)
 *   - テンプレ変数（${...} / {{...}} / <...>）
 * 外部依存ゼロ（Node 標準モジュールのみ）。破損リンクがあれば終了コード 1。
 *
 * `.ai-context/` はドットで始まる隠しディレクトリだが、走査（`mdFiles`）は
 * `fs.readdirSync` の全エントリをそのまま辿るため、隠しディレクトリだからといって
 * 黙ってスキップされることはない（資料再編 ADR-0029 で `docs/adr` `docs/specs` が
 * `.ai-context/` へ移設されたため、既定の走査対象へ明示的に含めてある）。
 *
 * 資料再編（ADR-0029 決定 2）で本リポジトリは planning への依存を持たない。
 * かつて存在した「planning submodule 未チェックアウト時は当該配下のリンクを検査対象外に
 * する」分岐（`--require-planning` 含む）は、planning 自体が撤去されたため削除した。
 *
 * 使い方:
 *   node scripts/check-doc-links.js [--dir docs --dir .ai-context]
 *   node scripts/check-doc-links.js --self-test  # 検査ロジック自体の自己試験。
 *   # CI 例: - run: node scripts/check-doc-links.js
 */
const fs = require('fs');
const path = require('path');

// 既定はリポジトリルート。テストでルートを差し替えるため DOC_LINKS_ROOT で上書き可能にする。
const REPO_ROOT = process.env.DOC_LINKS_ROOT
  ? path.resolve(process.env.DOC_LINKS_ROOT)
  : path.resolve(__dirname, '..');

// 既定の走査対象ディレクトリ（人が読む生きた文書 docs/ ＋ AI 向け文脈資料・凍結記録 .ai-context/）。
const DEFAULT_DIRS = ['docs', '.ai-context'];

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
  const a = { dirs: [] };
  for (let i = 0; i < argv.length; i++) {
    const x = argv[i];
    if (x === '--dir') a.dirs.push(argv[++i]);
    else if (x.startsWith('--dir=')) a.dirs.push(x.slice(6));
  }
  if (a.dirs.length === 0) a.dirs = DEFAULT_DIRS.slice();
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
  // `planning:<repo相対パス>` は資料再編（ADR-0029 決定 3）で frontmatter `plan_refs` へ
  // 導入された非パス表記（migrate-ai-context.js (c)）である。パスではなく、計画リポジトリ内の
  // 位置を平文で示す注記のため、実在検査の対象外とする。
  if (/^planning:/.test(t)) return false;
  // `feedback:<basename>` は同じ資料再編で feedback/ が撤去された際、frontmatter の
  // plan_refs 以外のリスト項目（related_specs 等）に残っていた feedback/ 参照を
  // 同型の非パス表記へ揃えたもの（.ai-context/ 側の補完的な機械変換）。
  if (/^feedback:/.test(t)) return false;
  if (t.startsWith('<') || t.includes('${') || t.includes('{{')) return false; // テンプレ変数
  t = t.split('#')[0].split('?')[0].trim();
  if (!t) return false;
  // **同一ディレクトリのベアファイル名（`./` も `/` も無い形）も相対リンクである**（planning#337）。
  // かつては `./` `../` で始まるか `/` を含むものしか相対と見なさず、`IADR-0118_xxx.md` の形が
  // **一切検査されていなかった** —— 実在しないファイルを指すリンクを足しても
  // `OK: … 破損した相対リンクはありません` で緑になった。**`docs/adr/` の §関連 はほぼこの形で
  // 書かれる**ため、最も壊れやすい箇所がまるごと対象外だったことになる。
  // **2 つの実装リポジトリが独立に同じ穴を踏んで同じ修正へ至った**（endazon/ai-stock-trading#399 /
  // endazon/microservices-platform#609）。**実測件数はここに書かない** —— リンクを 1 本足しただけで
  // 黙って古くなるためである。
  //
  // 誤検出の抑えは `LINK_EXT`（直後）が担う —— 拡張子を持たない語（`README`）や
  // `Foo.Bar` のような識別子は `LINK_EXT` に掛からず、相対リンクとして扱われない。
  // `!t.includes('/')` を明示して従来の節と互いに素にしてある（何が新たに対象へ入ったかを読めるように）。
  const bareFileName = !t.includes('/') && LINK_EXT.test(t);
  const looksRelative =
    t.startsWith('./') || t.startsWith('../') || (t.includes('/') && !t.startsWith('/')) || bareFileName;
  if (!looksRelative) return false;
  if (!LINK_EXT.test(t)) return false;
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

  // --- 同一ディレクトリのベアファイル名（`./` も `/` も無い形。planning#337） ----------------
  //
  // **この対が無かったことが穴を長く開けたままにした直接の原因である。**
  // `docs/adr/` の §関連 はほぼこの形で書かれており、実データに多数あるが、`looksRelative` が
  // `/` の有無しか見ていなかったため**全件が無検査**だった。
  t('正例: 同一ディレクトリの実在ファイルをベア名で指しても破損でない',
    isBrokenRef('check-doc-links.js', here) === false);
  t('負例: 同一ディレクトリの不在ファイルをベア名で指すと検出する',
    isBrokenRef('__no_such_script__.js', here) === true);
  t('負例: .md も同じ（ADR の §関連 で実際に踏んだ型）',
    isBrokenRef('__no_such_adr__.md', here) === true);
  t('誤検出しない: 拡張子を持たない語はベア名でも相対リンクと見なさない',
    isBrokenRef('README', here) === false && isBrokenRef('IADR-0118', here) === false);
  t('誤検出しない: 対象外拡張子の識別子はベア名でも検出しない',
    isBrokenRef('Foo.Bar', here) === false && isBrokenRef('__no_such__.txt', here) === false);

  t('対象外: 外部 URL・アンカー・ルート絶対パスは検出しない',
    ['https://example.com/a.js', '#section', '/etc/a.js'].every((x) => isBrokenRef(x, here) === false));
  t('対象外: テンプレ変数を含む表記は検出しない',
    isBrokenRef('${DIR}/a.js', here) === false && isBrokenRef('<path>/a.js', here) === false);
  t('対象外: planning: 非パス表記（frontmatter plan_refs。ADR-0029 決定3）は検出しない',
    isBrokenRef('planning:projects/ai-stock-trading/07_adr/ADR-0001_x.md', here) === false);
  t('対象外: feedback: 非パス表記（frontmatter related_specs 等の補完的機械変換）は検出しない',
    isBrokenRef('feedback:20260804_fr20-stage1-session-calendar', here) === false);
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
  const files = a.dirs.flatMap((d) => mdFiles(d));
  // ★ 0 件走査で緑を返さない（fail-closed。planning#337）。走査対象を 1 件も拾えないのは
  // 「検査しているつもりで何も見ていない」状態であり、**退行を止めているという記録だけが残る**。
  if (files.length === 0) {
    console.error(`[check-doc-links] ${a.dirs.join(' / ')} 配下に Markdown が 1 件もありません。`);
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    process.exit(1);
  }
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
    console.log(
      `[check-doc-links] OK: ${files.length} 件の Markdown に破損した相対リンクはありません。`
    );
    process.exit(0);
  }
  console.error(`[check-doc-links] 破損リンク ${total} 件を検出しました:`);
  for (const r of report) {
    console.error(`\n  ${r.fp}`);
    for (const l of r.links) console.error(`    - ${l}`);
  }
  console.error('\n相対パスの綴り・階層（例: docs/functional/ からは ../../docs/... ）を確認してください。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  parseArgs,
  isBrokenRef,
  collectBroken,
  selfTest,
  LINK_EXT,
  DEFAULT_DIRS,
};
