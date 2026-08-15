#!/usr/bin/env node
/*
 * AI レビューの判定投稿チェッカー
 *
 * **「緑だが検査されていない」を止める。**
 *
 * claude-code-action は、AI が判定（🔴 重大 / 🟡 推奨 / 🟢 軽微）を投稿しないまま
 * ターンを終えても `success` で終わる。ジョブは緑になり、PR には進行中の
 * プレースホルダだけが残る。**「レビュー済み・指摘なし」と読まれるが、実際には
 * 何も判定されていない。マージも止まらない。**
 *
 * 実測（planning#333。実装 ai-stock-trading#489 / #490 / `IADR-0190`）:
 * 同一 PR で 3 回連続、判定の投稿が無かった。
 *
 *   | 試行 | 実行時間 | 結論      | 判定 | 形                        |
 *   | ---: | -------- | --------- | ---- | ------------------------- |
 *   | 1    | 3m55s    | success   | 無し | A: 待たずにターンを終える |
 *   | 2    | 20m16s   | cancelled | 無し | B: 待って job timeout     |
 *   | 3    | 11m16s   | success   | 無し | A                         |
 *
 * **形 A の green のまま PR がマージされた。** B は少なくともマージを止めるが、
 * A は何の signal も出さない。ここで止める。
 *
 * **`check-permission-denials.js` では捕まらない。** あちらは「AI がツールを 1 つも
 * 実行できなかった」形を見る。本件は**ツールは正常に動いており、最後の投稿だけが
 * 無い** —— 別の穴である。入力（実行ログ）と配線は同じであり、`parseEvents` を
 * 借りる。**2 本セットで配布すること。**
 *
 * 使い方:
 *   node scripts/check-review-verdict.js <execution-file> [--self-test]
 *
 * 環境変数:
 *   ALLOW_MISSING_VERDICT=1   判定が無くても失敗させない（警告のみ。緊急避難用）
 *
 * 終了コード: 判定が揃っていない=1 / 揃っている=0 / 実行ログを読めない=0（fail-open）。
 */
'use strict';
const fs = require('fs');
const { emit, warn } = require('./lib/ci-annotate.js');
const { parseEvents } = require('./check-permission-denials.js');

/**
 * 判定の見出し（置換点ではない。**プロンプトの「出力フォーマット」と対にして直す**）。
 *
 * **絵文字と語の両方を要求する。** 語だけで探すと、レビューが本文中でその語を使った
 * 瞬間に誤検出する —— 「**重大**な問題は無い」という散文で緑になってしまう。これは
 * planning#319 知見 3 で実証済みの同型のアンチパターンである（検査器について書いた
 * 記録が、語の一致だけで自己発火した）。**見出しの構造で見る。**
 */
const VERDICTS = [
  { emoji: '🔴', word: '重大' },
  { emoji: '🟡', word: '推奨' },
  { emoji: '🟢', word: '軽微' },
];

/**
 * **判定はコメント投稿ツールの入力に載る。** レビューはサマリを地の文として出力するのではなく、
 * `mcp__github_comment__update_claude_comment` 等でコメントを更新する形で投稿する。
 * よって assistant の text ブロックだけを見ると**判定が在っても「無い」と読む**
 * （実測: planning#341 で本検査器自身が偽陽性で落ちた）。
 *
 * **ただし tool_use を無条件に含めてはならない。** `Bash(grep '### 🔴 重大' …)` のような
 * 入力で緑になってしまう。**コメント投稿ツールに限って**本文フィールドを読む。
 */
const COMMENT_TOOLS = [
  'mcp__github_comment__update_claude_comment',
  'mcp__github__add_issue_comment',
  'mcp__github__create_pull_request_review',
  'mcp__github__add_comment_to_pending_review',
];

