#!/usr/bin/env node
'use strict';
/*
 * gen-knowledge-graph.js
 * docs/ の trace ブロック・trace-table ブロックと、.ai-context/{adr,specs}/ の frontmatter
 * （related_ids / related_specs / plan_refs）からナレッジグラフを組み立てる生成器。
 *
 * 入力:
 *   - docs/ 配下の Markdown（.ai-context/ は含まない）の trace / trace-table ブロック
 *     （`scripts/lib/trace-blocks.js` を requires。パーサ・分類規則は check-trace-blocks.js と共有）
 *   - `.ai-context/adr/*.md`・`.ai-context/specs/*.md` の frontmatter（related_ids / related_specs /
 *     plan_refs）。`planning:` 接頭辞（および任意の `<英数字短縮名>:` 接頭辞。個別リポジトリ名は
 *     ハードコードしない。ADR-0029 決定9）の値は external として扱う
 *
 * 出力（生成物はコミットしない。都度標準出力へ書く）:
 *   --json                 グラフ（nodes / edges）を JSON で出力
 *   --mermaid [--scope dir] Mermaid `graph LR` を出力。--scope を渡すと、そのディレクトリ配下の
 *                           ノードとそれに接続するエッジだけへ絞り込む
 *   --check                in-repo 実在検査（計画 ID / 計画 ADR はレンジ、IADR はファイル実在）。
 *                           external（修飾付き参照）は数のみ数え、実在検査の対象にしない。
 *                           specs / related_specs の解決は best-effort であり、解決できなくても
 *                           それだけでは失敗させない（点在参照の中には計画リポジトリ側の文書も
 *                           混在しており、本リポジトリ単独では実在を判定できないため）
 *   （フラグ省略時は --json と同じ）
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 使い方:
 *   node scripts/gen-knowledge-graph.js --json
 *   node scripts/gen-knowledge-graph.js --mermaid --scope docs/functional
 *   node scripts/gen-knowledge-graph.js --check
 *   node scripts/gen-knowledge-graph.js --self-test
 */
const fs = require('fs');
const path = require('path');
const tb = require('./lib/trace-blocks.js');
const tt = require('./check-test-traceability.js');
const planRanges = require('./lib/plan-ranges.js');

const REPO_ROOT = process.env.KNOWLEDGE_GRAPH_ROOT
  ? path.resolve(process.env.KNOWLEDGE_GRAPH_ROOT)
  : path.resolve(__dirname, '..');

/** docs/ 配下の .md を再帰的に集める（check-trace-blocks.js と同じ走査規則。隠しディレクトリも見る）。 */
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

/** frontmatter（先頭 `---` 〜 `---`）の中身のテキストだけを返す。無ければ null。 */
function extractFrontmatterText(content) {
  const m = /^---\r?\n([\s\S]*?)\r?\n---\r?\n/.exec(content);
  return m ? m[1] : null;
}

/**
 * frontmatter のリスト 1 項目のテキストを取り出す。引用符付き（`"..."`/`'...'`）ならその中身を
 * そのまま返し（閉じ引用符より後ろは YAML コメントとして無視する）、引用符無しなら空白+`#` 以降を
 * コメントとして切り捨てる（実データ: `- ADR-0019 # platform（microservices-platform）側の計画 ADR`）。
 * 値そのものの先頭が `#`（例: issue 番号）である形はここでは扱わない（related_ids / related_specs /
 * plan_refs のいずれも先頭 `#` の値を持たないため）。
 */
