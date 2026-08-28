#!/usr/bin/env node
'use strict';
/*
 * detect-changed-areas.js
 * 変更ファイル一覧から「backend のビルド・テストを走らせる必要があるか」を判定する
 * （IADR-0208 決定 11）。
 *
 * なぜ要るか:
 *   実測（2026-08-22・直近 40 マージ）では **28 本（70%）が backend の C# を触っていない**。
 *   それらの PR も毎回 `build-and-test`（208 秒）と `lint` を全額払っていた。
 *
 * 🔴 **既定は「走らせる」。許可リストに全件が収まるときだけ skip する。**
 *   判定を誤ったときの倒れ方が非対称だからである ——
 *     - 余計に走らせる誤り: 遅くなるだけ。**気付ける。**
 *     - 走らせない誤り: **退行が無検証でマージされ、緑のまま誰も気付かない。**
 *   したがって**未知のパスは必ず backend=true へ倒す**。
 *
 * 🔴 **`.github/workflows/**` は許可リストに入れない。**
 *   CI 自身を変える PR で backend を skip すると、**その変更を検証できない**。
 *
 * 使い方:
 *   node scripts/detect-changed-areas.js --git                    # git から差分を取って判定（CI 既定）
 *   node scripts/detect-changed-areas.js --files <一覧ファイル>   # 1 行 1 パス
 *   git diff --name-only base...HEAD | node scripts/detect-changed-areas.js
 *   node scripts/detect-changed-areas.js --self-test
 *
 * 出力: 標準出力へ人が読む要約、`$GITHUB_OUTPUT` があれば backend=true|false を書く。
 */

const fs = require('fs');

/**
 * backend に影響しないと**言い切れる**パス。ここに全件が収まるときだけ skip する。
 * 迷ったら足さない —— 足す判断は「このパスの変更で backend のテスト結果が変わることは
 * 原理的に無い」と言えるときに限る。
 */
const SAFE = [
  /^docs\//,
  /^\.ai-context\//,
  /^\.claude\//,
  /^scripts\//, // 検査器・生成器。dotnet のビルド入力ではない（scripts-tests ジョブが別途守る）
  /^frontend\//, // 別スタック。専用ジョブが守る
  /^\.github\/ISSUE_TEMPLATE\//,
  /^\.github\/PULL_REQUEST_TEMPLATE\.md$/,
  /^\.github\/copilot-instructions\.md$/,
  /^\.github\/CODEOWNERS$/,
  /^\.github\/dependabot\.yml$/,
  /^[^/]+\.md$/, // ルート直下の Markdown（README / CHANGELOG / CLAUDE / AGENTS 等）
  /^\.gitignore$/,
  /^\.gitattributes$/,
];

/**
 * 🔴 **明示的に「必ず走らせる」パス。** SAFE より優先して評価する。
 * SAFE の正規表現が将来ゆるんでも、ここに当たれば走る（二重の歯止め）。
 */
const FORCE = [
  /^backend\//,
  // 🔴 `.editorconfig` は **`dotnet format --verify-no-changes` がまさに反応すべき入力**である。
  // 当初は「テスト結果は変わらない」として許可リストへ入れていたが、**`backend` フラグ 1 本で
  // `backend-test` と `lint` の両方を出し分けている**以上、その理由づけは lint 側に通らない
  // （AI レビューの指摘）。**1 つのフラグで 2 つのジョブを決めるなら、
  // どちらか一方でも走らせるべき入力は走らせる側へ倒す。**
  /^\.editorconfig$/,
  /^Directory\.(Build|Packages)\.props$/,
  /^global\.json$/,
  /^nuget\.config$/i,
  /^\.github\/workflows\//, // CI 自身を変える PR は必ず検証する
  /^deploy\//, // helm の pipeline.json 等をテストが読み得る。切り分けが済むまで保守的に
];

/**
 * @returns {{backend: boolean, reason: string, trigger: string|null}}
 */
function decide(files) {
  const list = (files || []).map((f) => String(f).trim()).filter(Boolean);

  // 🔴 変更 0 件は「安全」ではない。差分の取得に失敗した可能性があるので走らせる。
  if (list.length === 0) {
    return { backend: true, reason: '変更ファイルが 0 件（差分の取得に失敗した可能性）', trigger: null };
  }

  const forced = list.find((f) => FORCE.some((re) => re.test(f)));
  if (forced) return { backend: true, reason: '必ず走らせるパスが変更された', trigger: forced };

  const unknown = list.find((f) => !SAFE.some((re) => re.test(f)));
  if (unknown) return { backend: true, reason: '許可リストに無いパスが変更された（既定で走らせる）', trigger: unknown };

  return { backend: false, reason: '変更が許可リストに収まっている', trigger: null };
}

