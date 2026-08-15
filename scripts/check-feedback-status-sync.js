#!/usr/bin/env node
'use strict';
/*
 * check-feedback-status-sync.js
 * 環流記録の `status` が計画側の裁定に追随しているかの機械検査。
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * ■ なぜ要るか
 *   `status` は **計画側の裁定がどこまで進んだか** を表す（伝達したかは `dispatched:` が担う。
 *   裁定 planning#323）。**値の正は計画側にある**のに、実装側の記録は人手でしか追随せず、
 *   **`feedback/README.md` 自身が「この語彙を検査する機械は無い。値の誤りは沈黙する」と
 *   明記していた**。その沈黙が実際に 2 回起きている（planning#337）。
 *
 *     1. endazon/ai-stock-trading#477 —— 実装側 18 件が open、計画側は accepted
 *     2. endazon/microservices-platform#737 —— 10 件。`dispatched` の軸だけを確かめ、
 *        新しい `status` の軸（計画側が既に裁定していないか）を確かめなかった
 *
 *   **同型の事故が 2 回起きたため機械化する**（1 回目は記録に留める、が原則である）。
 *
 * ■ 何を見るか
 *   計画リポジトリの環流ディレクトリに **同名の写しを持つ**記録について、**frontmatter の
 *   `status` が一致している**こと。**ただ 1 点だけを見る。**
 *
 * ■ 何を見ないか（正直に書く）
 *   - **写しを持たない記録** —— **記録ファイル経路と GitHub Issue 経路は等価**であり
 *     （planning#319）、**写しの不在は伝達漏れを意味しない**。不一致に数えると恒久的な
 *     偽陽性が残る。
 *   - **`status` 以外の内容差分** —— 実装側はトリアージ結果節を持たない等、正当な差がある。
 *   - **値そのものの妥当性** —— 語彙外の値・意味の取り違えは、**両側が同じ値なら素通りする**。
 *     一致を見る検査であって、正しさを見る検査ではない。
 *
 * ■ fail-open / fail-closed の境目
 *   計画リポジトリを参照できないときは **skip（exit 0）**（`check-doc-links.js` と同じ扱い）。
 *   **ただし 0 件走査では緑にしない** —— 参照できているのに対象が 0 件なら **fail** する。
 *
 *   🔴 **`--require-planning` を付けると、参照できないときは skip ではなく fail になる。**
 *   **CI では必ずこれを付ける。** 計画リポを取得するはずのジョブで取得に失敗したとき、
 *   fail-open のままだと**「配線したのに一度も検査していない」状態が緑で固定される**
 *   （planning#343）。**フラグが無いと、各リポジトリが個別に気付いてジョブ側で塞ぐしかない。**
 *
 * ■ 未知の引数は受け付けない（planning#343）
 *   **知らないフラグを黙って無視してはならない。** 無視すると「CI は渡し続けているのに
 *   効いていない」状態が生まれ、**`run:` 行に文字列が在ることしか見ていない回帰テストは
 *   それを検出できない。** 未知の引数は設定誤りとして落とす。
 *
 * 使い方:
 *   node scripts/check-feedback-status-sync.js [--require-planning] [--self-test]
 *
 * 環境変数:
 *   PLANNING_FEEDBACK_DIR   計画側の環流ディレクトリを明示する（既定の探索順を上書きする）
 */

const fs = require('fs');
const path = require('path');

const REPO = path.join(__dirname, '..');

// 【置換点】本リポジトリの環流記録ディレクトリ（`check-feedback-dispatched.js` の `FEEDBACK_DIR` と揃える）。
const IMPL_DIR = path.join(REPO, 'feedback');

// 【置換点】計画リポジトリ側の環流ディレクトリの探索順（submodule / 隣接クローンの両方を見る）。
const PLAN_CANDIDATES = ['planning/draft/feedback', '../project-planning/draft/feedback'];

// 記録ではないもの（雛形と索引）。
const NOT_A_RECORD = new Set(['README.md', 'TEMPLATE.md']);

/** 計画側の環流ディレクトリを解決する。見つからなければ null（＝ skip）。 */
function resolvePlanDir(repo = REPO, env = process.env) {
  if (env.PLANNING_FEEDBACK_DIR) {
    return fs.existsSync(env.PLANNING_FEEDBACK_DIR) ? env.PLANNING_FEEDBACK_DIR : null;
  }
  for (const rel of PLAN_CANDIDATES) {
    const p = path.resolve(repo, rel);
    if (fs.existsSync(p)) return p;
  }
  return null;
}

/** frontmatter の 1 階層目の鍵を読む（YAML パーサは持ち込まない）。 */
function frontmatterValue(text, key) {
  const m = text.match(new RegExp(`^${key}:\\s*(.*)$`, 'm'));
  return m ? m[1].trim() : null;
}

