#!/usr/bin/env node
'use strict';
/*
 * check-permission-denials.js
 * claude-code-action の実行ログ（execution_file）を読み、**権限拒否で実行できなかった
 * ツール**を itemize して報告する。拒否が 1 件でもあれば exit 1 でジョブを止める。
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 背景（実運用で発生した障害）:
 *   claude-doc-review.yml のレビューが 21 ターン中 17 回の権限拒否で潰れ、レビュー本文を
 *   1 文字も書けないまま終了した。にもかかわらず結果は `"subtype": "success",
 *   "is_error": false` であり、**CI は緑**、PR に残ったのは「6 グループに分けて並列精査中」
 *   という進行中コメントだけだった。原因は `--allowedTools` に `Task`（サブエージェント
 *   起動）が無いことだったが、これを突き止めるにはジョブログを開いて `SDK options:` の
 *   配列と `permission_denials_count` を突き合わせる必要があった。
 *
 *   すなわち二重の欠陥がある:
 *     1. 許可ツールの不足そのもの
 *     2. **不足したことが誰にも見えない**（緑のジョブ・進行中のまま止まったコメント）
 *   本スクリプトは 2 を塞ぐ。1 は許可ツールを直せば消えるが、次に別のツールが不足した
 *   ときに再び黙って潰れるため、汎用の安全弁が要る。
 *
 * なぜ warn ではなく **exit 1** か:
 *   CI には承認する人間が居ない。したがって CI での権限拒否は「待たされた」ではなく
 *   **「その作業は永久に実行されない」**を意味する。レビュー用ワークフローなら
 *   「レビューが実施されていない」と同義であり、緑で通してはならない。
 *   キットの他の検査器（check-ai-workflow-config 等）が fail-open なのは「検査器側の
 *   都合で全リポジトリの CI を落とさない」ためだが、ここで報告するのは検査器の推測では
 *   なく **実行時に実際に起きた事実**であり、見逃す理由が無い。
 *
 * 使い方:
 *   node scripts/check-permission-denials.js <execution-file> [--self-test]
 *
 * 環境変数:
 *   ALLOW_PERMISSION_DENIALS=1  拒否があっても失敗させない（警告のみ。移行期の緊急避難用）
 */
const fs = require('fs');
const { emit, warn } = require('./lib/ci-annotate.js');

/**
 * 実行ログを読む。JSON 配列と NDJSON（1 行 1 イベント）の両方を受ける。
 * action の出力形式が変わっても片方で拾えるようにしておく。
 */
function parseEvents(text) {
  const trimmed = text.trim();
  if (!trimmed) return [];
  try {
    const parsed = JSON.parse(trimmed);
    if (Array.isArray(parsed)) return parsed;
    return [parsed];
  } catch (e) {
    // NDJSON フォールバック。壊れた行は黙って捨てず、読めた分だけで判断する。
    const events = [];
    for (const line of trimmed.split('\n')) {
      const s = line.trim();
      if (!s) continue;
      try {
        events.push(JSON.parse(s));
      } catch (e2) {
        /* 読めない行は無視する（部分的に壊れたログでも拒否は拾いたい） */
      }
    }
    return events;
  }
}

/** メッセージ本文の content 配列を取り出す（形が違っても落ちない）。 */
function contentOf(event) {
  const c = event && event.message && event.message.content;
  return Array.isArray(c) ? c : [];
}

/**
 * 報告用のラベルを作る。Bash は**コマンド名まで**出す。
 *
 * 背景（issue #146）: 当初はツール名だけを出していたため、報告が「Bash（4 件）」で止まり
 * **どのコマンドが拒否されたのか判らなかった**。許可リストは `Bash(git diff:*)` のように
 * コマンド単位で書くため、ツール名だけでは何を足せばよいか決められない。実際、起票者は
 * ジョブログを追って読み取り系 git だと推定する必要があった。報告が行動につながらないなら、
 * 件数だけを出していた元の状態とさして変わらない。
 *
 * 先頭 2 トークンまでに切り詰める（`git diff origin/main...HEAD` → `git diff`）。
 * 理由は 2 つある。許可リストの粒度がコマンド＋サブコマンドであること、そして引数には
 * トークン・パス等が入り得るため、ログへ丸ごと出さないことである。
 */
