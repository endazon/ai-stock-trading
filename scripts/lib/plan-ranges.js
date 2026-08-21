'use strict';
/*
 * plan-ranges.js — 計画 ADR の実在レンジを読む拡張点。
 *
 * FR/UC/SC のレンジは `check-test-traceability.js` の `readPlanIds()`（#532 拡張点。
 * `.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」節が単一情報源）が既に持つ。
 * 本ファイルは同じ節・同じ書式（バッククォート囲みの `` `ADR-0001..0029` `` 形）を流用し、
 * 計画 ADR のレンジも読めるようにする拡張である。
 *
 * `check-test-traceability.js` 自体（`PLAN_KINDS` に 'ADR' を混ぜる等）は変更しない —— それは
 * commit メッセージ検査・FR/UC/SC 実在検査という既存の挙動と自己試験が握っている契約であり、
 * 計画 ADR のレンジ検査という新しい用途のために触れると退行のリスクを持ち込む。
 * かわりに `planRangeSection()`（節の本文だけを切り出す既存のエクスポート）を再利用し、
 * 同じ節の中から `ADR-from..to` だけを別パーサで拾う。
 *
 * 外部依存ゼロ。読めない／節が無い／レンジが無い場合は例外を投げる（fail-loud。
 * `readPlanIds()` と同じ理由 —— 黙って skip すると「実在しない ADR の違反 0 件」という
 * 最も安全に見える出力で素通りする）。
 */
const fs = require('fs');
const path = require('path');
const tt = require('../check-test-traceability.js');

/** 計画 ADR のレンジ表記（例: `` `ADR-0001..0029` ``）。 */
const ADR_RANGE_RE = /`ADR-(\d+)\.\.(\d+)`/;

/** 既定の宣言ファイル（`check-test-traceability.js` と同じ単一情報源）。 */
const DEFAULT_RULES_PATH = path.resolve(__dirname, '..', '..', tt.RULES_FILE);

/**
 * `.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」節から計画 ADR のレンジを読む。
 * 戻り値: { from, to }（両端を含む）。
 */
function readPlanAdrRange(rulesPath = DEFAULT_RULES_PATH) {
  let md;
  try {
    md = fs.readFileSync(rulesPath, 'utf8');
  } catch {
    throw new Error(`${tt.RULES_FILE} を読めません（計画 ADR レンジの宣言元）: ${String(rulesPath).replace(/\\/g, '/')}`);
  }
  const section = tt.planRangeSection(md);
  if (section === null) {
    throw new Error(`${tt.RULES_FILE} に「${tt.PLAN_RANGE_HEADING}」節が見つかりません`);
  }
  const m = ADR_RANGE_RE.exec(section);
  if (!m) {
    throw new Error(
      `${tt.RULES_FILE} の「${tt.PLAN_RANGE_HEADING}」節に計画 ADR のレンジ`
        + '（例: `ADR-0001..0029`）が見つかりません'
    );
  }
  const from = Number(m[1]);
  const to = Number(m[2]);
  if (!(from >= 1) || !(to >= from)) {
    throw new Error(`計画 ADR レンジの範囲が不正です: ${m[0]}`);
  }
  return { from, to };
}

/** `ADR-xxxx` が与えられたレンジ内かを判定する。形が違えば false。 */
function isAdrInRange(id, range) {
  const m = /^ADR-(\d{3,4})$/.exec(String(id));
  if (!m) return false;
  const n = Number(m[1]);
  return n >= range.from && n <= range.to;
}

module.exports = { readPlanAdrRange, isAdrInRange, ADR_RANGE_RE, DEFAULT_RULES_PATH };
