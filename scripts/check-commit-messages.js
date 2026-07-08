#!/usr/bin/env node
'use strict';
/*
 * check-commit-messages.js
 * コミットメッセージ規約（`種別(起点ID): 要約`）の機械チェック。
 * 外部依存ゼロ（Node 標準モジュールのみ）。CI（PR 単位）での再発防止を目的とする。
 *
 * 方針（Issue #60）:
 *   - 既存履歴は書き換えない。検査対象は「PR で追加されるコミット」= base..HEAD の範囲のみ。
 *   - dependabot 等の自動コミット・マージコミット・自動生成コミットは除外する。
 *   - 規約違反があれば非ゼロ終了し、CI を失敗させる。
 *
 * 検査範囲の決定（優先順）:
 *   1) 引数 `--range <base>..<head>`
 *   2) 環境変数 `COMMIT_RANGE`
 *   3) PR 環境: `origin/$GITHUB_BASE_REF..HEAD`（GitHub Actions pull_request）
 *   4) フォールバック: `origin/develop..HEAD`（develop 既定運用）→ 取得不可なら `HEAD~20..HEAD`
 *
 * 使い方:
 *   node scripts/check-commit-messages.js [--range base..head] [--verbose]
 */
const { execSync } = require('child_process');
const fs = require('fs');
const path = require('path');

// 規約導入前の既存コミットの恒久適用除外リスト（force push 禁止のため件名を書き換えられない）。
const ALLOWLIST_PATH = path.join(__dirname, 'commit-allowlist.json');

// gen-changelog.js の TYPE_ORDER と一致させること。
const VALID_TYPES = ['feat', 'fix', 'perf', 'refactor', 'docs', 'test', 'build', 'ci', 'style', 'chore'];

// 起点 ID（スコープ）の省略を許す種別。ツールチェーン・雑多な housekeeping は計画 ID に
// 紐づかないことがあるため（traceability.md「雑多な変更は理由を明記する」）。それ以外の
// 内容変更（feat/fix/perf/refactor/docs/test）は起点 ID を必須とする（Issue #60・再発防止）。
const TYPES_ALLOW_NO_SCOPE = ['chore', 'style', 'build', 'ci'];

// 起点 ID の書式（.claude/rules/traceability.md と一致）。
//   FR-xx / NFR / UC-xx / SC-xx / ADR-xxxx / IADR-xxxx / P0..P3（フェーズ骨格）
const ID_PATTERN = /^(FR-\d+|NFR(?:-\w+)?|UC-\d+|SC-\d+|ADR-\d{3,4}|IADR-\d{3,4}|P[0-3])$/;

// 除外する自動コミットの著者（メール/名前に部分一致）。
//   dependabot 等の自動コミットは規約対象外（Issue #60）。
const BOT_AUTHORS = [
  'dependabot[bot]',
  'dependabot',
  'github-actions[bot]',
  'github-actions',
  'renovate[bot]',
  'renovate',
  'web-flow', // GitHub UI からのマージコミット署名
];

const US = '\x1f'; // Unit Separator
const RS = '\x1e'; // Record Separator

function parseArgs(argv) {
  const a = { range: null, verbose: false, title: null };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--range') a.range = argv[++i];
    else if (argv[i].startsWith('--range=')) a.range = argv[i].slice('--range='.length);
    else if (argv[i] === '--title') a.title = argv[++i];
    else if (argv[i].startsWith('--title=')) a.title = argv[i].slice('--title='.length);
    else if (argv[i] === '--verbose' || argv[i] === '-v') a.verbose = true;
  }
  return a;
}

