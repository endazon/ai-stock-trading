#!/usr/bin/env node
'use strict';
/*
 * check-action-versions.js
 * ワークフローの `uses: <action>@vN` を集め、メジャーバージョンの退行を検出する。
 * 既定はオフライン（外部依存ゼロ・Node 標準モジュールのみ）。
 *
 * 背景（issue #96 / #97 → #98 → #148）:
 *   キットが配布するワークフロー雛形の Actions が古いままだと、実装リポジトリが同期する
 *   たびに**実装リポ側で前進したバージョンが巻き戻る**。#98 で dependabot.yml に
 *   `directory: "/tools/impl-handoff-kit/repo-template"` のエントリを足して対処したはずが、
 *   これは実質 no-op だった。github-actions エコシステムの Dependabot は**リポジトリ直下の
 *   .github/workflows/ しか走査せず**、`directory` で探されるのは複合アクションの action.yml
 *   だからである。
 *
 *   実測（issue #148）: エントリ投入後に本リポジトリで Dependabot が作った PR は 4 件、
 *   すべてルート（directory: "/"）由来の `chore:` 接頭辞であり、テンプレート用に設定した
 *   `template:` 接頭辞の PR は **0 件**。その間に upload-artifact は v4 のまま取り残され、
 *   実装リポ側では手作業で v7 へ差し戻していた。
 *
 *   最悪なのは**失敗せず単に走らない**ことである。設定が入っているので「対処済み」に見え、
 *   巻き戻りが再発しても気付けない。誤った安心を残す仕組みは、無いより悪い。
 *
 * 検査内容:
 *   [ERROR] action-versions.json の下限を下回るメジャーを使っている（＝巻き戻り／取り残し）
 *   [ERROR] --compare-with で指定したディレクトリの方が新しい（Dependabot 管理下からの退行）
 *   [WARN ] ワークフローで使っているのに表にも除外にも無い（検査対象外のまま増えている）
 *   [WARN ] 表にあるのにどのワークフローでも使われていない（表が腐っている）
 *
 * なぜ --compare-with があるか:
 *   表は人手で更新する以上、放置されれば dependabot.yml と同じように腐る。一方リポジトリ
 *   直下の .github/workflows/ は Dependabot が実際に更新している。両者を突き合わせれば、
 *   共通して使うアクション（checkout / setup-node 等）については**人手ゼロで**追随できる。
 *   表が要るのはテンプレートにしか出てこないアクション（upload-artifact 等）のためである。
 *
 * 使い方:
 *   node scripts/check-action-versions.js [--dir <workflows>] [--compare-with <workflows>]
 *   node scripts/check-action-versions.js --check-latest   # GitHub API で新しいメジャーを調べる（warn のみ）
 *   node scripts/check-action-versions.js --self-test
 *
 * 環境変数:
 *   GITHUB_TOKEN  --check-latest の API 認証に使う（未設定でも動くがレート制限が厳しい）
 */
const fs = require('fs');
const path = require('path');
const { warn, notice } = require('./lib/ci-annotate.js');

const REPO_ROOT = path.join(__dirname, '..');
const DEFAULT_DIR = path.join(REPO_ROOT, '.github', 'workflows');
const MANIFEST_PATH = path.join(__dirname, 'action-versions.json');

/** ワークフローらしきファイルを列挙する（.example.yml も対象）。 */
function listWorkflowFiles(dir) {
  try {
    return fs
      .readdirSync(dir)
      .filter((f) => /\.ya?ml(\.example)?$/.test(f) || /\.example\.ya?ml$/.test(f))
      .map((f) => path.join(dir, f));
  } catch (e) {
    return [];
  }
}

/**
 * テキストから `uses: owner/repo[/path]@ref` を集める。
 *
 * アクション名はサブパスを落として `owner/repo` に正規化する
 * （`github/codeql-action/init` と `.../analyze` を別物として数えないため）。
 * ローカルアクション（`./...`）と docker 指定（`docker://...`）は対象外。
 */
function collectUses(text) {
  const found = [];
  const re = /^\s*(?:-\s*)?uses:\s*['"]?([^'"\s]+)['"]?/gm;
  let m;
  while ((m = re.exec(text)) !== null) {
    const spec = m[1];
    if (spec.startsWith('.') || spec.startsWith('docker://')) continue;
    const at = spec.lastIndexOf('@');
    if (at === -1) continue;
    const name = spec.slice(0, at);
    const ref = spec.slice(at + 1);
    const parts = name.split('/');
    if (parts.length < 2) continue;
    found.push({ action: `${parts[0]}/${parts[1]}`, ref, raw: spec });
  }
  return found;
}

/** `v7` / `v7.0.1` / `7` からメジャー番号を取る。取れなければ null（SHA pin 等）。 */
function majorOf(ref) {
  const m = String(ref).match(/^v?(\d+)(?:[._-]|$)/);
  return m ? Number(m[1]) : null;
}

