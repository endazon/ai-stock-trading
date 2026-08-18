#!/usr/bin/env node
'use strict';
/*
 * check-kit-sync.js
 * impl-handoff-kit `repo-template` との分類（A / B / C）に基づく追随の機械検査。
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * ■ なぜ要るか
 *   キットは「分類 A は diff でバイト一致を機械判定できる」ことを利点に挙げるが、
 *   **どのファイルがどの分類かを記した表がリポジトリに無ければ、判定を回す対象が決まらない。**
 *   表が無いまま運用した実装リポジトリで、次の 2 件が実測されている
 *   （endazon/microservices-platform#712 / endazon/microservices-platform#721。裁定は planning#336）。
 *
 *     1. キット由来の文書から**節が丸ごと欠落**していたことに、誰も気付かなかった
 *     2. 計画 pin だけを進めて**キット追随を伴わず**、後日テストが落ちて初めて判明した
 *
 *   **pin がずれていなくても乖離は起きる。** 差が出るのは「追随を機械が見ているか」である。
 *
 * ■ 何を見るか
 *   1. **分類 A はキットとバイト一致である**（本検査の主目的）
 *   2. **キットの全ファイルが分類表に載っている**（キットに増えたファイルを黙って見逃さない）
 *   3. **分類表に載っているのに実在しないファイルが無い**（表が現実から離れていない）
 *
 * ■ 何を見ないか（正直に書く）
 *   - **分類 B のデルタが妥当か** —— 何種に当たるかは表に書かせるが、**内容の妥当性は人が見る**。
 *     文字列一致では意味を読めない。
 *   - **キット側に新しい規範が入ったこと** —— 本検査が見るのは**バイト一致**であって規範の移動ではない。
 *   - **分類 C の内容** —— 定義上、同期しない。
 *   - 🔴 **キット版が実装側の版より「劣る」こと** —— **バイト一致は優劣を判定しない。**
 *     実装側の改善をキットへ一般化する過程で機能が落ちても（実測: `--require-planning` が
 *     落ちた。planning#343）、本検査は「分類 A が不一致」としか言わない。**キット版で上書き
 *     してよいかは、採る側が実走して確かめるしかない。** 機械化しないと決めた（裁定 planning#343）
 *     —— 一般の機能差検出は投機的であり、**取り込み手順の側**（HOWTO）で塗る。
 *
 * ■ fail-open / fail-closed の境目
 *   キットを参照できないとき（submodule 未 populate・隣接クローンが無い）は **skip（exit 0）**
 *   する。ローカル環境差で CI を落とさないため（`check-doc-links.js` と同じ扱い）。
 *   **ただし 0 件走査では緑にしない** —— 参照できているのに対象が 0 件なら **fail** する。
 *   「検査しているつもりで何も見ていない」を緑で通すと、退行を止めている記録だけが残る。
 *
 *   🔴 **`--require-planning` を付けると、参照できないときは skip ではなく fail になる。**
 *   **CI では必ずこれを付ける。** 計画リポを取得するはずのジョブで取得に失敗したとき、
 *   fail-open のままだと**「配線したのに一度も検査していない」状態が緑で固定される**
 *   —— 「緑だが検査されていない」そのものである（planning#343）。
 *
 * ■ 未知の引数は受け付けない（planning#343）
 *   **知らないフラグを黙って無視してはならない。** 本検査器の初版は `--require-planning` を
 *   持たないまま**黙って無視しており**、それを渡す CI では「フラグは効いていないのに
 *   回帰テストは通り続ける」状態になった —— **`run:` 行に文字列が在ることしか見ていない
 *   テストは、配線が効いていることを保証しない。** 未知の引数は設定誤りとして落とす。
 *
 * 使い方:
 *   node scripts/check-kit-sync.js [--require-planning] [--self-test]
 *
 * 環境変数:
 *   KIT_DIR   キット `repo-template` の場所を明示する（既定の探索順を上書きする）
 */

const fs = require('fs');
const path = require('path');

