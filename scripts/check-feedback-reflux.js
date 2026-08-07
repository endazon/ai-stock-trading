#!/usr/bin/env node
'use strict';
/*
 * check-feedback-reflux.js
 *
 * NFR / #439 / IADR-0170:
 * 環流記録（`feedback/*.md`）の front matter を走査し、**計画側へ起票されないまま滞留している**
 * 記録を報告する。
 *
 * 背景（2026-08-07 のバックログ監査）:
 *   issue が増える主因は実装速度ではなく「閉じるより速く増えること」であった。その一部は
 *   **機械的に検出できる無駄**であり、うちの 1 つが**環流の未起票**である——`feedback/` に
 *   `status: open` で残るが計画側 issue が無い記録が複数あり、**上流が実装の現状を知らないまま
 *   ADR を書く**状態を生んでいた（実装が「申し送った」つもりでも、届いていない）。
 *
 *   本作業の着手時点の実測: 27 件中 **6 件**が該当し、最古は 2026-07-08（約 30 日前）であった。
 *
 * **本検査は warn であり、ブロックしない**（終了コードは常に 0）:
 *   環流は起票までにタイムラグがあるのが**正常**である。落とすと正常な運用が止まり、
 *   「検査を外す」動機を生む。CI を赤くする価値は無い。
 *
 * 誤検出を作らないための設計:
 *   - **日数のしきい値**（既定 3 日）。起票直後は本文に issue 番号が入らない運用であるため、
 *     **ゼロ日で報告すると正常な運用を騒がしくする**。
 *   - `README.md` / `TEMPLATE.md` は対象外（記録ではない）。
 *   - 計画側 issue の参照は**番号を伴う 2 形**だけを認める（`project-planning#NNN` /
 *     `project-planning/issues/NNN`）。**番号なしの `project-planning/` はリポジトリへのリンクで
 *     あって起票の証拠ではない。**
 *
 * 検査しないもの（IADR-0170 判断3）:
 *   - **`status: resolved` なのに計画側 issue が open のまま**という逆向きの乖離。
 *     判定には計画リポジトリの issue 状態の問い合わせが要り、本スクリプトの**外部依存ゼロ**を壊す。
 *     この観点は**週次 AI 監査**（`.github/workflows/backlog-audit.yml`）の担当とする。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 使い方:
 *   node scripts/check-feedback-reflux.js
 *   FEEDBACK_REFLUX_DAYS=7 node scripts/check-feedback-reflux.js
 */
const fs = require('fs');
const path = require('path');
const { warn, notice } = require('./lib/ci-annotate.js');

const REPO_ROOT = process.env.FEEDBACK_REFLUX_ROOT
  ? path.resolve(process.env.FEEDBACK_REFLUX_ROOT)
  : path.resolve(__dirname, '..');

/** 記録ではないファイル（走査対象外）。 */
const NOT_RECORDS = new Set(['README.md', 'TEMPLATE.md']);

/** 未起票を報告するまでの猶予（日）。起票までのタイムラグを吸収する。 */
const DEFAULT_GRACE_DAYS = 3;

/**
 * 計画側 issue への参照。**番号を伴う形だけ**を認める。
 * `project-planning/`（番号なし）はリポジトリへのリンクであり、起票の証拠ではない。
 */
const PLANNING_ISSUE_PATTERNS = [
  /project-planning#(\d+)/,
  /project-planning\/issues\/(\d+)/,
];

function graceDays(raw = process.env.FEEDBACK_REFLUX_DAYS) {
  const n = Number.parseInt(raw ?? '', 10);
  // 読めない値・負数は既定へ倒す（0 は「猶予なし」として有効な指定であり尊重する）。
  return Number.isInteger(n) && n >= 0 ? n : DEFAULT_GRACE_DAYS;
}

