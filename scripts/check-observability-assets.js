#!/usr/bin/env node
'use strict';
/*
 * check-observability-assets.js
 * 可観測性の資産（Grafana ダッシュボード定義・otel-collector の dev 構成）が、コード側の計器と
 * 既定の送出方針から乖離していないかを機械検査する（NFR-07 / NFR-13 / #287 / IADR-0255）。
 * 外部依存ゼロ（Node 標準モジュールのみ）。check-consumer-endpoint-names.js と同型。
 *
 * ── なぜ要るか
 * **「ダッシュボードをリポジトリに置いた」だけでは、コードとダッシュボードは黙って乖離する。**
 * しかも乖離の現れ方が最悪である —— 系列名がずれたパネルはエラーを出さず、**空のグラフ**を描く。
 * 空のグラフは「異常が起きていない」と読めてしまうため、**監視しているつもりで何も見ていない**状態が
 * 気付かれずに続く。可観測性の資産では、この失敗の形こそ止める価値がある。
 *
 * ── 何を見るか
 *   D1. ダッシュボード JSON が JSON として妥当で、title / uid / panels を持つ
 *   D2. 各パネルが targets と expr を持ち、expr が空でない
 *   D3. uid がダッシュボード間で一意である（Grafana は uid で同一性を決める）
 *   R1. expr が引く `ast_*` 系列が、コード側のレジストリ（BusinessMetricNames）に実在する
 *   R2. レジストリの各計器が、少なくとも 1 つのパネルから引かれている
 *       （**宣言はあるが誰も見ていない計器**＝計上コストだけ払って価値を生まない状態を検出する）
 *   E1. dev の otel-collector 構成が metrics を `debug`（標準出力のみ）にしか出さない
 *       ＝**既定では計装が有効でも外部へ送らない**（IADR-0094 の opt-in の作法）
 *   A1. アラートルールが形を満たす（英字の alert 名・空でない expr・既知の severity・summary あり）
 *   A2. アラートの expr が引く `ast_*` 系列が、コード側のレジストリに実在する（R1 と同じ規則）
 *       🔴 **ずれたアラートはエラーを出さず、ただ永久に鳴らない。** 空のグラフと同じ失敗の形であり、
 *       しかもダッシュボードより悪い——**人が見に行かない前提の仕組みだから**である（#891 / IADR-0374）。
 *   M1. 走査したダッシュボード数・レジストリの計器数・アラートルール数が下限を下回らない
 *       （探索が壊れて 0 件になると、検査は緑のまま何も守らなくなる）
 *
 * ── 🔴 何を見ないか（明示する）
 *   - **Prometheus に実際に系列が出ているか** —— 実バックエンドが要り、機械検査の射程外である。
 *     本検査器が守るのは「名前の一致」までであり、疎通は実環境で確認する。
 *   - **クエリが意味的に正しいか**（集計関数・期間の妥当性）—— 人が読む前提である。
 *
 * ── コード名 → Prometheus 名の変換規則（本検査器が使う唯一の規則）
 *   `ast.foo.bar` → `ast_foo_bar`。Counter は `_total`、Histogram は `_bucket`/`_count`/`_sum` が付く。
 *   計器に `unit` を与えないのはこの規則を 1 本に保つためである（BusinessMetricNames の説明を参照）。
 *
 * 使い方:
 *   node scripts/check-observability-assets.js             # 実ツリーを走査。違反があれば終了コード 1。
 *   node scripts/check-observability-assets.js --self-test # 検査ロジック自体の自己試験。
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const DASHBOARD_DIR = path.join('deploy', 'observability', 'dashboards');
// #891, IADR-0374: アラートルールの置き場所（1 件目を本ディレクトリに置いた）。
const ALERT_DIR = path.join('deploy', 'observability', 'alerts');
const REGISTRY_FILE = path.join(
  'backend', 'Shared', 'AiStockTrading.Shared.Contracts', 'Observability', 'BusinessMetricNames.cs');
const COLLECTOR_CONFIG = path.join('infra', 'otel', 'otel-collector-config.yaml');

// M1: 下限（実測 2 ダッシュボード / 9 計器）。探索が壊れて 0 件になると無条件に緑になる。
const MIN_DASHBOARDS = 2;
const MIN_INSTRUMENTS = 9;
// M1: アラートは 1 件目を置いた（#891）。**0 件へ戻ることを検査で止める** —— ルールを全部消しても
// 緑のままなら、「アラートがある」という前提だけが文書に残り、実体が消えたことに誰も気付かない。
const MIN_ALERT_RULES = 1;

// A1: 許す severity。増やすときは deploy/observability/README.md の規約も同時に直す。
const ALLOWED_ALERT_SEVERITIES = ['warning', 'critical'];

// E1: dev の metrics パイプラインで許すエクスポータ。外部へ送るものを足すときは本配列と
// docs/observability/observability.md を同時に直す（既定の送出方針の変更であるため）。
const ALLOWED_DEV_METRIC_EXPORTERS = ['debug'];

/** コード側の計器名（`ast.` 始まり）を Prometheus の系列名の**接頭辞**へ変換する。 */
function promPrefixOf(instrumentName) {
  return instrumentName.replace(/\./g, '_');
}

