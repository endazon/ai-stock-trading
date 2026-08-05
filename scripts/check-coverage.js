#!/usr/bin/env node
'use strict';
/*
 * check-coverage.js
 * Cobertura 形式のカバレッジ出力を集計し、floor（下限）を下回っていないか検査する（#343）。
 *
 * 集計方法:
 *   複数のテストプロジェクトが同じアセンブリを重複して計測するため、レポートの
 *   `lines-valid` / `lines-covered` を単純に足すと二重計上になる。本スクリプトは
 *   **(ファイル, 行番号) の和集合**を取り、いずれかのレポートで hits > 0 なら被覆とみなす。
 *
 * 分母からの除外（#390 / IADR-0143）:
 *   `dotnet ef` が生成するファイル（`*.Designer.cs` / `*ModelSnapshot.cs`）は人が書くものでも
 *   テストするものでもないのに、1 マイグレーションあたり数百行の 0% コードを分母へ積む。
 *   これを放置すると**テストを 1 行も減らしていない PR が機械的に床を割る**（実害: PR #390）。
 *   除外は `coverage-floor.json` の `exclude`（パターン＋理由の対）で**宣言的に**持ち、
 *   何をどれだけ外したかを**必ず出力する**（黙って分母を縮めない）。
 *
 * floor の運用（ratchet）:
 *   - floor を下回れば失敗する。
 *   - 上回っても**自動では上げない**。`--suggest` で引き上げ候補を表示し、floor の更新は人手の PR で行う。
 *     自動 ratchet は不安定テストの揺れで floor が跳ね上がり、無関係な後続 PR を落とすため採らない。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。floor 未達なら終了コード 1。
 *
 * 測定手順の罠（作業仕様書 docs/specs/20260805_coverage-exclude-generated.md）:
 *   1. **CI は Release ビルド**である（ci.yml の build-and-test）。Debug で測ると行数が変わり
 *      CI と違う値が出る（実測: Debug 63.11% vs Release 61.10%）。
 *   2. **bin / obj / TestResults の残骸が混入する**（#353）。過去の実行が残っていると分母・分子とも
 *      水増しされる（実測: 未清掃 64.99%・レポート 255 件 / 清掃後 51 件）。
 *      → 本スクリプトが出す**レポート件数**が普段の件数と乖離していたら残骸を疑うこと。
 *
 * 使い方:
 *   node scripts/check-coverage.js                     # coverage-floor.json の floor で検査
 *   node scripts/check-coverage.js --suggest           # 引き上げ候補も表示
 *   node scripts/check-coverage.js --floor 0.70        # floor を明示（設定ファイルより優先）
 *   node scripts/check-coverage.js --root backend      # 探索起点を変える
 *   node scripts/check-coverage.js --no-exclude        # 除外せず素の値を測る（比較・監査用）
 */
const fs = require('fs');
const path = require('path');
const { notice } = require('./lib/ci-annotate.js');

const REPO_ROOT = process.env.COVERAGE_ROOT
  ? path.resolve(process.env.COVERAGE_ROOT)
  : path.resolve(__dirname, '..');

const FLOOR_FILE = 'coverage-floor.json';

/** ratchet 候補は実測から余裕（ヒステリシス）を引いて提案する。揺れで floor 割れを起こさないため。 */
const RATCHET_MARGIN = 0.02;

/**
 * 除外が「効きすぎる」ことへの歯止め（G1）。除外行が全体行のこの割合を超えたら失敗させる。
 * `**\/*.cs` のようなパターン事故で分母を丸ごと溶かす退行を止めるための上限であり、
 * 正当な除外の上限ではない。設定側 `maxExcludedLineShare` で上書きできる。
 */
const DEFAULT_MAX_EXCLUDED_LINE_SHARE = 0.35;

