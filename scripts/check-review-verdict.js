#!/usr/bin/env node
'use strict';
/*
 * check-review-verdict.js
 *
 * NFR / #490 / IADR-0190:
 * AI レビューが**判定（🔴 重大 / 🟡 推奨 / 🟢 軽微）を投稿しないまま終わった**ことを検出し、
 * ジョブを落とす。
 *
 * 背景（2026-08-14・PR #489 で実測）:
 *   同一 PR で **3 回連続**、レビューが判定を投稿しなかった。うち 2 回はジョブが
 *   **`success`** で終わっている——PR には進行中のプレースホルダだけが残り、
 *   「`dotnet test` をバックグラウンドで実行中です。完了後にサマリを投稿します」と
 *   書かれたまま終了していた。
 *
 *   **`claude-review` が緑であることは、PR を読む人にとって「レビュー済み・指摘なし」を意味する。**
 *   しかし何も判定されておらず、**マージも止まらない**。これが「緑だが検査されていない」である。
 *
 * **`check-permission-denials.js` とは別の穴を見る。**
 *   あちらは「AI がツールを 1 つも実行できなかった」形。本件は**ツールは正常に動いており、
 *   最後の投稿だけが無い**。実行ログは同じものを読むため `parseEvents` を借りる（2 本セット）。
 *
 * 判定の検出は**見出しの構造**で行う（決定2）:
 *   本文の語（「重大」等）を素で探すと、**レビューが本文中でその語を使った瞬間に誤検出する**。
 *   これは planning#319 知見3 で実証済みの同型のアンチパターンである（検査器について書いた
 *   記録が、語の一致だけで自己発火した）。よって**判定セクションの見出しが 3 種そろうこと**を
 *   条件とし、1 つでも欠ければ判定と認めない。
 *
 * 実行ログを読めないときは **warn ＋ exit 0**（fail-open）。
 *   実行ログが無いのは「レビューが動かなかった」ことを意味し、**その形は
 *   `check-permission-denials.js` が既に捕まえる**。二重に落とす必要は無い。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 使い方:
 *   node scripts/check-review-verdict.js <execution_file>
 *   node scripts/check-review-verdict.js --self-test
 */
const fs = require('fs');
const { warn, notice } = require('./lib/ci-annotate.js');
const { parseEvents } = require('./check-permission-denials.js');

/**
 * 判定セクションの見出し。**3 種すべて**が必要である。
 *
 * 絵文字と語の両方を要求する。**語だけ・絵文字だけでは通さない**——
 * 本文が「重大な問題は無い」と書いただけで緑になってはならない。
 */
