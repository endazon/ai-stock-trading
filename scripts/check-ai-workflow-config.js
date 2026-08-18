#!/usr/bin/env node
'use strict';
/*
 * check-ai-workflow-config.js
 * Claude 系ワークフロー（claude-coding / claude-code-review）のツール許可設定を機械検査する。
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 適用範囲: claude-code-action（既定名 claude-coding / claude-code-review）専用である。
 *   他エンジンのワークフロー（<engine>-coding.yml 等。docs/ai-orchestration.md）は対象外であり、
 *   **対象外は「検査済み」を意味しない**（非 Claude エンジンは本検査の外で動く）。
 *
 * 背景（実運用で発生した障害）:
 *   `claude_args` は**空白区切りで argv へトークン化**される。そのため
 *   `--allowedTools Bash(dotnet test:*)` と 1 ツール 1 行で書くと、値が
 *   `Bash(dotnet` と `test:*)` に割れて**その指定が無効**になる。実装用で 32 件中 14 件
 *   （dotnet / git 系すべて）が無効化され、レビュー用は実質 Read のみが有効という状態が
 *   2 週間気付かれずに続いた。ジョブは success で終わるため CI では顕在化しない。
 *   さらにレビュー用は、直前で SDK を用意しながら実行系ツールを 1 つも許可しておらず、
 *   AI レビューが毎回「承認待ちでブロックされ検証できませんでした」と報告していた
 *   （CI には承認する人間がいないため、この待ちは必ず失敗に終わる）。
 *
 * 検査内容:
 *   [ERROR] claude_args ブロック内のコメント行（引数として解釈され起動が壊れる）
 *   [ERROR] 引用符で囲まれていない --allowedTools 値に空白が含まれる（分割されて無効になる）
 *   [ERROR] setup-* でツールチェーンを用意しているのに、対応する実行ツールを許可していない
 *   [ERROR] 実装用とレビュー用でスタック別の実行ツールが食い違う（部分的な複製漏れ）
 *   [ERROR] スタック別以外の Bash 指定（読み取り専用の汎用コマンド・git -C 変種等）が
 *           実装用とレビュー用で食い違う（意図的な非対称は除外リストで宣言。issue planning#163）
 *   [WARN ] .claude/settings.json の allow とワークフローのツール集合の乖離（情報提供のみ）
 *   [WARN ] 検査そのものが成立していない（claude_args を解析できない / 既定名で引き当てられない）
 *
 * 警告の出し方:
 *   GitHub Actions 上では workflow コマンド（`::warning::`）で出し、PR の Checks 画面と
 *   実行サマリのアノテーションに載せる。素の stdout 行は**緑ジョブのログに埋もれて読まれない**
 *   （issue planning#122 の 3 系統乖離は、修正までのあいだ CI で毎回 warn が出ていたのに、
 *   気付いたのはローカル実行と AI レビューの実走であり CI ログ経由ではなかった）。
 *   ローカル実行時の見た目は従来どおり。実装は scripts/lib/ci-annotate.js。
 *
 * 使い方:
 *   node scripts/check-ai-workflow-config.js [--self-test] [--dir .github/workflows]
 *
 * 環境変数:
 *   STRICT_AI_WORKFLOW_CONFIG=1  警告を失敗として扱う（既定は fail-open。opt-in）
 */
const fs = require('fs');
const path = require('path');
const { warn } = require('./lib/ci-annotate.js');

const REPO_ROOT = path.join(__dirname, '..');
const DEFAULT_DIR = path.join(REPO_ROOT, '.github', 'workflows');
const SETTINGS_PATH = path.join(REPO_ROOT, '.claude', 'settings.json');

// setup-* アクションと、それが用意したツールチェーンを使うために必要な Bash 許可の候補。
// いずれか 1 つでも許可されていれば「実行できる」とみなす。
const TOOLCHAINS = [
  { action: 'setup-dotnet', commands: ['dotnet'] },
  { action: 'setup-node', commands: ['node', 'npm', 'npx', 'pnpm', 'yarn'] },
  { action: 'setup-python', commands: ['python', 'python3', 'pytest', 'pip'] },
  { action: 'setup-go', commands: ['go'] },
  // gradle ラッパは `Bash(./gradlew build:*)` と書かれる。下の照合はコマンド名を
  // 末尾（`gradlew`）へ正規化するため、`./gradlew` だけでは永久に一致しない。両方載せる。
  { action: 'setup-java', commands: ['mvn', 'gradle', './gradlew', 'gradlew'] },
];