function parseArgs(argv) {
  const a = { root: 'backend', suggest: false, floor: null, exclude: true };
  for (let i = 0; i < argv.length; i++) {
    const x = argv[i];
    if (x === '--suggest') a.suggest = true;
    else if (x === '--no-exclude') a.exclude = false;
    else if (x === '--root') a.root = argv[++i];
    else if (x.startsWith('--root=')) a.root = x.slice(7);
    else if (x === '--floor') a.floor = Number(argv[++i]);
    else if (x.startsWith('--floor=')) a.floor = Number(x.slice(8));
  }
  return a;
}

/** cobertura レポートを再帰的に探す。 */
function findReports(dir) {
  const out = [];
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const e of entries) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (e.name === 'obj' || e.name === 'node_modules') continue;
      out.push(...findReports(p));
    } else if (e.isFile() && e.name === 'coverage.cobertura.xml') {
      out.push(p);
    }
  }
  return out;
}

/**
 * Cobertura XML から (ファイル, 行番号) → 被覆有無を積み上げる。
 * XML パーサを持ち込まずに済む範囲の単純な走査で足りる（属性順は coverlet が固定）。
 */
function accumulate(xml, acc) {
  const classPattern = /<class\b[^>]*\bfilename="([^"]+)"[^>]*>([\s\S]*?)<\/class>/g;
  for (const cls of xml.matchAll(classPattern)) {
    const filename = cls[1];
    let lines = acc.get(filename);
    if (!lines) {
      lines = new Map();
      acc.set(filename, lines);
    }
    for (const line of cls[2].matchAll(/<line\b[^>]*\bnumber="(\d+)"[^>]*\bhits="(\d+)"/g)) {
      const no = Number(line[1]);
      const hit = Number(line[2]) > 0;
      lines.set(no, (lines.get(no) || false) || hit);
    }
  }
  return acc;
}

/** 和集合の行カバレッジを返す。 */
function summarize(acc) {
  let total = 0;
  let covered = 0;
  for (const lines of acc.values()) {
    for (const hit of lines.values()) {
      total++;
      if (hit) covered++;
    }
  }
  return { total, covered, lineRate: total === 0 ? 0 : covered / total };
}

// --- 分母からの除外（#390 / IADR-0143） ------------------------------------------------

/**
 * Cobertura の filename を比較用に正規化する。
 * 区切りを `/` に揃え、先頭へ `/` を 1 つ置く（絶対パス・相対パスのどちらで出力されても
 * `**\/` 始まりのパターンが同じように当たるようにするため）。
 */
function normalizePath(filename) {
  return '/' + String(filename).replace(/\\/g, '/').replace(/^\/+/, '');
}

/**
 * glob を正規表現へ変換する。パターンは**設定ファイルに人が書くもの**であり、レビュアが効果を
 * 読み取れることを優先して正規表現ではなく glob を採る（IADR-0143 決定1）。
 *   `**\/`  … 任意階層（0 段も可）
 *   `**`    … `/` を跨ぐ任意の文字列
 *   `*`     … `/` を跨がない任意の文字列
 *   `?`     … `/` 以外の 1 文字
 * パス末尾までの完全一致（部分一致ではない）。`Migrations` の語を含むだけの通常ファイルを
 * 巻き込まないための最低限の歯止めである。
 */
function globToRegExp(glob) {
  const g = String(glob).replace(/\\/g, '/');
  const src = g.startsWith('/') ? g : '/' + g;
  let re = '';
  for (let i = 0; i < src.length; i++) {
    const c = src[i];
    if (c === '*') {
      if (src[i + 1] === '*') {
        if (src[i + 2] === '/') {
          re += '(?:.*/)?'; // `**/` は 0 段以上のディレクトリ
          i += 2;
        } else {
          re += '.*';
          i += 1;
        }
      } else {
        re += '[^/]*';
      }
    } else if (c === '?') {
      re += '[^/]';
    } else if ('.+^${}()|[]\\'.includes(c)) {
      re += '\\' + c;
    } else {
      re += c;
    }
  }
  return new RegExp(`^${re}$`);
}

