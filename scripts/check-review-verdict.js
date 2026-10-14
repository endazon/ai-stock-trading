#!/usr/bin/env node
/*
 * AI レビューの判定投稿チェッカー
 *
 * **「緑だが検査されていない」を止める。**
 *
 * 適用範囲: claude-code-action（claude-code-review）の投稿挙動専用である。reviewer スロットを
 *   他エンジンへ差し替えた場合は対象外であり、**対象外は「検査済み」を意味しない** —— 同等の
 *   安全弁は差し替える側の責務である（docs/ai-orchestration.md §6）。
 *
 * claude-code-action は、AI が判定（🔴 重大 / 🟡 推奨 / 🟢 軽微）を投稿しないまま
 * ターンを終えても `success` で終わる。ジョブは緑になり、PR には進行中の
 * プレースホルダだけが残る。**「レビュー済み・指摘なし」と読まれるが、実際には
 * 何も判定されていない。マージも止まらない。**
 *
 * 実測（planning#333。実装 endazon/ai-stock-trading#489 / endazon/ai-stock-trading#490 / `IADR-0190`）:
 * 同一 PR で 3 回連続、判定の投稿が無かった。
 *
 *   | 試行 | 実行時間 | 結論      | 判定 | 形                        |
 *   | ---: | -------- | --------- | ---- | ------------------------- |
 *   | 1    | 3m55s    | success   | 無し | A: 待たずにターンを終える |
 *   | 2    | 20m16s   | cancelled | 無し | B: 待って job timeout     |
 *   | 3    | 11m16s   | success   | 無し | A                         |
 *
 * **形 A の green のまま PR がマージされた。** B は少なくともマージを止めるが、
 * A は何の signal も出さない。ここで止める。
 *
 * **`check-permission-denials.js` では捕まらない。** あちらは「AI がツールを 1 つも
 * 実行できなかった」形を見る。本件は**ツールは正常に動いており、最後の投稿だけが
 * 無い** —— 別の穴である。入力（実行ログ）の形は同じで `parseEvents` を借りるため、
 * **2 本セットで配布すること。**
 *
 * **ただし配線先は、判定の書式を `prompt:` で強制しているレビュー用ワークフローに限る。**
 * 実装依頼へ応答するワークフロー（`claude-coding`）は `prompt:` を持たず**判定を投稿しない**
 * ため、配線すると**正常に完了した実装タスクでもこのステップだけが恒常的に赤くなる**。
 * `ALLOW_MISSING_VERDICT=1` で黙らせるのは解にならない —— 常に警告だけ出すステップは
 * **本検査器がまさに止めようとしている形**（緑だが検査されていない）そのものである。
 * 実測（planning#355）: 「2 本セット」を「配線も 2 本」と読んだ誤配線が 1 件起きた。
 *
 * **検証の下限（#1315 / IADR-0190 決定 7）も同じ本文から読む。** プロンプトの【検証の下限】は
 * 「変更ファイルを全部読む」「helm の差分なら helm lint / helm template」「scripts/ .github/ の差分なら
 * scripts.test.js」等を求めるが、マージ後の抜き取りで 2 本の違反が出た（#1324: helm と .github の差分で
 * どちらも未実行 / #1336: 32 ファイル中 18 ファイルしか読まず「下限は満たしていません」と自己申告）。
 * 判定が揃っていても**下限を欠いたレビューは「緑だが検査されていない」と同型**なので、ここで落とす。
 *   (a) 本文の「変更 N ファイル中 M ファイルを読んだ」の申告が無い、または M + K < N（K は生成物の除外数）
 *   (b) 同じ行に「下限」と「満たして(い)ない / 満たさない」がある（自己申告の違反）。
 *       あわせて、(a) の申告を満たしながら、冒頭か「確認できたこと」の節で「全行は読んでいない」「行単位では読んでいない」
 *       「名前と範囲の確認にとどまる」「本文を精読せず」「要点だけ確認」等と書いている（読んだと申告したファイルを精読して
 *       いない自認。#1340 / #1344 の同型 2 回。生成物 K>0 の除外を申告した生成物だけの文と、計画書についての文は数えない）
 *   (c) 変更ファイル（env CHANGED_FILES）に deploy/helm/** があるのに本文に helm lint / helm template を
 *       実行した記載が無い、scripts/** か .github/** があるのに scripts.test.js を実行した記載が無い
 *       （「実行した記載」は「確認できたこと」の節に在ること。🔍 実行できなかったこと の節に在るだけなら
 *       記載が無いとみなす。#1324 の実文がその形だった）
 *
 * 使い方:
 *   node scripts/check-review-verdict.js <execution-file> [--self-test]
 *
 * 環境変数:
 *   ALLOW_MISSING_VERDICT=1   判定が無くても失敗させない（警告のみ。緊急避難用。**下限の判定には効かない**）
 *   CHANGED_FILES             PR の変更ファイル（改行区切り）。未設定・空なら (c) だけを skip する（warn で明示）
 *
 * 終了コード: 判定が揃っていない・下限に反した=1 / 揃っている=0 / 実行ログを読めない=0（fail-open）。
 */
