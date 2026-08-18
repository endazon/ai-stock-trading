#!/usr/bin/env node
'use strict';
/*
 * check-permission-denials.js
 * claude-code-action の実行ログ（execution_file）を読み、**権限拒否で実行できなかった
 * ツール**を itemize して報告する。拒否が実行を実質潰した規模ならジョブを止める。
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 適用範囲: claude-code-action の実行ログ形式・拒否文言専用である。他エンジンのワークフローは
 *   対象外であり、**対象外は「検査済み」を意味しない**（docs/ai-orchestration.md §6）。
 *
 * 背景（実運用で発生した障害）:
 *   claude-doc-review.yml のレビューが 21 ターン中 17 回の権限拒否で潰れ、レビュー本文を
 *   1 文字も書けないまま終了した。にもかかわらず結果は `"subtype": "success",
 *   "is_error": false` であり、**CI は緑**、PR に残ったのは「6 グループに分けて並列精査中」
 *   という進行中コメントだけだった。原因は `--allowedTools` に `Task`（サブエージェント
 *   起動）が無いことだったが、これを突き止めるにはジョブログを開いて `SDK options:` の
 *   配列と `permission_denials_count` を突き合わせる必要があった。
 *
 *   すなわち二重の欠陥がある:
 *     1. 許可ツールの不足そのもの
 *     2. **不足したことが誰にも見えない**（緑のジョブ・進行中のまま止まったコメント）
 *   本スクリプトは 2 を塞ぐ。1 は許可ツールを直せば消えるが、次に別のツールが不足した
 *   ときに再び黙って潰れるため、汎用の安全弁が要る。
 *
 * 失敗判定（段階ポリシー）:
 *   当初は「拒否 1 件でも exit 1」だった。しかし実運用 6 巡（拒否 17 → 12 → 8 → 5 → 3 → 2 件）
 *   の実測で、拒否は 2 種類に分かれることが判った。
 *     A. 実行を実質潰す拒否（17/21 ターン・Task 全滅、12 件・検証をすべて偽装）
 *     B. 探索的コマンド 1〜2 件の拒否（レビュー本文は正常・「実行できなかったこと」節も
 *        誠実に出力。プロセス置換・env 前置きなど**許可リストでは原理的に解決できない構文**
 *        が主で、ツールを足しても根絶できない）
 *   B まで赤にすると「成果物は正しいのにジョブが赤」（issue planning#146 / planning#149 / planning#160 が繰り返し
 *   問題視した形）が常態化し、「権限拒否の赤は無視してよい」という学習を生む。それは
 *   本検査器を入れた目的（A を見逃さない）を逆から壊す。
 *
 *   よって既定は: **件数 > 許容値（既定 4）または拒否がターン数の半分以上**で exit 1。
 *   それ未満は警告（アノテーション + 実行サマリ）を出して exit 0。可視化は件数に関わらず
 *   常に行う——「見えるが緑」と「見えずに緑」はまったく別物である。
 *   過去の実測に当てはめると: 17/21・12・8・7・5 件は失敗（すべて実害があった）、
 *   4 件以下は警告つき成功（いずれもレビュー本文は正常だった）。
 *
 * 使い方:
 *   node scripts/check-permission-denials.js <execution-file> [--self-test]
 *
 * 環境変数:
 *   ALLOW_PERMISSION_DENIALS=1     拒否があっても失敗させない（警告のみ。緊急避難用）
 *   STRICT_PERMISSION_DENIALS=1    拒否 1 件でも失敗させる（旧来の挙動。opt-in）
 *   PERMISSION_DENIALS_TOLERANCE   失敗としない上限件数（既定 4）
 */
const fs = require('fs');
const { emit, warn } = require('./lib/ci-annotate.js');

/**
 * 実行ログを読む。JSON 配列と NDJSON（1 行 1 イベント）の両方を受ける。
 * action の出力形式が変わっても片方で拾えるようにしておく。
 */
function parseEvents(text) {
  const trimmed = text.trim();
  if (!trimmed) return [];
  try {
    const parsed = JSON.parse(trimmed);
    if (Array.isArray(parsed)) return parsed;
    return [parsed];
  } catch (e) {
    // NDJSON フォールバック。壊れた行は黙って捨てず、読めた分だけで判断する。
    const events = [];
    for (const line of trimmed.split('\n')) {
      const s = line.trim();
      if (!s) continue;
      try {
        events.push(JSON.parse(s));
      } catch (e2) {
        /* 読めない行は無視する（部分的に壊れたログでも拒否は拾いたい） */
      }
    }
    return events;
  }
}

/** メッセージ本文の content 配列を取り出す（形が違っても落ちない）。 */
function contentOf(event) {
  const c = event && event.message && event.message.content;
  return Array.isArray(c) ? c : [];
}

/**
 * 報告用のラベルを作る。Bash は**コマンド名まで**出す。
 *
 * 背景（issue planning#146）: 当初はツール名だけを出していたため、報告が「Bash（4 件）」で止まり
 * **どのコマンドが拒否されたのか判らなかった**。許可リストは `Bash(git diff:*)` のように
 * コマンド単位で書くため、ツール名だけでは何を足せばよいか決められない。実際、起票者は
 * ジョブログを追って読み取り系 git だと推定する必要があった。報告が行動につながらないなら、
 * 件数だけを出していた元の状態とさして変わらない。
 *
 * 各セグメントは先頭 2 トークンまでに切り詰める（`git diff origin/main...HEAD` → `git diff`）。
 * 理由は 2 つある。許可リストの粒度がコマンド＋サブコマンドであること、そして引数には
 * トークン・パス等が入り得るため、ログへ丸ごと出さないことである。
 *
 * 【パイプライン（issue planning#158）】当初は「パイプ以降は別の話」として**先頭セグメントだけ**を
 * 見ていた。これは権限判定については誤りである。Claude Code の Bash 許可は
 * **パイプラインの各コマンドを個別に判定する**ため、`git show X | cmp - Y` は `cmp` が
 * 未許可なら拒否される。ところが先頭だけを見る実装では、報告に出るのは**無実の `git show`**
 * であった。結果として検査器は「原因のコマンドを隠し」「既に許可済みのコマンドを名指しし」
 * 「それを --allowedTools に加えよ」と助言する、誤誘導つきの報告を出していた。
 * 実測では 3 回の run で連続して発生し（3 / 3 / 7 件）、報告が原因を指さないため塞げなかった。
 *
 * よって `|` `&&` `||` `;` で分割し、**各セグメントの先頭 2 トークンをすべて列挙**する。
 * どれが原因かまでは実行ログから判らないが、**候補に原因が含まれる**ことが重要である
 * （従来は候補集合から原因が構造的に抜け落ちていた）。
 * 1 件の拒否は 1 件として数えたいので、セグメントごとに行を分けず 1 ラベルへまとめる。
 */
// シェルの複合コマンドで先頭に現れる予約語。コマンド名ではないので読み飛ばす。
const SHELL_KEYWORDS = new Set(['do', 'done', 'then', 'fi', 'else', 'elif', 'esac', 'in', '{', '}']);

