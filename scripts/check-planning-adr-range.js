#!/usr/bin/env node
'use strict';
/*
 * check-planning-adr-range.js
 * NFR / #717 / #710 / planning#591 Q2: 本リポジトリが宣言する計画 ID レンジと、計画リポジトリが
 * 公開する実物の導出結果を突き合わせる。外部依存ゼロ（gh CLI を子プロセスで呼ぶだけ）。
 *
 * 背景（#717）:
 *   週次バックログ監査（backlog-audit.yml）の項目 6「計画 ADR レンジ鮮度」は、当初 AI が
 *   `mcp__github__get_file_contents` で計画リポジトリを読む設計だったが、`secrets.GITHUB_TOKEN` は
 *   本リポジトリしか読めず **404** になり、項目 6 は恒久的に「未確認」で終わっていた
 *   （2026-09-09・run 34303693213 の #483 §6）。本スクリプトは Claude ステップの**前**に走り、
 *   `PLANNING_REPO_TOKEN`（cross-repo 読み取り用の secret）で計画側を取り、結果 JSON を書く。
 *   AI はそのファイルを読んで報告するだけになる（cross-repo の資格情報を AI に持たせない）。
 *
 * 出典の変更（planning#591 Q2・計画 ADR-0093 決定 1・2026-09-09）:
 *   従前は計画リポの `07_adr/` ディレクトリ一覧を取り、**ADR の最大番号だけ**を突き合わせていた。
 *   計画側が `tools/doc-checks/kg-ranges.json` を「実物から導出して公開する成果物」へ格上げした
 *   （手で書かず `gen-plan-ranges.js --write` で揃え、`--check` を CI の必須チェックに置いた）ため、
 *   本スクリプトはその**公開ファイルを 1 回取得し、FR / UC / SC / ADR の 4 種すべて**を突き合わせる。
 *   🔴 **これは計画 ADR-0093 決定 3 が範囲を 4 点に限って認めた例外である**（対象は ID レンジの突合ただ 1 つ／
 *   取得は読み取り専用の HTTP に限る／落とし方は警告に限る／ビルドやテストの前提にしない）。
 *   **他の planning 依存を復活させない**（ADR-0029 決定 2）。
 *
 * fail-open の設計と、その「放置しない」方法:
 *   secret が無い／API が失敗した／宣言が読めない、のいずれでも **exit 0** で `status: "unverified"` と
 *   理由を書く。項目 6 の検証不能で監査の他 5 項目を巻き込まない（産出検証 check-backlog-audit-output.js
 *   が「産出そのもの」を守る）。
 *   🔴 **ただし「ずれが 0 件」と「検査が動いていない」は必ず区別できるようにする**（ADR-0093 決定 3）。
 *   そのために **`scanned`（実際に突き合わせられた種別の数）を必ず併記する。** `scanned: 0` は
 *   「検査が動いていない」であり、`scanned: 4` かつ指摘 0 件が「ずれが無い」である。
 *   **終了コードは変えない** —— ADR-0093 決定 3 は「落とし方は警告に限り、ビルドやテストの前提にしない」と
 *   定めており、同決定が述べる「fail-open のままにしない」の内容は**走査件数の併記**である。
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
const tt = require('./check-test-traceability.js');

const DEFAULT_OWNER = 'endazon';
const DEFAULT_REPO = 'project-planning';
/** 計画側が公開する導出結果（計画 ADR-0093 決定 1）。 */
const DEFAULT_FILE = 'tools/doc-checks/kg-ranges.json';
/** `kg-ranges.json` のトップレベルキー（`projects/<name>/` のディレクトリ名）。 */
const PROJECT_KEY = 'ai-stock-trading';
/** 突き合わせる種別。🔴 NFR は入れない（ADR-0093 決定 2。足すには別途の裁定が要る）。 */
const KINDS = ['FR', 'UC', 'SC', 'ADR'];

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
 * 計画リポジトリが公開する `kg-ranges.json` を取り、本プロジェクトのレンジ表を返す。
 * `execFn` は差し替え可能（テストで gh を呼ばずに済ませる）。token が無ければ例外。
 * @returns {{[kind: string]: [number, number]}}
 */