/**
 * レジストリ（C# の `public const string X = "ast....";`）から計器名を読む。
 * タグ名の定数は `ast.` で始まらないため自然に除かれる。
 */
function parseRegistry(source) {
  const names = [];
  const re = /const\s+string\s+\w+\s*=\s*"(ast\.[A-Za-z0-9_.]+)"/g;
  let m;
  while ((m = re.exec(source)) !== null) names.push(m[1]);
  return [...new Set(names)].sort();
}

/** ダッシュボード JSON から、パネルの expr が引く `ast_*` 系列名を抽出する。 */
function referencedSeries(dashboard) {
  const found = new Set();
  for (const panel of dashboard.panels ?? []) {
    for (const target of panel.targets ?? []) {
      const expr = typeof target.expr === 'string' ? target.expr : '';
      for (const m of expr.matchAll(/\bast_[a-z0-9_]+/g)) found.add(m[0]);
    }
  }
  return [...found].sort();
}

/** 参照された系列名が、いずれかの計器に対応するか（接尾辞の差は変換規則で吸収する）。 */
function resolveSeries(series, instrumentNames) {
  const prefixes = instrumentNames.map((n) => ({ instrument: n, prefix: promPrefixOf(n) }));
  // 最長一致で解決する（`ast_llm_cost_jpy` と `ast_llm_cost_limit_ratio_percent` のように
  // 一方が他方の接頭辞にならないことは保証できないため、短い方へ誤って寄せない）。
  const candidates = prefixes
    .filter((p) => series === p.prefix || series.startsWith(`${p.prefix}_`))
    .sort((a, b) => b.prefix.length - a.prefix.length);
  if (candidates.length === 0) return null;
  const best = candidates[0];
  const suffix = series.slice(best.prefix.length);
  const allowed = ['', '_total', '_bucket', '_count', '_sum'];
  return allowed.includes(suffix) ? best.instrument : null;
}

/** ダッシュボード 1 枚の構造検査（D1・D2）。 */
function checkDashboardShape(name, dashboard) {
  const errors = [];
  for (const key of ['title', 'uid']) {
    if (typeof dashboard[key] !== 'string' || dashboard[key].length === 0) {
      errors.push(`[D1] ${name}: \`${key}\` が無い（または空）。`);
    }
  }
  if (!Array.isArray(dashboard.panels) || dashboard.panels.length === 0) {
    errors.push(`[D1] ${name}: \`panels\` が無い（または空）。パネルの無いダッシュボードは何も見せない。`);
    return errors;
  }
  dashboard.panels.forEach((panel, i) => {
    const label = `${name} panel[${i}]（${panel.title ?? '無題'}）`;
    if (!Array.isArray(panel.targets) || panel.targets.length === 0) {
      errors.push(`[D2] ${label}: \`targets\` が無い。クエリの無いパネルは空のグラフを描き続ける。`);
      return;
    }
    panel.targets.forEach((target, j) => {
      if (typeof target.expr !== 'string' || target.expr.trim().length === 0) {
        errors.push(`[D2] ${label} target[${j}]: \`expr\` が空である。`);
      }
    });
  });
  return errors;
}

