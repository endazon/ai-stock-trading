#!/usr/bin/env node
'use strict';
/*
 * check-ci-latency.js
 * 「逆転」——`build-and-test` が `claude-review` の下限を追い越したことを検知する（IADR-0208 決定 9）。
 *
 * 背景:
 *   PR の待ち時間は必須チェックの最大値で決まる。実測（2026-08-21）では
 *     claude-review: 最小 148 秒 / 中央 339 秒（差分量に比例する）
 *     build-and-test: 241 秒
 *   であり、**既に逆転していた**。
 *
 *   🔴 **中央値ではなく「下限」と比べる。** 密なループでは PR が小さく、レビューは下限へ寄る。
 *   下限を下回っている間、ビルド時間の増加は**レビューの陰に隠れて見えない**。
 *   下限を超えた瞬間から、**増加は 1 秒残らず待ち時間になる**。
 *
 *   ビルド時間は機能追加とともに連続的に伸びるため、**いつ超えたか誰も気付かない**。
 *   これは自動起票や fail-closed の門と同じ「**気付けない種類の劣化**」であり、検知を機械に任せる。
 *
 * 方式:
 *   直近のマージ済み PR について、**その PR の check 群が動き出した時刻からの完了までの経過**を
 *   GitHub API で引き、
 *     監視対象（既定 build-and-test）の**中央値**  >  基準（既定 claude-review）の**最小値**
 *   なら exit 1。
 *
 *   🔴 **check-run 自身の所要（completed_at - started_at）で測らない。** 実データで踏んだ罠である ——
 *   `build-and-test` は matrix の脚を `needs:` で束ねる**集約ジョブ**であり、
 *   自身の所要は **15 秒**しかない（実測。MSP#894 の head）。脚の 111 秒はそこに現れない。
 *   待ち時間として意味を持つのは **check 群の開始からの完了オフセット（同 126 秒）**である。
 *   所要で測ると、集約ジョブは実際の 1/8 に見え、**監視が永久に鳴らない**。
 *
 *   🔴 **しきい値を固定値で持たない（自己校正）。** 基準側も同じ run 群から実測する。
 *   固定値は環境とコードベースの変化で必ず腐る（本作業でも「キャッシュは初回必ずミス」という
 *   固定の思い込みが実測で誤りだった）。
 *
 *   🔴 **監視対象は中央値、基準は最小値。** 対象を中央値にするのは 1 本の外れ値（ランナーの
 *   当たり外れ）で鳴らさないため。基準を最小値にするのは、上記のとおり守るべき相手が
 *   下限だからである。**この非対称は意図的であり、揃えてはならない。**
 *
 * 使い方:
 *   GITHUB_REPOSITORY=owner/repo GITHUB_TOKEN=... node scripts/check-ci-latency.js
 *   node scripts/check-ci-latency.js --self-test
 *   node scripts/check-ci-latency.js --report-only   # 判定せず報告のみ（exit 0）
 *
 * 環境変数:
 *   CI_LATENCY_TARGET     監視対象の check 名（既定 build-and-test）
 *   CI_LATENCY_BASELINE   基準の check 名（既定 claude-review）
 *   CI_LATENCY_SAMPLES    走査するマージ済み PR の本数（既定 10）
 *   CI_LATENCY_STEP_RATIO 定常性の門の倍率（既定 2）。中央値 ÷ 最小 がこれ以上なら判定を skip する
 *   CI_LATENCY_CONFIG_PATH  CI 構成ファイル（既定 .github/workflows/ci.yml）。epoch の導出に使う
 *   CI_LATENCY_EPOCH        epoch の上書き（ISO 8601）。**両方向の実測に使う**
 *   CI_LATENCY_MIN_SAMPLES  判定に要る最小本数（既定 3）
 */

const TARGET = process.env.CI_LATENCY_TARGET || 'build-and-test';
const BASELINE = process.env.CI_LATENCY_BASELINE || 'claude-review';
const SAMPLES = Number(process.env.CI_LATENCY_SAMPLES || 10);
const STEP_RATIO = Number(process.env.CI_LATENCY_STEP_RATIO || 2);
const CONFIG_PATH = process.env.CI_LATENCY_CONFIG_PATH || '.github/workflows/ci.yml';
// 🔴 中央値は本数が少ないと 1 本に引きずられる。3 本を下限とする。
const MIN_SAMPLES = Number(process.env.CI_LATENCY_MIN_SAMPLES || 3);

/**
 * 秒。completed_at - started_at。**実際に走った run だけ**を返し、それ以外は null。
 *
 * 🔴 **skip / cancelled / action_required を必ず落とす。** 実データで踏んだ罠である ——
 * これらは started_at == completed_at で **0 秒**として返り、
 *   - 基準（claude-review）側に混ざると**最小が 0 秒**になり、監視が**常に鳴る**
 *   - 監視対象側に混ざると中央値を押し下げ、監視が**常に鳴らない**
 * どちらも「監視が付いている」という記録だけを残して機能しない。
 * **0 秒を弾くだけでは足りない**（1〜2 秒で終わる skip 相当もあるため conclusion で絞る）。
 */
function durationSec(run) {
  if (!run || !run.started_at || !run.completed_at) return null;
  if (run.status !== 'completed') return null;
  if (run.conclusion !== 'success' && run.conclusion !== 'failure') return null;
  const d = (Date.parse(run.completed_at) - Date.parse(run.started_at)) / 1000;
  if (!Number.isFinite(d) || d <= 0) return null;
  return d;
}

/**
 * その head の check 群が動き出した時刻（ミリ秒）。**skip された run も含めて**最小の
 * `started_at` を取る —— 起動しなかった run も「起動判定はされた」時刻を持つため、
 * 群の開始点としては使える。1 本も無ければ null。
 */
function checkSetStart(runs) {
  const ts = (runs || [])
    .map((r) => (r && r.started_at ? Date.parse(r.started_at) : NaN))
    .filter((n) => Number.isFinite(n));
  return ts.length ? Math.min(...ts) : null;
}

/**
 * 秒。**check 群の開始からの完了オフセット**を返す。これが利用者の待ち時間である。
 * 実際に走った run（`durationSec` が値を返す run）だけを対象にする。
 */
function waitSec(run, t0) {
  if (t0 === null || durationSec(run) === null) return null;
  const w = (Date.parse(run.completed_at) - t0) / 1000;
  if (!Number.isFinite(w) || w <= 0) return null;
  return w;
}

/** 中央値。空配列は null。偶数個は下側（保守的に小さく見る＝鳴りにくい側）。 */
function median(xs) {
  if (!xs.length) return null;
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.floor((s.length - 1) / 2)];
}

function min(xs) {
  return xs.length ? Math.min(...xs) : null;
}