function extractListItemText(raw) {
  const t = String(raw).trim();
  if (t.length >= 1 && (t[0] === '"' || t[0] === "'")) {
    const q = t[0];
    const closeIdx = t.indexOf(q, 1);
    if (closeIdx !== -1) return t.slice(1, closeIdx);
    return t;
  }
  const commentIdx = t.search(/\s#/);
  return (commentIdx === -1 ? t : t.slice(0, commentIdx)).trim();
}

/**
 * frontmatter テキストから `key:` の値をリストとして読む（flow 形 `[a, b]` とブロック形
 * `- a\n- b` の両方に対応する best-effort パーサ。`.ai-context/` の実データが両方式を混在させて
 * 使っているため）。見つからなければ null、キーはあるが値が空なら空配列。
 */
function parseFrontmatterListRaw(fmText, key) {
  const lines = String(fmText).split(/\r?\n/);
  const keyRe = new RegExp(`^${key}:\\s*(.*)$`);
  let idx = -1;
  let firstValue = '';
  for (let i = 0; i < lines.length; i++) {
    const m = keyRe.exec(lines[i]);
    if (m) {
      idx = i;
      firstValue = m[1].trim();
      break;
    }
  }
  if (idx === -1) return null;

  if (firstValue.startsWith('[')) {
    let buf = firstValue;
    let i = idx;
    while (!buf.includes(']') && i + 1 < lines.length) {
      i++;
      buf += ` ${lines[i].trim()}`;
    }
    const inner = buf.replace(/^\[/, '').replace(/\][\s\S]*$/, '');
    if (inner.trim() === '') return [];
    return inner.split(',').map((s) => extractListItemText(s.trim())).filter((s) => s !== '');
  }

  if (firstValue === '') {
    const items = [];
    let i = idx + 1;
    while (i < lines.length && /^\s*-\s+/.test(lines[i])) {
      items.push(extractListItemText(lines[i].replace(/^\s*-\s+/, '').trim()));
      i++;
    }
    return items;
  }

  return [extractListItemText(firstValue)];
}

/** 相対パス（`docs/...` / `.ai-context/...`）から、このグラフでのノード種別を推定する。 */
function nodeKindForPath(rel) {
  const p = rel.replace(/\\/g, '/');
  if (p.startsWith('docs/')) return 'doc';
  if (p.startsWith('.ai-context/adr/')) return 'iadr';
  if (p.startsWith('.ai-context/specs/')) return 'spec';
  return 'file';
}

/** docs/**・.ai-context/{adr,specs}/* のベース名（拡張子なし）→ 相対パスの索引。 */
function buildBasenameIndex(root) {
  const idx = new Map();
  for (const fp of collectMdFiles(path.join(root, 'docs'))) {
    idx.set(path.basename(fp, '.md'), path.relative(root, fp));
  }
  for (const sub of ['adr', 'specs']) {
    const dir = path.join(root, '.ai-context', sub);
    let entries;
    try {
      entries = fs.readdirSync(dir);
    } catch {
      entries = [];
    }
    for (const f of entries) {
      if (f.endsWith('.md')) idx.set(path.basename(f, '.md'), path.join('.ai-context', sub, f));
    }
  }
  return idx;
}

/** グラフ構築器。node/edge の登録関数をひとまとめにして返す（テスト・再利用のため）。 */
function createGraphBuilder(basenameIndex) {
  const nodes = new Map();
  const edges = [];

  function addNode(id, kind) {
    const existing = nodes.get(id);
    if (!existing) {
      nodes.set(id, { id, kind });
    } else if (existing.kind !== kind) {
      // 同じ ID が複数の型で参照されることがある（例: 基準名だけの specs トークンと related_specs の
      // 相対パスが同じファイルへ解決した場合）。より具体的な種別（file 系）を優先する。
      const rank = { doc: 3, iadr: 3, spec: 3, file: 2, unresolved: 1, plan: 2, issue: 1, external: 1, malformed: 0 };
      if ((rank[kind] || 0) > (rank[existing.kind] || 0)) existing.kind = kind;
    }
    return nodes.get(id);
  }
  function addEdge(from, to, type) {
    edges.push({ from, to, type });
  }

  function registerIdToken(fromId, raw, type) {
    const c = tb.classifyIdToken(raw);
    const id = c.raw;
    if (!c.kind) {
      addNode(id, 'malformed');
      addEdge(fromId, id, type);
      return;
    }
    if (c.external) {
      addNode(id, 'external');
      addEdge(fromId, id, type);
      return;
    }
    if (c.kind === 'IADR') {
      addNode(c.bare, 'iadr');
      addEdge(fromId, c.bare, type);
      return;
    }
    addNode(c.bare, 'plan');
    addEdge(fromId, c.bare, type);
  }

  function registerIssueToken(fromId, raw, type) {
    const c = tb.classifyIssueToken(raw);
    const id = c.raw;
    addNode(id, c.valid && c.external ? 'external' : 'issue');
    addEdge(fromId, id, type);
  }

  function registerSpecToken(fromId, raw, type) {
    const trimmed = String(raw).trim();
    const resolved = basenameIndex.get(trimmed);
    if (resolved) {
      addNode(resolved, nodeKindForPath(resolved));
      addEdge(fromId, resolved, type);
    } else {
      addNode(trimmed, 'unresolved');
      addEdge(fromId, trimmed, type);
    }
  }

  /** `related_specs:` 用。相対パス＋注記の混在テキストから best-effort でノードを作る。 */
  function registerSpecRefToken(fromId, raw, type) {
    const t = String(raw).trim();
    const q = tb.QUALIFIER_RE.exec(t);
    if (q) {
      addNode(t, 'external');
      addEdge(fromId, t, type);
      return;
    }
    const pathMatch = /(\.\.?\/[^\s"'（)]+\.md)/.exec(t);
    if (pathMatch) {
      const abs = path.resolve(path.dirname(path.resolve(REPO_ROOT, fromId)), pathMatch[1]);
      const rel = path.relative(REPO_ROOT, abs).replace(/\\/g, '/');
      const exists = fs.existsSync(abs);
      addNode(rel, exists ? nodeKindForPath(rel) : 'unresolved');
      addEdge(fromId, rel, type);
      return;
    }
    registerSpecToken(fromId, t, type);
  }

  /** `plan_refs:` 用。常に計画リポジトリを指す前提なので external として登録する。 */
  function registerPlanRefToken(fromId, raw, type) {
    const t = String(raw).trim();
    addNode(t, 'external');
    addEdge(fromId, t, type);
  }

  return {
    nodes,
    edges,
    addNode,
    addEdge,
    registerIdToken,
    registerIssueToken,
    registerSpecToken,
    registerSpecRefToken,
    registerPlanRefToken,
  };
}

/** docs/ 配下 1 ファイル分の trace / trace-table を builder へ登録する。 */
function ingestDocFile(builder, root, fp) {
  const rel = path.relative(root, fp).replace(/\\/g, '/');
  let content;
  try {
    content = fs.readFileSync(fp, 'utf8');
  } catch {
    return;
  }
  builder.addNode(rel, 'doc');

  const traceBlocks = tb.findTraceBlocks(content);
  if (traceBlocks.length && !traceBlocks[0].unterminated) {
    const split = tb.splitBlockLines(traceBlocks[0].body);
    if (!split.error) {
      const parsed = tb.parseTraceBlockLines(split.lines);
      for (const entry of parsed.entries) {
        for (const item of entry.items) {
          if (entry.key === 'specs') builder.registerSpecToken(rel, item, 'trace:specs');
          else if (entry.key === 'issues') builder.registerIssueToken(rel, item, 'trace:issues');
          else builder.registerIdToken(rel, item, `trace:${entry.key}`);
        }
      }
    }
  }

  for (const block of tb.findTraceTableBlocks(content)) {
    if (block.unterminated) continue;
    const split = tb.splitBlockLines(block.body);
    if (split.error) continue;
    const parsed = tb.parseTraceTableLines(split.lines);
    for (const row of parsed.rows) {
      for (const item of row.items) builder.registerIdToken(rel, item, 'trace-table');
    }
  }
}

/** `.ai-context/{adr,specs}/` 配下 1 ファイル分の frontmatter を builder へ登録する。 */
function ingestAiContextFile(builder, root, fp, sub) {
  const rel = path.relative(root, fp).replace(/\\/g, '/');
  let content;
  try {
    content = fs.readFileSync(fp, 'utf8');
  } catch {
    return;
  }
  builder.addNode(rel, sub === 'adr' ? 'iadr' : 'spec');
  const fm = extractFrontmatterText(content);
  if (fm === null) return;

  for (const item of parseFrontmatterListRaw(fm, 'related_ids') || []) {
    builder.registerIdToken(rel, item, 'related_ids');
  }
  for (const item of parseFrontmatterListRaw(fm, 'related_specs') || []) {
    builder.registerSpecRefToken(rel, item, 'related_specs');
  }
  for (const item of parseFrontmatterListRaw(fm, 'plan_refs') || []) {
    builder.registerPlanRefToken(rel, item, 'plan_refs');
  }
}

/** グラフ全体を組み立てる。戻り値: { nodes: [...], edges: [...] }（配列。JSON 化しやすい形）。 */
function buildGraph(root) {
  const basenameIndex = buildBasenameIndex(root);
  const builder = createGraphBuilder(basenameIndex);

  for (const fp of collectMdFiles(path.join(root, 'docs'))) ingestDocFile(builder, root, fp);

  for (const sub of ['adr', 'specs']) {
    const dir = path.join(root, '.ai-context', sub);
    let entries;
    try {
      entries = fs.readdirSync(dir);
    } catch {
      entries = [];
    }
    for (const f of entries) {
      if (f.endsWith('.md')) ingestAiContextFile(builder, root, path.join(dir, f), sub);
    }
  }

  return { nodes: [...builder.nodes.values()], edges: builder.edges };
}

// --- --check（in-repo 実在検査） -------------------------------------------------

/** 計画レンジ・IADR 実在集合を読む。 */
function buildCheckContext(root) {
  const planIds = new Set(tt.readPlanIds());
  const adrRange = planRanges.readPlanAdrRange(path.join(root, tt.RULES_FILE));
  let iadrIds = null;
  try {
    iadrIds = new Set(
      fs
        .readdirSync(path.join(root, '.ai-context', 'adr'))
        .map((f) => /^(IADR-\d{3,4})_/.exec(f))
        .filter(Boolean)
        .map((m) => m[1])
    );
  } catch {
    iadrIds = null;
  }
  return { planIds, adrRange, iadrIds, iadrAvailable: iadrIds !== null };
}

function normalizePlanId(kind, bare) {
  const n = Number(bare.slice(kind.length + 1));
  return `${kind}-${String(n).padStart(2, '0')}`;
}

/**
 * グラフのノードを in-repo 実在検査する。
 * 戻り値: { errors: [文字列], counts: { plan, adr, iadr, external, issue, unresolved, malformed } }
 */
function checkGraph(graph, ctx) {
  const errors = [];
  const counts = { plan: 0, adr: 0, iadr: 0, external: 0, issue: 0, unresolved: 0, malformed: 0, doc: 0, spec: 0, file: 0 };
  for (const node of graph.nodes) {
    counts[node.kind] = (counts[node.kind] || 0) + 1;
    if (node.kind === 'malformed') {
      errors.push(`形式が不正な参照です: ${node.id}`);
      continue;
    }
    if (node.kind === 'plan') {
      const m = /^(FR|UC|SC)-(\d{1,3})$/.exec(node.id);
      if (m) {
        const norm = normalizePlanId(m[1], node.id);
        if (!ctx.planIds.has(norm)) errors.push(`計画書に存在しない ID です: ${node.id}`);
        continue;
      }
      const a = /^ADR-(\d{3,4})$/.exec(node.id);
      if (a) {
        if (!planRanges.isAdrInRange(node.id, ctx.adrRange)) {
          errors.push(`計画 ADR のレンジ外です（${ctx.adrRange.from}〜${ctx.adrRange.to}）: ${node.id}`);
        }
        continue;
      }
      // NFR はレンジを持たない（無採番許容）。
      continue;
    }
    if (node.kind === 'iadr' && /^IADR-\d{3,4}$/.test(node.id)) {
      // ファイルパスへ解決済みの iadr ノード（`.ai-context/adr/...`）は実在そのもの。
      // 裸の ID のまま残っているノードだけを実在集合と突合する。
      if (ctx.iadrAvailable && !ctx.iadrIds.has(node.id)) {
        errors.push(`.ai-context/adr/ に実在しない IADR です: ${node.id}`);
      }
    }
  }
  return { errors, counts };
}

// --- --mermaid --------------------------------------------------------------

/** Mermaid のノード ID として安全な文字列へ変換する。 */
function mermaidId(id) {
  return id.replace(/[^A-Za-z0-9_]/g, '_');
}

function toMermaid(graph, scope) {
  const scopeNorm = scope ? scope.replace(/\\/g, '/').replace(/\/$/, '') : null;
  const inScope = (id) => !scopeNorm || id === scopeNorm || id.startsWith(`${scopeNorm}/`);

  let nodeIds;
  if (!scopeNorm) {
    nodeIds = new Set(graph.nodes.map((n) => n.id));
  } else {
    nodeIds = new Set(graph.nodes.filter((n) => inScope(n.id)).map((n) => n.id));
    for (const e of graph.edges) {
      if (inScope(e.from) || inScope(e.to)) {
        nodeIds.add(e.from);
        nodeIds.add(e.to);
      }
    }
  }

  const lines = ['graph LR'];
  const declared = new Set();
  const byId = new Map(graph.nodes.map((n) => [n.id, n]));
  for (const id of [...nodeIds].sort()) {
    if (declared.has(id)) continue;
    declared.add(id);
    const kind = (byId.get(id) || {}).kind || 'unknown';
    const label = id.replace(/"/g, "'");
    lines.push(`  ${mermaidId(id)}["${label}"]:::${kind}`);
  }
  for (const e of graph.edges) {
    if (!nodeIds.has(e.from) || !nodeIds.has(e.to)) continue;
    lines.push(`  ${mermaidId(e.from)} -->|${e.type}| ${mermaidId(e.to)}`);
  }
  return lines.join('\n');
}

// --- 自己試験 -------------------------------------------------------------------

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });

  t(
    'parseFrontmatterListRaw: flow 形（1 行）を読める',
    JSON.stringify(parseFrontmatterListRaw('related_ids: [FR-01, ADR-0001]\nauthor: x\n', 'related_ids')) ===
      JSON.stringify(['FR-01', 'ADR-0001'])
  );

  t(
    'parseFrontmatterListRaw: ブロック形（`- item`）を読める',
    JSON.stringify(
      parseFrontmatterListRaw('related_ids:\n  - FR-01\n  - ADR-0001\nauthor: x\n', 'related_ids')
    ) === JSON.stringify(['FR-01', 'ADR-0001'])
  );

  t(
    'parseFrontmatterListRaw: 引用符付きのブロック項目を読める（related_specs の実データ形）',
    JSON.stringify(
      parseFrontmatterListRaw(
        'related_specs:\n  - "../specs/x.md（注記）"\n  - IADR-0001\nauthor: x\n',
        'related_specs'
      )
    ) === JSON.stringify(['../specs/x.md（注記）', 'IADR-0001'])
  );

  t('parseFrontmatterListRaw: キーが無ければ null', parseFrontmatterListRaw('author: x\n', 'related_ids') === null);
  t(
    'parseFrontmatterListRaw: 値が空のフロー形は空配列',
    JSON.stringify(parseFrontmatterListRaw('related_ids: []\n', 'related_ids')) === '[]'
  );

  t('nodeKindForPath: docs/ は doc', nodeKindForPath('docs/functional/FR-10_x.md') === 'doc');
  t('nodeKindForPath: .ai-context/adr/ は iadr', nodeKindForPath('.ai-context/adr/IADR-0001_x.md') === 'iadr');
  t('nodeKindForPath: .ai-context/specs/ は spec', nodeKindForPath('.ai-context/specs/2026_x.md') === 'spec');

  {
    const basenameIndex = new Map([['20260101_x', '.ai-context/specs/20260101_x.md']]);
    const b = createGraphBuilder(basenameIndex);
    b.addNode('docs/a.md', 'doc');

    b.registerIdToken('docs/a.md', 'FR-05', 'trace:ids');
    b.registerIdToken('docs/a.md', 'MSP:IADR-0012', 'trace:iadrs');
    b.registerIdToken('docs/a.md', 'not-an-id', 'trace:ids');
    b.registerSpecToken('docs/a.md', '20260101_x', 'trace:specs');
    b.registerSpecToken('docs/a.md', 'no-such-basename', 'trace:specs');
    b.registerIssueToken('docs/a.md', 'planning#12', 'trace:issues');
    b.registerIssueToken('docs/a.md', '#5', 'trace:issues');
    b.registerPlanRefToken('.ai-context/adr/IADR-0001_x.md', 'planning:projects/x/07_adr/ADR-0001_y.md', 'plan_refs');

    const byId = new Map(b.nodes);
    t('builder: 裸の計画 ID は plan ノードになる', byId.get('FR-05') && byId.get('FR-05').kind === 'plan');
    t(
      'builder: 修飾付き（MSP: 等）は個別名を問わず external ノードになる',
      byId.get('MSP:IADR-0012') && byId.get('MSP:IADR-0012').kind === 'external'
    );
    t('builder: 形式不正なトークンは malformed ノードになる', byId.get('not-an-id') && byId.get('not-an-id').kind === 'malformed');
    t(
      'builder: 解決できた specs トークンはファイルノードへつながる',
      byId.get('.ai-context/specs/20260101_x.md') && byId.get('.ai-context/specs/20260101_x.md').kind === 'spec'
    );
    t(
      'builder: 解決できない specs トークンは unresolved ノードになる（存在チェックはしない）',
      byId.get('no-such-basename') && byId.get('no-such-basename').kind === 'unresolved'
    );
    t('builder: 修飾付き issue は external ノードになる', byId.get('planning#12') && byId.get('planning#12').kind === 'external');
    t('builder: 裸の issue は issue ノードになる', byId.get('#5') && byId.get('#5').kind === 'issue');
    t(
      'builder: plan_refs は常に external ノードになる',
      byId.get('planning:projects/x/07_adr/ADR-0001_y.md')
        && byId.get('planning:projects/x/07_adr/ADR-0001_y.md').kind === 'external'
    );
  }

  {
    const ctx = {
      planIds: new Set(['FR-01']),
      adrRange: { from: 1, to: 5 },
      iadrIds: new Set(['IADR-0001']),
      iadrAvailable: true,
    };
    const graph = {
      nodes: [
        { id: 'FR-01', kind: 'plan' },
        { id: 'FR-99', kind: 'plan' },
        { id: 'ADR-0099', kind: 'plan' },
        { id: 'IADR-0001', kind: 'iadr' },
        { id: 'IADR-9999', kind: 'iadr' },
        { id: 'MSP:FR-999', kind: 'external' },
      ],
      edges: [],
    };
    const result = checkGraph(graph, ctx);
    t('checkGraph: 実在する FR は違反なし', !result.errors.some((e) => e.includes('FR-01')));
    t('checkGraph: 実在しない FR は違反', result.errors.some((e) => e.includes('FR-99')));
    t('checkGraph: レンジ外 ADR は違反', result.errors.some((e) => e.includes('ADR-0099')));
    t('checkGraph: 実在しない IADR は違反', result.errors.some((e) => e.includes('IADR-9999')));
    t('checkGraph: external ノードは実在検査の対象外', !result.errors.some((e) => e.includes('MSP:FR-999')));
    t('checkGraph: external の件数を数える', result.counts.external === 1);
  }

  t(
    'mermaidId: 記号を安全な文字へ置換する',
    mermaidId('docs/functional/FR-10_risk-controls.md') === 'docs_functional_FR_10_risk_controls_md'
  );

  {
    const graph = {
      nodes: [
        { id: 'docs/a.md', kind: 'doc' },
        { id: 'docs/b/c.md', kind: 'doc' },
        { id: 'FR-01', kind: 'plan' },
      ],
      edges: [
        { from: 'docs/a.md', to: 'FR-01', type: 'trace:ids' },
        { from: 'docs/b/c.md', to: 'FR-01', type: 'trace:ids' },
      ],
    };
    const mm = toMermaid(graph, null);
    t('toMermaid: graph LR で始まる', mm.startsWith('graph LR'));
    t('toMermaid: 全ノードを含む（scope 無指定）', mm.includes('docs_a_md') && mm.includes('docs_b_c_md'));

    const scoped = toMermaid(graph, 'docs/b');
    t('toMermaid: --scope で絞り込むと対象外の docs ノードは含まれない', !scoped.includes('docs_a_md'));
    t('toMermaid: --scope で絞り込んでも対象ノードにつながる参照先は残る', scoped.includes('FR_01'));
  }

  {
    const root = REPO_ROOT;
    let graph;
    let ok = true;
    try {
      graph = buildGraph(root);
    } catch {
      ok = false;
    }
    t('buildGraph: 実ツリーで例外なく組み立てられる', ok);
    if (ok) {
      t('buildGraph: docs/ のノードを含む', graph.nodes.some((n) => n.kind === 'doc'));
      t('buildGraph: .ai-context/adr/ のノードを含む', graph.nodes.some((n) => n.kind === 'iadr'));
      t('buildGraph: .ai-context/specs/ のノードを含む', graph.nodes.some((n) => n.kind === 'spec'));
    }
  }

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) {
      failed++;
      if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual));
    }
  }
  if (failed) {
    console.error(`[gen-knowledge-graph] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[gen-knowledge-graph] 自己試験 ${cases.length} 件 OK。`);
}

