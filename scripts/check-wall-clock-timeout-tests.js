#!/usr/bin/env node
'use strict';
/*
 * check-wall-clock-timeout-tests.js
 *
 * NFR / #921 / IADR-0379 決定 4（2026-09-25 追記）:
 * **壁時計どうしの競争で合否が決まる試験**（#885 の形 (a)）を機械的に止める。
 * NFR / #922 / IADR-0168（2026-09-25 追記）: 同じ母集合で**形 (c)**（Wolverine の待ちヘルパを予算つきの入口を
 * 経ずに呼ぶ＝既定 5 秒の窓）も止める。検出規則は `detectShapeC` の注記を参照。
 *
 * 背景（#885 / #900 / #901。実際に落ちるところを捉えた同型が 3 件）:
 *   タイマーのコールバックはスレッドプールが配送する。ソリューション全体の並列実行でプールが塞がると、
 *   「50 ms の打ち切り」と「2 秒の遅延」は**空いた瞬間に両方とも期限切れ**で、どちらが先に走るかは
 *   保証されない（#906 / #907 の計装で確定）。**比を 40 倍にしても、待ちを延ばしても消えない。**
 *   是正の形は IADR-0379 決定 2（遅い上流ではなく**応答しない上流**＋打ち切りの観測）であり、
 *   同じ形のコピーが 12 ファイルあった（#907 / #920 で 0 件になった）。
 *
 * なぜスクリプトなのか:
 *   **次に書かれる試験に効かせないと、同じ形が静かに戻る**（`check-banned-libraries.js` /
 *   `check-tracked-session-timeout.js` と同じ動機）。確率的に赤くなる CI は再実行の習慣を育て、
 *   本物の退行も同じ反応で流される。
 *
 * 検出規則（形 (a)。IADR-0379 決定 4 の規則をそのまま機械化する）:
 *   同一テストファイル内で、**有限の実時間の打ち切り**
 *     `Timeout = <期間>` / `CancelAfter(<期間>)` / `new CancellationTokenSource(<期間>)` /
 *     `Deadline = ….Add…(<期間>)`
 *   が、**実時間の遅延**
 *     `Task.Delay(<期間>)` / `Thread.Sleep(<期間>)` / `DelayingHandler(<期間>)`
 *   **より小さい**なら落とす。<期間> は**定数だけ**を読む（`TimeSpan.FromXxx(<数値>)` か、
 *   ミリ秒の数値リテラル）。変数・式は読まない —— **再現率より的中率を採る**（#921 本文・IADR-0379 決定 4 の 2026-09-25 追記）。
 *
 * 検出しないもの（誤検出を作らないための設計）:
 *   - `Timeout.InfiniteTimeSpan` / `Timeout.Infinite` / 負のミリ秒（＝無期限）。有限でないので競争しない
 *     （**是正後の形〔応答しない上流〕は素通りする**）。
 *   - コメント・文字列リテラル中の言及（`check-tracked-session-timeout.js` の `stripComments` を共用する。
 *     **禁止を説明する散文で検査が自分の目的を殺さない**）。
 *   - `ReplyTimeout = …` のような別名のプロパティ（語境界で `Timeout` だけを見る）。
 *   - 形 (b)（本リポジトリに該当なし）。形 (c) は下の `SHAPES` の 2 行目（#922）。
 *
 * 母集合: **ディレクトリ名が `Tests` で終わる**ディレクトリ配下の `*.cs`。
 *   🔴 `/Tests/` だけで絞ると `backend/TestSupport/*.Tests/` を取りこぼす（#885 の走査で実際に 1 件）。
 *
 * 拡張点（#922 を見据えて）: 検出は `SHAPES`（形ごとの検出器の表）で持つ。新しい形は
 *   `{ id, title, remedy, detect(strippedText) → [{ line, detail }] }` を 1 行足せば、母集合・コメント潰し・
 *   allowlist・報告の書式をそのまま共用できる。
 *
 * allowlist: `ALLOWED`（ファイル単位＋理由＋起票 ID）。**空で始める**（#921。移行前に登録して検査を
 *   無効化する運用は採らない —— 無効化された検査は無いのと同じで、再有効化は必ず忘れられる）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。検出があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-wall-clock-timeout-tests.js
 *   WALL_CLOCK_RACE_CHECK_ROOT=<模擬ツリー> node scripts/check-wall-clock-timeout-tests.js
 */
const fs = require('fs');
const path = require('path');
const { stripComments } = require('./check-tracked-session-timeout.js');