/**
 * 実装側と計画側の 2 ディレクトリを突合する。
 * **経路を引数に取る**ので fixture で駆動できる —— これが無いと、実データが全件同期している
 * 間は比較演算子を取り違えても緑のままになる（**計画リポが未 populate な CI でも実効する**）。
 */
function compare(implDir, planDir) {
  const records = fs
    .readdirSync(implDir)
    .filter((f) => f.endsWith('.md') && !NOT_A_RECORD.has(f))
    .sort();

  const errors = [];

  // ★ 0 件走査で静かに緑にしない。
  if (records.length === 0) errors.push('環流記録が 0 件だった。走査が空振りしている');

  let compared = 0;
  let unpaired = 0;

  for (const f of records) {
    const planPath = path.join(planDir, f);
    if (!fs.existsSync(planPath)) {
      // 写しを持たない記録は検査対象外（Issue 経路・別日付での収録など）。
      unpaired += 1;
      continue;
    }
    compared += 1;

    const implStatus = frontmatterValue(fs.readFileSync(path.join(implDir, f), 'utf8'), 'status');
    const planStatus = frontmatterValue(fs.readFileSync(planPath, 'utf8'), 'status');

    if (implStatus !== planStatus) {
      errors.push(
        `[stale] ${f}: 実装側 status=${implStatus} / 計画側 status=${planStatus}。` +
          '`status` は計画側の裁定を表す（planning#323）ので、計画側へ追随させること',
      );
    }
  }

  // ★ 突合できた件数が 0 なら、検査が実質何も見ていない。
  if (records.length > 0 && compared === 0) {
    errors.push('計画側に写しを持つ記録が 0 件だった。検査が実質何も見ていない');
  }

  return { errors, records, compared, unpaired };
}

function selfTest() {
  const os = require('os');
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });

  /** fixture を組む。impl / plan は { ファイル名: status（null なら status 鍵ごと省く） }。 */
  const build = (impl, plan) => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'fbstatus-selftest-'));
    const implDir = path.join(dir, 'impl');
    const planDir = path.join(dir, 'plan');
    fs.mkdirSync(implDir);
    fs.mkdirSync(planDir);
    const write = (d, spec) => {
      for (const [name, status] of Object.entries(spec)) {
        const fm = status === null ? '' : `status: ${status}\n`;
        fs.writeFileSync(path.join(d, name), `---\ntitle: x\n${fm}---\n\n本文\n`);
      }
    };
    write(implDir, impl);
    write(planDir, plan);
    return { implDir, planDir };
  };

  // --- 追随漏れの本体（比較ロジックを食い違うデータで駆動する） ---
  {
    const { implDir, planDir } = build({ 'a.md': 'open' }, { 'a.md': 'accepted' });
    const r = compare(implDir, planDir);
    t('status が食い違えば [stale] を 1 件返す', r.errors.length === 1 && r.errors[0].includes('[stale] a.md'), r.errors);
    t(
      '双方の値をメッセージに載せる',
      r.errors[0] && r.errors[0].includes('実装側 status=open') && r.errors[0].includes('計画側 status=accepted'),
      r.errors[0],
    );
  }
  {
    const { implDir, planDir } = build({ 'a.md': 'accepted' }, { 'a.md': 'accepted' });
    t('裏: 一致していれば違反 0 件', compare(implDir, planDir).errors.length === 0);
  }
  {
    // **鍵が無い側**（片方だけ status を持たない）も食い違いである。
    const { implDir, planDir } = build({ 'a.md': null }, { 'a.md': 'accepted' });
    t('片側に status 鍵が無ければ食い違いとして検出', compare(implDir, planDir).errors.length === 1);
  }

  // --- 0 件走査の門 ---
  {
    const { implDir, planDir } = build({}, { 'a.md': 'accepted' });
    const r = compare(implDir, planDir);
    t('★ 記録 0 件なら門が発火', r.errors.some((e) => e.includes('記録が 0 件だった')), r.errors);
  }
  {
    const { implDir, planDir } = build({ 'a.md': 'open' }, { 'b.md': 'accepted' });
    const r = compare(implDir, planDir);
    t('★ 写しが 1 件も無ければ門が発火', r.errors.some((e) => e.includes('写しを持つ記録が 0 件だった')), r.errors);
  }

  // --- 写しを持たない記録を不一致に数えない（偽陽性を作らない） ---
  {
    const { implDir, planDir } = build({ 'paired.md': 'accepted', 'unpaired.md': 'open' }, { 'paired.md': 'accepted' });
    const r = compare(implDir, planDir);
    t('写しを持たない記録は違反にしない', r.errors.length === 0, r.errors);
    t('突合 1 件 / 対象外 1 件として数える', r.compared === 1 && r.unpaired === 1, {
      compared: r.compared,
      unpaired: r.unpaired,
    });
  }

  // --- 索引と雛形は記録ではない ---
  {
    const { implDir, planDir } = build(
      { 'README.md': 'open', 'TEMPLATE.md': 'open', 'a.md': 'accepted' },
      { 'README.md': 'accepted', 'TEMPLATE.md': 'accepted', 'a.md': 'accepted' },
    );
    const r = compare(implDir, planDir);
    t('索引・雛形が食い違っても違反にしない', r.errors.length === 0, r.errors);
    t('索引・雛形を記録として数えない', r.records.length === 1 && r.compared === 1, {
      records: r.records,
      compared: r.compared,
    });
  }

  // --- 参照先の解決 ---
  t('計画側を参照できなければ null（skip へ倒す）', resolvePlanDir(REPO, { PLANNING_FEEDBACK_DIR: '/no/such/dir' }) === null);

  // --- planning#343: fail-open を閉じる手段と、未知の引数の扱い ---
  //
  // **配線を見るテスト（`run:` 行に文字列が在るか）では捕まらない**ため、挙動を固定する。
  t('★ --require-planning を認識する（黙って無視しない）', parseArgs(['node', 'x', '--require-planning']).requirePlanning === true);
  t('★ 未知の引数は unknown へ入る', parseArgs(['node', 'x', '--requre-planning']).unknown.join() === '--requre-planning');
  t('★ 未知の引数を渡すと exit 1', main(['node', 'x', '--requre-planning']) === 1);
  {
    const saved = process.env.PLANNING_FEEDBACK_DIR;
    process.env.PLANNING_FEEDBACK_DIR = '/no/such/dir';
    try {
      t('★ 参照できないとき、フラグ無しは skip（exit 0）', main(['node', 'x']) === 0);
      t('★ 参照できないとき、--require-planning なら fail（exit 1）', main(['node', 'x', '--require-planning']) === 1);
    } finally {
      if (saved === undefined) delete process.env.PLANNING_FEEDBACK_DIR;
      else process.env.PLANNING_FEEDBACK_DIR = saved;
    }
  }

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) {
      failed++;
      if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual));
    }
  }
  if (failed) {
    console.error(`[check-feedback-status-sync] 自己試験 ${failed} 件 失敗。`);
    return 1;
  }
  console.log(`[check-feedback-status-sync] 自己試験 ${cases.length} 件 all passed。`);
  return 0;
}