/**
 * git から変更ファイル一覧を取る（`--git`）。
 *
 * 🔴 **`HEAD^1 HEAD` の 1 本で PR も push も賄える。**
 *   - `pull_request`: checkout が `refs/pull/N/merge` を取るため **`HEAD^1` が base** である。
 *   - `push`: `HEAD^1` は直前のコミットで、求める差分そのものである。
 *   分岐が要らないぶん、**片方だけ壊れて気付かない**という形にならない。
 *   checkout は `fetch-depth: 2` で足りる。
 *
 * 🔴 **取れなければ空を返す。** 呼び出し側（`decide`）が「変更 0 件＝差分取得の失敗の
 * 可能性」として **`backend=true`（走らせる）へ倒す**。ここで例外を投げてジョブを
 * 落とさない —— 本判定は速さのためのものであって門ではない。
 */
function filesFromGit() {
  const { execFileSync } = require('child_process');
  try {
    const out = execFileSync('git', ['diff', '--name-only', 'HEAD^1', 'HEAD'], {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    return out.split('\n');
  } catch (e) {
    console.log(`[detect-changed-areas] ::warning::git から差分を取得できなかった（${e.message.split('\n')[0]}）。走らせる側へ倒す。`);
    return [];
  }
}

function selfTest() {
  const t = [];
  const ok = (name, fn) => {
    try {
      fn();
      t.push(`  ok   ${name}`);
    } catch (e) {
      t.push(`  FAIL ${name}: ${e.message}`);
      process.exitCode = 1;
    }
  };
  const isRun = (files, m) => {
    const r = decide(files);
    if (!r.backend) throw new Error(`${m || ''} 走らせるべきなのに skip した: ${JSON.stringify(files)}`);
  };
  const isSkip = (files, m) => {
    const r = decide(files);
    if (r.backend) throw new Error(`${m || ''} skip すべきなのに走らせた: ${r.reason} / ${r.trigger}`);
  };

  ok('docs だけなら skip', () => isSkip(['docs/a.md', 'docs/b/c.md']));
  ok('作業仕様書・ADR だけなら skip', () => isSkip(['.ai-context/specs/x.md', '.ai-context/adr/IADR-0001_x.md']));
  ok('検査器だけなら skip', () => isSkip(['scripts/check-foo.js', 'scripts/README.md']));
  ok('フロントエンドだけなら skip', () => isSkip(['frontend/src/App.tsx']));
  ok('ルート直下の Markdown は skip', () => isSkip(['README.md', 'CHANGELOG.md', 'CLAUDE.md']));
  ok('許可リストの組み合わせも skip', () => isSkip(['docs/a.md', 'scripts/x.js', 'frontend/y.ts']));

  ok('backend の C# は走らせる', () => isRun(['backend/Services/X/src/X.cs']));
  // 🔴 コードでなくてもテストが読む入力は走らせる。
  ok('backend の JSON フィクスチャも走らせる', () => isRun(['backend/Services/X/tests/data/fixture.json']));
  ok('backend の appsettings も走らせる', () => isRun(['backend/Services/X/src/appsettings.json']));
  // VSA 統合後（1 サービス = 1 プロジェクト）のパス形（NFR / IADR-0258）。
  // 判定は `^backend/` 前置だけを見ており構成に依存しないが、**その事実をテストで固定しておく**
  // ——「たまたま動く」と「動くと確かめてある」は、移行の途中で読み分けられなくなる。
  ok('統合後: サービスディレクトリ直下の Program.cs も走らせる',
    () => isRun(['backend/Services/AuditService/Program.cs']));
  ok('統合後: Features/<Entity>/<Operation>/Handler.cs も走らせる',
    () => isRun(['backend/Services/AuditService/Features/Order/Approved/Handler.cs']));
  ok('統合後: サービス内 Tests/ も走らせる',
    () => isRun(['backend/Services/AuditService/Tests/Features/HandlerTests.cs']));
  ok('統合後: サービスディレクトリ直下の appsettings も走らせる',
    () => isRun(['backend/Services/AuditService/appsettings.json']));
  ok('Directory.Packages.props は走らせる', () => isRun(['Directory.Packages.props']));
  ok('Directory.Build.props は走らせる', () => isRun(['Directory.Build.props']));
  ok('global.json は走らせる', () => isRun(['global.json']));
  ok('deploy/ は走らせる（テストが読み得るため保守的に）', () => isRun(['deploy/helm/x/files/pipeline.json']));

  // 🔴 自己言及の罠の回帰テスト: この PR 自身が ci.yml を変える。
  ok('🔴 .github/workflows/ を変える PR は必ず走らせる（CI 自身の変更を検証するため）', () =>
    isRun(['.github/workflows/ci.yml']));
  ok('workflows が 1 件でも混ざれば走らせる', () => isRun(['docs/a.md', '.github/workflows/ci.yml']));
  ok('ISSUE_TEMPLATE は skip してよい（workflows と混同しない）', () =>
    isSkip(['.github/ISSUE_TEMPLATE/bug.yml']));

  // 🔴 AI レビューの指摘の回帰テスト。1 フラグで lint と test の両方を決めている以上、
  // dotnet format が反応する入力は走らせる側へ倒さねばならない。
  ok('🔴 .editorconfig は走らせる（dotnet format がまさに反応する入力）', () => isRun(['.editorconfig']));

  // 🔴 「未知は走らせる」＝ 既定の向き。ここが逆になると退行が無検証で通る。
  ok('🔴 知らないパスは走らせる（許可リスト方式の既定）', () => isRun(['some/new/thing.txt']));
  ok('🔴 ルート直下の未知ファイルも走らせる', () => isRun(['mystery.sh']));
  ok('🔴 1 件でも未知が混ざれば走らせる', () => isRun(['docs/a.md', 'docs/b.md', 'unknown.xyz']));
  ok('🔴 変更 0 件は走らせる（差分取得の失敗を skip と誤読しない）', () => isRun([]));
  ok('🔴 空行・空白のみの行は無視され、結果として 0 件なら走らせる', () => isRun(['', '   ']));

  // 前方一致で誤って許可しない（`docs-internal/` は `docs/` ではない）。
  ok('紛らわしい名前を前方一致で許可しない', () => {
    isRun(['docsomething/a.md']);
    isRun(['scriptsy/a.js']);
    isRun(['frontend-old/a.ts']);
  });
  ok('ルート直下でない Markdown は許可リストに当たらない', () => isRun(['weird/place/notes.md']));

  // 🔴 `--git` の契約: **何があっても例外を投げず配列を返す**。
  // 投げるとジョブが落ち、「速さのための判定」が「門」に化ける。
  ok('filesFromGit: 例外を投げず配列を返す', () => {
    const r = filesFromGit();
    if (!Array.isArray(r)) throw new Error('配列でない');
  });
  ok('filesFromGit: git の無い場所でも空配列に倒れる（走らせる側へ）', () => {
    const cwd = process.cwd();
    const os = require('os');
    const tmp = require('fs').mkdtempSync(require('path').join(os.tmpdir(), 'nogit-'));
    try {
      process.chdir(tmp);
      const r = filesFromGit();
      if (!Array.isArray(r)) throw new Error('配列でない');
      // 空（または空文字だけ）→ decide が走らせる側へ倒すこと
      const d = decide(r);
      if (!d.backend) throw new Error('差分が取れないのに skip した');
    } finally {
      process.chdir(cwd);
    }
  });

  console.log(t.join('\n'));
  console.log(`[detect-changed-areas] 自己試験 ${t.length} 件${process.exitCode ? ' に失敗あり' : ' OK。'}`);
}

function readFiles(argv) {
  if (argv.includes('--git')) return filesFromGit();
  const i = argv.indexOf('--files');
  if (i >= 0) return fs.readFileSync(argv[i + 1], 'utf8').split('\n');
  try {
    return fs.readFileSync(0, 'utf8').split('\n');
  } catch {
    return [];
  }
}

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) return selfTest();

  const files = readFiles(argv);
  const r = decide(files);
  const n = files.map((f) => f.trim()).filter(Boolean).length;

  console.log(`[detect-changed-areas] 変更 ${n} 件 → backend=${r.backend}（${r.reason}）`);
  if (r.trigger) console.log(`[detect-changed-areas] 判定の決め手: ${r.trigger}`);
  if (!r.backend) {
    console.log('[detect-changed-areas] backend のビルド・テストは skip する。');
    console.log('[detect-changed-areas] ⚠️ 迷ったら走らせる設計である。skip が誤りだと思ったら許可リストを狭めること。');
  }

  if (process.env.GITHUB_OUTPUT) {
    fs.appendFileSync(process.env.GITHUB_OUTPUT, `backend=${r.backend}\n`);
  }
}

module.exports = { decide, SAFE, FORCE, filesFromGit };

if (require.main === module) main();