const REPO = path.join(__dirname, '..');

// 【置換点】キット `repo-template` の探索順。**先に見つかったものを使う。**
// 計画リポジトリを submodule で取り込む構成と、隣接クローンで参照する構成の両方を既定で見る
// （キットの `CLAUDE.md`「計画書の参照」が定める 2 通りである）。構成が違うなら書き換える。
const KIT_CANDIDATES = [
  'planning/tools/impl-handoff-kit/repo-template',
  '../project-planning/tools/impl-handoff-kit/repo-template',
];

const TABLE = path.join(__dirname, 'kit-sync-classification.json');

/** キットの場所を解決する。見つからなければ null（＝ skip）。 */
function resolveKit(repo = REPO, env = process.env) {
  if (env.KIT_DIR) return fs.existsSync(env.KIT_DIR) ? env.KIT_DIR : null;
  for (const rel of KIT_CANDIDATES) {
    const p = path.resolve(repo, rel);
    if (fs.existsSync(p)) return p;
  }
  return null;
}

/** ディレクトリ配下の全ファイルを相対パスで列挙する（拡張子で絞らない。母集合の規則 3）。 */
function listFiles(dir) {
  const out = [];
  (function walk(d) {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      if (e.name === '.git') continue;
      const p = path.join(d, e.name);
      if (e.isDirectory()) walk(p);
      else out.push(path.relative(dir, p).split(path.sep).join('/'));
    }
  })(dir);
  return out;
}

/**
 * 分類表とキット・自リポの実物を突き合わせ、違反の配列を返す（純粋に近い形にしてテスト可能にする）。
 *
 * @param {{classes:{A:string[],B:Object,C:string[]},notApplicable:string[]}} table 分類表
 * @param {string[]} kitFiles キット配下の全ファイル（相対パス）
 * @param {(rel:string)=>boolean} existsInRepo 自リポに実在するか
 * @param {(rel:string)=>boolean} existsInKit  キットに実在するか
 * @param {(rel:string)=>boolean} bytesEqual   バイト一致か（分類 A のみ呼ぶ）
 */
function inspect(table, kitFiles, existsInRepo, existsInKit, bytesEqual) {
  const A = table.classes.A || [];
  const B = Object.keys(table.classes.B || {});
  const C = table.classes.C || [];
  const NA = table.notApplicable || [];
  const classified = new Set([...A, ...B, ...C, ...NA]);
  const errors = [];

  // ★ 0 件走査で静かに緑にしない。
  if (kitFiles.length === 0) {
    errors.push('キット配下のファイルが 0 件だった。走査が空振りしている（参照先の確認が要る）');
  }

  // 2. キットの全ファイルが表に載っているか
  for (const f of kitFiles) {
    if (!classified.has(f)) {
      errors.push(
        `[unclassified] ${f} が分類表に無い。キットに増えたファイルである可能性がある` +
          ' —— A / B / C のどれかへ分類すること',
      );
    }
  }

  // 3. 表に在るのに実在しないもの（表が現実から離れていないか）
  for (const f of [...A, ...B, ...C]) {
    if (!existsInRepo(f)) errors.push(`[missing] ${f} が分類表に在るが本リポに実在しない。表を追随させること`);
    if (!existsInKit(f)) errors.push(`[not-in-kit] ${f} が分類表に在るがキットに実在しない。表を追随させること`);
  }

  // 1. 分類 A のバイト一致（本検査の主目的）
  let checkedA = 0;
  for (const f of A) {
    if (!existsInKit(f) || !existsInRepo(f)) continue;
    checkedA += 1;
    if (!bytesEqual(f)) {
      errors.push(
        `[drift] ${f} が分類 A なのにキットとバイト一致でない。` +
          'キット原文で上書きするか、固有デルタとして分類 B へ移して理由を書くこと',
      );
    }
  }

  // ★ 分類 A が 0 件なら、検査が実質何も見ていない。
  if (checkedA === 0) errors.push('分類 A の照合対象が 0 件だった。検査が実質何も見ていない');

  return { errors, counts: { kit: kitFiles.length, A: A.length, B: B.length, C: C.length, NA: NA.length } };
}

