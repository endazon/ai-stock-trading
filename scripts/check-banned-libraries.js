#!/usr/bin/env node
'use strict';
/*
 * check-banned-libraries.js
 * platform ADR-0030 の棚卸しで**不採用**となったライブラリの再混入を止める（#345 / #351）。
 *
 * 背景:
 *   不採用ライブラリは一度剥がしても、サンプルコードの貼り付け・AI の既定選択・依存の推移で
 *   容易に戻ってくる。剥がした事実をコードレビューの記憶に頼ると、数か月後には元へ戻る。
 *   機械的に止めるのが唯一の再発防止である。
 *
 * 検査対象:
 *   - `Directory.Packages.props` / `*.csproj` の PackageVersion / PackageReference
 *   - `*.cs` の using ディレクティブ
 *
 * 「まだ移行中で残っている」ライブラリは BANNED から外し、担当 issue が移行を終えた時点で
 * 本ファイルへ追加する。**移行前に登録して検査を無効化（skip）する運用は採らない**
 * （無効化された検査は無いのと同じであり、再有効化は必ず忘れられる）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。混入があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-banned-libraries.js
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = process.env.BANNED_LIBS_ROOT
  ? path.resolve(process.env.BANNED_LIBS_ROOT)
  : path.resolve(__dirname, '..');

/**
 * 不採用ライブラリと、その代替・根拠。
 * **移行が完了したものだけを載せる**（未完了のものを載せると CI が常時赤になり、検査ごと無視される）。
 */
const BANNED = [
  {
    name: 'FluentAssertions',
    replacement: 'AwesomeAssertions',
    reason: 'v8 で商用ライセンスへ移行したため不採用（platform ADR-0030 / #351）',
  },
  // 以下 3 件は**そもそも導入されたことがない**（実測で参照 0 件）。「移行未完了だから
  // BANNED に置けない」という PENDING の趣旨に当たらず、今登録しても既存コードを壊さない。
  // むしろ #353 / #354 の着手前に登録しておくことに意味がある。本検査器が動機として挙げている
  // 再混入経路（AI の既定選択・サンプルコードの貼り付け）が最も働くのはまさにその作業中であり、
  // 無コストで防げるものを見送る理由が無い。
  {
    name: 'MediatR',
    replacement: 'Wolverine ハンドラ',
    reason: 'ローカルディスパッチは Wolverine に統一する（platform ADR-0030 / ADR-0013 / #354）',
  },
  {
    name: 'AutoMapper',
    replacement: 'Riok.Mapperly',
    reason: 'マッピングは Riok.Mapperly に統一する（platform ADR-0030 / #353）',
  },
  {
    name: 'Mapster',
    replacement: 'Riok.Mapperly',
    reason: 'マッピングは Riok.Mapperly に統一する（platform ADR-0030 / #353）',
  },
];

/**
 * 移行が完了していないため、まだ検査対象にできないもの。
 * 担当 issue の完了時に BANNED へ移す（一覧を残すのは「忘れられた移行」を可視化するため）。
 *
 * ここに置いてよいのは「**現に使われていて、剥がすまで BANNED にできない**」ものだけである。
 * 使われていないものを PENDING に置くのは、防げる再混入を見送っているだけになる。
 */
const PENDING = [
  // Wolverine への移行（#354）は段階制であり、第 1 段階（パイロット 2 サービス）完了時点で
  // 実測 91 ファイル・113 箇所（PackageReference / using）がまだ MassTransit を参照している。
  // **全サービスの移行が終わる第 2 段階まで PENDING のままにする**。ここで BANNED へ昇格させると
  // CI が常時赤になり、本ファイルが冒頭で戒めている「検査ごと無視される」状態を自ら作ることになる。
  // 昇格の条件: check-consumer-endpoint-names.js の出力で「MassTransit 未移行: 0 件」になること。
  { name: 'MassTransit', replacement: 'Wolverine', owningIssue: 354 },
];

const SCAN_EXT = new Set(['.cs', '.csproj', '.props']);
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

/** 1 ファイル分の混入を返す。行番号付き。 */
function findViolations(text, banned) {
  const hits = [];
  const lines = text.split(/\r?\n/);
  for (const lib of banned) {
    // パッケージ参照（PackageReference / PackageVersion の Include）と using を検出する。
    // 単なる散文中の言及（コメントで「FluentAssertions から移行した」等）は誤検出しない。
    const patterns = [
      new RegExp(`Include="${lib.name}"`),
      new RegExp(`^\\s*(global\\s+)?using\\s+${lib.name}\\b`),
    ];
    lines.forEach((line, i) => {
      if (patterns.some((p) => p.test(line))) {
        hits.push({ lib, line: i + 1, text: line.trim() });
      }
    });
  }
  return hits;
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
    const pending = PENDING.map((p) => `${p.name}（#${p.owningIssue}）`).join(' / ');
    console.log(
      `[check-banned-libraries] OK: 不採用ライブラリ ${BANNED.length} 件の混入はありません。`
        + `\n  移行未完了のため未検査: ${pending}`
    );
    process.exit(0);
  }

  console.error(`[check-banned-libraries] 不採用ライブラリの混入 ${violations.length} 件を検出しました:`);
  for (const v of violations) {
    console.error(`  - ${v.file}:${v.line}  ${v.lib.name} → ${v.lib.replacement}`);
    console.error(`      ${v.text}`);
    console.error(`      理由: ${v.lib.reason}`);
  }
  console.error('\n代替ライブラリへ置き換えてください（platform ADR-0030 の棚卸し）。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = { BANNED, PENDING, scanFiles, findViolations, checkTree };