/** コメント投稿ツールの入力から本文らしき文字列を取り出す。 */
function commentBodyOf(block) {
  const name = block && (block.name || block.tool_name);
  if (!name || !COMMENT_TOOLS.includes(name)) return '';
  const input = block.input || block.tool_input || {};
  const parts = [];
  for (const key of ['body', 'content', 'text', 'comment']) {
    if (typeof input[key] === 'string') parts.push(input[key]);
  }
  return parts.join('\n');
}

/**
 * 実行ログから AI の出力テキストを集める。
 *
 * 次の 3 つを見る。**どれか 1 つだけだと action の出力形式が変わったときに黙って通る。**
 *   1. `assistant` イベントの text ブロック（地の文で出す場合）
 *   2. **コメント投稿ツールの入力**（実際の投稿経路。上記のとおり本命である）
 *   3. `result` イベントの最終出力
 */
function collectAssistantText(events) {
  const parts = [];
  for (const e of events) {
    if (!e) continue;
    if (e.type === 'assistant') {
      const content = e.message && e.message.content;
      if (Array.isArray(content)) {
        for (const b of content) {
          if (b && b.type === 'text' && typeof b.text === 'string') parts.push(b.text);
          else if (b && b.type === 'tool_use') {
            const body = commentBodyOf(b);
            if (body) parts.push(body);
          }
        }
      }
    } else if (e.type === 'result' && typeof e.result === 'string') {
      parts.push(e.result);
    }
  }
  return parts.join('\n');
}

/**
 * 判定の見出しが在るかを返す。
 *
 * 見出しレベル（`##`〜`####`）は問わない。**絵文字と語の距離も問わない**
 * （`### 🔴 重大` / `### 🔴 重大（新規）` のどちらも拾う）。
 * ただし**同じ行に絵文字と語の両方が要る**。
 */
