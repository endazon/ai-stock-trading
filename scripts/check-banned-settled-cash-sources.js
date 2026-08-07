#!/usr/bin/env node
'use strict';
/*
 * check-banned-settled-cash-sources.js
 *
 * FR-19 / FR-10 / #425 / ADR-0025 / IADR-0166:
 * **決済済み資金（settled cash）の代替に使ってはならないブローカー値**が、C# の**コードとして**
 * 参照されることを機械的に止める。
 *
 * 背景（ADR-0025 が名指しで禁じた 2 つ）:
 *   - `Funds.AvlWithdrawalCash` / `Funds.MaxWithdrawal` は**出金可能額**であり、決済済み資金とは別概念である。
 *   - **`MaxTrdQtys.MaxCashBuy`（現金で買える最大数量）がもっとも危険である。** ブローカーの
 *     「現金買付余力」は現金口座では**未決済の売却代金を含む**のが通例であり、**それこそが GFV を
 *     引き起こす当の資金である。これを分母に据えると GFV 回避ガードが GFV を許可する。**
 *
 * なぜスクリプトなのか:
 *   コメントで注意書きを足すだけでは足りない。moomoo SDK には「近い名前の値」が並んでおり、
 *   **決済済み資金の供給元を探した実装者の手はいちばん近い名前へ伸びる**。同じ罠は
 *   IADR-0153 決定4 でも一度塞いだが、塞いだ事実をレビューの記憶に頼れば数か月後には戻る
 *   （`check-banned-libraries.js` と同じ動機）。
 *
 * 検出しないもの（誤検出を作らないための設計）:
 *   - **コメント**（`//` 行コメント・`/* *\/` ブロックコメント・`///` XML ドキュメント）中の言及。
 *     既存の IADR・仕様書・アダプタの注意書きは、まさにこれらの名前を挙げて禁止を説明している。
 *     散文の言及まで落とすと、禁止の理由を書けなくなる（検査が禁止の説明を殺す）。
 *   - `Markdown` などコード以外のファイル（走査対象は `.cs` のみ）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。混入があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-banned-settled-cash-sources.js
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = process.env.BANNED_SETTLED_CASH_ROOT
  ? path.resolve(process.env.BANNED_SETTLED_CASH_ROOT)
  : path.resolve(__dirname, '..');

/**
 * 決済済み資金の代替に使ってはならないブローカー値。
 * **ADR-0025 が名指しした 3 つ**だけを載せる（推測で増やさない）。
 */
const BANNED = [
  {
    name: 'MaxCashBuy',
    owner: 'MMAPI4Net TrdCommon.MaxTrdQtys',
    reason:
      '現金買付余力は現金口座では**未決済の売却代金を含む**のが通例であり、'
      + 'それこそが GFV を引き起こす当の資金である。分母に据えると GFV 回避ガードが GFV を許可する'
      + '（ADR-0025 / IADR-0153 決定4 / IADR-0166 決定1）',
  },
  {
    name: 'AvlWithdrawalCash',
    owner: 'MMAPI4Net TrdCommon.Funds',
    reason: '出金可能額であり決済済み資金とは別概念である（ADR-0025）',
  },
  {
    name: 'MaxWithdrawal',
    owner: 'MMAPI4Net TrdCommon.Funds',
    reason: '出金可能額であり決済済み資金とは別概念である（ADR-0025）',
  },
];

/** 決済済み資金の**唯一の正しい供給先**（エラーメッセージで指し示す）。 */
const SANCTIONED_SINK = 'BrokerAccountState.SettledCashInBase';

const SCAN_EXT = new Set(['.cs']);
// **生成コード（Migrations）も除外しない。** 列として持ってしまう形の混入も止めたい。
const SKIP_DIRS = new Set(['bin', 'obj', 'node_modules', '.git', 'planning']);

function scanFiles(dir, out = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const e of entries) {
    if (e.isDirectory()) {
      if (SKIP_DIRS.has(e.name)) continue;
      scanFiles(path.join(dir, e.name), out);
    } else if (e.isFile() && SCAN_EXT.has(path.extname(e.name))) {
      out.push(path.join(dir, e.name));
    }
  }
  return out;
}

/**
 * C# の**コメントと文字列リテラル**を空白へ潰す（行番号と行数は保つ）。
 *
 * 文字列リテラルまで潰すのは、**文字列からプロパティは読めない**からである——
 * 本検査が止めたいのは「その値を**読んで**決済済み資金に据える」ことであり、
 * `names.Should().NotContain("MaxCashBuy")` のような**禁止を表明するテスト自身**を落としては
 * 検査が自分の目的を殺す（`check-banned-libraries.js` が散文の言及を誤検出しないのと同じ理由）。
 *
 * リフレクション（`GetProperty("MaxCashBuy")`）は本走査をすり抜けるが、
 * **moomoo SDK の値を型付きで読む経路はこの検査が塞いでいる**。
 */