/**
 * #891, IADR-0374: PrometheusRule の YAML から alert ルールを読む（**行走査。YAML ライブラリは足さない** ——
 * 本検査器は外部依存ゼロであり、読むのは自分たちが書く定型の一部だけである）。
 * 読み取れる形を保つのは書き手の責任で、読めなければ下の A1 が fail-loud で落とす。
 */
function parseAlertRules(yaml) {
  const rules = [];
  let current = null;
  for (const line of yaml.split(/\r?\n/)) {
    if (/^\s*#/.test(line)) continue;
    const started = /^\s*-\s+alert:\s*(.+?)\s*$/.exec(line);
    if (started !== null) {
      current = { alert: started[1], expr: '', for: null, severity: null, summary: null };
      rules.push(current);
      continue;
    }
    if (current === null) continue;
    const expr = /^\s*expr:\s*(.+?)\s*$/.exec(line);
    if (expr !== null) { current.expr = expr[1]; continue; }
    const forClause = /^\s*for:\s*(\S+)\s*$/.exec(line);
    if (forClause !== null) { current.for = forClause[1]; continue; }
    const severity = /^\s*severity:\s*(\S+)\s*$/.exec(line);
    if (severity !== null) { current.severity = severity[1]; continue; }
    const summary = /^\s*summary:\s*(.+?)\s*$/.exec(line);
    if (summary !== null) { current.summary = summary[1]; continue; }
  }
  return rules;
}

/** アラートルール 1 本の形（A1）。 */
function checkAlertShape(name, rule) {
  const errors = [];
  const label = `${name}: alert \`${rule.alert}\``;
  if (!/^[A-Z][A-Za-z0-9]*$/.test(rule.alert)) {
    errors.push(`[A1] ${label}: アラート名は PascalCase の英字にする（Prometheus のアラート名は識別子である）。`);
  }
  if (rule.expr.trim().length === 0) {
    errors.push(`[A1] ${label}: \`expr\` が空である。条件の無いルールは永久に鳴らない。`);
  }
  if (rule.severity === null || !ALLOWED_ALERT_SEVERITIES.includes(rule.severity)) {
    errors.push(`[A1] ${label}: \`severity\` が ${ALLOWED_ALERT_SEVERITIES.join(' / ')} のいずれでもない`
      + `（読めた値: ${rule.severity ?? 'なし'}）。重大度が無いと通知の振り分けができない。`);
  }
  if (rule.summary === null || rule.summary.length === 0) {
    errors.push(`[A1] ${label}: \`annotations.summary\` が無い。`
      + '受け取った人が「何が起きたか」を 1 行で読めないアラートは無視される。');
  }
  return errors;
}

/** dev の otel-collector 構成が metrics を外部へ送らないこと（E1）。 */
function checkCollectorMetricsExporters(yaml) {
  const errors = [];
  const metricsPipeline = /\n\s{4}metrics:\n((?:\s{6}.*\n)+)/.exec(`${yaml}\n`);
  if (metricsPipeline === null) {
    // fail-loud: 構成の形が変わったら黙って 0 件検査へ落ちない。
    errors.push('[E1] otel-collector の構成から metrics パイプラインを読み取れなかった。'
      + '書式が変わったなら本検査器も直すこと（黙って検査対象を失わないため）。');
    return errors;
  }
  const exporters = /exporters:\s*\[([^\]]*)\]/.exec(metricsPipeline[1]);
  if (exporters === null) {
    errors.push('[E1] metrics パイプラインの `exporters:` を読み取れなかった（配列記法のみ対応）。');
    return errors;
  }
  const names = exporters[1].split(',').map((s) => s.trim()).filter(Boolean);
  const external = names.filter((n) => !ALLOWED_DEV_METRIC_EXPORTERS.includes(n));
  if (external.length > 0) {
    errors.push(`[E1] dev の otel-collector が metrics を外部へ送る構成になっている（${external.join(', ')}）。`
      + '既定は「計装は有効でも外部へ送らない」である（opt-in の作法）。'
      + '意図した変更なら ALLOWED_DEV_METRIC_EXPORTERS と可観測性仕様書を同時に直すこと。');
  }
  return errors;
}

