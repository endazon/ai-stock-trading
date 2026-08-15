#!/usr/bin/env node
'use strict';
/*
 * check-feedback-dispatched.js — 計画リポジトリへ「送付されていない」環流記録を警告する。
 *
 * 背景（planning#217 / planning#218 / planning#219 / planning#220 / planning#221 / planning#222）:
 *   `/plan-feedback` は 2 つの作業から成る ——(1) `feedback/<日付>_<概要>.md` に記録を作る、
 *   (2) 計画リポジトリへ**伝達する**（issue 起票、または記録を `draft/feedback/` へコピー。
 *   経路は後述の 2 つ）。**(1) だけ行われて (2) が漏れる事故が繰り返し
 *   起きている。** 記録には `status: open` と「未送付。計画リポジトリへ issue として起票する」
 *   と書かれたまま PR がマージされ、実装リポの `feedback/` に滞留した。
 *
 *   2026-08-06 の未到達棚卸しでは、**6 件が最長 1 か月近く滞留していた**（うち 1 件は
 *   実装コードのコメントが「計画へ環流済み」と述べていたのに該当ファイルすら無かった）。
 *   PR がマージされても検出されない点で、通常の未反映よりも見つけにくい。
 *
 * 方針:
 *   - **警告のみ。ジョブは落とさない**（exit 0）。起票は人手の判断を伴うため、ブロックにすると
 *     回避策（記録を作らない）を誘発する。統制の目的は「気付けること」であって「止めること」
 *     ではない。
 *   - GitHub Actions 上ではアノテーションとして出す（`lib/ci-annotate.js`）。ジョブログを
 *     開かなくても PR の Checks 画面で気付ける。
 *   - **厳格化は opt-in**（`STRICT_FEEDBACK_DISPATCH=1` で警告を失敗として扱う）。
 *
 * 伝達済みと見なす条件（いずれか 1 つ）。**いずれも構造化された記述である。**
 *   a. フロントマターに `planning_issue:` があり値が空でない
 *   b. フロントマターが `dispatched: true`
 *   c. **ファイル全体のどこか**に**計画リポジトリ**の GitHub issue **または PR** への URL がある
 *      （宛先は 置換点 `PLANNING_REPO`。自リポ判定は `GITHUB_REPOSITORY`。フロントマターの
 *      `source_ref:` も対象である）
 *
 *   **本文の「起票済み」は証拠にしない**（planning#320 の監査）。素の部分一致であり、
 *   文意と無関係に一致する（「他件は起票済みだが本件は未対応である」も証拠と判定された）。
 *   知見 3 で撤廃した「未送付」と同じアンチパターンであり、残すと**自己鎮火**が成立する。
 *
 * 警告する条件（いずれか）:
 *   1. フロントマターが `status: open` で、上記の伝達済みの証拠が無い
 *   2. フロントマターが `dispatched: false` で、**かつ上記の証拠も無い**（`status` を問わない。
 *      記録自身の自己申告）。**証拠があるときに発火させてはならない** —— 雛形が
 *      `dispatched: false` を既定値として配るため、無条件だと**雛形どおりに書いた記録が
 *      必ず警告される**（planning#320 の監査で検出）。鍵の更新漏れは伝達の欠落ではない。
 *   3. `dispatched:` の値が `true` / `false` のどちらでもない（**YAML 1.1 では `no` / `off` も
 *      偽である**ため、書けば黙って警告が消える。空振りを緑として記録しない）。
 *
 * **検出できない限界**（いずれも「鍵が見えない」形であり、鍵が無いのと区別がつかない）:
 *   - **鍵名の誤記**（`dispatchd:` 等）
 *   - **全角コロン**（`dispatched： false`）
 *   - **字下げされた鍵**（` planning_issue: 319`。ただしこれは YAML 自体が不正である）
 *
 *   いずれも `status: open` 側の条件が拾う（実コーパスでは大半がこれに該当する）が、
 *   **`status` が `open` 以外なら素通りする**。
 *
 *   **ブロックスカラー（`status: >-`）・アンカー（`&a`）・タグ（`!!str`）・フロー（`[a]`）・
 *   BOM 付きファイルも黙って誤読する。** 本検査器はフロントマターを行単位で読む簡易実装で
 *   あり、YAML の全構文は解さない（実コーパスでの出現は 0 件）。
 *
 * **`planning_issue: #319` は値として読む。これは YAML 仕様からの意図的な逸脱である**
 *   （YAML では空白直後の `#` はコメントで値は null）。人が issue 番号をこの形で書くため
 *   であり、**他の YAML 読取とは解釈が割れる**。`status:` / `dispatched:` には効かない。
 *
 * **証拠の走査はファイル全体を対象とする**（フロントマターを含む）。`source_ref:` に
 *   計画リポジトリの URL があれば証拠になる。また**コードフェンス内の URL も証拠になる**
 *   （`maskCode` は適用していない）—— 引用目的のリンクを証拠と見なす残存リスクであり、
 *   実コーパス 58 件では該当 0 件であることを確認したうえで受容している（planning#320）。
 *
 * **README が定める伝達経路は 2 つある**（planning#319 の裁定・2026-08-11）。
 *   - **GitHub Issue 経路**: 計画リポジトリへ issue を起票する
 *   - **記録ファイル経路**: 記録を計画リポジトリの `draft/feedback/` へコピーする
 *
 *   後者は issue を作らないため、**証拠として計画リポジトリの PR URL を認める**（条件 c）。
 *   「いずれか一方で足りる」という手順に対し、検査器が issue 経路しか読まないと、
 *   記録ファイル経路を採った記録に**恒久的な偽陽性**が残る（実測 1 件・planning#306）。
 *
 * **自己申告は本文の語ではなくフロントマターの鍵で表す**（同上の裁定）。
 *   以前は本文に「未送付」の語があることを警告条件にしていたが、**素の部分一致であるため
 *   検査器そのものを論じた記録が語を含むだけで自己発火した**（実測。見出しに 1 回使っただけで
 *   警告が 1 → 2 件に増えた）。環流の運用を論じる記録ほどこの語を使いたくなるため、
 *   同型の偽陽性を作り続ける。**鍵へ移すことで構造的に消える。**
 *
 * 使い方:
 *   node scripts/check-feedback-dispatched.js            # feedback/ を検査
 *   node scripts/check-feedback-dispatched.js --self-test
 */