/**
 * 判定。値が揃わないときは **fail-open**（exit 0）にする。
 * 🔴 理由: 本検査は「速さ」の監視であって正しさの門ではない。API が引けない・
 * サンプルが足りないといった理由で赤くすると、**本来の失敗と区別が付かない雑音**になる。
 * ただし **skip したことは必ず出力する**（黙って 0 件検査で緑を返さない）。
 *
 * 🔴 **定常性の門（実データで踏んだ罠）。** 中央値は「直近 N 本が同じ CI 構成で走った」と
 * 仮定している。CI を作り変えた直後はこの仮定が崩れ、**中央値は消えたはずの旧構成を指す**
 * ——実測（MSP・2026-08-21）では最適化直後のサンプル 12 本のうち **11 本が最適化前**で、
 * 中央値 447 秒に対し現在の構成は 126 秒だった。**このまま鳴らせば、直したばかりの CI を
 * 「遅い」と起票する。** 監視が最初の 1 回で狼少年になると、以後の本物も無視される。
 *
 * そこで**監視対象サンプルの中央値が最小の `stepRatio` 倍（既定 2）以上なら判定しない**。
 * ランナーの当たり外れは実測で 1.2 倍程度に収まるため、2 倍は構成変更だけを拾う。
 *
 * **代償を明記する: 窓の途中で実際にビルドが 2 倍に伸びた場合も、この門は 1 週遅らせる**
 * （窓が入れ替われば次回鳴る）。速さの監視は fail-open であるという上の方針と同じ向きであり、
 * **見逃しより誤報のほうが監視を壊す**という判断で意図的にこちらへ倒している。
 */
function judge({ targetDurations, baselineDurations, stepRatio = STEP_RATIO, minSamples = MIN_SAMPLES, epoch = null }) {
  const t = median(targetDurations);
  const b = min(baselineDurations);
  // 🔴 **現在の CI 構成で測られた PR が少なすぎるうちは判定しない**（決定 15）。
  // 🔴 **この門を null 判定より前に置く。** 0 本のときこそ epoch を理由に添えねばならない ——
  // 「サンプルが足りない」とだけ言われると、**check 名の設定ミスと区別が付かない**。
  // 中央値は「窓の多数派」を映す。構成を変えた直後は多数派が旧構成のままなので、
  // **判定すれば必ず旧構成の値を現在の値として報告する**。決定 9 の定常性の門は
  // 段差が 2 倍未満だとくぐられる（実測 223 → 125 秒 ＝ 1.8 倍）ため、これだけでは足りない。
  if (targetDurations.length < minSamples) {
    return {
      ok: true,
      skipped: true,
      reason:
        `現在の CI 構成（${epoch ? `${epoch} 以降` : 'epoch 不明'}）で測られた ${TARGET} の対象が ` +
        `${targetDurations.length} 本しかない（判定には ${minSamples} 本要る）。` +
        '中央値がまだ現在の構成を表さないため判定しない',
      target: t,
      baseline: b,
    };
  }
  if (t === null || b === null) {
    return { ok: true, skipped: true, reason: 'サンプルが足りない', target: t, baseline: b };
  }
  // 🔴 定常性の門。下の説明を参照。
  const lo = min(targetDurations);
  if (lo !== null && t >= lo * stepRatio) {
    return {
      ok: true,
      skipped: true,
      reason: `監視対象のサンプルが CI 構成の変更をまたいでいる（最小 ${Math.round(lo)} 秒 / 中央値 ${Math.round(t)} 秒 = ${(t / lo).toFixed(1)} 倍 ≧ ${stepRatio} 倍）。中央値が現在の構成を表していないため判定しない`,
      target: t,
      baseline: b,
    };
  }
  return { ok: t <= b, skipped: false, target: t, baseline: b };
}

async function fetchJson(url, token) {
  const res = await fetch(url, {
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
    },
  });
  if (!res.ok) {
    const err = new Error(`${res.status} ${res.statusText} for ${url}`);
    err.status = res.status;
    err.rateLimited = isRateLimited(res.headers);
    throw err;
  }
  return res.json();
}

/**
 * レート制限による失敗か。**GitHub はレート制限超過にも 403 を返す**ため、
 * ステータスだけ見ると権限誤りと区別が付かない（AI レビューの指摘）。
 * レート制限は待てば直る＝**一過性**なので、赤くしてはならない。
 */
function isRateLimited(headers) {
  if (!headers || typeof headers.get !== 'function') return false;
  return headers.get('x-ratelimit-remaining') === '0' || Boolean(headers.get('retry-after'));
}

/**
 * 権限・認証・存在の誤り（＝**直さない限り永久に直らない**失敗）か。
 *
 * 🔴 **これを 5xx やネットワーク断と同じ「fail-open」で扱ってはならない。** 本検査器は
 * 速さの監視なので API が引けないときは緑を返す設計だが、**権限設定を間違えていると
 * 毎回同じ 403 で早期終了し、監視は一度も働かないまま緑を返し続ける**。
 * それは本 ADR が「監視が黙って壊れるのは監視が無いより悪い」と書いている事態そのものである。
 *
 * 実際、ワークフローの `permissions` は当初 `actions: read` だけだった。スクリプトが叩くのは
 * **Pulls API（`pull-requests`）と Checks API（`checks`）**であり、別スコープである
 * （AI レビューが両リポジトリで独立に指摘した）。**その形はこの分岐が無ければ緑のまま素通りする。**
 */
function classifyFailure(status, { rateLimited = false, scope = 'repo' } = {}) {
  // レート制限（403 で来る）は待てば直る。権限誤りと同じ扱いにしない。
  if (rateLimited) return 'transient';
  if (status === 401 || status === 403) return 'config';
  if (status === 404) {
    // 🔴 **404 の意味は文脈で変わる**（AI レビューの指摘）。
    // リポジトリ／PR 一覧が 404 = リポジトリ名の誤りかアクセス権なし＝**設定の誤り**。
    // 個別コミットの check-runs が 404 = **その 1 本が消えただけ**（スカッシュ後に
    // head ブランチが削除され、dangling commit が GC された等）。
    // ここを一緒くたにすると、**1 本 GC されただけでバッチ全体が赤くなり誤起票する**。
    return scope === 'repo' ? 'config' : 'missing';
  }
  return 'transient'; // 5xx・ネットワーク断
}

/**
 * マージ済み PR を**新しくマージされた順**に並べる。
 *
 * 🔴 **`sort=updated` の順をそのまま使わない。** `updated_at` はマージ後のコメント追加でも
 * 進むため、**古い PR が新しい PR より前に来る**ことがある（AI レビューの指摘）。
 * 監視は「直近の CI 構成」を見たいので、**`merged_at` で並べ直す**。
 */
function sortByMergedAtDesc(prs) {
  return prs
    .filter((p) => p && p.merged_at)
    .sort((a, b) => Date.parse(b.merged_at) - Date.parse(a.merged_at));
}

/**
 * 「この PR で backend のビルド・テストが走ったか」を判定する道具を読む（IADR-0208 決定 13）。
 *
 * 🔴 **判定ロジックを 2 つ持たない。** CI 側（`detect-changed-areas.js`）と監視側で
 * 判定が食い違うと、**どちらが正しいのか誰にも分からなくなる**。同じ `decide()` を使う。
 *
 * 🔴 **同ファイルが無いリポジトリでも壊れないこと。** 本検査器は複数のリポジトリへ
 * **複写して**使っており、backend ジョブの skip（決定 11）を持たないリポジトリには
 * `detect-changed-areas.js` が無い。読めなければ**絞り込みをしないだけ**で、
 * 従来どおり全 PR を母集合にする（機能が減るだけで壊れない）。
 */
