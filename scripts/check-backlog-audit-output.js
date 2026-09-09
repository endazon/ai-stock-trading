#!/usr/bin/env node
'use strict';
/*
 * check-backlog-audit-output.js
 * #711 / NFR: 週次バックログ監査（backlog-audit.yml）が run success でも issue の産出・更新が
 * 無い事象を検出する。外部依存ゼロ（Node 標準モジュールのみ。gh CLI は子プロセスで呼ぶだけ）。
 *
 * 背景:
 *   Claude ステップは「issue を検索し、在れば upsert・無ければ作成する」プロンプトで動くが、
 *   ツール権限拒否・検索条件の不一致・タイトルの表記ゆれ等で **issue へ何も書けないまま
 *   subtype: success で終わる**ことがあり得る。ジョブは緑のまま、「今週は指摘なしだった」と
 *   「今週は黙って落ちた」が本文からは区別できない。
 *
 *   本検査器は「issue（#483）の updated_at が、この run の開始時刻より新しいか」だけを見る。
 *   本文の中身（監査の質）までは検証しない——それは人間のレビュー対象である。ここで塞ぐのは
 *   「産出そのものが起きたか」という、機械で判定できる最小限の面である。
 *
 * 使い方:
 *   node scripts/check-backlog-audit-output.js --run-start <ISO8601> [--issue-number 483]
 *   node scripts/check-backlog-audit-output.js --self-test
 *
 * run 開始時刻は呼び出し側（ワークフロー先頭のステップ）が `date -u +%Y-%m-%dT%H:%M:%SZ` で
 * 取り、env `RUN_START_TIME` または `--run-start` で渡す。**このスクリプト自身は時刻を
 * 生成しない**——生成すると「issue 取得と同じプロセス内の時刻」になり、Claude ステップの
 * 実行時間ぶん比較がずれる（取得漏れを見逃す方向にずれる）。
 *
 * gh CLI は `GH_TOKEN` / `GITHUB_TOKEN` を環境変数から読む（gh 自身の既定動作）。
 *
 * 補助（fail-open）: env `EXECUTION_FILE`（Claude ステップの実行記録 JSON）が読めれば、fail 時に
 * その中身から「サブエージェントへ委任して自分は終了した」「ターン数」を手掛かりとして併記する。
 * 2026-09-09 の実測（run 34303318956）では、AI が Agent ツールへ監査全体をバックグラウンド委任し
 * 2 ターンで終了したため issue が更新されなかった。判定そのものは updated_at の比較だけで決める。
 */
const { execFileSync } = require('child_process');
const fs = require('fs');
const { emit, warn } = require('./lib/ci-annotate.js');

const DEFAULT_ISSUE_NUMBER = 483;
const DEFAULT_ISSUE_TITLE = 'chore(NFR): バックログ定期監査の結果';

function parseArgs(argv) {
  const a = { selfTest: false, issueNumber: null, runStart: null };
  for (let i = 0; i < argv.length; i++) {
    const t = argv[i];
    if (t === '--self-test') a.selfTest = true;
    else if (t === '--run-start') a.runStart = argv[++i];
    else if (t.startsWith('--run-start=')) a.runStart = t.slice('--run-start='.length);
    else if (t === '--issue-number') a.issueNumber = argv[++i];
    else if (t.startsWith('--issue-number=')) a.issueNumber = t.slice('--issue-number='.length);
  }
  return a;
}

/**
 * `gh issue view <n> --json updatedAt,title,number` を叩き、パースして返す。
 * `execFn` は差し替え可能（テストで gh を実際に呼ばずに済ませるため）。
 * issue が存在しない／gh が失敗したときは例外を投げる（呼び出し側が「不在」として扱う）。
 */