function findVerdicts(text) {
  const found = [];
  const missing = [];
  const lines = String(text || '').split('\n');
  for (const v of VERDICTS) {
    const hit = lines.some((line) => {
      if (!/^\s{0,3}#{2,4}\s/.test(line)) return false;
      return line.includes(v.emoji) && line.includes(v.word);
    });
    (hit ? found : missing).push(v);
  }
  return { found, missing };
}

function selfTest() {
  const assert = require('assert');
  const ok = (name, fn) => {
    fn();
    console.log(`  ok  ${name}`);
  };
  let n = 0;
  const T = (name, fn) => {
    n += 1;
    ok(name, fn);
  };

  const full = '## AI コードレビュー結果\n\n### 🔴 重大\n- なし\n\n### 🟡 推奨\n- なし\n\n### 🟢 軽微\n- なし\n';

  T('判定 3 種が揃っていれば missing は空', () => {
    assert.deepStrictEqual(findVerdicts(full).missing, []);
  });

  T('1 つ欠けると検出する', () => {
    const t = full.replace('### 🟢 軽微', '### 軽微');
    assert.strictEqual(findVerdicts(t).missing.length, 1);
    assert.strictEqual(findVerdicts(t).missing[0].word, '軽微');
  });

  T('判定を 1 つも投稿しない形 A を検出する', () => {
    const t = '`dotnet test` をバックグラウンドで実行中です。完了後にサマリを投稿します。';
    assert.strictEqual(findVerdicts(t).missing.length, 3);
  });

  T('本文に語だけあっても緑にしない（散文で発火しない）', () => {
    const t = '## レビュー結果\n\n重大な問題は無い。推奨も軽微も特に無い。\n';
    assert.strictEqual(findVerdicts(t).missing.length, 3);
  });

  T('絵文字だけ・語だけの見出しでは通さない', () => {
    assert.strictEqual(findVerdicts('### 🔴\n### 🟡\n### 🟢\n').missing.length, 3);
    assert.strictEqual(findVerdicts('### 重大\n### 推奨\n### 軽微\n').missing.length, 3);
  });

  T('見出しレベルは ## 〜 #### を許す', () => {
    const t = '## 🔴 重大\n### 🟡 推奨\n#### 🟢 軽微\n';
    assert.deepStrictEqual(findVerdicts(t).missing, []);
  });

  T('見出しでない行に絵文字と語があっても拾わない', () => {
    const t = '- 🔴 重大 の指摘は無い\n- 🟡 推奨 も無い\n- 🟢 軽微 も無い\n';
    assert.strictEqual(findVerdicts(t).missing.length, 3);
  });

  T('見出しの後ろに補足が付いても拾う', () => {
    const t = '### 🔴 重大（新規）\n### 🟡 推奨\n### 🟢 軽微\n';
    assert.deepStrictEqual(findVerdicts(t).missing, []);
  });

  T('assistant イベントの text を集める', () => {
    const events = [{ type: 'assistant', message: { content: [{ type: 'text', text: full }] } }];
    assert.deepStrictEqual(findVerdicts(collectAssistantText(events)).missing, []);
  });

  T('result イベントの最終出力も見る（assistant が空でも拾う）', () => {
    const events = [{ type: 'result', result: full }];
    assert.deepStrictEqual(findVerdicts(collectAssistantText(events)).missing, []);
  });

  T('コメント投稿ツールの入力から判定を拾う（実際の投稿経路）', () => {
    for (const name of COMMENT_TOOLS) {
      const events = [
        { type: 'assistant', message: { content: [{ type: 'tool_use', id: 'x', name, input: { body: full } }] } },
      ];
      assert.deepStrictEqual(findVerdicts(collectAssistantText(events)).missing, [], name);
    }
  });

  T('コメント投稿以外の tool_use は数えない（Bash の引数で緑にしない）', () => {
    const events = [
      {
        type: 'assistant',
        message: { content: [{ type: 'tool_use', id: 'x', name: 'Bash', input: { command: `grep '${full}'` } }] },
      },
    ];
    assert.strictEqual(findVerdicts(collectAssistantText(events)).missing.length, 3);
  });

  console.log(`[check-review-verdict] self-test OK（${n} 件）`);
}

function main(argv) {
  const args = argv.slice(2);
  if (args.includes('--self-test')) {
    selfTest();
    return;
  }
  const file = args.find((a) => !a.startsWith('--'));

  // **実行ログを読めないときは fail-open。** その形（AI がそもそも動かなかった等）は
  // `check-permission-denials.js` が捕まえるため、二重に落とす必要は無い。
  if (!file) {
    warn('[check-review-verdict] 実行ログのパスが渡されていないため skip する（fail-open）。');
    process.exit(0);
  }
  let text;
  try {
    text = fs.readFileSync(file, 'utf8');
  } catch (e) {
    warn(`[check-review-verdict] 実行ログを読めないため skip する（fail-open）: ${file}`);
    process.exit(0);
  }

  const events = parseEvents(text);
  const { missing } = findVerdicts(collectAssistantText(events));

  if (missing.length === 0) {
    console.log('[check-review-verdict] OK: 判定（🔴 重大 / 🟡 推奨 / 🟢 軽微）が投稿されています。');
    process.exit(0);
  }

  const list = missing.map((v) => `${v.emoji} ${v.word}`).join(' / ');
  const message =
    `[check-review-verdict] AI レビューが判定を投稿していません（欠けている見出し: ${list}）。` +
    'ジョブは success でも**内容は検査されていない**。' +
    'PR に残ったコメントを確認し、レビューを再実行してください。' +
    '判定を投稿せずに終わる原因は、バックグラウンド実行やサブエージェントの結果待ちである。';

  if (process.env.ALLOW_MISSING_VERDICT === '1') {
    warn(`${message}（ALLOW_MISSING_VERDICT=1 のため失敗としない）`);
    process.exit(0);
  }
  emit('error', message);
  process.exit(1);
}

if (require.main === module) {
  main(process.argv);
}

module.exports = { collectAssistantText, commentBodyOf, findVerdicts, selfTest, VERDICTS, COMMENT_TOOLS };