/** 収集済みの入力に対する検査本体（自己試験からも呼ぶ）。 */
function checkAssets({ dashboards, instrumentNames, collectorYaml, alerts = [] }) {
  const errors = [];

  if (instrumentNames.length < MIN_INSTRUMENTS) {
    errors.push(`[M1] レジストリから読めた計器が ${instrumentNames.length} 件しかない`
      + `（下限 ${MIN_INSTRUMENTS}）。読み取りが壊れると全検査が無条件に緑になる。`);
  }
  if (dashboards.length < MIN_DASHBOARDS) {
    errors.push(`[M1] ダッシュボードが ${dashboards.length} 枚しかない（下限 ${MIN_DASHBOARDS}）。`);
  }

  const uids = new Map();
  const usedInstruments = new Set();

  for (const { name, dashboard } of dashboards) {
    errors.push(...checkDashboardShape(name, dashboard));

    if (typeof dashboard.uid === 'string' && dashboard.uid.length > 0) {
      if (uids.has(dashboard.uid)) {
        errors.push(`[D3] uid \`${dashboard.uid}\` が ${uids.get(dashboard.uid)} と ${name} で重複している。`
          + 'Grafana は uid で同一性を決めるため、後から投入した側が前を上書きする。');
      } else {
        uids.set(dashboard.uid, name);
      }
    }

    for (const series of referencedSeries(dashboard)) {
      const instrument = resolveSeries(series, instrumentNames);
      if (instrument === null) {
        errors.push(`[R1] ${name}: 系列 \`${series}\` に対応する計器がコード側に無い。`
          + '綴りの違うパネルはエラーを出さず空のグラフを描くため、監視しているつもりで何も見ない状態になる。');
      } else {
        usedInstruments.add(instrument);
      }
    }
  }

  // #891, IADR-0374: アラートも「系列名の一致」を守る対象である（R1 と同じ規則を適用する）。
  const alertRules = alerts.flatMap(({ name, rules }) => rules.map((rule) => ({ name, rule })));
  if (alertRules.length < MIN_ALERT_RULES) {
    errors.push(`[M1] アラートルールが ${alertRules.length} 件しかない（下限 ${MIN_ALERT_RULES}）。`
      + '0 件へ戻ると「アラートがある」という前提だけが文書に残る。');
  }
  for (const { name, rule } of alertRules) {
    errors.push(...checkAlertShape(name, rule));
    for (const series of (rule.expr.match(/\bast_[a-z0-9_]+/g) ?? [])) {
      const instrument = resolveSeries(series, instrumentNames);
      if (instrument === null) {
        errors.push(`[A2] ${name}: alert \`${rule.alert}\` が引く系列 \`${series}\` に対応する計器がコード側に無い。`
          + '🔴 綴りの違うアラートはエラーを出さず、**ただ永久に鳴らない**'
          + '（人が見に行かない前提の仕組みなので、空のグラフより気付きにくい）。');
      } else {
        usedInstruments.add(instrument);
      }
    }
  }

  for (const instrument of instrumentNames) {
    if (!usedInstruments.has(instrument)) {
      errors.push(`[R2] 計器 \`${instrument}\` をどのダッシュボードもアラートも引いていない。`
        + '計上のコストだけ払って誰も見ない計器になっている（不要なら計器ごと消す）。');
    }
  }

  errors.push(...checkCollectorMetricsExporters(collectorYaml));
  return errors;
}

