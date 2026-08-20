#!/usr/bin/env node
'use strict';
/*
 * check-trace-blocks.js
 * docs/ 配下の Markdown 全体（.ai-context/ は対象外）に置く trace ブロック／trace-table ブロックの検査器。
 *
 * 書式の正本: project-planning ADR-0029「実装リポジトリの資料再編」決定4（trace ブロック規約）。
 * 本検査器は同決定が「検査の実現手段」として指す実装であり、配備までは書き手と PR レビューが
 * 守っていた規約を機械化する（決定4本文より）。
 *
 * 検査内容:
 *   1. trace ブロックの文法（`<!-- trace: ... -->` が frontmatter 直後・最初の H1 見出し前に
 *      1 文書 1 個。`key: [item, ...]` 形の行を ids/adrs/iadrs/specs/issues の順にすべて持つ）
 *   2. 許可キーは ids/adrs/iadrs/specs/issues のみ（未知キーは error）
 *   3. trace-table ブロック（`<!-- trace-table: -->`）の文法（`rowN: <値>` が row1 から連番）。
 *      trace-table だけがあって trace が無い文書は error（decision4: 隣接する付随情報のため）
 *   4. 値域:
 *      - `ids:` の FR/UC/SC は `.claude/rules/traceability.repo.md` 宣言レンジ（`readPlanIds()`）
 *      - `adrs:` の計画 ADR は同ファイル同節の宣言レンジ（`scripts/lib/plan-ranges.js`）
 *      - `iadrs:` の IADR は `.ai-context/adr/` にファイルが実在すること
 *      - `ids:` の NFR は無採番許容（レンジを持たない）
 *      - 他プロジェクト／他リポジトリの修飾（`<英数字短縮名>:` 接頭辞。個別名はハードコードしない。
 *        利用者裁定・ADR-0029 決定9）が付いたトークンは external とみなし、実在検査の対象外にする
 *   5. 本文（frontmatter・HTML コメント・コードフェンスを除く可視テキスト）に計画 ID・IADR・
 *      修飾付き issue 参照（`<短縮名>#NNN`。個別リポジトリ名はハードコードしない）が
 *      残っていれば error（非表示メタデータ化の趣旨に反するため）。裸の `#NNN`（自リポジトリの
 *      issue 言及）は対象外——現行の docs/ でも通常の言及として残されている
 *
 * trace ブロックを持たない文書は許容する（decision4 は既存文書の即時移行を強制しない）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。違反があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-trace-blocks.js
 *   node scripts/check-trace-blocks.js --self-test   # 検査ロジック自体の自己試験
 */
const fs = require('fs');
const path = require('path');
const { notice } = require('./lib/ci-annotate.js');
const tb = require('./lib/trace-blocks.js');
const tt = require('./check-test-traceability.js');
const planRanges = require('./lib/plan-ranges.js');

const REPO_ROOT = process.env.TRACE_BLOCKS_ROOT
  ? path.resolve(process.env.TRACE_BLOCKS_ROOT)
  : path.resolve(__dirname, '..');

const DOCS_DIR = 'docs';

/**
 * 走査から除外するパスと理由（母集合の規則 6。planning リポ `check-related-graph.js` の
 * 「除外ルートを理由つきで表示する」方式に倣う。実行のたびに必ず出力へ出す）。
 *   - `docs/templates/` … 雛形。記入欄・例示に ID プレースホルダを置くのがテンプレートの目的そのもの
 *     であり、可視本文の ID 残存検査（grep-zero）はここでは成立しない
 *   - `docs/blocked-tasks.md` … ID をキーとする作業台帳。backend のコードコメント・
 *     `.github/workflows/backlog-audit.yml`・`.claude/hooks/check-impl.js` から参照され、
 *     ID を隠すと台帳として機能しない
 */
const EXCLUDED_ROOTS = [
  ['docs/templates/', '雛形。記入欄・例示の ID プレースホルダはテンプレートの目的そのものである'],
  [
    'docs/blocked-tasks.md',
    'ID をキーとする作業台帳。コードコメント・backlog-audit.yml・check-impl.js から参照され、ID を隠すと台帳として機能しない',
  ],
];