function loadBackendDecider() {
  try {
    const { decide } = require('./detect-changed-areas.js');
    return typeof decide === 'function' ? decide : null;
  } catch {
    return null;
  }
}

const FILES_PAGE_SIZE = 100;
// GitHub の `/pulls/{n}/files` は最大 3000 件までしか返さない（＝30 ページ）。
const FILES_MAX_PAGES = 30;

/**
 * PR の変更ファイル名を**全ページ**集める。
 * `complete` は「最後のページまで読み切れたか」であり、false のときに母集合を狭めてはならない。
 * fetchPage を引数に取るのは、ネットワークを伴わずに自己試験できるようにするためである。
 */
async function fetchChangedFiles(fetchPage, { maxPages = FILES_MAX_PAGES } = {}) {
  const names = [];
  for (let page = 1; page <= maxPages; page += 1) {
    const batch = await fetchPage(page);
    const list = Array.isArray(batch) ? batch : [];
    for (const f of list) {
      if (f && f.filename) names.push(f.filename);
    }
    if (list.length < FILES_PAGE_SIZE) return { names, complete: true };
  }
  return { names, complete: false };
}

/**
 * 母集合の PR が実際にマージされた先＝**統合ブランチ**を、PR 自身から導く。
 *
 * 🔴 **ブランチ名を焼き込まない。** 本ファイルは複数リポジトリへ複写して使うため、
 * `develop` と決め打つと複写先で静かに誤る。
 *
 * 🔴 **GitHub の「既定ブランチ」設定にも依存しない**（AI レビューの指摘）。
 * Commits API は `sha` を省略すると**既定ブランチ**を見る。本リポジトリでは現在
 * 既定＝`develop` なので実害は無いが、**設定が変われば epoch は別ブランチの値になる**。
 * 実測では `main` に `.github/workflows/ci.yml` を触ったコミットが 1 つも無く、
 * その場合 epoch は null になって**絞り込みが黙って消える**。
 *
 * 母集合の PR の `base.ref` は「その計測がどのブランチ向けに行われたか」そのものであり、
 * リポジトリ設定より確かな情報である。**最頻値**を採る（1 本だけ別ブランチ向けの PR が
 * 混ざっても揺らがない）。1 本も無ければ null＝`sha` を付けない（従来どおり既定ブランチ）。
 */
function integrationBranch(prs) {
  const counts = new Map();
  for (const p of prs || []) {
    const ref = p && p.base && p.base.ref;
    if (!ref) continue;
    counts.set(ref, (counts.get(ref) || 0) + 1);
  }
  let best = null;
  let bestN = 0;
  for (const [ref, n] of counts) {
    if (n > bestN) {
      best = ref;
      bestN = n;
    }
  }
  return best;
}

/**
 * 「CI 構成が最後に変わった時刻」（epoch）を GitHub API から引く。IADR-0208 決定 15。
 *
 * 🔴 **日付を焼き込まない。** 固定値は必ず腐る —— 本検査器が「しきい値を固定値で持たない
 * （自己校正）」と決めたのと同じ理由である。構成ファイルの最終変更時刻なら、
 * CI を触るたびに自動で前進する。
 *
 * 🔴 **`git log` で求めてはならない。** `actions/checkout` は既定で shallow であり、
 * その出力は**履歴の打ち切り位置**を指し得る。しかも形式としては正しい日付が返るため、
 * 誤っていることに気付けない。API なら `contents: read` だけで確実に引ける。
 *
 * 引けなければ null を返す（呼び出し側は絞り込みをやめる＝従来どおり全 PR を母集合にする）。
 * **監視を門に化けさせない** —— ここで赤くすると、速さの監視が可用性の門になる。
 */
async function fetchConfigEpoch({ base, token, path = CONFIG_PATH, branch = null }) {
  if (process.env.CI_LATENCY_EPOCH) return process.env.CI_LATENCY_EPOCH;
  try {
    const sha = branch ? `&sha=${encodeURIComponent(branch)}` : '';
    const commits = await fetchJson(`${base}/commits?path=${encodeURIComponent(path)}&per_page=1${sha}`, token);
    const date =
      Array.isArray(commits) && commits[0] && commits[0].commit && commits[0].commit.committer
        ? commits[0].commit.committer.date
        : null;
    return date && Number.isFinite(Date.parse(date)) ? date : null;
  } catch {
    return null;
  }
}

/**
 * 「この計測は現在の CI 構成を表すか」。
 *
 * 🔴 **マージ時刻だけで判定してはならない**（AI レビューの指摘で是正）。判定したいのは
 * 「どの構成で**測られた**か」であって「いつマージされたか」ではない。
 * **CI が走ったのは構成変更の前、マージはその後**という PR は普通に起こる
 * （PR を放置している間に develop 側で `ci.yml` が変わり、リベースせずマージされた場合）。
 * その 1 本は旧構成の値なのに母集合へ残り、**決定 15 が塞ごうとした穴を epoch 自身が再現する**。
 *
 * 🔴 **逆に、計測時刻だけで判定すると今度は取りこぼす**（実データで判明）。
 * **構成変更を持ち込んだ PR 自身**は、その run が**新しい `ci.yml` で走っている**
 * （`pull_request` の run は base を取り込んだ merge ref で動く）のに、
 * epoch＝develop へ載った時刻はその run より**後**になるため落ちてしまう。
 * 実測では #546 がこれに当たり、**母集合が 0 本になった**。
 * 落ちるのは「新しい構成での最初の 1 本」＝**最も知りたいサンプル**である。
 *
 * したがって両方を見る。変更ファイル一覧は backend の判定のために既に引いており、追加の API 呼び出しは無い。
 *
 * fail-open（絞り込まない）に倒すのは、epoch が無い・壊れている・計測時刻が取れないときである。
 * 境界（同時刻）は**残す** —— その構成で測られた 1 本目だからである。
 */
function representsCurrentConfig({ t0, mergedAt, changedConfig, epoch }) {
  if (!epoch) return true;
  const e = Date.parse(epoch);
  if (!Number.isFinite(e)) return true;
  if (t0 === null || t0 === undefined) return true; // 計測時刻が取れない
  if (t0 >= e) return true; // 現在の構成で測られた
  // その構成変更を持ち込んだ PR 自身（run は新しい ci.yml で走っている）
  const m = Date.parse(mergedAt);
  if (changedConfig && Number.isFinite(m) && m >= e) return true;
  return false;
}