/** coverage-floor.json を読む（無ければ null）。 */
function readConfig(root) {
  const fp = path.join(root, FLOOR_FILE);
  if (!fs.existsSync(fp)) return null;
  try {
    return JSON.parse(fs.readFileSync(fp, 'utf8'));
  } catch {
    return null;
  }
}

/** 設定から除外エントリ（`{ pattern, reason }` の配列）を取り出す。 */
function readExcludes(root) {
  const cfg = readConfig(root);
  return cfg && Array.isArray(cfg.exclude) ? cfg.exclude : [];
}

/** 設定から除外率の上限を取り出す（未指定なら既定）。 */
function readMaxExcludedLineShare(root) {
  const cfg = readConfig(root);
  const v = cfg && cfg.maxExcludedLineShare;
  return typeof v === 'number' && v > 0 && v <= 1 ? v : DEFAULT_MAX_EXCLUDED_LINE_SHARE;
}

/**
 * 自動生成マーカーを探す先頭バイト数。Roslyn 規約でマーカーはファイル冒頭に置かれる。
 *
 * **これはマーカーの「位置」についての前提である**（内容ではなく）。将来 T4 テンプレート等が
 * マーカーをこの範囲より後ろへ置くと、その生成物は「手書き」と判定される。
 * **外れ方は安全側である** —— 生成物が手書き扱いになると G4（除外に一致した手書きファイルの検出）が
 * exit 1 で落ちるため、**黙って除外され続けることはない**。誤検出として顕在化したら、
 * 本定数を広げるか判定条件を見直すこと（逆向き＝手書きを生成物と誤認する方向には外れない）。
 */
const AUTO_GENERATED_PROBE_BYTES = 512;

/**
 * ソースファイルが**自動生成**か判定する（`// <auto-generated />` マーカー）。
 * これは除外の**前提そのもの**である。`dotnet ef` が書く `*.Designer.cs` / `*ModelSnapshot.cs` は
 * 全件このマーカーを持ち、手書きのマイグレーション本体（`Up`/`Down`）は 1 件も持たない。
 * 名前ではなく中身で判定するため、パターンを広げても判定は欺けない。
 */
function isAutoGenerated(absPath) {
  let fd;
  try {
    fd = fs.openSync(absPath, 'r');
    const buf = Buffer.alloc(AUTO_GENERATED_PROBE_BYTES);
    const n = fs.readSync(fd, buf, 0, AUTO_GENERATED_PROBE_BYTES, 0);
    return /<auto-generated/i.test(buf.slice(0, n).toString('utf8'));
  } catch {
    return false;
  } finally {
    if (fd !== undefined) {
      try {
        fs.closeSync(fd);
      } catch {
        /* noop */
      }
    }
  }
}

/** 探索起点配下の `.cs` を、起点からの相対パス（`/` 区切り）で列挙する。 */
function findSourceFiles(dir, base = dir, out = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const e of entries) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) {
      // bin / obj は生成物の置き場であり、カバレッジ対象のソースではない（#353 の残骸も含む）。
      if (e.name === 'bin' || e.name === 'obj' || e.name === 'node_modules') continue;
      findSourceFiles(p, base, out);
    } else if (e.isFile() && e.name.endsWith('.cs')) {
      out.push(path.relative(base, p).split(path.sep).join('/'));
    }
  }
  return out;
}

/**
 * **G4（本機構の要）: 除外集合は自動生成ファイルの部分集合でなければならない。**
 *
 * 実ツリーのソースを走査し、**手書き**（`<auto-generated />` を持たない）ファイルが除外パターンに
 * 一致したら違反として返す。呼び出し側はこれを**失敗**にする（notice では素通りする）。
 *
 * これが無いと IADR-0143 決定2（手書きのマイグレーション本体は除外しない）が決定文だけの存在になる。
 * 実際、`**\/Migrations/*.Designer.cs` を `**\/Migrations/**` へ広げる変異は、手書き本体 24 ファイル・
 * 611 行を黙って分母から外しながら（62.03% → 85.50%）、既存の検査を全件素通りした。
 * 合成入力ではなく**実ツリー × 実設定**を対象にすることが要点である。
 */