// 2 トークン目（サブコマンド／スクリプト名）を採るコマンド。許可リストがこの粒度で
// 書かれるもの（`Bash(git log:*)` / `Bash(node scripts/x.js:*)`）に限る。
// この集合に無いコマンド（echo / ls / diff / cmp …）の 2 トークン目は**引数**であり、
// ラベルに出すと `Bash(echo done)` / `Bash(diff .claude-pr/…)` のようなノイズになる
// （実測で両方の形が出た）。
const SUBCOMMAND_COMMANDS = new Set([
  'git', 'gh', 'node', 'npm', 'npx', 'pnpm', 'yarn', 'dotnet',
  'python', 'python3', 'pip', 'go', 'cargo', 'docker', 'kubectl', 'helm',
  'make', 'bash', 'sh', 'gradle',
]);

function labelOf(name, input) {
  if (name !== 'Bash') return name || '(不明なツール)';
  const cmd = input && typeof input.command === 'string' ? input.command.trim() : '';
  if (!cmd) return 'Bash';

  // リダイレクトは**分割の前に**取り除く。`2>&1` は `&` を含むため、先に分割すると
  // `1` が独立したセグメントとして残り、`Bash(ls | 1 | head)` のような存在しない
  // コマンドを報告してしまう（実測でこの形が出た）。
  let stripped = cmd.replace(/(\d?>>?&?\d?|<)/g, ' ');
  // プロセス置換 `<(cmd)`・コマンド置換 `$(cmd)`・サブシェル `(cmd)` の中身も個別に
  // 判定対象になる。括弧を区切りへ正規化して中のコマンドを露出させる。放置すると
  // `diff <(git show A) <(git show B)` が `Bash(diff (git)` という壊れたラベルになる
  // （実測でこの形が出た。`<` は上で除去済みのため `(` だけが残る）。
  stripped = stripped.replace(/[$]?\(/g, '|').replace(/\)/g, ' ').replace(/`/g, '|');

  const labels = [];
  // パイプ・`&&` `||` `;` で分割する。各セグメントが個別に許可判定を受ける。
  for (const raw of stripped.split(/[|;&\n]+/)) {
    const seg = raw.trim();
    if (!seg) continue;
    const tokens = seg.split(/\s+/).filter(Boolean);
    while (tokens.length && SHELL_KEYWORDS.has(tokens[0])) tokens.shift();
    if (!tokens.length) continue;
    // `git -C <dir> <sub>` は 4 トークンまで採る（issue planning#160）。2 トークン固定だと
    // `Bash(git -C)` になり、**対処に必要なサブコマンドが消える**。キットは
    // `Bash(git -C planning log:*)` 等を自ら配布しており、planning を submodule 参照する
    // 全リポジトリでこの形が日常的に出る。許可リストのエントリと同じ粒度へ揃える。
    let label;
    if (tokens[0] === 'git' && tokens[1] === '-C' && tokens.length >= 4) {
      label = tokens.slice(0, 4).join(' ');
    } else if (/^[A-Za-z_][A-Za-z0-9_]*=/.test(tokens[0])) {
      // 環境変数の前置き形（`VAR=1 cmd`）。前方一致に当たらないこと自体が原因なので、
      // 割り当てとコマンド名を並べて見せる（`STRICT_…=1 node` の形。実測で出た表示を維持）。
      label = tokens.slice(0, 2).join(' ');
    } else {
      // 2 トークン目はサブコマンドを持つコマンドに限って採る。フラグ（`head -5`）・
      // 引用符付き文字列（`echo "exit:$?"`）・変数展開（`cmp $f`）も引数なので採らない。
      const second = tokens[1];
      const takeSecond = second && !/^[-"'$]/.test(second) && SUBCOMMAND_COMMANDS.has(tokens[0]);
      label = takeSecond ? `${tokens[0]} ${second}` : tokens[0];
    }
    if (!labels.includes(label)) labels.push(label);
  }
  if (!labels.length) return 'Bash';
  const shown = labels.slice(0, 4);
  if (labels.length > shown.length) shown.push('…');
  return `Bash(${shown.join(' | ')})`;
}

/**
 * 引用符（`'` / `"`）で囲まれた範囲に `|` が含まれるか。
 *
 * 素朴に 1 文字ずつ走査する。エスケープ（`\"`）は引用の開閉として数えない。
 * シェルの完全な字句解析ではないが、**交替を含む grep パターンを拾う**という目的には足りる。
 * 迷ったら false（＝ A 扱い）へ倒す方針に沿い、判定できない形は拾わない。
 */
function hasQuotedPipe(cmd) {
  let quote = null;
  for (let i = 0; i < cmd.length; i++) {
    const ch = cmd[i];
    if (ch === '\\') { i++; continue; } // エスケープされた次の 1 文字は読み飛ばす
    if (quote) {
      if (ch === quote) quote = null;
      else if (ch === '|') return true;
    } else if (ch === '"' || ch === "'") {
      quote = ch;
    }
  }
  return false;
}

/**
 * その拒否が「許可リストを直せば消えるか」を判定する（endazon/ai-stock-trading#391 ②の本体）。
 *
 * 拒否には性質の違う 2 種類がある。
 *   A. 許可漏れ            … `npm ci` / `dotnet ef` / `git ls-files` / MCP ツール等。**直せる**
 *   B. 構文上ありえない形  … env 前置き・リダイレクト・プロセス置換・ループ・`cd`・
 *                            `git -C <絶対パス>`。**許可リストでは原理的に直せない**
 *
 * B を失敗判定の分母に入れると、**許可リストを完璧にしても偽の赤が出続ける**。
 * かといって件数しきい値を上げると A の検出が鈍る。endazon/ai-stock-trading#391 が「維持すると偽の赤が出続け、
 * 緩めると許可漏れに気づけなくなる」と書いたトレードオフは、**2 種類を分けていない**
 * ことから生じている。よって分類して A だけで失敗を判定する。
 *
 * **B も必ず可視化する。** 失敗させないことと、見せないことは別である
 * （「見えるが緑」と「見えずに緑」はまったく別物、という本検査器の既存方針を維持する）。
 *
 * 判定は保守的に行う——**迷ったら A（直せる）とする**。B と誤判定すると、本物の許可漏れが
 * 分母から抜けて検出できなくなる。逆に A と誤判定しても、出るのは従来どおりの赤である。
 *
 * @returns {string|null} B ならその理由、A なら null
 */
function unfixableReason(name, input) {
  // MCP ツール等（Bash 以外）は許可リストに名前を書けば必ず通る＝常に A。
  if (name !== 'Bash') return null;
  const cmd = input && typeof input.command === 'string' ? input.command.trim() : '';
  if (!cmd) return null;

  if (hasRedirect(input)) return 'リダイレクト（レビュー用に書き込み手段は無い）';
  // プロセス置換 `<(…)` / コマンド置換 `$(…)` / バッククォート。中のコマンドが許可済みでも拒否される。
  if (/<\(|\$\(|`/.test(cmd)) return 'プロセス置換・コマンド置換';
  // 引用符の中の `|`（`grep -E "A|B"` の交替）。許可判定はコマンド文字列を分割してから
  // 前方一致を見るため、**引用符の中の `|` もパイプとして分割され**、`B"` のような
  // 存在しないコマンドが未許可と判定される。**正規表現の断片は許可リストに書けない**ので
  // 原理的に直せない。対処はプロンプト側（交替を使わず grep を分ける）である。
  // 実測: PR endazon/ai-stock-trading#395 のレビューで 5 件中 3 件がこの形だった
  // （`Bash(git -C planning show | Grep | 決定 | 決定14\ | …)` のように分割された姿で報告される）。
  if (hasQuotedPipe(cmd)) return '引用符内の `|`（grep の交替等。許可判定が分割してしまう）';

  // 以降はセグメント単位で見る。1 つでも該当すれば鎖全体が実行されないため B とする。
  const segments = cmd
    .replace(/(\d?>>?&?\d?)/g, ' ')
    .split(/[|;&\n]+/)
    .map((s) => s.trim())
    .filter(Boolean);
  for (const seg of segments) {
    const tokens = seg.split(/\s+/).filter(Boolean);
    if (!tokens.length) continue;
    const head = tokens[0];
    // 環境変数の前置き（`VAR=1 cmd`）。先頭トークンが `VAR=1` になり前方一致に当たらない。
    if (/^[A-Za-z_][A-Za-z0-9_]*=/.test(head)) return '環境変数の前置き（前方一致に当たらない）';
    // シェルの複合形。先頭が予約語になり許可リストで表現できない。
    if (/^(for|while|until|if|case|function)$/.test(head)) return 'シェルのループ・複合形';
    // `cd` は許可リストに無く、後続が許可済みでも先頭で拒否される。
    if (head === 'cd') return 'cd によるディレクトリ移動';
    // `git -C <絶対パス>` は `Bash(git -C planning …)` に当たらない。`Bash(git -C:*)` の
    // 一括許可は書き込み系まで通るため設計上禁止されており、許可リストでは直せない。
    if (head === 'git' && tokens[1] === '-C' && tokens[2] && tokens[2].startsWith('/')) {
      return 'git -C の絶対パス指定（相対パス `planning` を使うこと）';
    }
  }
  return null;
}

/** ラベルにパイプライン（複数セグメント）が含まれるか。報告の注記を出す判断に使う。 */
function hasPipeline(byTool) {
  for (const name of byTool.keys()) if (name.includes(' | ')) return true;
  return false;
}

/**
 * コマンドがリダイレクト（`>` / `>>`）を含むか。
 *
 * リダイレクトはラベルから落とす（ファイル名はコマンドではない）が、**拒否の原因が
 * リダイレクトそのもの**である場合がある（レビュー用は書き込み手段を持たない設計のため。
 * issue planning#160 で `git show X > /tmp/a` が `Bash(git show)` として報告された）。
 * ラベルだけ見ると「許可済みのはずが拒否された」と読めてしまうので、注記の材料に使う。
 */
function hasRedirect(input) {
  const cmd = input && typeof input.command === 'string' ? input.command : '';
  // `2>&1` 等の fd 複製は**ファイルへの書き込みではない**ので、注記の対象から外す
  // （これを数えると「書き込みが原因かもしれない」という誤った案内を毎回出すことになる）。
  const withoutFdDup = cmd.replace(/\d*>&\d*/g, ' ');
  return />>?\s*\S/.test(withoutFdDup);
}

/**
 * tool_result のテキストが「権限拒否」を表しているか。
 *
 * SDK が itemize した `permission_denials` を持たない版のためのフォールバック経路であり、
 * 文言に依存する。過剰一致を避けるため「permission」と「拒否を表す語」の同時出現を要求する。
 */
function looksLikeDenial(text) {
  const s = String(text);
  if (!/permission/i.test(s)) return false;
  return /(denied|deny|not granted|haven'?t granted|hasn'?t granted|requested permissions)/i.test(s);
}

/** tool_result の content は文字列にも配列にもなり得る。テキストへ畳む。 */
function resultTextOf(block) {
  const c = block && block.content;
  if (typeof c === 'string') return c;
  if (Array.isArray(c)) return c.map((p) => (p && typeof p.text === 'string' ? p.text : '')).join(' ');
  return '';
}

/**
 * 実行ログから拒否されたツールを集計する。
 * 戻り値 { count, byTool: Map<name, n>, itemized, source }。
 *
 * 3 段階で降格する。上ほど正確なので、取れた時点で確定させる。
 *   1. result.permission_denials（SDK が itemize したもの。ツール名が確実）
 *   2. tool_result の走査（tool_use_id からツール名を逆引きする）
 *   3. permission_denials_count のみ（件数は判るがツール名は判らない）
 * 3 になった場合も**黙らない**。件数だけでも出さないと、この検査を入れた意味が消える。
 */
function collectDenials(events) {
  const result = events.find((e) => e && e.type === 'result') || null;
  const numTurns = result && Number.isFinite(result.num_turns) ? result.num_turns : null;
  const byTool = new Map();
  // endazon/ai-stock-trading#391 ②: 許可リストで直せない拒否（B）は失敗判定の分母から外す。理由ごとに件数を持つ。
  const unfixableByReason = new Map();
  let unfixableCount = 0;
  const bump = (name) => byTool.set(name, (byTool.get(name) || 0) + 1);
  const bumpUnfixable = (reason) => {
    unfixableCount++;
    unfixableByReason.set(reason, (unfixableByReason.get(reason) || 0) + 1);
  };
  let redirect = false;

  // 1. SDK が itemize した配列。
  const itemizedList = result && Array.isArray(result.permission_denials) ? result.permission_denials : null;
  if (itemizedList && itemizedList.length) {
    for (const d of itemizedList) {
      const input = d && (d.tool_input || d.toolInput);
      const name = d && (d.tool_name || d.toolName);
      if (hasRedirect(input)) redirect = true;
      bump(labelOf(name, input));
      const reason = unfixableReason(name, input);
      if (reason) bumpUnfixable(reason);
    }
    return {
      count: itemizedList.length,
      fixableCount: itemizedList.length - unfixableCount,
      unfixableCount,
      unfixableByReason,
      byTool,
      itemized: true,
      source: 'permission_denials',
      redirect,
      numTurns,
    };
  }

  // 2. tool_use_id → ラベルを作ってから tool_result を走査する。
  const labelById = new Map();
  const redirectById = new Set();
  const unfixableById = new Map();
  for (const e of events) {
    if (!e || e.type !== 'assistant') continue;
    for (const b of contentOf(e)) {
      if (b && b.type === 'tool_use' && b.id) {
        labelById.set(b.id, labelOf(b.name, b.input));
        if (hasRedirect(b.input)) redirectById.add(b.id);
        const reason = unfixableReason(b.name, b.input);
        if (reason) unfixableById.set(b.id, reason);
      }
    }
  }
  for (const e of events) {
    if (!e || e.type !== 'user') continue;
    for (const b of contentOf(e)) {
      if (!b || b.type !== 'tool_result') continue;
      if (!b.is_error) continue;
      if (!looksLikeDenial(resultTextOf(b))) continue;
      if (redirectById.has(b.tool_use_id)) redirect = true;
      bump(labelById.get(b.tool_use_id) || '(不明なツール)');
      const reason = unfixableById.get(b.tool_use_id);
      if (reason) bumpUnfixable(reason);
    }
  }
  if (byTool.size) {
    let n = 0;
    for (const v of byTool.values()) n += v;
    return {
      count: n,
      fixableCount: n - unfixableCount,
      unfixableCount,
      unfixableByReason,
      byTool,
      itemized: true,
      source: 'tool_result',
      redirect,
      numTurns,
    };
  }

  // 3. 件数のみ。内訳が無いので分類もできない。**全件を A（直せる）として扱う**——
  // 分類できないことを理由に失敗を免れると、本検査器が塞いでいる「見えずに緑」へ戻る。
  const count = result && Number.isFinite(result.permission_denials_count) ? result.permission_denials_count : 0;
  return {
    count,
    fixableCount: count,
    unfixableCount: 0,
    unfixableByReason,
    byTool,
    itemized: false,
    source: 'permission_denials_count',
    redirect,
    numTurns,
  };
}

/**
 * 拒否が「実行を実質潰した」規模かを判定する（段階ポリシーの本体）。
 *
 * - 件数 > tolerance（既定 4）: 探索的な取りこぼしの規模を超えている。
 *   実測では 5 件以上の run はすべて実害（許可リストの構造的欠落・検証の偽装）を伴った。
 * - 拒否がターン数の半分以上: 件数が少なくても、短い実行の大半が拒否されたなら
 *   成果物は成立していない（元障害は 17/21 = 81% だった）。
 * 純粋関数（I/O を持たない。テスト可能にするため）。
 */
function isCritical({ count, fixableCount, numTurns }, tolerance) {
  // endazon/ai-stock-trading#391 ②: 判定に用いるのは**許可リストで直せる拒否（A）だけ**である。B を含めると
  // 許可リストを完璧にしても偽の赤が出続ける。fixableCount を持たない呼び出し（旧形の
  // オブジェクト・テスト）では count へ退避する（分類できないなら安全側＝全件 A）。
  const effective = Number.isFinite(fixableCount) ? fixableCount : count;
  if (effective > tolerance) return true;
  // ターン比の判定も A で行う。B ばかりのターンは「成果物が成立していない」形ではない
  // （レビュー本文は正常に出ており、拒否されたのは探索的な構文だけである）。
  if (Number.isFinite(numTurns) && numTurns > 0 && effective / numTurns >= 0.5) return true;
  return false;
}

/** 報告用の 1 行メッセージを組み立てる。 */
function formatDenials(denials) {
  const { count, byTool, itemized, redirect } = denials;
  const head = `AI の実行中にツールの権限拒否が ${count} 件発生した（CI には承認する人間が居ないため、これらの作業は実行されていない）`;
  if (!itemized) {
    return (
      `${head}。実行ログにツール名が残っていないため内訳を特定できない。` +
      'ジョブログの `SDK options:` の allowedTools 配列と、AI が実行しようとした操作を突き合わせること'
    );
  }
  const detail = [...byTool.entries()]
    .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
    .map(([name, n]) => `${name}（${n} 件）`)
    .join(' / ');
  const pipeNote = hasPipeline(byTool)
    ? '`A | B` の形は**各コマンドが個別に許可判定される**ため、拒否されたのは後段のコマンドかもしれない。' +
      '前段が既に許可済みなら、まず後段（例: cmp / diff / head / tail）を疑うこと。'
    : '';
  const redirectNote = redirect
    ? '**リダイレクト（`>`）を含むコマンドがある**。レビュー用は書き込み手段を持たない設計のため、' +
      'コマンド名ではなくリダイレクトそのものが拒否の原因かもしれない。出力はファイルへ書かず、そのまま読むこと。'
    : '';
  return (
    `${head}: ${detail}。${unfixableNote(denials)}${pipeNote}${redirectNote}` +
    '対処は 2 通りである: (a) 必要なツールを claude_args の --allowedTools に加える、' +
    '(b) そのツールを使わせないようプロンプト側で作業手順を狭める。' +
    'どちらも取らずに放置すると、ジョブは success のまま成果物だけが欠けた状態が続く'
  );
}

/**
 * 許可リストで直せない拒否（B）の内訳を 1 文にする（endazon/ai-stock-trading#391 ②）。
 * **失敗させないからこそ、必ず見せる。** 見せないと「B が何件あるか」が誰にも判らず、
 * 分類器が壊れて A を B と誤判定しても気付けない。
 */
function unfixableNote(denials) {
  const n = denials && denials.unfixableCount;
  if (!n) return '';
  const byReason = denials.unfixableByReason;
  const detail =
    byReason && byReason.size
      ? [...byReason.entries()]
          .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
          .map(([reason, c]) => `${reason}（${c} 件）`)
          .join(' / ')
      : '';
  return (
    `うち **${n} 件は許可リストでは直せない形**である${detail ? `: ${detail}` : ''}。` +
    'これらは失敗判定の対象外とし、レビュー本文で「未検証」と報告されていれば問題ない。' +
    '対処はプロンプト側（この形を使わせない）である。'
  );
}

/**
 * 拒否の内訳を GitHub の実行サマリ（$GITHUB_STEP_SUMMARY）へ書く。
 *
 * 背景（issue planning#155）: 拒否の内訳を知るにはジョブログを開く必要があり、**人が読む場所には
 * 出ていなかった**。実運用で、レビューが「実行できなかった検証」を ✅ として報告し、
 * 拒否への言及を本文に 1 行も書かなかった事例が出た。結論（レビュー本文）と、その結論が
 * どこまで裏付けられているか（拒否の内訳）が別々の場所にあると、読み手には見分けが付かない。
 *
 * ステップサマリは PR の Checks 画面から 1 クリックで開ける。スティッキーコメント本体は
 * アクションが所有しており、このステップからは安全に書き換えられないため、まずここへ出す。
 * 失敗しても本処理は続ける（可視化のために検査を落とすのは本末転倒である）。
 */
function writeStepSummary(denials) {
  const { count, byTool, itemized, unfixableCount, unfixableByReason } = denials;
  const file = process.env.GITHUB_STEP_SUMMARY;
  if (!file) return false;
  const lines = [
    '## ⚠️ ツールの権限拒否を検出',
    '',
    `AI の実行中に **${count} 件**の権限拒否が発生した。CI には承認する人間が居ないため、`,
    'これらの作業は**実行されていない**。',
    '',
  ];
  if (itemized) {
    lines.push('| 拒否されたツール | 件数 |', '| --- | --- |');
    for (const [name, n] of [...byTool.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))) {
      lines.push(`| \`${name}\` | ${n} |`);
    }
  } else {
    lines.push('実行ログにツール名が残っていないため内訳を特定できない。');
  }
  // endazon/ai-stock-trading#391 ②: 許可リストで直せない形（B）は失敗判定の対象外だが、**必ず見せる**。
  if (unfixableCount) {
    lines.push(
      '',
      `### うち ${unfixableCount} 件は許可リストでは直せない`,
      '',
      'これらは失敗判定の対象外である（許可リストを完璧にしても消えないため）。',
      'レビュー本文で「未検証」と報告されていれば問題ない。対処はプロンプト側である。',
      ''
    );
    if (unfixableByReason && unfixableByReason.size) {
      lines.push('| 理由 | 件数 |', '| --- | --- |');
      for (const [reason, n] of [...unfixableByReason.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))) {
        lines.push(`| ${reason} | ${n} |`);
      }
    }
  }
  lines.push(
    '',
    '### 読むときの注意',
    '',
    'AI の出力に「実測した」「✅ 確認済み」とある項目が、**実際には実行できていない**可能性がある。',
    '上記のツールを要する主張は、裏付けが取れているか個別に確認すること。',
    '',
    '### 対処',
    '',
    '1. 必要なツールを `claude_args` の `--allowedTools` に加える',
    '2. そのツールを使わせないようプロンプト側で作業手順を狭める',
    '',
    '放置すると、ジョブは success のまま成果物だけが欠けた状態が続く。',
    ''
  );
  try {
    fs.appendFileSync(file, lines.join('\n'));
    return true;
  } catch (e) {
    return false; // 可視化に失敗しても検査自体は続ける
  }
}

/** 検証器自体の自己試験（CI で毎回実行し、検査ロジックの退行を防ぐ）。 */
function selfTest() {
  const denial = 'Claude requested permissions to use Task, but you haven\'t granted it yet.';
  const cases = [
    {
      name: '拒否ゼロの実行は count 0',
      events: [{ type: 'result', subtype: 'success', permission_denials_count: 0 }],
      expect: (r) => r.count === 0,
    },
    {
      name: 'permission_denials 配列があればツール名まで特定する',
      events: [
        {
          type: 'result',
          permission_denials_count: 2,
          permission_denials: [{ tool_name: 'Task' }, { tool_name: 'Task' }],
        },
      ],
      expect: (r) => r.count === 2 && r.itemized && r.byTool.get('Task') === 2,
    },
    {
      name: 'tool_result からツール名を逆引きする（配列が無い版）',
      events: [
        { type: 'assistant', message: { content: [{ type: 'tool_use', id: 't1', name: 'Task' }] } },
        { type: 'user', message: { content: [{ type: 'tool_result', tool_use_id: 't1', is_error: true, content: denial }] } },
        { type: 'result', permission_denials_count: 1 },
      ],
      expect: (r) => r.count === 1 && r.itemized && r.byTool.get('Task') === 1,
    },
    {
      name: '権限拒否でないツールエラーは数えない',
      events: [
        { type: 'assistant', message: { content: [{ type: 'tool_use', id: 't1', name: 'Read' }] } },
        { type: 'user', message: { content: [{ type: 'tool_result', tool_use_id: 't1', is_error: true, content: 'File not found' }] } },
        { type: 'result', permission_denials_count: 0 },
      ],
      expect: (r) => r.count === 0,
    },
    {
      name: 'ツール名を特定できなくても件数は報告する',
      events: [{ type: 'result', permission_denials_count: 17 }],
      expect: (r) => r.count === 17 && r.itemized === false,
    },
    // issue planning#146: 「Bash（4 件）」ではどのコマンドを許可すればよいか決められない。
    {
      name: 'Bash はコマンド名まで報告する（許可リストの粒度に合わせる）',
      events: [
        {
          type: 'result',
          permission_denials: [
            { tool_name: 'Bash', tool_input: { command: 'git diff origin/main...HEAD' } },
            { tool_name: 'Bash', tool_input: { command: 'git diff' } },
            { tool_name: 'Bash', tool_input: { command: 'git status' } },
          ],
        },
      ],
      expect: (r) => r.byTool.get('Bash(git diff)') === 2 && r.byTool.get('Bash(git status)') === 1,
    },
    {
      // issue planning#158: 旧実装は先頭だけを見て `Bash(git log)` と報告し、原因の head を隠していた。
      name: 'パイプの各セグメントを列挙する（原因を隠さない）',
      events: [
        { type: 'result', permission_denials: [{ tool_name: 'Bash', tool_input: { command: 'git log | head -5' } }] },
      ],
      expect: (r) => r.byTool.get('Bash(git log | head)') === 1,
    },
    {
      // 実測の形: 許可済みの git show だけが 6 件報告され、未許可の cmp が出なかった。
      name: '許可済み前段 + 未許可後段（git show | cmp）で後段が候補に載る',
      events: [
        {
          type: 'result',
          permission_denials: [
            { tool_name: 'Bash', tool_input: { command: 'git show origin/main:a.yml | cmp - a.yml' } },
          ],
        },
      ],
      expect: (r) => r.byTool.get('Bash(git show | cmp)') === 1,
    },
    {
      // cd の 2 トークン目はディレクトリ名（引数）なので採らない。
      name: '&& / ; 連結も分割する',
      events: [
        { type: 'result', permission_denials: [{ tool_name: 'Bash', tool_input: { command: 'cd x && npm ci; npm test' } }] },
      ],
      expect: (r) => r.byTool.get('Bash(cd | npm ci | npm test)') === 1,
    },
    {
      // 実測（issue planning#158 の 6 巡目）: `diff <(git show A) <(git show B)` が
      // `Bash(diff (git` という壊れたラベルになっていた。
      name: 'プロセス置換の中のコマンドを露出させる',
      events: [
        {
          type: 'result',
          permission_denials: [
            { tool_name: 'Bash', tool_input: { command: 'diff <(git show a:f) <(git show b:f)' } },
          ],
        },
      ],
      expect: (r) => r.byTool.get('Bash(diff | git show)') === 1,
    },
    {
      name: 'コマンド置換 $(…) の中も露出させる',
      events: [
        { type: 'result', permission_denials: [{ tool_name: 'Bash', tool_input: { command: 'echo $(git log -1)' } }] },
      ],
      expect: (r) => r.byTool.get('Bash(echo | git log)') === 1,
    },
    {
      // 実測: `echo done` の `done` は引数なのにラベルへ出ていた。
      name: 'サブコマンドを持たないコマンドの引数は採らない',
      events: [
        {
          type: 'result',
          permission_denials: [
            { tool_name: 'Bash', tool_input: { command: 'git -C planning show x | echo done' } },
          ],
        },
      ],
      expect: (r) => r.byTool.get('Bash(git -C planning show | echo)') === 1,
    },
    {
      name: '環境変数の前置き形は割り当てとコマンド名を並べる',
      events: [
        { type: 'result', permission_denials: [{ tool_name: 'Bash', tool_input: { command: 'FOO=1 node x.js' } }] },
      ],
      expect: (r) => r.byTool.get('Bash(FOO=1 node)') === 1,
    },
    {
      // `for f in …; do …; done` は先頭トークンが for になり許可リストで表現できない。
      name: 'シェルのループは予約語を飛ばして中身のコマンドを出す',
      events: [
        {
          type: 'result',
          permission_denials: [
            { tool_name: 'Bash', tool_input: { command: 'for f in a b; do cmp $f x; done' } },
          ],
        },
      ],
      expect: (r) => /cmp/.test([...r.byTool.keys()][0]),
    },
    {
      name: 'リダイレクト先のファイル名はコマンドとして数えない',
      events: [
        { type: 'result', permission_denials: [{ tool_name: 'Bash', tool_input: { command: 'node x.js > out.txt' } }] },
      ],
      expect: (r) => r.byTool.get('Bash(node x.js)') === 1,
    },
    {
      // issue planning#160: `git show X > /tmp/a` が `Bash(git show)`（許可済み）として報告された。
      name: 'リダイレクトを含むなら注記を出す（許可済みに見えて実は書き込みが原因）',
      events: [
        { type: 'result', permission_denials: [{ tool_name: 'Bash', tool_input: { command: 'git show a:b > /tmp/x' } }] },
      ],
      expect: (r) => r.redirect === true && /リダイレクト/.test(formatDenials(r)),
    },
    {
      name: 'リダイレクトが無ければ注記を出さない',
      events: [
        { type: 'result', permission_denials: [{ tool_name: 'Bash', tool_input: { command: 'git show a:b' } }] },
      ],
      expect: (r) => !r.redirect && !/リダイレクト/.test(formatDenials(r)),
    },
    {
      // 実測: `2>&1` が `&` で分割され、`1` が独立したコマンドとして報告された。
      name: '2>&1 は分割せず、存在しないコマンド `1` を作らない',
      events: [
        { type: 'result', permission_denials: [{ tool_name: 'Bash', tool_input: { command: 'ls -la 2>&1 | head -5' } }] },
      ],
      expect: (r) => r.byTool.get('Bash(ls | head)') === 1,
    },
    {
      // fd 複製はファイルへの書き込みではないので、書き込み注記の対象にしない。
      name: '2>&1 だけならリダイレクト注記を出さない',
      events: [
        { type: 'result', permission_denials: [{ tool_name: 'Bash', tool_input: { command: 'node x.js 2>&1' } }] },
      ],
      expect: (r) => r.redirect !== true,
    },
    {
      // 実測: `echo "exit:$?"` の引用符付き引数がラベルに出ていた。
      name: '引用符付き引数・変数展開は 2 トークン目に採らない',
      events: [
        {
          type: 'result',
          permission_denials: [
            { tool_name: 'Bash', tool_input: { command: 'git show a | diff - b | head -20 | echo "exit:$?"' } },
          ],
        },
      ],
      expect: (r) => r.byTool.get('Bash(git show | diff | head | echo)') === 1,
    },
    {
      // issue planning#160: 2 トークン固定だと `Bash(git -C)` になりサブコマンドが消える。
      name: 'git -C <dir> <sub> は 4 トークンまで採る（許可リストと同じ粒度）',
      events: [
        {
          type: 'result',
          permission_denials: [
            { tool_name: 'Bash', tool_input: { command: 'git -C planning rev-parse HEAD' } },
          ],
        },
      ],
      expect: (r) => r.byTool.get('Bash(git -C planning rev-parse)') === 1,
    },
    {
      name: 'セグメントが多い場合は省略記号を付ける（ラベルが暴れない）',
      events: [
        {
          type: 'result',
          permission_denials: [
            { tool_name: 'Bash', tool_input: { command: 'a | b | c | d | e | f' } },
          ],
        },
      ],
      expect: (r) => [...r.byTool.keys()][0].endsWith('| …)'),
    },
    {
      name: 'パイプがあれば「後段を疑え」の注記を出す',
      events: [
        { type: 'result', permission_denials: [{ tool_name: 'Bash', tool_input: { command: 'git show a | cmp - b' } }] },
      ],
      expect: (r) => /後段のコマンドかもしれない/.test(formatDenials(r)),
    },
    {
      name: 'パイプが無ければ注記を出さない（余計な誘導をしない）',
      events: [
        { type: 'result', permission_denials: [{ tool_name: 'Bash', tool_input: { command: 'git diff' } }] },
      ],
      expect: (r) => !/後段のコマンドかもしれない/.test(formatDenials(r)),
    },
    {
      name: 'command が無い Bash はツール名のみ（落ちない）',
      events: [{ type: 'result', permission_denials: [{ tool_name: 'Bash' }] }],
      expect: (r) => r.byTool.get('Bash') === 1,
    },
    {
      name: 'tool_result 経由でもコマンド名まで出る',
      events: [
        { type: 'assistant', message: { content: [{ type: 'tool_use', id: 'b1', name: 'Bash', input: { command: 'git show HEAD' } }] } },
        { type: 'user', message: { content: [{ type: 'tool_result', tool_use_id: 'b1', is_error: true, content: denial }] } },
        { type: 'result', permission_denials_count: 1 },
      ],
      expect: (r) => r.byTool.get('Bash(git show)') === 1,
    },
    {
      name: 'tool_result の content が配列でも読める',
      events: [
        { type: 'assistant', message: { content: [{ type: 'tool_use', id: 't1', name: 'Bash' }] } },
        {
          type: 'user',
          message: { content: [{ type: 'tool_result', tool_use_id: 't1', is_error: true, content: [{ type: 'text', text: denial }] }] },
        },
        { type: 'result', permission_denials_count: 1 },
      ],
      expect: (r) => r.byTool.get('Bash') === 1,
    },
  ];

  const parseCases = [
    ['JSON 配列を読める', '[{"type":"result","permission_denials_count":3}]', (e) => e.length === 1],
    ['NDJSON を読める', '{"type":"result","permission_denials_count":3}\n{"type":"x"}', (e) => e.length === 2],
    ['壊れた行があっても読めた分で判断する', '{"type":"result","permission_denials_count":3}\n{壊れ', (e) => e.length === 1],
    ['空ファイルは空配列', '   ', (e) => e.length === 0],
  ];

  let failed = 0;
  for (const [label, text, expect] of parseCases) {
    if (expect(parseEvents(text))) process.stdout.write(`  ok  ${label}\n`);
    else {
      failed++;
      process.stderr.write(`  NG  ${label}\n`);
    }
  }
  for (const c of cases) {
    const r = collectDenials(c.events);
    if (c.expect(r)) process.stdout.write(`  ok  ${c.name}\n`);
    else {
      failed++;
      process.stderr.write(`  NG  ${c.name}\n      got=${JSON.stringify({ ...r, byTool: [...r.byTool] })}\n`);
    }
  }
  // issue planning#155: 内訳が「人が読む場所」へ出ること。
  {
    const osq = require('os');
    const pathq = require('path');
    const tmp = pathq.join(fs.mkdtempSync(pathq.join(osq.tmpdir(), 'pdsum-')), 'summary.md');
    const prev = process.env.GITHUB_STEP_SUMMARY;
    process.env.GITHUB_STEP_SUMMARY = tmp;
    const wrote = writeStepSummary(collectDenials([
      { type: 'result', permission_denials: [{ tool_name: 'Bash', tool_input: { command: 'git ls-tree HEAD' } }] },
    ]));
    const body = fs.existsSync(tmp) ? fs.readFileSync(tmp, 'utf8') : '';
    if (prev === undefined) delete process.env.GITHUB_STEP_SUMMARY;
    else process.env.GITHUB_STEP_SUMMARY = prev;

    const okSummary = wrote && body.includes('Bash(git ls-tree)') && body.includes('実測');
    if (okSummary) process.stdout.write('  ok  拒否の内訳を実行サマリへ書く\n');
    else {
      failed++;
      process.stderr.write(`  NG  拒否の内訳を実行サマリへ書く\n      body=${JSON.stringify(body.slice(0, 200))}\n`);
    }

    // 環境変数が無い環境（ローカル実行）では何もしない＝落ちない。
    const prev2 = process.env.GITHUB_STEP_SUMMARY;
    delete process.env.GITHUB_STEP_SUMMARY;
    const noop = writeStepSummary({ count: 1, byTool: new Map(), itemized: false }) === false;
    if (prev2 !== undefined) process.env.GITHUB_STEP_SUMMARY = prev2;
    if (noop) process.stdout.write('  ok  GITHUB_STEP_SUMMARY 未設定では何もしない\n');
    else {
      failed++;
      process.stderr.write('  NG  GITHUB_STEP_SUMMARY 未設定では何もしない\n');
    }
  }

  // --- 段階ポリシー（isCritical）。実運用 6 巡の実測値で固定する ---
  {
    const policyCases = [
      ['元障害 17 件 / 21 ターンは失敗', { count: 17, numTurns: 21 }, 4, true],
      ['検証偽装の 12 件は失敗', { count: 12, numTurns: 30 }, 4, true],
      ['許容値を超える 5 件は失敗', { count: 5, numTurns: 40 }, 4, true],
      ['探索的な 2 件 / 43 ターンは失敗させない', { count: 2, numTurns: 43 }, 4, false],
      ['4 件 / 16 ターンは失敗させない（planning#146 の形）', { count: 4, numTurns: 16 }, 4, false],
      ['件数が少なくても実行の半分が拒否なら失敗', { count: 3, numTurns: 6 }, 4, true],
      ['ターン数が取れない場合は件数だけで判定', { count: 2, numTurns: null }, 4, false],
      ['STRICT（許容 0）では 1 件でも失敗', { count: 1, numTurns: 43 }, 0, true],
    ];
    for (const [label, denials, tol, expected] of policyCases) {
      if (isCritical(denials, tol) === expected) process.stdout.write(`  ok  ${label}\n`);
      else {
        failed++;
        process.stderr.write(`  NG  ${label}\n`);
      }
    }
  }

  // --- endazon/ai-stock-trading#391 ②: A（許可リストで直せる）/ B（直せない）の分類と、それに基づく失敗判定 ---
  {
    const mk = (cmds) => ({
      type: 'result',
      num_turns: 40,
      permission_denials: cmds.map((c) =>
        typeof c === 'string' ? { tool_name: 'Bash', tool_input: { command: c } } : c
      ),
    });
    // 正の確認: B と分類されるべき形。
    const unfixableCases = [
      ['環境変数の前置き', 'STRICT_X=1 node scripts/a.js'],
      ['リダイレクト', 'git show a:b > /tmp/x'],
      ['/dev/null へのリダイレクトも B', 'npm ci > /dev/null'],
      ['プロセス置換', 'diff <(git show a:f) <(git show b:f)'],
      ['コマンド置換', 'echo $(git log -1)'],
      ['シェルのループ', 'for f in a b; do cmp $f x; done'],
      ['cd によるディレクトリ移動', 'cd planning && git log -1'],
      ['git -C の絶対パス', 'git -C /home/runner/work/x/x log -1'],
      ['鎖の後段が B なら全体が B', 'git diff | cd x'],
      // 実測（PR endazon/ai-stock-trading#395・5 件中 3 件）: 引用符内の `|` が分割され、正規表現の断片が
      // 未許可コマンドとして判定される。許可リストには書けないので B。
      ['引用符内の | （二重引用符）', 'git -C planning show a:b | grep -E "決定|決定14"'],
      ["引用符内の | （単一引用符）", "ls docs | grep -E 'check-banned|check-coverage'"],
    ];
    for (const [label, cmd] of unfixableCases) {
      const r = collectDenials([mk([cmd])]);
      if (r.unfixableCount === 1 && r.fixableCount === 0) process.stdout.write(`  ok  B と分類: ${label}\n`);
      else {
        failed++;
        process.stderr.write(`  NG  B と分類: ${label}\n      got=${JSON.stringify({ f: r.fixableCount, u: r.unfixableCount })}\n`);
      }
    }
    // 否定形（同数以上）: A と分類されるべき形。**B と誤分類すると本物の許可漏れが検出されなくなる。**
    const fixableCases = [
      ['npm ci', 'npm ci'],
      ['dotnet ef', 'dotnet ef migrations has-pending-model-changes'],
      ['git ls-files', 'git ls-files'],
      ['git -C の相対パス（許可リストで表現できる）', 'git -C planning log -1'],
      ['パイプだけなら A', 'git show a:b | cmp - b'],
      ['2>&1 は B ではない（ファイル書き込みでない）', 'node x.js 2>&1'],
      ['npx playwright', 'npx playwright test'],
      ['引数に = を含むが前置きではない', 'npm run test -- --reporter=dot'],
      ['MCP ツールは常に A', { tool_name: 'mcp__github__search_issues' }],
      // 引用符があっても `|` を含まなければ A（許可リストで直せる）。
      ['引用符付き引数でも | が無ければ A', 'grep -n "決定 14" docs/a.md'],
      ['エスケープされた引用符で誤判定しない', 'echo "say \\"hi\\""'],
      ['git -C planning rev-parse は A（許可リストに足せる）', 'git -C planning rev-parse HEAD'],
    ];
    for (const [label, cmd] of fixableCases) {
      const r = collectDenials([mk([cmd])]);
      if (r.fixableCount === 1 && r.unfixableCount === 0) process.stdout.write(`  ok  A と分類: ${label}\n`);
      else {
        failed++;
        process.stderr.write(`  NG  A と分類: ${label}\n      got=${JSON.stringify({ f: r.fixableCount, u: r.unfixableCount })}\n`);
      }
    }
    // 失敗判定が A のみで行われること。
    const verdictCases = [
      ['B のみ 10 件は失敗させない', Array(10).fill('cd x && ls'), false],
      ['A が 5 件なら失敗する', Array(5).fill('npm ci'), true],
      ['A 3 件 + B 10 件は失敗させない（A が許容内）', [...Array(3).fill('npm ci'), ...Array(10).fill('cd x')], false],
      ['A 4 件 + B 10 件も失敗させない（境界）', [...Array(4).fill('npm ci'), ...Array(10).fill('cd x')], false],
      ['A 5 件 + B 0 件は失敗する', Array(5).fill('dotnet ef list'), true],
    ];
    for (const [label, cmds, expected] of verdictCases) {
      const r = collectDenials([mk(cmds)]);
      if (isCritical(r, 4) === expected) process.stdout.write(`  ok  ${label}\n`);
      else {
        failed++;
        process.stderr.write(`  NG  ${label}\n      got=${JSON.stringify({ f: r.fixableCount, u: r.unfixableCount })}\n`);
      }
    }
    // 実測の再現 2（PR endazon/ai-stock-trading#395 の run 31012341385・5 件）。rev-parse を許可すれば A は 1 件になる。
    {
      const r = collectDenials([
        mk([
          'git -C planning show a:b | grep -E "決定|決定14"',
          'ls docs | git -C planning ls-tree HEAD | grep -E "ADR-0016|ADR-0019"',
          'ls scripts | grep -E "check-banned|check-coverage"',
          'ls scripts | grep foo',
          'git -C planning rev-parse HEAD',
        ]),
      ]);
      const ok = r.unfixableCount === 3 && r.fixableCount === 2 && isCritical(r, 4) === false;
      if (ok) process.stdout.write('  ok  PR endazon/ai-stock-trading#395 の 5 件を再現し、分類後は失敗しない\n');
      else {
        failed++;
        process.stderr.write(`  NG  PR endazon/ai-stock-trading#395 の再現\n      got=${JSON.stringify({ f: r.fixableCount, u: r.unfixableCount })}\n`);
      }
    }
    // 実測の再現（run 30998236984・5 件）。すべて A なので従来どおり失敗する。
    {
      const r = collectDenials([
        mk([
          'git -C /home/runner/work/ai-stock-trading/ai-stock-trading log',
          'git -C /home/runner/work/ai-stock-trading/ai-stock-trading ls-files',
          { tool_name: 'mcp__github__get_issue_comments' },
          { tool_name: 'mcp__github__get_pull_request_reviews' },
          { tool_name: 'mcp__github__search_issues' },
        ]),
      ]);
      // git -C 絶対パス 2 件は B、MCP 3 件は A。A は 3 件なので**許可リスト是正後は失敗しない**。
      const ok = r.unfixableCount === 2 && r.fixableCount === 3 && isCritical(r, 4) === false;
      if (ok) process.stdout.write('  ok  実測 run の 5 件を再現し、分類後は失敗しない\n');
      else {
        failed++;
        process.stderr.write(`  NG  実測 run の再現\n      got=${JSON.stringify({ f: r.fixableCount, u: r.unfixableCount })}\n`);
      }
    }
    // B の内訳が報告に出ること（失敗させないからこそ見せる）。
    {
      const r = collectDenials([mk(['cd x && ls'])]);
      if (/許可リストでは直せない/.test(formatDenials(r))) process.stdout.write('  ok  B の存在が報告に出る\n');
      else {
        failed++;
        process.stderr.write('  NG  B の存在が報告に出る\n');
      }
    }
  }

  // 実運用で起きた形（17 件・内訳不明）がメッセージに載ることを確かめる。
  const msg = formatDenials(collectDenials([{ type: 'result', permission_denials_count: 17 }]));
  if (msg.includes('17 件')) process.stdout.write('  ok  件数がメッセージに載る\n');
  else {
    failed++;
    process.stderr.write('  NG  件数がメッセージに載る\n');
  }

  if (failed) {
    process.stderr.write(`\n✗ 検証器の自己試験が ${failed} 件失敗した\n`);
    return 1;
  }
  // +42 は、上のループに含まれない個別検査の合計。内訳:
  //   ポリシー 8 / 実行サマリ 2 / 件数メッセージ 1
  //   endazon/ai-stock-trading#391 ② の分類: B 判定 11 / A 判定 12 / 失敗判定 5 / 実測 run の再現 2 / B の可視化 1
  process.stdout.write(`✓ 検証器の自己試験 ${cases.length + parseCases.length + 42} 件すべて合格\n`);
  return 0;
}

function main(argv) {
  const args = argv.slice(2);
  if (args.includes('--self-test')) process.exit(selfTest());

  const file = args.find((a) => !a.startsWith('--'));
  if (!file) {
    // 検査が成立しない状態を黙って通さない（この検査器が塞いでいる不具合と同じ形になる）。
    warn(
      '権限拒否チェック: 実行ログのパスが渡されていないため検査していない' +
        '（claude-code-action の outputs.execution_file を渡すこと）'
    );
    process.exit(0);
  }

  let text;
  try {
    text = fs.readFileSync(file, 'utf8');
  } catch (e) {
    warn(
      `権限拒否チェック: 実行ログを読めないため検査していない: ${file}（${e.code || e.message}）。` +
        'claude-code-action の outputs.execution_file が変わった可能性がある'
    );
    process.exit(0);
  }

  const events = parseEvents(text);
  if (events.length === 0) {
    warn(`権限拒否チェック: 実行ログを解析できないため検査していない: ${file}`);
    process.exit(0);
  }

  const denials = collectDenials(events);
  if (denials.count === 0) {
    process.stdout.write('✓ ツールの権限拒否は発生していない\n');
    process.exit(0);
  }

  // 人が読む場所（実行サマリ）へも出す。ジョブログを開かないと内訳が判らない状態を残さない。
  writeStepSummary(denials);

  const message = formatDenials(denials);
  if (process.env.ALLOW_PERMISSION_DENIALS === '1') {
    warn(`${message}（ALLOW_PERMISSION_DENIALS=1 のため失敗させない）`);
    process.exit(0);
  }

  // 段階ポリシー（ファイル冒頭のコメント参照）。可視化（アノテーション・実行サマリ）は
  // 件数に関わらず済んでいる——ここで決めるのは exit code だけである。
  const strict = process.env.STRICT_PERMISSION_DENIALS === '1';
  const tolRaw = parseInt(process.env.PERMISSION_DENIALS_TOLERANCE, 10);
  const tolerance = strict ? 0 : Number.isFinite(tolRaw) && tolRaw >= 0 ? tolRaw : 4;

  if (!isCritical(denials, tolerance)) {
    warn(
      `${message}（判定: 許可リストで直せる拒否 ${denials.fixableCount} 件 / 全 ${denials.count} 件 / ` +
        `${denials.numTurns ?? '?'} ターン。許容値 ${tolerance} 以下かつ実行の大半は成立しているため、` +
        'ジョブは失敗させない。拒否 1 件でも失敗させるには STRICT_PERMISSION_DENIALS=1 を設定する）'
    );
    process.exit(0);
  }
  emit('error', message, { stream: process.stderr, prefix: '  error  ' });
  process.exit(1);
}

if (require.main === module) {
  main(process.argv);
}

module.exports = {
  parseEvents,
  collectDenials,
  formatDenials,
  looksLikeDenial,
  labelOf,
  unfixableReason,
  isCritical,
  writeStepSummary,
  selfTest,
};
