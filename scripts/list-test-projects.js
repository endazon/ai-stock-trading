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
 * 🔴 **分配は LPT（Longest Processing Time first）貪欲である**（IADR-0257）。
 *   重み（テスト件数）の降順に、その時点で最も軽いシャードへ積む。
 *   ラウンドロビンは重みを見ないため、重いプロジェクトが 1 本のシャードへ寄ると
 *   **残りのシャードが遊ぶ**。LPT は重い順に置くことでその形を避ける。
 *
 * 🔴 **重みは走査で毎回導き、JSON へコミットしない**（IADR-0257 決定 2）。
 *   コミットした重みは必ず実体とずれ、**ずれても誰も気付かない**
 *   —— 本リポジトリが繰り返し扱ってきた劣化の形である。
 *   読めなければ重み 1 へ倒す（fail-open。**分配が偏るだけでテストは落ちない**）。
 *
 * 使い方:
 *   node scripts/list-test-projects.js --shard 1 --of 4   # シャード 1 の担当を 1 行 1 件で出す
 *     [--slnx shard.slnx]        担当テストだけの solution（dotnet test 用）
 *     [--build-slnx build.slnx]  担当テストの参照閉包を直下へ並べた solution（restore / build 用。#1314）
 *     [--publish-out publish.txt] この脚が publish 乾式実行を担うサービス本体（各サービスちょうど 1 脚）
 *   node scripts/list-test-projects.js --count            # 全体の件数
 *   node scripts/list-test-projects.js --weights          # 走査で導いた重み（重い順）
 *   node scripts/list-test-projects.js --self-test
 */

const fs = require('fs');
const os = require('os');
const path = require('path');

const REPO_ROOT = process.env.TEST_PROJECTS_ROOT
  ? path.resolve(process.env.TEST_PROJECTS_ROOT)
  : path.resolve(__dirname, '..');

/** 被覆の検査で「全プロジェクト」とみなす solution（リポジトリ相対）。 */
const SOLUTION = 'backend/backend.slnx';

/** テストプロジェクトと見なす参照。どれか 1 つでも含めばテストプロジェクトである。 */
const TEST_MARKERS = ['Microsoft.NET.Test.Sdk', 'xunit'];

/** 重み（テスト件数）を数える印。xUnit のテスト属性である。 */
const TEST_ATTRIBUTE_MARKERS = ['[Fact', '[Theory'];

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

/** `.cs` を再帰的に集める（重みの走査用）。 */
function findCs(dir, out = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const e of entries) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (!SKIP_DIRS.has(e.name)) findCs(p, out);
    } else if (e.isFile() && e.name.endsWith('.cs')) {
      out.push(p);
    }
  }
  return out;
}

/**
 * 1 つのソースに含まれるテスト件数を数える。
 *
 * `[Fact` / `[Theory` の**出現数**を数える（`[Fact]` も `[Fact(DisplayName = "…")]` も拾う）。
 * 素朴な文字列一致であり、コメントアウトされた属性も数える —— それでよい。
 * **重みは所要時間の代理変数**であって正確な件数の台帳ではなく、
 * 多少の過大評価は分配をわずかに偏らせるだけで、テストを落とさない。
 */
function countTests(text) {
  let n = 0;
  for (const m of TEST_ATTRIBUTE_MARKERS) {
    let i = text.indexOf(m);
    while (i !== -1) {
      n += 1;
      i = text.indexOf(m, i + m.length);
    }
  }
  return n;
}

/**
 * テストプロジェクトごとの重みを**走査で導く**。
 *
 * 🔴 **JSON へコミットしない**（IADR-0257 決定 2）。コミットした重みは
 * テストが増減するたびに実体とずれ、**ずれても誰も気付かない**
 * —— 分配が静かに偏り、CI が理由もなく遅くなる形である。走査は 51 本で 100ms 未満であり、
 * 毎回導けば定義上ずれない。
 *
 * 🔴 **読めなければ 1 へ倒す（fail-open）。** 重みは所要時間の代理変数にすぎず、
 * 誤っても**分配が偏るだけでテストは落ちない**。ここで例外を投げると、
 * 走査の失敗が「テストが 1 本も走らない」へ化ける。
 * 下限を 1 に置くのは、テストプロジェクトが 0 件でもホスト起動の固定費を必ず払うためである。
 */