/** `EXCLUDED_ROOTS` に基づき、REPO_ROOT 相対パス（`docs/...`。`/` 区切り）が除外対象かを判定する。 */
function isExcludedRelPath(rel) {
  const norm = rel.replace(/\\/g, '/');
  return EXCLUDED_ROOTS.some(([root]) => (root.endsWith('/') ? norm.startsWith(root) : norm === root));
}

// 可視本文に残っていてはならないトークン（HTML コメント・コードフェンスの外）。
// issue 参照は「英数字短縮名 + `#数字`」という形だけを見る（個別リポジトリ名はハードコードしない。
// ADR-0029 決定9）。裸の `#NNN`（自リポジトリ言及）はここには含めない。
const LEAK_PATTERNS = [
  { re: /\b(FR|UC|SC)-\d{1,3}\b/g, label: '計画 ID（FR/UC/SC）' },
  { re: /\bADR-\d{3,4}\b/g, label: '計画 ADR' },
  { re: /\bIADR-\d{3,4}\b/g, label: 'IADR' },
  { re: /\bNFR(?:-[A-Za-z0-9_-]+)?\b/g, label: 'NFR' },
  { re: /\b[A-Za-z][A-Za-z0-9]*#\d+\b/g, label: '修飾付き issue 参照' },
];

/** docs/ 配下の .md を再帰的に集める。隠しディレクトリもスキップしない（decision4 対象範囲）。 */
function collectMdFiles(dir) {
  let out = [];
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const e of entries) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) out = out.concat(collectMdFiles(p));
    else if (e.isFile() && e.name.endsWith('.md')) out.push(p);
  }
  return out;
}

/** `.ai-context/adr/` のファイル名から実在する IADR 番号集合を返す。読めなければ null。 */
function loadIadrIds(root) {
  const dir = path.join(root, '.ai-context', 'adr');
  let entries;
  try {
    entries = fs.readdirSync(dir);
  } catch {
    return null;
  }
  const ids = new Set();
  for (const f of entries) {
    const m = /^(IADR-\d{3,4})_/.exec(f);
    if (m) ids.add(m[1]);
  }
  return ids;
}

/** 検査に要る文脈（計画レンジ・IADR 実在集合）をまとめて読む。 */
function buildContext(root) {
  const planIds = new Set(tt.readPlanIds());
  const adrRange = planRanges.readPlanAdrRange(path.join(root, tt.RULES_FILE));
  const iadrIds = loadIadrIds(root);
  return { planIds, adrRange, iadrIds: iadrIds || new Set(), iadrAvailable: iadrIds !== null };
}

/** FR/UC/SC の裸トークン（例 "FR-5"）を計画書表記（"FR-05"）へ正規化する。 */
function normalizePlanId(kind, bare) {
  const n = Number(bare.slice(kind.length + 1));
  return `${kind}-${String(n).padStart(2, '0')}`;
}

/** 裸（修飾子なし）の ids/adrs/iadrs トークンの実在性を検査する。 */
function checkBareIdMembership(c, ctx) {
  if (c.kind === 'NFR') return []; // 無採番許容（レンジを持たない）
  if (c.kind === 'FR' || c.kind === 'UC' || c.kind === 'SC') {
    const norm = normalizePlanId(c.kind, c.bare);
    return ctx.planIds.has(norm) ? [] : [`計画書に存在しない ID です: ${c.bare}`];
  }
  if (c.kind === 'ADR') {
    return planRanges.isAdrInRange(c.bare, ctx.adrRange)
      ? []
      : [`計画 ADR のレンジ外です（${ctx.adrRange.from}〜${ctx.adrRange.to}）: ${c.bare}`];
  }
  if (c.kind === 'IADR') {
    if (!ctx.iadrAvailable) return []; // .ai-context/adr/ を読めない環境では skip（呼び出し側で notice 済み）
    return ctx.iadrIds.has(c.bare) ? [] : [`.ai-context/adr/ に実在しない IADR です: ${c.bare}`];
  }
  return [];
}

