#!/usr/bin/env node
'use strict';
/*
 * check-planning-adr-range.js
 * #717 / #710 / NFR: 週次バックログ監査（backlog-audit.yml）の項目 6「計画 ADR レンジ鮮度」を、
 * AI ではなく決定的なステップで解決する。外部依存ゼロ（gh CLI を子プロセスで呼ぶだけ）。
 *
 * 背景:
 *   監査項目 6 は `.claude/rules/traceability.repo.md` の宣言 `` `ADR-0001..NNNN` `` と、計画リポジトリ
 *   `projects/ai-stock-trading/07_adr/` の実在最大番号を突き合わせる。当初は AI が
 *   `mcp__github__get_file_contents` で計画リポジトリを読む設計だったが、`secrets.GITHUB_TOKEN` は
 *   本リポジトリしか読めず **404** になり、項目 6 は恒久的に「未確認」で終わっていた
 *   （2026-09-09・run 34303693213 の #483 §6）。
 *
 *   本スクリプトは Claude ステップの**前**に走り、`PLANNING_REPO_TOKEN`（cross-repo 読み取り用の secret。
 *   #496 で PR CI から使えることが実測されている）で計画側の一覧を取り、結果 JSON をファイルへ書く。
 *   AI はそのファイルを読んで報告するだけになる（cross-repo の資格情報を AI に持たせない）。
 *
 * fail-open の設計:
 *   secret が無い／API が失敗した／宣言が読めない、のいずれでも **exit 0** で `status: "unverified"` と
 *   理由を書く。項目 6 の検証不能で監査の他 5 項目を巻き込まない（産出検証 check-backlog-audit-output.js
 *   が「産出そのもの」を守る）。宣言と実在の食い違いは `status: "behind" | "ahead"` として書き、
 *   CI アノテーション（warning）にも出す——判断（レンジ宣言の更新）は人間／後続 PR に残す。
 *
 * 使い方:
 *   node scripts/check-planning-adr-range.js --out <path.json>
 *   node scripts/check-planning-adr-range.js --self-test
 *
 * gh は `GH_TOKEN` を環境変数から読む。本スクリプトは env `PLANNING_REPO_TOKEN` を子プロセスの
 * `GH_TOKEN` へ写す（既定の `GITHUB_TOKEN` は使わない——それでは 404 になることが実測済み）。
 */
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
const { emit } = require('./lib/ci-annotate.js');
const { readPlanAdrRange } = require('./lib/plan-ranges.js');

const DEFAULT_OWNER = 'endazon';
const DEFAULT_REPO = 'project-planning';
const DEFAULT_DIR = 'projects/ai-stock-trading/07_adr';
const ADR_FILE_RE = /^ADR-(\d{4})_/;

function parseArgs(argv) {
  const a = { selfTest: false, out: null };
  for (let i = 0; i < argv.length; i++) {
    const t = argv[i];
    if (t === '--self-test') a.selfTest = true;
    else if (t === '--out') a.out = argv[++i];
    else if (t.startsWith('--out=')) a.out = t.slice('--out='.length);
  }
  return a;
}

/**
 * 計画リポジトリの ADR ディレクトリ一覧（ファイル名の配列）を gh api で取る。
 * `execFn` は差し替え可能（テストで gh を呼ばずに済ませる）。token が無ければ例外。
 */
function fetchPlanningAdrNames({ owner = DEFAULT_OWNER, repo = DEFAULT_REPO, dir = DEFAULT_DIR, token, execFn = execFileSync } = {}) {
  if (!token) throw new Error('PLANNING_REPO_TOKEN が渡されていない（secret 不在。B-3）');
  const out = execFn('gh', ['api', `repos/${owner}/${repo}/contents/${dir}`], {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, GH_TOKEN: token, GITHUB_TOKEN: token },
  });
  const entries = JSON.parse(out);
  if (!Array.isArray(entries)) throw new Error('contents API の応答が配列ではない（ディレクトリではなくファイルを指している可能性）');
  return entries.map((e) => (e && typeof e.name === 'string' ? e.name : '')).filter(Boolean);
}

