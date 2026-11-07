#!/usr/bin/env node
'use strict';
/*
 * backlog-audit.js — 週次のバックログ監査。滞留を**列挙するだけで状態は書き換えない**。
 * NFR / IADR-0625。外部依存ゼロ（Node 標準モジュールのみ。GitHub API は fetch）。
 *
 * なぜ要るか:
 *   CLAUDE.md「乖離の検知は issue 運用と定期棚卸し（backlog-audit.yml）に委ねる」の実現手段である。
 *   旧版は生成 AI に監査させていたが、決定的に数えられる面は機械で数え、判断が要る面
 *   （クローズ漏れの判定・分類の誤り）は**候補を並べて人間に残す**形へ改めた（IADR-0625）。
 *   - 機械で数えるので、指摘が 0 件でも各節が「指摘なし」を出し、同じ入力からは同じ報告になる。
 *   - issue の題名・本文は**データとしてのみ**扱う。シェル・式・プロンプトへは渡さない
 *     （GitHub API を fetch で読み、報告に載せるのは無害化した題名と番号だけである）。
 *
 * 見るもの（各節は「指摘なし」を明示的に出す。0 件でも節は消えない）:
 *   1. `.ai-context/adr/` で `status: Proposed` のまま止まっている実装 ADR
 *   2. `.ai-context/specs/` で draft / in progress / pending のまま `updated:` が古い作業仕様書
 *   3. `docs/` で draft / in-progress / pending のまま `updated:` が古い文書
 *   4. GitHub: `blocked*` ラベルの open issue のうち、再検証が要るもの（`再検証期限: YYYY-MM-DD` が過ぎた、
 *      または期限が無く最後のコメントから閾値日数を超えた）。全件を台帳として並べる
 *   5. GitHub: `blocked*` ラベルの open issue のうち、`docs/blocked-tasks.md` に番号が現れないもの
 *      （ラベルと一覧の食い違い。一覧の側の記載漏れか、ラベルの外し忘れ）
 *   6. GitHub: 題名を正規化すると一致する open issue の組（重複起票の候補。先着〔番号の小さい方〕を正とする）
 *   7. GitHub: エピック（既定 #344）の子 issue の進捗（open / closed の件数）
 *   8. GitHub: `ci-failure` ラベルの open issue
 *   9. GitHub: 更新が古い open PR
 *   10. 計画 ID レンジ宣言の鮮度。前段の `check-planning-adr-range.js --out` が書いた JSON を `--plan-range` で受ける。
 *      🔴 計画側に届かなかった（`unverified`）は「指摘なし」と書かず、未確認として指摘に数える
 *
 * 「success だが無産出」を作り込まない:
 *   - 報告は**必ず**生成する（指摘 0 件でも全節を持つ Markdown）。
 *   - `--post` では追跡 issue（ラベル `backlog-audit`・マーカー `<!-- backlog-audit -->`）の本文を置き換え、
 *     **読み戻して run 固有のマーカーが在ることを確かめ、無ければ exit 1**。
 *   - `--post` なのに GITHUB_TOKEN / GITHUB_REPOSITORY が無ければ exit 1（黙って skip しない）。
 *   - GitHub 面の取得に失敗したら、その節に失敗を書いたうえで exit 1（部分報告を成功に見せない）。
 *
 * 使い方:
 *   node scripts/backlog-audit.js                       # 報告を標準出力へ（GitHub 面は token があれば読む）
 *   node scripts/backlog-audit.js --post                # 追跡 issue へ反映（CI 用）
 *   node scripts/backlog-audit.js --stale-days 14       # 「古い」の閾値（既定 14 日）
 *   node scripts/backlog-audit.js --epic 344            # 進捗を数えるエピックの番号
 *   node scripts/backlog-audit.js --plan-range r.json   # 計画 ID レンジの突合結果（前段の JSON）
 *   node scripts/backlog-audit.js --out report.md       # 報告をファイルにも書く
 *   node scripts/backlog-audit.js --self-test
 *   （GITHUB_OUTPUT があれば、更新した issue 番号を `issue_number` として書く。後段の産出検証が読む）
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const MARKER = '<!-- backlog-audit -->';
const LABEL = 'backlog-audit';
// 旧版（AI）が upsert していた追跡 issue の題名。マーカーを持つ issue が無い初回だけ、この題名で引き継ぐ。
const TITLE = 'chore(NFR): バックログ定期監査の結果';
const TRUSTED_ASSOCIATIONS = new Set(['OWNER', 'MEMBER', 'COLLABORATOR']);
const BLOCKED_LABELS = ['blocked', 'blocked:env', 'blocked:human', 'blocked:decision'];
const STALE_SPEC_STATUSES = new Set(['draft', 'in progress', 'in-progress', 'pending']);
const STALE_DOC_STATUSES = new Set(['draft', 'in progress', 'in-progress', 'pending']);
const DEFAULT_STALE_DAYS = 14;
const DEFAULT_EPIC = 344;
const BLOCKED_TASKS_DOC = path.join('docs', 'blocked-tasks.md');

function toPosix(p) { return String(p).replace(/\\/g, '/'); }

/**
 * 外から書かれた文字列（issue / PR の題名）を報告へ載せられる形にする（純関数）。
 * - 改行・制御文字は空白へ（Markdown の構造を壊さない）
 * - `<` `>` は全角へ（HTML コメント＝本ファイルのマーカーや埋め込みを作らせない）
 * - `@` の直後へゼロ幅空白を入れる（報告の更新で他人へ通知を飛ばさない）
 * - バッククォート・角括弧は全角へ（コード・リンクの構文を作らせない）
 * - 120 文字で切る
 */
