#!/usr/bin/env node
'use strict';
/*
 * scripts.repo.test.js
 * 本リポジトリ固有のテスト（キットには無い自前スクリプトの検査・呼び出し側の回帰）。
 *
 * キットが配布する scripts.test.js から自動で読み込まれる（受け口は impl-handoff-kit が提供）。
 * 固有テストをここに置くことで scripts.test.js をキットとバイト一致に保て、同期は上書きコピー
 * 1 回で済む。キットが同じテストを取り込んだ際の重複も起きない。
 *
 * 実行は `node scripts/scripts.test.js`（本ファイルを直接叩く必要はない）。
 */
const { execSync } = require('child_process');

module.exports = ({ ok, assert }) => {

  // --- check-doc-links.js: --require-planning / planningPopulated（Issue #104 / PR #105） ---
  const fsDl = require('fs');
  const osDl = require('os');
  const pathDl = require('path');
  const { parseArgs: dlParseArgs, planningPopulated } = require('./check-doc-links.js');

  ok('check-doc-links: parseArgs 既定は requirePlanning=false', () => {
    const a = dlParseArgs([]);
    assert.strictEqual(a.requirePlanning, false);
    assert.strictEqual(a.dir, 'docs');
  });
  ok('check-doc-links: --require-planning で requirePlanning=true', () => {
    assert.strictEqual(dlParseArgs(['--require-planning']).requirePlanning, true);
  });
  ok('check-doc-links: --dir と --require-planning の併用', () => {
    const a = dlParseArgs(['--dir', 'notes', '--require-planning']);
    assert.strictEqual(a.dir, 'notes');
    assert.strictEqual(a.requirePlanning, true);
  });
  ok('check-doc-links: planningPopulated は planning/projects 実在で true', () => {
    const root = fsDl.mkdtempSync(pathDl.join(osDl.tmpdir(), 'dl-pop-'));
    fsDl.mkdirSync(pathDl.join(root, 'planning', 'projects'), { recursive: true });
    assert.strictEqual(planningPopulated(root), true);
  });
  ok('check-doc-links: planningPopulated は空プレースホルダ（planning/ のみ）で false', () => {
    const root = fsDl.mkdtempSync(pathDl.join(osDl.tmpdir(), 'dl-empty-'));
    fsDl.mkdirSync(pathDl.join(root, 'planning'), { recursive: true });
    assert.strictEqual(planningPopulated(root), false);
  });
  ok('check-doc-links: planningPopulated は planning/ 不在で false', () => {
    const root = fsDl.mkdtempSync(pathDl.join(osDl.tmpdir(), 'dl-none-'));
    assert.strictEqual(planningPopulated(root), false);
  });

  // --- validate-pipeline-config: 実 pipeline.json の契約テスト -------------------
  // ADR-0001, IADR-0077, #22: 取引パイプラインの宣言（deploy/helm/ai-stock-trading/files/pipeline.json）が
  // 検証器（V1〜V6）に合格することを回帰テストとして固定する。宣言が壊れた・接続性/循環違反へ退行した場合に
  // CI（node scripts/scripts.test.js）と実ファイル検証ステップの両方で検知する。
  const pathPc = require('path');
  const PIPELINE_JSON = pathPc.join(__dirname, '..', 'deploy', 'helm', 'ai-stock-trading', 'files', 'pipeline.json');
  ok('validate-pipeline-config: 実 pipeline.json が検証器に合格する', () => {
    const out = execSync(
      `node ${JSON.stringify(pathPc.join(__dirname, 'validate-pipeline-config.js'))} ${JSON.stringify(PIPELINE_JSON)}`,
      { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }
    );
    assert.match(out, /^OK: /m, `検証器が OK を返すべき（実出力: ${out}）`);
  });

  // --- check-consumer-endpoint-names: サービス跨ぎのキュー名衝突検査（Issue #258 / #354 再発防止） ---
  // ADR-0013, IADR-0106: MassTransit の既定エンドポイント名は consumer クラス名のみから導かれ
  // namespace を含まない。別サービスで同名の consumer を作ると同一キューを共有して competing consumer になり、
  // pub/sub のつもりが取り合いになる（RiskManagement と MarketMonitor が TradeDecisionMade を取り合った）。
  // IADR-0129 / #354: Wolverine ではキュー名にハンドラのクラス名が関与しないため、不変条件を
  // 「ServiceName の一意性＋共通ヘルパの迂回禁止＋新旧混在の禁止」へ入れ替えた。移行期間は両規則が並走する。
  const {
    endpointNameOf,
    wolverineQueueNameOf,
    consumerClassesIn,
    pathService,
    serviceNameConstantIn,
    messagingModeOf,
    forbiddenTopologyCallsIn,
    findCollisions,
    findServiceNameCollisions,
    checkTree: checkConsumerEndpointNames,
  } = require('./check-consumer-endpoint-names.js');

  const RISK_CS = [
    'namespace AiStockTrading.RiskManagement.Worker.Composable.Steps;',
    '',
    '// FR-10: 取引判断を購読し承認/拒否を発行する。',
    'internal sealed class TradeDecisionMadeConsumer(',
    '    OrderScreeningService screeningService,',
    '    ILogger<TradeDecisionMadeConsumer> logger)',
    '    : IConsumer<TradeDecisionMade>',
    '{',
    '}',
  ].join('\n');

  ok('endpointNameOf: 末尾 Consumer を落とす（DefaultEndpointNameFormatter と同じ規則）', () => {
    assert.strictEqual(endpointNameOf('TradeDecisionMadeConsumer'), 'TradeDecisionMade');
    assert.strictEqual(endpointNameOf('TradeDecisionMadeBaselineConsumer'), 'TradeDecisionMadeBaseline');
    assert.strictEqual(endpointNameOf('Foo'), 'Foo');
  });

  ok('consumerClassesIn: 複数行プライマリコンストラクタの IConsumer 実装を検出する', () => {
    assert.deepStrictEqual(consumerClassesIn(RISK_CS), ['TradeDecisionMadeConsumer']);
  });

  ok('consumerClassesIn: IConsumer を実装しないクラス・コメント中の IConsumer は拾わない', () => {
    assert.deepStrictEqual(consumerClassesIn('// : IConsumer<T>\nclass Poller : BackgroundService\n{\n}'), []);
  });

  ok('pathService: backend/Services/<Service>/ からサービス名を得る', () => {
    assert.strictEqual(pathService('backend/Services/RiskManagementService/src/W/X.cs'), 'RiskManagementService');
    assert.strictEqual(pathService('backend/Shared/X.cs'), null);
  });

  ok('findCollisions: サービス跨ぎの同名 consumer を衝突として検出する（#258 の回帰）', () => {
    const collisions = findCollisions([
      { service: 'RiskManagementService', className: 'TradeDecisionMadeConsumer', endpoint: 'TradeDecisionMade', file: 'a.cs' },
      { service: 'MarketMonitorService', className: 'TradeDecisionMadeConsumer', endpoint: 'TradeDecisionMade', file: 'b.cs' },
    ]);
    assert.strictEqual(collisions.length, 1);
    assert.strictEqual(collisions[0].endpoint, 'TradeDecisionMade');
  });

  ok('findCollisions: 改名でキューが分離されれば衝突しない', () => {
    assert.deepStrictEqual(findCollisions([
      { service: 'RiskManagementService', className: 'TradeDecisionMadeConsumer', endpoint: 'TradeDecisionMade', file: 'a.cs' },
      { service: 'MarketMonitorService', className: 'TradeDecisionMadeBaselineConsumer', endpoint: 'TradeDecisionMadeBaseline', file: 'b.cs' },
    ]), []);
  });

  ok('findCollisions: 同一サービス内は衝突判定の対象外（同名はコンパイルエラーで防がれる）', () => {
    assert.deepStrictEqual(findCollisions([
      { service: 'S', className: 'AConsumer', endpoint: 'A', file: 'a.cs' },
      { service: 'S', className: 'BConsumer', endpoint: 'B', file: 'b.cs' },
    ]), []);
  });

  ok('wolverineQueueNameOf: キュー名は ServiceName とメッセージ型名から導く（IADR-0129 決定 1）', () => {
    assert.strictEqual(
      wolverineQueueNameOf('ai-stock-trading.cost-control-service', 'LlmCostIncurred'),
      'ai-stock-trading.cost-control-service.LlmCostIncurred'
    );
    // #258 の新世界版: 同じイベントでもサービスが違えばキューは別。
    assert.notStrictEqual(
      wolverineQueueNameOf('ai-stock-trading.risk-management-service', 'TradeDecisionMade'),
      wolverineQueueNameOf('ai-stock-trading.market-monitor-service', 'TradeDecisionMade')
    );
  });

  ok('serviceNameConstantIn: Program.cs の ServiceName 定数を読む', () => {
    assert.strictEqual(
      serviceNameConstantIn('const string ServiceName = "ai-stock-trading.audit-service";'),
      'ai-stock-trading.audit-service'
    );
    assert.strictEqual(serviceNameConstantIn('var x = 1;'), null);
  });

  ok('messagingModeOf: 新旧の配線を判定し、混在を検出する', () => {
    assert.strictEqual(messagingModeOf('opts.UseAiStockTradingRabbitMq(ServiceName, null);'), 'wolverine');
    assert.strictEqual(messagingModeOf('builder.Services.AddMassTransit(x => { });'), 'masstransit');
    assert.strictEqual(
      messagingModeOf('builder.Services.AddMassTransit(x => { });\nopts.UseAiStockTradingRabbitMq(S, null);'),
      'mixed'
    );
    assert.strictEqual(messagingModeOf('var x = 1;'), 'none');
  });

  ok('forbiddenTopologyCallsIn: 共通ヘルパを迂回したトポロジ指定を検出する（IADR-0129 決定 4）', () => {
    assert.strictEqual(forbiddenTopologyCallsIn('opts.UseRabbitMq().UseConventionalRouting();').length, 1);
    assert.strictEqual(forbiddenTopologyCallsIn('opts.UseAiStockTradingRabbitMq(S, null);').length, 0);
    // 説明文で API 名に触れることは禁止しない（コメント行は対象外）。
    assert.strictEqual(forbiddenTopologyCallsIn('// UseConventionalRouting( は共通ヘルパに閉じる').length, 0);
  });

  ok('findServiceNameCollisions: ServiceName の重複を検出する（新世界の #258 相当）', () => {
    const collisions = findServiceNameCollisions([
      { service: 'AService', serviceName: 'ai-stock-trading.a-service' },
      { service: 'BService', serviceName: 'ai-stock-trading.a-service' },
      { service: 'CService', serviceName: 'ai-stock-trading.c-service' },
    ]);
    assert.strictEqual(collisions.length, 1);
    assert.strictEqual(collisions[0].serviceName, 'ai-stock-trading.a-service');
    assert.deepStrictEqual(findServiceNameCollisions([
      { service: 'AService', serviceName: 'ai-stock-trading.a-service' },
      { service: 'BService', serviceName: 'ai-stock-trading.b-service' },
    ]), []);
  });

  ok('実ツリー: キュー名の衝突・ヘルパ迂回・新旧混在がいずれも無い（#258 / #354 の回帰）', () => {
    const result = checkConsumerEndpointNames();
    assert.deepStrictEqual(result.consumerCollisions, [], '未移行サービスのキュー名が衝突している');
    assert.deepStrictEqual(result.serviceNameCollisions, [], 'ServiceName が重複している');
    assert.deepStrictEqual(result.mixed, [], '新旧のメッセージング配線が混在している');
    assert.deepStrictEqual(result.forbidden, [], '共通ヘルパを迂回したトポロジ指定がある');
    // 検査器が空振りして無条件に緑になる経路を塞ぐ（IADR-0127 と同じ性質）。
    assert.ok(result.services.length >= 11, `走査できたサービスが少なすぎる: ${result.services.length}`);
  });

  // --- check-test-traceability.js: 受け入れ基準 → テスト写像の検査（#343 / IADR-0127） ---
  const fsTt = require('fs');
  const osTt = require('os');
  const pathTt = require('path');
  const tt = require('./check-test-traceability.js');

  ok('check-test-traceability: parseArgs 既定は requirePlanning=false', () => {
    assert.strictEqual(tt.parseArgs([]).requirePlanning, false);
    assert.strictEqual(tt.parseArgs(['--require-planning']).requirePlanning, true);
  });

  ok('check-test-traceability: 起点 ID を 2 桁へ正規化して収集する（FR-5 と FR-05 を同一視）', () => {
    const root = fsTt.mkdtempSync(pathTt.join(osTt.tmpdir(), 'tt-refs-'));
    const dir = pathTt.join(root, 'backend', 'Services', 'X', 'tests', 'X.Domain.Tests');
    fsTt.mkdirSync(dir, { recursive: true });
    fsTt.writeFileSync(pathTt.join(dir, 'A.cs'), '// FR-5, UC-06: something\n');
    fsTt.writeFileSync(pathTt.join(dir, 'B.cs'), '// FR-05 again, SC-01\n');
    const refs = tt.collectReferences(tt.testFiles(root), root);
    assert.strictEqual(refs.get('FR-05').length, 2);
    assert.strictEqual(refs.get('UC-06').length, 1);
    assert.strictEqual(refs.get('SC-01').length, 1);
  });

  ok('check-test-traceability: bin/obj 配下は収集しない（生成物の誤検出を避ける）', () => {
    const root = fsTt.mkdtempSync(pathTt.join(osTt.tmpdir(), 'tt-binobj-'));
    const dir = pathTt.join(root, 'backend', 'Services', 'X', 'tests', 'X.Domain.Tests');
    fsTt.mkdirSync(pathTt.join(dir, 'bin'), { recursive: true });
    fsTt.mkdirSync(pathTt.join(dir, 'obj'), { recursive: true });
    fsTt.writeFileSync(pathTt.join(dir, 'bin', 'Gen.cs'), '// FR-10\n');
    fsTt.writeFileSync(pathTt.join(dir, 'obj', 'Gen.cs'), '// FR-19\n');
    assert.deepStrictEqual(tt.testFiles(root), []);
  });

  ok('check-test-traceability: tests/ 以外に *.Tests ディレクトリも収集する（backend/Tests/ 直下）', () => {
    const root = fsTt.mkdtempSync(pathTt.join(osTt.tmpdir(), 'tt-toplevel-'));
    const dir = pathTt.join(root, 'backend', 'Tests', 'AiStockTrading.PlanConformance.Tests');
    fsTt.mkdirSync(dir, { recursive: true });
    fsTt.writeFileSync(pathTt.join(dir, 'A.cs'), '// FR-20\n');
    assert.strictEqual(tt.testFiles(root).length, 1);
  });

  ok('check-test-traceability: 必須 FR の仕様書欠落を列挙する', () => {
    const root = fsTt.mkdtempSync(pathTt.join(osTt.tmpdir(), 'tt-specs-'));
    fsTt.mkdirSync(pathTt.join(root, 'docs', 'tests'), { recursive: true });
    fsTt.mkdirSync(pathTt.join(root, 'docs', 'functional'), { recursive: true });
    fsTt.writeFileSync(pathTt.join(root, 'docs', 'tests', 'FR-10_x.md'), '');
    fsTt.writeFileSync(pathTt.join(root, 'docs', 'functional', 'FR-10_x.md'), '');
    const missing = tt.missingSpecs(root);
    // FR-10 は揃っているので出ず、残り 4 FR × 2 種 = 8 件が欠落として出る。
    assert.strictEqual(missing.length, 8);
    assert.ok(!missing.some((m) => m.startsWith('FR-10:')));
  });

  ok('check-test-traceability: planning 未 populate なら planIds は null（実在検査を skip する合図）', () => {
    const root = fsTt.mkdtempSync(pathTt.join(osTt.tmpdir(), 'tt-noplan-'));
    assert.strictEqual(tt.planIds(root), null);
  });

  ok('check-test-traceability: 実ツリーで必須 FR にテストと仕様書が揃っている（#343 の受け入れ）', () => {
    const refs = tt.collectReferences(tt.testFiles(pathTt.resolve(__dirname, '..')), pathTt.resolve(__dirname, '..'));
    for (const n of tt.REQUIRED_FRS) {
      const id = `FR-${String(n).padStart(2, '0')}`;
      assert.ok(refs.has(id), `${id} を参照するテストが無い`);
    }
    assert.deepStrictEqual(tt.missingSpecs(pathTt.resolve(__dirname, '..')), []);
  });

  // --- check-coverage.js: カバレッジ floor / ratchet（#343） ---
  const cov = require('./check-coverage.js');

  ok('check-coverage: parseArgs は --floor / --root / --suggest を解釈する', () => {
    const a = cov.parseArgs(['--floor', '0.7', '--root', 'src', '--suggest']);
    assert.strictEqual(a.floor, 0.7);
    assert.strictEqual(a.root, 'src');
    assert.strictEqual(a.suggest, true);
    assert.strictEqual(cov.parseArgs(['--floor=0.55']).floor, 0.55);
  });

  ok('check-coverage: 同一行を複数レポートが計測しても二重計上しない（和集合で数える）', () => {
    const report = (hits) =>
      `<class filename="A.cs"><lines><line number="1" hits="${hits[0]}" /><line number="2" hits="${hits[1]}" /></lines></class>`;
    const acc = new Map();
    cov.accumulate(report([1, 0]), acc);
    cov.accumulate(report([0, 0]), acc);
    const s = cov.summarize(acc);
    // 2 レポートあっても行数は 2 のまま。1 行目はどちらかで被覆されていれば covered。
    assert.strictEqual(s.total, 2);
    assert.strictEqual(s.covered, 1);
    assert.strictEqual(s.lineRate, 0.5);
  });

  ok('check-coverage: 別レポートで被覆された行は covered に昇格する', () => {
    const acc = new Map();
    cov.accumulate('<class filename="A.cs"><lines><line number="1" hits="0" /></lines></class>', acc);
    cov.accumulate('<class filename="A.cs"><lines><line number="1" hits="3" /></lines></class>', acc);
    assert.strictEqual(cov.summarize(acc).covered, 1);
  });

  ok('check-coverage: 別ファイルの同じ行番号は別行として数える', () => {
    const acc = new Map();
    cov.accumulate('<class filename="A.cs"><lines><line number="1" hits="1" /></lines></class>', acc);
    cov.accumulate('<class filename="B.cs"><lines><line number="1" hits="0" /></lines></class>', acc);
    const s = cov.summarize(acc);
    assert.strictEqual(s.total, 2);
    assert.strictEqual(s.covered, 1);
  });

  ok('check-coverage: coverage-floor.json の lineRateFloor を読む', () => {
    const root = fsTt.mkdtempSync(pathTt.join(osTt.tmpdir(), 'cov-floor-'));
    fsTt.writeFileSync(pathTt.join(root, 'coverage-floor.json'), JSON.stringify({ lineRateFloor: 0.42 }));
    assert.strictEqual(cov.readFloor(root), 0.42);
    assert.strictEqual(cov.readFloor(fsTt.mkdtempSync(pathTt.join(osTt.tmpdir(), 'cov-none-'))), null);
  });

  ok('check-coverage: 実ツリーの coverage-floor.json は 0〜1 の比率である', () => {
    const floor = cov.readFloor(pathTt.resolve(__dirname, '..'));
    assert.ok(typeof floor === 'number' && floor > 0 && floor < 1, `floor=${floor}`);
  });

  // --- check-banned-libraries.js: 不採用ライブラリの再混入検査（#345 / #351） ---
  const pathBl = require('path');
  const bl = require('./check-banned-libraries.js');

  ok('check-banned-libraries: csproj の PackageReference を検出する', () => {
    const hits = bl.findViolations('  <PackageReference Include="FluentAssertions" />', bl.BANNED);
    assert.strictEqual(hits.length, 1);
    assert.strictEqual(hits[0].lib.name, 'FluentAssertions');
    assert.strictEqual(hits[0].line, 1);
  });

  ok('check-banned-libraries: using ディレクティブを検出する（global using も）', () => {
    assert.strictEqual(bl.findViolations('using FluentAssertions;', bl.BANNED).length, 1);
    assert.strictEqual(bl.findViolations('global using FluentAssertions;', bl.BANNED).length, 1);
  });

  ok('check-banned-libraries: 散文中の言及は誤検出しない（移行の経緯を書けること）', () => {
    const prose = '// FluentAssertions から AwesomeAssertions へ移行した（#351）';
    assert.deepStrictEqual(bl.findViolations(prose, bl.BANNED), []);
  });

  ok('check-banned-libraries: 名前の前方一致では誤検出しない', () => {
    // FluentAssertionsExtras のような別パッケージを巻き込まないこと。
    assert.deepStrictEqual(bl.findViolations('using FluentAssertionsExtras;', bl.BANNED), []);
    assert.deepStrictEqual(bl.findViolations('<PackageReference Include="FluentAssertionsExtras" />', bl.BANNED), []);
  });

  ok('check-banned-libraries: サブパッケージ（ドット区切り）も検出する', () => {
    // #354: 本体だけを止めても MassTransit.RabbitMQ のようなサブパッケージ経由で同じ依存が戻る。
    assert.strictEqual(
      bl.findViolations('<PackageVersion Include="MassTransit.RabbitMQ" Version="8.4.1" />', bl.BANNED).length,
      1
    );
    assert.strictEqual(bl.findViolations('using MassTransit.RabbitMQ;', bl.BANNED).length, 1);
  });

  ok('check-banned-libraries: 移行未完了のものは PENDING にあり BANNED には無い', () => {
    const bannedNames = bl.BANNED.map((b) => b.name);
    for (const p of bl.PENDING) {
      assert.ok(!bannedNames.includes(p.name), `${p.name} は移行未完了のため BANNED に置かない`);
      assert.ok(p.owningIssue > 0, `${p.name} には担当 issue が必要`);
    }
  });

  ok('check-banned-libraries: PENDING に置いてよいのは実際に使われているものだけ', () => {
    // 使われていないものを PENDING に置くのは、防げる再混入を見送っているだけになる
    // （PR #355 のレビュー指摘）。実ツリーで参照が 0 件なら BANNED へ移すべきである。
    const root = pathBl.resolve(__dirname, '..');
    for (const p of bl.PENDING) {
      const hits = bl.checkTree(root, [{ name: p.name, replacement: p.replacement, reason: 'pending' }]);
      assert.ok(
        hits.length > 0,
        `${p.name} は実ツリーで参照 0 件である。PENDING ではなく BANNED へ移すこと（担当 #${p.owningIssue}）`
      );
    }
  });

  ok('check-banned-libraries: PENDING は現在 0 件である（#354 の完了で MassTransit が昇格した）', () => {
    // 上の 2 つは PENDING の**各要素**に対する検査であり、PENDING が空だと無条件に通る（空振り）。
    // 今の状態を明示的に表明して、検査が静かに空振りしている経路を塞ぐ（IADR-0127 と同じ思想）。
    // 新たに PENDING を置く場合は本テストを更新し、置く理由と BANNED へ移す条件を書くこと。
    assert.deepStrictEqual(bl.PENDING, []);
    assert.ok(bl.BANNED.some((b) => b.name === 'MassTransit'), 'MassTransit は BANNED であること（#354）');
  });

  ok('check-banned-libraries: BANNED には未導入のライブラリも含められる（先回り登録の許容）', () => {
    const names = bl.BANNED.map((b) => b.name);
    for (const n of ['MediatR', 'AutoMapper', 'Mapster']) {
      assert.ok(names.includes(n), `${n} は参照 0 件のため先回りで BANNED に置く`);
    }
  });

  ok('実ツリー: 不採用ライブラリの混入が無い（#351 の回帰）', () => {
    assert.deepStrictEqual(bl.checkTree(pathBl.resolve(__dirname, '..')), []);
  });
};