/** 受け付ける引数（ここに無いものは設定誤りとして落とす。planning#343）。 */
const KNOWN_FLAGS = ['--require-planning', '--self-test'];

/**
 * 引数を解釈する。**未知のフラグは黙って無視せず、呼び出し側へ返す。**
 * 黙って無視すると「CI は渡し続けているのに効いていない」状態が緑で固定される。
 */
function parseArgs(argv) {
  const args = argv.slice(2);
  const unknown = args.filter((a) => !KNOWN_FLAGS.includes(a));
  return { requirePlanning: args.includes('--require-planning'), unknown };
}

function main(argv = process.argv) {
  const { requirePlanning, unknown } = parseArgs(argv);
  if (unknown.length) {
    console.error(
      `[check-kit-sync] 未知の引数: ${unknown.join(' ')}\n` +
        `  受け付けるのは ${KNOWN_FLAGS.join(' / ')} である。` +
        '黙って無視すると、CI が渡し続けているフラグが効いていないことに誰も気付けない。',
    );
    return 1;
  }

  const kit = resolveKit();
  if (!kit) {
    const where = KIT_CANDIDATES.join(' / ');
    if (requirePlanning) {
      console.error(
        `[check-kit-sync] キット repo-template を参照できない（探した先: ${where}）。` +
          '--require-planning が指定されているため fail する —— ' +
          '計画リポを取得するはずのジョブで取得できていない。skip して緑にしてはならない。',
      );
      return 1;
    }
    console.log(
      `  warn  [check-kit-sync] キット repo-template を参照できないため skip した（探した先: ${where}）。` +
        'この範囲は検査されていない。',
    );
    return 0;
  }
  if (!fs.existsSync(TABLE)) {
    console.error(
      `[check-kit-sync] 分類表が無い: ${TABLE}\n` +
        '  キットの scripts/kit-sync-classification.example.json を雛形に作成すること。',
    );
    return 1;
  }

  const table = JSON.parse(fs.readFileSync(TABLE, 'utf8'));
  const { errors, counts } = inspect(
    table,
    listFiles(kit),
    (f) => fs.existsSync(path.join(REPO, f)),
    (f) => fs.existsSync(path.join(kit, f)),
    (f) => fs.readFileSync(path.join(kit, f)).equals(fs.readFileSync(path.join(REPO, f))),
  );

  if (errors.length > 0) {
    console.error(`[check-kit-sync] 追随の違反 ${errors.length} 件を検出しました:`);
    for (const e of errors) console.error(`    ${e}`);
    return 1;
  }

  console.log(
    `[check-kit-sync] OK: キット ${counts.kit} 件を分類表と突合しました` +
      `（A ${counts.A} 件はバイト一致 / B ${counts.B} 件は固有デルタ / C ${counts.C} 件は同期しない` +
      ` / 対象外 ${counts.NA} 件）。`,
  );
  return 0;
}