'use strict';
const fs = require('fs');
const { emit, warn } = require('./lib/ci-annotate.js');
const { parseEvents } = require('./check-permission-denials.js');

/**
 * 判定の見出し（置換点ではない。**プロンプトの「出力フォーマット」と対にして直す**）。
 *
 * **絵文字と語の両方を要求する。** 語だけで探すと、レビューが本文中でその語を使った
 * 瞬間に誤検出する —— 「**重大**な問題は無い」という散文で緑になってしまう。これは
 * planning#319 知見 3 で実証済みの同型のアンチパターンである（検査器について書いた
 * 記録が、語の一致だけで自己発火した）。**見出しの構造で見る。**
 */
const VERDICTS = [
  { emoji: '🔴', word: '重大' },
  { emoji: '🟡', word: '推奨' },
  { emoji: '🟢', word: '軽微' },
];

/**
 * **判定はコメント投稿ツールの入力に載る。** レビューはサマリを地の文として出力するのではなく、
 * `mcp__github_comment__update_claude_comment` 等でコメントを更新する形で投稿する。
 * よって assistant の text ブロックだけを見ると**判定が在っても「無い」と読む**
 * （実測: planning#341 で本検査器自身が偽陽性で落ちた）。
 *
 * **ただし tool_use を無条件に含めてはならない。** `Bash(grep '### 🔴 重大' …)` のような
 * 入力で緑になってしまう。**コメント投稿ツールに限って**本文フィールドを読む。
 */
const COMMENT_TOOLS = [
  'mcp__github_comment__update_claude_comment',
  'mcp__github__add_issue_comment',
  'mcp__github__create_pull_request_review',
  'mcp__github__add_comment_to_pending_review',
];

/** コメント投稿ツールの入力から本文らしき文字列を取り出す。 */
function commentBodyOf(block) {
  const name = block && (block.name || block.tool_name);
  if (!name || !COMMENT_TOOLS.includes(name)) return '';
  const input = block.input || block.tool_input || {};
  const parts = [];
  for (const key of ['body', 'content', 'text', 'comment']) {
    if (typeof input[key] === 'string') parts.push(input[key]);
  }
  return parts.join('\n');
}

/**
 * 実行ログから AI の出力テキストを集める。
 *
 * 次の 3 つを見る。**どれか 1 つだけだと action の出力形式が変わったときに黙って通る。**
 *   1. `assistant` イベントの text ブロック（地の文で出す場合）
 *   2. **コメント投稿ツールの入力**（実際の投稿経路。上記のとおり本命である）
 *   3. `result` イベントの最終出力
 */
function collectAssistantText(events) {
  const parts = [];
  for (const e of events) {
    if (!e) continue;
    if (e.type === 'assistant') {
      const content = e.message && e.message.content;
      if (Array.isArray(content)) {
        for (const b of content) {
          if (b && b.type === 'text' && typeof b.text === 'string') parts.push(b.text);
          else if (b && b.type === 'tool_use') {
            const body = commentBodyOf(b);
            if (body) parts.push(body);
          }
        }
      }
    } else if (e.type === 'result' && typeof e.result === 'string') {
      parts.push(e.result);
    }
  }
  return parts.join('\n');
}

/**
 * 判定の見出しが在るかを返す。
 *
 * 見出しレベル（`##`〜`####`）は問わない。**絵文字と語の距離も問わない**
 * （`### 🔴 重大` / `### 🔴 重大（新規）` のどちらも拾う）。
 * ただし**同じ行に絵文字と語の両方が要る**。
 */