/** `ids:`/`adrs:`/`iadrs:`/`specs:`/`issues:` の 1 トークンを検査する。 */
function checkTraceValueToken(key, raw, ctx) {
  if (key === 'specs') {
    const c = tb.classifySpecToken(raw);
    return c.valid ? [] : [`"specs" 内のトークンが不正です: ${JSON.stringify(raw)}`];
  }
  if (key === 'issues') {
    const c = tb.classifyIssueToken(raw);
    return c.valid
      ? []
      : [`"issues" 内のトークンが不正です（\`#123\` または \`<短縮名>#123\` 形式ではありません）: ${JSON.stringify(raw)}`];
  }
  const c = tb.classifyIdToken(raw);
  if (!c.kind) return [`"${key}" 内のトークンの形式が不正です: ${JSON.stringify(raw)}`];
  if (!tb.KEY_ALLOWED_KINDS[key].has(c.kind)) {
    return [`"${key}" に ${c.kind} 種別のトークンは置けません（キーと種別が一致しません）: ${JSON.stringify(raw)}`];
  }
  if (c.external) return []; // 修飾付き（他プロジェクト／他リポジトリ）は external・実在検査対象外
  return checkBareIdMembership(c, ctx);
}

/** trace-table の 1 セル（`rowN: a, b` の a・b）を検査する。種別はトークン自身の形から自動判定する。 */
function checkTraceTableToken(rowN, raw, ctx) {
  const c = tb.classifyIdToken(raw);
  if (!c.kind) return [`trace-table row${rowN} のトークンの形式が不正です: ${JSON.stringify(raw)}`];
  if (c.external) return [];
  return checkBareIdMembership(c, ctx).map((e) => `trace-table row${rowN}: ${e}`);
}

/** `<!-- trace: -->` ブロック 1 個を検査する。 */
function checkTraceBlock(content, block, fmEnd, h1Idx, ctx) {
  const errors = [];
  if (block.unterminated) return ['trace ブロックの閉じタグ `-->` が見つかりません'];
  if (block.start !== fmEnd) {
    errors.push(`trace ブロックが frontmatter 直後にありません（位置 ${block.start}、期待位置 ${fmEnd}）`);
  }
  if (h1Idx !== -1 && block.blockEnd > h1Idx) {
    errors.push('trace ブロックが最初の H1 見出しより後にあります');
  }
  const split = tb.splitBlockLines(block.body);
  if (split.error) {
    errors.push(`trace ブロックの文法エラー: ${split.error}`);
    return errors;
  }
  const parsed = tb.parseTraceBlockLines(split.lines);
  for (const e of parsed.errors) errors.push(`trace ブロック: ${e}`);

  const seen = parsed.keysSeen;
  const dupe = [...new Set(seen.filter((k, i) => seen.indexOf(k) !== i))];
  if (dupe.length) errors.push(`trace ブロックにキーの重複があります: ${dupe.join(', ')}`);

  const knownSeen = seen.filter((k) => tb.KEY_ORDER.includes(k));
  const orderOk =
    knownSeen.length === tb.KEY_ORDER.length && tb.KEY_ORDER.every((k, i) => knownSeen[i] === k);
  if (!orderOk) {
    errors.push(
      `trace ブロックは ${tb.KEY_ORDER.join(', ')} をこの順ですべて 1 回ずつ持つ必要があります`
        + `（実際: ${knownSeen.join(', ') || '(なし)'}）`
    );
  }

  for (const entry of parsed.entries) {
    for (const item of entry.items) {
      errors.push(...checkTraceValueToken(entry.key, item, ctx));
    }
  }
  return errors;
}

/** `<!-- trace-table: -->` ブロック 1 個を検査する。 */
function checkTraceTableBlock(block, ctx) {
  if (block.unterminated) return ['trace-table ブロックの閉じタグ `-->` が見つかりません'];
  const errors = [];
  const split = tb.splitBlockLines(block.body);
  if (split.error) return [`trace-table ブロックの文法エラー: ${split.error}`];
  const parsed = tb.parseTraceTableLines(split.lines);
  for (const e of parsed.errors) errors.push(`trace-table ブロック: ${e}`);
  for (const row of parsed.rows) {
    for (const item of row.items) errors.push(...checkTraceTableToken(row.n, item, ctx));
  }
  return errors;
}

/** frontmatter・HTML コメント・コードフェンスを除いた「可視本文」を返す（長さは保つ）。 */
function stripForLeakScan(content) {
  const fmEnd = tb.frontmatterEnd(content);
  let body = content.slice(fmEnd);
  body = body.replace(/<!--[\s\S]*?-->/g, (m) => ' '.repeat(m.length));
  body = body.replace(/```[\s\S]*?```/g, (m) => ' '.repeat(m.length));
  return body;
}