/** ファイル名の配列から実在する最大 ADR 番号を返す。1 件も無ければ null。 */
function maxAdrNumber(names) {
  let max = null;
  for (const n of names) {
    const m = ADR_FILE_RE.exec(n);
    if (!m) continue;
    const v = Number(m[1]);
    if (max === null || v > max) max = v;
  }
  return max;
}

/**
 * 判定の中核（純関数）。
 * @returns {{status: 'ok'|'behind'|'ahead'|'unverified', declaredMax: number|null, planningMax: number|null, reason: string}}
 */
function verdict({ declaredMax, planningMax, reason = '' }) {
  if (declaredMax === null || planningMax === null) {
    return { status: 'unverified', declaredMax, planningMax, reason: reason || '宣言または実在の一方が取得できない' };
  }
  if (planningMax > declaredMax) {
    return {
      status: 'behind',
      declaredMax,
      planningMax,
      reason: `宣言 ADR-0001..${pad(declaredMax)} が計画側の実在 ${pad(planningMax)} に ${planningMax - declaredMax} 件遅れている（レンジ宣言の更新漏れ。#710 の再発）`,
    };
  }
  if (planningMax < declaredMax) {
    return {
      status: 'ahead',
      declaredMax,
      planningMax,
      reason: `宣言 ADR-0001..${pad(declaredMax)} が計画側の実在 ${pad(planningMax)} を超えている（計画側で ADR が消えたか、宣言が先走っている）`,
    };
  }
  return { status: 'ok', declaredMax, planningMax, reason: `宣言と実在が一致（ADR-0001..${pad(declaredMax)}）` };
}

function pad(n) {
  return String(n).padStart(4, '0');
}

/**
 * 全体の結合点。宣言の読み取り・計画側の取得のどちらが失敗しても例外を投げず unverified を返す。
 * @param {{token?: string, readRangeFn?: Function, fetchFn?: Function}} deps
 */
function resolve({ token, readRangeFn = readPlanAdrRange, fetchFn = fetchPlanningAdrNames } = {}) {
  let declaredMax = null;
  const reasons = [];
  try {
    declaredMax = readRangeFn().to;
  } catch (e) {
    reasons.push(`宣言を読めない: ${e.message || e}`);
  }
  let planningMax = null;
  try {
    const names = fetchFn({ token });
    planningMax = maxAdrNumber(names);
    if (planningMax === null) reasons.push('計画側のディレクトリに ADR-NNNN_ 形式のファイルが 1 件も無い');
  } catch (e) {
    reasons.push(`計画側を取得できない: ${e.message || e}`);
  }
  const v = verdict({ declaredMax, planningMax, reason: reasons.join('。') });
  return { ...v, checkedAt: new Date().toISOString(), source: `${DEFAULT_OWNER}/${DEFAULT_REPO}/${DEFAULT_DIR}` };
}

