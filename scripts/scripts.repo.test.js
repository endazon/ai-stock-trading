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

  // --- check-consumer-endpoint-names: サービス跨ぎのキュー名衝突検査（Issue #258 再発防止） ---
  // ADR-0013, IADR-0106: MassTransit の既定エンドポイント名は consumer クラス名のみから導かれ
  // namespace を含まない。別サービスで同名の consumer を作ると同一キューを共有して competing consumer になり、
  // pub/sub のつもりが取り合いになる（RiskManagement と MarketMonitor が TradeDecisionMade を取り合った）。
  const {
    endpointNameOf,
    consumerClassesIn,
    pathService,
    findCollisions,
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

  ok('実ツリー: サービス跨ぎのキュー名衝突が無い（#258 の回帰）', () => {
    assert.deepStrictEqual(checkConsumerEndpointNames(), []);
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

  ok('check-banned-libraries: 移行未完了のものは PENDING にあり BANNED には無い', () => {
    const bannedNames = bl.BANNED.map((b) => b.name);
    for (const p of bl.PENDING) {
      assert.ok(!bannedNames.includes(p.name), `${p.name} は移行未完了のため BANNED に置かない`);
      assert.ok(p.owningIssue > 0, `${p.name} には担当 issue が必要`);
    }
  });

  ok('実ツリー: 不採用ライブラリの混入が無い（#351 の回帰）', () => {
    assert.deepStrictEqual(bl.checkTree(pathBl.resolve(__dirname, '..')), []);
  });
};