function labelOf(name, input) {
  if (name !== 'Bash') return name || '(不明なツール)';
  const cmd = input && typeof input.command === 'string' ? input.command.trim() : '';
  if (!cmd) return 'Bash';
  // パイプ・リダイレクト・連結より前だけを見る（後続コマンドは別の話）。
  const head = cmd.split(/[|;&><]/)[0].trim();
  const tokens = head.split(/\s+/).filter(Boolean).slice(0, 2);
  return tokens.length ? `Bash(${tokens.join(' ')})` : 'Bash';
}

/**
 * tool_result のテキストが「権限拒否」を表しているか。
 *
 * SDK が itemize した `permission_denials` を持たない版のためのフォールバック経路であり、
 * 文言に依存する。過剰一致を避けるため「permission」と「拒否を表す語」の同時出現を要求する。
 */
function looksLikeDenial(text) {
  const s = String(text);
  if (!/permission/i.test(s)) return false;
  return /(denied|deny|not granted|haven'?t granted|hasn'?t granted|requested permissions)/i.test(s);
}

/** tool_result の content は文字列にも配列にもなり得る。テキストへ畳む。 */
function resultTextOf(block) {
  const c = block && block.content;
  if (typeof c === 'string') return c;
  if (Array.isArray(c)) return c.map((p) => (p && typeof p.text === 'string' ? p.text : '')).join(' ');
  return '';
}

/**
 * 実行ログから拒否されたツールを集計する。
 * 戻り値 { count, byTool: Map<name, n>, itemized, source }。
 *
 * 3 段階で降格する。上ほど正確なので、取れた時点で確定させる。
 *   1. result.permission_denials（SDK が itemize したもの。ツール名が確実）
 *   2. tool_result の走査（tool_use_id からツール名を逆引きする）
 *   3. permission_denials_count のみ（件数は判るがツール名は判らない）
 * 3 になった場合も**黙らない**。件数だけでも出さないと、この検査を入れた意味が消える。
 */
function collectDenials(events) {
  const result = events.find((e) => e && e.type === 'result') || null;
  const byTool = new Map();
  const bump = (name) => byTool.set(name, (byTool.get(name) || 0) + 1);

  // 1. SDK が itemize した配列。
  const itemizedList = result && Array.isArray(result.permission_denials) ? result.permission_denials : null;
  if (itemizedList && itemizedList.length) {
    for (const d of itemizedList) {
      bump(labelOf(d && (d.tool_name || d.toolName), d && (d.tool_input || d.toolInput)));
    }
    return { count: itemizedList.length, byTool, itemized: true, source: 'permission_denials' };
  }

  // 2. tool_use_id → ラベルを作ってから tool_result を走査する。
  const labelById = new Map();
  for (const e of events) {
    if (!e || e.type !== 'assistant') continue;
    for (const b of contentOf(e)) {
      if (b && b.type === 'tool_use' && b.id) labelById.set(b.id, labelOf(b.name, b.input));
    }
  }
  for (const e of events) {
    if (!e || e.type !== 'user') continue;
    for (const b of contentOf(e)) {
      if (!b || b.type !== 'tool_result') continue;
      if (!b.is_error) continue;
      if (!looksLikeDenial(resultTextOf(b))) continue;
      bump(labelById.get(b.tool_use_id) || '(不明なツール)');
    }
  }
  if (byTool.size) {
    let n = 0;
    for (const v of byTool.values()) n += v;
    return { count: n, byTool, itemized: true, source: 'tool_result' };
  }

  // 3. 件数のみ。
  const count = result && Number.isFinite(result.permission_denials_count) ? result.permission_denials_count : 0;
  return { count, byTool, itemized: false, source: 'permission_denials_count' };
}