const fs = require('fs');
const path = require('path');
const { warn, notice } = require('./lib/ci-annotate.js');

/** 検査対象のディレクトリ（リポジトリ直下からの相対）。 */
const FEEDBACK_DIR = 'feedback';

// 【置換点】**計画リポジトリ**（`owner/repo`）。伝達の証拠として認める URL の宛先を、
// ここに挙げたリポジトリだけに限る。**配布時に必ず自分の計画リポジトリへ書き換えること。**
//
// **書き換え忘れは偽陽性を生む（こちらのほうが起きやすい）。** 別の計画リポジトリを使う
// 組織がこの既定のまま動かすと、**自組織の計画リポへの URL が証拠として認められず**、
// URL だけを証拠にしていた記録がすべて赤くなる（実測: コーパス 58 件で警告 17 → 37 件）。
// **恒久的な偽陽性は検査そのものを外させる** —— `check-cross-repo-refs.js` の `SELF_NAMES`
// で実測済みの失敗（22 件）と同型である。
//
// **空にすると「自リポジトリ以外なら何でも証拠」へ倒れる**（旧挙動）。そちらは逆に、
// **無関係な第三者リポの issue URL を 1 行足すだけで検査器が沈黙する**（planning#320 の
// 6 巡目監査が実証）。実装リポの環流記録が上流 OSS の issue を引くのは自然な運用であり、
// 「計画リポジトリへ伝達した」の代理として文意と無関係に一致する。
//
// 環境変数 `PLANNING_REPOSITORY` で上書きできる。**`??` であって `||` ではない** ——
// `||` だと `PLANNING_REPOSITORY=''` が既定値へ落ち、**env から「無効化」を表現できない**
// （文書は「空にすると旧挙動へ倒れる」と書いているのに、env 経由ではそうならない）。
const PLANNING_REPO_RAW = (process.env.PLANNING_REPOSITORY ?? 'endazon/project-planning').trim();

/**
 * 置換点の値を検証する。`owner/repo` の形でなければ**旧挙動へ倒し、理由を告げる**。
 * URL 全体・`.git` 付き・owner 欠落を**黙って受け入れると全証拠を捨てる**（実測で
 * 警告が 2.2 倍になった）。**空振りを緑としても赤としても、黙って記録しない。**
 */
function normalizePlanningRepo(raw, onWarn) {
  const v = String(raw || '').trim().toLowerCase();
  if (v === '') return '';
  // `.git` を除くのは、`git remote -v` の出力から末尾を写す経路が現実にあるためである
  // （`[\w.-]+` は `.` を許すので、素の `owner/repo` 検査だけでは通ってしまう。
  // planning#320 の 8 巡目監査で検出 —— docstring が名指しした 3 例のうち 1 例が漏れていた）。
  if (!/^[\w-][\w.-]*\/[\w-][\w.-]*$/.test(v) || v.endsWith('.git')) {
    if (onWarn) {
      onWarn(
        `[check-feedback-dispatched] 置換点 PLANNING_REPO が owner/repo の形ではありません: ${raw}。` +
          '証拠の絞り込みを無効化し、「自リポジトリ以外なら証拠」の旧挙動で続行します。'
      );
    }
    return '';
  }
  return v;
}