const REPO_ROOT = process.env.WALL_CLOCK_RACE_CHECK_ROOT
  ? path.resolve(process.env.WALL_CLOCK_RACE_CHECK_ROOT)
  : path.resolve(__dirname, '..');

/**
 * 検出してよいと判断したファイル（リポジトリルートからの相対パス・POSIX 区切り → 理由と起票 ID）。
 * 例: ['backend/…/XxxTests.cs', '実時間の経過そのものが命題である（#NNN）。']
 * **空で始める**（#921）。足すときは理由と起票 ID を必ず書く。
 */
const ALLOWED = new Map([
]);

const SCAN_EXT = new Set(['.cs']);
// '.claude' は Claude Code のサブエージェント用 worktree の複製を持つため除外する（姉妹検査器と同じ）。
const SKIP_DIRS = new Set(['bin', 'obj', 'node_modules', '.git', 'planning', '.claude']);

/** 相対パスが「ディレクトリ名が `Tests` で終わる」ディレクトリの配下か。 */
function isUnderTestsDir(rel) {
  const segs = rel.split('/');
  return segs.slice(0, -1).some((s) => s.endsWith('Tests'));
}

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

// ---------------------------------------------------------------------------
// 期間（定数）の読み取り
// ---------------------------------------------------------------------------

/** C# の数値リテラル（桁区切り `_`・小数・接尾辞を許す）。 */
const NUM = String.raw`-?\d[\d_]*(?:\.\d+)?[fFdDmMlLuU]{0,2}`;
const UNIT_MS = { Milliseconds: 1, Seconds: 1000, Minutes: 60_000, Hours: 3_600_000, Days: 86_400_000 };
/** `TimeSpan.FromXxx(<数値>)`（引数 1 個の定数だけ）。 */
const FROM = String.raw`TimeSpan\s*\.\s*From(Milliseconds|Seconds|Minutes|Hours|Days)\s*\(\s*(${NUM})\s*\)`;

/**
 * 期間の式の終わり（`,` `)` `;` `}`）。`TimeSpan.FromSeconds(1) * 2` のような式は読まない（定数だけを読む）。
 */
const END = String.raw`(?=\s*[,;)}])`;

function parseNum(s) {
  return Number(s.replace(/_/g, '').replace(/[fFdDmMlLuU]+$/, ''));
}

/**
 * 引数の先頭にある期間を読む。読めなければ null（変数・式・無期限）。
 * @param {string} arg     引数の先頭から始まる文字列
 * @param {boolean} bareMs 裸の数値をミリ秒として読むか（`Task.Delay(300)` 等）
 */
function readDuration(arg, bareMs) {
  let m = new RegExp(`^\\s*${FROM}${END}`).exec(arg);
  if (m) {
    const ms = parseNum(m[2]) * UNIT_MS[m[1]];
    return Number.isFinite(ms) && ms >= 0 ? ms : null;
  }
  if (bareMs) {
    m = new RegExp(`^\\s*(${NUM})${END}`).exec(arg);
    if (m) {
      const ms = parseNum(m[1]);
      // 負のミリ秒（`-1`）は無期限を意味する。有限でないので競争しない。
      return Number.isFinite(ms) && ms >= 0 ? ms : null;
    }
  }
  return null;
}

/** 位置 → 1 始まりの行番号（stripComments は改行の位置を保つ）。 */
function lineOf(text, index) {
  let line = 1;
  for (let i = 0; i < index; i += 1) if (text.charCodeAt(i) === 10) line += 1;
  return line;
}

// ---------------------------------------------------------------------------
// 形 (a): 有限の実時間の打ち切り ＜ 実時間の遅延
// ---------------------------------------------------------------------------

/**
 * 打ち切り（cutoff）と遅延（delay）の入口。`head` の直後から期間を読む。
 * `head` は語境界つきで照合するため、`ReplyTimeout =` や `readyDeadline =` には当たらない。
 */