async function collect({ repo, token, samples }) {
  const base = `https://api.github.com/repos/${repo}`;
  // per_page を samples の 3 倍取るのは、closed には**マージされずに閉じた** PR が混ざるためである。
  let prs;
  try {
    prs = await fetchJson(`${base}/pulls?state=closed&per_page=${samples * 3}&sort=updated&direction=desc`, token);
  } catch (e) {
    e.scope = 'repo'; // リポジトリ全体が引けない＝設定の誤りとして扱う
    throw e;
  }
  const merged = sortByMergedAtDesc(prs).slice(0, samples);

  // 🔴 **現在の CI 構成で測られた PR だけを母集合にする**（決定 15）。
  // 中央値は「窓の多数派」を映すので、構成を変えた直後の窓は旧構成の値を現在の値として報告する。
  // 判定は下のループで、**計測時刻（check 群の開始）**に対して行う（マージ時刻ではない）。
  const branch = integrationBranch(merged);
  const epoch = await fetchConfigEpoch({ base, token, branch });
  let skippedPreEpoch = 0;
  if (!epoch) {
    console.log(
      `::notice::[check-ci-latency] ${CONFIG_PATH}（${branch || '既定ブランチ'}）の最終変更時刻を引けなかったため、` +
        'CI 構成による母集合の限定をしていない（従来どおり全 PR を見る）。',
    );
  }

  const targetDurations = [];
  const baselineDurations = [];
  let fetchFailures = 0;
  let missingHeads = 0;
  let skippedBackend = 0;
  const decideBackend = loadBackendDecider();
  for (const pr of merged) {
    const sha = pr.head && pr.head.sha;
    if (!sha) continue;

    // 決定 13: docs だけの PR は構造上速く終わる（実測 24 秒）ので、混ぜると母集合が二峰性になり、
    // 定常性の門（決定 9）が「構成が変わった」と誤認して**永久に判定しなくなる**。
    // 監視が答えたい問いは「**CI が仕事をしたとき**、レビューより長くかかるか」である。
    // 変更ファイル一覧は **1 度だけ**引き、backend の判定（決定 13）と
    // 「構成変更を持ち込んだ PR か」の判定（決定 15）の両方に使う。
    // 🔴 **1 ページ目だけ見てはならない。** 変更 100 件を超える PR で、最初の 100 件が
    // すべて許可リスト内（docs 等）・backend の変更が 101 件目以降にある場合、
    // **実際には backend を触った PR を母集合から静かに落とす**（AI レビューの指摘）。
    // 実測でも #536（変更 619 ファイル）が該当した。
    let changedFiles = null; // 読み切れなかった／引けなかったときは null のまま
    try {
      const { names, complete } = await fetchChangedFiles((page) =>
        fetchJson(`${base}/pulls/${pr.number}/files?per_page=${FILES_PAGE_SIZE}&page=${page}`, token)
      );
      if (complete) changedFiles = names;
    } catch (e) {
      if (classifyFailure(e.status, { rateLimited: e.rateLimited, scope: 'commit' }) === 'config') {
        e.scope = 'commit';
        throw e;
      }
      // 引けなければ絞り込まない（母集合に残す）。速さの監視を門に化けさせない。
    }

    // 🔴 **backend を skip した PR は母集合に入れない**（決定 13）。
    // 一覧が引けなかった／読み切れなかったときは**除外しない**（判断材料が欠けている状態で
    // 母集合を狭めるのは、この検査が防ごうとしている「黙って狭める」ことそのものである）。
    if (decideBackend && changedFiles && !decideBackend(changedFiles).backend) {
      skippedBackend += 1;
      continue;
    }

    let runs;
    try {
      runs = await fetchJson(`${base}/commits/${sha}/check-runs?per_page=100`, token);
    } catch (e) {
      // 🔴 権限・認証の誤りは**握りつぶさない**。握りつぶすと「権限設定が恒久的に誤っている」と
      // 「単にマージ済み PR が少ない」が**区別できなくなり**、監視は緑のまま一度も働かない。
      const kind = classifyFailure(e.status, { rateLimited: e.rateLimited, scope: 'commit' });
      if (kind === 'config') {
        e.scope = 'commit';
        throw e;
      }
      // 一過性（5xx・レート制限）と、その 1 本が消えただけ（404）は落として続ける。
      if (kind === 'missing') missingHeads += 1;
      else fetchFailures += 1;
      continue;
    }
    const all = runs.check_runs || [];
    const t0 = checkSetStart(all);
    // 🔴 旧構成で測られた run は母集合に入れない（決定 15）。
    // **計測時刻で見る**（マージ時刻ではない）。ただし構成変更を持ち込んだ PR 自身は残す。
    //
    // 🔴 **ここだけ fail-open の向きが逆なのは意図的である**（AI レビューの 🟢 指摘）。
    // ファイル一覧が引けなかった（`changedFiles === null`）とき、backend の判定は
    // 「除外しない」へ倒すが、こちらは `changedConfig` が false 扱いになり、
    // **計測が epoch より前ならその PR は落ちる**。理由:
    //   - 落とす誤り  … 母集合が 1 本小さくなるだけで、**本数は出力される**（黙っていない）
    //   - 残す誤り    … **旧構成の値が中央値へ混ざる**＝決定 15 が塞いだ誤報そのものが戻る
    // 「判断材料が欠けたら狭めない」の原則は**黙って狭めないこと**を言っており、
    // ここは数えて出しているので当たらない。**向きを揃えると誤報の側へ倒れる。**
    if (!representsCurrentConfig({
      t0,
      mergedAt: pr.merged_at,
      changedConfig: Boolean(changedFiles && changedFiles.includes(CONFIG_PATH)),
      epoch,
    })) {
      skippedPreEpoch += 1;
      continue;
    }
    for (const r of all) {
      const d = waitSec(r, t0);
      if (d === null) continue;
      if (r.name === TARGET) targetDurations.push(d);
      else if (r.name === BASELINE) baselineDurations.push(d);
    }
  }
  return {
    merged: merged.length,
    targetDurations,
    baselineDurations,
    fetchFailures,
    missingHeads,
    skippedBackend,
    skippedPreEpoch,
    epoch,
    branch,
  };
}