function fetchPlanningRanges({
  owner = DEFAULT_OWNER, repo = DEFAULT_REPO, file = DEFAULT_FILE,
  projectKey = PROJECT_KEY, token, execFn = execFileSync,
} = {}) {
  if (!token) throw new Error('PLANNING_REPO_TOKEN が渡されていない（secret 不在。B-3）');
  const out = execFn('gh', [
    'api', `repos/${owner}/${repo}/contents/${file}`,
    '-H', 'Accept: application/vnd.github.raw',
  ], {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, GH_TOKEN: token, GITHUB_TOKEN: token },
  });
  let doc;
  try {
    doc = JSON.parse(out);
  } catch (e) {
    throw new Error(`${file} が JSON として読めない: ${e.message || e}`);
  }
  const table = doc && doc[projectKey];
  if (!table || typeof table !== 'object') {
    throw new Error(`${file} に「${projectKey}」のレンジ表が無い（計画側でプロジェクトキーが変わった可能性）`);
  }
  const ranges = {};
  for (const kind of KINDS) {
    const v = table[kind];
    if (Array.isArray(v) && v.length === 2 && Number.isInteger(v[0]) && Number.isInteger(v[1])) {
      ranges[kind] = [v[0], v[1]];
    }
  }
  if (Object.keys(ranges).length === 0) {
    throw new Error(`${file} の「${projectKey}」に ${KINDS.join(' / ')} のレンジが 1 件も無い`);
  }
  return ranges;
}

/**
 * 本リポジトリが宣言しているレンジを読む。
 * FR/UC/SC は `check-test-traceability.js` の `readPlanIds()`、ADR は `lib/plan-ranges.js` が単一情報源。
 * 🔴 パーサを書き写さない —— 本番の抽出関数そのものを呼ぶ。
 * @returns {{[kind: string]: [number, number]}}
 */
function readDeclaredRanges({ readIdsFn = tt.readPlanIds, readAdrFn = readPlanAdrRange } = {}) {
  const ranges = {};
  const ids = readIdsFn();
  for (const id of ids) {
    const m = /^(FR|UC|SC)-(\d+)$/.exec(String(id));
    if (!m) continue;
    const [, kind, num] = m;
    const n = Number(num);
    const cur = ranges[kind];
    if (!cur) ranges[kind] = [n, n];
    else ranges[kind] = [Math.min(cur[0], n), Math.max(cur[1], n)];
  }
  const adr = readAdrFn();
  ranges.ADR = [adr.from, adr.to];
  return ranges;
}

/**
 * 種別ごとに宣言と実物を突き合わせる（純関数）。
 * @returns {{ranges: Array, scanned: number, status: 'ok'|'behind'|'ahead'}}
 */
function compareRanges(declared, planning) {
  const rows = [];
  for (const kind of KINDS) {
    const d = declared && declared[kind];
    const p = planning && planning[kind];
    if (!d || !p) continue;
    let status = 'ok';
    if (p[1] > d[1]) status = 'behind';
    else if (p[1] < d[1]) status = 'ahead';
    rows.push({ kind, declared: d, planning: p, status });
  }
  // 🔴 behind を ahead より優先して報告する。前進漏れのほうが実害（レンジ外 ID を引く PR の CI 落ち）を起こす。
  let status = 'ok';
  if (rows.some((r) => r.status === 'behind')) status = 'behind';
  else if (rows.some((r) => r.status === 'ahead')) status = 'ahead';
  return { ranges: rows, scanned: rows.length, status };
}

function pad4(n) {
  return String(n).padStart(4, '0');
}

/** 指摘のある種別だけを人が読める 1 行にする。 */
function describe(rows) {
  const bad = rows.filter((r) => r.status !== 'ok');
  if (bad.length === 0) return `宣言と実物が一致（${rows.length} 種を突合）`;
  return bad.map((r) => {
    const fmt = r.kind === 'ADR' ? pad4 : (n) => String(n).padStart(2, '0');
    const diff = Math.abs(r.planning[1] - r.declared[1]);
    const dir = r.status === 'behind' ? `実物 ${fmt(r.planning[1])} に ${diff} 件遅れている` : `実物 ${fmt(r.planning[1])} を ${diff} 件超えている`;
    return `${r.kind}: 宣言 ${fmt(r.declared[0])}..${fmt(r.declared[1])} が${dir}`;
  }).join(' / ');
}

/**
 * 全体の結合点。宣言の読み取り・計画側の取得のどちらが失敗しても例外を投げず unverified を返す。
 * 既存の JSON キー（status / declaredMax / planningMax / reason / checkedAt / source）は維持する
 * —— backlog-audit.yml のプロンプトと scripts.repo.test.js が読んでいる契約である。
 */
