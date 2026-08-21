#!/usr/bin/env node
'use strict';
/*
 * list-test-projects.js
 * テストプロジェクトを発見し、シャードへ分配する（IADR-0208 決定 10）。
 *
 * なぜ要るか:
 *   テストを N ランナーへ分割するには「どのプロジェクトをどのシャードが持つか」を
 *   **決定的に**決める必要がある。ここが揺れると、あるプロジェクトが
 *   **どのシャードでも走らない**という形で静かに失われる。
 *
 * 🔴 **発見は命名ではなく内容で行う。** `*.Tests.csproj` という命名に依存すると、
 *   命名から外れたプロジェクトを取りこぼす。テスト SDK / xunit への参照で判定する。
 *   （着手前の実測では命名 51 件・内容 51 件で一致した。一致しているうちに内容側へ寄せる。）
 *
 * 🔴 **0 件なら失敗させる。** 発見に失敗したまま「テスト 0 件で緑」を作らない
 *   —— 本リポジトリで繰り返し扱ってきた「気付けない種類の劣化」そのものである。
 *
 * 使い方:
 *   node scripts/list-test-projects.js --shard 1 --of 3   # シャード 1 の担当を 1 行 1 件で出す
 *   node scripts/list-test-projects.js --count            # 全体の件数
 *   node scripts/list-test-projects.js --self-test
 */

const fs = require('fs');
const path = require('path');

const REPO_ROOT = process.env.TEST_PROJECTS_ROOT
  ? path.resolve(process.env.TEST_PROJECTS_ROOT)
  : path.resolve(__dirname, '..');

/** テストプロジェクトと見なす参照。どれか 1 つでも含めばテストプロジェクトである。 */
const TEST_MARKERS = ['Microsoft.NET.Test.Sdk', 'xunit'];

const SKIP_DIRS = new Set(['bin', 'obj', 'node_modules', '.git', '.vs']);

/** `.csproj` を再帰的に集める。 */
function findCsproj(dir, out = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const e of entries) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (!SKIP_DIRS.has(e.name)) findCsproj(p, out);
    } else if (e.isFile() && e.name.endsWith('.csproj')) {
      out.push(p);
    }
  }
  return out;
}

function isTestProject(text) {
  return TEST_MARKERS.some((m) => text.includes(m));
}

/**
 * テストプロジェクトのリポジトリ相対パスを**ソート済み**で返す。
 * ソートするのは、シャードの割り当てを run 間で再現させるためである
 * （ファイルシステムの列挙順は保証されない）。
 */
function discover(root = REPO_ROOT) {
  return findCsproj(root)
    .filter((p) => {
      try {
        return isTestProject(fs.readFileSync(p, 'utf8'));
      } catch {
        return false;
      }
    })
    .map((p) => path.relative(root, p).split(path.sep).join('/'))
    .sort();
}

/**
 * ラウンドロビンで分配する。`shard` は 1 始まり。
 *
 * 🔴 **連続した塊で切らない（`slice`）。** プロジェクトはパス順で並ぶため、
 * 塊で切ると同じサービスのテストが 1 シャードへ固まり、所要が偏る。
 * ラウンドロビンなら重いプロジェクトが自然に散る。
 */
function assign(projects, shard, of) {
  if (!Number.isInteger(shard) || !Number.isInteger(of) || of < 1 || shard < 1 || shard > of) {
    throw new Error(`シャード指定が不正である: --shard ${shard} --of ${of}`);
  }
  return projects.filter((_, i) => i % of === shard - 1);
}