async function selfTest() {
  const t = [];
  // 🔴 **非同期の試験を同期のハーネスへ入れてはならない。** `fn()` を await しないと、
  // 中で throw しても**その行は `ok` と表示され、集計も「N 件 OK」と出る**（実測）。
  // 落ちるのは Node が未処理の拒否でプロセスを中断するからであり、
  // **報告と実態が食い違う**（どの試験が失敗したのか読み取れない）。
  // 実測: ページネーションを 1 ページ目で打ち切る変異を入れた状態で、
  // 同期ハーネスは当該試験を `ok` と表示した。非同期対応後は `FAIL` と表示する。
  // 戻り値が thenable なら解決を待ち、その完了を pending へ積んで最後にまとめて待つ。
  const pending = [];
  const ok = (name, fn) => {
    let r;
    try {
      r = fn();
    } catch (e) {
      t.push(`  FAIL ${name}: ${e.message}`);
      process.exitCode = 1;
      return;
    }
    if (r && typeof r.then === 'function') {
      pending.push(
        r.then(
          () => t.push(`  ok   ${name}`),
          (e) => {
            t.push(`  FAIL ${name}: ${e.message}`);
            process.exitCode = 1;
          }
        )
      );
      return;
    }
    t.push(`  ok   ${name}`);
  };
  const eq = (a, b, m) => {
    if (JSON.stringify(a) !== JSON.stringify(b)) throw new Error(`${m || ''} ${JSON.stringify(a)} != ${JSON.stringify(b)}`);
  };

  // fetch の Headers を模す（`get` を持つオブジェクト）。実物の Headers は Node 18+ に有るが、
  // 試験の意図は「`get` で引ける入れ物なら拾えるか」なので最小の模造で足りる。
  const headersOf = (obj) => ({ get: (k) => (k in obj ? obj[k] : null) });
  const okRun = (a, b, extra) => ({ started_at: a, completed_at: b, status: 'completed', conclusion: 'success', ...extra });
  ok('durationSec: 差を秒で返す', () => eq(durationSec(okRun('2026-01-01T00:00:00Z', '2026-01-01T00:02:00Z')), 120));
  ok('durationSec: 欠損は null', () => eq(durationSec(okRun(null, 'x')), null));
  ok('durationSec: 負値は null（時計のずれを通さない）', () =>
    eq(durationSec(okRun('2026-01-01T00:02:00Z', '2026-01-01T00:00:00Z')), null));
  // 🔴 実データで踏んだ罠の回帰テスト（skip が 0 秒として基準へ混ざり、最小が 0 になった）。
  ok('durationSec: skipped は null（0 秒が最小を汚す）', () =>
    eq(durationSec(okRun('2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', { conclusion: 'skipped' })), null));
  ok('durationSec: action_required は null', () =>
    eq(durationSec(okRun('2026-01-01T00:00:00Z', '2026-01-01T00:01:00Z', { conclusion: 'action_required' })), null));
  ok('durationSec: cancelled は null', () =>
    eq(durationSec(okRun('2026-01-01T00:00:00Z', '2026-01-01T00:01:00Z', { conclusion: 'cancelled' })), null));
  ok('durationSec: 未完了は null', () =>
    eq(durationSec(okRun('2026-01-01T00:00:00Z', '2026-01-01T00:01:00Z', { status: 'in_progress' })), null));
  ok('durationSec: 0 秒は null（同時刻の完了を通さない）', () =>
    eq(durationSec(okRun('2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')), null));
  ok('durationSec: failure は数える（遅さの観測に成否は関係ない）', () =>
    eq(durationSec(okRun('2026-01-01T00:00:00Z', '2026-01-01T00:01:00Z', { conclusion: 'failure' })), 60));
  // 🔴 実データで踏んだ罠の回帰テスト（集約ジョブを自身の所要で測り、実際の 1/8 に見えた）。
  ok('checkSetStart: 最小の started_at を返す（skip も含める）', () =>
    eq(
      checkSetStart([
        { started_at: '2026-01-01T00:00:10Z' },
        { started_at: '2026-01-01T00:00:00Z' },
        { started_at: '2026-01-01T00:00:05Z' },
      ]),
      Date.parse('2026-01-01T00:00:00Z')
    ));
  ok('checkSetStart: 空は null', () => eq(checkSetStart([]), null));
  ok('waitSec: 群の開始からの完了オフセットを返す', () => {
    const t0 = Date.parse('2026-01-01T00:00:00Z');
    // 集約ジョブ: +111s に起動し +126s に完了（自身の所要は 15 秒）。
    const agg = okRun('2026-01-01T00:01:51Z', '2026-01-01T00:02:06Z');
    eq(durationSec(agg), 15, '所要');
    eq(waitSec(agg, t0), 126, '待ち時間');
  });
  ok('waitSec: 走らなかった run は null（durationSec と同じ門を通す）', () => {
    const t0 = Date.parse('2026-01-01T00:00:00Z');
    eq(waitSec(okRun('2026-01-01T00:01:51Z', '2026-01-01T00:02:06Z', { conclusion: 'skipped' }), t0), null);
  });
  ok('waitSec: t0 が無ければ null', () =>
    eq(waitSec(okRun('2026-01-01T00:00:00Z', '2026-01-01T00:01:00Z'), null), null));

  // 🔴 AI レビューが両リポジトリで独立に指摘した罠の回帰テスト。
  // permissions が足りないと 403 で早期終了し、fail-open で**緑のまま永久に働かない**。
  ok('classifyFailure: 401 / 403 は設定の誤り（赤くする）', () =>
    eq([401, 403].map((c) => classifyFailure(c)), ['config', 'config']));
  ok('classifyFailure: 5xx・不明は一過性（fail-open のまま）', () =>
    eq([500, 502, 503, undefined, null].map((c) => classifyFailure(c)), ['transient', 'transient', 'transient', 'transient', 'transient']));
  // 🔴 AI レビューの指摘の回帰テスト。403 はレート制限でも返るため、
  // ステータスだけで権限誤りと断じると**待てば直る失敗で誤起票する**。
  ok('classifyFailure: レート制限の 403 は一過性（待てば直るので赤くしない）', () =>
    eq(classifyFailure(403, { rateLimited: true }), 'transient'));
  // 🔴 AI レビューの指摘の回帰テスト。404 の意味は文脈で変わる。
  ok('classifyFailure: リポジトリ側の 404 は設定の誤り（リポジトリ名の誤り・権限なし）', () =>
    eq(classifyFailure(404, { scope: 'repo' }), 'config'));
  ok('classifyFailure: 個別コミットの 404 は「その 1 本が消えただけ」', () =>
    eq(classifyFailure(404, { scope: 'commit' }), 'missing'));
  ok('isRateLimited: x-ratelimit-remaining=0 を拾う', () =>
    eq(isRateLimited(headersOf({ 'x-ratelimit-remaining': '0' })), true));
  ok('isRateLimited: retry-after を拾う', () => eq(isRateLimited(headersOf({ 'retry-after': '60' })), true));
  ok('isRateLimited: 余裕があれば false', () =>
    eq(isRateLimited(headersOf({ 'x-ratelimit-remaining': '4321' })), false));
  ok('isRateLimited: headers が無ければ false', () => eq(isRateLimited(null), false));
  // 🔴 `sort=updated` はマージ後のコメントでも進むため、マージ順とは一致しない。
  ok('sortByMergedAtDesc: merged_at の新しい順に並べ直す', () =>
    eq(
      sortByMergedAtDesc([
        { number: 1, merged_at: '2026-08-20T00:00:00Z' },
        { number: 2, merged_at: '2026-08-22T00:00:00Z' },
        { number: 3, merged_at: '2026-08-21T00:00:00Z' },
      ]).map((p) => p.number),
      [2, 3, 1]
    ));
  ok('sortByMergedAtDesc: マージされずに閉じた PR を落とす', () =>
    eq(
      sortByMergedAtDesc([
        { number: 1, merged_at: null },
        { number: 2, merged_at: '2026-08-22T00:00:00Z' },
      ]).map((p) => p.number),
      [2]
    ));

  ok('median: 奇数個', () => eq(median([3, 1, 2]), 2));
  ok('median: 偶数個は下側（鳴りにくい側へ倒す）', () => eq(median([1, 2, 3, 4]), 2));
  ok('median: 空は null', () => eq(median([]), null));
  ok('min: 最小を返す', () => eq(min([5, 2, 9]), 2));

  ok('judge: 対象の中央値が基準の最小以下なら ok', () =>
    eq(judge({ targetDurations: [100, 110, 120], baselineDurations: [130, 300] }).ok, true));
  ok('judge: 対象の中央値が基準の最小を超えたら NG（＝逆転）', () =>
    eq(judge({ targetDurations: [200, 240, 260], baselineDurations: [148, 400] }).ok, false));
  ok('judge: 基準は中央値ではなく最小で見る', () => {
    // 基準の中央値は 300 だが最小は 148。対象の中央値 240 は中央値なら通り、最小なら落ちる。
    // 🔴 対象を 3 本にしてあるのは、決定 15 の最小本数の門（既定 3 本）を通すためである。
    // 1 本だと門が先に効いて skip し、**この試験は「落ちない」ことを確かめるだけの空試験になる**。
    const r = judge({ targetDurations: [235, 240, 245], baselineDurations: [148, 300, 500] });
    eq(r.skipped, false, '門ではなく本判定に到達していること');
    eq(r.ok, false, '最小と比べていない');
  });
  ok('judge: 対象は最小ではなく中央値で見る（外れ値で鳴らさない）', () => {
    // 対象の最小は 140 だが中央値は 240。基準 148 に対し、
    // 最小で見れば通り（140 <= 148）、中央値で見れば落ちる（240 > 148）。落ちるのが正しい。
    // 🔴 開き幅を 1.71 倍に抑えてあるのは、定常性の門（2 倍）に掛からせないためである
    //    ——「中央値で見ているか」だけを試験したいので、門の側で skip されては測れない。
    const r = judge({ targetDurations: [140, 240, 260], baselineDurations: [148] });
    eq(r.skipped, false, '門で skip されてはならない');
    eq(r.ok, false);
  });
  // 🔴 実データで踏んだ罠の回帰テスト（最適化直後、12 本中 11 本が旧構成で中央値が旧値を指した）。
  ok('judge: 構成変更をまたいだサンプルは判定しない（狼少年にしない）', () => {
    // 旧構成 447 秒が 11 本・新構成 126 秒が 1 本。中央値 447 は現在の構成を表していない。
    const old11 = Array(11).fill(447);
    const r = judge({ targetDurations: [...old11, 126], baselineDurations: [129] });
    eq(r.ok, true, '鳴ってはならない');
    eq(r.skipped, true, 'skip として記録されねばならない');
  });
  ok('judge: 窓が入れ替わったら判定する（門は恒久的に黙らせない）', () => {
    // すべて新構成。ばらつきは 1.2 倍程度で門を通り、126 <= 129 なので ok。
    const r = judge({ targetDurations: [120, 126, 140], baselineDurations: [129] });
    eq(r.skipped, false, '判定されねばならない');
    eq(r.ok, true);
  });
  ok('judge: 定常なサンプルなら逆転を見逃さない', () => {
    const r = judge({ targetDurations: [230, 241, 250], baselineDurations: [148] });
    eq(r.skipped, false);
    eq(r.ok, false);
  });
  ok('judge: ランナーのばらつき（1.2 倍）は門を通す', () => {
    const r = judge({ targetDurations: [409, 447, 479], baselineDurations: [129] });
    eq(r.skipped, false, '1.2 倍で skip してはならない');
    eq(r.ok, false);
  });
  ok('judge: サンプル不足は fail-open だが skipped を立てる', () => {
    const r = judge({ targetDurations: [], baselineDurations: [148] });
    eq(r.ok, true);
    eq(r.skipped, true);
  });
  ok('judge: 同値は逆転ではない（境界）', () => {
    // 🔴 対象を 3 本にする理由は上と同じ。1 本だと最小本数の門が先に効き、
    // **skip の ok:true を「境界が通った」と読み違える**（黙って素通りする試験になっていた）。
    const r = judge({ targetDurations: [148, 148, 148], baselineDurations: [148] });
    eq(r.skipped, false, '門ではなく本判定に到達していること');
    eq(r.ok, true);
  });

  // 🔴 決定 13 の回帰テスト。複写して使うファイルなので、
  // 「同ファイルが無いリポジトリでも壊れない」ことを固定する。
  ok('loadBackendDecider: 本リポでは decide を返す', () => {
    const d = loadBackendDecider();
    if (typeof d !== 'function') throw new Error('decide を読めていない');
  });
  ok('loadBackendDecider: 読めなければ null（絞り込みをしないだけ）', () => {
    const Module = require('module');
    const orig = Module.prototype.require;
    Module.prototype.require = function (id) {
      if (String(id).includes('detect-changed-areas')) throw new Error('模擬: ファイルが無い');
      return orig.apply(this, arguments);
    };
    try {
      eq(loadBackendDecider(), null);
    } finally {
      Module.prototype.require = orig;
    }
  });
  ok('decide: docs だけの PR は母集合から外れる側（backend=false）', () => {
    const d = loadBackendDecider();
    eq(d(['docs/a.md']).backend, false);
  });
  ok('decide: backend を触る PR は母集合に残る（backend=true）', () => {
    const d = loadBackendDecider();
    eq(d(['backend/x.cs']).backend, true);
  });
  ok('decide: ファイル一覧が引けなかった場合は母集合に残す（安全側）', () => {
    const d = loadBackendDecider();
    eq(d([]).backend, true);
  });

  // 🔴 ページネーションの回帰テスト（AI レビューの指摘）。
  // 1 ページ目だけ見ると、**backend の変更が 101 件目以降にある PR を静かに母集合から落とす**。
  const page = (n, prefix) => Array.from({ length: n }, (_, i) => ({ filename: `${prefix}${i}.md` }));
  ok('fetchChangedFiles: 1 ページで終わる場合', async () => {
    const r = await fetchChangedFiles(() => page(3, 'docs/a'));
    eq(r.names.length, 3);
    eq(r.complete, true);
  });
  ok('fetchChangedFiles: ちょうど 100 件なら次ページを引く', async () => {
    const calls = [];
    const r = await fetchChangedFiles((p) => {
      calls.push(p);
      return p === 1 ? page(100, 'docs/a') : [];
    });
    eq(calls.join(','), '1,2');
    eq(r.names.length, 100);
    eq(r.complete, true);
  });
  ok('🔴 fetchChangedFiles: 101 件目以降の backend 変更を取りこぼさない', async () => {
    const r = await fetchChangedFiles((p) =>
      p === 1 ? page(100, 'docs/a') : [{ filename: 'backend/Services/X/src/X.cs' }]
    );
    eq(r.complete, true);
    eq(loadBackendDecider()(r.names).backend, true);
  });
  ok('参考: 1 ページ目だけ見ると backend=false になる（回帰の再現）', () => {
    eq(loadBackendDecider()(page(100, 'docs/a').map((f) => f.filename)).backend, false);
  });
  ok('fetchChangedFiles: 上限まで読んでも終わらなければ complete=false', async () => {
    const r = await fetchChangedFiles(() => page(100, 'docs/a'), { maxPages: 3 });
    eq(r.complete, false);
    eq(r.names.length, 300);
  });
  ok('fetchChangedFiles: 配列以外が返っても落ちない（complete で扱う）', async () => {
    const r = await fetchChangedFiles(() => null);
    eq(r.names.length, 0);
    eq(r.complete, true);
  });


  // 🔴 決定 15 の回帰テスト。母集合を「現在の CI 構成以降」に限る。
  const EPOCH = '2026-08-22T00:15:00Z';
  const at = (iso) => Date.parse(iso);
  const rep = (o) => representsCurrentConfig({ changedConfig: false, mergedAt: null, epoch: EPOCH, ...o });
  ok('representsCurrentConfig: epoch より前に測られた run は落とす', () =>
    eq(rep({ t0: at('2026-08-21T00:00:00Z') }), false));
  ok('representsCurrentConfig: epoch 以降に測られた run は残す', () =>
    eq(rep({ t0: at('2026-08-23T00:00:00Z') }), true));
  ok('representsCurrentConfig: 境界（epoch と同時刻）は残す', () =>
    eq(rep({ t0: at(EPOCH) }), true, 'その構成で測られた 1 本目である'));
  ok('🔴 representsCurrentConfig: epoch が無ければ絞り込まない（fail-open）', () =>
    eq(rep({ t0: at('2020-01-01T00:00:00Z'), epoch: null }), true));
  ok('representsCurrentConfig: 壊れた epoch でも絞り込まない（fail-open）', () =>
    eq(rep({ t0: at('2020-01-01T00:00:00Z'), epoch: 'not-a-date' }), true));
  ok('representsCurrentConfig: 計測時刻が取れなければ絞り込まない（fail-open）', () =>
    eq(rep({ t0: null }), true));
  // 🔴 AI レビューの指摘の回帰テスト。**マージ時刻だけで判定していたら通ってしまう形。**
  ok('🔴 representsCurrentConfig: 「CI は構成変更の前、マージはその後」の PR を落とす', () =>
    eq(
      rep({ t0: at('2026-08-21T12:00:00Z'), mergedAt: '2026-08-23T00:00:00Z' }),
      false,
      '計測時刻で見ていない',
    ));
  // 🔴 実データで判明した取りこぼしの回帰テスト。**計測時刻だけで判定していたら落ちてしまう形。**
  ok('🔴 representsCurrentConfig: 構成変更を持ち込んだ PR 自身は残す（実測 #546）', () =>
    eq(
      rep({ t0: at('2026-08-21T23:57:00Z'), mergedAt: '2026-08-22T00:15:29Z', changedConfig: true }),
      true,
      'run は新しい ci.yml で走っている。最も知りたい 1 本目を落としてはならない',
    ));
  // 🔴 AI レビューの指摘の回帰テスト。epoch を**どのブランチで測るか**。
  const withBase = (ref) => ({ base: { ref } });
  ok('integrationBranch: 母集合の PR がマージされた先を採る', () =>
    eq(integrationBranch([withBase('develop'), withBase('develop')]), 'develop'));
  ok('integrationBranch: 最頻値を採る（1 本の別ブランチで揺らがない）', () =>
    // 🔴 **先頭を別ブランチにしてある。** `[develop, main, develop]` だと
    // 「先頭を採る」実装でも通ってしまい、**最頻値を採っていることを検査できない**（実測）。
    eq(integrationBranch([withBase('main'), withBase('develop'), withBase('develop')]), 'develop'));
  ok('integrationBranch: 1 本も無ければ null（＝既定ブランチに委ねる）', () => {
    eq(integrationBranch([]), null);
    eq(integrationBranch(null), null);
    eq(integrationBranch([{}, { base: {} }]), null);
  });
  ok('🔴 fetchConfigEpoch: ブランチを渡したら sha としてクエリへ載せる', async () => {
    const orig = process.env.CI_LATENCY_EPOCH;
    delete process.env.CI_LATENCY_EPOCH;
    const seen = [];
    const origFetch = global.fetch;
    global.fetch = async (url) => {
      seen.push(String(url));
      return {
        ok: true,
        headers: { get: () => null },
        json: async () => [{ commit: { committer: { date: '2026-08-22T00:15:29Z' } } }],
      };
    };
    try {
      const e = await fetchConfigEpoch({ base: 'https://api.example/repos/o/r', token: 'x', branch: 'develop' });
      eq(e, '2026-08-22T00:15:29Z');
      if (!seen[0].includes('&sha=develop')) throw new Error(`sha が載っていない: ${seen[0]}`);
    } finally {
      global.fetch = origFetch;
      if (orig === undefined) delete process.env.CI_LATENCY_EPOCH;
      else process.env.CI_LATENCY_EPOCH = orig;
    }
  });
  ok('fetchConfigEpoch: ブランチが無ければ sha を付けない（既定ブランチに委ねる）', async () => {
    const orig = process.env.CI_LATENCY_EPOCH;
    delete process.env.CI_LATENCY_EPOCH;
    const seen = [];
    const origFetch = global.fetch;
    global.fetch = async (url) => {
      seen.push(String(url));
      return { ok: true, headers: { get: () => null }, json: async () => [] };
    };
    try {
      eq(await fetchConfigEpoch({ base: 'https://api.example/repos/o/r', token: 'x', branch: null }), null);
      if (seen[0].includes('sha=')) throw new Error(`sha が載ってしまっている: ${seen[0]}`);
    } finally {
      global.fetch = origFetch;
      if (orig === undefined) delete process.env.CI_LATENCY_EPOCH;
      else process.env.CI_LATENCY_EPOCH = orig;
    }
  });
  ok('representsCurrentConfig: 古い構成変更 PR は残さない（マージも epoch より前）', () =>
    eq(
      rep({ t0: at('2026-08-19T00:00:00Z'), mergedAt: '2026-08-19T01:00:00Z', changedConfig: true }),
      false,
    ));

  ok('🔴 judge: 対象が最小本数に満たなければ判定しない', () => {
    const r = judge({ targetDurations: [120, 130], baselineDurations: [146], minSamples: 3, epoch: EPOCH });
    eq(r.skipped, true);
    if (!r.reason.includes('2 本しかない')) throw new Error(`本数が理由に出ていない: ${r.reason}`);
    if (!r.reason.includes(EPOCH)) throw new Error(`epoch が理由に出ていない: ${r.reason}`);
  });
  ok('🔴 judge: 本数が足りれば判定する（skip しっぱなしでない）', () => {
    const r = judge({ targetDurations: [120, 130, 125], baselineDurations: [146], minSamples: 3, epoch: EPOCH });
    eq(r.skipped, false);
    eq(r.ok, true, '125 秒 <= 146 秒');
  });
  ok('judge: 本数が足りていれば逆転を検出する', () => {
    const r = judge({ targetDurations: [200, 210, 205], baselineDurations: [146], minSamples: 3, epoch: EPOCH });
    eq(r.skipped, false);
    eq(r.ok, false);
  });
  ok('judge: 最小本数の門は定常性の門より先に効く', () => {
    // 中央値 240 / 最小 20 ＝ 12 倍。両方の門に当たるが、報告すべきは「本数が足りない」ほうである。
    const r = judge({ targetDurations: [20, 240], baselineDurations: [146], minSamples: 3, epoch: EPOCH });
    eq(r.skipped, true);
    if (!r.reason.includes('本しかない')) throw new Error(`理由が違う: ${r.reason}`);
  });
  ok('judge: epoch 不明でも本数の門は効く（理由にその旨が出る）', () => {
    const r = judge({ targetDurations: [120], baselineDurations: [146], minSamples: 3, epoch: null });
    eq(r.skipped, true);
    if (!r.reason.includes('epoch 不明')) throw new Error(`理由が違う: ${r.reason}`);
  });
  ok('CI_LATENCY_EPOCH が指定されていれば API を叩かずそれを使う', async () => {
    const orig = process.env.CI_LATENCY_EPOCH;
    process.env.CI_LATENCY_EPOCH = EPOCH;
    try {
      // token/base を渡さずとも API を叩かないことが、例外が出ないことで分かる。
      eq(await fetchConfigEpoch({ base: 'http://invalid.invalid', token: 'x' }), EPOCH);
    } finally {
      if (orig === undefined) delete process.env.CI_LATENCY_EPOCH;
      else process.env.CI_LATENCY_EPOCH = orig;
    }
  });
  await Promise.all(pending);
  console.log(t.join('\n'));
  console.log(`[check-ci-latency] 自己試験 ${t.length} 件${process.exitCode ? ' に失敗あり' : ' OK。'}`);
}