function resolve({ token, readDeclaredFn = readDeclaredRanges, fetchFn = fetchPlanningRanges } = {}) {
  let declared = null;
  let planning = null;
  const reasons = [];
  try {
    declared = readDeclaredFn();
  } catch (e) {
    reasons.push(`宣言を読めない: ${e.message || e}`);
  }
  try {
    planning = fetchFn({ token });
  } catch (e) {
    reasons.push(`計画側を取得できない: ${e.message || e}`);
  }
  const cmp = compareRanges(declared, planning);
  const declaredMax = declared && declared.ADR ? declared.ADR[1] : null;
  const planningMax = planning && planning.ADR ? planning.ADR[1] : null;
  const base = {
    declaredMax,
    planningMax,
    scanned: cmp.scanned,
    ranges: cmp.ranges,
    checkedAt: new Date().toISOString(),
    source: `${DEFAULT_OWNER}/${DEFAULT_REPO}/${DEFAULT_FILE}#${PROJECT_KEY}`,
  };
  // 🔴 scanned が 0 なら「ずれが無い」ではなく「検査が動いていない」である。
  if (cmp.scanned === 0) {
    return { status: 'unverified', ...base, reason: reasons.join('。') || '突き合わせられた種別が 1 件も無い' };
  }
  const detail = describe(cmp.ranges);
  const reason = reasons.length ? `${detail}（ただし ${reasons.join('。')}）` : detail;
  return { status: cmp.status, ...base, reason };
}

