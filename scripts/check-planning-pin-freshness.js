#!/usr/bin/env node
'use strict';
/*
 * check-planning-pin-freshness.js — 計画リポジトリの pin が古いことをセッション開始時に知らせる。
 *
 * ■ なぜ要るか
 *   計画側で裁定が反映されても、**本リポの pin を進めるまで実装側には何も伝わらない。**
 *   実装リポジトリでの実測では、**待ち時間の実体は「回答待ち」ではなく「回答に気づいていない
 *   時間」だった**（planning#337 / 環流元は endazon/microservices-platform#589）。同型は複数回
 *   「人が気づいて起票」で処理されている —— **自動化すべきはその手作業のほうである。**
 *
 * ■ 方針
 *   - **落とさない（fail-open）。常に exit 0 で終わる。** pin を進める判断は人・AI が行う。
 *     赤にすると pin が古い間ずっと CI が止まる。通知手段は「赤」ではなく警告である。
 *   - **「検査していない」と「古くない」を読み分けられるようにする。** 計画リポを参照できない
 *     ときはその旨を出して終わる。**黙って緑を返さない。**
 *   - **既定はオフラインで完結する**（pin されたコミットの日付を見るだけ）。`--fetch` を付けた
 *     ときだけ既定ブランチとの差（何コミット遅れているか）も取りに行く。
 *
 * ■ 何を見ないか
 *   **その差分が着手可否に効くかは見ない。** 「どの ADR が Accepted になったら着手してよいか」は
 *   リポジトリごとの規約であり、キットでは判定できない。ここが要るリポジトリは、本スクリプトを
 *   土台に分類規則を足すこと（固有デルタとして分類表へ記録する）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 使い方:
 *   node scripts/check-planning-pin-freshness.js [--fetch] [--self-test]
 *
 * 環境変数:
 *   PLANNING_PIN_MAX_AGE_DAYS   警告を出す日数のしきい値（既定 14）
 */

const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
const { warn } = require('./lib/ci-annotate.js');

const REPO_ROOT = path.join(__dirname, '..');

// 【置換点】計画リポジトリの場所（submodule 名、または隣接クローンへの相対パス）。
const PLANNING_CANDIDATES = ['planning', '../project-planning'];

// 【置換点】これを超えたら警告する日数。**0 にはしない**（毎回鳴ると読まれなくなる）。
const DEFAULT_MAX_AGE_DAYS = 14;

/** 計画リポジトリの場所を解決する。populate 済みの git 作業ツリーでなければ null。 */
function resolvePlanning(repo = REPO_ROOT, candidates = PLANNING_CANDIDATES) {
  for (const rel of candidates) {
    const p = path.resolve(repo, rel);
    // submodule は未 populate でも空ディレクトリとして存在するため、`.git` の実在で判定する。
    if (fs.existsSync(path.join(p, '.git'))) return p;
  }
  return null;
}

/** pin されたコミットの日時を epoch 秒で返す。取れなければ null（fail-open）。 */
function pinnedCommitDate(dir, run = git) {
  const out = run(dir, ['log', '-1', '--format=%ct']);
  if (out === null) return null;
  const n = Number(out.trim());
  return Number.isFinite(n) && n > 0 ? n : null;
}