const PLANNING_REPO = normalizePlanningRepo(PLANNING_REPO_RAW, warn);

/** フロントマターの本体（`---` に挟まれた部分）を返す。無ければ空文字。 */
function frontMatterOf(text) {
  const m = /^---\r?\n([\s\S]*?)\r?\n---/.exec(text);
  return m ? m[1] : '';
}

/**
 * フロントマターから 1 キーの値を取り出す（クォート・行末コメント・前後空白は落とす）。
 *
 * **区切りに `\s` を使ってはならない。** `\s` は改行に一致するため、**値が空の鍵は
 * 次の行を丸ごと値として飲み込む**（`planning_issue:` の直後に `dispatched: false` が
 * 並ぶと、`planning_issue` の値が `"dispatched: false"` になる）。結果、**空の鍵が
 * 「非空の証拠」と判定され、検査器が完全に沈黙する**（planning#320 の監査で検出）。
 *
 * **踏むのは `feedback/README.md` の移行指示に従ったときである** —— 既存記録へ
 * `dispatched: false` を書き足すと、空の `planning_issue:` の**後ろ**に鍵が並ぶ。
 * 出荷時の雛形は `planning_issue:` がフロントマター最終行なので**たまたま踏まない**が、
 * それは並び順に依存した偶然であり、根拠にしてはならない。
 */
