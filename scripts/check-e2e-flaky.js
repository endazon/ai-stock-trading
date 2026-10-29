#!/usr/bin/env node
'use strict';
/*
 * check-e2e-flaky.js — NFR / #2037 / IADR-0623
 *
 * L1 の Playwright E2E（frontend.yml の e2e ジョブ）の flaky を数え、可視化し、再発したら起票する。
 * 外部依存ゼロ（Node 22 の標準モジュールと fetch だけ）。基準書は docs/tests/E2E_STANDARDS.md §10。
 *
 * 何を読むか:
 *   `merge-reports` が html と同時に書く **JSON レポート**（`--reporter html,github,json`）。html レポートと同じ
 *   実行結果を持ち、html の中身（圧縮して埋め込まれた JSON）を解くより壊れにくい。test の `status` が `flaky`
 *   （一度失敗して再試行で通った）のものを flaky と数える。`retries: 1` なので flaky は「pass」と数えない。
 *
 * 何をするか（3 段。段ごとに独立して動く）:
 *   1. 集計（`--report`）: flaky の件数と test を数え、要約 JSON（`--out`）を書く。**flaky が 1 件でもあれば
 *      test ごとに `::warning::` を出す**（閾値 WARN_AT = 1）。終了コードは 0（flaky は PR を落とさない）。
 *   2. 連続の判定（`--history`）: develop の直前の実行の要約（同じ形の JSON）と突き合わせ、
 *      **同じ test が直近 STREAK（= 3）回連続で flaky** なら「再発」とする。履歴が STREAK-1 件に満たなければ判定しない。
 *      直前の実行を新しい順に見て、要約が無い実行（シャードが落ちてまとめられなかった等）に当たったらそこで履歴を打ち切る
 *      （飛ばさない＝連続を作らない側へ倒す。historyUntilGap）。
 *   3. 起票（`--upsert-issue`）: 再発した test ごとに専用 issue を 1 つ持つ（本文のマーカーで引く）。
 *      無ければ作り、在ればコメントを足す。**自動ではクローズしない。**
 *
 * 🔴 **flaky を skip・fixme・quarantine で緑にする仕組みは持たない**（IADR-0623）。本スクリプトは数えて、見せて、
 *    起票するだけである。直すのは人（と次の PR）であり、test を外して緑にしない。
 *
 * fail-closed（exit 1）:
 *   - JSON レポートを読めない・形が違う・test が 0 件（0 件の集計を「flaky 0」と誤らない）。
 *   - 再発を検知したのに起票できない（token 不在・API 失敗）。検知を黙って捨てない。
 *
 * 使い方:
 *   node scripts/check-e2e-flaky.js --report <playwright.json> --out <e2e-flaky-summary.json>
 *   node scripts/check-e2e-flaky.js --summary <e2e-flaky-summary.json> --history <dir> [--upsert-issue]
 *   node scripts/check-e2e-flaky.js --self-test
 */
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const { emit, format } = require('./lib/ci-annotate.js');

/** 1 件でも flaky なら警告する（基準書 §10）。 */
const WARN_AT = 1;
/** 同じ test が何回連続で flaky なら起票するか（基準書 §10）。 */
const STREAK = 3;
const SUMMARY_NAME = 'e2e-flaky-summary.json';
const LABEL = 'e2e-flaky';
const MARKER_PREFIX = 'e2e-flaky:';

function parseArgs(argv) {
  const a = { selfTest: false, report: null, out: null, summary: null, history: null, upsertIssue: false };
  for (let i = 0; i < argv.length; i++) {
    const t = argv[i];
    if (t === '--self-test') a.selfTest = true;
    else if (t === '--upsert-issue') a.upsertIssue = true;
    else if (t === '--report') a.report = argv[++i];
    else if (t === '--out') a.out = argv[++i];
    else if (t === '--summary') a.summary = argv[++i];
    else if (t === '--history') a.history = argv[++i];
    else throw new Error(`未知の引数: ${t}`);
  }
  return a;
}

