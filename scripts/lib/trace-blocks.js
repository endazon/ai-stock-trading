'use strict';
/*
 * trace-blocks.js — docs/ の trace ブロック・trace-table ブロックの共有パーサ／分類ロジック。
 *
 * 書式の正本は project-planning ADR-0029 決定4（`.claude/rules/traceability.md` の
 * 「trace ブロック規約」節・kit V2）。本モジュールはその書式を読み書きする最小限の純関数を持ち、
 * `check-trace-blocks.js`（検査器）と `gen-knowledge-graph.js`（生成器）の両方から requires される
 * 単一情報源である（同じ文法パーサを 2 本持たないため）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。副作用（ファイル I/O・プロセス終了）を持たない。
 *
 * 【他プロジェクト／他リポジトリの修飾子について】
 * 実装リポジトリは今後増える前提のため、`MSP:` のような個別リポジトリ名をこのモジュールへ
 * ハードコードしない（利用者裁定・ADR-0029 決定9）。かわりに「英字 1 文字＋英数字の短縮名 + `:`」
 * という**形**だけを見て、一致すれば無条件に external（実在検査の対象外・数のみ）として扱う
 * （QUALIFIER_RE）。将来のリポジトリが増えても本モジュールの変更は要らない。
 */

/** trace ブロックの開始マーカー（trace-table と前方一致しないよう `:` まで含めて判定する）。 */
const TRACE_MARKER = '<!-- trace:';
/** trace-table ブロックの開始マーカー。 */
const TRACE_TABLE_MARKER = '<!-- trace-table:';
const COMMENT_CLOSE = '-->';

/** trace ブロックの許可キー（ADR-0029 決定4）。この順序で 1 つずつ持つ。 */
const KEY_ORDER = ['ids', 'adrs', 'iadrs', 'specs', 'issues'];

/** `key: [item, item]` 形の行（キー名は緩く取り、未知キーの検出にも使う）。 */
const KEY_LINE_RE = /^([A-Za-z][A-Za-z0-9_-]*): \[(.*)\]$/;

/** trace-table の行（`rowN: id, id`）。 */
const ROW_LINE_RE = /^row(\d+): (.+)$/;

/** 計画 ID / IADR の形（裸＝修飾子なし）。 */
const ID_SHAPES = {
  FR: /^FR-(\d{1,3})$/,
  UC: /^UC-(\d{1,3})$/,
  SC: /^SC-(\d{1,3})$/,
  ADR: /^ADR-(\d{3,4})$/,
  IADR: /^IADR-(\d{3,4})$/,
  NFR: /^NFR(?:-[A-Za-z0-9_-]+)?$/,
  // フェーズ骨格 ID（P0〜P3）。CLAUDE.md / check-commit-messages.js の起点 ID 語彙と同じもので、
  // `.ai-context/` frontmatter の related_ids に現れる（例: `related_ids: [P3]`）。計画ID/IADR
  // とは別の軸（工程管理）であり、レンジは持たない（NFR と同じ扱い）。
  P: /^P[0-3]$/,
};

/** `ids:` キーに許すトークン種別。`adrs:`/`iadrs:` はそれぞれ単一種別のみ。 */
const KEY_ALLOWED_KINDS = {
  ids: new Set(['FR', 'UC', 'SC', 'NFR']),
  adrs: new Set(['ADR']),
  iadrs: new Set(['IADR']),
};

/**
 * 修飾子（他プロジェクト／他リポジトリを指す接頭辞）の汎用規則。
 * 個別名（`MSP` 等）を列挙しない —— 「英字＋英数字の短縮名 + `:`」の形に一致すれば
 * 無条件に external とする（ADR-0029 決定9）。
 */
const QUALIFIER_RE = /^([A-Za-z][A-Za-z0-9]*):(.+)$/;

/** issues キーのトークン（`#123` / `<修飾子>#123`）。修飾子は同じ汎用規則。 */
const ISSUE_BARE_RE = /^#(\d+)$/;
const ISSUE_QUALIFIED_RE = /^([A-Za-z][A-Za-z0-9]*)#(\d+)$/;

/**
 * frontmatter（先頭 `---` 〜 `---`）の直後の位置を返す。frontmatter が無ければ 0。
 * `check-doc-links.js` と同じ正規表現（`^---\n...\n---` を先頭一致で見る）。
 */