function selfTest() {
  const PLANNING_OK = { FR: [1, 21], UC: [1, 7], SC: [1, 3], ADR: [1, 37] };
  const DECLARED_OK = { FR: [1, 21], UC: [1, 7], SC: [1, 3], ADR: [1, 37] };
  const cases = [
    {
      name: 'compareRanges: 4 種すべて一致なら ok・scanned=4',
      run: () => compareRanges(DECLARED_OK, PLANNING_OK),
      expect: (r) => r.status === 'ok' && r.scanned === 4,
    },
    {
      // 🔴 陽性対照。ずらしたら落ちることを確かめずに検査器を信用しない。
      name: '陽性対照: ADR だけずらすと behind になり、指摘はその 1 種だけ（#710 の再発形）',
      run: () => compareRanges({ ...DECLARED_OK, ADR: [1, 35] }, PLANNING_OK),
      expect: (r) => r.status === 'behind' && r.scanned === 4
        && r.ranges.filter((x) => x.status !== 'ok').length === 1
        && r.ranges.find((x) => x.kind === 'ADR').status === 'behind',
    },
    {
      name: '陽性対照: ADR 以外（SC）のずれも検出する（従前は ADR しか見ていなかった）',
      run: () => compareRanges({ ...DECLARED_OK, SC: [1, 2] }, PLANNING_OK),
      expect: (r) => r.status === 'behind' && r.ranges.find((x) => x.kind === 'SC').status === 'behind',
    },
    {
      name: '宣言が先走っていれば ahead',
      run: () => compareRanges({ ...DECLARED_OK, ADR: [1, 40] }, PLANNING_OK),
      expect: (r) => r.status === 'ahead',
    },
    {
      name: 'behind と ahead が同時なら behind を優先する（実害が大きい側）',
      run: () => compareRanges({ ...DECLARED_OK, ADR: [1, 35], SC: [1, 9] }, PLANNING_OK),
      expect: (r) => r.status === 'behind',
    },
    {
      name: '🔴 scanned=0 は ok ではなく unverified（「ずれが無い」と「動いていない」を区別する）',
      run: () => compareRanges(DECLARED_OK, {}),
      expect: (r) => r.scanned === 0 && r.ranges.length === 0,
    },
    {
      name: 'resolve: 一致なら ok・scanned=4',
      run: () => resolve({ token: 't', readDeclaredFn: () => DECLARED_OK, fetchFn: () => PLANNING_OK }),
      expect: (r) => r.status === 'ok' && r.scanned === 4 && r.declaredMax === 37 && r.planningMax === 37,
    },
    {
      name: 'resolve: secret 不在は unverified・scanned=0・理由に PLANNING_REPO_TOKEN を残す（exit させない）',
      run: () => resolve({ token: '', readDeclaredFn: () => DECLARED_OK }),
      expect: (r) => r.status === 'unverified' && r.scanned === 0
        && /PLANNING_REPO_TOKEN/.test(r.reason) && r.declaredMax === 37,
    },
    {
      name: 'resolve: gh 失敗（404 等）は unverified に理由を残す',
      run: () => resolve({
        token: 't',
        readDeclaredFn: () => DECLARED_OK,
        fetchFn: () => { throw new Error('gh: HTTP 404: Not Found'); },
      }),
      expect: (r) => r.status === 'unverified' && /404/.test(r.reason) && r.scanned === 0,
    },
    {
      name: 'resolve: 宣言が読めなくても unverified（例外で落とさない）',
      run: () => resolve({
        token: 't',
        readDeclaredFn: () => { throw new Error('節が無い'); },
        fetchFn: () => PLANNING_OK,
      }),
      expect: (r) => r.status === 'unverified' && /宣言を読めない/.test(r.reason) && r.planningMax === 37,
    },
    {
      name: 'fetchPlanningRanges: raw JSON からプロジェクトのレンジを取り、GH_TOKEN を子プロセスへ写す',
      run: () => {
        let seenEnv = null;
        let seenArgs = null;
        const r = fetchPlanningRanges({
          token: 'secret-x',
          execFn: (cmd, args, opts) => {
            seenEnv = opts.env;
            seenArgs = args;
            if (cmd !== 'gh' || args[0] !== 'api') throw new Error('gh api 以外を呼んだ');
            return JSON.stringify({
              'ai-stock-trading': { FR: [1, 21], UC: [1, 7], SC: [1, 3], ADR: [1, 37] },
              'microservices-platform': { FR: [1, 22], UC: [1, 11], SC: [1, 21], ADR: [1, 93] },
            });
          },
        });
        return { r, tokenPassed: seenEnv && seenEnv.GH_TOKEN === 'secret-x', args: seenArgs };
      },
      expect: (x) => x.tokenPassed === true && x.r.ADR[1] === 37 && x.r.SC[1] === 3
        && x.args.join(' ').includes(DEFAULT_FILE)
        && x.args.join(' ').includes('application/vnd.github.raw'),
    },
    {
      name: 'fetchPlanningRanges: プロジェクトキーが無ければ例外（黙って 0 件検査へ落ちない）',
      run: () => {
        try {
          fetchPlanningRanges({ token: 't', execFn: () => JSON.stringify({ other: {} }) });
          return 'なぜか成功した';
        } catch (e) { return e.message; }
      },
      expect: (m) => /ai-stock-trading/.test(m),
    },
    {
      // 🔴 本番の抽出関数そのものを呼ぶ。正規表現を試験側へ書き写すと本番だけ変えても緑のままになる。
      name: 'readDeclaredRanges: 実物の宣言ファイルから 4 種すべてを読める',
      run: () => readDeclaredRanges(),
      expect: (r) => KINDS.every((k) => Array.isArray(r[k]) && r[k][1] >= r[k][0] && r[k][0] === 1),
    },
    {
      // 🔴 **現在の宣言値をリテラルと突き合わせない。** それをやると、次にレンジを前進させる PR で
      //    この自己試験まで同時に直さないと `ahead` で落ちる —— **本検査器が塞ごうとしている
      //    「導出値の書き写し」そのものである**（母集合の規則 10）。ここで固定するのは
      //    「実物の宣言が 4 種そろってパースでき、比較器がそれを処理できる」という配線であり、
      //    **ずれの検出は上の陽性対照（フィクスチャ）が持つ。** 実際のずれは CI の本走が見る。
      name: 'readDeclaredRanges → compareRanges の配線が実物の宣言で通る（値は固定しない）',
      run: () => {
        const declared = readDeclaredRanges();
        return compareRanges(declared, declared);
      },
      expect: (r) => r.status === 'ok' && r.scanned === 4,
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
  // 🔴 走査件数を必ず添える。0 件は「ずれが無い」ではなく「検査が動いていない」である。
  const line = `計画 ID レンジ鮮度: ${result.status}（突合 ${result.scanned} 種）— ${result.reason}`;
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

module.exports = {
  fetchPlanningRanges, readDeclaredRanges, compareRanges, describe, resolve, selfTest,
  DEFAULT_FILE, PROJECT_KEY, KINDS,
};