function selfTest() {
  const names = ['README.md', 'ADR-0001_a.md', 'ADR-0035_b.md', 'ADR-0012_c.md', 'notes.txt'];
  const cases = [
    {
      name: 'maxAdrNumber: ADR-NNNN_ 形式だけを拾い最大を返す',
      run: () => maxAdrNumber(names),
      expect: (r) => r === 35,
    },
    {
      name: 'maxAdrNumber: 該当なしは null',
      run: () => maxAdrNumber(['README.md']),
      expect: (r) => r === null,
    },
    {
      name: '一致なら ok',
      run: () => resolve({ token: 't', readRangeFn: () => ({ from: 1, to: 35 }), fetchFn: () => names }),
      expect: (r) => r.status === 'ok' && r.declaredMax === 35 && r.planningMax === 35,
    },
    {
      name: '宣言が遅れていれば behind（#710 の再発形）',
      run: () => resolve({ token: 't', readRangeFn: () => ({ from: 1, to: 32 }), fetchFn: () => names }),
      expect: (r) => r.status === 'behind' && /3 件遅れている/.test(r.reason),
    },
    {
      name: '宣言が先走っていれば ahead',
      run: () => resolve({ token: 't', readRangeFn: () => ({ from: 1, to: 36 }), fetchFn: () => names }),
      expect: (r) => r.status === 'ahead',
    },
    {
      name: 'secret 不在は unverified（exit させない・理由を書く）',
      run: () => resolve({ token: '', readRangeFn: () => ({ from: 1, to: 35 }) }),
      expect: (r) => r.status === 'unverified' && /secret 不在/.test(r.reason) && r.declaredMax === 35,
    },
    {
      name: 'gh 失敗（404 等）は unverified に理由を残す',
      run: () => resolve({ token: 't', readRangeFn: () => ({ from: 1, to: 35 }), fetchFn: () => { throw new Error('gh: HTTP 404: Not Found'); } }),
      expect: (r) => r.status === 'unverified' && /404/.test(r.reason),
    },
    {
      name: '宣言が読めなくても unverified（例外で落とさない）',
      run: () => resolve({ token: 't', readRangeFn: () => { throw new Error('節が無い'); }, fetchFn: () => names }),
      expect: (r) => r.status === 'unverified' && /宣言を読めない/.test(r.reason) && r.planningMax === 35,
    },
    {
      name: 'fetchPlanningAdrNames: gh api の JSON をファイル名配列にし、GH_TOKEN を子プロセスへ写す',
      run: () => {
        let seenEnv = null;
        const r = fetchPlanningAdrNames({
          token: 'secret-x',
          execFn: (cmd, args, opts) => {
            seenEnv = opts.env;
            if (cmd !== 'gh' || args[0] !== 'api') throw new Error('gh api 以外を呼んだ');
            return JSON.stringify([{ name: 'ADR-0001_a.md', type: 'file' }, { name: 'README.md', type: 'file' }]);
          },
        });
        return { r, tokenPassed: seenEnv && seenEnv.GH_TOKEN === 'secret-x' };
      },
      expect: (x) => x.tokenPassed === true && x.r.length === 2 && x.r[0] === 'ADR-0001_a.md',
    },
  ];
  let failed = 0;
  for (const c of cases) {
    let got;
    try {
      got = c.run();
    } catch (e) {
      failed++;
      process.stderr.write(`  NG  ${c.name}\n      例外: ${e.message}\n`);
      continue;
    }
    if (c.expect(got)) process.stdout.write(`  ok  ${c.name}\n`);
    else {
      failed++;
      process.stderr.write(`  NG  ${c.name}\n      got=${JSON.stringify(got)}\n`);
    }
  }
  if (failed) {
    process.stderr.write(`\n✗ 検証器の自己試験が ${failed} 件失敗した\n`);
    return 1;
  }
  process.stdout.write(`✓ 検証器の自己試験 ${cases.length} 件すべて合格\n`);
  return 0;
}

function main(argv) {
  const args = parseArgs(argv.slice(2));
  if (args.selfTest) process.exit(selfTest());
  if (!args.out) {
    process.stderr.write('usage: check-planning-adr-range.js --out <path.json> | --self-test\n');
    process.exit(2);
  }
  const result = resolve({ token: process.env.PLANNING_REPO_TOKEN });
  fs.mkdirSync(path.dirname(path.resolve(args.out)), { recursive: true });
  fs.writeFileSync(args.out, `${JSON.stringify(result, null, 2)}\n`, 'utf8');
  const line = `計画 ADR レンジ鮮度: ${result.status} — ${result.reason}`;
  if (result.status === 'behind' || result.status === 'ahead') {
    emit('warning', line, { stream: process.stderr, prefix: '  warning  ' });
  } else if (result.status === 'unverified') {
    emit('warning', `${line}（監査項目 6 は「未確認」として報告される）`, { stream: process.stderr, prefix: '  warning  ' });
  } else {
    process.stdout.write(`✓ ${line}\n`);
  }
  process.stdout.write(`結果を書いた: ${args.out}\n`);
  process.exit(0);
}

if (require.main === module) {
  main(process.argv);
}

module.exports = { fetchPlanningAdrNames, maxAdrNumber, verdict, resolve, selfTest, DEFAULT_DIR };