function fetchIssue(issueNumber, execFn = execFileSync) {
  const out = execFn('gh', ['issue', 'view', String(issueNumber), '--json', 'updatedAt,title,number'], {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  return JSON.parse(out);
}

/**
 * 判定の中核（純関数）。issue の updated_at と run 開始時刻を比べるだけであり、
 * gh 呼び出しの成否とは切り離してテストできるようにする。
 *
 * @param {{updatedAt: string, runStart: string}} args
 * @returns {{ok: boolean, reason?: string}}
 */
function verdict({ updatedAt, runStart }) {
  const u = Date.parse(updatedAt);
  const r = Date.parse(runStart);
  if (!Number.isFinite(r)) return { ok: false, reason: `run 開始時刻を解釈できない: ${runStart}` };
  if (!Number.isFinite(u)) return { ok: false, reason: `issue の updated_at を解釈できない: ${updatedAt}` };
  // 同時刻（境界）は「更新されていない」側に倒す。実運用ではあり得ない一致だが、
  // 「更新された」と誤判定して見逃す方向より、誤って fail する方向に倒すほうが安全である。
  if (u <= r) {
    return {
      ok: false,
      reason:
        `issue の updated_at（${updatedAt}）が run 開始時刻（${runStart}）以降に更新されていない` +
        '（本文を書き換えないまま success 終了した可能性がある）',
    };
  }
  return { ok: true };
}

/**
 * `fetchIssue` の失敗（issue 不在・gh 認証失敗等）も含めて評価する。
 * main() とセルフテストの両方から使う結合点。
 *
 * @param {{issueNumber: number|string, runStart: string, fetchFn?: Function}} args
 */
function evaluate({ issueNumber, runStart, fetchFn = fetchIssue }) {
  let issue;
  try {
    issue = fetchFn(issueNumber);
  } catch (e) {
    return {
      ok: false,
      reason: `issue #${issueNumber}（タイトル "${DEFAULT_ISSUE_TITLE}"）を取得できない: ${e.message || e}`,
    };
  }
  return verdict({ updatedAt: issue.updatedAt, runStart });
}

/** サブエージェント委任（Agent / Task ツール）に当たる tool_use 名。 */
const DELEGATION_TOOLS = new Set(['Agent', 'Task']);

/**
 * 実行記録（claude-code-action の execution_file。メッセージの配列）から、fail の手掛かりを抽出する。
 * 純関数。読めない・形が違う場合は空の結果を返し、判定へ影響させない（fail-open）。
 *
 * @param {unknown} records
 * @returns {{delegated: string[], numTurns: number|null}}
 */
function inspectExecution(records) {
  const out = { delegated: [], numTurns: null };
  if (!Array.isArray(records)) return out;
  for (const m of records) {
    if (!m || typeof m !== 'object') continue;
    if (m.type === 'result' && Number.isFinite(m.num_turns)) out.numTurns = m.num_turns;
    if (m.type !== 'assistant') continue;
    const content = m.message && Array.isArray(m.message.content) ? m.message.content : [];
    for (const c of content) {
      if (c && c.type === 'tool_use' && DELEGATION_TOOLS.has(c.name)) {
        const desc = c.input && typeof c.input.description === 'string' ? c.input.description : '';
        out.delegated.push(desc ? `${c.name}（${desc}）` : c.name);
      }
    }
  }
  return out;
}

/** inspectExecution の結果を人が読む 1 文にする。手掛かりが無ければ空文字。 */
function describeExecution(info) {
  const parts = [];
  if (info.delegated.length > 0) {
    parts.push(
      `AI が監査をサブエージェントへ委任している（${info.delegated.join(' / ')}）。` +
        'headless の run は親の応答終了で終わり、子は完了を待たれない——委任させない（--disallowedTools Agent,Task とプロンプトの禁止）',
    );
  }
  if (info.numTurns !== null) parts.push(`ターン数 ${info.numTurns}`);
  return parts.join('。');
}

/** env EXECUTION_FILE を読み、手掛かりの文を返す。読めなければ空文字（判定へ影響させない）。 */
function executionHint(filePath) {
  if (!filePath) return '';
  try {
    return describeExecution(inspectExecution(JSON.parse(fs.readFileSync(filePath, 'utf8'))));
  } catch {
    return '';
  }
}

/** 検証器自体の自己試験。gh を実際には呼ばず、fetchFn を差し替えて判定ロジックのみ確かめる。 */
function selfTest() {
  const delegatedRecords = [
    { type: 'system', subtype: 'init' },
    {
      type: 'assistant',
      message: { content: [{ type: 'tool_use', name: 'Agent', input: { description: 'Repo backlog audit and issue upsert', prompt: '…' } }] },
    },
    { type: 'result', subtype: 'success', num_turns: 2 },
  ];
  const cases = [
    {
      name: '実行記録: Agent への委任とターン数を手掛かりとして抽出する',
      run: () => describeExecution(inspectExecution(delegatedRecords)),
      expect: (s) => /委任/.test(s) && /Repo backlog audit/.test(s) && /ターン数 2/.test(s),
    },
    {
      name: '実行記録: 委任が無ければ委任の文は出さない（ターン数のみ）',
      run: () => describeExecution(inspectExecution([
        { type: 'assistant', message: { content: [{ type: 'tool_use', name: 'Bash', input: { command: 'gh issue list' } }] } },
        { type: 'result', num_turns: 36 },
      ])),
      expect: (s) => !/委任/.test(s) && /ターン数 36/.test(s),
    },
    {
      name: '実行記録: 配列でない・空ならば手掛かりは空（fail-open）',
      run: () => [describeExecution(inspectExecution(null)), describeExecution(inspectExecution({})), executionHint('/nonexistent/execution.json'), executionHint('')],
      expect: (r) => r.every((s) => s === ''),
    },
    {
      name: 'run 開始より後に更新されていれば ok',
      run: () => evaluate({
        issueNumber: DEFAULT_ISSUE_NUMBER,
        runStart: '2026-09-09T00:00:00Z',
        fetchFn: () => ({ updatedAt: '2026-09-09T00:05:00Z' }),
      }),
      expect: (r) => r.ok === true,
    },
    {
      name: '更新なし（run 開始より前）は fail',
      run: () => evaluate({
        issueNumber: DEFAULT_ISSUE_NUMBER,
        runStart: '2026-09-09T00:10:00Z',
        fetchFn: () => ({ updatedAt: '2026-09-08T23:00:00Z' }),
      }),
      expect: (r) => r.ok === false && /以降に更新されていない/.test(r.reason),
    },
    {
      name: '境界（同時刻）は fail 側に倒す',
      run: () => evaluate({
        issueNumber: DEFAULT_ISSUE_NUMBER,
        runStart: '2026-09-09T00:00:00Z',
        fetchFn: () => ({ updatedAt: '2026-09-09T00:00:00Z' }),
      }),
      expect: (r) => r.ok === false,
    },
    {
      name: 'issue 不在（gh issue view が失敗）は fail',
      run: () => evaluate({
        issueNumber: DEFAULT_ISSUE_NUMBER,
        runStart: '2026-09-09T00:00:00Z',
        fetchFn: () => { throw new Error('gh: issue not found (404)'); },
      }),
      expect: (r) => r.ok === false && /取得できない/.test(r.reason),
    },
    {
      name: 'run 開始時刻が壊れていれば fail（issue 側は正常）',
      run: () => evaluate({
        issueNumber: DEFAULT_ISSUE_NUMBER,
        runStart: 'not-a-date',
        fetchFn: () => ({ updatedAt: '2026-09-09T00:05:00Z' }),
      }),
      expect: (r) => r.ok === false && /run 開始時刻を解釈できない/.test(r.reason),
    },
    {
      name: 'fetchIssue: JSON を正しくパースする（execFn を差し替え）',
      run: () => fetchIssue(483, () => JSON.stringify({ updatedAt: '2026-09-09T01:00:00Z', title: DEFAULT_ISSUE_TITLE, number: 483 })),
      expect: (r) => r.updatedAt === '2026-09-09T01:00:00Z' && r.number === 483,
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

  const runStart = args.runStart || process.env.RUN_START_TIME;
  if (!runStart) {
    // fail-loud: 判定不能を「該当なし」として緑にしない。呼び出し側（ワークフロー）の配線漏れである。
    emit('error', 'run 開始時刻が渡されていない（--run-start または env RUN_START_TIME が必要）', {
      stream: process.stderr,
      prefix: '  error  ',
    });
    process.exit(1);
  }
  const issueNumber = args.issueNumber || DEFAULT_ISSUE_NUMBER;

  const result = evaluate({ issueNumber, runStart });
  if (!result.ok) {
    const hint = executionHint(process.env.EXECUTION_FILE);
    emit('error', `バックログ監査の産出検証に失敗: ${result.reason}${hint ? `。実行記録の手掛かり: ${hint}` : ''}`, {
      stream: process.stderr,
      prefix: '  error  ',
    });
    process.exit(1);
  }
  process.stdout.write(`✓ issue #${issueNumber}（"${DEFAULT_ISSUE_TITLE}"）は run 開始時刻以降に更新されている\n`);
  process.exit(0);
}

if (require.main === module) {
  main(process.argv);
}

module.exports = {
  DEFAULT_ISSUE_NUMBER,
  DEFAULT_ISSUE_TITLE,
  fetchIssue,
  verdict,
  evaluate,
  inspectExecution,
  describeExecution,
  executionHint,
  selfTest,
};