function findWrongfullyExcluded(root, entries) {
  const compiled = (entries || [])
    .filter((e) => e && typeof e.pattern === 'string')
    .map((e) => ({ pattern: e.pattern, re: globToRegExp(e.pattern) }));
  if (compiled.length === 0) return { violations: [], scanned: 0, generated: 0 };

  const files = findSourceFiles(root);
  const violations = [];
  let generated = 0;
  for (const rel of files) {
    const norm = normalizePath(rel);
    const hit = compiled.find((c) => c.re.test(norm));
    if (!hit) continue;
    if (isAutoGenerated(path.join(root, rel))) {
      generated++;
      continue;
    }
    violations.push({ file: rel, pattern: hit.pattern });
  }
  return { violations, scanned: files.length, generated };
}

/**
 * **G5: パターンの隠蔽（shadowing）を検出する。**
 *
 * 1 件も一致しなかったパターンのうち、**先行パターンが飲み込んだファイルを本来なら拾えたはずの
 * もの**を返す。これは設定が過度に広いことの決定的な証拠であり、部分実行では起こり得ない
 * （空振り一般とは違う）ため**失敗**にしてよい。上記の変異では
 * `**\/Migrations/*ModelSnapshot.cs` が空振りする一方 `**\/Migrations/**` が同じ 7 ファイルを
 * 飲んでおり、症状は出ていたのに notice のため素通りしていた。
 */
function findShadowedPatterns(acc, entries) {
  const compiled = (entries || [])
    .filter((e) => e && typeof e.pattern === 'string')
    .map((e) => ({ pattern: e.pattern, re: globToRegExp(e.pattern), owned: [] }));
  for (const name of acc.keys()) {
    const norm = normalizePath(name);
    const owner = compiled.find((c) => c.re.test(norm));
    if (owner) owner.owned.push(norm);
  }
  const shadowed = [];
  for (const [i, c] of compiled.entries()) {
    if (c.owned.length > 0) continue;
    for (let j = 0; j < i; j++) {
      const stolen = compiled[j].owned.filter((n) => c.re.test(n));
      if (stolen.length > 0) {
        shadowed.push({ pattern: c.pattern, shadowedBy: compiled[j].pattern, files: stolen.length });
        break;
      }
    }
  }
  return shadowed;
}

/**
 * 除外を適用し、**残した集合と外した内訳**の両方を返す。
 * 内訳を返すのは呼び出し側が必ず出力するためであり、「黙って分母を縮める」ことを構造的に避ける。
 */
function applyExcludes(acc, entries) {
  const compiled = (entries || []).map((e) => ({
    pattern: e.pattern,
    reason: e.reason,
    re: globToRegExp(e.pattern),
    files: 0,
    lines: 0,
    covered: 0,
  }));
  const kept = new Map();
  for (const [name, lines] of acc) {
    const norm = normalizePath(name);
    const hit = compiled.find((c) => c.re.test(norm));
    if (!hit) {
      kept.set(name, lines);
      continue;
    }
    hit.files++;
    for (const h of lines.values()) {
      hit.lines++;
      if (h) hit.covered++;
    }
  }
  const byPattern = compiled.map(({ pattern, reason, files, lines, covered }) => ({
    pattern,
    reason,
    files,
    lines,
    covered,
  }));
  return {
    kept,
    byPattern,
    files: byPattern.reduce((s, p) => s + p.files, 0),
    lines: byPattern.reduce((s, p) => s + p.lines, 0),
    covered: byPattern.reduce((s, p) => s + p.covered, 0),
    /** 1 件も当たらなかったパターン（誤記・対象消滅の検知。G2。失敗はさせない） */
    unmatched: byPattern.filter((p) => p.files === 0).map((p) => p.pattern),
  };
}