/** test の識別子。project・ファイル・describe の連なり・title で 1 つに決まる（行番号は含めない＝行がずれても同じ test）。 */
function testId({ project, file, titlePath }) {
  return [project, file, ...titlePath].join(' › ');
}

/** 識別子から issue のマーカーを作る（本文で引く。title が長くても検索できる長さにする）。 */
function markerOf(id) {
  return `<!-- ${MARKER_PREFIX}${crypto.createHash('sha256').update(id).digest('hex').slice(0, 16)} -->`;
}

/**
 * Playwright の JSON レポートを集計する（純関数）。
 * 形: { suites: [{ title, file, specs: [{ title, file, line, tests: [{ projectName, status }] }], suites: [...] }], stats }
 */
function summarize(report, meta = {}) {
  if (!report || typeof report !== 'object' || !Array.isArray(report.suites)) {
    throw new Error('Playwright の JSON レポートの形ではない（suites が無い）');
  }
  const counts = { expected: 0, unexpected: 0, flaky: 0, skipped: 0 };
  const flaky = [];
  let total = 0;
  const walk = (suite, titles) => {
    // ファイルの suite（最上位）の title はファイル名なので describe の連なりに入れない。
    const next = suite.__top ? titles : [...titles, suite.title];
    for (const spec of suite.specs || []) {
      for (const t of spec.tests || []) {
        total += 1;
        if (!(t.status in counts)) throw new Error(`未知の test の status: ${t.status}`);
        counts[t.status] += 1;
        if (t.status === 'flaky') {
          const entry = { project: t.projectName || '', file: spec.file, line: spec.line, titlePath: [...next, spec.title] };
          flaky.push({ id: testId(entry), ...entry });
        }
      }
    }
    for (const child of suite.suites || []) walk(child, next);
  };
  for (const top of report.suites) walk({ ...top, __top: true }, []);
  if (total === 0) throw new Error('test が 0 件である（0 件の集計を「flaky 0」としない）');
  flaky.sort((x, y) => x.id.localeCompare(y.id));
  return { schema: 1, ...meta, total, counts, flaky };
}

/** 要約を読む（形の検査つき）。 */
function readSummary(file) {
  const s = JSON.parse(fs.readFileSync(file, 'utf8'));
  if (!s || s.schema !== 1 || !Array.isArray(s.flaky) || !Number.isInteger(s.total)) {
    throw new Error(`${file}: e2e-flaky の要約の形ではない`);
  }
  return s;
}

/** 履歴の目録の名前。ワークフローが直前の実行を新しい順に 1 行ずつ書く（`<runNumber> <runId> ok|missing`）。 */
const MANIFEST_NAME = 'manifest.tsv';

/** 目録の本文を読む（純関数）。行の順（新しい順）を保つ。 */
function parseManifest(text) {
  const rows = [];
  for (const raw of String(text).split(/\r?\n/)) {
    const line = raw.trim();
    if (!line) continue;
    const m = /^(\d+)\s+(\d+)\s+(ok|missing)$/.exec(line);
    if (!m) throw new Error(`履歴の目録の行の形が違う: ${line}`);
    rows.push({ runNumber: Number(m[1]), runId: m[2], status: m[3] });
  }
  return rows;
}

/**
 * 連続の判定に使える履歴（純関数）。目録の新しい順に要約を取り、**要約の無い実行に当たったらそこで打ち切る**
 * （飛ばさない）。要約の無い実行（e2e をまとめられなかった等）は「flaky だったか」を言えないので、
 * それを飛ばして古い実行と繋ぐと、連続でないものを連続と数える。打ち切れば連続を作らない側へ倒れる。
 */
function historyUntilGap(rows, summaryOf) {
  const out = [];
  for (const r of rows) {
    if (r.status !== 'ok') break;
    out.push(summaryOf(r));
  }
  return out;
}

