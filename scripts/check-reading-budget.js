#!/usr/bin/env node
/**
 * 必読規約の総量予算（NFR / #519 / IADR-0204）を検査する。
 *
 * 運用標準（planning/docs/ai-implementation-workflow-guide.md・CLAUDE.md）は
 * 「**毎セッション必読の規約は総量 50KB の予算内に収める**」と定める。
 * 本スクリプトはその母集合を定義し、実測して予算と比べる。
 *
 * ■ 🔴 なぜ「エージェントごと」に判定するのか（本検査器の設計の核心）
 *
 *   **「毎セッション必読」は、読む主体によって中身が違う。**
 *   Claude Code は `CLAUDE.md` と `.claude/rules/*.md` を自動で読み込むが `AGENTS.md` は読まない。
 *   逆に `AGENTS.md` を読むツール（Codex / Cursor / Aider）は `.claude/rules/` を読まない。
 *   **1 つのセッションが両方を背負うことはない。**
 *
 *   制約は**セッション 1 本が背負う量**に掛かる。したがって
 *   **集合ごとに予算と比べる。合算しない** —— 合算は**誰も背負わない量**を作る。
 *
 *   **実測: 合算すると 2 回とも誤った**（#519）。`AGENTS.md` を足した 45,082 / 45,760 バイトを
 *   「90.2% / 91.5%」と報告し、**#519 の着手条件（90%）を満たしたことにしていた**。
 *   正しくは **41,326 バイト（82.7%）** であり、条件は満たされていない。
 *   **母集合を取り違えると、「いつ動くか」の判断まで狂う。**
 *
 * ■ 母集合の根拠（定義と根拠を同じ場所に置く）
 *
 *   定義を別ファイルへ置くと、**定義と実装がずれても誰も気づかない**（本リポで繰り返している型）。
 *   よって根拠を各集合のコメントとして持たせる。
 *
 * ■ 閾値
 *
 *   超過（100% 超）で **fail**、接近（既定 90% 以上）で **warn**。
 *   **超えてから気づくと、減量を迫られる場面で減らせるものが残っていない。**
 *
 * 使い方:
 *   node scripts/check-reading-budget.js [--self-test]
 *
 * 環境変数:
 *   READING_BUDGET_BYTES  予算（既定 50000）
 *   READING_BUDGET_WARN   warn を出す割合（既定 0.9）
 */
const fs = require('fs');
const path = require('path');

const REPO = path.resolve(__dirname, '..');
const BUDGET_BYTES = Number(process.env.READING_BUDGET_BYTES || 50000);
const WARN_RATIO = Number(process.env.READING_BUDGET_WARN || 0.9);

/**
 * エージェントごとの自動読み込み集合。
 *
 * `files` は固定パス、`globDirs` は「そのディレクトリ直下の *.md すべて」を指す
 * （`.claude/rules/` は**同ディレクトリの `*.md` が自動適用される**ため、
 * ファイルを 1 つ足しただけで予算が動く。列挙ではなく走査で取る）。
 */
const AGENT_SETS = [
  {
    name: 'Claude Code',
    // 根拠: CLAUDE.md「Claude はこのファイルを毎セッション読み込む」
    //       .claude/rules/traceability.md「同ディレクトリの *.md は自動適用される」
    files: ['CLAUDE.md'],
    globDirs: ['.claude/rules'],
  },
  {
    name: 'AGENTS.md 対応（Codex / Cursor / Aider）',
    // 根拠: AGENTS.md 冒頭「Claude 以外の AI エージェント…が読み込む共通指示」
    // 🔴 Claude は読まない。Claude Code の集合へ足してはならない。
    files: ['AGENTS.md'],
    globDirs: [],
  },
  {
    name: 'GitHub Copilot',
    // 根拠: CLAUDE.md「Copilot 固有の運用は .github/copilot-instructions.md」
    files: ['.github/copilot-instructions.md'],
    globDirs: [],
  },
];

/** 集合を実ファイルへ展開する。存在しないものは黙って落とさず、呼び出し側へ返す。 */
function resolveSet(set, repo = REPO) {
  const found = [];
  const missing = [];
  for (const rel of set.files) {
    if (fs.existsSync(path.join(repo, rel))) found.push(rel);
    else missing.push(rel);
  }
  for (const dir of set.globDirs) {
    const abs = path.join(repo, dir);
    if (!fs.existsSync(abs)) {
      missing.push(`${dir}/*.md`);
      continue;
    }
    for (const name of fs.readdirSync(abs).sort()) {
      if (name.endsWith('.md')) found.push(path.posix.join(dir, name));
    }
  }
  return { found, missing };
}

/** 集合の実測（バイト・内訳）。 */
function measure(set, repo = REPO) {
  const { found, missing } = resolveSet(set, repo);
  const entries = found.map((rel) => ({ file: rel, bytes: fs.statSync(path.join(repo, rel)).size }));
  const total = entries.reduce((s, e) => s + e.bytes, 0);
  return { name: set.name, entries, total, missing };
}

/** 判定。**超過は fail、接近は warn。** */
function verdict(total, budget = BUDGET_BYTES, warnRatio = WARN_RATIO) {
  if (total > budget) return 'over';
  if (total >= budget * warnRatio) return 'warn';
  return 'ok';
}

function pct(total, budget = BUDGET_BYTES) {
  return Math.round((total / budget) * 1000) / 10;
}