/** 受け付ける引数（ここに無いものは設定誤りとして落とす。planning#343）。 */
const KNOWN_FLAGS = ['--require-planning', '--self-test'];

/** 引数を解釈する。**未知のフラグは黙って無視せず、呼び出し側へ返す。** */
function parseArgs(argv) {
  const args = argv.slice(2);
  return {
    requirePlanning: args.includes('--require-planning'),
    selfTest: args.includes('--self-test'),
    unknown: args.filter((a) => !KNOWN_FLAGS.includes(a)),
  };
}

function main(argv = process.argv) {
  const { requirePlanning, selfTest: wantSelfTest, unknown } = parseArgs(argv);
  if (unknown.length) {
    console.error(
      `[check-feedback-status-sync] 未知の引数: ${unknown.join(' ')}\n` +
        `  受け付けるのは ${KNOWN_FLAGS.join(' / ')} である。` +
        '黙って無視すると、CI が渡し続けているフラグが効いていないことに誰も気付けない。',
    );
    return 1;
  }
  if (wantSelfTest) return selfTest();

  const planDir = resolvePlanDir();
  if (!planDir) {
    const where = PLAN_CANDIDATES.join(' / ');
    if (requirePlanning) {
      console.error(
        `[check-feedback-status-sync] 計画リポジトリを参照できない（探した先: ${where}）。` +
          '--require-planning が指定されているため fail する —— ' +
          '取得するはずのジョブで取得できていない。skip して緑にしてはならない。',
      );
      return 1;
    }
    console.log(
      `  warn  [check-feedback-status-sync] 計画リポジトリを参照できないため skip した（探した先: ${where}）。` +
        'この範囲は検査されていない。',
    );
    return 0;
  }
  if (!fs.existsSync(IMPL_DIR)) {
    console.log('  warn  [check-feedback-status-sync] 本リポに feedback/ が無いため skip した。');
    return 0;
  }

  const { errors, records, compared, unpaired } = compare(IMPL_DIR, planDir);

  if (errors.length > 0) {
    console.error(`[check-feedback-status-sync] status の追随漏れ ${errors.length} 件を検出しました:`);
    for (const e of errors) console.error(`    ${e}`);
    return 1;
  }

  console.log(
    `[check-feedback-status-sync] OK: 記録 ${records.length} 件のうち ${compared} 件を計画側と突合しました` +
      `（写しを持たない ${unpaired} 件は対象外 —— 記録ファイル経路と Issue 経路は等価である）。`,
  );
  return 0;
}

if (require.main === module) process.exit(main());
module.exports = { main, compare, resolvePlanDir, frontmatterValue, parseArgs, selfTest, PLAN_CANDIDATES, KNOWN_FLAGS };