/**
 * 除外設定の健全性を検査する。違反理由の配列を返す（空なら合格）。
 * G1: 除外率の上限 / G3: 理由の必須。
 */
function validateExclusion({ entries, excludedLines, totalLines, maxExcludedLineShare }) {
  const violations = [];
  const max =
    typeof maxExcludedLineShare === 'number' ? maxExcludedLineShare : DEFAULT_MAX_EXCLUDED_LINE_SHARE;
  for (const [i, e] of (entries || []).entries()) {
    if (!e || typeof e.pattern !== 'string' || e.pattern.trim() === '') {
      violations.push(`exclude[${i}]: pattern が文字列でない`);
      continue;
    }
    // G3: 理由の無い除外は「とりあえず外して恒久化」への入口になるため許さない。
    if (typeof e.reason !== 'string' || e.reason.trim() === '') {
      violations.push(`exclude[${i}] (${e.pattern}): reason が無い（何をなぜ外したかを書くこと）`);
    }
  }
  // G1: パターンが実コードを飲み込む退行を止める。
  if (totalLines > 0 && excludedLines / totalLines > max) {
    violations.push(
      `除外行が全体の ${pct(excludedLines / totalLines)}（${excludedLines}/${totalLines} 行）で上限 ${pct(max)} を超えた。`
        + 'パターンがプロダクションコードを巻き込んでいないか確認すること'
    );
  }
  return violations;
}

function readFloor(root) {
  const fp = path.join(root, FLOOR_FILE);
  if (!fs.existsSync(fp)) return null;
  try {
    const parsed = JSON.parse(fs.readFileSync(fp, 'utf8'));
    return typeof parsed.lineRateFloor === 'number' ? parsed.lineRateFloor : null;
  } catch {
    return null;
  }
}

const pct = (rate) => `${(rate * 100).toFixed(2)}%`;