function stripComments(text) {
  let out = '';
  let i = 0;
  const n = text.length;
  let state = 'code'; // code | line | block | string | verbatim | char

  while (i < n) {
    const c = text[i];
    const c2 = i + 1 < n ? text[i + 1] : '';

    if (state === 'code') {
      if (c === '/' && c2 === '/') { state = 'line'; out += '  '; i += 2; continue; }
      if (c === '/' && c2 === '*') { state = 'block'; out += '  '; i += 2; continue; }
      if (c === '@' && c2 === '"') { state = 'verbatim'; out += '@"'; i += 2; continue; }
      if (c === '"') { state = 'string'; out += c; i += 1; continue; }
      if (c === '\'') { state = 'char'; out += c; i += 1; continue; }
      out += c; i += 1; continue;
    }

    if (state === 'line') {
      // 改行は保つ（行番号を狂わせない）。
      if (c === '\n') { state = 'code'; out += c; i += 1; continue; }
      out += ' '; i += 1; continue;
    }

    if (state === 'block') {
      if (c === '*' && c2 === '/') { state = 'code'; out += '  '; i += 2; continue; }
      out += c === '\n' ? '\n' : ' '; i += 1; continue;
    }

    if (state === 'string') {
      if (c === '\\') { out += '  '; i += 2; continue; }
      // 未終端（改行）でも code へ戻す（壊れた入力で走査全体が巻き込まれないようにする）。
      if (c === '"' || c === '\n') { state = 'code'; }
      out += c === '\n' ? '\n' : ' '; i += 1; continue;
    }

    if (state === 'verbatim') {
      if (c === '"' && c2 === '"') { out += '  '; i += 2; continue; }
      if (c === '"') { state = 'code'; }
      out += c === '\n' ? '\n' : ' '; i += 1; continue;
    }

    // char リテラル
    if (c === '\\') { out += '  '; i += 2; continue; }
    if (c === '\'') { state = 'code'; }
    out += c === '\n' ? '\n' : ' '; i += 1;
  }

  return out;
}

/** 1 ファイル分の混入を返す（行番号付き）。 */
function findViolations(text, banned = BANNED) {
  const hits = [];
  const lines = stripComments(text).split(/\r?\n/);
  const rawLines = text.split(/\r?\n/);

  for (const item of banned) {
    // 語境界で照合する。前方一致の別名（例: 撤退ドメインの `MaxWithdrawalRatio`）は巻き込まない。
    const pattern = new RegExp(`\\b${item.name}\\b`);
    lines.forEach((line, i) => {
      if (pattern.test(line)) {
        hits.push({ item, line: i + 1, text: (rawLines[i] || '').trim() });
      }
    });
  }

  return hits.sort((a, b) => a.line - b.line);
}

function checkTree(root = REPO_ROOT, banned = BANNED) {
  const violations = [];
  for (const fp of scanFiles(root)) {
    let text;
    try {
      text = fs.readFileSync(fp, 'utf8');
    } catch {
      continue;
    }
    for (const hit of findViolations(text, banned)) {
      violations.push({ file: path.relative(root, fp), ...hit });
    }
  }
  return violations;
}

function main() {
  const violations = checkTree();

  if (violations.length === 0) {
    console.log(
      `[check-banned-settled-cash-sources] OK: 決済済み資金の代替に使ってはならない値 ${BANNED.length} 件`
      + '（MaxCashBuy / AvlWithdrawalCash / MaxWithdrawal）のコード参照はありません。'
    );
    process.exit(0);
  }

  console.error(
    `[check-banned-settled-cash-sources] 決済済み資金の代替値の参照 ${violations.length} 件を検出しました:`
  );
  for (const v of violations) {
    console.error(`  - ${v.file}:${v.line}  ${v.item.name}（${v.item.owner}）`);
    console.error(`      ${v.text}`);
    console.error(`      理由: ${v.item.reason}`);
  }
  console.error(
    `\n決済済み資金の供給先は ${SANCTIONED_SINK} だけである。導出経路（TrdFlowSummary の `
    + 'SettlementDate 付き明細の積み上げ）の検証は ADR-0019 の **PoC 項目 8**（期限 2026-08-31）であり、'
    + '**その検証時も上記の代替値を使ってはならない**（ADR-0025 決定1）。'
  );
  process.exit(1);
}

if (require.main === module) main();

module.exports = { BANNED, SANCTIONED_SINK, scanFiles, stripComments, findViolations, checkTree };