function collectFromTree() {
  const dir = path.join(REPO_ROOT, DASHBOARD_DIR);
  const dashboards = [];
  for (const file of fs.readdirSync(dir).filter((f) => f.endsWith('.json')).sort()) {
    const full = path.join(dir, file);
    const raw = fs.readFileSync(full, 'utf8');
    let dashboard;
    try {
      dashboard = JSON.parse(raw);
    } catch (e) {
      // JSON として壊れているものは即座に落とす（Grafana へ投入できない）。
      console.error(`[check-observability-assets] ${DASHBOARD_DIR}/${file} が JSON として不正: ${e.message}`);
      process.exit(1);
    }
    dashboards.push({ name: file, dashboard });
  }
  // #891, IADR-0374: アラート資産（ディレクトリが無ければ 0 件。下限検査が落とす）。
  const alertDir = path.join(REPO_ROOT, ALERT_DIR);
  const alerts = [];
  if (fs.existsSync(alertDir)) {
    for (const file of fs.readdirSync(alertDir).filter((f) => /\.ya?ml$/.test(f)).sort()) {
      alerts.push({ name: file, rules: parseAlertRules(fs.readFileSync(path.join(alertDir, file), 'utf8')) });
    }
  }
  return {
    dashboards,
    alerts,
    instrumentNames: parseRegistry(fs.readFileSync(path.join(REPO_ROOT, REGISTRY_FILE), 'utf8')),
    collectorYaml: fs.readFileSync(path.join(REPO_ROOT, COLLECTOR_CONFIG), 'utf8'),
  };
}

function main() {
  if (process.argv.includes('--self-test')) return selfTest();

  const input = collectFromTree();
  const errors = checkAssets(input);

  if (errors.length === 0) {
    console.log(
      `[check-observability-assets] OK: ダッシュボード ${input.dashboards.length} 枚 / `
        + `アラート ${input.alerts.reduce((n, a) => n + a.rules.length, 0)} 件 / `
        + `計器 ${input.instrumentNames.length} 件を突き合わせました。`
        + `\n  計器: ${input.instrumentNames.join(', ')}`
        + '\n  dev の otel-collector は metrics を debug（標準出力のみ）にしか出していません（外部送信なし）。');
    process.exit(0);
  }

  console.error(`[check-observability-assets] 違反 ${errors.length} 件を検出しました:`);
  for (const e of errors) console.error(`\n  ${e}`);
  console.error('\n根拠は .ai-context/adr/IADR-0255_business-metrics-and-dashboards.md を参照してください。');
  process.exit(1);
}