/** git を実行して stdout を返す。失敗（未インストール・非 git・権限）は null（fail-open）。 */
function git(dir, args) {
  try {
    return execFileSync('git', ['-C', dir, ...args], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
  } catch (e) {
    return null;
  }
}

/**
 * 鮮度を判定する。**純関数**（時刻と pin 日時を受け取るだけ）。
 *
 * @param {number|null} pinnedEpoch pin されたコミットの epoch 秒
 * @param {number} nowEpoch 現在時刻の epoch 秒
 * @param {number} maxAgeDays しきい値
 * @returns {{state:'unknown'|'fresh'|'stale', ageDays:number|null}}
 */
function freshness(pinnedEpoch, nowEpoch, maxAgeDays) {
  if (!pinnedEpoch) return { state: 'unknown', ageDays: null };
  const ageDays = Math.floor((nowEpoch - pinnedEpoch) / 86400);
  return { state: ageDays > maxAgeDays ? 'stale' : 'fresh', ageDays };
}

function main(argv = process.argv) {
  const doFetch = argv.includes('--fetch');
  const maxAgeDays = Number(process.env.PLANNING_PIN_MAX_AGE_DAYS) || DEFAULT_MAX_AGE_DAYS;

  const dir = resolvePlanning();
  if (!dir) {
    console.log(
      '[check-planning-pin-freshness] 計画リポジトリを参照できないため skip した（探した先: ' +
        `${PLANNING_CANDIDATES.join(' / ')}）。**pin の鮮度は検査されていない。**`,
    );
    return 0;
  }

  const { state, ageDays } = freshness(pinnedCommitDate(dir), Math.floor(Date.now() / 1000), maxAgeDays);
  if (state === 'unknown') {
    console.log('[check-planning-pin-freshness] pin のコミット日時を取得できなかったため skip した。');
    return 0;
  }

  let behind = '';
  if (doFetch && git(dir, ['fetch', '--quiet', 'origin']) !== null) {
    const head = git(dir, ['symbolic-ref', '--quiet', '--short', 'refs/remotes/origin/HEAD']);
    const base = head ? head.trim() : 'origin/HEAD';
    const count = git(dir, ['rev-list', '--count', `HEAD..${base}`]);
    if (count !== null) behind = `（既定ブランチより ${count.trim()} コミット遅れ）`;
  }

  if (state === 'stale') {
    warn(
      `[check-planning-pin-freshness] 計画 pin が ${ageDays} 日前のコミットである${behind}。` +
        'この間に下りた裁定は実装側から見えていない。**着手前に pin を進めること。**',
    );
    return 0; // 判断は人が行う。ここでは落とさない。
  }

  console.log(`[check-planning-pin-freshness] OK: 計画 pin は ${ageDays} 日前のコミットである${behind}。`);
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
  const DAY = 86400;
  const now = 1_700_000_000;

  T('しきい値以内なら fresh', () => {
    assert.deepStrictEqual(freshness(now - 3 * DAY, now, 14), { state: 'fresh', ageDays: 3 });
  });

  T('しきい値ちょうどは fresh（境界は鳴らさない）', () => {
    assert.strictEqual(freshness(now - 14 * DAY, now, 14).state, 'fresh');
  });

  T('しきい値を 1 日超えたら stale', () => {
    assert.strictEqual(freshness(now - 15 * DAY, now, 14).state, 'stale');
  });

  T('pin 日時が取れなければ unknown（黙って fresh にしない）', () => {
    assert.deepStrictEqual(freshness(null, now, 14), { state: 'unknown', ageDays: null });
  });

  T('計画リポが無ければ null を返す（skip へ倒す）', () => {
    assert.strictEqual(resolvePlanning(REPO_ROOT, ['__no_such_planning__']), null);
  });

  T('git が失敗しても例外を投げず null を返す（fail-open）', () => {
    assert.strictEqual(git('/no/such/dir', ['log', '-1']), null);
    assert.strictEqual(pinnedCommitDate('/no/such/dir'), null);
  });

  T('★ 常に exit 0 で終わる（この検査で CI を止めない）', () => {
    assert.strictEqual(main(['node', 'x']), 0);
  });

  console.log(`[check-planning-pin-freshness] self-test OK（${n} 件）`);
}

if (require.main === module) {
  if (process.argv.includes('--self-test')) {
    selfTest();
    process.exit(0);
  }
  process.exit(main());
}

module.exports = { main, freshness, resolvePlanning, pinnedCommitDate, selfTest, PLANNING_CANDIDATES };