function weigh(projects, root = REPO_ROOT) {
  const weights = new Map();
  for (const rel of projects) {
    const dir = path.join(root, path.dirname(rel));
    let n = 0;
    for (const f of findCs(dir)) {
      try {
        n += countTests(fs.readFileSync(f, 'utf8'));
      } catch {
        // 読めないファイルは 0 件として飛ばす（fail-open）
      }
    }
    weights.set(rel, Math.max(1, n));
  }
  return weights;
}

/** 重み表（Map / プレーンオブジェクト / 未指定）から重みを引く。不正値は 1 へ倒す。 */
function weightOf(weights, project) {
  if (!weights) return 1;
  const v = weights instanceof Map ? weights.get(project) : weights[project];
  return Number.isFinite(v) && v > 0 ? v : 1;
}

/**
 * **LPT（Longest Processing Time first）貪欲**で分配する。`shard` は 1 始まり。
 *
 * 重みの降順に走査し、その時点で**最も軽いシャード**へ積む。
 *
 * 🔴 **連続した塊で切らない（`slice`）。** プロジェクトはパス順で並ぶため、
 * 塊で切ると同じサービスのテストが 1 シャードへ固まり、所要が偏る。
 *
 * 🔴 **ラウンドロビンでも足りない。** ラウンドロビンは重みを見ないため、
 * 重いプロジェクトが偶然 1 本のシャードへ寄ると**残りが遊ぶ**。
 * LPT は最悪でも理想分配の (4/3 − 1/(3m)) 倍に収まることが知られている。
 *
 * 🔴 **決定的でなければならない。** シャード間で分配が食い違うと、
 * あるプロジェクトが**どのシャードでも走らない**形で静かに失われる。
 * ①重み降順 ②同じ重みならパス昇順、で全順序を作り、
 * ③積み先が同点なら**添字の小さいシャード**を選ぶ —— 3 つとも決定的である。
 *
 * ⚠️ 返す並びは**パス順ではなく LPT の積み順**（重い順）である。
 * 呼び出し側は行数しか見ない（`wc -l` と `.slnx` の生成）ため並びに依存しないが、
 * 「パス順で出る」と思い込まないこと。
 *
 * ⚠️ 重み未指定（すべて 1）のとき、LPT は**ラウンドロビンに退化する**
 * （同点は添字の小さい側から埋まるため）。移行前の挙動がそのまま部分集合として残る。
 */
function assign(projects, shard, of, weights = null) {
  if (!Number.isInteger(shard) || !Number.isInteger(of) || of < 1 || shard < 1 || shard > of) {
    throw new Error(`シャード指定が不正である: --shard ${shard} --of ${of}`);
  }
  const ordered = [...projects].sort((a, b) => {
    const d = weightOf(weights, b) - weightOf(weights, a);
    if (d !== 0) return d;
    return a < b ? -1 : a > b ? 1 : 0;
  });
  const loads = new Array(of).fill(0);
  const buckets = Array.from({ length: of }, () => []);
  for (const p of ordered) {
    let best = 0;
    for (let i = 1; i < of; i += 1) {
      if (loads[i] < loads[best]) best = i;
    }
    buckets[best].push(p);
    loads[best] += weightOf(weights, p);
  }
  return buckets[shard - 1];
}

/**
 * 1 つの `.csproj` が直接参照する `.csproj` をリポジトリ相対パスで返す（#1314 / IADR-0257 追記 2026-10-12）。
 *
 * XML コメントは先に剥がす（コメント中の例示を参照と取り違えないため）。
 * 読めなければ空を返す —— その場合は下の被覆検査が「どの脚でもビルドされない」として fail-loud に落とす。
 */
function projectRefs(rel, root = REPO_ROOT) {
  let text;
  try {
    text = fs.readFileSync(path.join(root, rel), 'utf8');
  } catch {
    return [];
  }
  text = text.replace(/<!--[\s\S]*?-->/g, '');
  // 🔴 **解決できない形は fail-loud に落とす。** `$(...)` を含む Include や Update= 形式は、
  // MSBuild の評価なしには参照先が決まらない。黙って飛ばすと閉包が欠け、
  // 欠けた先が「どの脚でもビルドされない」か、publish の担当が誤って決まる。
  if (/<ProjectReference\b[^>]*\bUpdate\s*=/.test(text)) {
    throw new Error(`${rel}: ProjectReference の Update= 形式は解決できない（Include= の相対パスで書くこと）`);
  }
  const out = [];
  for (const m of text.matchAll(/<ProjectReference\s+Include="([^"]+)"/g)) {
    if (m[1].includes('$(')) {
      throw new Error(`${rel}: ProjectReference の Include に MSBuild プロパティ（${m[1]}）を含むものは解決できない`);
    }
    const target = path.posix.normalize(path.posix.join(path.posix.dirname(rel), m[1].replace(/\\/g, '/')));
    out.push(target);
  }
  return out;
}