function selfTest() {
  const cases = [];
  const t = (name, pass) => cases.push({ name, pass });
  const tmp = fs.mkdtempSync(path.join(require('os').tmpdir(), 'rbudget-'));

  // --- 判定の境界 ---
  t('予算ちょうどは over ではない', verdict(50000, 50000, 0.9) === 'warn');
  t('予算を 1 バイト超えたら over', verdict(50001, 50000, 0.9) === 'over');
  t('90% ちょうどは warn', verdict(45000, 50000, 0.9) === 'warn');
  t('90% 未満は ok', verdict(44999, 50000, 0.9) === 'ok');
  // 🔴 「接近で warn」を持たない実装は、超えてから気づくことになる。境界を固定する。

  // --- 集合の展開 ---
  fs.mkdirSync(path.join(tmp, '.claude/rules'), { recursive: true });
  fs.writeFileSync(path.join(tmp, 'CLAUDE.md'), 'x'.repeat(100));
  fs.writeFileSync(path.join(tmp, '.claude/rules/a.md'), 'y'.repeat(10));
  fs.writeFileSync(path.join(tmp, '.claude/rules/b.md'), 'z'.repeat(20));
  fs.writeFileSync(path.join(tmp, '.claude/rules/not-md.txt'), 'w'.repeat(9999));
  const set = { name: 'x', files: ['CLAUDE.md'], globDirs: ['.claude/rules'] };
  const m = measure(set, tmp);
  t('直下の *.md をすべて拾う（列挙ではなく走査）', m.total === 130);
  t('*.md 以外は拾わない', !m.entries.some((e) => e.file.endsWith('.txt')));
  t('内訳を返す', m.entries.length === 3);

  // 🔴 **存在しないファイルを黙って 0 として扱わない。**
  // 落とすと「集合が縮んだ」ことに気づけず、予算に収まったように見える。
  const miss = measure({ name: 'y', files: ['NOPE.md'], globDirs: ['no/such'] }, tmp);
  t('存在しないファイルは missing として返す', miss.missing.length === 2);
  t('missing は合計に入らない', miss.total === 0);

  // --- 🔴 合算しないこと（本検査器の設計の核心） ---
  t('集合は 3 つに分かれている', AGENT_SETS.length === 3);
  t(
    'AGENTS.md は Claude Code の集合に入っていない',
    !AGENT_SETS[0].files.includes('AGENTS.md'),
  );
  t(
    'AGENTS.md は専用の集合を持つ',
    AGENT_SETS.some((s) => s.files.includes('AGENTS.md') && s.globDirs.length === 0),
  );

  fs.rmSync(tmp, { recursive: true, force: true });

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) failed++;
  }
  if (failed) {
    console.error(`[check-reading-budget] 自己試験 ${failed} 件が失敗した。`);
    return 1;
  }
  console.log(`[check-reading-budget] 自己試験 ${cases.length} 件がすべて通った。`);
  return 0;
}

function main(argv = process.argv) {
  const args = argv.slice(2);
  const known = ['--self-test'];
  const unknown = args.filter((a) => !known.includes(a));
  if (unknown.length) {
    console.error(
      `[check-reading-budget] 未知の引数: ${unknown.join(' ')}（受け付けるのは ${known.join(' / ')}）。` +
        '黙って無視すると、渡し続けているフラグが効いていないことに誰も気付けない。',
    );
    return 1;
  }
  if (args.includes('--self-test')) return selfTest();

  const results = AGENT_SETS.map((s) => measure(s));
  let bad = 0;
  let warned = 0;
  for (const r of results) {
    const v = verdict(r.total);
    const mark = v === 'over' ? '  NG' : v === 'warn' ? 'warn' : '  ok';
    console.log(`  ${mark}  ${r.name}: ${r.total.toLocaleString()} バイト（予算の ${pct(r.total)}%）`);
    for (const e of r.entries) {
      console.log(`          ${e.file}  ${e.bytes.toLocaleString()}`);
    }
    if (r.missing.length) {
      // 集合が縮んだことを黙って通さない（予算に収まったように見えるため）。
      console.log(`          【欠落】${r.missing.join(' / ')}`);
    }
    if (v === 'over') bad++;
    if (v === 'warn') warned++;
  }

  if (bad) {
    console.error(
      `\n[check-reading-budget] ${bad} 件の集合が予算 ${BUDGET_BYTES.toLocaleString()} バイトを超えた。\n` +
        '  減量するか、予算そのものを計画側へ環流して見直すこと。\n' +
        '  🔴 集合をまたいで足し引きしてはならない —— 拘束するのは「セッション 1 本が背負う量」である。',
    );
    return 1;
  }
  if (warned) {
    console.log(
      `\n  warn  [check-reading-budget] ${warned} 件の集合が予算の ${Math.round(WARN_RATIO * 100)}% 以上である。` +
        '超えてから気づくと、減量を迫られる場面で減らせるものが残っていない。',
    );
  } else {
    console.log(
      `\n[check-reading-budget] OK: ${results.length} 件の集合すべてが予算 ${BUDGET_BYTES.toLocaleString()} バイト内である。`,
    );
  }
  return 0;
}

if (require.main === module) process.exit(main());
module.exports = { main, measure, resolveSet, verdict, pct, selfTest, AGENT_SETS, BUDGET_BYTES };