function safeText(value) {
  let s = String(value == null ? '' : value).replace(/[\u0000-\u001f\u007f]+/g, ' ');
  s = s.replace(/</g, '＜').replace(/>/g, '＞').replace(/`/g, '｀').replace(/\[/g, '［').replace(/\]/g, '］');
  s = s.replace(/@/g, '@​');
  s = s.replace(/\s+/g, ' ').trim();
  return s.length > 120 ? `${s.slice(0, 119)}…` : s;
}

/** frontmatter（`---` で囲む先頭ブロック）から `key: value` を抜く（純関数）。 */
function parseFrontmatter(text) {
  const m = String(text).match(/^---\r?\n([\s\S]*?)\r?\n---/);
  if (!m) return {};
  const out = {};
  for (const line of m[1].split(/\r?\n/)) {
    const kv = line.match(/^([A-Za-z_]+):\s*(.*)$/);
    if (kv) out[kv[1]] = kv[2].trim().replace(/^["']|["']$/g, '');
  }
  return out;
}

function daysBetween(a, b) { return Math.floor((b - a) / 86400000); }

/** `updated:` が閾値より古いか（純関数。日付が読めなければ「古い」と扱う —— 読めないのも滞留の一種）。 */
function isStale(updated, now, staleDays) {
  const t = Date.parse(String(updated || ''));
  if (Number.isNaN(t)) return true;
  return daysBetween(t, now) > staleDays;
}

/** 本文とコメント（時系列順）から最後の `再検証期限` の日付を抜く（純関数。無ければ null）。 */
function parseRevalidationDeadline(texts) {
  let last = null;
  for (const t of texts) {
    const text = String(t || '').replace(/<!--[\s\S]*?-->/g, '');
    for (const m of text.matchAll(/再検証期限[^\n\d]*\s*(\d{4}-\d{2}-\d{2})/g)) last = m[1];
  }
  return last && !Number.isNaN(Date.parse(last)) ? last : null;
}

/** 題名を重複判定用に正規化する（純関数）。種別の接頭辞・括弧内の ID・空白・記号の差を消す。 */
function normalizeTitle(title) {
  return String(title || '')
    .normalize('NFKC')
    .toLowerCase()
    .replace(/^\s*[a-z]+(\([^)]*\))?!?:\s*/, '')
    .replace(/[\s\p{P}\p{S}]+/gu, '');
}

/** 正規化した題名が一致する issue の組（純関数）。2 件以上の組だけを、番号の昇順で返す。 */
function duplicateGroups(issues) {
  const by = new Map();
  for (const i of issues) {
    const k = normalizeTitle(i.title);
    if (k.length < 6) continue; // 短すぎる題名は偶然一致しやすい
    if (!by.has(k)) by.set(k, []);
    by.get(k).push(i);
  }
  return [...by.values()].filter((g) => g.length > 1).map((g) => g.sort((a, b) => a.number - b.number));
}

/** 文書の本文に現れる issue 番号（`#NNN`。他リポジトリ修飾付き `xxx#NNN` は除く）の集合（純関数）。 */
function referencedIssueNumbers(text) {
  const out = new Set();
  for (const m of String(text || '').matchAll(/(^|[^\w/#-])#(\d{1,6})\b/g)) out.add(Number(m[2]));
  for (const m of String(text || '').matchAll(/\/endazon\/ai-stock-trading\/(?:issues|pull)\/(\d{1,6})\b/g)) out.add(Number(m[1]));
  return out;
}

function walkMd(dir, acc = []) {
  if (!fs.existsSync(dir)) return acc;
  for (const name of fs.readdirSync(dir).sort()) {
    const p = path.join(dir, name);
    if (name === 'node_modules' || name === '.git' || name === 'templates') continue;
    if (fs.statSync(p).isDirectory()) walkMd(p, acc);
    else if (/\.md$/.test(name)) acc.push(p);
  }
  return acc;
}

/** 面 1〜3: リポジトリ内の滞留を集める。 */
function collectRepoFindings({ root = REPO_ROOT, now = Date.now(), staleDays = DEFAULT_STALE_DAYS } = {}) {
  const rel = (p) => toPosix(path.relative(root, p));
  const proposedAdrs = [];
  for (const f of walkMd(path.join(root, '.ai-context', 'adr'))) {
    const fm = parseFrontmatter(fs.readFileSync(f, 'utf8'));
    if (/^proposed$/i.test(fm.status || '')) proposedAdrs.push({ file: rel(f), updated: fm.updated || '（不明）' });
  }
  const staleSpecs = [];
  for (const f of walkMd(path.join(root, '.ai-context', 'specs'))) {
    const fm = parseFrontmatter(fs.readFileSync(f, 'utf8'));
    const st = (fm.status || '').toLowerCase();
    if (STALE_SPEC_STATUSES.has(st) && isStale(fm.updated, now, staleDays)) staleSpecs.push({ file: rel(f), status: fm.status, updated: fm.updated || '（不明）' });
  }
  const staleDocs = [];
  for (const f of walkMd(path.join(root, 'docs'))) {
    const fm = parseFrontmatter(fs.readFileSync(f, 'utf8'));
    const st = (fm.status || '').toLowerCase();
    if (STALE_DOC_STATUSES.has(st) && isStale(fm.updated, now, staleDays)) staleDocs.push({ file: rel(f), status: fm.status, updated: fm.updated || '（不明）' });
  }
  let blockedDocRefs = null;
  try {
    blockedDocRefs = referencedIssueNumbers(fs.readFileSync(path.join(root, BLOCKED_TASKS_DOC), 'utf8'));
  } catch { blockedDocRefs = null; }
  return { proposedAdrs, staleSpecs, staleDocs, blockedDocRefs };
}

const GH_HEADERS = (token) => ({ Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28' });

async function ghJson(url, token) {
  const res = await fetch(url, { headers: GH_HEADERS(token) });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText} for ${url}`);
  return res.json();
}

async function ghPaginate(base, token) {
  const out = [];
  for (let page = 1; page <= 20; page++) {
    const sep = base.includes('?') ? '&' : '?';
    const items = await ghJson(`${base}${sep}per_page=100&page=${page}`, token);
    out.push(...items);
    if (items.length < 100) break;
  }
  return out;
}

/** 面 4〜9: GitHub の滞留を集める（token が無ければ null。呼び出し側が「取れなかった」と書く）。 */
async function collectGithubFindings({ repo, token, now = Date.now(), staleDays = DEFAULT_STALE_DAYS, epic = DEFAULT_EPIC, blockedDocRefs = null, api = ghPaginate } = {}) {
  if (!repo || !token) return null;
  const base = `https://api.github.com/repos/${repo}`;
  const issues = (await api(`${base}/issues?state=open`, token)).filter((i) => !i.pull_request);
  const prs = await api(`${base}/pulls?state=open`, token);
  const labelsOf = (i) => (i.labels || []).map((l) => (typeof l === 'string' ? l : l.name));
  const blocked = [];
  for (const i of issues.filter((x) => labelsOf(x).some((l) => BLOCKED_LABELS.includes(l)))) {
    // 日数の基準は `updated_at` ではなく最後のコメント（ラベル操作で戻らない）。
    const comments = await api(`${base}/issues/${i.number}/comments`, token);
    const lastAt = comments.length ? comments[comments.length - 1].created_at : i.created_at;
    const deadline = parseRevalidationDeadline([i.body, ...comments.map((c) => c.body)]);
    const ageDays = daysBetween(Date.parse(lastAt), now);
    const overdue = deadline ? Date.parse(deadline) <= now : false;
    blocked.push({
      number: i.number, title: i.title, labels: labelsOf(i).filter((l) => BLOCKED_LABELS.includes(l)),
      deadline, overdue, ageDays, needsRevalidation: deadline ? overdue : ageDays > staleDays,
      inBlockedDoc: blockedDocRefs ? blockedDocRefs.has(i.number) : null,
    });
  }
  const staleBlocked = blocked.filter((b) => b.needsRevalidation);
  const blockedNotInDoc = blockedDocRefs ? blocked.filter((b) => b.inBlockedDoc === false) : null;
  const duplicates = duplicateGroups(issues).map((g) => g.map((i) => ({ number: i.number, title: i.title })));
  let epicProgress = null;
  if (epic) {
    try {
      const subs = await api(`${base}/issues/${epic}/sub_issues`, token);
      epicProgress = { epic, open: subs.filter((s) => s.state === 'open').length, closed: subs.filter((s) => s.state === 'closed').length };
    } catch (e) {
      epicProgress = { epic, error: e.message };
    }
  }
  const ciFailures = issues.filter((i) => labelsOf(i).includes('ci-failure')).map((i) => ({ number: i.number, title: i.title, ageDays: daysBetween(Date.parse(i.updated_at), now) }));
  const stalePrs = prs.filter((p) => daysBetween(Date.parse(p.updated_at), now) > staleDays).map((p) => ({ number: p.number, title: p.title, ageDays: daysBetween(Date.parse(p.updated_at), now) }));
  return { blocked, staleBlocked, blockedNotInDoc, duplicates, epicProgress, ciFailures, stalePrs, openIssues: issues.length, openPrs: prs.length };
}

/** 前段（`check-planning-adr-range.js --out`）の JSON を読む。読めなければ `{ error }`。未指定なら undefined。 */
function readPlanRange(file) {
  if (!file) return undefined;
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch (e) {
    return { error: `突合結果 ${toPosix(file)} を読めない（前段が落ちたか書かなかった）: ${e.message}` };
  }
}

/** 計画 ID レンジの節の指摘（純関数）。🔴 未確認は「指摘なし」にしない。 */
function planRangeFindings(pr) {
  if (pr === undefined) return null;
  if (!pr || pr.error) return [`🔴 未確認: ${pr ? pr.error : '突合結果が空'}`];
  const out = [];
  const issueRef = pr.lagIssue && Number.isInteger(pr.lagIssue.number) ? `（専用 issue #${pr.lagIssue.number}）` : '';
  if (pr.status === 'error') out.push(`🔴 宣言を読めない（本リポジトリの欠陥）: ${pr.reason}`);
  for (const r of pr.ranges || []) {
    if (r.status === 'ok') continue;
    const fmt = (n) => String(n).padStart(r.kind === 'ADR' ? 4 : 2, '0');
    const what = r.status === 'behind' ? '宣言が計画側の実物に遅れている' : '宣言が計画側の実物を超えている';
    out.push(`\`${r.kind}\`: ${what} —— 宣言 \`${r.kind}-${fmt(r.declared[0])}..${fmt(r.declared[1])}\` / 計画側 \`${r.kind}-${fmt(r.planning[0])}..${fmt(r.planning[1])}\`${issueRef}`);
  }
  if (pr.status === 'unverified') out.push(`🔴 未確認（突合 ${pr.scanned || 0} 種）: ${pr.reason}`);
  if (pr.lagIssue && pr.lagIssue.action === 'failed') out.push(`🔴 ずれを専用 issue へ書けなかった: ${pr.lagIssue.reason}`);
  return out;
}

// 1 節に並べる上限。issue 本文の上限（65,536 文字）を超えて書き込みが失敗しないようにする（件数は全数を出す）。
let SECTION_LIMIT = 80;

function section(title, items, render, none = '指摘なし') {
  const lines = [`### ${title}（${items ? items.length : 0} 件）`, ''];
  if (!items || items.length === 0) lines.push(`- ${none}`);
  else {
    for (const it of items.slice(0, SECTION_LIMIT)) lines.push(`- ${render(it)}`);
    if (items.length > SECTION_LIMIT) lines.push(`- …ほか ${items.length - SECTION_LIMIT} 件（\`node scripts/backlog-audit.js\` を手元で実行すると全件を出す）`);
  }
  lines.push('');
  return lines;
}

function renderBlocked(b) {
  const kind = b.labels.includes('blocked') ? '・🔴 種別なしの `blocked`' : '';
  const due = b.deadline ? `期限 ${b.deadline}${b.overdue ? '（過ぎた）' : ''}` : '期限なし';
  return `#${b.number} ${safeText(b.title)}（${b.labels.join(' / ')}${kind}・${due}・最後のコメントから ${b.ageDays} 日）`;
}

/** 報告 Markdown を組み立てる（純関数）。指摘 0 件でも全節を持つ。 */
function renderReport({ repoFindings, gh, ghError, runId, date, staleDays = DEFAULT_STALE_DAYS, planRange }) {
  const planFindings = planRangeFindings(planRange);
  const total = repoFindings.proposedAdrs.length + repoFindings.staleSpecs.length + repoFindings.staleDocs.length
    + (gh ? gh.staleBlocked.length + (gh.blockedNotInDoc ? gh.blockedNotInDoc.length : 0) + gh.duplicates.length + gh.ciFailures.length + gh.stalePrs.length : 0)
    + (planFindings ? planFindings.length : 0);
  const lines = [
    MARKER,
    `<!-- backlog-audit:run:${runId} -->`,
    `## バックログ定期監査 ${date}（指摘 ${total} 件・閾値 ${staleDays} 日）`,
    '',
    '**列挙するだけで状態は書き換えない（issue を自動でクローズしない）。** 各節は 0 件でも「指摘なし」を明示する。',
    'クローズ漏れ・分類の誤りの判定は機械では行わない。下の候補を根拠に人が判断する。',
    '',
    ...section('Proposed のまま止まっている実装 ADR', repoFindings.proposedAdrs, (a) => `\`${a.file}\`（updated ${a.updated}）`),
    ...section(`${staleDays} 日以上動いていない未完了の作業仕様書（draft / in progress / pending）`, repoFindings.staleSpecs, (s) => `\`${s.file}\`（${s.status} / updated ${s.updated}）`),
    ...section(`${staleDays} 日以上動いていない draft / in-progress / pending の文書（docs/）`, repoFindings.staleDocs, (d) => `\`${d.file}\`（${d.status} / updated ${d.updated}）`),
  ];
  if (gh) {
    lines.push(
      ...section(`blocked 系ラベルの open issue のうち再検証が要るもの（期限が過ぎた・期限が無く最後のコメントから ${staleDays} 日超）`, gh.staleBlocked, renderBlocked),
      `（blocked 系ラベルの open issue は全部で ${gh.blocked.length} 件 / open issue ${gh.openIssues} 件。台帳として全件を並べる）`, '',
      ...gh.blocked.slice(0, SECTION_LIMIT).map((b) => `- ${renderBlocked(b)}`),
      ...(gh.blocked.length > SECTION_LIMIT ? [`- …ほか ${gh.blocked.length - SECTION_LIMIT} 件（\`node scripts/backlog-audit.js\` を手元で実行すると全件を出す）`] : []),
      ...(gh.blocked.length ? [''] : []),
    );
    if (gh.blockedNotInDoc) {
      lines.push(...section('blocked 系ラベルが付いているのに `docs/blocked-tasks.md` に番号が無い issue', gh.blockedNotInDoc, (b) => `#${b.number} ${safeText(b.title)}（${b.labels.join(' / ')}）`));
    } else {
      lines.push('### blocked 系ラベルと `docs/blocked-tasks.md` の突合', '', '- 🔴 `docs/blocked-tasks.md` を読めなかった（突合していない）', '');
    }
    lines.push(
      ...section('題名が一致する open issue の組（重複起票の候補。先着を正とする）', gh.duplicates, (g) => g.map((i) => `#${i.number} ${safeText(i.title)}`).join(' ／ ')),
    );
    const ep = gh.epicProgress;
    lines.push('### エピックの進捗', '');
    if (!ep) lines.push('- 対象のエピックを指定していない');
    else if (ep.error) lines.push(`- #${ep.epic} の子 issue を取得できなかった: ${safeText(ep.error)}`);
    else lines.push(`- #${ep.epic}: 子 issue ${ep.open + ep.closed} 件中 closed ${ep.closed} 件・open ${ep.open} 件`);
    lines.push('');
    lines.push(
      ...section('ci-failure ラベルの open issue（後段の失敗が放置されている）', gh.ciFailures, (c) => `#${c.number} ${safeText(c.title)}（${c.ageDays} 日）`),
      ...section(`${staleDays} 日以上動いていない open PR`, gh.stalePrs, (p) => `#${p.number} ${safeText(p.title)}（${p.ageDays} 日）`),
      `（open PR は全部で ${gh.openPrs} 件）`, '',
    );
  } else {
    lines.push('### GitHub 面（blocked issue / 重複 / エピック / ci-failure / 古い PR）', '', `- 🔴 取得できなかった: ${safeText(ghError || 'GITHUB_TOKEN / GITHUB_REPOSITORY が無い')}`, '');
  }
  if (planFindings) {
    const scanned = planRange && !planRange.error ? `突合 ${planRange.scanned || 0} 種` : '突合 0 種';
    lines.push(...section(`計画 ID レンジ宣言の鮮度（${scanned}。\`.claude/rules/traceability.repo.md\` と計画側の実物）`, planFindings, (f) => f,
      `指摘なし（${planRange && planRange.reason ? planRange.reason : '宣言と計画側の実物が一致'}）`));
  } else {
    lines.push('### 計画 ID レンジ宣言の鮮度', '', '- 突合していない（`--plan-range` 未指定。CI では backlog-audit.yml の前段が渡す）', '');
  }
  lines.push('---', '正本: `scripts/backlog-audit.js`（`.github/workflows/backlog-audit.yml` が週次で実行）。');
  return lines.join('\n');
}

async function ghWrite(method, url, token, body) {
  const res = await fetch(url, { method, headers: { ...GH_HEADERS(token), 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText} for ${method} ${url}: ${await res.text()}`);
  return res.json();
}

/**
 * 追跡 issue を選ぶ（純関数）。ラベルとマーカーを持つ issue を優先し、無ければ旧版の題名の issue を
 * **信頼できる書き手（OWNER / MEMBER / COLLABORATOR・本リポジトリの Actions）が立てたものに限って**引き継ぐ。
 * 外の人が同じ題名で立てた issue を上書きしない。
 */
function pickTrackingIssue(labeled, titled) {
  const byMarker = labeled.find((i) => !i.pull_request && String(i.body || '').includes(MARKER));
  if (byMarker) return byMarker;
  const trusted = (i) => TRUSTED_ASSOCIATIONS.has(i.author_association) || (i.user && i.user.login === 'github-actions[bot]');
  return titled.filter((i) => !i.pull_request && i.title === TITLE && trusted(i)).sort((a, b) => a.number - b.number)[0] || null;
}

/** 追跡 issue へ反映し、産出を読み戻して確かめる。 */
async function postReport({ repo, token, report, runId, write = ghWrite, read = ghJson, list = ghPaginate }) {
  const base = `https://api.github.com/repos/${repo}`;
  try { await write('POST', `${base}/labels`, token, { name: LABEL, color: '0e8a16', description: 'バックログ定期監査（backlog-audit.yml が週次で更新）' }); } catch (e) { if (!/^422/.test(e.message)) throw e; }
  const labeled = await list(`${base}/issues?state=open&labels=${encodeURIComponent(LABEL)}`, token);
  // マーカー付きが無い初回だけ、open issue 全体から旧版の題名の issue を探す（信頼できる書き手のものに限る）。
  let issue = pickTrackingIssue(labeled, []);
  if (!issue) issue = pickTrackingIssue([], await list(`${base}/issues?state=open`, token));
  if (issue) {
    await write('PATCH', `${base}/issues/${issue.number}`, token, { body: report, labels: [...new Set([...(issue.labels || []).map((l) => (typeof l === 'string' ? l : l.name)), LABEL])] });
  } else {
    issue = await write('POST', `${base}/issues`, token, { title: TITLE, labels: [LABEL], body: report });
  }
  // 🔴 産出の検証: 本文を読み戻し、run 固有のマーカーが在ることを確かめる。
  const back = await read(`${base}/issues/${issue.number}`, token);
  if (!String(back.body || '').includes(`backlog-audit:run:${runId}`)) throw new Error('issue 本文に今回の run のマーカーが無い（本文の更新が確認できない）');
  return issue.number;
}

function argOf(name, dflt) {
  const i = process.argv.indexOf(name);
  return i !== -1 && process.argv[i + 1] ? process.argv[i + 1] : dflt;
}

async function main() {
  if (process.argv.includes('--self-test')) { await selfTest(); return; }
  const post = process.argv.includes('--post');
  const staleDays = Number(argOf('--stale-days', DEFAULT_STALE_DAYS));
  const epic = Number(argOf('--epic', DEFAULT_EPIC));
  const out = argOf('--out', null);
  const planRange = readPlanRange(argOf('--plan-range', null));
  const repo = process.env.GITHUB_REPOSITORY;
  const token = process.env.GITHUB_TOKEN;
  const runId = process.env.GITHUB_RUN_ID || `local-${Date.now()}`;
  const date = new Date().toISOString().slice(0, 10);
  if (post && (!repo || !token)) {
    console.error('[backlog-audit] --post には GITHUB_REPOSITORY と GITHUB_TOKEN が要る（黙って skip しない）。');
    process.exit(1);
  }
  if (!post) SECTION_LIMIT = Infinity; // 手元の実行は全件を出す
  const repoFindings = collectRepoFindings({ staleDays });
  let gh = null; let ghError = null;
  try { gh = await collectGithubFindings({ repo, token, staleDays, epic, blockedDocRefs: repoFindings.blockedDocRefs }); } catch (e) { ghError = e.message; }
  const report = renderReport({ repoFindings, gh, ghError, runId, date, staleDays, planRange });
  console.log(report);
  if (out) {
    fs.mkdirSync(path.dirname(path.resolve(out)), { recursive: true });
    fs.writeFileSync(out, report + '\n');
  }
  if (ghError) {
    console.error(`[backlog-audit] GitHub 面の取得に失敗した: ${ghError}（部分報告を成功にしない）`);
    process.exit(1);
  }
  if (post) {
    const n = await postReport({ repo, token, report, runId });
    console.log(`[backlog-audit] issue #${n} の本文を置き換え、読み戻して確認した。`);
    if (process.env.GITHUB_OUTPUT) fs.appendFileSync(process.env.GITHUB_OUTPUT, `issue_number=${n}\n`);
  }
}

async function selfTest() {
  const assert = require('assert');
  let n = 0;
  const ok = async (name, fn) => { await fn(); n++; console.log(`  ok  ${name}`); };
  const now = Date.parse('2026-11-05T00:00:00Z');
  await ok('safeText: マーカー・メンション・コード・リンク・改行を無害化し、長さを切る', () => {
    const s = safeText('x <!-- backlog-audit --> @someone `a` [l](http://e)\nnext');
    assert.ok(!s.includes('<!--') && !s.includes('`') && !s.includes('[') && !s.includes('\n'), s);
    assert.ok(!/(^|\s)@someone/.test(s), s);
    assert.strictEqual(safeText('a'.repeat(500)).length, 120);
    assert.strictEqual(safeText(null), '');
  });
  await ok('parseFrontmatter / isStale', () => {
    assert.deepStrictEqual(parseFrontmatter('---\ntitle: x\nstatus: Proposed\nupdated: 2026-08-01\n---\n# h'), { title: 'x', status: 'Proposed', updated: '2026-08-01' });
    assert.deepStrictEqual(parseFrontmatter('# no frontmatter'), {});
    assert.strictEqual(isStale('2026-10-01', now, 14), true);
    assert.strictEqual(isStale('2026-11-01', now, 14), false);
    assert.strictEqual(isStale('（不明）', now, 14), true);
  });
  await ok('normalizeTitle / duplicateGroups: 種別の接頭辞・空白・記号の差を消して組にし、先着を先に並べる', () => {
    const g = duplicateGroups([
      { number: 12, title: 'fix(NFR): 監査の結果を issue へ書く' },
      { number: 7, title: 'chore: 監査の結果を issue へ書く。' },
      { number: 9, title: 'まったく別の題名である' },
      { number: 3, title: 'ab' },
      { number: 4, title: 'ab' },
    ]);
    assert.deepStrictEqual(g.map((x) => x.map((i) => i.number)), [[7, 12]]);
  });
  await ok('referencedIssueNumbers: 裸の #NNN と自リポジトリの URL を拾い、他リポジトリ修飾は拾わない', () => {
    const s = referencedIssueNumbers('A-1（#342）/ planning#852 / MSP#2064 / [x](https://github.com/endazon/ai-stock-trading/issues/644) #1214');
    assert.deepStrictEqual([...s].sort((a, b) => a - b), [342, 644, 1214]);
  });
  await ok('parseRevalidationDeadline: 最後の期限を読み、暦に無い日付は捨てる', () => {
    assert.strictEqual(parseRevalidationDeadline(['再検証期限: 2026-10-01', '再検証期限：2026-11-19']), '2026-11-19');
    assert.strictEqual(parseRevalidationDeadline(['再検証期限: 2026-13-45']), null);
  });
  const repoFindings = { proposedAdrs: [], staleSpecs: [], staleDocs: [], blockedDocRefs: new Set() };
  const emptyGh = { blocked: [], staleBlocked: [], blockedNotInDoc: [], duplicates: [], epicProgress: { epic: 344, open: 0, closed: 2 }, ciFailures: [], stalePrs: [], openIssues: 3, openPrs: 1 };
  await ok('renderReport: 指摘 0 件でも全節が「指摘なし」を明示し、マーカーを 2 種持つ', () => {
    const r = renderReport({ repoFindings, gh: emptyGh, runId: 'r1', date: '2026-11-05' });
    assert.ok(r.startsWith(MARKER)); assert.ok(r.includes('backlog-audit:run:r1'));
    assert.strictEqual((r.match(/^- 指摘なし/gm) || []).length, 8, r);
    assert.ok(r.includes('指摘 0 件')); assert.ok(r.includes('#344: 子 issue 2 件中 closed 2 件'));
    assert.ok(r.includes('突合していない'));
  });
  await ok('renderReport: GitHub 面が取れなければその旨を書き、題名は無害化して載せる', () => {
    const r = renderReport({ repoFindings: { ...repoFindings, proposedAdrs: [{ file: 'a.md', updated: '2026-01-01' }] }, gh: null, ghError: 'boom <!-- x -->', runId: 'r2', date: 'd' });
    assert.ok(r.includes('指摘 1 件')); assert.ok(r.includes('取得できなかった: boom ＜!-- x --＞'));
    const withTitle = renderReport({ repoFindings, gh: { ...emptyGh, stalePrs: [{ number: 5, title: `t ${MARKER} @a`, ageDays: 30 }] }, runId: 'r3', date: 'd' });
    assert.strictEqual(withTitle.split(MARKER).length, 2, '題名からマーカーが複製された');
    assert.ok(!/(^|\s)@a\b/.test(withTitle));
  });
  await ok('🔴 renderReport: 計画側に届かない・結果ファイルが読めないは「指摘なし」にせず数える', () => {
    const unv = renderReport({ repoFindings, gh: emptyGh, runId: 'p1', date: 'd', planRange: { status: 'unverified', scanned: 0, ranges: [], reason: '計画側を取得できない' } });
    assert.ok(unv.includes('- 🔴 未確認（突合 0 種）: 計画側を取得できない'), unv); assert.ok(unv.includes('指摘 1 件'));
    const missing = renderReport({ repoFindings, gh: emptyGh, runId: 'p2', date: 'd', planRange: readPlanRange('/nonexistent/plan-range.json') });
    assert.ok(missing.includes('- 🔴 未確認: 突合結果 /nonexistent/plan-range.json を読めない'), missing);
    const behind = renderReport({ repoFindings, gh: emptyGh, runId: 'p3', date: 'd', planRange: { status: 'behind', scanned: 4, reason: 'x', lagIssue: { action: 'updated', number: 1300 }, ranges: [{ kind: 'ADR', declared: [1, 76], planning: [1, 77], status: 'behind' }, { kind: 'FR', declared: [1, 21], planning: [1, 21], status: 'ok' }] } });
    assert.ok(behind.includes('`ADR`: 宣言が計画側の実物に遅れている —— 宣言 `ADR-0001..0076` / 計画側 `ADR-0001..0077`（専用 issue #1300）'), behind);
    assert.ok(behind.includes('指摘 1 件'));
  });
  await ok('collectGithubFindings: 再検証の要否・一覧との突合・重複・エピック・ci-failure・古い PR を数える', async () => {
    const comments = {
      1: [{ created_at: '2026-10-01T00:00:00Z', body: '再検証期限: 2026-10-20' }],
      2: [{ created_at: '2026-11-01T00:00:00Z', body: '' }],
      5: [],
    };
    const api = async (url) => {
      const m = url.match(/\/issues\/(\d+)\/comments$/);
      if (m) return comments[m[1]];
      if (url.endsWith('/issues/344/sub_issues')) return [{ state: 'open' }, { state: 'closed' }, { state: 'closed' }];
      if (url.includes('/issues?')) return [
        { number: 1, title: 'overdue', labels: [{ name: 'blocked:env' }], created_at: '2026-07-01T00:00:00Z', body: '' },
        { number: 2, title: 'fresh', labels: [{ name: 'blocked' }], created_at: '2026-07-01T00:00:00Z', body: '' },
        { number: 3, title: 'ci 落ち', labels: [{ name: 'ci-failure' }], updated_at: '2026-11-04T00:00:00Z' },
        { number: 4, title: 'pr-as-issue', labels: [{ name: 'blocked' }], pull_request: {} },
        { number: 5, title: 'no-deadline-old', labels: [{ name: 'blocked:human' }], created_at: '2026-09-01T00:00:00Z', body: '' },
        { number: 6, title: 'docs: 同じ主題の起票', labels: [] },
        { number: 8, title: 'chore(NFR): 同じ主題の起票', labels: [] },
      ];
      return [{ number: 9, title: 'old pr', updated_at: '2026-09-01T00:00:00Z' }, { number: 10, title: 'new pr', updated_at: '2026-11-04T00:00:00Z' }];
    };
    const g = await collectGithubFindings({ repo: 'o/r', token: 't', now, staleDays: 14, blockedDocRefs: new Set([1, 5]), api });
    assert.deepStrictEqual(g.staleBlocked.map((b) => b.number), [1, 5]);
    assert.deepStrictEqual(g.blockedNotInDoc.map((b) => b.number), [2]);
    assert.deepStrictEqual(g.duplicates.map((x) => x.map((i) => i.number)), [[6, 8]]);
    assert.deepStrictEqual(g.epicProgress, { epic: 344, open: 1, closed: 2 });
    assert.deepStrictEqual(g.ciFailures.map((c) => c.number), [3]);
    assert.deepStrictEqual(g.stalePrs.map((p) => p.number), [9]);
  });
  await ok('section: 上限を超えたら残りの件数を書く（件数の見出しは全数）', () => {
    const r = section('x', Array.from({ length: SECTION_LIMIT + 3 }, (_, i) => i), (i) => `n${i}`).join('\n');
    assert.ok(r.includes(`（${SECTION_LIMIT + 3} 件）`)); assert.ok(r.includes('…ほか 3 件'));
  });
  await ok('renderReport: blocked の台帳にも 1 節の上限を当て、超えた件数を書く', () => {
    const blocked = Array.from({ length: SECTION_LIMIT + 2 }, (_, i) => ({ number: 1000 + i, title: `b${i}`, labels: ['blocked:env'], deadline: null, overdue: false, ageDays: 1 }));
    const r = renderReport({ repoFindings, gh: { ...emptyGh, blocked }, runId: 'b1', date: 'd' });
    assert.ok(r.includes(`全部で ${SECTION_LIMIT + 2} 件`)); assert.ok(r.includes('…ほか 2 件'), r);
    assert.ok(r.includes(`#${1000 + SECTION_LIMIT - 1} `)); assert.ok(!r.includes(`#${1000 + SECTION_LIMIT} `));
  });
  await ok('collectGithubFindings: token が無ければ null', async () => {
    assert.strictEqual(await collectGithubFindings({ repo: null, token: null }), null);
  });
  await ok('pickTrackingIssue: マーカー付きを優先し、題名一致は信頼できる書き手のものだけを引き継ぐ', () => {
    const outsider = { number: 2, title: TITLE, author_association: 'NONE', body: '' };
    const owner = { number: 483, title: TITLE, author_association: 'OWNER', body: '' };
    const marked = { number: 900, title: 'x', author_association: 'NONE', body: `${MARKER}\n` };
    assert.strictEqual(pickTrackingIssue([marked], [owner]).number, 900);
    assert.strictEqual(pickTrackingIssue([], [outsider, owner]).number, 483);
    assert.strictEqual(pickTrackingIssue([], [outsider]), null);
    assert.strictEqual(pickTrackingIssue([], [{ ...outsider, user: { login: 'github-actions[bot]' } }]).number, 2);
  });
  await ok('postReport: 本文を置き換えて読み戻す。読み戻しにマーカーが無ければ例外', async () => {
    const calls = [];
    const issue = { number: 483, title: TITLE, author_association: 'OWNER', body: '', labels: [] };
    const list = async (url) => (url.includes('labels=') ? [] : [issue]);
    let stored = '';
    const write = async (method, url, _t, body) => { calls.push(`${method} ${url.replace('https://api.github.com/repos/o/r', '')}`); if (method === 'PATCH') stored = body.body; return { number: 483 }; };
    const read = async () => ({ body: stored });
    assert.strictEqual(await postReport({ repo: 'o/r', token: 't', report: `${MARKER}\n<!-- backlog-audit:run:42 -->`, runId: '42', write, read, list }), 483);
    assert.deepStrictEqual(calls, ['POST /labels', 'PATCH /issues/483']);
    await assert.rejects(postReport({ repo: 'o/r', token: 't', report: 'x', runId: '43', write, read: async () => ({ body: 'old' }), list }), /マーカーが無い/);
  });
  await ok('collectRepoFindings: 実データを読める', () => {
    const f = collectRepoFindings();
    assert.ok(Array.isArray(f.proposedAdrs)); assert.ok(f.blockedDocRefs instanceof Set && f.blockedDocRefs.size > 0, 'docs/blocked-tasks.md の番号を読めていない');
  });
  console.log(`[backlog-audit] 自己試験 ${n} 件 OK。`);
}

if (require.main === module) main().catch((e) => { console.error(`[backlog-audit] ${e.stack || e}`); process.exit(1); });

module.exports = {
  safeText, parseFrontmatter, isStale, parseRevalidationDeadline, normalizeTitle, duplicateGroups, referencedIssueNumbers,
  collectRepoFindings, collectGithubFindings, readPlanRange, planRangeFindings, renderReport, pickTrackingIssue, postReport,
  MARKER, LABEL, TITLE, BLOCKED_LABELS,
};