function selfTest() {
  const t = [];
  const ok = (name, fn) => {
    try {
      fn();
      t.push(`  ok   ${name}`);
    } catch (e) {
      t.push(`  FAIL ${name}: ${e.message}`);
      process.exitCode = 1;
    }
  };
  const eq = (a, b, m) => {
    if (JSON.stringify(a) !== JSON.stringify(b)) throw new Error(`${m || ''} ${JSON.stringify(a)} != ${JSON.stringify(b)}`);
  };

  ok('isTestProject: テスト SDK 参照を拾う', () =>
    eq(isTestProject('<PackageReference Include="Microsoft.NET.Test.Sdk" />'), true));
  ok('isTestProject: xunit 参照を拾う', () => eq(isTestProject('<PackageReference Include="xunit.v3" />'), true));
  ok('isTestProject: 実装プロジェクトは拾わない', () =>
    eq(isTestProject('<PackageReference Include="Serilog" />'), false));

  const p = ['a', 'b', 'c', 'd', 'e', 'f', 'g'];
  ok('assign: ラウンドロビンで配る', () => {
    eq(assign(p, 1, 3), ['a', 'd', 'g']);
    eq(assign(p, 2, 3), ['b', 'e']);
    eq(assign(p, 3, 3), ['c', 'f']);
  });
  // 🔴 分割が壊れる形は「重複」と「取りこぼし」の 2 つ。両方を固定する。
  ok('assign: 全シャードの和が全体と一致する（取りこぼしなし）', () => {
    const all = [1, 2, 3].flatMap((s) => assign(p, s, 3)).sort();
    eq(all, [...p].sort());
  });
  ok('assign: シャード間に重複が無い', () => {
    const all = [1, 2, 3].flatMap((s) => assign(p, s, 3));
    eq(all.length, new Set(all).size);
  });
  ok('assign: of=1 なら全部 1 本目へ', () => eq(assign(p, 1, 1), p));
  ok('assign: シャード数がプロジェクト数を超えても壊れない', () => {
    const all = [1, 2, 3, 4, 5].flatMap((s) => assign(['x', 'y'], s, 5));
    eq(all, ['x', 'y']);
  });
  ok('assign: 範囲外の指定は例外（黙って空を返さない）', () => {
    for (const [s, o] of [[0, 3], [4, 3], [1, 0]]) {
      let threw = false;
      try {
        assign(p, s, o);
      } catch {
        threw = true;
      }
      if (!threw) throw new Error(`--shard ${s} --of ${o} が通ってしまった`);
    }
  });

  console.log(t.join('\n'));
  console.log(`[list-test-projects] 自己試験 ${t.length} 件${process.exitCode ? ' に失敗あり' : ' OK。'}`);
}

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) return selfTest();

  const projects = discover();
  // 🔴 0 件は必ず失敗させる。「テストが 1 本も無い状態で緑」を作らない。
  if (projects.length === 0) {
    console.error(
      '[list-test-projects] テストプロジェクトが 1 件も見つからない。' +
        '発見条件（Microsoft.NET.Test.Sdk / xunit への参照）か走査ルートを疑うこと。',
    );
    process.exitCode = 1;
    return;
  }

  if (argv.includes('--count')) {
    console.log(String(projects.length));
    return;
  }

  const get = (flag) => {
    const i = argv.indexOf(flag);
    return i >= 0 ? Number(argv[i + 1]) : NaN;
  };
  const shard = get('--shard');
  const of = get('--of');
  if (!Number.isNaN(shard) || !Number.isNaN(of)) {
    const picked = assign(projects, shard, of);
    // シャードが空になるのは「プロジェクト数 < シャード数」のときだけで、CI では起こらない。
    // 起きたら分配かシャード数の指定が誤っているので、黙って 0 件を返さない。
    if (picked.length === 0) {
      console.error(
        `[list-test-projects] シャード ${shard}/${of} の担当が 0 件である` +
          `（全 ${projects.length} 件）。シャード数が多すぎないか確かめること。`,
      );
      process.exitCode = 1;
      return;
    }
    console.log(picked.join('\n'));
    return;
  }
  console.log(projects.join('\n'));
}

module.exports = { discover, assign, isTestProject };

if (require.main === module) main();