/** 可視本文に計画 ID / IADR / 修飾付き issue 参照が残っていないかを検査する。 */
function checkVisibleLeaks(content) {
  const body = stripForLeakScan(content);
  const errors = [];
  for (const { re, label } of LEAK_PATTERNS) {
    re.lastIndex = 0;
    const found = new Set();
    let m;
    while ((m = re.exec(body)) !== null) found.add(m[0]);
    if (found.size) {
      errors.push(
        `可視本文に${label}が残っています（HTML コメント外・コードフェンス外。trace ブロックへ収穫すること）: `
          + [...found].sort().join(', ')
      );
    }
  }
  return errors;
}

/** 1 ファイル分を検査する。 */
function checkContent(content, ctx) {
  const errors = [];
  const fmEnd = tb.frontmatterEnd(content);
  const h1Idx = tb.firstH1Index(content);
  const traceBlocks = tb.findTraceBlocks(content);
  const tableBlocks = tb.findTraceTableBlocks(content);

  if (traceBlocks.length === 0) {
    if (tableBlocks.length > 0) {
      errors.push('trace-table ブロックはあるが trace ブロックがありません（trace-table は trace に隣接する付随情報である）');
    }
  } else {
    if (traceBlocks.length > 1) {
      errors.push(`trace ブロックが複数あります（${traceBlocks.length} 個。1 文書 1 ブロックの規約違反）`);
    }
    errors.push(...checkTraceBlock(content, traceBlocks[0], fmEnd, h1Idx, ctx));
  }

  for (const block of tableBlocks) {
    errors.push(...checkTraceTableBlock(block, ctx));
  }

  errors.push(...checkVisibleLeaks(content));
  return errors;
}