async function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) return await selfTest();

  const reportOnly = argv.includes('--report-only');
  const repo = process.env.GITHUB_REPOSITORY;
  const token = process.env.GITHUB_TOKEN;
  if (!repo || !token) {
    // fail-open。CI 以外（ローカル）で叩いたときに赤くしない。
    console.log('[check-ci-latency] GITHUB_REPOSITORY / GITHUB_TOKEN が無いため skip した（fail-open）。');
    return;
  }

  const {
    merged,
    targetDurations,
    baselineDurations,
    fetchFailures,
    missingHeads,
    skippedBackend,
    skippedPreEpoch,
    epoch,
    branch,
  } = await collect({ repo, token, samples: SAMPLES });
  const r = judge({ targetDurations, baselineDurations, epoch });

  // 内訳は 1 つの括弧へまとめる（両方非ゼロのとき括弧が区切りなく並んで読みにくいため）。
  const notes = [];
  if (fetchFailures) notes.push(`${fetchFailures} 本は一過性の API 失敗で取得できず`);
  if (missingHeads) notes.push(`${missingHeads} 本は head が既に無く 404（GC 済みのため恒久的にこのまま）`);
  // 🔴 黙って母集合を狭めない。何本外したかを必ず出す。
  if (skippedBackend) notes.push(`${skippedBackend} 本は backend を skip した PR なので母集合から外した`);
  // 🔴 黙って母集合を狭めない（決定 15 も同じ扱い）。
  if (skippedPreEpoch) {
    notes.push(
      `${skippedPreEpoch} 本は現在の CI 構成より前（${branch || '既定ブランチ'} の ${CONFIG_PATH} が最後に変わった ${epoch} より前）に測られたので母集合から外した`,
    );
  }
  console.log(
    `[check-ci-latency] マージ済み PR ${merged} 本を走査: ` +
      `${TARGET} ${targetDurations.length} 件 / ${BASELINE} ${baselineDurations.length} 件` +
      (notes.length ? `（うち ${notes.join('、')}）` : ''),
  );
  // 🔴 該当 run が 0 件なら、まず **check 名の設定ミス**を疑わせる。
  // 既定値のまま配備すると 0 件になり、fail-open で**緑のまま永久に skip する**。
  // 🔴 **ただし epoch で外したことが原因なら、この警告を出してはならない。**
  // 原因が分かっているのに「設定ミスを疑え」と言うと、**読み手を誤った方向へ差し向ける**
  // ——「監視が黙って壊れる」と同じくらい、**誤った原因を指す監視**は害になる。
  if ((!targetDurations.length || !baselineDurations.length) && !skippedPreEpoch) {
    console.log(
      `[check-ci-latency] ⚠️ 該当 run が 0 件の側がある。CI_LATENCY_TARGET（現在 ${TARGET}）/ ` +
        `CI_LATENCY_BASELINE（現在 ${BASELINE}）が**このリポジトリの check 名と一致しているか**を確かめること。`,
    );
  }
  if (r.skipped) {
    console.log(`[check-ci-latency] 判定を skip した（${r.reason}）。`);
    console.log('[check-ci-latency] **緑は「逆転していない」を意味しない。判定していないだけである。**');
    if (r.target !== null && r.baseline !== null) {
      console.log(
        `[check-ci-latency] 参考値（判定には使っていない）: ${TARGET} 中央値 ${Math.round(r.target)} 秒 / ` +
          `${BASELINE} 最小 ${Math.round(r.baseline)} 秒`,
      );
    }
    return;
  }
  console.log(
    `[check-ci-latency] ${TARGET} の中央値 ${Math.round(r.target)} 秒  vs  ` +
      `${BASELINE} の最小 ${Math.round(r.baseline)} 秒（余裕 ${Math.round(r.baseline - r.target)} 秒）`,
  );

  if (r.ok) {
    console.log('[check-ci-latency] OK: 逆転していない。');
    return;
  }

  const over = Math.round(r.target - r.baseline);
  console.log(`::error::[check-ci-latency] 逆転している。${TARGET}（中央値 ${Math.round(r.target)} 秒）が ` +
    `${BASELINE} の下限（${Math.round(r.baseline)} 秒）を ${over} 秒超えた。` +
    'これ以降、ビルド時間の増加はそのまま PR の待ち時間になる。');
  console.log('[check-ci-latency] 次の手（IADR の「段 3」に分析済み）: ' +
    '①テストのシャーディング ②backend 非変更 PR で backend ジョブを skip ③ビルド成果物の再利用。');
  if (!reportOnly) process.exitCode = 1;
}

main().catch((e) => {
  // 🔴 **権限・認証の誤りは赤くする。** これは「CI が遅い」ではなく「**監視が壊れている**」であり、
  // fail-open で緑を返せば、監視は一度も働かないまま緑を返し続ける
  // （＝「監視が黙って壊れるのは監視が無いより悪い」そのもの）。
  if (classifyFailure(e.status, { rateLimited: e.rateLimited, scope: e.scope || 'repo' }) === 'config') {
    console.log(
      `::error::[check-ci-latency] GitHub API が ${e.status} を返した。**監視が動いていない。** ` +
        'ワークフローの permissions を確かめること —— 本検査器は Pulls API（pull-requests: read）と ' +
        'Checks API（checks: read）を叩く。actions: read では足りない。',
    );
    console.log(`[check-ci-latency] 詳細: ${e.message}`);
    process.exitCode = 1;
    return;
  }
  // 一過性のネットワーク・API 失敗では赤くしない（fail-open）。ただし黙らない。
  console.log(`[check-ci-latency] 実行できなかったため skip した（fail-open）: ${e.message}`);
});