/** ディレクトリ全体から action → 最大メジャー / 出現箇所を集計する。 */
function scanDir(dir) {
  const byAction = new Map();
  for (const file of listWorkflowFiles(dir)) {
    const text = fs.readFileSync(file, 'utf8');
    for (const u of collectUses(text)) {
      const major = majorOf(u.ref);
      if (major === null) continue; // SHA pin は比較対象にしない
      const cur = byAction.get(u.action) || { major, files: new Set() };
      cur.major = Math.min(cur.major, major); // 最も古いものを問題として扱う
      cur.files.add(path.basename(file));
      byAction.set(u.action, cur);
    }
  }
  return byAction;
}

/** 表を読む。読めなければ null（呼び出し側が warn する）。 */
function loadManifest(manifestPath = MANIFEST_PATH) {
  try {
    const j = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
    return { expected: j.expected || {}, exempt: j.$exempt || {} };
  } catch (e) {
    return null;
  }
}

/**
 * 集計結果を表・比較対象と突き合わせる。
 * 戻り値 { errors, warnings }。純粋関数（テスト可能にするため I/O を持たない）。
 */
function evaluate(found, manifest, compareWith = null) {
  const errors = [];
  const warnings = [];
  const { expected, exempt } = manifest;

  for (const [action, info] of found) {
    if (Object.prototype.hasOwnProperty.call(exempt, action)) continue;

    const floor = expected[action];
    if (floor === undefined) {
      warnings.push(
        `${action} は action-versions.json に無いため下限を検査していない` +
          `（使用箇所: ${[...info.files].join(', ')}）。表へ追加するか $exempt に理由を書くこと`
      );
    } else if (info.major < floor) {
      errors.push(
        `${action}@v${info.major} は下限 v${floor} を下回る（使用箇所: ${[...info.files].join(', ')}）。` +
          'テンプレートが古いままだと、実装リポジトリが同期するたびにバージョンが巻き戻る'
      );
    }

    if (compareWith) {
      const other = compareWith.get(action);
      if (other && other.major > info.major) {
        errors.push(
          `${action}@v${info.major} は比較対象の v${other.major} より古い` +
            `（比較元: ${[...other.files].join(', ')}）。` +
            'リポジトリ直下は Dependabot が更新するため、テンプレート側が取り残されている'
        );
      }
    }
  }

  for (const action of Object.keys(expected)) {
    if (!found.has(action)) {
      warnings.push(`${action} は action-versions.json にあるが、どのワークフローでも使われていない（表が古い可能性）`);
    }
  }
  return { errors, warnings };
}

/**
 * GitHub API で最新のメジャーを調べ、表より新しければ warn する（--check-latest）。
 * 失敗は握りつぶす（fail-open）。ネットワーク・レート制限で CI を落とさないため。
 */
async function checkLatest(manifest) {
  const warnings = [];
  const headers = { 'user-agent': 'check-action-versions', accept: 'application/vnd.github+json' };
  if (process.env.GITHUB_TOKEN) headers.authorization = `Bearer ${process.env.GITHUB_TOKEN}`;

  for (const [action, floor] of Object.entries(manifest.expected)) {
    try {
      const res = await fetch(`https://api.github.com/repos/${action}/releases/latest`, { headers });
      if (!res.ok) {
        warnings.push(`${action}: 最新版を取得できなかった（HTTP ${res.status}）。表の確認は手動で行うこと`);
        continue;
      }
      const latest = majorOf((await res.json()).tag_name);
      if (latest !== null && latest > floor) {
        warnings.push(
          `${action} は v${latest} が出ているが action-versions.json は v${floor} のままである。` +
            'テンプレートの uses: と表を同時に上げること'
        );
      }
    } catch (e) {
      warnings.push(`${action}: 最新版の確認に失敗した（${e.message}）`);
    }
  }
  return warnings;
}