// issue planning#163: スタック別以外の Bash 指定（読み取り専用の汎用コマンド・`git -C <submodule>` 変種
// 等）の片落ちを ERROR で検出するための**意図的な非対称の宣言**。toolchainDrift は TOOLCHAINS
// （スタック別の実行ツール）しか比較しないため、planning#155 の cat/head/tail、planning#160 の cmp/diff、
// planning#163 の grep/sort と**同じ型の欠落が 3 度**すり抜けた。「手で揃えること」というコメントでは
// 守れなかったので、以後は下の除外リストに無い Bash 指定の差分をすべて ERROR にする。
// 除外リストは Bash(...) の内側（末尾の `:*` を除く）と完全一致で照合する。
// 実装用にだけあるのが正しいコマンド（書き込み・ファイル操作系。レビューは読むだけ）:
const CODING_ONLY_BASH = [
  'git add',
  'git commit',
  'git push',
  'git switch',
  'git checkout',
  'git branch',
  'find',
  'mkdir',
];
// レビュー用にだけあるのが正しいコマンド（PR / CI の読解系。実装用は GitHub MCP で代替する）:
const REVIEW_ONLY_BASH = ['gh issue view', 'gh pr view', 'gh run list'];

/** `claude_args: |` のブロックスカラー本文を配列で返す（複数あれば複数要素）。 */
function extractClaudeArgsBlocks(text) {
  const lines = text.split('\n');
  const blocks = [];
  for (let i = 0; i < lines.length; i++) {
    const m = lines[i].match(/^(\s*)claude_args:\s*[|>]/);
    if (!m) continue;
    const baseIndent = m[1].length;
    const body = [];
    for (let j = i + 1; j < lines.length; j++) {
      const line = lines[j];
      if (line.trim() === '') { body.push(line); continue; }
      const indent = line.match(/^\s*/)[0].length;
      if (indent <= baseIndent) break;
      body.push(line);
    }
    blocks.push({ startLine: i + 1, body });
  }
  return blocks;
}

/** `--allowedTools <値>` の値を取り出す。引用符は外し、カンマ区切りは展開する。 */
function parseAllowedTools(body) {
  const entries = [];
  for (const raw of body) {
    const line = raw.trim();
    const idx = line.indexOf('--allowedTools');
    if (idx === -1) continue;
    const value = line.slice(idx + '--allowedTools'.length).trim();
    const quoted = /^"(.*)"$/.test(value) || /^'(.*)'$/.test(value);
    const inner = quoted ? value.slice(1, -1) : value;
    entries.push({ raw: value, quoted, tools: inner.split(',').map((t) => t.trim()).filter(Boolean) });
  }
  return entries;
}

/**
 * claude_args ブロックの `--<flag> <値>` をすべて取り出す。
 *
 * 記法の落とし穴（空白で割れて無効になる）は `--allowedTools` に固有ではない。
 * issue planning#149 で `--append-system-prompt "…"` を導入したように、値に空白を含む指定は
 * 今後も増える。フラグ名で限定していると、次に増えたものが同じ形で黙って壊れる
 * ——このキットが繰り返し潰してきた型そのものになる。よってフラグ横断で検査する。
 *
 * `${{ ... }}` は実行時に単一トークンへ展開されるため、空白とみなさない
 * （`--model ${{ steps.model.outputs.model }}` を誤検知しないため。これを入れないと
 * 既存の全ワークフローが一斉に ERROR になる）。
 */
function parseFlagArgs(body) {
  const entries = [];
  for (const raw of body) {
    const line = raw.trim();
    const m = line.match(/^(--[A-Za-z][A-Za-z0-9-]*)\s+(.+)$/);
    if (!m) continue;
    const flag = m[1];
    const value = m[2].trim();
    if (!value) continue;
    const quoted = /^"(.*)"$/.test(value) || /^'(.*)'$/.test(value);
    const probe = value.replace(/\$\{\{[^}]*\}\}/g, 'X');
    entries.push({ flag, raw: value, quoted, hasSpace: /\s/.test(probe) });
  }
  return entries;
}