// --- CLI ----------------------------------------------------------------------

function parseArgs(argv) {
  const a = { json: false, mermaid: false, check: false, scope: null, selfTest: false };
  for (let i = 0; i < argv.length; i++) {
    const x = argv[i];
    if (x === '--json') a.json = true;
    else if (x === '--mermaid') a.mermaid = true;
    else if (x === '--check') a.check = true;
    else if (x === '--self-test') a.selfTest = true;
    else if (x === '--scope') a.scope = argv[++i];
    else if (x.startsWith('--scope=')) a.scope = x.slice('--scope='.length);
  }
  return a;
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.selfTest) {
    selfTest();
    return;
  }

  if (args.check) {
    let ctx;
    try {
      ctx = buildCheckContext(REPO_ROOT);
    } catch (e) {
      console.error(`[gen-knowledge-graph] 計画レンジを読めません: ${e.message}`);
      process.exit(1);
    }
    if (!ctx.iadrAvailable) {
      console.error('[gen-knowledge-graph] .ai-context/adr/ を読めないため IADR 実在性検査を skip しました。');
    }
    const graph = buildGraph(REPO_ROOT);
    const { errors, counts } = checkGraph(graph, ctx);
    console.log(
      `[gen-knowledge-graph] ノード ${graph.nodes.length} 件・エッジ ${graph.edges.length} 件`
        + `（plan=${counts.plan || 0} iadr=${counts.iadr || 0} doc=${counts.doc || 0} spec=${counts.spec || 0}`
        + ` external=${counts.external || 0} issue=${counts.issue || 0} unresolved=${counts.unresolved || 0}`
        + ` malformed=${counts.malformed || 0}）`
    );
    if (counts.unresolved) {
      console.log(
        `[gen-knowledge-graph] 注記: unresolved（specs / related_specs の基準名解決に失敗） ${counts.unresolved} 件は`
          + ' 計画リポジトリ側の文書等を指し得るため、この検査では失敗として扱いません。'
      );
    }
    if (errors.length === 0) {
      console.log('[gen-knowledge-graph] OK: in-repo 参照（計画 ID・計画 ADR・IADR）に実在しないものはありません。');
      process.exit(0);
    }
    console.error(`[gen-knowledge-graph] in-repo 実在検査で違反 ${errors.length} 件を検出しました:`);
    for (const e of errors) console.error(`  - ${e}`);
    process.exit(1);
    return;
  }

  const graph = buildGraph(REPO_ROOT);
  if (args.mermaid) {
    console.log(toMermaid(graph, args.scope));
    return;
  }
  console.log(JSON.stringify(graph, null, 2));
}

if (require.main === module) main();

module.exports = {
  REPO_ROOT,
  collectMdFiles,
  extractFrontmatterText,
  extractListItemText,
  parseFrontmatterListRaw,
  nodeKindForPath,
  buildBasenameIndex,
  createGraphBuilder,
  ingestDocFile,
  ingestAiContextFile,
  buildGraph,
  buildCheckContext,
  checkGraph,
  mermaidId,
  toMermaid,
  parseArgs,
  selfTest,
};