const CUTOFF_RULES = [
  { kind: 'Timeout =', head: /\bTimeout\s*=(?!=)/g, bareMs: false },
  { kind: 'CancelAfter(…)', head: /\bCancelAfter\s*\(/g, bareMs: true },
  { kind: 'new CancellationTokenSource(…)', head: /\bnew\s+CancellationTokenSource\s*\(/g, bareMs: true },
  // `Deadline = DateTime.UtcNow.AddSeconds(1)` / `.Add(TimeSpan.FromSeconds(1))` / `.AddMilliseconds(50)`。
  // 同じ文（`;` まで）の中の最初の `.Add…(` を読む。
  { kind: 'Deadline = ….Add…', head: /\bDeadline\s*=(?!=)/g, deadline: true },
];

const DELAY_RULES = [
  { kind: 'Task.Delay(…)', head: /\bTask\s*\.\s*Delay\s*\(/g, bareMs: true },
  { kind: 'Thread.Sleep(…)', head: /\bThread\s*\.\s*Sleep\s*\(/g, bareMs: true },
  { kind: 'DelayingHandler(…)', head: /\bDelayingHandler\s*\(/g, bareMs: false },
];

const DEADLINE_ADD = new RegExp(
  String.raw`^[^;]*?\.\s*Add(Milliseconds|Seconds|Minutes|Hours|Days)?\s*\(`
);

function collect(stripped, rules) {
  const out = [];
  for (const rule of rules) {
    const re = new RegExp(rule.head.source, 'g');
    let m;
    while ((m = re.exec(stripped)) !== null) {
      const rest = stripped.slice(m.index + m[0].length);
      let ms = null;
      if (rule.deadline) {
        const add = DEADLINE_ADD.exec(rest);
        if (add) {
          const arg = rest.slice(add[0].length);
          if (add[1]) {
            const n = new RegExp(`^\\s*(${NUM})\\s*\\)`).exec(arg);
            if (n) {
              const v = parseNum(n[1]) * UNIT_MS[add[1]];
              ms = Number.isFinite(v) && v >= 0 ? v : null;
            }
          } else {
            ms = readDuration(arg, false);
          }
        }
      } else {
        ms = readDuration(rest, rule.bareMs);
      }
      if (ms !== null) out.push({ kind: rule.kind, ms, line: lineOf(stripped, m.index) });
    }
  }
  return out;
}

/**
 * 形 (a) の検出。ファイル内の**最小の有限の打ち切り**が**最大の実時間の遅延**より小さければ 1 件。
 * （規則は「どれか 1 組でも打ち切り ＜ 遅延」であり、最小と最大の比較と同値である。）
 */
function detectShapeA(stripped) {
  const cutoffs = collect(stripped, CUTOFF_RULES);
  const delays = collect(stripped, DELAY_RULES);
  if (cutoffs.length === 0 || delays.length === 0) return [];
  const minCut = cutoffs.reduce((a, b) => (b.ms < a.ms ? b : a));
  const maxDelay = delays.reduce((a, b) => (b.ms > a.ms ? b : a));
  if (!(minCut.ms < maxDelay.ms)) return [];
  return [{
    line: minCut.line,
    detail: `打ち切り ${minCut.kind} ${minCut.ms} ms（${minCut.line} 行）`
      + ` ＜ 遅延 ${maxDelay.kind} ${maxDelay.ms} ms（${maxDelay.line} 行）`,
    cutoff: minCut,
    delay: maxDelay,
  }];
}

// ---------------------------------------------------------------------------
// 形 (c): 待ちヘルパの上限が仕事量に対して小さい（Wolverine の短縮入口の既定 5 秒。#922）
// ---------------------------------------------------------------------------

/**
 * Wolverine の**短縮入口**（`IServiceProvider` / `IHost` の拡張）と、`TrackedSessionConfiguration` の
 * 同名メソッド。前者は `timeoutInMilliseconds = 5000` を既定に持ち、`check-tracked-session-timeout.js`
 * （素の `TrackActivity()` の禁止）を素通りする。後者は `TrackActivityForTest()` の予算（IADR-0168）の中で走る。
 * **名前では両者を区別できない**ため、受け手（`.` の左の式）を読んで区別する。
 */
const WAIT_SHORTCUTS = [
  'ExecuteAndWaitAsync',
  'ExecuteAndWaitValueTaskAsync',
  'InvokeMessageAndWaitAsync',
  'SendMessageAndWaitAsync',
  'PublishMessageAndWaitAsync',
];

/** 予算つきの入口（`AiStockTrading.TestSupport.Messaging`）。受け手の式にこれが現れれば予算の中である。 */
const BUDGETED_ENTRY = /\bTrackActivityForTest\s*\(/;

const IDENT_CHAR = /[A-Za-z0-9_]/;

/**
 * `.` の位置（dotIndex）から左へ、メソッド呼び出しの受け手の式を読む。
 * 識別子・`.`・`?.`・呼び出しの括弧（入れ子を数える）・型引数の山括弧・空白（改行を含む）をたどる。
 */
function receiverOf(text, dotIndex) {
  let i = dotIndex - 1;
  for (;;) {
    while (i >= 0 && /\s/.test(text[i])) i -= 1;
    if (i < 0) break;
    if (text[i] === ')' || text[i] === '>' || text[i] === ']') {
      const close = text[i];
      const open = close === ')' ? '(' : close === '>' ? '<' : '[';
      let depth = 0;
      for (; i >= 0; i -= 1) {
        if (text[i] === close) depth += 1;
        else if (text[i] === open) {
          depth -= 1;
          if (depth === 0) break;
        }
      }
      i -= 1;
      continue;
    }
    if (IDENT_CHAR.test(text[i])) {
      while (i >= 0 && IDENT_CHAR.test(text[i])) i -= 1;
      let j = i;
      while (j >= 0 && /\s/.test(text[j])) j -= 1;
      if (j >= 0 && text[j] === '.') {
        i = j - 1;
        if (i >= 0 && text[i] === '?') i -= 1;
        continue;
      }
      break;
    }
    break;
  }
  return text.slice(i + 1, dotIndex).trim();
}

/**
 * 受け手が予算の中か。受け手の式が `TrackActivityForTest(` を含むか、受け手が**単独の識別子**で、
 * 呼び出しより前・**同じスコープ**にあるその識別子への**直近の**代入が `TrackActivityForTest(` を含めば
 * 予算の中とみなす（`var tracking = host.TrackActivityForTest(); … tracking.ExecuteAndWaitAsync(…)` の形）。
 * #922 のレビュー: ファイル全体で名前だけを見ると、別メソッドの同名変数（予算つき）に引きずられて
 * 素の `TrackActivity(…)` を束縛した呼び出しを見逃す。代入の位置から呼び出しまでの間に、代入を含む
 * ブロックが閉じる（波括弧の深さが代入の位置より浅くなる）ものはスコープ外として採らない。
 * 同じスコープで後から再代入していれば、呼び出しに近い方（直近）で判定する（自分から派生する再代入は読み飛ばす）。
 */
function isBudgeted(receiver, stripped, callIndex = stripped.length) {
  if (BUDGETED_ENTRY.test(receiver)) return true;
  const id = /^[A-Za-z_]\w*$/.exec(receiver);
  if (!id) return false;
  const assign = new RegExp(String.raw`\b${id[0]}\s*=(?!=)([^;]*)`, 'g');
  const candidates = [];
  let a;
  while ((a = assign.exec(stripped)) !== null && a.index < callIndex) candidates.push(a);
  const selfDerived = new RegExp(String.raw`^\s*${id[0]}\b`);
  for (let i = candidates.length - 1; i >= 0; i--) {
    if (!inSameScope(stripped, candidates[i].index, callIndex)) continue;
    // `tracking = tracking.DoNotAssertOnExceptionsDetected()` のように自分から派生する再代入は、元の束縛を引き継ぐ。
    if (selfDerived.test(candidates[i][1])) continue;
    return BUDGETED_ENTRY.test(candidates[i][1]);
  }
  return false;
}

/** from から to までの間に、from の位置を含むブロックが閉じないか（波括弧の深さが 0 を下回らないか）。 */
function inSameScope(text, from, to) {
  let depth = 0;
  for (let i = from; i < to; i++) {
    if (text[i] === '{') depth++;
    else if (text[i] === '}' && --depth < 0) return false;
  }
  return true;
}

/**
 * 形 (c) の検出。Wolverine の待ちヘルパを、予算つきの入口を経ずに（＝既定 5 秒の窓で）呼んでいれば 1 件。
 * `timeoutInMilliseconds:` を明示した呼び出しも落とす —— 予算の単一情報源（IADR-0168 決定 2。環境変数で
 * 上書きできる）を迂回するからである。使うべき入口は `ExecuteAndWaitForTestAsync()` か
 * `TrackActivityForTest().…AndWaitAsync()`。
 */
function detectShapeC(stripped) {
  const hits = [];
  const re = new RegExp(String.raw`\.\s*(${WAIT_SHORTCUTS.join('|')})\s*(?:<[^()]*>)?\s*\(`, 'g');
  let m;
  while ((m = re.exec(stripped)) !== null) {
    const receiver = receiverOf(stripped, m.index);
    if (receiver === '' || isBudgeted(receiver, stripped, m.index)) continue;
    const line = lineOf(stripped, m.index + m[0].indexOf(m[1]));
    hits.push({
      line,
      detail: `\`${receiver.replace(/\s+/g, ' ')}.${m[1]}(…)\` は予算つきの入口を経ていない`
        + '（Wolverine の既定 5 秒の窓で打ち切る）',
      method: m[1],
      receiver,
    });
  }
  return hits;
}

/**
 * 形ごとの検出器の表。**#922（形 (c)）などの新しい形はここへ 1 行足す**（母集合・コメント潰し・
 * allowlist・報告の書式を共用する）。
 */
const SHAPES = [
  {
    id: 'a',
    title: '壁時計どうしの競争（有限の実時間の打ち切り ＜ 実時間の遅延）',
    remedy: '遅い上流ではなく「応答しない上流」（`Task.Delay(Timeout.InfiniteTimeSpan, ct)`）で上限を固定し、'
      + '打ち切りで終わったことを観測で確定させてください（IADR-0379 決定 2。例: HttpCostControlGateTests）。',
    detect: detectShapeA,
  },
  {
    id: 'c',
    title: '待ちヘルパの上限が仕事量に対して小さい（Wolverine の短縮入口の既定 5 秒）',
    remedy: '`services.ExecuteAndWaitForTestAsync(…)` か `host.TrackActivityForTest().ExecuteAndWaitAsync(…)`'
      + '（AiStockTrading.TestSupport.Messaging）を使い、壁時計の予算（既定 30 秒・環境変数で上書き可）の中で待ってください'
      + '（IADR-0168。#357 は 5 秒をスケジューリング遅延だけで超えた実測を持つ）。',
    detect: detectShapeC,
  },
];

/** 1 ファイル分の検出（コメント・文字列を潰してから全形を当てる）。 */
function findViolations(text, shapes = SHAPES) {
  const stripped = stripComments(text);
  const hits = [];
  for (const shape of shapes) {
    for (const h of shape.detect(stripped)) hits.push({ shape: shape.id, ...h });
  }
  return hits;
}

/**
 * 母集合を走査して検出を返す。`stats.scanned` に母集合の件数を書く（0 件なら何も見ていない）。
 */
function checkTree(root = REPO_ROOT, allowed = ALLOWED, shapes = SHAPES, stats = {}) {
  const violations = [];
  let scanned = 0;
  for (const fp of scanFiles(root)) {
    const rel = path.relative(root, fp).split(path.sep).join('/');
    if (!isUnderTestsDir(rel)) continue;
    scanned += 1;
    if (allowed.has(rel)) continue;
    let text;
    try {
      text = fs.readFileSync(fp, 'utf8');
    } catch {
      continue;
    }
    for (const hit of findViolations(text, shapes)) violations.push({ file: rel, ...hit });
  }
  stats.scanned = scanned;
  return violations;
}

function main() {
  const stats = {};
  const violations = checkTree(REPO_ROOT, ALLOWED, SHAPES, stats);

  if (violations.length === 0) {
    console.log(
      `[check-wall-clock-timeout-tests] OK: テストファイル ${stats.scanned} 件に`
      + ' 壁時計に合否を委ねる形（(a) 打ち切りと遅延の競争・(c) 既定 5 秒の待ちヘルパ）はありません'
      + `（allowlist ${ALLOWED.size} 件）。`
    );
    process.exit(0);
  }

  console.error(
    `[check-wall-clock-timeout-tests] 壁時計に合否を委ねる形を ${violations.length} 件検出しました:`
  );
  for (const v of violations) {
    console.error(`  ${v.file}:${v.line}: [形 (${v.shape})] ${v.detail}`);
  }
  console.error('');
  for (const shape of SHAPES.filter((s) => violations.some((v) => v.shape === s.id))) {
    console.error(`  形 (${shape.id}) ${shape.title}`);
    console.error(`  → ${shape.remedy}`);
  }
  if (violations.some((v) => v.shape === 'a')) {
    console.error(
      '  塞がったスレッドプールは、既に期限の切れた 2 つのタイマーの順序を入れ替えます。比を広げても、'
    );
    console.error('  待ちを延ばしても消えません（#885 / #900 / #901 で実測・IADR-0379）。');
  }
  console.error(
    '  実時間の経過そのものが命題である試験だけは、ALLOWED に理由と起票 ID を添えて登録してください。'
  );
  process.exit(1);
}

if (require.main === module) {
  main();
}

module.exports = {
  ALLOWED,
  SHAPES,
  WAIT_SHORTCUTS,
  CUTOFF_RULES,
  DELAY_RULES,
  isUnderTestsDir,
  readDuration,
  detectShapeA,
  detectShapeC,
  receiverOf,
  findViolations,
  checkTree,
};