function selfTest() {
  const assert = require('assert');
  let n = 0;
  const T = (name, fn) => {
    n += 1;
    fn();
    console.log(`  ok  ${name}`);
  };

  const mk = (over = {}) => ({
    classes: { A: ['a.md'], B: { 'b.md': '1. リポジトリ構成' }, C: ['c.md'], ...over.classes },
    notApplicable: over.notApplicable || ['x.example.yml'],
  });
  const all = ['a.md', 'b.md', 'c.md', 'x.example.yml'];
  const yes = () => true;
  const eq = () => true;

  T('全ファイルが分類済みでバイト一致なら違反ゼロ', () => {
    assert.deepStrictEqual(inspect(mk(), all, yes, yes, eq).errors, []);
  });

  T('分類 A がバイト一致でなければ drift を上げる', () => {
    const { errors } = inspect(mk(), all, yes, yes, () => false);
    assert.strictEqual(errors.length, 1);
    assert.match(errors[0], /^\[drift\] a\.md/);
  });

  T('キットに増えたファイルは unclassified で上がる', () => {
    const { errors } = inspect(mk(), [...all, 'new.md'], yes, yes, eq);
    assert.strictEqual(errors.length, 1);
    assert.match(errors[0], /^\[unclassified\] new\.md/);
  });

  T('表に在るのに自リポに無ければ missing で上がる', () => {
    const { errors } = inspect(mk(), all, (f) => f !== 'c.md', yes, eq);
    assert.ok(errors.some((e) => e.startsWith('[missing] c.md')));
  });

  T('表に在るのにキットに無ければ not-in-kit で上がる', () => {
    const { errors } = inspect(mk(), all, yes, (f) => f !== 'b.md', eq);
    assert.ok(errors.some((e) => e.startsWith('[not-in-kit] b.md')));
  });

  T('★ キットが 0 件なら緑にしない', () => {
    const { errors } = inspect(mk(), [], yes, yes, eq);
    assert.ok(errors.some((e) => /0 件だった。走査が空振り/.test(e)));
  });

  T('★ 分類 A が 0 件なら緑にしない（検査が何も見ていない）', () => {
    const table = mk({ classes: { A: [], B: { 'b.md': '1. リポジトリ構成' }, C: ['c.md'] } });
    const { errors } = inspect(table, ['b.md', 'c.md', 'x.example.yml'], yes, yes, eq);
    assert.ok(errors.some((e) => /分類 A の照合対象が 0 件/.test(e)));
  });

  T('notApplicable は分類済みとして扱う（unclassified を出さない）', () => {
    const { errors } = inspect(mk(), all, yes, yes, eq);
    assert.ok(!errors.some((e) => e.includes('x.example.yml')));
  });

  T('KIT_DIR が実在しなければ null（skip へ倒す）', () => {
    assert.strictEqual(resolveKit(REPO, { KIT_DIR: '/no/such/dir' }), null);
  });

  T('KIT_DIR が実在すればそれを使う', () => {
    assert.strictEqual(resolveKit(REPO, { KIT_DIR: REPO }), REPO);
  });

  // --- planning#343: fail-open を閉じる手段と、未知の引数の扱い ---
  //
  // **これらが無かったために「CI はフラグを渡し続けるが効いていない」状態が生まれた。**
  // 配線を見るテスト（`run:` 行に文字列が在るか）では捕まらないため、ここで挙動を固定する。
  T('★ --require-planning を認識する（黙って無視しない）', () => {
    assert.deepStrictEqual(parseArgs(['node', 'x', '--require-planning']), {
      requirePlanning: true,
      unknown: [],
    });
  });

  T('★ 未知の引数は設定誤りとして落とす', () => {
    assert.deepStrictEqual(parseArgs(['node', 'x', '--requre-planning']).unknown, ['--requre-planning']);
    assert.strictEqual(main(['node', 'x', '--requre-planning']), 1);
  });

  T('★ キットを参照できないとき、--require-planning なら fail する', () => {
    const saved = process.env.KIT_DIR;
    process.env.KIT_DIR = '/no/such/kit';
    try {
      assert.strictEqual(main(['node', 'x']), 0, 'フラグ無しは skip（fail-open）');
      assert.strictEqual(main(['node', 'x', '--require-planning']), 1, 'フラグ有りは fail');
    } finally {
      if (saved === undefined) delete process.env.KIT_DIR;
      else process.env.KIT_DIR = saved;
    }
  });

  console.log(`[check-kit-sync] self-test OK（${n} 件）`);
}

if (require.main === module) {
  if (process.argv.includes('--self-test')) {
    selfTest();
    process.exit(0);
  }
  process.exit(main());
}

module.exports = { main, inspect, resolveKit, listFiles, parseArgs, selfTest, KIT_CANDIDATES, KNOWN_FLAGS };