// --- 自己試験 -------------------------------------------------------------------

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });

  const ctx = {
    planIds: new Set(['FR-01', 'FR-02', 'UC-01', 'SC-01']),
    adrRange: { from: 1, to: 5 },
    iadrIds: new Set(['IADR-0001']),
    iadrAvailable: true,
  };

  const VALID_DOC =
    '---\ntitle: x\n---\n'
    + '<!-- trace:\nids: [FR-01, UC-01]\nadrs: [ADR-0003]\niadrs: [IADR-0001]\nspecs: [20260101_x]\nissues: [#1]\n-->\n\n\n'
    + '# タイトル\n\n本文。\n';

  t('正例: 5 キー・全値有効な文書は違反 0 件', checkContent(VALID_DOC, ctx).length === 0, checkContent(VALID_DOC, ctx));

  t(
    '正例: trace ブロックが無い文書は許容する',
    checkContent('---\ntitle: x\n---\n\n# タイトル\n\n本文。\n', ctx).length === 0
  );

  t(
    '正例: frontmatter が無く先頭が trace ブロックでも許容する',
    checkContent(
      '<!-- trace:\nids: []\nadrs: []\niadrs: []\nspecs: []\nissues: []\n-->\n\n# T\n\n本文\n',
      ctx
    ).length === 0
  );

  t(
    '負例: frontmatter とのあいだに空行があると placement 違反',
    checkContent(
      '---\ntitle: x\n---\n\n'
        + '<!-- trace:\nids: []\nadrs: []\niadrs: []\nspecs: []\nissues: []\n-->\n\n# T\n',
      ctx
    ).some((e) => e.includes('frontmatter 直後'))
  );

  t(
    '負例: trace ブロックが H1 より後にあると違反',
    checkContent(
      '---\ntitle: x\n---\n# T\n\n<!-- trace:\nids: []\nadrs: []\niadrs: []\nspecs: []\nissues: []\n-->\n',
      ctx
    ).some((e) => e.includes('H1 見出しより後'))
  );

  t(
    '負例: 未知キーは error',
    checkContent(
      '---\ntitle: x\n---\n<!-- trace:\nids: []\nadrs: []\niadrs: []\nspecs: []\nissues: []\nfoo: [bar]\n-->\n\n# T\n',
      ctx
    ).some((e) => e.includes('未知のキー'))
  );

  t(
    '負例: キーが 1 つ欠けていると違反（完全性）',
    checkContent(
      '---\ntitle: x\n---\n<!-- trace:\nids: []\nadrs: []\niadrs: []\nspecs: []\n-->\n\n# T\n',
      ctx
    ).some((e) => e.includes('この順ですべて 1 回ずつ'))
  );

  t(
    '負例: キーの順序が違うと違反',
    checkContent(
      '---\ntitle: x\n---\n<!-- trace:\nadrs: []\nids: []\niadrs: []\nspecs: []\nissues: []\n-->\n\n# T\n',
      ctx
    ).some((e) => e.includes('この順ですべて 1 回ずつ'))
  );

  t(
    '負例: キーの重複は違反',
    checkContent(
      '---\ntitle: x\n---\n<!-- trace:\nids: []\nids: []\nadrs: []\niadrs: []\nspecs: []\nissues: []\n-->\n\n# T\n',
      ctx
    ).some((e) => e.includes('重複'))
  );

  t(
    '負例: ids に計画書実在しない FR は違反',
    checkContent(
      '---\ntitle: x\n---\n<!-- trace:\nids: [FR-99]\nadrs: []\niadrs: []\nspecs: []\nissues: []\n-->\n\n# T\n',
      ctx
    ).some((e) => e.includes('計画書に存在しない ID'))
  );

  t(
    '負例: adrs がレンジ外なら違反',
    checkContent(
      '---\ntitle: x\n---\n<!-- trace:\nids: []\nadrs: [ADR-0099]\niadrs: []\nspecs: []\nissues: []\n-->\n\n# T\n',
      ctx
    ).some((e) => e.includes('計画 ADR のレンジ外'))
  );

  t(
    '負例: iadrs がファイル実在しなければ違反',
    checkContent(
      '---\ntitle: x\n---\n<!-- trace:\nids: []\nadrs: []\niadrs: [IADR-9999]\nspecs: []\nissues: []\n-->\n\n# T\n',
      ctx
    ).some((e) => e.includes('実在しない IADR'))
  );

  t(
    '負例: ids に ADR 種別のトークンは置けない（キーと種別の不一致）',
    checkContent(
      '---\ntitle: x\n---\n<!-- trace:\nids: [ADR-0001]\nadrs: []\niadrs: []\nspecs: []\nissues: []\n-->\n\n# T\n',
      ctx
    ).some((e) => e.includes('種別のトークンは置けません'))
  );

  t(
    '正例: NFR は無採番でも採番済みでも常に許容する',
    checkContent(
      '---\ntitle: x\n---\n<!-- trace:\nids: [NFR, NFR-05]\nadrs: []\niadrs: []\nspecs: []\nissues: []\n-->\n\n# T\n',
      ctx
    ).length === 0
  );

  t(
    '正例: 修飾付き（他プロジェクト／他リポジトリ）は個別名を問わず external として実在検査しない',
    checkContent(
      '---\ntitle: x\n---\n<!-- trace:\nids: [MSP:FR-999, ZZZ:UC-777]\nadrs: [ANYNAME:ADR-9999]\niadrs: [MSP:IADR-9999]\nspecs: []\nissues: []\n-->\n\n# T\n',
      ctx
    ).length === 0
  );

  t(
    '負例: issues に不正なトークンは違反',
    checkContent(
      '---\ntitle: x\n---\n<!-- trace:\nids: []\nadrs: []\niadrs: []\nspecs: []\nissues: [not-an-issue]\n-->\n\n# T\n',
      ctx
    ).some((e) => e.includes('"issues" 内'))
  );

  t(
    '正例: issues は裸番号・修飾付き番号のいずれも許容する',
    checkContent(
      '---\ntitle: x\n---\n<!-- trace:\nids: []\nadrs: []\niadrs: []\nspecs: []\nissues: [#1, planning#2, MSP#3]\n-->\n\n# T\n',
      ctx
    ).length === 0
  );

  t(
    '負例: trace-table だけがあって trace が無いと違反',
    checkContent('---\ntitle: x\n---\n\n# T\n\n<!-- trace-table:\nrow1: FR-01\n-->\n', ctx).some((e) =>
      e.includes('trace ブロックがありません')
    )
  );

  t(
    '正例: trace-table の行番号が row1 から連番なら許容する',
    checkContent(
      VALID_DOC.replace('本文。\n', '本文。\n\n<!-- trace-table:\nrow1: FR-01\nrow2: FR-01, ADR-0003\n-->\n'),
      ctx
    ).length === 0
  );

  t(
    '負例: trace-table の行番号が連番でないと違反',
    checkContent(
      VALID_DOC.replace('本文。\n', '本文。\n\n<!-- trace-table:\nrow1: FR-01\nrow3: FR-01\n-->\n'),
      ctx
    ).some((e) => e.includes('連番'))
  );

  t(
    '負例: 可視本文に計画 ID が残っていると違反',
    checkContent(
      '---\ntitle: x\n---\n<!-- trace:\nids: []\nadrs: []\niadrs: []\nspecs: []\nissues: []\n-->\n\n\n# T\n\nFR-01 の詳細。\n',
      ctx
    ).some((e) => e.includes('計画 ID（FR/UC/SC）'))
  );

  t(
    '正例: コードフェンス内の計画 ID は可視漏れとして検出しない',
    checkContent(
      '---\ntitle: x\n---\n<!-- trace:\nids: []\nadrs: []\niadrs: []\nspecs: []\nissues: []\n-->\n\n\n'
        + '# T\n\n```\nFR-01 のコード例\n```\n',
      ctx
    ).length === 0
  );

  t(
    '負例: インラインコード内の計画 ID は可視漏れとして検出する（コードフェンスのみが除外対象）',
    checkContent(
      '---\ntitle: x\n---\n<!-- trace:\nids: []\nadrs: []\niadrs: []\nspecs: []\nissues: []\n-->\n\n\n'
        + '# T\n\n`FR-01` を参照。\n',
      ctx
    ).some((e) => e.includes('計画 ID（FR/UC/SC）'))
  );

  t(
    '正例: 一般の HTML コメント内の計画 ID は可視漏れとして検出しない',
    checkContent(
      '---\ntitle: x\n---\n<!-- trace:\nids: []\nadrs: []\niadrs: []\nspecs: []\nissues: []\n-->\n\n\n'
        + '# T\n\n<!-- 旧: FR-01 を参照していた -->\n本文。\n',
      ctx
    ).length === 0
  );

  t(
    '負例: 可視本文の planning#N は違反、裸の #N（自リポ言及）は違反にしない',
    (() => {
      const errs = checkContent(
        '---\ntitle: x\n---\n<!-- trace:\nids: []\nadrs: []\niadrs: []\nspecs: []\nissues: []\n-->\n\n\n'
          + '# T\n\n続報は planning#123、詳細は #99 を参照。\n',
        ctx
      );
      return errs.some((e) => e.includes('修飾付き issue 参照')) && !errs.some((e) => e.includes('#99'));
    })()
  );

  t(
    '負例: trace ブロックが複数あると違反',
    checkContent(
      '---\ntitle: x\n---\n<!-- trace:\nids: []\nadrs: []\niadrs: []\nspecs: []\nissues: []\n-->\n'
        + '<!-- trace:\nids: []\nadrs: []\niadrs: []\nspecs: []\nissues: []\n-->\n\n# T\n',
      ctx
    ).some((e) => e.includes('複数あります'))
  );

  // --- 除外ルート（母集合の規則 6。docs/templates/・docs/blocked-tasks.md） ---
  t(
    '正例: docs/templates/ 配下は除外対象と判定する',
    isExcludedRelPath('docs/templates/adr_template.md')
  );
  t(
    '正例: docs/blocked-tasks.md は除外対象と判定する',
    isExcludedRelPath('docs/blocked-tasks.md')
  );
  t(
    '負例: docs/templates という名前だけの兄弟ファイルは除外しない（前方一致の誤爆防止）',
    !isExcludedRelPath('docs/templates-overview.md')
  );
  t(
    '負例: 通常の docs/ 文書は除外しない',
    !isExcludedRelPath('docs/functional/FR-10_risk-controls.md')
  );
  t(
    '正例: 除外ファイルは可視本文に計画 ID が残っていても走査対象外なので違反にならない',
    (() => {
      const files0 = collectMdFiles(path.join(REPO_ROOT, DOCS_DIR));
      const templateFiles = files0.filter((fp) => isExcludedRelPath(path.relative(REPO_ROOT, fp)));
      // 実ツリーに docs/templates/ 配下のファイルが存在すること（判定が空振りしていないこと）の確認。
      return templateFiles.some((fp) => fp.includes(`${path.sep}templates${path.sep}`));
    })()
  );
  t(
    '正例: docs/blocked-tasks.md は実ツリーに存在し、除外リストに一致する',
    (() => {
      const rels = collectMdFiles(path.join(REPO_ROOT, DOCS_DIR))
        .map((fp) => path.relative(REPO_ROOT, fp).replace(/\\/g, '/'))
        .filter((rel) => rel === 'docs/blocked-tasks.md');
      return rels.length === 1 && rels.every((rel) => isExcludedRelPath(rel));
    })()
  );

  // --- lib/plan-ranges.js: 実ツリーの traceability.repo.md からレンジを読めること ---
  t(
    'plan-ranges: 実ツリーから計画 ADR レンジを読める（fail-loud の裏返し）',
    (() => {
      try {
        const r = planRanges.readPlanAdrRange();
        return Number.isInteger(r.from) && Number.isInteger(r.to) && r.to >= r.from;
      } catch {
        return false;
      }
    })()
  );

  t(
    'plan-ranges: レンジ宣言が無い規約は例外（黙って skip しない）',
    (() => {
      const fs2 = require('fs');
      const os2 = require('os');
      const path2 = require('path');
      const f = path2.join(fs2.mkdtempSync(path2.join(os2.tmpdir(), 'trace-adr-range-')), 'rules.md');
      fs2.writeFileSync(f, '## 起点 ID の種別（固有）\n\n`FR-01..21` / `UC-01..07` / `SC-01..03`\n');
      try {
        planRanges.readPlanAdrRange(f);
        return false;
      } catch (e) {
        return /ADR-0001\.\.0029/.test(e.message) || /レンジ/.test(e.message);
      }
    })()
  );

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) {
      failed++;
      if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual));
    }
  }
  if (failed) {
    console.error(`[check-trace-blocks] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-trace-blocks] 自己試験 ${cases.length} 件 OK。`);
}