function main() {
  const args = parseArgs(process.argv.slice(2));
  const reports = findReports(path.resolve(REPO_ROOT, args.root));

  if (reports.length === 0) {
    // 収集前に呼ばれた場合に CI を落とさない（テスト実行前の順序ミスは別の失敗として現れる）。
    notice(
      'check-coverage: coverage.cobertura.xml が見つかりません。dotnet test --collect:"XPlat Code Coverage" の後に実行してください'
    );
    console.log('[check-coverage] SKIP: カバレッジレポートが見つかりませんでした。');
    process.exit(0);
  }

  const acc = new Map();
  for (const fp of reports) accumulate(fs.readFileSync(fp, 'utf8'), acc);
  const raw = summarize(acc);

  // 除外（#390 / IADR-0143）。何をどれだけ外したかは**必ず**出力する。
  const entries = args.exclude ? readExcludes(REPO_ROOT) : [];
  const ex = applyExcludes(acc, entries);
  const { total, covered, lineRate } = args.exclude ? summarize(ex.kept) : raw;

  if (!args.exclude) {
    console.log('[check-coverage] --no-exclude: 除外を適用していない（素の値）。');
  } else if (entries.length === 0) {
    console.log(`[check-coverage] 除外なし（${FLOOR_FILE} に exclude の宣言が無い）。`);
  } else {
    console.log(
      `[check-coverage] 除外 ${ex.files} ファイル・${ex.lines} 行（うち被覆 ${ex.covered} 行）。`
        + `分母 ${raw.total} → ${total} 行`
    );
    for (const p of ex.byPattern) {
      console.log(
        `[check-coverage]   - ${p.pattern}: ${p.files} ファイル・${p.lines} 行（被覆 ${p.covered}）`
          + ` … ${p.reason || '(理由未記入)'}`
      );
    }
    // G2: 空振りは誤記か対象消滅。黙って効かなくなるより騒がしい方がよい（失敗はさせない。
    // 単一プロジェクトだけを部分実行した場合に正当に空振りするため。隠蔽は G5 が失敗させる）。
    if (ex.unmatched.length > 0) {
      notice(
        'check-coverage: 1 件も一致しない除外パターンがある（誤記か対象消滅の可能性）: '
          + ex.unmatched.join(', ')
      );
      console.log(
        `[check-coverage] 注意: 一致 0 件のパターン: ${ex.unmatched.join(', ')}`
      );
    }
    const violations = validateExclusion({
      entries,
      excludedLines: ex.lines,
      totalLines: raw.total,
      maxExcludedLineShare: readMaxExcludedLineShare(REPO_ROOT),
    });

    // G5: 隠蔽されたパターン（先行パターンが飲み込んだために空振りしている）は設定が広すぎる証拠。
    for (const s of findShadowedPatterns(acc, entries)) {
      violations.push(
        `${s.pattern} が 1 件も一致しないのに、先行する ${s.shadowedBy} が同じ ${s.files} ファイルを`
          + '飲み込んでいる（先行パターンが広すぎる）'
      );
    }

    // G4: 除外集合は自動生成ファイルの部分集合であること（IADR-0143 決定2 の機械的担保）。
    // 合成入力ではなく**実ツリー × 実設定**で検査する。
    const srcRoot = path.resolve(REPO_ROOT, args.root);
    const wrong = findWrongfullyExcluded(srcRoot, entries);
    if (wrong.scanned === 0) {
      notice(
        `check-coverage: ${args.root} 配下にソースが見つからず、除外対象が自動生成かを検証できませんでした`
      );
    } else {
      console.log(
        `[check-coverage] 除外対象の素性検査: ソース ${wrong.scanned} 件を走査し、`
          + `除外に一致した ${wrong.generated + wrong.violations.length} 件のうち`
          + `自動生成 ${wrong.generated} 件・手書き ${wrong.violations.length} 件`
      );
      for (const v of wrong.violations) {
        violations.push(
          `手書きファイルが除外されている: ${v.file}（パターン ${v.pattern}）。`
            + '除外してよいのは `// <auto-generated />` を持つファイルだけである（IADR-0143 決定2）'
        );
      }
    }

    if (violations.length > 0) {
      console.error('[check-coverage] 除外設定が不正です:');
      for (const v of violations) console.error(`  - ${v}`);
      process.exit(1);
    }
  }

  const floor = args.floor !== null && !Number.isNaN(args.floor) ? args.floor : readFloor(REPO_ROOT);
  if (floor === null) {
    console.error(`[check-coverage] ${FLOOR_FILE} に lineRateFloor がありません。`);
    process.exit(1);
  }

  console.log(
    `[check-coverage] 行カバレッジ ${pct(lineRate)}（${covered}/${total} 行・レポート ${reports.length} 件）/ floor ${pct(floor)}`
  );

  if (lineRate + 1e-9 < floor) {
    console.error(
      `[check-coverage] floor ${pct(floor)} を下回りました（実測 ${pct(lineRate)}）。`
        + '\nテストを追加するか、低下の理由を PR で説明したうえで floor の見直しを提案してください。'
    );
    process.exit(1);
  }

  if (args.suggest) {
    const candidate = Math.floor((lineRate - RATCHET_MARGIN) * 100) / 100;
    if (candidate > floor) {
      console.log(
        `[check-coverage] ratchet 候補: lineRateFloor を ${floor} → ${candidate} へ引き上げられます`
          + `（実測 ${pct(lineRate)} から余裕 ${pct(RATCHET_MARGIN)} を引いた値）。更新は人手の PR で行ってください。`
      );
    }
  }
  process.exit(0);
}

if (require.main === module) main();

module.exports = {
  parseArgs,
  findReports,
  accumulate,
  summarize,
  readFloor,
  readConfig,
  readExcludes,
  readMaxExcludedLineShare,
  normalizePath,
  globToRegExp,
  applyExcludes,
  validateExclusion,
  isAutoGenerated,
  findSourceFiles,
  findWrongfullyExcluded,
  findShadowedPatterns,
  RATCHET_MARGIN,
  DEFAULT_MAX_EXCLUDED_LINE_SHARE,
};