/** 履歴のディレクトリ（`<dir>/manifest.tsv` と `<dir>/<runId>/e2e-flaky-summary.json`）を読む。 */
function readHistory(dir) {
  const manifest = path.join(dir, MANIFEST_NAME);
  if (!fs.existsSync(manifest)) throw new Error(`履歴の目録が無い: ${manifest}（履歴を集めるステップが動いていない）`);
  return historyUntilGap(parseManifest(fs.readFileSync(manifest, 'utf8')), (r) => readSummary(path.join(dir, r.runId, SUMMARY_NAME)));
}

/**
 * 連続の判定（純関数）。current と、それより前の実行の要約（新しい順）から、
 * 直近 streak 回すべてで flaky だった test を返す。履歴が streak-1 件に満たなければ判定しない（空）。
 */
function findStreaks(current, history, streak = STREAK) {
  const prev = history.filter((h) => Number(h.runNumber || 0) < Number(current.runNumber || Infinity)).slice(0, streak - 1);
  if (prev.length < streak - 1) return { judged: false, streaks: [], window: [current, ...prev] };
  const window = [current, ...prev];
  const streaks = current.flaky.filter((f) => prev.every((h) => h.flaky.some((x) => x.id === f.id)));
  return { judged: true, streaks, window };
}

/**
 * test 名を Markdown の表のセルへ入れる形にする（純関数）。test 名は spec の書き手が決める任意の文字列である。
 * 🔴 `\` を先に、`|` と `` ` `` を同じ 1 回の置換で扱う（`|` だけを `\|` にすると、名前の中の `\|` が `\\|` になって
 * セルの区切りに戻る。CodeQL js/incomplete-sanitization）。改行はセルを壊すので空白へ畳む。
 */