function main() {
  if (process.argv.includes('--self-test')) {
    selfTest();
    return;
  }
  const allFiles = collectMdFiles(path.join(REPO_ROOT, DOCS_DIR));
  if (allFiles.length === 0) {
    console.error(`[check-trace-blocks] ${DOCS_DIR}/ 配下に Markdown が 1 件もありません。`);
    process.exit(1);
  }
  console.log('[check-trace-blocks] 除外ルート（母集合の規則 6。走査対象から外す。理由は各行末）:');
  for (const [root, reason] of EXCLUDED_ROOTS) console.log(`  - ${root} … ${reason}`);
  const files = allFiles.filter((fp) => !isExcludedRelPath(path.relative(REPO_ROOT, fp)));
  console.log(
    `[check-trace-blocks] 走査対象 ${files.length} 件（除外 ${allFiles.length - files.length} 件を差し引いた後）`
  );
  let ctx;
  try {
    ctx = buildContext(REPO_ROOT);
  } catch (e) {
    console.error(`[check-trace-blocks] 計画レンジを読めません: ${e.message}`);
    process.exit(1);
  }
  if (!ctx.iadrAvailable) {
    notice('check-trace-blocks: .ai-context/adr/ を読めないため IADR 実在性検査を skip しました');
  }

  let total = 0;
  const report = [];
  for (const fp of files) {
    let content;
    try {
      content = fs.readFileSync(fp, 'utf8');
    } catch {
      continue;
    }
    const rel = path.relative(REPO_ROOT, fp);
    const errs = checkContent(content, ctx);
    if (errs.length) {
      total += errs.length;
      report.push({ rel, errs });
    }
  }

  if (total === 0) {
    console.log(`[check-trace-blocks] OK: ${files.length} 件の Markdown に trace ブロックの違反はありません。`);
    process.exit(0);
  }
  console.error(`[check-trace-blocks] 違反 ${total} 件（${report.length} / ${files.length} ファイル）を検出しました:`);
  for (const r of report) {
    console.error(`\n  ${r.rel}`);
    for (const e of r.errs) console.error(`    - ${e}`);
  }
  console.error(
    '\n書式は project-planning ADR-0029 決定4（.claude/rules/traceability.md の trace ブロック規約）を参照してください。'
  );
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  REPO_ROOT,
  DOCS_DIR,
  EXCLUDED_ROOTS,
  isExcludedRelPath,
  collectMdFiles,
  loadIadrIds,
  buildContext,
  checkContent,
  checkTraceValueToken,
  checkTraceTableToken,
  checkBareIdMembership,
  checkVisibleLeaks,
  stripForLeakScan,
  normalizePlanId,
  selfTest,
};