/** 報告用の 1 行メッセージを組み立てる。 */
function formatDenials({ count, byTool, itemized }) {
  const head = `AI の実行中にツールの権限拒否が ${count} 件発生した（CI には承認する人間が居ないため、これらの作業は実行されていない）`;
  if (!itemized) {
    return (
      `${head}。実行ログにツール名が残っていないため内訳を特定できない。` +
      'ジョブログの `SDK options:` の allowedTools 配列と、AI が実行しようとした操作を突き合わせること'
    );
  }
  const detail = [...byTool.entries()]
    .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
    .map(([name, n]) => `${name}（${n} 件）`)
    .join(' / ');
  return (
    `${head}: ${detail}。` +
    '対処は 2 通りである: (a) 必要なツールを claude_args の --allowedTools に加える、' +
    '(b) そのツールを使わせないようプロンプト側で作業手順を狭める。' +
    'どちらも取らずに放置すると、ジョブは success のまま成果物だけが欠けた状態が続く'
  );
}

/** 検証器自体の自己試験（CI で毎回実行し、検査ロジックの退行を防ぐ）。 */
function selfTest() {
  const denial = 'Claude requested permissions to use Task, but you haven\'t granted it yet.';
  const cases = [
    {
      name: '拒否ゼロの実行は count 0',
      events: [{ type: 'result', subtype: 'success', permission_denials_count: 0 }],
      expect: (r) => r.count === 0,
    },
    {
      name: 'permission_denials 配列があればツール名まで特定する',
      events: [
        {
          type: 'result',
          permission_denials_count: 2,
          permission_denials: [{ tool_name: 'Task' }, { tool_name: 'Task' }],
        },
      ],
      expect: (r) => r.count === 2 && r.itemized && r.byTool.get('Task') === 2,
    },
    {
      name: 'tool_result からツール名を逆引きする（配列が無い版）',
      events: [
        { type: 'assistant', message: { content: [{ type: 'tool_use', id: 't1', name: 'Task' }] } },
        { type: 'user', message: { content: [{ type: 'tool_result', tool_use_id: 't1', is_error: true, content: denial }] } },
        { type: 'result', permission_denials_count: 1 },
      ],
      expect: (r) => r.count === 1 && r.itemized && r.byTool.get('Task') === 1,
    },
    {
      name: '権限拒否でないツールエラーは数えない',
      events: [
        { type: 'assistant', message: { content: [{ type: 'tool_use', id: 't1', name: 'Read' }] } },
        { type: 'user', message: { content: [{ type: 'tool_result', tool_use_id: 't1', is_error: true, content: 'File not found' }] } },
        { type: 'result', permission_denials_count: 0 },
      ],
      expect: (r) => r.count === 0,
    },
    {
      name: 'ツール名を特定できなくても件数は報告する',
      events: [{ type: 'result', permission_denials_count: 17 }],
      expect: (r) => r.count === 17 && r.itemized === false,
    },
    // issue #146: 「Bash（4 件）」ではどのコマンドを許可すればよいか決められない。
    {
      name: 'Bash はコマンド名まで報告する（許可リストの粒度に合わせる）',
      events: [
        {
          type: 'result',
          permission_denials: [
            { tool_name: 'Bash', tool_input: { command: 'git diff origin/main...HEAD' } },
            { tool_name: 'Bash', tool_input: { command: 'git diff' } },
            { tool_name: 'Bash', tool_input: { command: 'git status' } },
          ],
        },
      ],
      expect: (r) => r.byTool.get('Bash(git diff)') === 2 && r.byTool.get('Bash(git status)') === 1,
    },
    {
      name: 'パイプ以降は見ない（後続コマンドを取り違えない）',
      events: [
        { type: 'result', permission_denials: [{ tool_name: 'Bash', tool_input: { command: 'git log | head -5' } }] },
      ],
      expect: (r) => r.byTool.get('Bash(git log)') === 1,
    },
    {
      name: 'command が無い Bash はツール名のみ（落ちない）',
      events: [{ type: 'result', permission_denials: [{ tool_name: 'Bash' }] }],
      expect: (r) => r.byTool.get('Bash') === 1,
    },
    {
      name: 'tool_result 経由でもコマンド名まで出る',
      events: [
        { type: 'assistant', message: { content: [{ type: 'tool_use', id: 'b1', name: 'Bash', input: { command: 'git show HEAD' } }] } },
        { type: 'user', message: { content: [{ type: 'tool_result', tool_use_id: 'b1', is_error: true, content: denial }] } },
        { type: 'result', permission_denials_count: 1 },
      ],
      expect: (r) => r.byTool.get('Bash(git show)') === 1,
    },
    {
      name: 'tool_result の content が配列でも読める',
      events: [
        { type: 'assistant', message: { content: [{ type: 'tool_use', id: 't1', name: 'Bash' }] } },
        {
          type: 'user',
          message: { content: [{ type: 'tool_result', tool_use_id: 't1', is_error: true, content: [{ type: 'text', text: denial }] }] },
        },
        { type: 'result', permission_denials_count: 1 },
      ],
      expect: (r) => r.byTool.get('Bash') === 1,
    },
  ];

  const parseCases = [
    ['JSON 配列を読める', '[{"type":"result","permission_denials_count":3}]', (e) => e.length === 1],
    ['NDJSON を読める', '{"type":"result","permission_denials_count":3}\n{"type":"x"}', (e) => e.length === 2],
    ['壊れた行があっても読めた分で判断する', '{"type":"result","permission_denials_count":3}\n{壊れ', (e) => e.length === 1],
    ['空ファイルは空配列', '   ', (e) => e.length === 0],
  ];

  let failed = 0;
  for (const [label, text, expect] of parseCases) {
    if (expect(parseEvents(text))) process.stdout.write(`  ok  ${label}\n`);
    else {
      failed++;
      process.stderr.write(`  NG  ${label}\n`);
    }
  }
  for (const c of cases) {
    const r = collectDenials(c.events);
    if (c.expect(r)) process.stdout.write(`  ok  ${c.name}\n`);
    else {
      failed++;
      process.stderr.write(`  NG  ${c.name}\n      got=${JSON.stringify({ ...r, byTool: [...r.byTool] })}\n`);
    }
  }
  // 実運用で起きた形（17 件・内訳不明）がメッセージに載ることを確かめる。
  const msg = formatDenials(collectDenials([{ type: 'result', permission_denials_count: 17 }]));
  if (msg.includes('17 件')) process.stdout.write('  ok  件数がメッセージに載る\n');
  else {
    failed++;
    process.stderr.write('  NG  件数がメッセージに載る\n');
  }

  if (failed) {
    process.stderr.write(`\n✗ 検証器の自己試験が ${failed} 件失敗した\n`);
    return 1;
  }
  process.stdout.write(`✓ 検証器の自己試験 ${cases.length + parseCases.length + 1} 件すべて合格\n`);
  return 0;
}