function frontmatterEnd(content) {
  const m = /^---\r?\n[\s\S]*?\r?\n---\r?\n/.exec(content);
  return m ? m[0].length : 0;
}

/** 本文中で最初に現れる ATX H1（`# ...`）の開始位置。無ければ -1。 */
function firstH1Index(content) {
  const m = /^#[^#].*$|^#$/m.exec(content);
  return m ? m.index : -1;
}

/**
 * `<!-- trace: ... -->` に相当するコメントブロックを 1 つ切り出す。
 * `markerStart` はマーカー文字列の開始位置、`markerLen` はマーカー文字列長。
 * 戻り値: { bodyStart, bodyEnd, blockEnd, body } または閉じタグが無ければ null。
 * `body` はマーカー直後 〜 `-->` 直前のテキスト（改行を含む）。
 */
function sliceCommentBlock(content, markerStart, markerLen) {
  const bodyStart = markerStart + markerLen;
  const bodyEnd = content.indexOf(COMMENT_CLOSE, bodyStart);
  if (bodyEnd === -1) return null;
  return {
    bodyStart,
    bodyEnd,
    blockEnd: bodyEnd + COMMENT_CLOSE.length,
    body: content.slice(bodyStart, bodyEnd),
  };
}

/**
 * コメントブロックの中身（`sliceCommentBlock` の `body`）を行配列へ分解する。
 * 先頭・末尾が改行で無ければ文法エラー（`error` を返す）。
 */
function splitBlockLines(body) {
  if (!body.startsWith('\n')) {
    return { error: 'マーカー（`<!-- trace:` 等）の直後に改行がありません' };
  }
  if (!body.endsWith('\n')) {
    return { error: '`-->` の直前に改行がありません（閉じタグが行頭にありません）' };
  }
  const inner = body.slice(1, -1);
  if (inner === '') return { lines: [] };
  return { lines: inner.split('\n') };
}

/** 全ての `<!-- trace: -->` ブロックを検出する（複数あれば呼び出し側が違反として扱う）。 */
function findTraceBlocks(content) {
  const out = [];
  let from = 0;
  for (;;) {
    const start = content.indexOf(TRACE_MARKER, from);
    if (start === -1) break;
    const sliced = sliceCommentBlock(content, start, TRACE_MARKER.length);
    if (!sliced) {
      out.push({ start, unterminated: true });
      break;
    }
    out.push({ start, ...sliced });
    from = sliced.blockEnd;
  }
  return out;
}

/** 全ての `<!-- trace-table: -->` ブロックを検出する。 */
function findTraceTableBlocks(content) {
  const out = [];
  let from = 0;
  for (;;) {
    const start = content.indexOf(TRACE_TABLE_MARKER, from);
    if (start === -1) break;
    const sliced = sliceCommentBlock(content, start, TRACE_TABLE_MARKER.length);
    if (!sliced) {
      out.push({ start, unterminated: true });
      break;
    }
    out.push({ start, ...sliced });
    from = sliced.blockEnd;
  }
  return out;
}

/** `[a, b, c]` の中身をトークン配列へ分解する。空配列は `[]`。空要素（連続カンマ）があれば `hasEmpty: true`。 */
function parseArrayItems(bracketInner) {
  const trimmed = bracketInner.trim();
  if (trimmed === '') return { items: [], hasEmpty: false };
  const items = trimmed.split(',').map((s) => s.trim());
  return { items, hasEmpty: items.some((s) => s === '') };
}

/**
 * `<!-- trace: -->` ブロックの中身（行配列）を構造化する。
 * 戻り値: { entries: [{ key, raw, items, hasEmpty }], errors: [文字列], keysSeen: [キー名の出現順] }
 */
function parseTraceBlockLines(lines) {
  const errors = [];
  const entries = [];
  const keysSeen = [];
  for (const line of lines) {
    const m = KEY_LINE_RE.exec(line);
    if (!m) {
      errors.push(`不正な行です（\`key: [item, ...]\` 形式ではありません）: ${JSON.stringify(line)}`);
      continue;
    }
    const [, key, inner] = m;
    keysSeen.push(key);
    if (!KEY_ORDER.includes(key)) {
      errors.push(`未知のキーです: "${key}"（許可キー: ${KEY_ORDER.join('/')}）`);
      continue;
    }
    const { items, hasEmpty } = parseArrayItems(inner);
    if (hasEmpty) errors.push(`"${key}" に空の要素があります（カンマの連続・末尾カンマ）: ${JSON.stringify(line)}`);
    entries.push({ key, raw: line, items, hasEmpty });
  }
  return { entries, errors, keysSeen };
}