/** ツール指定の集合から Bash(<コマンド> ...) のコマンド名を抜き出す。 */
function bashCommandsOf(tools) {
  const cmds = new Set();
  for (const t of tools) {
    const m = t.match(/^Bash\(([^\s:)]+)/);
    if (m) cmds.add(m[1].replace(/^.*\//, '')); // フルパス指定は末尾のみ見る
  }
  return cmds;
}

/** 既定名（claude-coding / claude-code-review）でファイルを引き当てる。 */
function pickCanonical(files, keyword) {
  return files.find((f) => path.basename(f.file).includes(keyword));
}

/**
 * 検査が**成立しなかった**ことを警告する（失敗はさせない）。
 *
 * キットが繰り返し潰してきた「ジョブは成功するのに実は効いていない」型を 2 つ塞ぐ。
 * いずれも ERROR ではなく **warn**（exit 0 のまま）にしている。理由は 2 つある。
 *   - ファイル名・ワークフロー構成の自由度を各リポジトリに残すため。
 *   - 1 は claude-code-action の**入力名が将来変わった**場合にも成立してしまう。ERROR に
 *     すると、新しい版へ追随した全リポジトリの CI が検証器の更新まで一斉に落ちる。
 *     キットが他所（PLAN_PROJECT・check-doc-links）で採っている fail-open と揃える。
 * その代わり「効いていない」ことは必ず出力に出す。warn を読まない運用だと 1 は
 * 素通りするため、CI ログの warn は無視しないこと。
 *
 * 1. **既定名のファイルがあるのに claude_args を解析できない**（issue planning#134）。
 *    checkWorkflow は claude_args ブロックを 1 つも取れないと applicable: false を返し、
 *    呼び出し側が集計から丸ごと除外する。結果、記法検査・SDK 整合検査・ドリフト検査の
 *    **すべてが実行されない**のに green で終わる。手がかりは「2 件を検査」→「1 件を検査」
 *    という件数の変化だけで、これは誰も見ていない。この状態からレビュー側の実行ツールが
 *    全部消えても検出されず、検査器が入ったまま元の障害へ戻れてしまう。
 *    起こり方は現実的である（入力名の変更・YAML のインデント崩れ・`with:` の付け替えミス）。
 * 2. **既定名で 2 ファイルを引き当てられない**（issue planning#130 副次指摘）。2 つを 1 ファイルへ
 *    統合した構成や別名を採ったリポジトリでは、ドリフト検査だけが黙って無効になる。
 *
 * allFiles はディスク上の全ワークフローのパス（listWorkflowFiles の結果）。
 * 省略時は 1 の検査を行わない（自己試験など、ディスクを持たない呼び出し向け）。
 */
function driftScopeWarnings(files, allFiles = []) {
  const unparsed = [];
  for (const kw of ['claude-coding', 'claude-code-review']) {
    const onDisk = allFiles.find((p) => path.basename(p).includes(kw));
    if (onDisk && !pickCanonical(files, kw)) unparsed.push(path.basename(onDisk));
  }
  if (unparsed.length) {
    return [
      `${unparsed.join(', ')} は存在するが claude_args を解析できず、検査対象から外れている` +
        '（記法検査・SDK 整合・ドリフト検査がいずれも実行されていない）。' +
        'キー名（claude_args）とインデントを確認すること',
    ];
  }
  if (files.length < 2) return []; // 実装用のみ・レビュー用のみの構成は正常
  if (pickCanonical(files, 'claude-coding') && pickCanonical(files, 'claude-code-review')) return [];
  return [
    'claude_args を持つワークフローが ' +
      `${files.length} 件あるが、既定名（claude-coding / claude-code-review）で 2 ファイルを` +
      `引き当てられないため、実装用とレビュー用のドリフト検査は**実行されていない**` +
      `（対象: ${files.map((f) => path.basename(f.file)).join(', ')}）。` +
      '既定名へ寄せるか、この検査に頼らない運用であることを承知して使うこと',
  ];
}

/**
 * スタック別実行コマンドに該当するツール指定だけを抜き出す。
 * 全ツールの単純比較は誤検知する（実装側の Edit / Write / 書き込み系 git は、
 * レビュー側に**無いのが正しい**設計）ため、TOOLCHAINS で対象を絞る。
 *
 * `requireUses` は「実際に `uses:` されている setup-* に対応するものだけ」へさらに絞る。
 *   - 単一ファイルの検査（「setup-* があるのに実行ツールが無い」）では **true が必要**。
 *     用意していないツールチェーンを要求してしまうため。
 *   - 2 ファイル間の突き合わせでは **false にする**。理由は toolchainDrift を参照。
 */
function toolchainCommandsOf(text, tools, { requireUses = true } = {}) {
  const used = requireUses
    ? TOOLCHAINS.filter((tc) =>
        new RegExp(`^\\s*-?\\s*uses:\\s*\\S*${tc.action}`, 'm').test(text)
      )
    : TOOLCHAINS;
  // 比較は**ツール指定そのもの**（`Bash(dotnet build:*)`）の粒度で行う。
  // bashCommandsOf はコマンド名（`dotnet`）へ畳み込むため、build と test の差が消えて
  // 部分的なドリフトを検出できない。
  return new Set(
    tools.filter((t) => {
      const m = t.match(/^Bash\(([^\s:)]+)/);
      if (!m) return false;
      const cmd = m[1].replace(/^.*\//, '');
      return used.some((tc) => tc.commands.includes(cmd));
    })
  );
}

/**
 * 実装用とレビュー用のスタック別実行ツールの差分を検出する。
 *
 * 単一ファイルの検査（「setup-* があるのに実行ツールを 1 つも許可していない」）は
 * **全滅**の形しか捉えられない。片方にだけ一部のコマンドが無い**部分的なドリフト**は
 * すり抜け、レビューは「一部のコマンドだけ承認待ちでブロックされる」という中途半端な
 * 劣化を起こす。レビュー本文には検証結果が載るため、全滅より気付きにくい。
 * files は [{ file, text, tools }]。
 *
 * 【重要】比較の基準は TOOLCHAINS 全体であり、`uses: setup-*` の有無で絞らない
 * （`requireUses: false`）。各ファイル自身の setup-* で絞ると 2 方向に壊れた:
 *   - **偽陰性**: ランナーにプリインストール済みのランタイムは setup-* を書かないため
 *     比較対象から外れる。`Bash(node:*)` の複製漏れが検出できなかった。これは
 *     キットの検査器群（scripts.test.js / check-*.js …）をレビューが実走する唯一の口で、
 *     落ちると検証が全滅する。`dotnet test` 1 つより影響が広い（issue planning#131）。
 *   - **偽陽性**: 2 ファイルの setup-* 構成が非対称だと、`--allowedTools` が完全に同一でも
 *     差分として報告された。「両ファイルを同じ内容に保つ」という規約を守っている利用者ほど
 *     踏む形であり、しかも ERROR（exit 1）だった（issue planning#130）。
 * 2 ファイル間では、片方にあって片方に無ければ setup-* の有無に関わらずドリフトである。
 */
function toolchainDrift(files) {
  const errors = [];
  const coding = pickCanonical(files, 'claude-coding');
  const review = pickCanonical(files, 'claude-code-review');
  if (!coding || !review) return errors; // 片方しか無い構成は対象外

  const opts = { requireUses: false };
  const a = toolchainCommandsOf(coding.text, coding.tools, opts);
  const b = toolchainCommandsOf(review.text, review.tools, opts);
  const missingInReview = [...a].filter((c) => !b.has(c));
  const missingInCoding = [...b].filter((c) => !a.has(c));

  if (missingInReview.length) {
    errors.push(
      `${path.basename(review.file)}: 実装用にあるスタック別の実行ツールが欠けている: ` +
        `${missingInReview.join(', ')}（レビューがその検証を実行できず、承認待ちでブロックされる）`
    );
  }
  if (missingInCoding.length) {
    errors.push(
      `${path.basename(coding.file)}: レビュー用にあるスタック別の実行ツールが欠けている: ` +
        `${missingInCoding.join(', ')}（両ファイルは同じ内容に保つ）`
    );
  }
  return errors;
}

/** Bash(<内側>) の内側を返す（末尾の `:*` は除く）。Bash 指定でなければ null。 */
function bashInnerOf(tool) {
  const m = tool.match(/^Bash\((.*?)(?::\*)?\)$/);
  return m ? m[1].trim() : null;
}

/**
 * スタック別以外の Bash 指定（読み取り専用の汎用コマンド・`git -C <submodule>` 変種等）の
 * 実装用⇔レビュー用ドリフトを検出する（issue planning#163）。
 *
 * toolchainDrift が塞ぐのはスタック別の実行ツールだけで、`grep` / `sort` / `git -C … log` の
 * 片落ちは検出されない。この型の欠落は planning#155（cat/head/tail）→ planning#160（cmp/diff）→
 * planning#163（grep/sort）と 3 度繰り返された。パイプは各コマンドが個別判定されるため、後段の
 * 1 コマンドの欠落で鎖全体が実行されず、しかも**その回のレビューが何回それを使おうと
 * したかで拒否件数が変わる**（間欠的に赤くなり「再実行したら緑」を誘発する）。
 * 人手の規律では守れないため、意図的な非対称（CODING_ONLY_BASH / REVIEW_ONLY_BASH）を
 * 明示の除外リストとして持ち、それ以外の Bash 指定の差分をすべて ERROR にする。
 *
 * 比較は toolchainDrift と同じく**ツール指定そのもの**（`Bash(git -C planning log:*)`）の
 * 粒度で行う。`git -C` はパスごとに別エントリであり、コマンド名へ畳み込むと
 * submodule パスの片落ち（planning#163 の本体）が消えるためである。
 */
function genericBashDrift(files) {
  const errors = [];
  const coding = pickCanonical(files, 'claude-coding');
  const review = pickCanonical(files, 'claude-code-review');
  if (!coding || !review) return errors; // 片方しか無い構成は対象外

  const opts = { requireUses: false };
  const genericOf = (f, excluded) => {
    const toolchain = toolchainCommandsOf(f.text, f.tools, opts);
    return new Set(
      f.tools.filter((t) => {
        if (!/^Bash\(/.test(t) || toolchain.has(t)) return false;
        const inner = bashInnerOf(t);
        return inner !== null && !excluded.includes(inner);
      })
    );
  };
  const a = genericOf(coding, CODING_ONLY_BASH);
  const b = genericOf(review, REVIEW_ONLY_BASH);
  const missingInReview = [...a].filter((t) => !b.has(t));
  const missingInCoding = [...b].filter((t) => !a.has(t));

  if (missingInReview.length) {
    errors.push(
      `${path.basename(review.file)}: 実装用にある汎用 Bash 指定が欠けている: ` +
        `${missingInReview.join(', ')}（パイプの後段で使われると鎖全体が拒否され、間欠的に赤くなる。` +
        '意図的な非対称なら check-ai-workflow-config.js の CODING_ONLY_BASH へ宣言すること）'
    );
  }
  if (missingInCoding.length) {
    errors.push(
      `${path.basename(coding.file)}: レビュー用にある汎用 Bash 指定が欠けている: ` +
        `${missingInCoding.join(', ')}（意図的な非対称なら check-ai-workflow-config.js の ` +
        'REVIEW_ONLY_BASH へ宣言すること）'
    );
  }
  return errors;
}

/** 1 ファイルを検査し {errors, warnings, tools} を返す。 */
function checkWorkflow(file, text) {
  const errors = [];
  const warnings = [];
  const blocks = extractClaudeArgsBlocks(text);
  const allTools = [];

  for (const block of blocks) {
    for (const raw of block.body) {
      if (raw.trim().startsWith('#')) {
        errors.push(
          `claude_args ブロック内にコメント行がある: ${raw.trim()}` +
            '（ブロックの各行は引数として渡るため、コメントは claude_args の外に置く）'
        );
      }
    }
    for (const e of parseAllowedTools(block.body)) {
      allTools.push(...e.tools);
      if (!e.quoted && /\s/.test(e.raw)) {
        errors.push(
          `--allowedTools の値が引用符で囲まれておらず空白を含む: ${e.raw}` +
            '（空白で分割され、この指定は無効になる。"A,B,C" のように 1 引数・カンマ区切りで書く）'
        );
      }
    }
    // --allowedTools 以外のフラグも同じ形で壊れる（--append-system-prompt 等）。
    for (const e of parseFlagArgs(block.body)) {
      if (e.flag === '--allowedTools') continue; // 上で専用のメッセージを出している
      if (!e.quoted && e.hasSpace) {
        errors.push(
          `${e.flag} の値が引用符で囲まれておらず空白を含む: ${e.raw}` +
            '（claude_args は空白区切りで argv へトークン化されるため、2 つ目以降が別の引数になり' +
            'この指定は意図どおり効かない。値全体を二重引用符でくくって 1 引数にする）'
        );
      }
    }
  }

  if (blocks.length === 0) return { errors, warnings, tools: allTools, applicable: false };

  const cmds = bashCommandsOf(allTools);
  for (const tc of TOOLCHAINS) {
    // 実際に `uses:` されているものだけを対象にする（コメント中の言及で誤検知しない）。
    const used = new RegExp(`^\\s*-?\\s*uses:\\s*\\S*${tc.action}`, 'm').test(text);
    if (!used) continue;
    if (!tc.commands.some((c) => cmds.has(c))) {
      errors.push(
        `${tc.action} でツールチェーンを用意しているのに、対応する実行ツールを許可していない` +
          `（${tc.commands.map((c) => `Bash(${c}:*)`).join(' / ')} のいずれかを --allowedTools に加える）。` +
          'この不一致があると、AI は検証を実行できず「承認待ちでブロックされた」と報告するだけになる'
      );
    }
  }
  return { errors, warnings, tools: allTools, applicable: true };
}

/** settings.json の allow との乖離を警告として返す（情報提供のみ・失敗させない）。 */
function parityWarnings(perFile, settingsPath = SETTINGS_PATH) {
  const warnings = [];
  let allow;
  try {
    allow = new Set(JSON.parse(fs.readFileSync(settingsPath, 'utf8')).permissions.allow);
  } catch (e) {
    return warnings; // settings.json が無い/壊れている場合は黙って skip
  }
  for (const { file, tools } of perFile) {
    const missing = tools.filter((t) => !allow.has(t));
    if (missing.length) {
      warnings.push(
        `${file}: settings.json の allow に無いツールを CI で許可している: ${missing.join(', ')}` +
          '（ローカルと CI で挙動が変わる。3 系統を揃えること）'
      );
    }
  }
  return warnings;
}

function listWorkflowFiles(dir) {
  try {
    return fs
      .readdirSync(dir)
      .filter((f) => /\.ya?ml(\.example)?$/.test(f) || /\.example\.ya?ml$/.test(f))
      .map((f) => path.join(dir, f));
  } catch (e) {
    return [];
  }
}

/** 検証器自体の自己試験（CI で毎回実行し、検査ロジックの退行を防ぐ）。 */
function selfTest() {
  const cases = [
    {
      name: '引用符なし・空白ありは ERROR',
      yaml: 'jobs:\n  x:\n    steps:\n      - with:\n          claude_args: |\n            --allowedTools Bash(dotnet test:*)\n',
      expect: (r) => r.errors.some((e) => e.includes('引用符で囲まれておらず')),
    },
    {
      name: '引用符あり・カンマ区切りは OK',
      yaml: 'jobs:\n  x:\n    steps:\n      - with:\n          claude_args: |\n            --allowedTools "Read,Bash(dotnet test:*)"\n',
      expect: (r) => r.errors.length === 0,
    },
    {
      name: '空白を含まない指定は引用符なしでも OK',
      yaml: 'jobs:\n  x:\n    steps:\n      - with:\n          claude_args: |\n            --allowedTools Read\n',
      expect: (r) => r.errors.length === 0,
    },
    {
      name: 'ブロック内コメントは ERROR',
      yaml: 'jobs:\n  x:\n    steps:\n      - with:\n          claude_args: |\n            # comment\n            --allowedTools Read\n',
      expect: (r) => r.errors.some((e) => e.includes('コメント行')),
    },
    {
      name: 'setup-dotnet があるのに dotnet 未許可は ERROR',
      yaml: 'jobs:\n  x:\n    steps:\n      - uses: actions/setup-dotnet@v5\n      - with:\n          claude_args: |\n            --allowedTools "Read"\n',
      expect: (r) => r.errors.some((e) => e.includes('setup-dotnet')),
    },
    {
      name: 'コメント中の setup-python は誤検知しない',
      yaml: 'jobs:\n  x:\n    steps:\n      # 他スタックは actions/setup-python に置き換える\n      - uses: actions/setup-dotnet@v5\n      - with:\n          claude_args: |\n            --allowedTools "Read,Bash(dotnet test:*)"\n',
      expect: (r) => r.errors.length === 0,
    },
    {
      name: 'setup-dotnet と dotnet 許可が揃えば OK',
      yaml: 'jobs:\n  x:\n    steps:\n      - uses: actions/setup-dotnet@v5\n      - with:\n          claude_args: |\n            --allowedTools "Read,Bash(dotnet test:*)"\n',
      expect: (r) => r.errors.length === 0,
    },
    // issue planning#149: 記法の穴は --allowedTools に固有ではない。
    {
      // 実際の文面は「Task ツールによる spawn」のように空白を含む。
      name: '引用符なしで空白を含む --append-system-prompt は ERROR',
      yaml: 'jobs:\n  x:\n    steps:\n      - with:\n          claude_args: |\n            --append-system-prompt サブエージェント（Task ツール）は使用しない\n',
      expect: (r) => r.errors.some((e) => e.includes('--append-system-prompt')),
    },
    {
      // 空白が 1 つも無い値は分割されないため、引用符が無くても壊れない（誤検知しない）。
      name: '空白を含まない値は引用符なしでも OK（フラグ横断の検査でも同じ）',
      yaml: 'jobs:\n  x:\n    steps:\n      - with:\n          claude_args: |\n            --append-system-prompt サブエージェントは使用しない\n',
      expect: (r) => r.errors.length === 0,
    },
    {
      name: '引用符付きの --append-system-prompt は OK',
      yaml: 'jobs:\n  x:\n    steps:\n      - with:\n          claude_args: |\n            --append-system-prompt "サブエージェントは使用しない。単一セッションで完結する。"\n',
      expect: (r) => r.errors.length === 0,
    },
    {
      // これを誤検知すると既存の全ワークフローが一斉に ERROR になる。
      name: '${{ }} 式は空白とみなさない（--model を誤検知しない）',
      yaml: 'jobs:\n  x:\n    steps:\n      - with:\n          claude_args: |\n            --model ${{ steps.model.outputs.model }}\n',
      expect: (r) => r.errors.length === 0,
    },
    {
      name: 'claude_args が無いファイルは対象外',
      yaml: 'jobs:\n  x:\n    steps:\n      - uses: actions/setup-dotnet@v5\n',
      expect: (r) => r.applicable === false,
    },
  ];
  // 2 ファイル間の突き合わせ（toolchainDrift）は checkWorkflow 単体では検証できないため個別に試す。
  const mkWf = (tools, setups = ['setup-dotnet']) =>
    `jobs:\n  x:\n    steps:\n` +
    setups.map((s) => `      - uses: actions/${s}@v6\n`).join('') +
    `      - with:\n          claude_args: |\n            --allowedTools "${tools}"\n`;
  const pairWith = (codingTools, reviewTools, codingSetups, reviewSetups) => [
    { file: 'claude-coding.example.yml', text: mkWf(codingTools, codingSetups), tools: codingTools.split(',') },
    { file: 'claude-code-review.example.yml', text: mkWf(reviewTools, reviewSetups), tools: reviewTools.split(',') },
  ];
  const pair = (codingTools, reviewTools) => pairWith(codingTools, reviewTools, undefined, undefined);
  const FULL = 'Read,Bash(dotnet restore:*),Bash(dotnet build:*),Bash(dotnet test:*),Bash(dotnet format:*)';
  const driftCases = [
    ['部分的な複製漏れ（レビュー側に一部が無い）を検出する', pair(FULL, 'Read,Bash(dotnet test:*)'), true],
    ['両者が揃っていれば検出しない', pair(FULL, FULL), false],
    // 実装側にしか無いのが正しいツール（Edit / Write / 書き込み系 git）で誤検知しないこと。
    ['設計上の差（Edit / 書き込み系 git）では誤検知しない', pair(`${FULL},Edit,Write,Bash(git commit:*)`, FULL), false],
    // issue planning#131: setup-* を書かないツールチェーン（node はランナーにプリインストール）。
    // これを取りこぼすと、レビューがキットの検査器群を実走できなくなる。
    [
      'setup-* を書かないツールチェーン（node）の複製漏れも検出する',
      pair(`${FULL},Bash(node:*)`, FULL),
      true,
    ],
    // issue planning#130: setup-* が非対称でも、ツール指定が同一なら差分ではない。
    [
      'setup-* が非対称でもツール指定が同一なら検出しない',
      pairWith(`${FULL},Bash(npm run:*)`, `${FULL},Bash(npm run:*)`, ['setup-dotnet', 'setup-node'], ['setup-dotnet']),
      false,
    ],
    // 逆向き（レビュー側にだけある）も同じく検出すること。
    ['レビュー側にだけあるツールも検出する', pair(FULL, `${FULL},Bash(node:*)`), true],
  ];

  // issue planning#163: スタック別以外の Bash 指定のドリフト（genericBashDrift）。
  // 受け入れ時の陽性対照（片方から Bash(grep:*) を抜いて ERROR / 戻して合格）を恒久化する。
  const RO = 'Read,Bash(rg:*),Bash(grep:*),Bash(sort:*),Bash(cat:*),Bash(git -C planning log:*)';
  const genericCases = [
    ['陽性対照: レビュー側に grep が無ければ検出する', pair(RO, RO.replace(',Bash(grep:*)', '')), true],
    ['陽性対照の対: 両者が揃っていれば検出しない', pair(RO, RO), false],
    ['git -C のパス片落ちも検出する', pair(`${RO},Bash(git -C src/x log:*)`, RO), true],
    [
      '意図的な非対称（実装専用の書き込み系 git / find / mkdir）は誤検知しない',
      pair(`${RO},Bash(git add:*),Bash(git commit:*),Bash(git push:*),Bash(find:*),Bash(mkdir:*)`, RO),
      false,
    ],
    [
      '意図的な非対称（レビュー専用の gh 読解系）は誤検知しない',
      pair(RO, `${RO},Bash(gh issue view:*),Bash(gh pr view:*),Bash(gh run list:*)`),
      false,
    ],
    [
      'スタック別ツールは対象外（toolchainDrift の持ち場と重複させない）',
      pair(`${RO},Bash(dotnet test:*)`, RO),
      false,
    ],
    [
      '引数固定形（:* なし）の片落ちも検出する',
      pair(`${RO},Bash(true --version)`, RO),
      true,
    ],
  ];

  // issue planning#130 副次 / planning#134: 検査が無言で無効になる経路。
  // 第 3 要素は allFiles（ディスク上の全ワークフロー）。
  const CANON = ['.github/workflows/claude-coding.example.yml', '.github/workflows/claude-code-review.example.yml'];
  const scopeCases = [
    ['既定名で 2 ファイル引き当てられれば警告しない', pair(FULL, FULL), false, CANON],
    [
      '既定名で引き当てられない 2 ファイル構成は警告する',
      [
        { file: 'claude.yml', text: mkWf(FULL), tools: FULL.split(',') },
        { file: 'claude-2.yml', text: mkWf(FULL), tools: FULL.split(',') },
      ],
      true,
    ],
    ['1 ファイルのみの構成は警告しない', [{ file: 'claude.yml', text: mkWf(FULL), tools: FULL.split(',') }], false],
    // issue planning#134: 既定名のファイルはディスクに在るが、claude_args を解析できず
    // applicable: false で除外された（＝ files に現れない）状態。
    [
      '既定名のファイルがあるのに claude_args を解析できなければ警告する',
      [{ file: CANON[0], text: mkWf(FULL), tools: FULL.split(',') }],
      true,
      CANON,
    ],
    // 既定名のファイルがディスクにも無い構成（レビュー用のみ等）は警告しない。
    [
      '既定名のファイルがそもそも無ければ警告しない',
      [{ file: CANON[1], text: mkWf(FULL), tools: FULL.split(',') }],
      false,
      [CANON[1]],
    ],
  ];

  let failed = 0;
  for (const [label, files, expectWarn, allFiles] of scopeCases) {
    const got = driftScopeWarnings(files, allFiles).length > 0;
    if (got === expectWarn) {
      process.stdout.write(`  ok  ${label}\n`);
    } else {
      failed++;
      process.stderr.write(`  NG  ${label}（期待 ${expectWarn} / 実際 ${got}）\n`);
    }
  }
  for (const [label, files, expectDrift] of driftCases) {
    const got = toolchainDrift(files).length > 0;
    if (got === expectDrift) {
      process.stdout.write(`  ok  ${label}\n`);
    } else {
      failed++;
      process.stderr.write(`  NG  ${label}（期待 ${expectDrift} / 実際 ${got}）\n`);
    }
  }
  for (const [label, files, expectDrift] of genericCases) {
    const got = genericBashDrift(files).length > 0;
    if (got === expectDrift) {
      process.stdout.write(`  ok  ${label}\n`);
    } else {
      failed++;
      process.stderr.write(`  NG  ${label}（期待 ${expectDrift} / 実際 ${got}）\n`);
    }
  }

  for (const c of cases) {
    const r = checkWorkflow('self-test', c.yaml);
    if (c.expect(r)) {
      process.stdout.write(`  ok  ${c.name}\n`);
    } else {
      failed++;
      process.stderr.write(`  NG  ${c.name}\n      errors=${JSON.stringify(r.errors)}\n`);
    }
  }
  if (failed) {
    process.stderr.write(`\n✗ 検証器の自己試験が ${failed} 件失敗した\n`);
    return 1;
  }
  process.stdout.write(
    `✓ 検証器の自己試験 ${cases.length + driftCases.length + genericCases.length + scopeCases.length} 件すべて合格\n`
  );
  return 0;
}

function main(argv) {
  const args = argv.slice(2);
  if (args.includes('--self-test')) {
    process.exit(selfTest());
  }
  const dirIdx = args.indexOf('--dir');
  const dir = dirIdx !== -1 ? args[dirIdx + 1] : DEFAULT_DIR;

  const files = listWorkflowFiles(dir);
  if (files.length === 0) {
    process.stdout.write(`ワークフローが見つからないため検査をスキップする: ${dir}\n`);
    process.exit(0);
  }

  const allErrors = [];
  const perFile = [];
  const forDrift = [];
  let checked = 0;
  for (const file of files) {
    const text = fs.readFileSync(file, 'utf8');
    const r = checkWorkflow(file, text);
    if (!r.applicable) continue;
    checked++;
    perFile.push({ file: path.basename(file), tools: r.tools });
    forDrift.push({ file, text, tools: r.tools });
    for (const e of r.errors) allErrors.push(`${path.basename(file)}: ${e}`);
  }

  // 2 ファイル間の突き合わせ（部分的な複製漏れ）。単一ファイルの検査では捉えられない。
  allErrors.push(...toolchainDrift(forDrift));
  // スタック別以外の Bash 指定（読み取り専用の汎用コマンド等）も突き合わせる（issue planning#163）。
  allErrors.push(...genericBashDrift(forDrift));

  process.stdout.write(`AI ワークフロー設定チェック: ${checked} 件を検査\n`);
  // 第 2 引数（ディスク上の全ワークフロー）を渡さないと、issue planning#134 の検査は黙って
  // 効かなくなる——この検査器が塞いでいる不具合と同じ形になる。省略しないこと。
  const warnings = [...driftScopeWarnings(forDrift, files), ...parityWarnings(perFile)];
  for (const w of warnings) warn(w);

  // 【任意・opt-in】警告を失敗として扱う厳格モード（issue planning#136）。
  // 警告はいずれも「検査そのものが効いていない」状態を指すため、ファイル名・構成が
  // 固まったリポジトリでは失敗させたい。既定は fail-open のまま（アクションの入力名変更で
  // 全リポジトリの CI が一斉に落ちるのを避ける）。scripts.test.js の REQUIRE_REPO_TESTS と
  // 同じ「既定はオフ、確定したリポジトリだけ厳格化」の運用に揃える。
  if (warnings.length && process.env.STRICT_AI_WORKFLOW_CONFIG === '1') {
    process.stderr.write(
      `\n✗ 検査が成立していない警告が ${warnings.length} 件ある（STRICT_AI_WORKFLOW_CONFIG=1）\n`
    );
    process.exit(1);
  }

  if (allErrors.length) {
    process.stderr.write(`\n✗ 設定の不備 ${allErrors.length} 件:\n`);
    for (const e of allErrors) process.stderr.write(`  - ${e}\n`);
    process.stderr.write(
      '\n検証方法: 実行後のジョブログにある `SDK options:` の allowedTools 配列を確認する。\n' +
        '`"Bash(gh", "issue", "create:*)"` のように割れていれば記法が誤っている。\n'
    );
    process.exit(1);
  }
  process.stdout.write('✓ AI ワークフローのツール許可設定に問題なし\n');
  process.exit(0);
}

if (require.main === module) {
  main(process.argv);
}

module.exports = {
  extractClaudeArgsBlocks,
  parseAllowedTools,
  parseFlagArgs,
  bashCommandsOf,
  bashInnerOf,
  toolchainCommandsOf,
  toolchainDrift,
  genericBashDrift,
  driftScopeWarnings,
  checkWorkflow,
  selfTest,
};