/** front matter（先頭の `---` で挟まれた範囲）の素朴なパース。値は文字列のまま返す。 */
function parseFrontMatter(text) {
  const m = /^---\r?\n([\s\S]*?)\r?\n---/.exec(text);
  if (!m) return null;

  const out = {};
  for (const line of m[1].split(/\r?\n/)) {
    const kv = /^([A-Za-z_][A-Za-z0-9_]*):\s*(.*)$/.exec(line);
    if (kv) out[kv[1]] = kv[2].trim();
  }
  return out;
}

/** 本文に計画側 issue への参照（番号つき）があるか。 */
function hasPlanningIssueRef(text) {
  return PLANNING_ISSUE_PATTERNS.some((re) => re.test(text));
}

/** `created` からの経過日数。読めなければ null（＝日数で判定しない）。 */
function ageInDays(created, now) {
  if (!created) return null;
  const t = Date.parse(created);
  if (Number.isNaN(t)) return null;
  return Math.floor((now - t) / 86_400_000);
}

/**
 * 1 件分の判定。
 * @returns {{kind: string, message: string}|null}
 */
function inspect({ file, text, now, days }) {
  const fm = parseFrontMatter(text);
  const status = fm?.status ?? '';

  if (!status) {
    return {
      kind: 'missing-status',
      file,
      message: `front matter に status がありません（open / resolved を記入してください）`,
    };
  }

  if (status !== 'open') return null;
  if (hasPlanningIssueRef(text)) return null;

  const age = ageInDays(fm?.created, now);
  // **しきい値は「超えたら」報告する。** 猶予日数ちょうどはまだ猶予の内である。
  if (age === null || age <= days) return null;

  return {
    kind: 'unfiled',
    file,
    age,
    message:
      `status: open のまま ${age} 日が経過し、計画側 issue への参照がありません`
      + `（起票されていない可能性があります。猶予 ${days} 日）`,
  };
}

function listRecords(root) {
  const dir = path.join(root, 'feedback');
  let entries;
  try {
    entries = fs.readdirSync(dir);
  } catch {
    return [];
  }
  return entries
    .filter((f) => f.endsWith('.md') && !NOT_RECORDS.has(f))
    .sort((a, b) => a.localeCompare(b, 'en'))
    .map((f) => path.join(dir, f));
}

function checkTree(root = REPO_ROOT, now = Date.now(), days = graceDays()) {
  const findings = [];
  for (const fp of listRecords(root)) {
    let text;
    try {
      text = fs.readFileSync(fp, 'utf8');
    } catch {
      continue;
    }
    const hit = inspect({ file: path.basename(fp), text, now, days });
    if (hit) findings.push(hit);
  }
  return findings;
}

function main() {
  const days = graceDays();
  const findings = checkTree(REPO_ROOT, Date.now(), days);
  const total = listRecords(REPO_ROOT).length;

  if (findings.length === 0) {
    console.log(
      `[check-feedback-reflux] OK: 環流記録 ${total} 件に、未起票のまま ${days} 日を超えた滞留はありません。`
    );
    process.exit(0);
  }

  const missing = findings.filter((f) => f.kind === 'missing-status');
  const unfiled = findings.filter((f) => f.kind === 'unfiled');

  // **警告であり失敗ではない。** 環流のタイムラグは正常であり、ここで CI を落とす価値は無い。
  for (const f of findings) {
    warn(`[check-feedback-reflux] feedback/${f.file}: ${f.message}`);
  }

  notice(
    `[check-feedback-reflux] 環流記録 ${total} 件のうち ${findings.length} 件に注意点があります`
      + `（status 欠落 ${missing.length} 件 / 未起票の滞留 ${unfiled.length} 件）。`
      + '計画側へ起票し、記録本文へ issue 番号（project-planning#NNN）を書き添えてください。'
      + '**本検査は警告であり CI を落としません。**'
  );

  process.exit(0);
}

if (require.main === module) {
  main();
}

module.exports = {
  DEFAULT_GRACE_DAYS,
  NOT_RECORDS,
  PLANNING_ISSUE_PATTERNS,
  graceDays,
  parseFrontMatter,
  hasPlanningIssueRef,
  ageInDays,
  inspect,
  listRecords,
  checkTree,
};