function main(argv) {
  const args = argv.slice(2);
  if (args.includes('--self-test')) process.exit(selfTest());

  const file = args.find((a) => !a.startsWith('--'));
  if (!file) {
    // 検査が成立しない状態を黙って通さない（この検査器が塞いでいる不具合と同じ形になる）。
    warn(
      '権限拒否チェック: 実行ログのパスが渡されていないため検査していない' +
        '（claude-code-action の outputs.execution_file を渡すこと）'
    );
    process.exit(0);
  }

  let text;
  try {
    text = fs.readFileSync(file, 'utf8');
  } catch (e) {
    warn(
      `権限拒否チェック: 実行ログを読めないため検査していない: ${file}（${e.code || e.message}）。` +
        'claude-code-action の outputs.execution_file が変わった可能性がある'
    );
    process.exit(0);
  }

  const events = parseEvents(text);
  if (events.length === 0) {
    warn(`権限拒否チェック: 実行ログを解析できないため検査していない: ${file}`);
    process.exit(0);
  }

  const denials = collectDenials(events);
  if (denials.count === 0) {
    process.stdout.write('✓ ツールの権限拒否は発生していない\n');
    process.exit(0);
  }

  const message = formatDenials(denials);
  if (process.env.ALLOW_PERMISSION_DENIALS === '1') {
    warn(`${message}（ALLOW_PERMISSION_DENIALS=1 のため失敗させない）`);
    process.exit(0);
  }
  emit('error', message, { stream: process.stderr, prefix: '  error  ' });
  process.exit(1);
}

if (require.main === module) {
  main(process.argv);
}

module.exports = { parseEvents, collectDenials, formatDenials, looksLikeDenial, labelOf, selfTest };