/**
 * 与えたプロジェクト群の**推移的な参照閉包**（自身を含む）をパス昇順で返す。
 *
 * 🔴 **脚は `backend.slnx` 全体ではなくこの閉包だけをビルドする**（#1314）。
 * `dotnet build shard.slnx` は ProjectReference を辿って閉包をビルドするので、
 * ここで求める集合は MSBuild が実際にビルドする集合と一致する。
 * ここで使うのは**被覆の検査**（全プロジェクトがどれかの脚でビルドされるか）と
 * **publish の担当決め**だけであり、ビルドそのものは MSBuild に任せる。
 */
function closure(projects, root = REPO_ROOT) {
  const seen = new Set();
  const stack = [...projects];
  while (stack.length) {
    const p = stack.pop();
    if (seen.has(p)) continue;
    seen.add(p);
    stack.push(...projectRefs(p, root));
  }
  return [...seen].sort();
}

/** サービス本体（`backend/Services/<Name>/<Name>.csproj`）か。CI の publish 乾式実行の対象と同じ形。 */
function isServiceProject(rel) {
  return /^backend\/Services\/[^/]+\/[^/]+\.csproj$/.test(rel);
}

/** `.slnx` に載っているプロジェクトをリポジトリ相対パスで返す。読めなければ null。 */
function solutionProjects(slnxRel, root = REPO_ROOT) {
  let text;
  try {
    text = fs.readFileSync(path.join(root, slnxRel), 'utf8');
  } catch {
    return null;
  }
  text = text.replace(/<!--[\s\S]*?-->/g, '');
  const base = path.posix.dirname(slnxRel);
  return [...text.matchAll(/<Project\s+Path="([^"]+)"/g)]
    .map((m) => path.posix.normalize(path.posix.join(base, m[1].replace(/\\/g, '/'))))
    .sort();
}

/**
 * 全シャードの「担当テスト」と「ビルド閉包」を求める（#1314）。
 * 各脚は自分の分だけを使うが、**被覆と publish の重複排除は全シャードを見ないと決まらない**ため、
 * 毎回全シャード分を計算する（20 本で数 ms）。
 */
function planShards(projects, of, weights, root = REPO_ROOT) {
  const plans = [];
  for (let s = 1; s <= of; s += 1) {
    const tests = assign(projects, s, of, weights);
    plans.push({ tests, closure: closure(tests, root) });
  }
  return plans;
}

/**
 * シャード `shard` が publish 乾式実行を担うサービス本体。
 * 自分の閉包にあり、**添字の小さいシャードの閉包に無い**ものだけを返す
 * —— 各サービスはちょうど 1 本の脚で publish される（重複も取りこぼしも作らない）。
 */
function publishTargets(plans, shard) {
  const earlier = new Set(plans.slice(0, shard - 1).flatMap((p) => p.closure));
  return plans[shard - 1].closure.filter((p) => isServiceProject(p) && !earlier.has(p));
}

/** `required` のうち、どのシャードの閉包にも入らないもの（＝どの脚でもビルドされないもの）。 */
function uncovered(required, plans) {
  const built = new Set(plans.flatMap((p) => p.closure));
  return required.filter((p) => !built.has(p));
}

/**
 * シャード用の solution（`.slnx`）を組み立てる。
 *
 * 🔴 **なぜ solution にするのか。** 1 プロジェクトずつ `dotnet test` を回すと、
 * **1 回あたり約 4.9 秒の起動コスト**（MSBuild の評価・テストホストの立ち上げ）が
 * プロジェクト数だけ積み上がる —— 51 本で **約 250 秒**の純粋な無駄である（実測で較正）。
 * solution にまとめれば起動は**シャードごとに 1 回**で済み、
 * プロジェクト間の並列も MSBuild が従来どおり面倒を見る。
 *
 * パスは solution ファイルからの相対である。**リポジトリ直下に置く前提**で、
 * `discover()` が返すリポジトリ相対パスをそのまま書ける（書き換えを挟むと取り違える）。
 */