/**
 * `<!-- trace-table: -->` ブロックの中身（行配列）を構造化する。
 * 行番号は `row1` から連番であることを要求する（ADR-0029 決定4「表の ID は隣接する trace-table へ」・
 * 実データ 6 ファイル全件が row1..N の連番だったことを踏まえた文法）。
 * 戻り値: { rows: [{ n, raw, value, items }], errors: [文字列] }
 */
function parseTraceTableLines(lines) {
  const errors = [];
  const rows = [];
  let expected = 1;
  for (const line of lines) {
    const m = ROW_LINE_RE.exec(line);
    if (!m) {
      errors.push(`不正な行です（\`rowN: <値>\` 形式ではありません）: ${JSON.stringify(line)}`);
      continue;
    }
    const n = Number(m[1]);
    const value = m[2];
    if (n !== expected) {
      errors.push(`行番号が連番ではありません（"row${expected}" を期待、"row${n}" が現れました）`);
    }
    expected = n + 1;
    const items = value.split(',').map((s) => s.trim()).filter((s) => s !== '');
    if (items.length === 0) errors.push(`row${n} の値が空です: ${JSON.stringify(line)}`);
    rows.push({ n, raw: line, value, items });
  }
  return { rows, errors };
}

/** 裸トークン（修飾子を外した後）の種別（FR/UC/SC/ADR/IADR/NFR）。該当なしは null。 */
function detectKind(bareToken) {
  if (ID_SHAPES.NFR.test(bareToken)) return 'NFR';
  if (ID_SHAPES.P.test(bareToken)) return 'P';
  for (const k of ['FR', 'UC', 'SC', 'ADR', 'IADR']) {
    if (ID_SHAPES[k].test(bareToken)) return k;
  }
  return null;
}

/**
 * `ids:`/`adrs:`/`iadrs:` の 1 トークンを分類する。
 * 修飾子（`<英数字短縮名>:`）が付いていれば external（実在検査の対象外）とする。
 * 個別のリポジトリ名は判定に使わない（形だけを見る。ADR-0029 決定9）。
 */
function classifyIdToken(raw) {
  const trimmed = String(raw).trim();
  const m = QUALIFIER_RE.exec(trimmed);
  const qualifier = m ? m[1] : null;
  const bare = m ? m[2] : trimmed;
  const kind = detectKind(bare);
  return { raw: trimmed, qualifier, bare, kind, external: qualifier !== null };
}

/** issues キーの 1 トークンを分類する。`#123` または `<修飾子>#123`。 */
function classifyIssueToken(raw) {
  const trimmed = String(raw).trim();
  let m = ISSUE_BARE_RE.exec(trimmed);
  if (m) return { raw: trimmed, qualifier: null, number: Number(m[1]), valid: true, external: false };
  m = ISSUE_QUALIFIED_RE.exec(trimmed);
  if (m) return { raw: trimmed, qualifier: m[1], number: Number(m[2]), valid: true, external: true };
  return { raw: trimmed, qualifier: null, number: null, valid: false, external: false };
}

/** specs キー（拡張子なしベース名。基本的な字面検査のみ、実在検査は行わない）の 1 トークン。 */
function classifySpecToken(raw) {
  const trimmed = String(raw).trim();
  const valid = trimmed.length > 0 && !/[[\]\s]/.test(trimmed);
  return { raw: trimmed, valid };
}

module.exports = {
  TRACE_MARKER,
  TRACE_TABLE_MARKER,
  COMMENT_CLOSE,
  KEY_ORDER,
  KEY_LINE_RE,
  ROW_LINE_RE,
  ID_SHAPES,
  KEY_ALLOWED_KINDS,
  QUALIFIER_RE,
  ISSUE_BARE_RE,
  ISSUE_QUALIFIED_RE,
  frontmatterEnd,
  firstH1Index,
  sliceCommentBlock,
  splitBlockLines,
  findTraceBlocks,
  findTraceTableBlocks,
  parseArrayItems,
  parseTraceBlockLines,
  parseTraceTableLines,
  detectKind,
  classifyIdToken,
  classifyIssueToken,
  classifySpecToken,
};