function tryGit(args) {
  try {
    return execSync(`git ${args}`, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
  } catch (e) {
    return null;
  }
}

function revExists(ref) {
  return tryGit(`rev-parse --verify --quiet ${ref}`) !== null;
}

/** 検査範囲を決定する。決められない場合は null（= 全コミットは検査しない）を返す。 */
function resolveRange(explicit) {
  if (explicit) return explicit;
  if (process.env.COMMIT_RANGE) return process.env.COMMIT_RANGE;

  // GitHub Actions の pull_request コンテキスト
  const baseRef = process.env.GITHUB_BASE_REF;
  if (baseRef) {
    if (revExists(`origin/${baseRef}`)) return `origin/${baseRef}..HEAD`;
    if (revExists(baseRef)) return `${baseRef}..HEAD`;
  }

  // develop 既定運用のフォールバック
  if (revExists('origin/develop')) return 'origin/develop..HEAD';
  if (revExists('develop')) return 'develop..HEAD';

  // 最終フォールバック（浅いクローン等）
  if (revExists('HEAD~20')) return 'HEAD~20..HEAD';
  return 'HEAD';
}

/** 範囲のコミットを {hash, subject, author} で返す（マージコミットは除外）。 */
function collectCommits(range) {
  const fmt = `%H${US}%s${US}%an${US}%ae`;
  const raw = tryGit(`log ${range} --no-merges --pretty=format:${fmt}${RS}`);
  if (raw === null) {
    process.stderr.write(`検査範囲を git log できなかった: ${range}\n`);
    return null;
  }
  if (!raw.trim()) return [];
  return raw
    .split(RS)
    .map((r) => r.replace(/^\n/, '').trim())
    .filter(Boolean)
    .map((line) => {
      const [hash, subject = '', author = '', email = ''] = line.split(US);
      return { hash, subject, author, email };
    });
}

function isBot(c) {
  const hay = `${c.author} ${c.email}`.toLowerCase();
  return BOT_AUTHORS.some((b) => hay.includes(b.toLowerCase()));
}

/** 短縮/完全 SHA を前方一致で照合する（changelog-overrides.json と同方針）。 */
function hashMatches(a, b) {
  if (!a || !b) return false;
  const x = String(a).toLowerCase();
  const y = String(b).toLowerCase();
  return x.startsWith(y) || y.startsWith(x);
}

/**
 * 恒久適用除外リストを読み込む。未配置・不正 JSON でも CI をブロックしない（fail-open）。
 * 返り値は {hash, reason} の配列。
 */
function loadAllowlist(file = ALLOWLIST_PATH) {
  let raw;
  try {
    raw = fs.readFileSync(file, 'utf8');
  } catch (e) {
    if (e && e.code !== 'ENOENT') {
      process.stderr.write(`commit-allowlist.json を読めなかった（無視）: ${e.message}\n`);
    }
    return [];
  }
  try {
    const json = JSON.parse(raw);
    const list = Array.isArray(json.allow) ? json.allow : [];
    return list.filter((e) => e && typeof e.hash === 'string' && e.hash.trim());
  } catch (e) {
    process.stderr.write(`commit-allowlist.json が不正な JSON（無視）: ${e.message}\n`);
    return [];
  }
}

/** commit hash が allowlist に含まれれば該当エントリを、無ければ null を返す。 */
function findAllowlisted(hash, allowlist) {
  return allowlist.find((e) => hashMatches(hash, e.hash)) || null;
}

function isSkippable(subject) {
  // 自動生成コミット・リバートは規約対象外。
  if (/\[skip ci\]/i.test(subject)) return true;
  if (/^Revert\s+"/.test(subject)) return true;
  // GitHub の PR マージ／スカッシュ既定件名（末尾の (#123)）は許容。件名本体で判定する。
  return false;
}

/** 件名を検証し、違反理由の配列を返す（空なら合格）。 */
function validateSubject(subject) {
  const reasons = [];
  // 末尾の PR 番号 " (#123)" は除去して判定する。
  const s = subject.replace(/\s*\(#\d+\)\s*$/, '').trim();

  const m = s.match(/^(\w+)(?:\(([^)]*)\))?(!)?:\s+(.+)$/);
  if (!m) {
    reasons.push('形式が `種別(起点ID): 要約` に一致しない');
    return reasons;
  }
  const [, type, scope, , desc] = m;

  const lowerType = type.toLowerCase();
  if (!VALID_TYPES.includes(lowerType)) {
    reasons.push(`未知の種別 "${type}"（許可: ${VALID_TYPES.join(' / ')}）`);
  }
  if (scope === undefined) {
    // スコープ（起点 ID）が無い。内容変更の種別では必須（抜け穴防止・Issue #60）。
    if (!TYPES_ALLOW_NO_SCOPE.includes(lowerType)) {
      reasons.push(
        `起点 ID が無い（${lowerType} は必須）。例: ${lowerType}(FR-08): ...。` +
          `ID が本当に無い雑多な変更は ${TYPES_ALLOW_NO_SCOPE.join(' / ')} 種別を用いる`
      );
    }
  } else {
    const ids = scope.split(',').map((x) => x.trim()).filter(Boolean);
    if (ids.length === 0) {
      reasons.push('スコープ () が空');
    }
    for (const id of ids) {
      if (!ID_PATTERN.test(id)) {
        reasons.push(`起点 ID "${id}" が書式に一致しない（例: FR-08 / UC-03 / NFR / ADR-0001 / IADR-0001 / P0）`);
      }
    }
  }
  if (!desc || !desc.trim()) {
    reasons.push('要約が空');
  }
  return reasons;
}

/**
 * 単一件名（PR タイトル = スカッシュ後件名の由来）を検査する（Issue #125・再発防止）。
 * git を使わず、渡された 1 件名のみを規約に照合する。Revert / [skip ci] はスキップ扱い。
 * 合格・スキップ時 0、違反時 1 を返す。
 */
function checkSingleTitle(title) {
  const subject = String(title == null ? '' : title).trim();
  process.stdout.write(`PR タイトル（スカッシュ後件名）チェック: "${subject}"\n`);

  if (!subject) {
    // タイトル未取得（イベント外実行等）。CI をブロックしない（fail-open）。
    process.stderr.write('PR タイトルが空のため検査をスキップする。\n');
    return 0;
  }
  if (isSkippable(subject)) {
    process.stdout.write('  skip(auto)   Revert / [skip ci] は規約対象外\n');
    return 0;
  }

  const reasons = validateSubject(subject);
  if (reasons.length) {
    process.stderr.write('\n✗ PR タイトルが規約違反:\n');
    process.stderr.write(`  ${subject}\n`);
    for (const r of reasons) process.stderr.write(`      - ${r}\n`);
    process.stderr.write(
      '\nスカッシュマージ既定件名は「PR タイトル + (#番号)」。PR タイトルを規約 ' +
        '`種別(起点ID): 要約` に合わせること（詳細は .claude/rules/traceability.md）。\n'
    );
    return 1;
  }
  process.stdout.write('✓ PR タイトルが規約に適合\n');
  return 0;
}

function main() {
  const args = parseArgs(process.argv.slice(2));

  // 単一件名モード（PR タイトル検査）。git リポジトリ内外を問わず動作する（Issue #125）。
  const title = args.title != null ? args.title : process.env.PR_TITLE;
  if (title != null) {
    process.exit(checkSingleTitle(title));
  }

  if (tryGit('rev-parse --is-inside-work-tree') !== 'true') {
    process.stderr.write('git リポジトリではないため検査をスキップする。\n');
    process.exit(0);
  }

  const range = resolveRange(args.range);
  const commits = collectCommits(range);
  if (commits === null) {
    // 範囲解決に失敗（浅いクローン等）。CI をブロックしないため警告終了。
    process.stderr.write('検査範囲を特定できなかったため、コミット規約チェックをスキップする。\n');
    process.exit(0);
  }

  const allowlist = loadAllowlist();

  process.stdout.write(`コミット規約チェック: 範囲 ${range}（${commits.length} 件）\n`);

  const violations = [];
  let skipped = 0;
  for (const c of commits) {
    const short = c.hash.slice(0, 8);
    if (isBot(c)) {
      if (args.verbose) process.stdout.write(`  skip(bot)    ${short} ${c.subject}\n`);
      skipped++;
      continue;
    }
    if (isSkippable(c.subject)) {
      if (args.verbose) process.stdout.write(`  skip(auto)   ${short} ${c.subject}\n`);
      skipped++;
      continue;
    }
    // 規約導入前の既存コミットは恒久適用除外（監査のため常に表示する）。
    const allowed = findAllowlisted(c.hash, allowlist);
    if (allowed) {
      process.stdout.write(`  skip(allowlist) ${short} ${c.subject}\n`);
      if (allowed.reason) process.stdout.write(`      ↳ ${allowed.reason}\n`);
      skipped++;
      continue;
    }
    const reasons = validateSubject(c.subject);
    if (reasons.length) {
      violations.push({ short, subject: c.subject, reasons });
    } else if (args.verbose) {
      process.stdout.write(`  ok           ${short} ${c.subject}\n`);
    }
  }

  process.stdout.write(`検査対象 ${commits.length - skipped} 件 / 除外 ${skipped} 件\n`);

  if (violations.length) {
    process.stderr.write(`\n✗ 規約違反 ${violations.length} 件:\n`);
    for (const v of violations) {
      process.stderr.write(`  ${v.short}  ${v.subject}\n`);
      for (const r of v.reasons) process.stderr.write(`      - ${r}\n`);
    }
    process.stderr.write('\n規約: `種別(起点ID): 要約`（詳細は .claude/rules/traceability.md）\n');
    process.exit(1);
  }

  process.stdout.write('✓ すべてのコミットが規約に適合\n');
  process.exit(0);
}

if (require.main === module) {
  main();
}

// テスト用途に一部関数を公開する（本体実行時の副作用は上記ガードで抑止）。
module.exports = {
  validateSubject,
  checkSingleTitle,
  isBot,
  isSkippable,
  hashMatches,
  loadAllowlist,
  findAllowlisted,
  VALID_TYPES,
  TYPES_ALLOW_NO_SCOPE,
  ID_PATTERN,
};