function fmValue(text, key) {
  const fm = frontMatterOf(text);
  const re = new RegExp(`^${key}[ \\t]*:[ \\t]*(.*)$`, 'm');
  const m = re.exec(fm);
  if (!m) return '';
  // コメント（`# …`）を落とす。YAML は値として解釈しないため。
  // **値がコメントだけの場合も落とす。** 「値の後ろ」だけを落とす形にすると
  // `planning_issue: # 後で埋める` が非空の値として残り、**空の鍵と同じ沈黙**を招く。
  //
  // **区切りは `[^\S\n]`（改行以外の空白）である。** `[ \t]` へ狭めてはならない ——
  // **全角空白（U+3000）を挟んだコメントが値に残り、同じ沈黙になる**（日本語文書では
  // 現実に混入する）。改行を除くのは、`m[1]` が単一行だからではなく念のためである。
  // **鍵の正規表現（上）で `\s` を禁じた理由は改行への一致であり、単一行の値を扱う
  // ここには当てはまらない** —— 3 巡目の判断をここへ持ち込んだのが誤りであった。
  //
  // **`#319` を値として残す例外は `planning_issue:` に限る。** 人が issue 番号をこの形で
  // 書くためだが、**閉じた語彙と突き合わせる鍵へ一律に効かせてはならない**（planning#320
  // の 5 巡目監査で検出）。`status: open #319 で起票予定` が `open` と読めず**沈黙**し、
  // `dispatched: true #319 へ起票済み` が「解釈できない値」の**偽陽性**になる。
  const comment = key === 'planning_issue' ? /(^|[^\S\n])#(?!\d)[^\n]*$/ : /(^|[^\S\n])#[^\n]*$/;
  return m[1].replace(comment, '').trim().replace(/^['"]|['"]$/g, '').trim();
}

/**
 * 本文中の GitHub issue / PR URL のうち、伝達の証拠になり得るものを返す。
 * selfRepo は `owner/repo`（`GITHUB_REPOSITORY` の形）。自リポジトリを指す URL は除く。
 * 空なら「すべて他リポ扱い」とする ——ローカル実行で自リポを特定できないときに、
 * 伝達済みを未伝達と誤判定しないためである。
 * planningRepo を指定した場合は**その宛先だけ**を返す（無関係な第三者リポを証拠にしない）。
 * 空なら宛先を問わない旧挙動へ倒れる（fail-open）。
 *
 * **PR を含めるのは、記録ファイル経路（計画リポジトリの `draft/feedback/` へコピー）が
 * issue を作らないためである**（planning#319）。この経路の証拠はコピーを載せた PR しかない。
 */
function foreignPlanRefs(text, selfRepo, planningRepo = PLANNING_REPO) {
  const out = [];
  // ホストは `www.` 付き・大文字混じりも受ける（GitHub のホスト名は大小を区別しない）。
  // **証拠を取りこぼす向きの誤りは、正しく起票した記録を恒久的に赤くする**ため、この向きだけは
  // 広く取る。番号（`\d+`）は必須である —— 番号なしの一覧 URL は特定の issue / PR を指さない。
  const re = /https?:\/\/(?:www\.)?github\.com\/([\w.-]+)\/([\w.-]+)\/(issues|pull)\/(\d+)/gi;
  let m;
  while ((m = re.exec(text)) !== null) {
    const repo = `${m[1]}/${m[2]}`;
    if (selfRepo && repo.toLowerCase() === selfRepo.toLowerCase()) continue;
    // 計画リポジトリを特定できているなら、その宛先だけを証拠と認める。
    // 空のときは従来どおり「自リポ以外なら証拠」へ倒す（fail-open を保つ）。
    if (planningRepo && repo.toLowerCase() !== planningRepo) continue;
    out.push(`${repo}#${m[4]}`);
  }
  return out;
}

/**
 * 1 ファイルを判定する。`{ dispatched, reasons }` を返す。
 *
 * `planningRepo` は置換点の値を上書きする。**自己試験が置換点に依存しないために要る** ——
 * 検体は `endazon/project-planning` を固定で書くので、配布先が置換点を書き換えた瞬間に
 * 自己試験が落ち、**手順書どおりに書き換えた全配布先で CI が赤くなる**（planning#320 の
 * 8 巡目監査が実証）。「警告のみ・ジョブは落とさない」という本検査器の設計原則に反する。
 */
function inspectImpl(text, selfRepo, planningRepo = PLANNING_REPO) {
  const status = fmValue(text, 'status').toLowerCase();
  const links = foreignPlanRefs(text, selfRepo, planningRepo);
  const hasPlanningIssueKey = fmValue(text, 'planning_issue') !== '';

  // 自己申告はフロントマターの鍵で表す（本文の語では判定しない。planning#319）。
  const declared = fmValue(text, 'dispatched').toLowerCase();
  const saysNotSent = declared === 'false';
  const saysSent = declared === 'true';
  // 値を検証する。**YAML 1.1 では `no` / `off` も偽である**ため、`dispatched: no` と書くと
  // 黙って警告が消える（planning#320 の監査で検出）。**空振りを緑として記録しない。**
  const badDeclared = declared !== '' && !saysSent && !saysNotSent;

  // **証拠は構造化されたものだけを認める。** 本文の「起票済み」は素の部分一致であり、
  // **知見 3 で撤廃した「未送付」とまったく同じアンチパターン**である。実測でこの語は
  // 文意と無関係に一致する（「他件は起票済みだが本件は未対応である」「未起票済み」の
  // いずれも証拠と判定された）。**この検査器や環流運用を論じる記録は説明文で必ずこの語を
  // 使う**ため、残すかぎり**自己鎮火**が成立する —— 知見 3 が防いだ自己発火の鏡像である。
  //
  // **`dispatched:` からだけ塞いで `status:` に残すのは一貫しない**（planning#320 の監査で
  // 2 度指摘された）。実コーパス 58 件を実測し「起票済み」だけが証拠の記録が **0 件**で
  // あることを確かめたうえで、証拠から外した。
  const dispatched = hasPlanningIssueKey || saysSent || links.length > 0;

  const reasons = [];
  if (badDeclared) {
    reasons.push(`\`dispatched: ${declared}\` は解釈できない（\`true\` / \`false\` のみ）`);
  }
  // **構造化された証拠が無いときだけ発火させる。** 雛形は `dispatched: false` を既定値として
  // 配るため、無条件に警告すると「`planning_issue:` を埋めたのに赤いまま」になり、**雛形どおりに
  // 書いた記録が必ず偽陽性になる**（planning#320 の監査で検出）。裁定は「いずれか一方で足りる」で
  // あり、鍵の更新漏れは伝達そのものの欠落ではない。
  if (saysNotSent && !dispatched) {
    reasons.push('`dispatched: false` で、他に伝達の証拠も無い（記録自身が未伝達と述べている）');
  }
  if (status === 'open' && !dispatched) {
    reasons.push('`status: open` だが計画リポジトリの issue / PR への参照が無い');
  }
  return { dispatched, status, links, reasons };
}

/**
 * 検査対象から外すファイル名（小文字で比較）。
 * `TEMPLATE.md` は雛形であり `status: open` を持つのが正常であるため、除外しないと常時 warn になる。
 */
const EXCLUDED = new Set(['readme.md', 'template.md']);

/** feedback/ 配下の Markdown を列挙する（存在しなければ空配列）。 */
function listFeedbackFiles(root) {
  const dir = path.join(root, FEEDBACK_DIR);
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return [];
  }
  return entries
    .filter((e) => e.isFile() && e.name.endsWith('.md') && !EXCLUDED.has(e.name.toLowerCase()))
    .map((e) => path.join(dir, e.name))
    .sort();
}

function selfTest() {
  const assert = require('assert');
  const SELF = 'endazon/ai-stock-trading';
  // **自己試験は固定の試験用設定で走る**（`check-cross-repo-refs.js` と同じ形）。置換点
  // `PLANNING_REPO` をどう書き換えても結果が変わらないようにするためである。**配布先が
  // 手順書どおりに書き換えたときに自己試験が落ちてはならない。**
  const PLAN = 'endazon/project-planning';
  const inspect = (text, selfRepo, planningRepo = PLAN) => inspectImpl(text, selfRepo, planningRepo);

  // 起票済み: 他リポの issue URL がある
  assert.strictEqual(
    inspect('---\nstatus: open\n---\n[#209](https://github.com/endazon/project-planning/issues/209)', SELF)
      .reasons.length,
    0,
    '他リポの issue URL があれば起票済みと見なす'
  );

  // 自リポの issue URL は起票の証拠にならない
  assert.ok(
    inspect('---\nstatus: open\n---\n本文 https://github.com/endazon/ai-stock-trading/issues/375', SELF)
      .reasons.length > 0,
    '自リポの issue URL は計画への起票ではない'
  );

  // `dispatched: false` かつ証拠なしは status を問わず警告する（移行対象を取りこぼさない）
  assert.ok(
    inspect('---\nstatus: accepted\ndispatched: false\n---\n本文', SELF).reasons.length > 0,
    '`dispatched: false` ＋証拠なしは status に関わらず警告する'
  );

  // 【回帰】本文の「起票済み」は証拠にしない。撤廃した「未送付」と同じ素の部分一致であり、
  // 残すと**自己鎮火**が成立する（自己発火の鏡像）。`status` 側・自己申告側の両方で確かめる。
  assert.ok(
    inspect('---\nstatus: accepted\ndispatched: false\n---\n本検査器は本文の「起票済み」を証拠と見なす', SELF)
      .reasons.length > 0,
    '本文の「起票済み」が明示的な `dispatched: false` を打ち消している'
  );
  assert.ok(
    inspect('---\nstatus: open\n---\n他件は起票済みだが本件は未対応である', SELF).reasons.length > 0,
    '本文の「起票済み」が `status: open` の証拠になっている（文意と無関係に一致する）'
  );

  // 【回帰】空の鍵が次の行を値として飲み込まないこと。`\s` を区切りに使うと
  // `planning_issue:` の値が `"dispatched: false"` になり、**検査器が完全に沈黙する**。
  assert.strictEqual(
    fmValue('---\nstatus: open\nplanning_issue:\ndispatched: false\n---\n', 'planning_issue'),
    '',
    '空の鍵が次の行を飲み込んでいる'
  );
  assert.strictEqual(
    inspect('---\nstatus: open\nplanning_issue:\ndispatched: false\n---\n本文', SELF).reasons.length,
    2,
    '空の `planning_issue:` が証拠と誤判定され、検査器が沈黙している'
  );

  // 【回帰】値の正規化（クォート・大小・コメント）。値検証を入れた以上、ここが効く。
  // **件数だけを見てはならない。** 正規化が壊れると「解釈できない値」の警告へ入れ替わり、
  // 件数は 1 のままである（`> 0` では素通りする＝ vacuous。planning#320 の監査で検出）。
  // **理由文まで検証し、肯定側〔緑になるべき形〕も固定する。**
  assert.match(
    inspect('---\nstatus: accepted\ndispatched: "false"\n---\n本文', SELF).reasons.join(),
    /他に伝達の証拠も無い/,
    'クォート付きの `"false"` を false と解釈できていない'
  );
  assert.match(
    inspect('---\nstatus: accepted\ndispatched: FALSE\n---\n本文', SELF).reasons.join(),
    /他に伝達の証拠も無い/,
    '大文字の `FALSE` を false と解釈できていない'
  );
  assert.strictEqual(
    inspect('---\nstatus: open\ndispatched: "true"\n---\n本文', SELF).reasons.length,
    0,
    'クォート付きの `"true"` を true と解釈できていない'
  );
  assert.strictEqual(
    inspect('---\nstatus: open\ndispatched: True\n---\n本文', SELF).reasons.length,
    0,
    '大文字混じりの `True` を true と解釈できていない'
  );

  // 【回帰】値がコメントだけの鍵は空として扱う（非空の証拠と誤判定して沈黙しない）。
  assert.strictEqual(
    fmValue('---\nplanning_issue: # 後で埋める\n---\n', 'planning_issue'),
    '',
    'コメントだけの値を非空と判定している'
  );
  assert.strictEqual(
    inspect('---\nstatus: open\nplanning_issue: # 後で埋める\n---\n本文', SELF).reasons.length,
    1,
    'コメントだけの `planning_issue:` が証拠と誤判定され、検査器が沈黙している'
  );
  // 【回帰】`#319`（人が issue 番号を書く形）はコメントではない。落とすと正当な証拠が消える。
  assert.strictEqual(
    fmValue('---\nplanning_issue: #319\n---\n', 'planning_issue'),
    '#319',
    '`#319` をコメントとして落としている（逆向きの偽陽性）'
  );
  // 【回帰】`#<数字>` の例外は `planning_issue:` に限る。閉じた語彙の鍵へ効かせると、
  // `status` は沈黙し `dispatched` は偽陽性になる（同じ構文が両方向に壊れる）。
  assert.match(
    inspect('---\nstatus: open #319 で起票予定\n---\n本文', SELF).reasons.join(),
    /status: open/,
    '`status:` の行末コメントが値に残り、open と読めず沈黙している'
  );
  assert.strictEqual(
    inspect(
      '---\nstatus: accepted\ndispatched: true #319 へ起票済み\nplanning_issue: 319\n---\n本文',
      SELF
    ).reasons.length,
    0,
    '`dispatched:` の行末コメントが値に残り、証拠が揃っているのに赤くなっている'
  );
  // 【回帰】全角空白（U+3000）を挟んだコメントも落とす。`[ \t]` へ狭めると値に残って沈黙する。
  assert.strictEqual(
    fmValue('---\nplanning_issue: 　# 後で埋める\n---\n', 'planning_issue'),
    '',
    '全角空白を挟んだコメントが値に残っている'
  );
  assert.strictEqual(
    inspect('---\nstatus: Open\n---\n本文', SELF).reasons.length,
    1,
    '`status: Open`（大文字）を open と解釈できていない'
  );
  assert.strictEqual(
    inspect('---\nstatus: accepted\ndispatched: false # 補足\n---\nhttps://github.com/endazon/project-planning/issues/1', SELF)
      .reasons.length,
    0,
    '行末コメントを値に含めてしまっている'
  );

  // 【回帰】`dispatched` の値を検証する（YAML 1.1 の `no` / `off` で黙って緑にしない）
  for (const bad of ['no', 'off', '0', 'yes']) {
    assert.ok(
      inspect(`---\nstatus: accepted\ndispatched: ${bad}\n---\n本文`, SELF).reasons.length > 0,
      `\`dispatched: ${bad}\` を解釈できない値として警告する`
    );
  }

  // 【回帰】雛形どおり `dispatched: false` のままでも、証拠があれば警告しない。
  // 雛形は `dispatched: false` を既定で配るため、ここが無条件だと**雛形どおりに書いた
  // 記録が必ず偽陽性になる**（planning#320 の監査で検出）。
  assert.strictEqual(
    inspect('---\nstatus: accepted\ndispatched: false\nplanning_issue: 319\n---\n本文', SELF).reasons.length,
    0,
    '`planning_issue:` があれば `dispatched: false` のままでも警告しない'
  );
  assert.strictEqual(
    inspect(
      '---\nstatus: accepted\ndispatched: false\n---\nhttps://github.com/endazon/project-planning/pull/320',
      SELF
    ).reasons.length,
    0,
    '計画リポの PR URL があれば `dispatched: false` のままでも警告しない'
  );

  // `dispatched: true` は伝達の証拠になる
  assert.strictEqual(
    inspect('---\nstatus: open\ndispatched: true\n---\n本文', SELF).reasons.length,
    0,
    '`dispatched: true` は伝達済みと見なす'
  );

  // 【回帰】本文に「未送付」の語があるだけでは警告しない（検査器を論じた記録の自己発火を防ぐ）
  assert.strictEqual(
    inspect(
      '---\nstatus: accepted\n---\n## 「未送付」検査器が語の一致だけで自己発火する\n本文',
      SELF
    ).reasons.length,
    0,
    '本文の「未送付」の語では発火しない（planning#319 知見 3）'
  );

  // 【回帰】記録ファイル経路: 計画リポジトリの PR URL を伝達の証拠と認める
  assert.strictEqual(
    inspect('---\nstatus: open\n---\nコピー済み https://github.com/endazon/project-planning/pull/306', SELF)
      .reasons.length,
    0,
    '他リポの PR URL は伝達の証拠になる（planning#319 知見 1）'
  );

  // 自リポの PR URL は証拠にならない（issue と同じ扱い）
  assert.ok(
    inspect('---\nstatus: open\n---\nhttps://github.com/endazon/ai-stock-trading/pull/306', SELF).reasons.length > 0,
    '自リポの PR URL は計画への伝達ではない'
  );

  // planning_issue キーがあれば起票済み
  assert.strictEqual(
    inspect('---\nstatus: open\nplanning_issue: 209\n---\n本文', SELF).reasons.length,
    0,
    'planning_issue キーがあれば起票済みと見なす'
  );

  // 空値の planning_issue は証拠にならない
  assert.ok(
    inspect('---\nstatus: open\nplanning_issue:\n---\n本文', SELF).reasons.length > 0,
    '空値の planning_issue は起票の証拠にならない'
  );

  // open 以外で伝達の証拠が無くても、`dispatched: false` が無ければ警告しない
  assert.strictEqual(
    inspect('---\nstatus: accepted\n---\n本文', SELF).reasons.length,
    0,
    'open 以外は起票済みの証拠を求めない'
  );

  // 【回帰】計画リポジトリ以外への URL は証拠にしない。**第三者リポの issue を 1 行引くだけで
  // 検査器が沈黙する**のは、「本文の『起票済み』」を外したのと同じ理由（文意と無関係に一致する）
  // で認められない（planning#320 の 6 巡目監査で検出）。
  assert.ok(
    inspect('---\nstatus: open\n---\n参考: https://github.com/dotnet/runtime/issues/12345', SELF).reasons.length > 0,
    '第三者リポの issue URL が伝達の証拠になっている'
  );
  assert.strictEqual(
    inspect('---\nstatus: open\n---\nhttps://github.com/endazon/project-planning/issues/319', SELF).reasons.length,
    0,
    '計画リポジトリの issue URL が証拠として効いていない'
  );
  // 置換点が空なら従来どおり「自リポ以外なら証拠」へ倒す（fail-open）。
  assert.strictEqual(
    foreignPlanRefs('https://github.com/dotnet/runtime/issues/1', SELF, '').length,
    1,
    '置換点が空のとき fail-open になっていない'
  );
  // 【回帰】計画リポの照合は**厳密一致**である。前方一致・包含・owner 一致へ緩めると、
  // 紛らわしい名前のリポや同一 owner の任意のリポが証拠に化ける（塞いだ穴が復活する）。
  for (const u of [
    'https://github.com/endazon/project-planning-old/issues/2',
    'https://github.com/endazon/microservices-platform/issues/2',
    'https://github.com/other/project-planning/issues/2',
  ]) {
    assert.ok(
      inspect(`---\nstatus: open\n---\n${u}`, SELF).reasons.length > 0,
      `計画リポと紛らわしい URL が証拠になっている: ${u}`
    );
  }
  // 【回帰】計画リポ側も大小を無視する（肯定側＝緑になるべき形を固定する）。
  assert.strictEqual(
    inspect('---\nstatus: open\n---\nhttps://github.com/ENDAZON/PROJECT-PLANNING/issues/9', SELF).reasons.length,
    0,
    '計画リポの URL が大小の違いで証拠から落ちている'
  );
  // 【回帰】置換点の正規化。`owner/repo` でない値は旧挙動へ倒し、黙って全証拠を捨てない。
  assert.strictEqual(normalizePlanningRepo('  Endazon/Project-Planning  '), 'endazon/project-planning');
  for (const bad of [
    'https://github.com/endazon/project-planning',
    'project-planning',
    'a/b/c',
    'endazon/project-planning.git',
  ]) {
    let warned = 0;
    assert.strictEqual(
      normalizePlanningRepo(bad, () => {
        warned += 1;
      }),
      '',
      `不正な置換点 ${bad} を受け入れている（全証拠を捨てる）`
    );
    assert.strictEqual(warned, 1, `不正な置換点 ${bad} を黙って無視している`);
  }
  // 【回帰】値の前後空白を落とす（落とさないと `status: open ` が読めず沈黙する）。
  assert.strictEqual(fmValue('---\nstatus: open  \n---\n', 'status'), 'open');

  // selfRepo が空なら、どの issue URL も起票の証拠として扱う（誤検出を避ける）
  // selfRepo が不明（ローカル実行等）でも、計画リポジトリの URL は証拠のままである
  // ——「伝達済みを未伝達と誤判定しない」という当初の意図を、宛先の絞り込み後も保つ。
  assert.strictEqual(
    inspect('---\nstatus: open\n---\nhttps://github.com/endazon/project-planning/issues/1', '').reasons.length,
    0,
    'selfRepo 不明時は誤検出しない側へ倒す'
  );

  // 【回帰】自己試験は置換点に依存しない。**配布先が手順書どおりに書き換えたときに
  // 自己試験が落ちてはならない**（落ちると「警告のみ・ジョブは落とさない」が破れる）。
  assert.strictEqual(
    inspectImpl('---\nstatus: open\n---\nhttps://github.com/acme/planning/issues/1', SELF, 'acme/planning')
      .reasons.length,
    0,
    '置換点を書き換えた配布先で判定が壊れている'
  );

  console.log('[check-feedback-dispatched] self-test OK');
}

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) {
    selfTest();
    process.exit(0);
  }

  const root = process.cwd();
  const files = listFeedbackFiles(root);

  if (files.length === 0) {
    notice(
      `[check-feedback-dispatched] ${FEEDBACK_DIR}/ が無いか Markdown がありません。検査をスキップします。`
    );
    process.exit(0);
  }

  const selfRepo = process.env.GITHUB_REPOSITORY || '';
  const findings = [];
  for (const fp of files) {
    const text = fs.readFileSync(fp, 'utf8');
    const r = inspectImpl(text, selfRepo);
    if (r.reasons.length > 0) findings.push({ fp: path.relative(root, fp), reasons: r.reasons, text });
  }

  if (findings.length === 0) {
    console.log(
      `[check-feedback-dispatched] OK: ${files.length} 件の環流記録に未送付のものはありません。`
    );
    process.exit(0);
  }

  const lines = findings.map((f) => `${f.fp}: ${f.reasons.join(' / ')}`);

  // **置換点の取り違えを、警告そのものから気付けるようにする。** 制限を外せば証拠になった
  // 外部 URL が指摘対象の記録にあるなら、書き手は URL を残しているのに赤いままである
  // ——「何をすればよいか」が読み取れない恒久的な警告は無視されるようになる（本検査器が
  // `SELF_NAMES` の実測 22 件から学んだ型）。planning#320 の 8 巡目監査で検出。
  //
  // **selfRepo が不明なら hint は出さない。** 証拠判定は「誤検出しない側へ倒す」（自リポ判定を
  // 諦めて全 URL を証拠扱いにする）が、hint は逆に「誤誘導しない側へ倒す」必要がある
  // —— 自リポの URL を「制限を外せば証拠になった URL」と数えてしまい、**正しい設定を疑わせる**
  // からである。GITHUB_REPOSITORY を持たないローカル実行で実際に発火した（実測: 自リポ URL
  // だけの 7 ファイルで hint が出た）。planning#320 の 9 巡目監査で検出。
  let hint = '';
  if (PLANNING_REPO && selfRepo) {
    const missed = findings.some((f) => foreignPlanRefs(f.text, selfRepo, '').length > 0);
    if (missed) {
      hint =
        `  なお現在の PLANNING_REPO は "${PLANNING_REPO}" です。` +
        '自組織の計画リポジトリと異なる場合は scripts/check-feedback-dispatched.js の置換点' +
        '（または環境変数 PLANNING_REPOSITORY）を確認してください。';
    }
  }

  warn(
    `[check-feedback-dispatched] 計画リポジトリへ未送付の可能性がある環流記録が ${findings.length} 件あります。` +
      `${lines.join('  ')}  ` +
      '記録を作るだけでは計画へ届きません。計画リポジトリへ issue として起票するか、' +
      '記録を計画リポジトリの draft/feedback/ へコピーし、いずれの場合も issue / PR の URL を記録に残してください' +
      '（フロントマターの planning_issue: でもよい）。' +
      hint
  );
  for (const f of findings) console.error(`  - ${f.fp}\n      ${f.reasons.join('\n      ')}`);

  if (process.env.STRICT_FEEDBACK_DISPATCH === '1') {
    console.error('[check-feedback-dispatched] STRICT_FEEDBACK_DISPATCH=1 のため失敗として扱います。');
    process.exit(1);
  }
  process.exit(0);
}

if (require.main === module) main();

module.exports = {
  frontMatterOf,
  fmValue,
  foreignPlanRefs,
  normalizePlanningRepo,
  PLANNING_REPO,
  inspect: inspectImpl,
  listFeedbackFiles,
  selfTest,
  FEEDBACK_DIR,
  EXCLUDED,
};