// ── 自己試験 ────────────────────────────────────────────────────────────────
function selfTest() {
  const results = [];
  const ok = (name, fn) => {
    try {
      fn();
      results.push([true, name]);
    } catch (e) {
      results.push([false, `${name}: ${e.message}`]);
    }
  };
  const assert = (cond, msg) => { if (!cond) throw new Error(msg); };

  const registry = `
    public const string MeterName = "AiStockTrading.Business";
    public const string A = "ast.foo.bar";
    public const string B = "ast.llm.cost_jpy";
    public const string TagAction = "action";
  `;
  const panel = (expr) => ({ panels: [{ title: 't', targets: [{ expr }] }], title: 'T', uid: 'u' });

  ok('レジストリから ast. 始まりの定数だけを読む', () => {
    assert(JSON.stringify(parseRegistry(registry)) === JSON.stringify(['ast.foo.bar', 'ast.llm.cost_jpy']),
      `読めた: ${parseRegistry(registry)}`);
  });

  ok('Meter 名やタグ名は計器として読まない', () => {
    assert(!parseRegistry(registry).includes('action'));
    assert(!parseRegistry(registry).includes('AiStockTrading.Business'));
  });

  ok('Counter の _total 接尾辞を解決できる', () => {
    assert(resolveSeries('ast_foo_bar_total', ['ast.foo.bar']) === 'ast.foo.bar');
  });

  ok('Histogram の _bucket / _count / _sum を解決できる', () => {
    for (const s of ['_bucket', '_count', '_sum']) {
      assert(resolveSeries(`ast_foo_bar${s}`, ['ast.foo.bar']) === 'ast.foo.bar', s);
    }
  });

  ok('接尾辞なし（Gauge）も解決できる', () => {
    assert(resolveSeries('ast_foo_bar', ['ast.foo.bar']) === 'ast.foo.bar');
  });

  ok('未知の接尾辞は解決しない', () => {
    assert(resolveSeries('ast_foo_bar_created', ['ast.foo.bar']) === null);
  });

  ok('存在しない系列は解決しない', () => {
    assert(resolveSeries('ast_typo_here_total', ['ast.foo.bar']) === null);
  });

  ok('接頭辞が重なる計器は最長一致で解決する', () => {
    const names = ['ast.llm.cost', 'ast.llm.cost_limit_ratio_percent'];
    assert(resolveSeries('ast_llm_cost_limit_ratio_percent', names) === 'ast.llm.cost_limit_ratio_percent');
    assert(resolveSeries('ast_llm_cost_total', names) === 'ast.llm.cost');
  });

  ok('R1: 綴り違いの系列を違反として上げる', () => {
    const errors = checkAssets({
      dashboards: [{ name: 'd.json', dashboard: panel('sum(ast_typo_total)') }],
      instrumentNames: ['ast.foo.bar'],
      collectorYaml: 'service:\n  pipelines:\n    metrics:\n      exporters: [debug]\n',
    });
    assert(errors.some((e) => e.startsWith('[R1]')), errors.join('\n'));
  });

  ok('R2: どのパネルも引かない計器を違反として上げる', () => {
    const errors = checkAssets({
      dashboards: [{ name: 'd.json', dashboard: panel('sum(ast_foo_bar_total)') }],
      instrumentNames: ['ast.foo.bar', 'ast.unused.metric'],
      collectorYaml: 'service:\n  pipelines:\n    metrics:\n      exporters: [debug]\n',
    });
    assert(errors.some((e) => e.startsWith('[R2]') && e.includes('ast.unused.metric')), errors.join('\n'));
  });

  ok('D2: expr の無いパネルを違反として上げる', () => {
    const errors = checkAssets({
      dashboards: [{ name: 'd.json', dashboard: { title: 'T', uid: 'u', panels: [{ title: 'p', targets: [] }] } }],
      instrumentNames: [],
      collectorYaml: 'service:\n  pipelines:\n    metrics:\n      exporters: [debug]\n',
    });
    assert(errors.some((e) => e.startsWith('[D2]')), errors.join('\n'));
  });

  ok('D3: uid の重複を違反として上げる', () => {
    const errors = checkAssets({
      dashboards: [
        { name: 'a.json', dashboard: panel('sum(ast_foo_bar_total)') },
        { name: 'b.json', dashboard: panel('sum(ast_foo_bar_total)') },
      ],
      instrumentNames: ['ast.foo.bar'],
      collectorYaml: 'service:\n  pipelines:\n    metrics:\n      exporters: [debug]\n',
    });
    assert(errors.some((e) => e.startsWith('[D3]')), errors.join('\n'));
  });

  ok('E1: dev の metrics が外部 exporter を持てば違反として上げる', () => {
    const yaml = 'service:\n  pipelines:\n    traces:\n      exporters: [debug]\n'
      + '    metrics:\n      receivers: [otlp]\n      exporters: [debug, prometheusremotewrite]\n';
    const errors = checkCollectorMetricsExporters(yaml);
    assert(errors.some((e) => e.includes('prometheusremotewrite')), errors.join('\n'));
  });

  ok('E1: debug のみなら違反にしない', () => {
    const yaml = 'service:\n  pipelines:\n    metrics:\n      receivers: [otlp]\n      exporters: [debug]\n';
    assert(checkCollectorMetricsExporters(yaml).length === 0);
  });

  ok('E1: metrics パイプラインを読めなければ fail-loud で上げる', () => {
    const errors = checkCollectorMetricsExporters('service:\n  pipelines:\n    traces:\n      exporters: [debug]\n');
    assert(errors.some((e) => e.startsWith('[E1]')), errors.join('\n'));
  });

  ok('M1: 下限を下回る入力を違反として上げる（ダッシュボード・計器・アラートの 3 本）', () => {
    const errors = checkAssets({
      dashboards: [],
      instrumentNames: [],
      collectorYaml: 'service:\n  pipelines:\n    metrics:\n      exporters: [debug]\n',
    });
    assert(errors.filter((e) => e.startsWith('[M1]')).length === 3, errors.join('\n'));
  });

  // ── #891, IADR-0374: アラート資産 ──────────────────────────────────────────
  const alertYaml = [
    'spec:',
    '  groups:',
    '    - name: ai-stock-trading.trade-cycle',
    '      rules:',
    '        - alert: AstSomethingBroke',
    '          expr: sum(increase(ast_foo_bar_total[15m])) > 0',
    '          for: 30m',
    '          labels:',
    '            severity: warning',
    '          annotations:',
    '            summary: 何かが壊れている',
    '',
  ].join('\n');
  const alertsOf = (yaml) => [{ name: 'a.yaml', rules: parseAlertRules(yaml) }];
  const withAlerts = (yaml, instrumentNames = ['ast.foo.bar']) => checkAssets({
    dashboards: [
      { name: 'a.json', dashboard: { ...panel('sum(ast_foo_bar_total)'), uid: 'a' } },
      { name: 'b.json', dashboard: { ...panel('sum(ast_foo_bar_total)'), uid: 'b' } },
    ],
    instrumentNames,
    alerts: alertsOf(yaml),
    collectorYaml: 'service:\n  pipelines:\n    metrics:\n      exporters: [debug]\n',
  });

  ok('アラート YAML から alert 名・expr・for・severity・summary を読む', () => {
    const rules = parseAlertRules(alertYaml);
    assert(rules.length === 1, `件数: ${rules.length}`);
    assert(rules[0].alert === 'AstSomethingBroke', rules[0].alert);
    assert(rules[0].expr.includes('ast_foo_bar_total'), rules[0].expr);
    assert(rules[0].for === '30m', String(rules[0].for));
    assert(rules[0].severity === 'warning', String(rules[0].severity));
    assert(rules[0].summary === '何かが壊れている', String(rules[0].summary));
  });

  ok('A1: 形の整ったアラートは違反にしない', () => {
    const errors = withAlerts(alertYaml).filter((e) => e.startsWith('[A1]') || e.startsWith('[A2]'));
    assert(errors.length === 0, errors.join('\n'));
  });

  ok('A2: 綴り違いの系列を引くアラートを違反として上げる（鳴らないアラートを止める）', () => {
    const errors = withAlerts(alertYaml.replace('ast_foo_bar_total', 'ast_typo_total'));
    assert(errors.some((e) => e.startsWith('[A2]')), errors.join('\n'));
  });

  ok('A1: 未知の severity・日本語のアラート名・summary 欠落を違反として上げる', () => {
    const broken = alertYaml
      .replace('AstSomethingBroke', '保有不明アラート')
      .replace('severity: warning', 'severity: info')
      .replace('            summary: 何かが壊れている\n', '');
    const errors = withAlerts(broken).filter((e) => e.startsWith('[A1]'));
    assert(errors.length === 3, errors.join('\n'));
  });

  ok('R2: アラートだけが引く計器は「誰も見ていない」に数えない', () => {
    const errors = withAlerts(alertYaml, ['ast.foo.bar', 'ast.alert.only']).filter((e) => e.startsWith('[R2]'));
    // ast.alert.only はどこからも引かれていないので上がる。ast.foo.bar はパネルとアラートの両方が引く。
    assert(errors.length === 1 && errors[0].includes('ast.alert.only'), errors.join('\n'));
    const alertOnly = checkAssets({
      dashboards: [
        { name: 'a.json', dashboard: { ...panel('sum(ast_foo_bar_total)'), uid: 'a' } },
        { name: 'b.json', dashboard: { ...panel('sum(ast_foo_bar_total)'), uid: 'b' } },
      ],
      instrumentNames: ['ast.foo.bar'],
      alerts: alertsOf(alertYaml),
      collectorYaml: 'service:\n  pipelines:\n    metrics:\n      exporters: [debug]\n',
    });
    assert(!alertOnly.some((e) => e.startsWith('[R2]')), alertOnly.join('\n'));
  });

  const failed = results.filter(([pass]) => !pass);
  for (const [pass, name] of results) console.log(`${pass ? 'ok  ' : 'FAIL'} ${name}`);
  console.log(`\n[check-observability-assets --self-test] ${results.length - failed.length}/${results.length} 件成功`);
  process.exit(failed.length === 0 ? 0 : 1);
}

if (require.main === module) main();

module.exports = {
  promPrefixOf,
  parseRegistry,
  referencedSeries,
  resolveSeries,
  checkDashboardShape,
  checkCollectorMetricsExporters,
  parseAlertRules,
  checkAlertShape,
  checkAssets,
};