function buildSlnx(projects) {
  const esc = (v) =>
    String(v).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  const lines = ['<Solution>'];
  for (const p of projects) lines.push(`  <Project Path="${esc(p)}" />`);
  lines.push('</Solution>', '');
  return lines.join('\n');
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
  // 重みが等しいとき LPT は**ラウンドロビンに退化する**（同点は添字の小さいシャードから埋まる）。
  // 移行前の期待値をそのまま残しているのは、退化が起きていることの検査そのものである。
  ok('assign: 重みが等しければラウンドロビンに退化する', () => {
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

  // ---- ここから LPT 固有の性質（IADR-0257）。上の 4 件〔和・重複・of=1・範囲外〕は
  // 分配方式に依らない不変条件なので、重み付きでももう一度確かめる。 ----
  // 🔴 **パス昇順と重み降順を意図的にずらした重み表である。**
  // 最初 `{ a: 100, b: 60, …, g: 10 }`（パス順＝重み順）で書いたところ、
  // **重み降順のソートを削る変異が自己試験を素通りした**（実測）——
  // 並びが偶然一致していたため、パス昇順だけでも同じ分配になっていた。
  // **検査していないのに緑になる**形なので、順序が食い違う値へ組み替えてある。並びを戻さないこと。
  const W = { a: 10, b: 100, c: 40, d: 60, e: 20, f: 50, g: 30 }; // 合計 310
  const loadOf = (list, w) => list.reduce((n, x) => n + (w[x] || 1), 0);

  ok('assign(LPT): 重い順に積む（パス順に積むのでは通らない）', () => {
    // 重み降順は b(100) d(60) f(50) c(40) g(30) e(20) a(10)。3 シャードでの手順:
    // [0,0,0] b→S1[100] d→S2[60] f→S3[50] c→S3[90] g→S2[90]
    // e→ 最軽量が S2=90 と S3=90 の同点 → 添字の小さい S2[110] / a→S3[100]
    eq(assign(p, 1, 3, W), ['b']);
    eq(assign(p, 2, 3, W), ['d', 'g', 'e']);
    eq(assign(p, 3, 3, W), ['f', 'c', 'a']);
  });
  ok('assign(LPT): 各シャードの先頭は重い順に並ぶ（重い順に積んだ証跡）', () => {
    for (let sh = 1; sh <= 3; sh += 1) {
      const got = assign(p, sh, 3, W).map((x) => W[x]);
      const desc = [...got].sort((x, y) => y - x);
      eq(got, desc, `シャード ${sh} が重い順でない`);
    }
  });
  ok('assign(LPT): 重み付きでも和が全体と一致し、重複が無い', () => {
    const all = [1, 2, 3].flatMap((sh) => assign(p, sh, 3, W));
    eq([...all].sort(), [...p].sort(), '和');
    eq(all.length, new Set(all).size, '重複');
  });
  // 🔴 makespan の検査は「偏りに気付けるか」の本体である。
  // LPT の最悪保証は理想分配（＝ max(総和/シャード数, 最大の重み)）の (4/3 − 1/(3m)) 倍。
  ok('assign(LPT): makespan が LPT の最悪保証（4/3 − 1/3m）に収まる', () => {
    for (const of of [2, 3, 4, 5]) {
      const loads = [];
      for (let sh = 1; sh <= of; sh += 1) loads.push(loadOf(assign(p, sh, of, W), W));
      const total = loadOf(p, W);
      const ideal = Math.max(total / of, Math.max(...p.map((x) => W[x])));
      const bound = ideal * (4 / 3 - 1 / (3 * of));
      const makespan = Math.max(...loads);
      if (makespan > bound + 1e-9) {
        throw new Error(`of=${of}: makespan ${makespan} が保証 ${bound.toFixed(2)}（理想 ${ideal.toFixed(2)}）を超えた`);
      }
    }
  });
  // 🔴 **旧方式（ラウンドロビン）と直接比べる。** 「LPT にした」と言えるのは、
  // ラウンドロビンより makespan が小さくなる入力を実際に固定したときだけである。
  ok('assign(LPT): ラウンドロビンより makespan が小さい入力を固定する', () => {
    const q = ['a', 'b', 'c', 'd']; // パス順の重みは 10 / 100 / 40 / 60 = 210
    const roundRobin = (list, sh, of) => list.filter((_, i) => i % of === sh - 1);
    const lptMakespan = Math.max(loadOf(assign(q, 1, 2, W), W), loadOf(assign(q, 2, 2, W), W));
    const rrMakespan = Math.max(loadOf(roundRobin(q, 1, 2), W), loadOf(roundRobin(q, 2, 2), W));
    eq([lptMakespan, rrMakespan], [110, 160], 'LPT 110 / ラウンドロビン 160');
    if (!(lptMakespan < rrMakespan)) throw new Error('LPT がラウンドロビンを改善していない');
  });
  ok('assign(LPT): 決定的（入力の並び順を変えても結果が変わらない）', () => {
    const shuffled = ['g', 'c', 'a', 'f', 'd', 'b', 'e'];
    for (let sh = 1; sh <= 3; sh += 1) eq(assign(shuffled, sh, 3, W), assign(p, sh, 3, W), `シャード ${sh}`);
  });
  ok('assign(LPT): 同じ重みが並んでもパス昇順で安定する', () => {
    const flat = { a: 7, b: 7, c: 7, d: 7 };
    eq(assign(['d', 'c', 'b', 'a'], 1, 2, flat), ['a', 'c']);
    eq(assign(['d', 'c', 'b', 'a'], 2, 2, flat), ['b', 'd']);
  });
  // 🔴 fail-open の向き。重みが引けない・不正でも例外にせず 1 として扱う
  // （分配が偏るだけでテストは落ちない）。
  ok('assign(LPT): 未知・不正な重みは 1 として扱う（fail-open）', () => {
    const bad = { a: undefined, b: 0, c: -5, d: NaN, e: 'x' };
    const all = [1, 2].flatMap((sh) => assign(p, sh, 2, bad));
    eq([...all].sort(), [...p].sort(), '重みが壊れていても取りこぼさない');
    eq(assign(p, 1, 2, bad), assign(p, 1, 2), '全部 1 と同じ分配になる');
  });
  ok('assign(LPT): 巨大な 1 本が単独シャードを占め、残りが均される', () => {
    // ここもパス昇順（x, y, z, zbig）と重み降順（zbig, x, y, z）を食い違わせてある。
    const heavy = { zbig: 1000, x: 10, y: 10, z: 10 };
    const list = ['zbig', 'x', 'y', 'z'];
    eq(assign(list, 1, 3, heavy), ['zbig']);
    eq(assign(list, 2, 3, heavy), ['x', 'z']);
    eq(assign(list, 3, 3, heavy), ['y']);
  });

  // ---- 重みの走査（IADR-0257 決定 2: JSON へコミットせず毎回導く） ----
  ok('countTests: [Fact] / [Theory] の出現数を数える', () => {
    eq(countTests('[Fact]\npublic void A(){}\n[Theory]\n[InlineData(1)]\npublic void B(int i){}'), 2);
  });
  ok('countTests: 引数付きの属性も数える', () =>
    eq(countTests('[Fact(DisplayName = "x")]\n[Theory(Skip = "y")]'), 2));
  ok('countTests: テストでない属性は数えない', () => eq(countTests('[Trait("Category","Integration")]\n[Obsolete]'), 0));

  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'ltp-selftest-'));
  try {
    fs.mkdirSync(path.join(tmp, 'X', 'sub'), { recursive: true });
    fs.mkdirSync(path.join(tmp, 'X', 'obj'), { recursive: true });
    fs.mkdirSync(path.join(tmp, 'Y'), { recursive: true });
    fs.writeFileSync(path.join(tmp, 'X', 'X.csproj'), '<Project />');
    fs.writeFileSync(path.join(tmp, 'X', 'A.cs'), '[Fact]\n[Fact]\n[Theory]\n');
    fs.writeFileSync(path.join(tmp, 'X', 'sub', 'B.cs'), '[Fact]\n');
    fs.writeFileSync(path.join(tmp, 'X', 'obj', 'Gen.cs'), '[Fact]\n[Fact]\n[Fact]\n[Fact]\n[Fact]\n');
    fs.writeFileSync(path.join(tmp, 'Y', 'Y.csproj'), '<Project />');
    fs.writeFileSync(path.join(tmp, 'Y', 'C.cs'), '// テストが 1 件も無い\n');

    ok('weigh: プロジェクト配下の .cs を再帰的に数える', () =>
      eq(weigh(['X/X.csproj'], tmp).get('X/X.csproj'), 4));
    ok('weigh: bin / obj の生成物は数えない', () => {
      if (weigh(['X/X.csproj'], tmp).get('X/X.csproj') !== 4) throw new Error('obj/ を数えている');
    });
    ok('weigh: テスト 0 件でも下限 1（ホスト起動の固定費がある）', () =>
      eq(weigh(['Y/Y.csproj'], tmp).get('Y/Y.csproj'), 1));
    ok('weigh: 実在しないディレクトリは重み 1（fail-open。例外を投げない）', () =>
      eq(weigh(['Z/Z.csproj'], tmp).get('Z/Z.csproj'), 1));
    ok('weigh: 与えた全プロジェクトに重みが付く（黙って落とさない）', () =>
      eq(weigh(['X/X.csproj', 'Y/Y.csproj', 'Z/Z.csproj'], tmp).size, 3));
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }

  // ---- ビルド閉包・被覆・publish の担当（#1314 / IADR-0257 追記 2026-10-12） ----
  // 🔴 脚は担当テストの参照閉包だけをビルドする。壊れる形は 2 つ:
  // ①どの脚でもビルドされないプロジェクトが出る（コンパイルエラーが赤くならない）
  // ②publish が 0 回 / 2 回になる。両方を固定する。
  const t2 = fs.mkdtempSync(path.join(os.tmpdir(), 'ltp-closure-'));
  try {
    const put = (rel, body) => {
      fs.mkdirSync(path.join(t2, path.dirname(rel)), { recursive: true });
      fs.writeFileSync(path.join(t2, rel), body);
    };
    const ref = (...xs) => `<Project>${xs.map((x) => `<ProjectReference Include="${x}" />`).join('')}</Project>`;
    put(
      'backend/backend.slnx',
      '<Solution>' +
        ['Services/A/A.csproj', 'Services/A/Tests/A.Tests.csproj', 'Services/B/B.csproj',
          'Services/B/Tests/B.Tests.csproj', 'Shared/S/S.csproj', 'Lib/Orphan/Orphan.csproj']
          .map((x) => `<Project Path="${x}" />`).join('') +
        '<!-- <Project Path="Commented/Out.csproj" /> --></Solution>',
    );
    put('backend/Services/A/A.csproj', ref('..\\..\\Shared\\S\\S.csproj'));
    put('backend/Services/A/Tests/A.Tests.csproj', ref('../A.csproj'));
    // コメント中の参照は参照ではない（剥がさないと Orphan が閉包へ紛れ込み、①を見逃す）。
    put('backend/Services/B/B.csproj', '<Project><!-- <ProjectReference Include="../../Lib/Orphan/Orphan.csproj" /> --></Project>');
    put('backend/Services/B/Tests/B.Tests.csproj', ref('../B.csproj', '../../A/A.csproj'));
    put('backend/Shared/S/S.csproj', '<Project />');
    put('backend/Lib/Orphan/Orphan.csproj', '<Project />');
    const tests = ['backend/Services/A/Tests/A.Tests.csproj', 'backend/Services/B/Tests/B.Tests.csproj'];

    ok('projectRefs: 相対パス（\\ 区切りも）をリポジトリ相対へ正規化する', () =>
      eq(projectRefs('backend/Services/A/A.csproj', t2), ['backend/Shared/S/S.csproj']));
    ok('projectRefs: XML コメント中の参照は数えない', () => eq(projectRefs('backend/Services/B/B.csproj', t2), []));
    ok('projectRefs: $( を含む Include と Update= 形式は例外（fail-loud。黙って閉包を欠かせない）', () => {
      put('backend/X/Prop.csproj', '<Project><ProjectReference Include="$(RepoRoot)/a/A.csproj" /></Project>');
      put('backend/X/Upd.csproj', '<Project><ProjectReference Update="../a/A.csproj" /></Project>');
      for (const f of ['backend/X/Prop.csproj', 'backend/X/Upd.csproj']) {
        let threw = false;
        try {
          projectRefs(f, t2);
        } catch {
          threw = true;
        }
        if (!threw) throw new Error(`${f} が通ってしまった`);
      }
    });
    ok('projectRefs: 読めなければ空（被覆検査が落とす側へ倒す）', () => eq(projectRefs('nope/X.csproj', t2), []));
    ok('closure: 推移的に辿り、自身を含む', () =>
      eq(closure([tests[0]], t2), ['backend/Services/A/A.csproj', tests[0], 'backend/Shared/S/S.csproj']));
    ok('solutionProjects: solution の位置から解決し、コメントを除く', () => {
      const got = solutionProjects('backend/backend.slnx', t2);
      eq(got.length, 6);
      if (!got.includes('backend/Lib/Orphan/Orphan.csproj')) throw new Error('解決先が違う');
      if (got.some((x) => x.includes('Commented'))) throw new Error('コメントを拾った');
    });
    ok('solutionProjects: 読めなければ null（空配列で緑にしない）', () =>
      eq(solutionProjects('missing.slnx', t2), null));
    ok('isServiceProject: サービス本体だけを拾う（Tests は拾わない）', () => {
      eq(isServiceProject('backend/Services/A/A.csproj'), true);
      eq(isServiceProject('backend/Services/A/Tests/A.Tests.csproj'), false);
      eq(isServiceProject('backend/Shared/S/S.csproj'), false);
    });
    const plans = planShards(tests, 2, null, t2);
    ok('uncovered: どのテストからも参照されないプロジェクトを名指しする', () =>
      eq(uncovered(solutionProjects('backend/backend.slnx', t2), plans), ['backend/Lib/Orphan/Orphan.csproj']));
    ok('uncovered: 全部被覆されていれば空', () =>
      eq(uncovered(['backend/Services/A/A.csproj', 'backend/Shared/S/S.csproj'], plans), []));
    ok('publishTargets: 各サービスをちょうど 1 本の脚が担う（添字の小さい側が優先）', () => {
      // A は両方の閉包に入る（B.Tests が A を参照）。shard 1 が担い、shard 2 は担わない。
      eq(publishTargets(plans, 1), ['backend/Services/A/A.csproj']);
      eq(publishTargets(plans, 2), ['backend/Services/B/B.csproj']);
    });
    ok('publishTargets: 和は閉包に現れる全サービスと一致し、重複が無い', () => {
      const all = [1, 2].flatMap((s) => publishTargets(plans, s));
      eq(all.length, new Set(all).size, '重複');
      eq([...all].sort(), ['backend/Services/A/A.csproj', 'backend/Services/B/B.csproj'], '和');
    });
  } finally {
    fs.rmSync(t2, { recursive: true, force: true });
  }

  ok('buildSlnx: Solution 要素で包む', () => {
    const x = buildSlnx(['a/b.csproj']);
    if (!x.startsWith('<Solution>')) throw new Error('ルート要素が違う');
    if (!x.trimEnd().endsWith('</Solution>')) throw new Error('閉じ要素が無い');
  });
  ok('buildSlnx: 1 プロジェクト 1 行で、件数が保たれる', () => {
    const projs = ['a/x.csproj', 'b/y.csproj', 'c/z.csproj'];
    const n = buildSlnx(projs).split('\n').filter((l) => l.includes('<Project ')).length;
    eq(n, projs.length);
  });
  ok('buildSlnx: パスをそのまま書く（書き換えない）', () => {
    if (!buildSlnx(['backend/Services/X/tests/X.Tests/X.Tests.csproj']).includes(
      'Path="backend/Services/X/tests/X.Tests/X.Tests.csproj"')) throw new Error('パスが変わっている');
  });
  ok('buildSlnx: XML の特殊文字を escape する', () => {
    const x = buildSlnx(['a&b/"c".csproj']);
    if (x.includes('a&b')) throw new Error('& が escape されていない');
    if (!x.includes('&amp;') || !x.includes('&quot;')) throw new Error('escape が足りない');
  });
  ok('buildSlnx: 空でも壊れない（呼び出し側が 0 件を弾く前提）', () => {
    if (!buildSlnx([]).includes('<Solution>')) throw new Error('壊れている');
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

  // 🔴 重みは**毎回ここで走査して導く**（IADR-0257 決定 2）。JSON へ持たない。
  const weights = weigh(projects);

  if (argv.includes('--weights')) {
    for (const [proj, w] of [...weights].sort((a, b) => b[1] - a[1] || (a[0] < b[0] ? -1 : 1))) {
      console.log(`${w}\t${proj}`);
    }
    return;
  }

  const get = (flag) => {
    const i = argv.indexOf(flag);
    return i >= 0 ? Number(argv[i + 1]) : NaN;
  };
  const shard = get('--shard');
  const of = get('--of');
  if (!Number.isNaN(shard) || !Number.isNaN(of)) {
    let plans;
    try {
      plans = planShards(projects, of, weights);
    } catch (e) {
      console.error(`[list-test-projects] 参照閉包を求められない: ${e.message}`);
      process.exitCode = 1;
      return;
    }
    const picked = plans[shard - 1].tests;
    // 🔴 **被覆の検査（#1314）。** 脚は `backend.slnx` 全体ではなく担当テストの参照閉包だけをビルドする。
    // したがって「どのテストからも参照されないプロジェクト」は**どの脚でもビルドされない**
    // —— コンパイルエラーが入っても CI が赤くならない形である。黙って許さず、ここで落とす。
    // 対象は `backend.slnx` の全プロジェクトと、publish 乾式実行の対象（Services/*/*.csproj）。
    const inSlnx = solutionProjects(SOLUTION);
    if (inSlnx === null || inSlnx.length === 0) {
      console.error(`[list-test-projects] ${SOLUTION} を読めない（被覆を検査できない）。走査ルートを疑うこと。`);
      process.exitCode = 1;
      return;
    }
    const services = findCsproj(path.join(REPO_ROOT, 'backend', 'Services'))
      .map((p) => path.relative(REPO_ROOT, p).split(path.sep).join('/'))
      .filter(isServiceProject);
    const missing = uncovered([...new Set([...inSlnx, ...services])].sort(), plans);
    if (missing.length > 0) {
      console.error(
        `[list-test-projects] 次の ${missing.length} 件はどのシャードのビルド閉包にも入らない` +
          '（＝どの脚でもビルドされない）。テストプロジェクトから参照するか、' +
          'テストプロジェクトを足すこと（IADR-0257 追記 2026-10-12）:\n  ' +
          missing.join('\n  '),
      );
      process.exitCode = 1;
      return;
    }
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
    const slnxAt = argv.indexOf('--slnx');
    if (slnxAt >= 0) {
      const out = argv[slnxAt + 1];
      fs.writeFileSync(out, buildSlnx(picked));
      const load = picked.reduce((n, x) => n + weightOf(weights, x), 0);
      const total = [...weights.values()].reduce((n, x) => n + x, 0);
      // 分配の偏りは「遅い」以外の症状を出さないため、**毎 run で数字を残す**。
      console.error(
        `[list-test-projects] ${out} へ ${picked.length} 件（重み ${load} / 全体 ${total}` +
          ` = ${((100 * load) / total).toFixed(1)}%、理想 ${(100 / of).toFixed(1)}%）を書いた。` +
          `ビルド閉包 ${plans[shard - 1].closure.length} / ${inSlnx.length} プロジェクト。`,
      );
    }
    // 🔴 **ビルドは閉包を solution の直下へ並べた別の `.slnx` で行う**（#1314）。
    // テスト用の `.slnx`（担当テストだけ）をビルドすると、参照先は ProjectReference 経由の
    // 「参照としてのビルド」になり、Web SDK のサービス本体で静的 Web 資産のマニフェスト
    // （obj/.../staticwebassets.build.json）が作られず、`dotnet publish --no-build` が落ちる（実測）。
    // 閉包を直下へ並べれば、各プロジェクトは従来の `backend.slnx` と同じ「solution の項目」としてビルドされる。
    const buildAt = argv.indexOf('--build-slnx');
    if (buildAt >= 0) {
      fs.writeFileSync(argv[buildAt + 1], buildSlnx(plans[shard - 1].closure));
    }
    const pubAt = argv.indexOf('--publish-out');
    if (pubAt >= 0) {
      const targets = publishTargets(plans, shard);
      fs.writeFileSync(argv[pubAt + 1], targets.map((x) => `${x}\n`).join(''));
      console.error(`[list-test-projects] publish 乾式実行の担当 ${targets.length} / ${services.length} 件。`);
    }
    console.log(picked.join('\n'));
    return;
  }
  console.log(projects.join('\n'));
}

module.exports = {
  discover,
  assign,
  isTestProject,
  buildSlnx,
  weigh,
  countTests,
  weightOf,
  projectRefs,
  closure,
  isServiceProject,
  solutionProjects,
  planShards,
  publishTargets,
  uncovered,
};

if (require.main === module) main();