function mdCell(text) {
  return String(text).replace(/[\\|`]/g, (c) => `\\${c}`).replace(/\s*\r?\n\s*/g, ' ');
}

/** インラインコード（`…`）の中へ入れる形にする（純関数）。バッククォートは閉じを作るので ' へ、改行は空白へ。 */
function mdCode(text) {
  return String(text).replace(/`/g, "'").replace(/\s*\r?\n\s*/g, ' ');
}

/** flaky 1 件の警告の本文（純関数）。workflow コマンドの符号化（`%`・CR・LF）は ci-annotate の format が行う。 */
function warningMessage(f) {
  return `E2E の flaky: ${f.id}（e2e/${f.file}:${f.line}）。flaky は pass と数えない`;
}

/** step summary 用の Markdown（純関数）。 */
function renderSummary(s) {
  const lines = [
    `### E2E の flaky（L1）: ${s.flaky.length} 件 / ${s.total} 件`,
    '',
    `| expected | unexpected | flaky | skipped |`,
    `| --- | --- | --- | --- |`,
    `| ${s.counts.expected} | ${s.counts.unexpected} | ${s.counts.flaky} | ${s.counts.skipped} |`,
  ];
  if (s.flaky.length) {
    lines.push('', '| test | 場所 |', '| --- | --- |');
    for (const f of s.flaky) lines.push(`| ${mdCell(f.id)} | ${mdCell(`${f.file}:${f.line}`)} |`);
    lines.push('', `flaky は pass と数えない。同じ test が develop で ${STREAK} 回連続で flaky なら issue が自動で立つ（skip・隔離で緑にしない）。`);
  }
  return `${lines.join('\n')}\n`;
}

function runUrl(repo, runId) {
  return repo && runId ? `https://github.com/${repo}/actions/runs/${runId}` : '（ローカル）';
}

/** 専用 issue の本文（純関数）。 */
function renderIssue(f, window, { repo } = {}) {
  return [
    markerOf(f.id),
    `## E2E の test が develop で ${window.length} 回連続で flaky だった`,
    '',
    `- test: \`${mdCode(f.id)}\``,
    `- 場所: \`src/platform/frontend/e2e/${mdCode(f.file)}:${f.line}\``,
    `- 実行（新しい順）: ${window.map((w) => runUrl(repo, w.runId)).join(' / ')}`,
    '',
    '### 扱い',
    '',
    '- 原因（待ち方・試験データの共有・時刻依存・スタブの抜け）を直す。基準書（`docs/tests/E2E_STANDARDS.md`）§4・§10。',
    '- 🔴 **`test.skip` / `test.fixme` / 再試行回数の引き上げで緑にしない。** 直したら、PR 本文に `Closes #<この issue の番号>` を書く。',
    '- 各実行の html レポート（成果物 `e2e-html-report`。trace は再試行した test に入る）で、1 回目の失敗の原因を見る。',
    '',
    '---',
    '本 issue は `.github/workflows/frontend.yml` の `e2e-flaky-issue` ジョブ（`scripts/check-e2e-flaky.js --upsert-issue`）が立てた。',
    '同じ test が連続で flaky である間は、実行ごとにコメントが足される。**自動ではクローズしない。**',
  ].join('\n');
}

async function ghRequest(method, url, token, body) {
  const res = await fetch(url, {
    method,
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
      ...(body ? { 'Content-Type': 'application/json' } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText} for ${method} ${url}`);
  return res.json();
}

/**
 * 再発した test ごとに専用 issue を upsert する。再発が 0 件なら API を一度も呼ばない。
 * `api(method, url, body)` は差し替え可能（自己試験は実ネットワークを叩かない）。
 */
async function upsertIssues({ streaks, window, repo, token, api } = {}) {
  if (!streaks || streaks.length === 0) return [];
  if (!repo || !token) throw new Error('flaky の再発を検知したが、起票に要る GITHUB_REPOSITORY / GITHUB_TOKEN が無い');
  const call = api || ((method, url, body) => ghRequest(method, url, token, body));
  const base = `https://api.github.com/repos/${repo}`;
  try {
    await call('POST', `${base}/labels`, { name: LABEL, color: 'fbca04', description: 'E2E の test が develop で連続して flaky（frontend.yml が自動起票）' });
  } catch (e) {
    if (!/^422/.test(String(e.message))) throw e; // 既にあれば 422
  }
  const open = await call('GET', `${base}/issues?state=open&labels=${encodeURIComponent(LABEL)}&per_page=100`);
  const done = [];
  for (const f of streaks) {
    const marker = markerOf(f.id);
    const hit = (open || []).find((i) => !i.pull_request && String(i.body || '').includes(marker));
    if (hit) {
      await call('POST', `${base}/issues/${hit.number}/comments`, {
        body: `まだ flaky である（直近 ${window.length} 回連続）。実行: ${runUrl(repo, window[0].runId)}`,
      });
      done.push({ id: f.id, action: 'commented', number: hit.number });
      continue;
    }
    const name = String(f.titlePath[f.titlePath.length - 1]).replace(/\s*\r?\n\s*/g, ' ');
    const title = `test(NFR): E2E の「${name}」が develop で ${window.length} 回連続で flaky（自動起票）`;
    const r = await call('POST', `${base}/issues`, { title: title.slice(0, 250), labels: [LABEL], body: renderIssue(f, window, { repo }) });
    if (!r || !Number.isInteger(r.number)) throw new Error('issue を作成したが番号を読み戻せない');
    done.push({ id: f.id, action: 'created', number: r.number });
  }
  return done;
}

// ---- 自己試験 -------------------------------------------------------------------------------------------

/** 自己試験用の JSON レポート（Playwright 1.62 の実物と同じ入れ子。手元で一時 spec を走らせた出力から形を写した）。 */
function fixtureReport(statuses) {
  return {
    config: {},
    errors: [],
    stats: {},
    suites: [
      {
        title: 'a.smoke.spec.ts',
        file: 'a.smoke.spec.ts',
        specs: Object.entries(statuses).map(([title, status], i) => ({
          title, file: 'a.smoke.spec.ts', line: 10 + i, tests: [{ projectName: 'smoke', status }],
        })),
        suites: [
          {
            title: 'グループ',
            file: 'a.smoke.spec.ts',
            specs: [{ title: '入れ子', file: 'a.smoke.spec.ts', line: 50, tests: [{ projectName: 'smoke', status: 'flaky' }] }],
          },
        ],
      },
    ],
  };
}

function fakeApi(existing = []) {
  const calls = [];
  let next = 900;
  const api = async (method, url, body) => {
    calls.push({ method, url, body });
    if (method === 'GET') return existing;
    if (method === 'POST' && url.endsWith('/issues')) return { number: next++ };
    return {};
  };
  return { api, calls };
}

async function selfTest() {
  const R = fixtureReport({ 'ok': 'expected', 'ゆらぐ': 'flaky', '落ちる': 'unexpected', '飛ばす': 'skipped' });
  const sum = (n, ids) => ({ schema: 1, runNumber: n, runId: String(1000 + n), total: 5, counts: {}, flaky: ids.map((id) => ({ id, titlePath: [id], file: 'a', line: 1 })) });
  const ID = 'smoke › a.smoke.spec.ts › ゆらぐ';
  const cases = [
    {
      name: 'summarize: status=flaky だけを flaky と数え、describe の連なりも識別子に入れる',
      run: async () => summarize(R, { runNumber: 1 }),
      expect: (s) => s.total === 5 && s.counts.flaky === 2 && s.counts.unexpected === 1 && s.counts.skipped === 1
        && s.flaky.map((f) => f.id).sort().join('|') === [ID, 'smoke › a.smoke.spec.ts › グループ › 入れ子'].sort().join('|'),
    },
    {
      name: '🔴 summarize: test が 0 件なら例外（0 件の集計を flaky 0 としない）',
      run: async () => { try { summarize({ suites: [] }); return 'no-throw'; } catch (e) { return e.message; } },
      expect: (m) => /0 件/.test(m),
    },
    {
      name: '🔴 summarize: JSON レポートの形でなければ例外',
      run: async () => { try { summarize({ stats: {} }); return 'no-throw'; } catch (e) { return e.message; } },
      expect: (m) => /suites/.test(m),
    },
    {
      name: 'summarize: 未知の status は例外（Playwright の形が変わったら黙って数え落とさない）',
      run: async () => { try { summarize(fixtureReport({ x: 'interrupted' })); return 'no-throw'; } catch (e) { return e.message; } },
      expect: (m) => /未知の test の status/.test(m),
    },
    {
      name: 'findStreaks: 3 回連続で flaky なら再発',
      run: async () => findStreaks(sum(5, [ID]), [sum(4, [ID]), sum(3, [ID]), sum(2, [])]),
      expect: (r) => r.judged && r.streaks.length === 1 && r.streaks[0].id === ID && r.window.length === 3,
    },
    {
      name: 'findStreaks: 途中で 1 回でも flaky でなければ再発にしない（連続でない）',
      run: async () => findStreaks(sum(5, [ID]), [sum(4, []), sum(3, [ID])]),
      expect: (r) => r.judged && r.streaks.length === 0,
    },
    {
      name: 'findStreaks: 履歴が 2 件に満たなければ判定しない',
      run: async () => findStreaks(sum(5, [ID]), [sum(4, [ID])]),
      expect: (r) => !r.judged && r.streaks.length === 0,
    },
    {
      name: 'findStreaks: 自分より新しい・同じ実行は履歴に数えない（再実行で自分自身を数えない）',
      run: async () => findStreaks(sum(5, [ID]), [sum(5, [ID]), sum(6, [ID]), sum(4, [ID])]),
      expect: (r) => !r.judged,
    },
    {
      name: '🔴 historyUntilGap: 間に要約の無い実行があれば、そこで打ち切る（飛ばして古い実行と繋がない）',
      run: async () => {
        const rows = parseManifest('41 9041 ok\n40 9040 missing\n39 9039 ok\n');
        const h = historyUntilGap(rows, (r) => sum(r.runNumber, [ID]));
        return { n: h.length, r: findStreaks(sum(42, [ID]), h) };
      },
      expect: ({ n, r }) => n === 1 && !r.judged && r.streaks.length === 0,
    },
    {
      name: 'historyUntilGap: 直前 2 回とも要約が在れば両方を使う（3 回連続の判定ができる）',
      run: async () => findStreaks(sum(42, [ID]), historyUntilGap(parseManifest('41 9041 ok\n40 9040 ok\n'), (r) => sum(r.runNumber, [ID]))),
      expect: (r) => r.judged && r.streaks.length === 1,
    },
    {
      name: '🔴 parseManifest: 目録の行の形が違えば例外（黙って 0 件の履歴にしない）',
      run: async () => { try { parseManifest('41 9041 skipped'); return 'no-throw'; } catch (e) { return e.message; } },
      expect: (m) => /目録の行の形/.test(m),
    },
    {
      name: '🔴 mdCell / mdCode / 警告: test 名の \\・|・`・改行で表・コード・workflow コマンドが壊れない',
      run: async () => {
        const nasty = { id: 'smoke › a › 名前に \\| と `x` と\n改行 100%', file: 'a.smoke.spec.ts', line: 3, titlePath: ['名前に \\| と\n改行'] };
        const md = renderSummary({ total: 1, counts: { expected: 0, unexpected: 0, flaky: 1, skipped: 0 }, flaky: [nasty] });
        const row = md.split('\n').find((l) => l.includes('名前に'));
        const prev = process.env.GITHUB_ACTIONS;
        process.env.GITHUB_ACTIONS = 'true';
        const cmd = format('warning', warningMessage(nasty), '');
        if (prev === undefined) delete process.env.GITHUB_ACTIONS; else process.env.GITHUB_ACTIONS = prev;
        const body = renderIssue(nasty, [sum(1, [])], { repo: 'o/r' });
        return { row, cmd, body };
      },
      expect: ({ row, cmd, body }) =>
        // 表の行: 区切りの | は外枠の 3 本だけ（名前の中の | は \| として残る）。改行は無い。
        row.replace(/\\./g, '').split('|').length === 4 && row.includes('\\\\\\|') && row.includes('\\`x\\`')
        // workflow コマンド: 1 行で、% は %25 に符号化される。
        && cmd.trimEnd().split('\n').length === 1 && /100%25/.test(cmd) && cmd.startsWith('::warning::')
        // issue の本文のインラインコードは閉じを作らない（` は ' へ）。
        && /- test: `[^`]*'x'[^`]*`/.test(body) && !/改行 100%\n/.test(body),
    },
    {
      name: 'upsertIssues: 再発が無ければ API を一度も呼ばない（token が無くても例外にしない）',
      run: async () => { const f = fakeApi(); const r = await upsertIssues({ streaks: [], window: [], repo: '', token: '', api: f.api }); return { r, n: f.calls.length }; },
      expect: (x) => x.r.length === 0 && x.n === 0,
    },
    {
      name: 'upsertIssues: 既存が無ければ test ごとに issue を作る（ラベル・マーカー・skip しない旨の本文）',
      run: async () => { const f = fakeApi([]); const r = await upsertIssues({ streaks: [{ id: ID, titlePath: ['ゆらぐ'], file: 'a.smoke.spec.ts', line: 11 }], window: [sum(5, []), sum(4, []), sum(3, [])], repo: 'o/r', token: 't', api: f.api }); return { r, f }; },
      expect: ({ r, f }) => {
        const created = f.calls.find((c) => c.method === 'POST' && c.url.endsWith('/issues'));
        return r[0].action === 'created' && created && created.body.labels[0] === LABEL
          && created.body.body.includes(markerOf(ID)) && /skip/.test(created.body.body) && /3 回連続/.test(created.body.title);
      },
    },
    {
      name: 'upsertIssues: マーカーの合う open issue が在れば、作らずにコメントを足す',
      run: async () => { const f = fakeApi([{ number: 77, body: `x ${markerOf(ID)}` }]); const r = await upsertIssues({ streaks: [{ id: ID, titlePath: ['ゆらぐ'], file: 'a', line: 1 }], window: [sum(5, [])], repo: 'o/r', token: 't', api: f.api }); return { r, f }; },
      expect: ({ r, f }) => r[0].action === 'commented' && r[0].number === 77 && !f.calls.some((c) => c.method === 'POST' && c.url.endsWith('/issues'))
        && f.calls.some((c) => c.url.endsWith('/issues/77/comments')),
    },
    {
      name: '🔴 upsertIssues: 再発を検知したのに token が無ければ例外（黙って捨てない）',
      run: async () => { try { await upsertIssues({ streaks: [{ id: ID, titlePath: ['x'] }], window: [], repo: 'o/r', token: '' }); return 'no-throw'; } catch (e) { return e.message; } },
      expect: (m) => /GITHUB_TOKEN/.test(m),
    },
    {
      name: 'renderSummary: flaky の件数と test を表で出す',
      run: async () => renderSummary(summarize(R)),
      expect: (md) => /flaky（L1）: 2 件 \/ 5 件/.test(md) && md.includes(ID),
    },
  ];
  let failed = 0;
  for (const c of cases) {
    let got;
    try {
      got = await c.run();
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

const USAGE = 'usage: check-e2e-flaky.js --report <json> --out <summary.json> | --summary <summary.json> --history <dir> [--upsert-issue] | --self-test\n';

async function main(argv) {
  const args = parseArgs(argv.slice(2));
  if (args.selfTest) process.exit(await selfTest());

  if (args.report) {
    if (!args.out) { process.stderr.write(USAGE); process.exit(2); }
    const report = JSON.parse(fs.readFileSync(args.report, 'utf8'));
    const s = summarize(report, {
      runId: process.env.GITHUB_RUN_ID || null,
      runNumber: process.env.GITHUB_RUN_NUMBER ? Number(process.env.GITHUB_RUN_NUMBER) : null,
      sha: process.env.GITHUB_SHA || null,
      ref: process.env.GITHUB_REF || null,
    });
    fs.mkdirSync(path.dirname(path.resolve(args.out)), { recursive: true });
    fs.writeFileSync(args.out, `${JSON.stringify(s, null, 2)}\n`, 'utf8');
    if (process.env.GITHUB_STEP_SUMMARY) fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, renderSummary(s));
    if (s.flaky.length >= WARN_AT) {
      for (const f of s.flaky) {
        emit('warning', warningMessage(f), { stream: process.stderr, prefix: '  warning  ' });
      }
    }
    process.stdout.write(`E2E の flaky: ${s.flaky.length} 件 / ${s.total} 件（expected ${s.counts.expected}・unexpected ${s.counts.unexpected}・skipped ${s.counts.skipped}）\n`);
    process.exit(0);
  }

  if (args.summary) {
    const current = readSummary(args.summary);
    const history = readHistory(args.history);
    const r = findStreaks(current, history);
    if (!r.judged) {
      process.stdout.write(`連続の判定: 履歴が ${history.length} 件で ${STREAK - 1} 件に満たないので判定しない\n`);
      process.exit(0);
    }
    process.stdout.write(`連続の判定: 直近 ${r.window.length} 回で連続して flaky な test は ${r.streaks.length} 件\n`);
    for (const f of r.streaks) emit('warning', `E2E の flaky が ${STREAK} 回連続: ${f.id}`, { stream: process.stderr, prefix: '  warning  ' });
    if (!args.upsertIssue) process.exit(0);
    const done = await upsertIssues({ streaks: r.streaks, window: r.window, repo: process.env.GITHUB_REPOSITORY, token: process.env.GITHUB_TOKEN });
    for (const d of done) process.stdout.write(`issue #${d.number} を${d.action === 'created' ? '作成した' : 'に追記した'}: ${d.id}\n`);
    process.exit(0);
  }

  process.stderr.write(USAGE);
  process.exit(2);
}

if (require.main === module) {
  main(process.argv).catch((e) => {
    emit('error', `check-e2e-flaky: ${e.message || e}`, { stream: process.stderr, prefix: '  error  ' });
    process.exit(1);
  });
}

module.exports = {
  summarize, readSummary, readHistory, parseManifest, historyUntilGap, mdCell, mdCode, warningMessage, findStreaks, renderSummary, renderIssue, upsertIssues, markerOf, testId, selfTest,
  WARN_AT, STREAK, LABEL, SUMMARY_NAME,
};