const VERDICT_HEADINGS = [
  { key: '重大', re: /^#{1,6}\s*🔴\s*重大\s*$/m },
  { key: '推奨', re: /^#{1,6}\s*🟡\s*推奨\s*$/m },
  { key: '軽微', re: /^#{1,6}\s*🟢\s*軽微\s*$/m },
];

/**
 * AI が出力したテキストを集める。
 *
 * 🔴 **全文を走査してはならない。** 実行ログには**プロンプト自身**が含まれ、その中に
 * 判定フォーマットの雛形（`### 🔴 重大` 等）が書かれている。素朴に全文を検索すると
 * **常に一致して緑になり、ゲートが無意味になる**（本検査器の初版を実 CI へ載せる前に発見した）。
 *
 * よって**AI の出力に当たる場所だけ**を見る。配送経路が複数あるため、次を対象とする。
 *   1. `assistant` メッセージの `text` ブロック（通常の応答）
 *   2. `result` イベントの `result`（最終応答がここに入る版がある）
 *   3. **`input.body` を持つツール呼び出し全般**（判定をツール呼び出しで投稿する経路）
 *
 * 3 を入れるのは、`--allowedTools` にコメント投稿系のツール
 * （`mcp__github__add_issue_comment` / `pull_request_review_write` /
 * `add_comment_to_pending_review` 等）が含まれており、**判定をそれらで投稿すると
 * 1・2 に現れない**ためである（AI レビューの指摘・PR #491）。
 * **ツール名では絞らない** —— 実測では本ワークフローが実際に使うのは
 * `mcp__github_comment__update_claude_comment` であり、**名前で列挙すると取りこぼす**。
 * 代わりに **`input.body` というフィールドに限定する** —— 全ての `input` を見ると
 * `Bash` の `command` 等まで拾い、本文にたまたま見出しを含むコマンドで緑にできてしまう。
 */
function assistantText(events) {
  const out = [];
  const push = (v) => {
    if (typeof v === 'string' && v) out.push(v);
  };
  for (const e of events) {
    if (!e) continue;
    if (e.type === 'result') push(e.result);
    if (e.type !== 'assistant') continue;
    const content = e.message && e.message.content;
    if (!Array.isArray(content)) continue;
    for (const c of content) {
      if (!c) continue;
      if (c.type === 'text') push(c.text);
      // 判定をコメント投稿ツールで出す経路。body 以外は見ない（上のコメント参照）。
      if (c.type === 'tool_use' && c.input) push(c.input.body);
    }
  }
  return out.join('\n');
}

/** 実行ログのイベント種別の内訳（判定不能だったときの診断用）。 */
function eventShape(events) {
  const counts = new Map();
  for (const e of events) {
    const t = (e && e.type) || '(no type)';
    counts.set(t, (counts.get(t) || 0) + 1);
  }
  return [...counts.entries()].map(([t, n]) => `${t}=${n}`).join(' ');
}

/**
 * 判定の有無を調べる。
 * @returns {{ok: boolean, found: string[], missing: string[]}}
 */
function inspectVerdict(text) {
  const src = String(text == null ? '' : text);
  const found = [];
  const missing = [];
  for (const h of VERDICT_HEADINGS) (h.re.test(src) ? found : missing).push(h.key);
  return { ok: missing.length === 0, found, missing };
}

function writeStepSummary(result) {
  const file = process.env.GITHUB_STEP_SUMMARY;
  if (!file) return false;
  const lines = [
    '## 🔴 AI レビューが判定を投稿していない',
    '',
    'レビューは実行されたが、**判定（🔴 重大 / 🟡 推奨 / 🟢 軽微）が出力されていない**。',
    'ジョブが緑のままだと「レビュー済み・指摘なし」と読まれるため、ここで落とす。',
    '',
    `- 見つかった見出し: ${result.found.length ? result.found.join(' / ') : '（無し）'}`,
    `- 欠けている見出し: **${result.missing.join(' / ')}**`,
    '',
    '**よくある原因**: コマンドをバックグラウンドで起動し、その完了を待たずにターンを終えた。',
    '時間が足りないときは待たずに「未検証（理由）」と書き、**サマリの投稿を最優先する**こと。',
    '',
    '詳細は #490 / IADR-0190 を参照。',
  ];
  try {
    fs.appendFileSync(file, `${lines.join('\n')}\n`);
    return true;
  } catch (e) {
    return false;
  }
}

function main(argv) {
  const file = argv[0];
  if (!file) {
    warn('[check-review-verdict] 実行ログのパスが渡されていないため検査をスキップする。');
    return 0;
  }
  let raw;
  try {
    raw = fs.readFileSync(file, 'utf8');
  } catch (e) {
    // 実行ログが無い＝レビューが動いていない。その形は check-permission-denials.js の担当。
    warn(`[check-review-verdict] 実行ログを読めないため検査をスキップする: ${e.message}`);
    return 0;
  }

  const events = parseEvents(raw);
  if (!events.length) {
    warn('[check-review-verdict] 実行ログにイベントが無いため検査をスキップする。');
    return 0;
  }

  const text = assistantText(events);

  // 🔴 **AI の出力を 1 文字も取り出せなかった場合は判定できない。落とさない。**
  //
  // 初版はここを区別せず「判定なし」として落とし、**PR #491 の初回実行で偽陽性を出した**
  // （レビューは判定を正しく投稿していたのに、こちらが実行ログの形を取り違えて何も
  // 抽出できていなかった）。**「判定が無い」と「読めていない」は別である。**
  //
  // 診断のためイベント種別の内訳を出す。次に同じことが起きたとき、ログを漁らずに形が分かる。
  if (!text.trim()) {
    warn(
      '[check-review-verdict] AI の出力を実行ログから取り出せなかったため判定できない（検査をスキップする）。' +
        ` イベント内訳: ${eventShape(events) || '(空)'}`
    );
    return 0;
  }

  const result = inspectVerdict(text);
  if (result.ok) {
    notice('[check-review-verdict] OK: レビューの判定（🔴/🟡/🟢）が出力されている。');
    return 0;
  }

  writeStepSummary(result);
  process.stderr.write(
    '\n[check-review-verdict] 🔴 レビューが判定を投稿しないまま終了した。\n' +
      `  見つかった見出し: ${result.found.length ? result.found.join(' / ') : '（無し）'}\n` +
      `  欠けている見出し: ${result.missing.join(' / ')}\n\n` +
      'ジョブを緑にすると「レビュー済み・指摘なし」と読まれるため落とす（#490 / IADR-0190）。\n' +
      'よくある原因: コマンドをバックグラウンドで起動し、完了を待たずにターンを終えた。\n' +
      '時間が足りないときは待たずに「未検証（理由）」と書き、サマリの投稿を最優先すること。\n'
  );
  return 1;
}

/** 自己試験。判定規則そのものが壊れていれば、実データに対する結果は意味を持たない。 */
function selfTest() {
  const assert = require('assert');
  let n = 0;
  const ok = (name, fn) => {
    fn();
    n += 1;
    process.stdout.write(`  ok   ${name}\n`);
  };

  const VERDICT = ['### 🔴 重大', '- 指摘なし', '', '### 🟡 推奨', '- 指摘なし', '', '### 🟢 軽微', '- 指摘なし'].join('\n');
  const ev = (text) => [{ type: 'assistant', message: { content: [{ type: 'text', text }] } }];

  ok('判定 3 種がそろえば ok', () => {
    const r = inspectVerdict(VERDICT);
    assert.strictEqual(r.ok, true);
    assert.deepStrictEqual(r.missing, []);
  });

  // **最重要の否定形。** #489 で実際に起きた形（判定を投稿せず終了）を再現する。
  ok('判定が無ければ ok=false（#489 の形 A の回帰）', () => {
    const r = inspectVerdict('レビュー進行中\n\n- [ ] サマリコメント投稿\n\nバックグラウンドで実行中です。');
    assert.strictEqual(r.ok, false);
    assert.deepStrictEqual(r.missing, ['重大', '推奨', '軽微']);
  });

  ok('1 つでも欠ければ ok=false', () => {
    for (const drop of ['🔴 重大', '🟡 推奨', '🟢 軽微']) {
      const partial = VERDICT.split('\n').filter((l) => !l.includes(drop)).join('\n');
      assert.strictEqual(inspectVerdict(partial).ok, false, `${drop} を欠いても ok になっている`);
    }
  });

  // **語の一致で緑にしない**（planning#319 知見3 と同型のアンチパターンを避ける）。
  ok('本文で語を使っただけでは ok にならない', () => {
    const prose = '重大な問題は無い。推奨事項も無く、軽微な指摘も無い。';
    assert.strictEqual(inspectVerdict(prose).ok, false);
  });

  // 絵文字だけ・語だけの見出しも通さない。
  ok('見出しに絵文字と語の両方が要る', () => {
    assert.strictEqual(inspectVerdict('### 重大\n### 推奨\n### 軽微').ok, false);
    assert.strictEqual(inspectVerdict('### 🔴\n### 🟡\n### 🟢').ok, false);
  });

  ok('見出しレベルは 1〜6 のいずれでもよい', () => {
    const h2 = VERDICT.replace(/^### /gm, '## ');
    assert.strictEqual(inspectVerdict(h2).ok, true);
  });

  ok('assistant 以外のイベントのテキストは判定に使わない', () => {
    const events = [{ type: 'user', message: { content: [{ type: 'text', text: VERDICT }] } }];
    assert.strictEqual(inspectVerdict(assistantText(events)).ok, false);
  });

  ok('assistant の複数メッセージを連結して判定する', () => {
    const events = [
      ...ev('### 🔴 重大\n- 指摘なし'),
      ...ev('### 🟡 推奨\n- 指摘なし'),
      ...ev('### 🟢 軽微\n- 指摘なし'),
    ];
    assert.strictEqual(inspectVerdict(assistantText(events)).ok, true);
  });

  // --- 配送経路（PR #491 の 🔴/🟡 を受けた回帰） ---

  ok('result イベントの result からも判定を拾う', () => {
    const events = [{ type: 'result', subtype: 'success', result: VERDICT }];
    assert.strictEqual(inspectVerdict(assistantText(events)).ok, true);
  });

  // 判定をコメント投稿ツールで出す経路（`use_sticky_comment` に頼らない書き方）。
  ok('コメント投稿ツールの input.body からも判定を拾う', () => {
    const events = [
      { type: 'assistant', message: { content: [{ type: 'tool_use', name: 'mcp__github__add_issue_comment', input: { body: VERDICT } }] } },
    ];
    assert.strictEqual(inspectVerdict(assistantText(events)).ok, true);
  });

  // **`body` 以外は見ない。** 全 input を見ると Bash の command 等で緑にできてしまう。
  ok('tool_use の body 以外（command 等）は判定に使わない', () => {
    const events = [
      { type: 'assistant', message: { content: [{ type: 'tool_use', name: 'Bash', input: { command: `echo '${VERDICT}'` } }] } },
    ];
    assert.strictEqual(inspectVerdict(assistantText(events)).ok, false);
  });

  // 🔴 **最重要**: 「判定が無い」と「読めていない」を区別する。
  // 初版はこれを混同し、PR #491 の初回実行で偽陽性を出した。
  ok('AI の出力を取り出せなければ exit 0（判定不能。落とさない）', () => {
    const os = require('os');
    const path = require('path');
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'review-verdict-shape-'));
    const f = path.join(dir, 'unknown-shape.json');
    // 出力に当たるフィールドを持たないイベントだけのログ。
    fs.writeFileSync(f, JSON.stringify([{ type: 'system', subtype: 'init' }, { type: 'result', subtype: 'success' }]));
    try {
      assert.strictEqual(main([f]), 0);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });

  ok('eventShape はイベント種別の内訳を返す', () => {
    const s = eventShape([{ type: 'system' }, { type: 'result' }, { type: 'result' }]);
    assert.match(s, /system=1/);
    assert.match(s, /result=2/);
  });

  ok('実行ログのパスが無ければ exit 0（fail-open）', () => {
    assert.strictEqual(main([]), 0);
  });

  ok('実行ログを読めなければ exit 0（fail-open）', () => {
    assert.strictEqual(main(['/nonexistent/execution.json']), 0);
  });

  ok('判定を含む実行ログなら exit 0 / 含まなければ exit 1', () => {
    const os = require('os');
    const path = require('path');
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'review-verdict-'));
    const good = path.join(dir, 'good.json');
    const bad = path.join(dir, 'bad.json');
    fs.writeFileSync(good, JSON.stringify(ev(VERDICT)));
    fs.writeFileSync(bad, JSON.stringify(ev('進行中です')));
    // GITHUB_STEP_SUMMARY を汚さない（#140 と同型の事故を避ける）。
    const prev = process.env.GITHUB_STEP_SUMMARY;
    delete process.env.GITHUB_STEP_SUMMARY;
    try {
      assert.strictEqual(main([good]), 0);
      assert.strictEqual(main([bad]), 1);
    } finally {
      if (prev !== undefined) process.env.GITHUB_STEP_SUMMARY = prev;
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });

  process.stdout.write(`[check-review-verdict] 自己試験 ${n} 件 all passed。\n`);
}

if (require.main === module) {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) {
    selfTest();
    process.exit(0);
  }
  process.exit(main(argv));
}

module.exports = { inspectVerdict, assistantText, main, selfTest, VERDICT_HEADINGS };