function findVerdicts(text) {
  const found = [];
  const missing = [];
  const lines = String(text || '').split('\n');
  for (const v of VERDICTS) {
    const hit = lines.some((line) => {
      if (!/^\s{0,3}#{2,4}\s/.test(line)) return false;
      return line.includes(v.emoji) && line.includes(v.word);
    });
    (hit ? found : missing).push(v);
  }
  return { found, missing };
}

/**
 * 下限の判定に使う「レビュー本文」を選ぶ（#1315）。
 *
 * **集めた全文では判定しない。** 途中の独り言（「下限を満たしていないので読み足す」）や、進行中の
 * プレースホルダの更新で赤くなる。候補（コメント投稿ツールの入力・assistant の text・result）のうち
 * **判定の見出しを 1 つでも含む最後のもの**を本文とする。無ければ集めた全文で代える（その場合は
 * 判定の見出しの検査がすでに落ちている）。
 */
function selectReviewBody(events) {
  const candidates = [];
  for (const e of events) {
    if (!e) continue;
    if (e.type === 'assistant') {
      const content = e.message && e.message.content;
      if (!Array.isArray(content)) continue;
      for (const b of content) {
        if (b && b.type === 'text' && typeof b.text === 'string') candidates.push(b.text);
        else if (b && b.type === 'tool_use') {
          const body = commentBodyOf(b);
          if (body) candidates.push(body);
        }
      }
    } else if (e.type === 'result' && typeof e.result === 'string') {
      candidates.push(e.result);
    }
  }
  for (let i = candidates.length - 1; i >= 0; i -= 1) {
    if (findVerdicts(candidates[i]).found.length > 0) return candidates[i];
  }
  return candidates.join('\n');
}

/** 全角数字・全角括弧・全角空白を半角へ寄せる（「９ ファイル中 ９ ファイル」を同じに読む）。 */
function normalize(text) {
  return String(text || '').normalize('NFKC');
}

const UNIT = '(?:ファイル|件)';

/**
 * 「変更 N ファイル中 M ファイルを読んだ」の申告を読む。
 *
 * 母集合は #1318〜#1336 のレビューコメントで実在した書式（作業仕様書 20261013_1315_review-floor-check の表）:
 *   - `変更 9 ファイル中 9 ファイルを読んだ` / `… 14 ファイルの差分を読んだ` / `… 15 ファイルの差分を確認した`
 *   - `変更 32 ファイル中、…。**全文または差分を読んだのは 18 ファイル**です。`
 *   - `変更 38 ファイル中、実装 15 ファイル（…）の差分を読みました` / `… 15 ファイルは差分を読みました`
 * N を見つけたら、**同じ段落（次の空行まで）**から M と生成物の除外数 K を探す。
 * M が無い（「主要ファイルは読んだ」「19 ファイルを対象にした」等）は**申告なし**として扱う。
 *
 * @returns {{ n: number, m: number|null, k: number } | null} N も無ければ null
 */
function parseReadDeclaration(text) {
  // **冒頭（最初の判定の見出しより前）だけを見る。** 申告は本文の冒頭に書く決まりであり、
  // 指摘の本文に現れる「変更 1 件」等の別の数を N と取り違えない（#1337 の AI レビュー 🟢）。
  const all = normalize(text);
  const firstVerdict = all.search(/^\s{0,3}#{2,4}\s.*(?:🔴|🟡|🟢)/mu);
  const t = firstVerdict < 0 ? all : all.slice(0, firstVerdict);
  // 「変更 N ファイル中 / のうち」の形を優先し、無ければ「変更 N ファイル」で代える（#1329 の
  // 「変更 21 ファイル（`git diff …`）中」のように、間に括弧が挟まる実文も前者で拾う）。
  const head =
    new RegExp(`変更\\s*(\\d+)\\s*${UNIT}\\s*(?:\\([^)\\n]*\\))?\\s*(?:中|のうち)`).exec(t) ||
    new RegExp(`変更\\s*(\\d+)\\s*${UNIT}`).exec(t);
  if (!head) return null;
  const n = Number(head[1]);
  const rest = t.slice(head.index + head[0].length);
  const blank = rest.search(/\n\s*\n/);
  const seg = blank < 0 ? rest : rest.slice(0, blank);

  let m = null;
  const byWho = new RegExp(`読んだ(?:の|ファイル)は\\s*\\**\\s*(\\d+)\\s*${UNIT}`).exec(seg);
  const byCount = new RegExp(
    `(\\d+)\\s*${UNIT}\\s*(?:\\([^)\\n]*\\))?\\s*[のはを]?\\s*(?:すべて|全て|全部)?\\s*` +
      '(?:(?:全文|差分|本文|内容)(?:(?:または|か|と)(?:全文|差分))?\\s*[をでの]?\\s*)?' +
      '(?:読|精読|確認|目視)'
  ).exec(seg);
  // 先に現れた方を採る（「18 ファイルは確認、読んだのは 10 ファイル」のような併記で後ろを拾わない）
  const hits = [byWho, byCount].filter(Boolean).sort((a, b) => a.index - b.index);
  if (hits.length) m = Number(hits[0][1]);

  const ex = new RegExp(`生成物\\s*(\\d+)\\s*${UNIT}\\s*(?:\\([^)\\n]*\\))?\\s*を?\\s*(?:除外|除く|除い)`).exec(seg);
  const k = ex ? Number(ex[1]) : 0;
  return { n, m, k };
}

/**
 * 「満たしていない」の活用形（常体・敬体・可能形）。#1336 の実文は「満たしていません」（敬体）である。
 * 満たして(い)ない / 満たして(い)ません / 満たさない / 満たしません / 満たせ(てい)ない / 満たせ(てい)ません
 */
const NOT_MET = '(?:満たし(?:て(?:い)?)?(?:ない|ません)|満たさない|満たせ(?:て(?:い)?)?(?:ない|ません))';

/** (b) 下限を満たしていないという自己申告の行を返す。 */
function findFloorAdmissions(text) {
  return normalize(text)
    .split('\n')
    // 引用（インラインコード・「」・『』）の中は数えない。検査器そのものを論じるレビュー（本検査器の PR 等）が
    // 「下限…満たしていない」を引用しただけで自己発火する（planning#319 知見 3 と同型）のを防ぐ。
    .map((line) => line.replace(/`[^`]*`/g, '').replace(/「[^」]*」/g, '').replace(/『[^』]*』/g, ''))
    .filter((line) => line.includes('下限'))
    .filter((line) => new RegExp(NOT_MET).test(line))
    // 「満たしていない項目は無い」は否定の否定（違反ではない）
    .filter((line) => !new RegExp(`${NOT_MET}(?:もの|項目|点|検証)は(?:無|な)(?:い|し)`).test(line))
    .map((line) => line.trim());
}

/**
 * (b) の続き: 読んだと申告したファイルを**精読していない**自認の表記揺れ（#1315 の追加の是正）。
 *
 * #1340（3 回目）と #1344（最終）は冒頭で N/N を申告しながら、本文で「行単位では読んでいない」「全行は読んでいない」と
 * 認めていた。(a) の数は満たすので緑だった。母集合は #1318〜#1345 の AI レビューの実文
 * （作業仕様書 20261014_1315_review-floor-not-read-declaration の表）。
 * 「全文ではなく差分を読んだ」は**数えない** —— 「読んだ」の定義は差分を行単位で読むことである。
 */
const NOT_READ_PATTERNS = [
  /全行(?:は|を|まで)?(?:読|精読|目視)[^。\n]{0,6}?(?:ない|ません|おらず|せず)/, // #1344「全行は読んでいない」/ #1328「全行は精読していない」
  /行単位(?:で|では|の)?は?(?:読|精読)[^。\n]{0,6}?(?:ない|ません|おらず|せず)/, // #1340「行単位では読んでいない」
  /精読[^。\n]{0,10}?(?:せず|しておらず|して(?:い)?(?:ない|ません)|行って(?:い)?(?:ない|ません)|未実施)/, // #1336「本文を精読せず」/ #1324「精読までは行っていない」
  /名前と範囲の確認|名前(?:を|の)一覧|名前だけ/, // #1340「名前と範囲の確認にとどまり」/ #1344「名前を一覧し」
  /要点[^。\n]{0,12}?(?:だけ|のみ)/, // #1336「要点だけ確認」「差分の要点（列の追加）だけを確認」
  /検査器の(?:結果|出力)(?:だけ|のみ)/, // #1336「検査器の結果だけで確認」/ #1340「検査器の出力だけで見た」
  /確認(?:に(?:とどま|留ま)|まで(?:で|である|だ|にとどま))/, // #1340「確認にとどまり」/ #1326「確認までである」
  /未読/, // #1336 / #1340（2 回目）
  /差分を読ま(?:ず|ない)|差分を読んで(?:い)?(?:ない|ません)/, // #1340（2 回目）
  /読めて(?:い)?(?:ない|ません)/, // #1318「全文は読めていない」（出力が切り詰められた）
  /しか(?:目視|読|確認|見)[^。\n]{0,6}?(?:ない|ません)/, // #1318「先頭部分しか目視していない」
  /流し読み|斜め読み|拾い読み|ざっと(?:読|目を通|見)/,
];

/** 否定の否定（「未読のファイルは無い」「精読していないものはない」）。一致の直後に続く形で読む。 */
const NOT_READ_NEGATED = /^(?:の)?(?:ファイル|もの|箇所|部分|項目)?(?:は|も)?(?:無|な)(?:い|し|く)/;

/** 生成物の名前（除外してよいもの）。プロンプトの【検証の下限】の例と対にして直す。 */
const GENERATED_TOKENS =
  /[\w.*/-]*Designer\.cs|[\w.*/-]*ModelSnapshot(?:\.cs)?|messages\.(?:ts|po)|CHANGELOG\.md|package-lock\.json|openapi\.yaml/g;
const GENERATED_WORD = /生成物|自動生成|Designer|ModelSnapshot/;
/** 生成物の名前を除いた後に残っていれば「生成物以外も並べた文」と読む語（テスト・文書・本体など）。 */
const NON_GENERATED_TARGET = /テスト|Tests?\b|docs|文書|仕様|IADR|ADR|本体|README|helm|workflow|ワークフロー|\.(?:cs|tsx?|jsx?|ya?ml|md|json|sh)\b/i;
/** 計画書（読む対象のファイルではない）についての文。 */
const PLAN_DOC = /計画書|計画\s*ADR|計画リポ(?:ジトリ)?/g;

/** 引用（インラインコード・「」・『』）を外す。検査器そのものを論じるレビューで自己発火しないため。 */
function stripQuotes(s) {
  return s.replace(/`[^`]*`/g, '').replace(/「[^」]*」/g, '').replace(/『[^』]*』/g, '');
}

/** 冒頭（最初の判定の見出しより前）。parseReadDeclaration と同じ切り方。 */
function preambleOf(text) {
  const all = normalize(text);
  const firstVerdict = all.search(/^\s{0,3}#{2,4}\s.*(?:🔴|🟡|🟢)/mu);
  return firstVerdict < 0 ? all : all.slice(0, firstVerdict);
}

/**
 * (b) の続き: 冒頭と「確認できたこと」の節から、精読していない自認の文を返す。
 *
 * @param {string} text  レビュー本文
 * @param {{ k: number }|null} decl  冒頭の申告（生成物の除外数 K を使う）
 * @returns {{ sentence: string, generated: boolean }[]}
 */
function findNotReadAdmissions(text, decl) {
  const k = decl ? decl.k : 0;
  const scope = `${preambleOf(text)}\n${confirmedSections(normalize(text))}`;
  const out = [];
  for (const raw of scope.split(/\n|(?<=。)/)) {
    const sentence = raw.trim();
    if (!sentence) continue;
    const bare = stripQuotes(sentence);
    const hit = NOT_READ_PATTERNS.some((re) => {
      const m = re.exec(bare);
      return m && !NOT_READ_NEGATED.test(bare.slice(m.index + m[0].length));
    });
    if (!hit) continue;
    // 計画書は読む対象のファイルではない（🔍 の「計画書は未検証」と同じ扱い）。ただし計画書とテスト等を並べた文は数える
    // （生成物と同じ規則。#1346 の AI レビュー 🟢）
    if (new RegExp(PLAN_DOC.source).test(bare) && !NON_GENERATED_TARGET.test(bare.replace(PLAN_DOC, ''))) continue;
    const generated = GENERATED_WORD.test(sentence) || new RegExp(GENERATED_TOKENS.source).test(sentence);
    if (generated && k > 0) {
      const rest = sentence.replace(GENERATED_TOKENS, '').replace(/生成物|自動生成/g, '');
      if (!NON_GENERATED_TARGET.test(rest)) continue; // 生成物だけの文で、冒頭に K>0 の除外を申告している
    }
    out.push({ sentence, generated });
  }
  return out;
}

/**
 * 「実行した」を**本文の節で**読む（#1337 の AI レビュー 🟡 による是正）。
 *
 * 初版は行内の否定語（「拒否」「省略」等）で「実行していない」行を除いていたが、語が広く、
 * 「`scripts.test.js` 全件成功（権限拒否なし）」のような**実行した行まで未実行と読む偽陽性**が出た。
 * **行内の否定語には頼らない。** プロンプトは下限の結果を「確認できたこと」に、実行しなかったものを
 * 「🔍 実行できなかったこと」に書かせているので、**前者の節にコマンド名があれば実行した**とみなす。
 * 後者の節（#1324 の「`helm lint` / `helm template`: 実行していない」はここに在った）や指摘の節に
 * 在るだけでは、実行した記載とみなさない。
 *
 * 見出しの母集合（#1318〜#1336 の 32 本。全本に 1 つずつ在った）:
 *   `### 確認できたこと` / `### ✅ 確認できたこと（このセッションで実行）` /
 *   `### 確認できたこと（実行した）` / `### ✅ 確認できたこと(このセッションで実走)` ほか括弧内の揺れ。
 *   未実行の節は 32 本とも `### 🔍 実行できなかったこと`。
 */
const CONFIRMED_HEADING = /確認できたこと/;
const NOT_RUN_HEADING = /🔍|できなかった/;

/**
 * 本文を見出し（`##`〜`####`）で節に分け、「確認できたこと」の節の本文だけをつないで返す。
 *
 * **節を開いた見出しより深い見出し（`### 確認できたこと` の下の `#### 検査器` 等）では閉じない。**
 * 同じ深さ以浅の見出し（`### 🔍 実行できなかったこと` 等）で閉じる（#1337 の再レビュー 🟡。
 * 小見出しで閉じると、その下の実行記載を読まずに偽陽性になる）。
 */
function confirmedSections(text) {
  const out = [];
  let depth = 0; // 開いている「確認できたこと」の見出しの深さ（0 = 節の外）
  for (const line of String(text || '').split('\n')) {
    const h = /^\s{0,3}(#{2,4})\s/.exec(line);
    if (h) {
      const level = h[1].length;
      if (depth && level > depth) continue; // 節の中の小見出し
      depth = CONFIRMED_HEADING.test(line) && !NOT_RUN_HEADING.test(line) ? level : 0;
      continue;
    }
    if (depth) out.push(line);
  }
  return out.join('\n');
}

/** 「確認できたこと」の節に、そのコマンド名が在るか。 */
function mentionsRun(text, cmd) {
  return confirmedSections(text).includes(cmd);
}

/** CHANGED_FILES（改行区切り）を配列にする。空行は捨てる。 */
function parseChangedFiles(raw) {
  return String(raw || '')
    .split('\n')
    .map((s) => s.trim())
    .filter(Boolean);
}

/**
 * 下限の違反を列挙する（#1315 / IADR-0190 決定 7）。
 *
 * @param {string} body  レビュー本文（selectReviewBody の結果）
 * @param {string[]|null} changedFiles  変更ファイル。null なら (c) を判定しない
 * @returns {string[]} 違反ごとの 1 行
 */
function findFloorViolations(body, changedFiles) {
  const problems = [];
  const text = normalize(body);

  const decl = parseReadDeclaration(text);
  if (!decl || decl.m === null) {
    problems.push(
      '下限「変更ファイルは全部読む」: 本文に「変更 N ファイル中 M ファイルを読んだ」の申告が無い' +
        (decl ? `（N=${decl.n} は在るが、読んだ件数 M が数字で書かれていない）` : '')
    );
  } else if (decl.m + decl.k < decl.n) {
    problems.push(
      `下限「変更ファイルは全部読む」: 変更 ${decl.n} ファイル中 ${decl.m} ファイルしか読んでいない` +
        (decl.k ? `（生成物の除外 ${decl.k} を足しても ${decl.m + decl.k}）` : '')
    );
  }

  for (const line of findFloorAdmissions(text)) {
    problems.push(`下限の自己申告: 本文が下限を満たしていないと書いている —— 「${line.slice(0, 120)}」`);
  }

  // 申告の数を満たすときだけ見る（満たさなければ上の (a) がすでに赤。同じ欠落を 2 度数えない）
  if (decl && decl.m !== null && decl.m + decl.k >= decl.n) {
    for (const { sentence, generated } of findNotReadAdmissions(text, decl)) {
      problems.push(
        `下限「変更ファイルは全部読む」の自己申告: ${decl.m} ファイルを読んだと申告したが、本文が精読していないと書いている —— 「${sentence.slice(0, 120)}」` +
          (generated && !decl.k ? '（生成物なら、冒頭で「生成物 K ファイルを除外」と申告して M から外す）' : '')
      );
    }
  }

  if (changedFiles) {
    if (changedFiles.some((f) => f.startsWith('deploy/helm/'))) {
      for (const cmd of ['helm lint', 'helm template']) {
        if (!mentionsRun(text, cmd)) {
          problems.push(`下限「deploy/helm/** の差分があれば ${cmd}」: 本文に「${cmd}」を実行した記載が無い`);
        }
      }
    }
    const touchedScripts = changedFiles.filter((f) => f.startsWith('scripts/') || f.startsWith('.github/'));
    if (touchedScripts.length && !mentionsRun(text, 'scripts.test.js')) {
      problems.push(
        `下限「scripts/ か .github/ の差分があれば scripts.test.js 全件」: 本文に「scripts.test.js」を実行した記載が無い（該当 ${touchedScripts.length} ファイル）`
      );
    }
  }
  return problems;
}

function selfTest() {
  const assert = require('assert');
  const ok = (name, fn) => {
    fn();
    console.log(`  ok  ${name}`);
  };
  let n = 0;
  const T = (name, fn) => {
    n += 1;
    ok(name, fn);
  };

  const full = '## AI コードレビュー結果\n\n### 🔴 重大\n- なし\n\n### 🟡 推奨\n- なし\n\n### 🟢 軽微\n- なし\n';

  T('判定 3 種が揃っていれば missing は空', () => {
    assert.deepStrictEqual(findVerdicts(full).missing, []);
  });

  T('1 つ欠けると検出する', () => {
    const t = full.replace('### 🟢 軽微', '### 軽微');
    assert.strictEqual(findVerdicts(t).missing.length, 1);
    assert.strictEqual(findVerdicts(t).missing[0].word, '軽微');
  });

  T('判定を 1 つも投稿しない形 A を検出する', () => {
    const t = '`dotnet test` をバックグラウンドで実行中です。完了後にサマリを投稿します。';
    assert.strictEqual(findVerdicts(t).missing.length, 3);
  });

  T('本文に語だけあっても緑にしない（散文で発火しない）', () => {
    const t = '## レビュー結果\n\n重大な問題は無い。推奨も軽微も特に無い。\n';
    assert.strictEqual(findVerdicts(t).missing.length, 3);
  });

  T('絵文字だけ・語だけの見出しでは通さない', () => {
    assert.strictEqual(findVerdicts('### 🔴\n### 🟡\n### 🟢\n').missing.length, 3);
    assert.strictEqual(findVerdicts('### 重大\n### 推奨\n### 軽微\n').missing.length, 3);
  });

  T('見出しレベルは ## 〜 #### を許す', () => {
    const t = '## 🔴 重大\n### 🟡 推奨\n#### 🟢 軽微\n';
    assert.deepStrictEqual(findVerdicts(t).missing, []);
  });

  T('見出しでない行に絵文字と語があっても拾わない', () => {
    const t = '- 🔴 重大 の指摘は無い\n- 🟡 推奨 も無い\n- 🟢 軽微 も無い\n';
    assert.strictEqual(findVerdicts(t).missing.length, 3);
  });

  T('見出しの後ろに補足が付いても拾う', () => {
    const t = '### 🔴 重大（新規）\n### 🟡 推奨\n### 🟢 軽微\n';
    assert.deepStrictEqual(findVerdicts(t).missing, []);
  });

  T('assistant イベントの text を集める', () => {
    const events = [{ type: 'assistant', message: { content: [{ type: 'text', text: full }] } }];
    assert.deepStrictEqual(findVerdicts(collectAssistantText(events)).missing, []);
  });

  T('result イベントの最終出力も見る（assistant が空でも拾う）', () => {
    const events = [{ type: 'result', result: full }];
    assert.deepStrictEqual(findVerdicts(collectAssistantText(events)).missing, []);
  });

  T('コメント投稿ツールの入力から判定を拾う（実際の投稿経路）', () => {
    for (const name of COMMENT_TOOLS) {
      const events = [
        { type: 'assistant', message: { content: [{ type: 'tool_use', id: 'x', name, input: { body: full } }] } },
      ];
      assert.deepStrictEqual(findVerdicts(collectAssistantText(events)).missing, [], name);
    }
  });

  T('コメント投稿以外の tool_use は数えない（Bash の引数で緑にしない）', () => {
    const events = [
      {
        type: 'assistant',
        message: { content: [{ type: 'tool_use', id: 'x', name: 'Bash', input: { command: `grep '${full}'` } }] },
      },
    ];
    assert.strictEqual(findVerdicts(collectAssistantText(events)).missing.length, 3);
  });

  const review = (head) => `## AI コードレビュー結果\n\n${head}\n\n### 🔴 重大\n- なし\n\n### 🟡 推奨\n- なし\n\n### 🟢 軽微\n- なし\n`;

  T('下限: N 中 N を読んだ申告は緑', () => {
    assert.deepStrictEqual(findFloorViolations(review('変更 9 ファイル中 9 ファイルを読んだ(生成物の除外なし)。'), []), []);
  });

  T('下限: 読んだのは M ファイル（M < N）は赤', () => {
    const v = findFloorViolations(review('変更 32 ファイル中、生成物の除外なし。**全文または差分を読んだのは 18 ファイル**です。'), []);
    assert.strictEqual(v.length, 1);
    assert.match(v[0], /32 ファイル中 18/);
  });

  T('下限: 申告が無いのは赤', () => {
    assert.strictEqual(findFloorViolations(review('差分を読んだ。'), []).length, 1);
  });

  T('下限: 「下限は満たしていません」は赤', () => {
    const v = findFloorViolations(review('変更 2 ファイル中 2 ファイルを読んだ。\n\n下限（全ファイルを読む）は満たしていません。'), []);
    assert.strictEqual(v.length, 1);
  });

  T('下限: helm 差分で helm lint / helm template が無ければ赤', () => {
    const body = review('変更 1 ファイル中 1 ファイルを読んだ。');
    assert.strictEqual(findFloorViolations(body, ['deploy/helm/ast/values.yaml']).length, 2);
    const ok = `${body}\n### 確認できたこと\n- helm lint: OK\n- helm template: OK`;
    assert.deepStrictEqual(findFloorViolations(ok, ['deploy/helm/ast/values.yaml']), []);
  });

  T('下限: 🔍 の節に在るだけでは実行した記載とみなさない（#1324 の形）', () => {
    const body = review('変更 2 ファイル中 2 ファイルを読んだ。') + '\n### 🔍 実行できなかったこと\n- `helm lint` / `helm template`: 実行していない。\n- `scripts.test.js`: 実行していない。\n';
    assert.strictEqual(findFloorViolations(body, ['deploy/helm/a/values.yaml', '.github/workflows/helm.yml']).length, 3);
  });

  T('下限: N/N を申告しつつ「行単位では読んでいない」と書けば赤、🔍 の節や「差分を読んだ」は緑（#1340 / #1344）', () => {
    const head = '変更 3 ファイル中 3 ファイルを読んだ（生成物の除外なし）。';
    assert.strictEqual(findFloorViolations(review(`${head}テストは名前と範囲の確認にとどまり、行単位では読んでいない。`), []).length, 1);
    assert.strictEqual(findFloorViolations(review(`${head}\n- テスト 2 本は全行は読んでいない。`), []).length, 1);
    assert.deepStrictEqual(findFloorViolations(review(`${head}全文ではなく差分を読んだ。`), []), []);
    const tail = '\n### 🔍 実行できなかったこと\n- 計画書との照合は未検証。\n- 本体以外の行単位の精読は行っていない。\n';
    assert.deepStrictEqual(findFloorViolations(review(head) + tail, []), []);
  });

  T('下限: 生成物だけを精読しない文は K>0 の申告があれば緑、K の申告が無ければ赤', () => {
    const line = '`Designer.cs` と `ModelSnapshot` は生成物なので読んでいない。要点だけ確認した `Designer.cs` もある。';
    assert.deepStrictEqual(findFloorViolations(review(`変更 5 ファイル中 3 ファイルを読んだ（生成物 2 ファイルを除外）。${line}`), []), []);
    assert.strictEqual(findFloorViolations(review(`変更 5 ファイル中 5 ファイルを読んだ（生成物の除外なし）。${line}`), []).length, 1);
  });

  T('下限: 判定の見出しを含む最後の候補を本文に選ぶ（独り言で赤くしない）', () => {
    const events = [
      { type: 'assistant', message: { content: [{ type: 'text', text: '下限を満たしていないので読み足す。' }] } },
      { type: 'result', result: review('変更 3 ファイル中 3 ファイルを読んだ。') },
    ];
    assert.deepStrictEqual(findFloorViolations(selectReviewBody(events), []), []);
  });

  console.log(`[check-review-verdict] self-test OK（${n} 件）`);
}

function main(argv) {
  const args = argv.slice(2);
  if (args.includes('--self-test')) {
    selfTest();
    return;
  }
  const file = args.find((a) => !a.startsWith('--'));

  // **実行ログを読めないときは fail-open。** その形（AI がそもそも動かなかった等）は
  // `check-permission-denials.js` が捕まえるため、二重に落とす必要は無い。
  if (!file) {
    warn('[check-review-verdict] 実行ログのパスが渡されていないため skip する（fail-open）。');
    process.exit(0);
  }
  let text;
  try {
    text = fs.readFileSync(file, 'utf8');
  } catch (e) {
    warn(`[check-review-verdict] 実行ログを読めないため skip する（fail-open）: ${file}`);
    process.exit(0);
  }

  const events = parseEvents(text);
  const { missing } = findVerdicts(collectAssistantText(events));

  // 検証の下限（#1315 / IADR-0190 決定 7）。判定の有無とは独立に見て、違反は 1 行ずつ ::error で出す。
  // **ALLOW_MISSING_VERDICT は下限には効かない**（判定の未投稿の緊急避難であり、下限を省く口実にしない）。
  let floorFailed = false;
  if (events.length > 0) {
    const changed = parseChangedFiles(process.env.CHANGED_FILES);
    if (changed.length === 0) {
      warn(
        '[check-review-verdict] CHANGED_FILES が未設定または空のため、差分の種類に応じた下限' +
          '（helm lint / helm template / scripts.test.js）の判定だけを skip する（fail-open）。'
      );
    }
    const floor = findFloorViolations(selectReviewBody(events), changed.length ? changed : null);
    for (const p of floor) emit('error', `[check-review-verdict] 検証の下限に反している —— ${p}`);
    if (floor.length) {
      floorFailed = true;
      emit(
        'error',
        `[check-review-verdict] AI レビューが検証の下限（#1315）を ${floor.length} 件満たしていない。` +
          '判定が揃っていても下限を欠いたレビューは検査済みと読めないため、レビューを再実行してください。'
      );
    } else {
      console.log('[check-review-verdict] OK: 検証の下限（全ファイルの読了・差分に応じた検証の記載）を満たしています。');
    }
  }

  if (missing.length === 0) {
    console.log('[check-review-verdict] OK: 判定（🔴 重大 / 🟡 推奨 / 🟢 軽微）が投稿されています。');
    process.exit(floorFailed ? 1 : 0);
  }

  const list = missing.map((v) => `${v.emoji} ${v.word}`).join(' / ');
  const message =
    `[check-review-verdict] AI レビューが判定を投稿していません（欠けている見出し: ${list}）。` +
    'ジョブは success でも**内容は検査されていない**。' +
    'PR に残ったコメントを確認し、レビューを再実行してください。' +
    '判定を投稿せずに終わる原因は、バックグラウンド実行やサブエージェントの結果待ちである。';

  if (process.env.ALLOW_MISSING_VERDICT === '1') {
    warn(`${message}（ALLOW_MISSING_VERDICT=1 のため失敗としない）`);
    process.exit(floorFailed ? 1 : 0);
  }
  emit('error', message);
  process.exit(1);
}

if (require.main === module) {
  main(process.argv);
}

module.exports = {
  collectAssistantText,
  commentBodyOf,
  findVerdicts,
  selectReviewBody,
  parseReadDeclaration,
  findFloorAdmissions,
  findNotReadAdmissions,
  findFloorViolations,
  parseChangedFiles,
  confirmedSections,
  selfTest,
  VERDICTS,
  COMMENT_TOOLS,
};