/** 検証器自体の自己試験。 */
function selfTest() {
  const cases = [];
  const add = (name, fn) => cases.push({ name, fn });
  const mkFound = (entries) =>
    new Map(entries.map(([a, major, file]) => [a, { major, files: new Set([file || 'w.yml']) }]));
  const manifest = { expected: { 'actions/checkout': 7, 'actions/upload-artifact': 7 }, exempt: {} };

  add('uses: を収集しメジャーを取る', () => {
    const u = collectUses('steps:\n  - uses: actions/checkout@v7\n  - uses: actions/upload-artifact@v4\n');
    return u.length === 2 && u[0].action === 'actions/checkout' && majorOf(u[1].ref) === 4;
  });
  add('サブパスは owner/repo へ正規化する', () => {
    const u = collectUses('  - uses: github/codeql-action/init@v4\n');
    return u.length === 1 && u[0].action === 'github/codeql-action';
  });
  add('ローカル / docker 指定は対象外', () => {
    return collectUses('  - uses: ./.github/actions/x\n  - uses: docker://alpine:3\n').length === 0;
  });
  add('SHA pin はメジャーを取れない（比較対象外）', () => majorOf('a81bbbf8298c0fa03ea29cdc473d45769f953675') === null);
  add('v7.0.1 のようなパッチ付きも読める', () => majorOf('v7.0.1') === 7);

  add('下限を下回れば ERROR（実障害 upload-artifact@v4 の形）', () => {
    const r = evaluate(mkFound([['actions/upload-artifact', 4]]), manifest);
    return r.errors.length === 1 && r.errors[0].includes('upload-artifact');
  });
  add('下限を満たせば ERROR にしない', () => evaluate(mkFound([['actions/checkout', 7]]), manifest).errors.length === 0);
  add('下限より新しいのは当然 OK', () => evaluate(mkFound([['actions/checkout', 9]]), manifest).errors.length === 0);

  add('表に無いアクションは WARN（黙って検査対象外にしない）', () => {
    const r = evaluate(mkFound([['foo/bar', 1]]), manifest);
    return r.warnings.some((w) => w.includes('foo/bar')) && r.errors.length === 0;
  });
  add('$exempt のアクションは検査しない', () => {
    const m = { expected: {}, exempt: { 'github/codeql-action': '理由' } };
    const r = evaluate(mkFound([['github/codeql-action', 1]]), m);
    return r.errors.length === 0 && r.warnings.length === 0;
  });
  add('使われていない表エントリは WARN（表の腐りを検出）', () => {
    const r = evaluate(mkFound([['actions/checkout', 7]]), manifest);
    return r.warnings.some((w) => w.includes('upload-artifact'));
  });

  add('比較対象の方が新しければ ERROR（Dependabot 管理下からの退行）', () => {
    const r = evaluate(mkFound([['actions/checkout', 6]]), { expected: {}, exempt: {} }, mkFound([['actions/checkout', 7, 'root.yml']]));
    return r.errors.some((e) => e.includes('比較対象'));
  });
  add('比較対象が同じ / 古ければ ERROR にしない', () => {
    const cmp = mkFound([['actions/checkout', 7, 'root.yml']]);
    const same = evaluate(mkFound([['actions/checkout', 7]]), { expected: {}, exempt: {} }, cmp);
    const newer = evaluate(mkFound([['actions/checkout', 8]]), { expected: {}, exempt: {} }, cmp);
    return same.errors.length === 0 && newer.errors.length === 0;
  });
  add('比較対象に無いアクションは比較しない', () => {
    const r = evaluate(mkFound([['only/here', 1]]), { expected: { 'only/here': 1 }, exempt: {} }, new Map());
    return r.errors.length === 0;
  });

  let failed = 0;
  for (const c of cases) {
    let okResult = false;
    try {
      okResult = c.fn() === true;
    } catch (e) {
      okResult = false;
    }
    if (okResult) process.stdout.write(`  ok  ${c.name}\n`);
    else {
      failed++;
      process.stderr.write(`  NG  ${c.name}\n`);
    }
  }
  if (failed) {
    process.stderr.write(`\n✗ 検証器の自己試験が ${failed} 件失敗した\n`);
    return 1;
  }
  process.stdout.write(`✓ 検証器の自己試験 ${cases.length} 件すべて合格\n`);
  return 0;
}

async function main(argv) {
  const args = argv.slice(2);
  if (args.includes('--self-test')) process.exit(selfTest());

  const manifest = loadManifest();
  if (!manifest) {
    warn(`action-versions.json を読めないため検査していない: ${MANIFEST_PATH}`);
    process.exit(0);
  }

  if (args.includes('--check-latest')) {
    const ws = await checkLatest(manifest);
    for (const w of ws) warn(w);
    if (ws.length === 0) notice('action-versions.json は最新のメジャーに追随している');
    process.exit(0); // 情報提供のみ。ネットワーク要因で CI を落とさない
  }

  const dirIdx = args.indexOf('--dir');
  const dir = dirIdx !== -1 ? args[dirIdx + 1] : DEFAULT_DIR;
  const cmpIdx = args.indexOf('--compare-with');
  const cmpDir = cmpIdx !== -1 ? args[cmpIdx + 1] : null;

  const found = scanDir(dir);
  if (found.size === 0) {
    warn(`ワークフローから uses: を 1 つも取れないため検査していない: ${dir}`);
    process.exit(0);
  }
  const compareWith = cmpDir ? scanDir(cmpDir) : null;

  const { errors, warnings } = evaluate(found, manifest, compareWith);
  process.stdout.write(`Actions バージョン検査: ${found.size} 個のアクションを検査（${dir}）\n`);
  for (const w of warnings) warn(w);

  if (errors.length) {
    process.stderr.write(`\n✗ バージョンの退行 ${errors.length} 件:\n`);
    for (const e of errors) process.stderr.write(`  - ${e}\n`);
    process.stderr.write(
      '\n対処: テンプレートの uses: を上げ、scripts/action-versions.json の下限も同時に更新する。\n' +
        '最新のメジャーは `node scripts/check-action-versions.js --check-latest` で確認できる。\n'
    );
    process.exit(1);
  }
  process.stdout.write('✓ Actions のバージョンに退行なし\n');
  process.exit(0);
}

if (require.main === module) {
  main(process.argv);
}

module.exports = { collectUses, majorOf, scanDir, loadManifest, evaluate, selfTest };
