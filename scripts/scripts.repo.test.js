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

  // --- check-doc-links.js: parseArgs（資料再編 ADR-0029 で docs/ ・ .ai-context/ の 2 系統走査へ） ---
  const fsDl = require('fs');
  const osDl = require('os');
  const pathDl = require('path');
  const { parseArgs: dlParseArgs, DEFAULT_DIRS: dlDefaultDirs } = require('./check-doc-links.js');

  ok('check-doc-links: parseArgs 既定は docs/ と .ai-context/ を両方走査する', () => {
    const a = dlParseArgs([]);
    assert.deepStrictEqual(a.dirs, ['docs', '.ai-context']);
    assert.deepStrictEqual(dlDefaultDirs, ['docs', '.ai-context']);
  });
  ok('check-doc-links: --dir を明示すると既定を上書きする', () => {
    const a = dlParseArgs(['--dir', 'notes']);
    assert.deepStrictEqual(a.dirs, ['notes']);
  });
  ok('check-doc-links: --dir を複数回渡すと両方が対象になる', () => {
    const a = dlParseArgs(['--dir', 'notes', '--dir', 'more']);
    assert.deepStrictEqual(a.dirs, ['notes', 'more']);
  });
  ok('check-doc-links: 資料再編後は .ai-context/ 配下も実在検査の対象になる', () => {
    const root = fsDl.mkdtempSync(pathDl.join(osDl.tmpdir(), 'dl-aictx-'));
    fsDl.mkdirSync(pathDl.join(root, '.ai-context', 'adr'), { recursive: true });
    fsDl.writeFileSync(
      pathDl.join(root, '.ai-context', 'adr', 'IADR-0001_x.md'),
      '# X\n\n[missing](./IADR-9999_missing.md)\n'
    );
    let out = '';
    try {
      out = execSync(
        `node ${JSON.stringify(pathDl.join(__dirname, 'check-doc-links.js'))} --dir ${JSON.stringify(pathDl.join(root, '.ai-context'))}`,
        { encoding: 'utf8' }
      );
    } catch (e) {
      out = (e.stdout || '').toString() + (e.stderr || '').toString();
    }
    assert.match(out, /破損リンク 1 件/, '.ai-context/ 配下の破損リンクを検出する');
  });

  // --- check-doc-links: 同一ディレクトリ内の裸ファイル名リンク（NFR / #399 / IADR-0147） ---
  //
  // 判定が `./` `../` 始まりか `/` を含む形しか見ておらず、.ai-context/adr/ の IADR 相互参照が使う
  // **裸ファイル名**（`IADR-0119_xxx.md`）が実在検査へ到達しないまま素通りしていた。CI は
  // `OK` を返し続け、develop 上に破損 1 件が現存した（発見は PR #395 の AI レビュー）。
  //
  // 検査器は「効きすぎる方向」に壊れれば赤で気付けるが、**「効かない方向」に壊れると CI が
  // 緑のまま誰も気付けない**。よって否定形（誤検出しないこと）を正の確認と同数以上置く
  // （IADR-0143 / IADR-0145 と同じ思想）。
  const { isBrokenRef: dlIsBroken, collectBroken: dlCollect } = require('./check-doc-links.js');

  // フィクスチャ: docs/ に実在ファイルを 1 つ置いた一時ディレクトリを作る。
  const mkBareFixture = () => {
    const root = fsDl.mkdtempSync(pathDl.join(osDl.tmpdir(), 'dl-bare-'));
    const docs = pathDl.join(root, 'docs');
    fsDl.mkdirSync(docs, { recursive: true });
    fsDl.writeFileSync(pathDl.join(docs, 'IADR-0119_decision-derived-close.md'), '# 実在する参照先\n');
    fsDl.writeFileSync(pathDl.join(docs, 'diagram.png'), '');
    return { root, docs };
  };

  // ---- 正の確認（検査が効くこと）: P1〜P5 ----

  ok('check-doc-links[P1]: 実在しない裸ファイル名リンクを破損と判定する', () => {
    const { docs } = mkBareFixture();
    assert.strictEqual(dlIsBroken('IADR-0119_decision-derived-position-effect.md', docs), true);
  });

  ok('check-doc-links[P2]: 裸ファイル名は .md 以外の LINK_EXT（.png 等）も対象になる', () => {
    const { docs } = mkBareFixture();
    // 判定を .md 限定にすると同一ディレクトリの図・スキーマ参照が対象外に残る（IADR-0147 決定1）。
    assert.strictEqual(dlIsBroken('no-such-diagram.png', docs), true);
    assert.strictEqual(dlIsBroken('diagram.png', docs), false, '実在する .png は破損としない');
  });

  ok('check-doc-links[P3]: collectBroken が本文の裸ファイル名破損リンクを拾う', () => {
    const { docs } = mkBareFixture();
    const fp = pathDl.join(docs, 'a.md');
    fsDl.writeFileSync(
      fp,
      '# A\n\n- [IADR-0119](IADR-0119_decision-derived-position-effect.md)（存在しない）\n' +
        '- [IADR-0119](IADR-0119_decision-derived-close.md)（実在する）\n'
    );
    assert.deepStrictEqual(dlCollect(fp), ['IADR-0119_decision-derived-position-effect.md']);
  });

  ok('check-doc-links[P4]: フロントマターの裸ファイル名も対象になる', () => {
    const { docs } = mkBareFixture();
    const fp = pathDl.join(docs, 'b.md');
    fsDl.writeFileSync(
      fp,
      '---\nrelated_specs:\n  - "IADR-0119_decision-derived-close.md"\n' +
        '  - "IADR-0119_decision-derived-position-effect.md"\n---\n\n# B\n'
    );
    assert.deepStrictEqual(dlCollect(fp), ['IADR-0119_decision-derived-position-effect.md']);
  });

  // 変異検査。「破損リンクは無い」という主張は、検査器が壊れていても緑になる。意図的に壊した
  // 裸ファイル名リンクを **main が exit 1 で名指しする**ことを常設で固定する（IADR-0147 決定5）。
  ok('check-doc-links[P5・変異検査]: 破損した裸ファイル名リンクで exit 1 になり名指しされる', () => {
    const { root, docs } = mkBareFixture();
    const script = pathDl.join(__dirname, 'check-doc-links.js');
    const run = () =>
      execSync(`node ${JSON.stringify(script)} --dir ${JSON.stringify(docs)}`, {
        env: { ...process.env, DOC_LINKS_ROOT: root },
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'pipe'],
      });

    // (1) 健全な状態では緑
    fsDl.writeFileSync(pathDl.join(docs, 'c.md'), '# C\n\n[IADR-0119](IADR-0119_decision-derived-close.md)\n');
    assert.match(run(), /OK: /, '健全な裸ファイル名リンクで落ちてはならない');

    // (2) 変異（ファイル名を 1 語欠く）を入れると赤で名指し
    fsDl.writeFileSync(pathDl.join(docs, 'c.md'), '# C\n\n[IADR-0119](IADR-0119_decision-derived.md)\n');
    let failed = false;
    let out = '';
    try { run(); } catch (e) {
      failed = true;
      out = String(e.stdout || '') + String(e.stderr || '');
    }
    assert.ok(failed, '破損した裸ファイル名リンクで exit 1 になっていない（検査が効いていない）');
    assert.match(out, /破損リンク 1 件/, '件数が報告されること');
    assert.match(out, /IADR-0119_decision-derived\.md/, '破損リンクが名指しされること');

    // (3) 戻せば緑
    fsDl.writeFileSync(pathDl.join(docs, 'c.md'), '# C\n\n[IADR-0119](IADR-0119_decision-derived-close.md)\n');
    assert.match(run(), /OK: /, '復元しても緑に戻らない（検査が効きすぎている）');
  });

  // ---- 否定形の確認（誤検出しないこと）: N1〜N10。正の確認 5 件に対し 10 件
  //      （旧 N11「未 populate な submodule 配下は skip される」は、資料再編 ADR-0029 で
  //      planning submodule 自体が撤去され対象外になったため削除した） ----

  ok('check-doc-links[N1]: 実在する裸ファイル名リンクは破損としない', () => {
    const { docs } = mkBareFixture();
    assert.strictEqual(dlIsBroken('IADR-0119_decision-derived-close.md', docs), false);
    // アンカー・クエリ付きでも実体で判定する
    assert.strictEqual(dlIsBroken('IADR-0119_decision-derived-close.md#決定', docs), false);
  });

  ok('check-doc-links[N2]: 外部 URL は対象外', () => {
    const { docs } = mkBareFixture();
    for (const u of [
      'https://github.com/endazon/ai-stock-trading/issues/399',
      'http://example.com/no-such.md',
      'ftp://example.com/no-such.md',
    ]) {
      assert.strictEqual(dlIsBroken(u, docs), false, `外部 URL を破損と判定した: ${u}`);
    }
  });

  ok('check-doc-links[N3]: mailto: は対象外', () => {
    const { docs } = mkBareFixture();
    assert.strictEqual(dlIsBroken('mailto:someone@example.com', docs), false);
  });

  ok('check-doc-links[N4]: アンカーのみのリンクは対象外', () => {
    const { docs } = mkBareFixture();
    assert.strictEqual(dlIsBroken('#決定', docs), false);
    assert.strictEqual(dlIsBroken('#no-such-section', docs), false);
  });

  ok('check-doc-links[N5]: ルート絶対パスは対象外', () => {
    const { docs } = mkBareFixture();
    assert.strictEqual(dlIsBroken('/docs/no-such.md', docs), false);
    assert.strictEqual(dlIsBroken('/no-such.md', docs), false);
  });

  ok('check-doc-links[N6]: テンプレ変数 <...> は対象外（例示リンクの escape。IADR-0147 決定4）', () => {
    const { docs } = mkBareFixture();
    assert.strictEqual(dlIsBroken('<IADR-0119_xxx>.md', docs), false);
    assert.strictEqual(dlIsBroken('<file-name>.md', docs), false);
  });

  ok('check-doc-links[N7]: テンプレ変数 ${...} は対象外', () => {
    const { docs } = mkBareFixture();
    assert.strictEqual(dlIsBroken('${NAME}.md', docs), false);
  });

  ok('check-doc-links[N8]: テンプレ変数 {{...}} は対象外', () => {
    const { docs } = mkBareFixture();
    assert.strictEqual(dlIsBroken('{{ name }}.md', docs), false);
  });

  ok('check-doc-links[N9]: 拡張子を持たない裸の語はリンク扱いしない', () => {
    const { docs } = mkBareFixture();
    // 本文中の普通の語を破損リンクにすると誤検出が常態化し、赤が読まれなくなる（IADR-0147 決定2）。
    for (const w of ['README', 'IADR-0119', 'develop', 'TradingDefaults']) {
      assert.strictEqual(dlIsBroken(w, docs), false, `拡張子の無い語を破損と判定した: ${w}`);
    }
  });

  ok('check-doc-links[N10]: インラインコード内の裸ファイル名は拾わない（既存の扱いを壊さない）', () => {
    const { docs } = mkBareFixture();
    const fp = pathDl.join(docs, 'code.md');
    // 第 3 経路は `./` `../` 始まりのみを拾う。言及は参照ではない（IADR-0147 決定3）。
    fsDl.writeFileSync(
      fp,
      '# Code\n\n実ファイル名は `IADR-0119_decision-derived-position-effect.md` ではない。\n' +
        '設定は `no-such-config.yaml` に置く。\n'
    );
    assert.deepStrictEqual(dlCollect(fp), []);
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
  // ADR-0013, IADR-0106, IADR-0129: #258 では 2 サービスが同一キューを共有し、pub/sub のつもりが
  // competing consumer になって取引判断が無言で消えた。Wolverine（#354 で全面移行）はキュー名の導出に
  // ハンドラのクラス名を一切使わないため、一意性は `<ServiceName>.<メッセージ型名>` の ServiceName に
  // 帰着する。検査する不変条件は N1（ServiceName の一意性）・N2（トポロジの直接指定の禁止）・
  // N3（共通ヘルパ経由でのみ Wolverine を配線する）＋メタ検査（走査数の下限）である。
  // 旧 MassTransit 規則（consumer クラス名の一意性）は移行完了に伴い #354 第 3 段階で撤去した。
  const {
    wolverineQueueNameOf,
    pathService,
    serviceNameConstantIn,
    wiringOf,
    forbiddenTopologyCallsIn,
    findServiceNameCollisions,
    checkTree: checkConsumerEndpointNames,
  } = require('./check-consumer-endpoint-names.js');

  ok('pathService: backend/Services/<Service>/ からサービス名を得る', () => {
    assert.strictEqual(pathService('backend/Services/RiskManagementService/src/W/X.cs'), 'RiskManagementService');
    assert.strictEqual(pathService('backend/Shared/X.cs'), null);
  });

  ok('wolverineQueueNameOf: キュー名は ServiceName とメッセージ型名から導く（IADR-0129 決定 1）', () => {
    assert.strictEqual(
      wolverineQueueNameOf('ai-stock-trading.cost-control-service', 'LlmCostIncurred'),
      'ai-stock-trading.cost-control-service.LlmCostIncurred'
    );
    // #258 の再発経路: 同じイベントを購読しても、サービスが違えばキューは別。
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

  ok('wiringOf: Wolverine 配線と共通ヘルパの通過を判定する（N3）', () => {
    const viaHelper = 'builder.Host.UseWolverine(opts => opts.UseAiStockTradingRabbitMq(ServiceName, null));';
    assert.deepStrictEqual(wiringOf(viaHelper), { wiresWolverine: true, usesHelper: true });
    assert.deepStrictEqual(
      wiringOf('builder.Host.UseWolverine(opts => opts.UseRabbitMq(new Uri("amqp://rabbitmq")));'),
      { wiresWolverine: true, usesHelper: false }
    );
    assert.deepStrictEqual(wiringOf('var x = 1;'), { wiresWolverine: false, usesHelper: false });
    // 説明文で API 名に触れることは禁止しない（コメント行は対象外）。
    assert.strictEqual(wiringOf('// builder.Host.UseWolverine( を呼ぶ').wiresWolverine, false);
  });

  ok('forbiddenTopologyCallsIn: 共通ヘルパを迂回したトポロジ指定を検出する（IADR-0129 決定 4）', () => {
    assert.strictEqual(forbiddenTopologyCallsIn('opts.UseRabbitMq().UseConventionalRouting();').length, 1);
    assert.strictEqual(forbiddenTopologyCallsIn('opts.UseAiStockTradingRabbitMq(S, null);').length, 0);
    // 説明文で API 名に触れることは禁止しない（コメント行は対象外）。
    assert.strictEqual(forbiddenTopologyCallsIn('// UseConventionalRouting( は共通ヘルパに閉じる').length, 0);
  });

  ok('findServiceNameCollisions: ServiceName の重複を検出する（#258 の唯一の再発経路）', () => {
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

  ok('実ツリー: ServiceName の重複・ヘルパ迂回・ヘルパ非経由の配線がいずれも無い（#258 / #354 の回帰）', () => {
    const result = checkConsumerEndpointNames();
    assert.deepStrictEqual(result.serviceNameCollisions, [], 'ServiceName が重複している');
    assert.deepStrictEqual(result.forbidden, [], '共通ヘルパを迂回したトポロジ指定がある');
    assert.deepStrictEqual(result.bypassed, [], '共通ヘルパを通さない Wolverine 配線がある');
    // 検査器が空振りして無条件に緑になる経路を塞ぐ（IADR-0127 と同じ性質）。
    assert.ok(result.services.length >= 11, `走査できたサービスが少なすぎる: ${result.services.length}`);
    assert.ok(
      result.services.filter((s) => s.wiresWolverine).length >= 10,
      'Wolverine を配線しているサービスが少なすぎる（N1〜N3 の母数が消えている）'
    );
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

  // --- check-coverage.js: 分母からの除外（#390 / IADR-0143） ---
  //
  // NFR: EF Core の自動生成ファイルが分母に入っていたため、マイグレーションを 1 つ足すだけで
  // 数百行の 0% が積まれ、テストを 1 行も減らしていない PR が機械的に床を割っていた。
  // 除外機構で最も危険な退行は「パターンが効きすぎてプロダクションコードまで分母から消える」
  // ことなので、**否定形**（外れてはいけないものが外れないこと）を正の確認と同数以上置く。

  const REPO_ROOT_COV = pathTt.resolve(__dirname, '..');
  const GENERATED_PATTERNS = [
    { pattern: '**/Migrations/*.Designer.cs', reason: 'ef 生成' },
    { pattern: '**/Migrations/*ModelSnapshot.cs', reason: 'ef 生成' },
  ];
  const matchesAny = (p) =>
    GENERATED_PATTERNS.some((e) => cov.globToRegExp(e.pattern).test(cov.normalizePath(p)));
  /** filename → 行数 の擬似 acc を作る（hits はすべて 0＝未被覆）。 */
  const accOf = (spec) => {
    const acc = new Map();
    for (const [name, n] of Object.entries(spec)) {
      const lines = new Map();
      for (let i = 1; i <= n; i++) lines.set(i, false);
      acc.set(name, lines);
    }
    return acc;
  };

  ok('check-coverage: 自動生成ファイル（Designer / ModelSnapshot）は除外パターンに一致する', () => {
    for (const p of [
      'Services/AuditService/src/AuditService.Infrastructure/Migrations/20260710095747_InitialCreate.Designer.cs',
      'Services/AuditService/src/AuditService.Infrastructure/Migrations/AuditDbContextModelSnapshot.cs',
      // 絶対パス出力（環境によって coverlet が絶対パスを吐く）でも同じく一致すること。
      '/home/runner/work/x/backend/Services/ReportService/src/ReportService.Infrastructure/Migrations/ReportDbContextModelSnapshot.cs',
      // Windows 区切りでも一致すること。
      'Services\\ReportService\\src\\ReportService.Infrastructure\\Migrations\\20260729110333_AddReportBody.Designer.cs',
    ]) {
      assert.ok(matchesAny(p), `除外されるべき: ${p}`);
    }
  });

  // 否定形 1: 手書きのマイグレーション本体（Up/Down を持つ）は分母に残す。
  // 生成物と同じディレクトリに同居しているため、`**/Migrations/**` のような雑なパターンへ
  // 退行すると黙って一緒に消える。
  ok('check-coverage: 手書きのマイグレーション本体は除外されない（否定形）', () => {
    for (const p of [
      'Services/RiskManagementService/src/RiskManagementService.Infrastructure/Migrations/20260805044003_AddStage1ExcludedInternalPaperDays.cs',
      'Services/AuditService/src/AuditService.Infrastructure/Migrations/20260710095747_InitialCreate.cs',
    ]) {
      assert.ok(!matchesAny(p), `除外されてはいけない: ${p}`);
    }
  });

  // 否定形 2: `Migrations` / `Designer` の語を含むだけの通常のプロダクションコードを飲み込まないこと。
  // 部分一致や `*` の `/` 跨ぎへ退行すると、これらが静かに分母から消えてカバレッジが水増しされる。
  ok('check-coverage: Migrations / Designer の語を含む通常コードは除外されない（否定形）', () => {
    for (const p of [
      'Services/RiskManagementService/src/RiskManagementService.Infrastructure/MigrationsRunner.cs',
      'Services/RiskManagementService/src/RiskManagementService.Infrastructure/Migrations.cs',
      'Services/ReportService/src/ReportService.Application/MigrationsHealthCheck.cs',
      'Shared/AiStockTrading.Shared.Infrastructure/DesignerLayout.cs',
      'Shared/AiStockTrading.Shared.Infrastructure/DbContextModelSnapshot.cs', // Migrations/ 配下ではない
      'Services/AuditService/src/AuditService.Api/Endpoints/MigrationsEndpoints.cs',
      // ディレクトリ名が Migrations で「始まる」だけのもの（* は / を跨がない）。
      'Services/AuditService/src/AuditService.Infrastructure/MigrationsSupport/Helper.Designer.cs',
    ]) {
      assert.ok(!matchesAny(p), `除外されてはいけない: ${p}`);
    }
  });

  ok('check-coverage: applyExcludes は残した集合と外した内訳の両方を返す（黙って縮めない）', () => {
    const acc = accOf({
      'Svc/src/Infra/Migrations/20260101_Init.Designer.cs': 100,
      'Svc/src/Infra/Migrations/SvcDbContextModelSnapshot.cs': 50,
      'Svc/src/Infra/Migrations/20260101_Init.cs': 20,
      'Svc/src/App/OrderService.cs': 30,
    });
    const ex = cov.applyExcludes(acc, GENERATED_PATTERNS);
    assert.strictEqual(ex.files, 2);
    assert.strictEqual(ex.lines, 150);
    assert.strictEqual(ex.covered, 0);
    assert.deepStrictEqual(
      ex.byPattern.map((p) => [p.pattern, p.files, p.lines]),
      [
        ['**/Migrations/*.Designer.cs', 1, 100],
        ['**/Migrations/*ModelSnapshot.cs', 1, 50],
      ]
    );
    // 残った集合には手書き本体と通常コードが含まれる（＝分母から消えていない）。
    assert.deepStrictEqual(
      [...ex.kept.keys()].sort(),
      ['Svc/src/App/OrderService.cs', 'Svc/src/Infra/Migrations/20260101_Init.cs']
    );
    assert.strictEqual(cov.summarize(ex.kept).total, 50);
  });

  ok('check-coverage: 一致 0 件のパターンは unmatched として報告される', () => {
    const ex = cov.applyExcludes(accOf({ 'Svc/src/App/OrderService.cs': 10 }), GENERATED_PATTERNS);
    assert.deepStrictEqual(ex.unmatched, [
      '**/Migrations/*.Designer.cs',
      '**/Migrations/*ModelSnapshot.cs',
    ]);
    assert.strictEqual(ex.lines, 0);
  });

  // 否定形 3: 除外が効きすぎたとき（パターン事故で分母を溶かしたとき）に失敗すること。
  ok('check-coverage: 除外率が上限を超えると違反になる（否定形）', () => {
    const v = cov.validateExclusion({
      entries: GENERATED_PATTERNS,
      excludedLines: 9000,
      totalLines: 10000,
      maxExcludedLineShare: 0.35,
    });
    assert.strictEqual(v.length, 1, `違反が返るべき: ${JSON.stringify(v)}`);
    assert.match(v[0], /上限/);
    // 上限内なら違反なし。
    assert.deepStrictEqual(
      cov.validateExclusion({
        entries: GENERATED_PATTERNS,
        excludedLines: 3000,
        totalLines: 10000,
        maxExcludedLineShare: 0.35,
      }),
      []
    );
  });

  // 否定形 4: 理由の無い除外（「とりあえず外して恒久化」）を通さない。
  ok('check-coverage: reason の無い除外エントリは違反になる（否定形）', () => {
    const v = cov.validateExclusion({
      entries: [{ pattern: '**/Foo.cs' }, { pattern: '**/Bar.cs', reason: '  ' }],
      excludedLines: 0,
      totalLines: 100,
    });
    assert.strictEqual(v.length, 2, JSON.stringify(v));
    assert.match(v.join(' '), /reason が無い/);
  });

  ok('check-coverage: 実ツリーの exclude 宣言は全件 reason を持ち、除外率が上限内である', () => {
    const entries = cov.readExcludes(REPO_ROOT_COV);
    assert.ok(entries.length > 0, 'exclude の宣言が消えている（#390 の退行）');
    assert.deepStrictEqual(
      cov.validateExclusion({ entries, excludedLines: 0, totalLines: 1 }),
      [],
      'reason 欠落がある'
    );
    const max = cov.readMaxExcludedLineShare(REPO_ROOT_COV);
    assert.ok(max > 0 && max <= 1, `maxExcludedLineShare=${max}`);
  });

  ok('check-coverage: parseArgs は --no-exclude を解釈する（既定は除外あり）', () => {
    assert.strictEqual(cov.parseArgs([]).exclude, true);
    assert.strictEqual(cov.parseArgs(['--no-exclude']).exclude, false);
  });

  // --- check-coverage.js: 実設定 × 実ツリーの検査（IADR-0143 決定2 の機械的担保） ---
  //
  // 上の否定形テストは `applyExcludes` に**ハードコードしたパターン**を渡していた。そのため
  // coverage-floor.json の 1 つ目のパターンを `**/Migrations/**` へ広げる変異を当てても全件緑のまま、
  // 手書きのマイグレーション本体 24 ファイル・611 行が黙って分母から外れた（62.03% → 85.50%）。
  // **モック／合成入力を検証していて実物を検査していない**という型の欠陥である。
  // 以下は必ず**実ツリー（backend/）× 実設定（coverage-floor.json）**を対象にする。
  const BACKEND_ROOT = pathTt.join(REPO_ROOT_COV, 'backend');
  const realEntries = () => cov.readExcludes(REPO_ROOT_COV);

  ok('check-coverage: 自動生成マーカーが実ファイルで生成物と手書きを判別する', () => {
    const designer = pathTt.join(
      BACKEND_ROOT,
      'Services/RiskManagementService/src/RiskManagementService.Infrastructure/Migrations/20260804090000_AddStage1Progress.Designer.cs'
    );
    const body = pathTt.join(
      BACKEND_ROOT,
      'Services/RiskManagementService/src/RiskManagementService.Infrastructure/Migrations/20260804090000_AddStage1Progress.cs'
    );
    assert.ok(fsTt.existsSync(designer) && fsTt.existsSync(body), '検査対象の実ファイルが無い');
    assert.strictEqual(cov.isAutoGenerated(designer), true, 'Designer は自動生成と判定されるべき');
    assert.strictEqual(cov.isAutoGenerated(body), false, '手書き本体は自動生成と判定されてはならない');
  });

  ok('check-coverage: 実ツリーの手書きマイグレーション本体は実設定の除外に 1 件も一致しない', () => {
    // Migrations/ 直下で .Designer.cs / ModelSnapshot.cs でない .cs ＝ 手書き本体。
    const bodies = cov
      .findSourceFiles(BACKEND_ROOT)
      .filter(
        (f) =>
          /\/Migrations\/[^/]+\.cs$/.test('/' + f) &&
          !/\.Designer\.cs$/.test(f) &&
          !/ModelSnapshot\.cs$/.test(f)
      );
    // メタ検査: 列挙が空なら以下の検査は空振りする（検査の形骸化を防ぐ）。
    assert.ok(bodies.length >= 20, `手書き本体の列挙が少なすぎる（${bodies.length} 件）`);
    const res = cov.findWrongfullyExcluded(BACKEND_ROOT, realEntries());
    const excludedBodies = res.violations.map((v) => v.file);
    assert.deepStrictEqual(
      excludedBodies,
      [],
      `手書きファイルが除外対象に入っている（IADR-0143 決定2 違反）: ${excludedBodies.join(', ')}`
    );
  });

  ok('check-coverage: 実設定が除外するのは自動生成ファイルだけである（実ツリー走査）', () => {
    const res = cov.findWrongfullyExcluded(BACKEND_ROOT, realEntries());
    assert.ok(res.scanned > 500, `走査したソースが少なすぎる（${res.scanned} 件）`);
    // メタ検査: 1 件も一致していなければ「違反ゼロ」は無意味（除外が効いていない）。
    assert.ok(res.generated > 0, '除外パターンが実ツリーの自動生成ファイルに 1 件も一致していない');
    assert.deepStrictEqual(res.violations, []);
  });

  // **この検査が本物であることの証明（変異検査の常設化）**。
  // 上の 2 本は「違反が無いこと」を主張するため、検査器が壊れて常に空を返しても緑になる。
  // 実ツリーに対して意図的に広すぎるパターンを当て、**手書き本体が違反として挙がること**を固定する。
  ok('check-coverage: パターンを **/Migrations/** へ広げると実ツリーの手書き本体が違反として挙がる', () => {
    const widened = [{ pattern: '**/Migrations/**', reason: '変異検査用（広すぎるパターン）' }];
    const res = cov.findWrongfullyExcluded(BACKEND_ROOT, widened);
    assert.ok(res.violations.length >= 20, `違反が検出されない（${res.violations.length} 件）`);
    assert.ok(
      res.violations.some((v) => /_AddStage1Progress\.cs$/.test(v.file)),
      '既知の手書き本体が違反に含まれるべき'
    );
    // 生成物側は違反にならない（＝手書きだけを咎めている）。
    assert.ok(
      !res.violations.some((v) => /\.Designer\.cs$|ModelSnapshot\.cs$/.test(v.file)),
      '自動生成ファイルを違反に数えてはならない'
    );
  });

  ok('check-coverage: 実設定にパターンの隠蔽（shadowing）は無い', () => {
    const acc = accOf({
      'Svc/src/Infra/Migrations/20260101_Init.Designer.cs': 1,
      'Svc/src/Infra/Migrations/SvcDbContextModelSnapshot.cs': 1,
      'Svc/src/Infra/Migrations/20260101_Init.cs': 1,
    });
    assert.deepStrictEqual(cov.findShadowedPatterns(acc, realEntries()), []);
  });

  // 否定形 5: 先行パターンが後続を飲み込む設定（今回の変異が出した症状）を失敗として検出する。
  ok('check-coverage: 先行パターンが後続を飲み込むと shadowing として検出される（否定形）', () => {
    const acc = accOf({
      'Svc/src/Infra/Migrations/20260101_Init.Designer.cs': 1,
      'Svc/src/Infra/Migrations/SvcDbContextModelSnapshot.cs': 1,
    });
    const shadowed = cov.findShadowedPatterns(acc, [
      { pattern: '**/Migrations/**', reason: '広すぎる' },
      { pattern: '**/Migrations/*ModelSnapshot.cs', reason: '飲み込まれる側' },
    ]);
    assert.strictEqual(shadowed.length, 1, JSON.stringify(shadowed));
    assert.strictEqual(shadowed[0].pattern, '**/Migrations/*ModelSnapshot.cs');
    assert.strictEqual(shadowed[0].shadowedBy, '**/Migrations/**');
  });

  ok('check-coverage: 対象が単に存在しない空振りは shadowing ではない（誤検出しない）', () => {
    // 部分実行などで対象が 1 件も現れないのは正当。これを失敗にすると無関係な実行が落ちる。
    const acc = accOf({ 'Svc/src/App/OrderService.cs': 1 });
    assert.deepStrictEqual(cov.findShadowedPatterns(acc, realEntries()), []);
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
  // --- check-banned-settled-cash-sources.js: 決済済み資金の代替値の遮断（FR-19 / #425 / ADR-0025 / IADR-0165） ---
  //
  // ADR-0025 は `MaxTrdQtys.MaxCashBuy` / `Funds.AvlWithdrawalCash` / `Funds.MaxWithdrawal` を
  // 決済済み資金の代替に使うことを名指しで禁じた。とりわけ現金買付余力は**未決済の売却代金を含む**のが
  // 通例であり、**分母に据えると GFV 回避ガードが GFV を許可する**。
  //
  // **本検査は「効かない方向」に壊れると CI が緑のまま誰も気付けない。** よって
  //   (1) コードとしての参照を実際に検出できること（正）
  //   (2) コメント中の言及を誤検出しないこと（否定形。既存の IADR・アダプタの注意書きが壊れない）
  // の両方を置く（check-banned-libraries と同じ思想）。
  const pathBscs = require('path');
  const bscs = require('./check-banned-settled-cash-sources.js');

  ok('check-banned-settled-cash-sources: ADR-0025 が名指しした 3 件を対象にする', () => {
    const names = bscs.BANNED.map((b) => b.name).sort();
    assert.deepStrictEqual(names, ['AvlWithdrawalCash', 'MaxCashBuy', 'MaxWithdrawal']);
  });

  ok('check-banned-settled-cash-sources: コードとしての参照を検出する', () => {
    const src = 'var settled = funds.AvlWithdrawalCash;\n';
    const hits = bscs.findViolations(src);
    assert.strictEqual(hits.length, 1);
    assert.strictEqual(hits[0].item.name, 'AvlWithdrawalCash');
    assert.strictEqual(hits[0].line, 1);
  });

  ok('check-banned-settled-cash-sources: MaxCashBuy を分母に据える形を検出する', () => {
    const src = 'return new BrokerAccountState(AccountType.Cash, qtys.MaxCashBuy * price);\n';
    assert.strictEqual(bscs.findViolations(src).length, 1);
  });

  ok('check-banned-settled-cash-sources: MaxWithdrawal のコード参照を検出する', () => {
    assert.strictEqual(bscs.findViolations('x = f.MaxWithdrawal;').length, 1);
  });

  // --- 否定形（誤検出しないこと） ---

  ok('check-banned-settled-cash-sources: 行コメント中の言及は検出しない', () => {
    const src = '// MaxCashBuy は未決済の売却代金を含むため使わない（ADR-0025）\nvar x = 1;\n';
    assert.deepStrictEqual(bscs.findViolations(src), []);
  });

  ok('check-banned-settled-cash-sources: XML ドキュメント中の言及は検出しない', () => {
    const src = '/// <summary><c>MaxTrdQtys.MaxCashBuy</c> を代替に使ってはならない。</summary>\nint a;\n';
    assert.deepStrictEqual(bscs.findViolations(src), []);
  });

  ok('check-banned-settled-cash-sources: ブロックコメント中の言及は検出しない', () => {
    const src = '/*\n AvlWithdrawalCash / MaxWithdrawal は出金可能額である。\n*/\nint a;\n';
    assert.deepStrictEqual(bscs.findViolations(src), []);
  });

  ok('check-banned-settled-cash-sources: 文字列リテラル中の名前は検出しない', () => {
    // **禁止を表明するテスト自身を落とさない。** 文字列からプロパティは読めないため、
    // 「その値を読んで決済済み資金に据える」という本検査の目的に当たらない
    // （実際、BrokerAccountState の否定形テストは names.Should().NotContain("MaxCashBuy") と書く）。
    assert.deepStrictEqual(
      bscs.findViolations('names.Should().NotContain("MaxCashBuy", "理由");'), []);
  });

  ok('check-banned-settled-cash-sources: 逐語的文字列（@"..."）中の名前も検出しない', () => {
    assert.deepStrictEqual(bscs.findViolations('var s = @"AvlWithdrawalCash";'), []);
  });

  ok('check-banned-settled-cash-sources: 文字列を跨いだ直後のコード参照は検出する', () => {
    // 文字列を潰す実装が「以降ずっと文字列扱い」へ壊れると、**検査が効かない方向**に静かに死ぬ。
    const hits = bscs.findViolations('Log("MaxCashBuy"); var x = funds.MaxCashBuy;');
    assert.strictEqual(hits.length, 1);
  });

  ok('check-banned-settled-cash-sources: 前方一致の別名（撤退ドメイン）は巻き込まない', () => {
    // WithdrawalTriggered 系（段階ゲートの「撤退」）は現金の出金とは別概念であり、巻き込むと
    // 無関係な実装が止まる。語境界で照合していることの表明。
    assert.deepStrictEqual(bscs.findViolations('var r = policy.MaxWithdrawalRatio;'), []);
  });

  ok('check-banned-settled-cash-sources: stripComments は行番号を保つ', () => {
    const stripped = bscs.stripComments('// a\n// b\nvar x = funds.MaxCashBuy;\n');
    assert.strictEqual(stripped.split('\n').length, 4);
    assert.strictEqual(bscs.findViolations('// a\n// b\nvar x = funds.MaxCashBuy;\n')[0].line, 3);
  });

  ok('実ツリー: 決済済み資金の代替値のコード参照が無い（#425 の回帰）', () => {
    assert.deepStrictEqual(bscs.checkTree(pathBscs.resolve(__dirname, '..')), []);
  });

  // --- check-tracked-session-timeout.js: 素の TrackActivity() の遮断（NFR / #357 / IADR-0168） ---
  //
  // #357 の flaky は `Wolverine.Tracking.TrackedSession` の**既定 5 秒**を、並列実行時の
  // スケジューリング遅延が超えたものである（実測 6 秒）。131 か所を予算つきの入口へ替えたが、
  // **次に書かれるテストは素の標準 API を素直に呼ぶ**——だから機械的に止める。
  //
  // **本検査も「効かない方向」に壊れると CI が緑のまま flake だけが戻る。** よって
  //   (1) 素の入口を実際に検出できること（正）
  //   (2) 予算つきの入口・コメント中の言及を誤検出しないこと（否定形）
  // の両方を置く。
  const pathTst = require('path');
  const tst = require('./check-tracked-session-timeout.js');

  ok('check-tracked-session-timeout: 素の TrackActivity() を検出する', () => {
    const hits = tst.findViolations('var s = host.TrackActivity();\n');
    assert.strictEqual(hits.length, 1);
    assert.strictEqual(hits[0].line, 1);
  });

  ok('check-tracked-session-timeout: 予算つきの TrackActivityForTest() は誤検出しない', () => {
    assert.deepStrictEqual(tst.findViolations('var s = host.TrackActivityForTest();\n'), []);
  });

  ok('check-tracked-session-timeout: コメント中の言及は誤検出しない（禁止の理由を書けること）', () => {
    assert.deepStrictEqual(tst.findViolations('// 素の TrackActivity() は使わない\n'), []);
    assert.deepStrictEqual(tst.findViolations('/// <summary>TrackActivity の代替</summary>\n'), []);
  });

  ok('check-tracked-session-timeout: 文字列リテラル中の言及は誤検出しない', () => {
    assert.deepStrictEqual(tst.findViolations('var name = "TrackActivity";\n'), []);
  });

  // #447 のレビュー指摘: 補間文字列の**穴**（`$"…{ ここはコード }…"`）はコードである。
  // 穴まで非コード扱いにすると `$"…{host.TrackActivity()}…"` が**検出漏れ**になる——
  // 検査が効かない方向に壊れると CI は緑のまま flake だけが戻る。
  ok('check-tracked-session-timeout: 補間文字列の穴の中の呼び出しを検出する', () => {
    assert.strictEqual(tst.findViolations('var s = $"x{host.TrackActivity()}y";\n').length, 1);
    assert.strictEqual(tst.findViolations('var s = $@"x{host.TrackActivity()}y";\n').length, 1);
    assert.strictEqual(tst.findViolations('var s = @$"x{host.TrackActivity()}y";\n').length, 1);
  });

  ok('check-tracked-session-timeout: 補間文字列の literal 部分・二重波括弧は誤検出しない', () => {
    assert.deepStrictEqual(tst.findViolations('var s = $"TrackActivity は使わない";\n'), []);
    assert.deepStrictEqual(tst.findViolations('var s = $"{{TrackActivity}}";\n'), []);
  });

  // #447 の 2 度目のレビュー指摘: **文字列は入れ子になり得る**。
  // 補間の穴の中にはさらに文字列リテラルを書ける（`$@"a{b + "c"}d"`）。
  // 単一のフラグで「いま逐語か」を持つと**内側の文字列が外側の種別を上書きし、穴を抜けた後の解析が壊れる**。
  // 実測（修正前 → 修正後）: 下の 1 件目は 0 → 1 件（**検出漏れだった**）。
  ok('check-tracked-session-timeout: 入れ子の文字列を挟んでも穴の中の呼び出しを見失わない', () => {
    assert.strictEqual(
      tst.findViolations('var y = $@"a{1 + "z"}b{host.TrackActivity()}c";\n').length, 1);
    assert.strictEqual(
      tst.findViolations('var y = $"a{1 + "z"}b{host.TrackActivity()}c";\n').length, 1);
  });

  ok('check-tracked-session-timeout: 逐語文字列の作法（"" と \\）を取り違えない', () => {
    // 逐語では `""` が literal の引用符であり、`\` はエスケープではない。
    assert.deepStrictEqual(tst.findViolations('var s = @"he said ""TrackActivity"" ok";\n'), []);
    assert.deepStrictEqual(tst.findViolations('var s = @"path\\TrackActivity";\n'), []);
    // 入れ子の文字列を挟んでも、外側が逐語であることを保つ（種別の取り違えで文字列の終端がずれない）。
    assert.strictEqual(
      tst.findViolations('var y = $@"a{1 + "z"}b"; host.TrackActivity();\n').length, 1);
  });

  ok('check-tracked-session-timeout: 許可ファイルは 1 件だけ（予算を適用する当の実装）', () => {
    assert.deepStrictEqual([...tst.ALLOWED_FILES], [
      'backend/TestSupport/AiStockTrading.TestSupport.Messaging/WolverineTrackingExtensions.cs',
    ]);
  });

  ok('check-tracked-session-timeout: 許可ファイルを外すと実ツリーで検出される（検査が効いていることの証明）', () => {
    const root = pathTst.resolve(__dirname, '..');
    const hits = tst.checkTree(root, new Set());
    assert.ok(
      hits.some((h) => h.file.endsWith('WolverineTrackingExtensions.cs')),
      '許可を外しても検出されないなら、検査は素の入口を見ていない'
    );
  });

  ok('実ツリー: 素の TrackActivity() の使用が無い（#357 の回帰）', () => {
    assert.deepStrictEqual(tst.checkTree(pathTst.resolve(__dirname, '..')), []);
  });


  const pathFb = require('path');

  // --- 計画 ID の修飾（NFR / #477 / IADR-0189 / IADR-0262） ---
  //
  // 🔴 **［2026-08-28 変更］既定値は空ではない。** `PROJECT_PREFIXES` は本リポの値（`MSP, AST`）を
  // ファイルへ直書きしている（IADR-0262 が IADR-0189 決定2・決定6 を部分的に supersede した）。
  // **CI ジョブが `PLAN_ID_PREFIXES` を渡すのは冗長化のためであり、落としても skip して緑には
  // ならない**（既定が拾う）。本テストは env を明示的に与えて呼ぶが、これは「env が無いと検査され
  // ない」からではなく、**単に CI と同じ値で走らせて確認する**ためである。
  ok('実ツリー: 他プロジェクトの計画 ID が `<PROJ>/<ID>` で書かれている（#477 の回帰）', () => {
    const { execFileSync } = require('child_process');
    execFileSync(process.execPath, [pathFb.join(__dirname, 'check-plan-id-qualification.js')], {
      cwd: pathFb.resolve(__dirname, '..'),
      // **`MSP` だけにしない。** 本リポは `AST/` を自プロジェクトの修飾として実際に使っており、
      // `/` と空白が混在していた（着手時点で 46 件）。修飾を使う以上、表記は一貫している必要がある。
      env: { ...process.env, PLAN_ID_PREFIXES: 'MSP,AST' },
      stdio: 'pipe',
    });
  });

  // **既定を空へ戻す退行を検出する（NFR / IADR-0262）。** `PLAN_ID_PREFIXES` を明示的に落とし、
  // それでも skip（exit 0・「PROJECT_PREFIXES が空のため skip した」）にならないことを固定する。
  // 落ちれば「素の実行が検査されない」という IADR-0189 決定6 の残余リスクが再発したことになる。
  ok('実ツリー: PLAN_ID_PREFIXES を明示的に落としても既定値で走査する（IADR-0262 の回帰）', () => {
    const { execFileSync } = require('child_process');
    const env = { ...process.env };
    delete env.PLAN_ID_PREFIXES;
    const out = execFileSync(process.execPath, [pathFb.join(__dirname, 'check-plan-id-qualification.js')], {
      cwd: pathFb.resolve(__dirname, '..'),
      env,
      stdio: 'pipe',
    }).toString();
    assert.ok(!/PROJECT_PREFIXES が空のため skip した/.test(out), `skip してはならない: ${out}`);
    assert.ok(/件に他プロジェクト ID の修飾違反はありません/.test(out), `実件数を報告すること: ${out}`);
  });

  ok('実ツリー: kit の計画 ID 修飾検査器の自己試験が通る（#477 の回帰）', () => {
    const { execFileSync } = require('child_process');
    execFileSync(process.execPath, [pathFb.join(__dirname, 'check-plan-id-qualification.js'), '--self-test'], {
      stdio: 'pipe',
    });
  });

  // --- クロスリポジトリ参照の表記（NFR / #487 / IADR-0200） ---
  //
  // 🔴 **姉妹検査器と同じ理由でここに置く。** `check-cross-repo-refs.js` は置換点
  // （`CROSS_REPOS` / `SELF_NAMES`）がキットのプレースホルダのままだと**正当な自リポ参照を大量に
  // 違反として上げる**——規約自身が「検査そのものを外させる」と警告している状態である。
  // よって**環境変数を自前で明示的に与えて**呼ぶ。CI 側の env を消しても、こちらが赤くなる。
  //
  // 除外の根拠は `.claude/rules/traceability.repo.md`（全数を理由つきで記載）。要点:
  //   - `.ai-context/specs/` は **point-in-time の記録**（後から表記だけ直すと当時の記述と食い違う）
  //   - **`CHANGELOG.md` は除外しない**（生成物は `changelog-overrides.json` の remap で是正する）
  //   - 🔴 **`.claude/rules/traceability.md` の除外は 2026-08-15 に外した**（#517 / IADR-0202）。
  //     キット側が planning#349 を反映して `planning#202` へ是正し、本リポも追随したため対象に戻せる。
  //     **同ファイルは分類 A（バイト一致）へ移したので、今後キットが違反を持ち込めばここが赤くなる。**
  //   - `planning`（submodule）・`feedback/` の除外は資料再編（ADR-0029）で両ディレクトリ自体が
  //     消滅したため削除した（2026-08-21）。
  const CROSS_REPO_ENV = {
    CROSS_REPO_NAMES: 'project-planning:planning,microservices-platform:MSP',
    CROSS_REPO_SELF_NAMES: 'AST,ai-stock-trading',
    CROSS_REPO_EXCLUDES: ':!.ai-context/specs',
  };

  ok('実ツリー: 他リポの issue / PR 番号が短縮形で書かれている（#487 の回帰）', () => {
    const { execFileSync } = require('child_process');
    execFileSync(process.execPath, [pathFb.join(__dirname, 'check-cross-repo-refs.js')], {
      cwd: pathFb.resolve(__dirname, '..'),
      env: { ...process.env, ...CROSS_REPO_ENV },
      stdio: 'pipe',
    });
  });

  // 🔴 **除外が効いていることを否定形で固定する。**
  // 除外を外せば `.ai-context/specs/` の point-in-time 記録が一斉に違反として上がる——
  // **上のテストが「たまたま 0 件だから緑」なのではなく、除外設定が効いた結果であること**を示す。
  ok('実ツリー: 除外を外すと point-in-time 記録が違反として上がる（除外が効いている証拠）', () => {
    const { execFileSync } = require('child_process');
    let failed = false;
    try {
      execFileSync(process.execPath, [pathFb.join(__dirname, 'check-cross-repo-refs.js')], {
        cwd: pathFb.resolve(__dirname, '..'),
        env: { ...process.env, ...CROSS_REPO_ENV, CROSS_REPO_EXCLUDES: ':!CHANGELOG.md' },
        stdio: 'pipe',
      });
    } catch {
      failed = true;
    }
    assert(failed, '除外を外しても違反 0 件だった。除外設定が実は何も除外していない可能性がある');
  });

  // --- クロスリポ参照を「実害の出る面」で検査する（NFR / #515 / IADR-0201） -------------------
  //
  // 🔴 **`.md` の面（#487）とは守っている対象が違う。**
  // コミットメッセージ・PR タイトルでは**裸の `#NNN` が本リポジトリの issue へ自動リンクする**ため、
  // 他リポの番号を裸で書くと**誤リンク**という実害が出る。規約はこの面を「優先して直す」と定めている。
  //
  // ここで検査するのは `check-commit-messages.js` が**実際に検査器を呼んでいること**である。
  // 呼んでいなければ、違反を含む件名が緑で通ってしまう（配線前の状態）。
  const ccm = require('./check-commit-messages.js');

  ok('クロスリポ参照: 違反する件名を検出する（#515 の回帰）', () => {
    assert(ccm.validateCrossRepoRefs('chore(NFR): project-planning#349 を取り込む').length > 0);
    assert(ccm.validateCrossRepoRefs('chore(NFR): microservices-platform#445 を待つ').length > 0);
    assert(ccm.validateCrossRepoRefs('chore(NFR): planning PR #329 に追随する').length > 0);
    assert(ccm.validateCrossRepoRefs('chore(NFR): planning#319 / #323 を反映する').length > 0);
  });

  // 🔴 **否定形（最重要）。** 正当な参照を止めると**検査そのものを外させる**——
  // 規約自身が「正当な自リポ参照を大量に違反として上げ、検査そのものを外させる」と警告している。
  ok('クロスリポ参照: 正当な参照は止めない（#515 の否定形）', () => {
    assert.strictEqual(ccm.validateCrossRepoRefs('chore(NFR): planning#349 を取り込む').length, 0);
    assert.strictEqual(ccm.validateCrossRepoRefs('chore(NFR): MSP#445 を待つ').length, 0);
    // 裸の #NNN は**本リポジトリ**を指す。これは正しい。
    assert.strictEqual(ccm.validateCrossRepoRefs('chore(NFR): 自リポの #487 を閉じる').length, 0);
    // スカッシュ既定件名の末尾。
    assert.strictEqual(ccm.validateCrossRepoRefs('chore(NFR,IADR-0200): 表記を確定する (#514)').length, 0);
    // 意図的な誤例はインラインコードへ入れる（literal な引用は表記規約の対象外）。
    assert.strictEqual(
      ccm.validateCrossRepoRefs('chore(NFR): 誤例は `project-planning#1` と書く').length, 0);
  });

  // 🔴 **本文の面**。規約が名指しした実害例は **footer の `Refs #NNN`** であり、
  // 件名だけ見ていては構造的に取りこぼす。
  ok('クロスリポ参照: 本文だけに違反があっても検出する（#515 の回帰）', () => {
    const body = '件名は適合している。\n\nRefs project-planning#349\n';
    assert(ccm.validateCrossRepoRefs(body).length > 0, '本文の違反を取りこぼしている');
  });

  // 🔴 **配線が実効していることの対照実験。**
  // `checkSingleTitle` は書式・ID 実在性・クロスリポ参照の 3 つを見る。
  // **書式も ID も正しく、クロスリポ参照だけが違反**の件名で 1 が返ることを確かめる——
  // ここが 0 なら「検査器を呼んでいない」（配線前の状態）である。
  ok('クロスリポ参照: PR タイトル経路が配線されている（#515 の対照実験）', () => {
    // `checkSingleTitle` は違反を stderr へ書く。**フィクスチャ由来の出力を CI ログへ漏らさない**
    // （scripts.test.js の同種テストと同じ作法。漏らすと本物の違反と見分けが付かなくなる）。
    const [so, se] = [process.stdout.write, process.stderr.write];
    process.stdout.write = () => true;
    process.stderr.write = () => true;
    let violating, clean;
    try {
      violating = ccm.checkSingleTitle('chore(NFR): project-planning#349 を取り込む', '');
      clean = ccm.checkSingleTitle('chore(NFR): planning#349 を取り込む', '');
    } finally {
      process.stdout.write = so;
      process.stderr.write = se;
    }
    assert.strictEqual(violating, 1, 'クロスリポ参照だけが違反の件名が通った＝検査器が呼ばれていない');
    assert.strictEqual(clean, 0, '正当な件名が止まった＝偽陽性');
  });

  ok('実ツリー: kit のクロスリポ参照検査器の自己試験が通る（#487 の回帰）', () => {
    const { execFileSync } = require('child_process');
    execFileSync(process.execPath, [pathFb.join(__dirname, 'check-cross-repo-refs.js'), '--self-test'], {
      stdio: 'pipe',
    });
  });

  // --- ADR 索引行の追随（NFR / #497） ---
  //
  // 🔴 **過去の事故そのものを回帰として固定する。**
  // 自己試験は合成した差分で判定規則を確かめるが、**実データで捕まえられることは示さない**。
  // #491 / #495 の実コミットを食わせて赤になることを確かめる（履歴が浅い環境では skip）。
  ok('過去の索引追随漏れ 2 件を検出できる（#491 / #495 の実コミットで回帰）', () => {
    const { execFileSync } = require('child_process');
    const repo = pathFb.resolve(__dirname, '..');
    const revOk = (rev) => {
      try {
        execFileSync('git', ['rev-parse', '--verify', '--quiet', `${rev}^{commit}`], { cwd: repo, stdio: 'pipe' });
        return true;
      } catch {
        return false;
      }
    };
    // 事故1: PR #491 の 270a7c1（IADR-0190 本文を更新・索引は据え置き）
    // 事故2: PR #495 の a39e3e7（IADR-0191 本文を訂正・索引は据え置き）
    const cases = [
      ['270a7c1^', '270a7c1'],
      ['4788905', 'a39e3e7'],
    ];
    let checked = 0;
    for (const [base, head] of cases) {
      if (!revOk(base) || !revOk(head)) continue; // 浅いクローンでは対象外
      checked += 1;
      let exitCode = 0;
      try {
        execFileSync(process.execPath, [pathFb.join(__dirname, 'check-adr-index-sync.js')], {
          cwd: repo,
          env: { ...process.env, COMMIT_RANGE: `${base}..${head}` },
          stdio: 'pipe',
        });
      } catch (e) {
        exitCode = e.status;
      }
      assert(exitCode === 1, `${base}..${head} は索引追随漏れとして赤になるべきだが exit=${exitCode} だった`);
    }
    // 是正コミットは緑であること（偽陽性を出していないことの確認）
    if (revOk('270a7c1') && revOk('04126d8')) {
      execFileSync(process.execPath, [pathFb.join(__dirname, 'check-adr-index-sync.js')], {
        cwd: repo,
        env: { ...process.env, COMMIT_RANGE: '270a7c1..04126d8' },
        stdio: 'pipe',
      });
    }
    if (checked === 0) {
      // 履歴が無い環境では検査していないことを明示する（黙って緑にしない）
      process.stdout.write('       （履歴が浅いため過去コミットでの回帰は skip した）\n');
    }
  });

  // ［2026-08-21 追記 / IADR-0208 決定 6］旧 adr-index-sync ジョブは static-checks へ統合された。
  // 本試験の主眼は「fetch-depth: 0 が在るか」（無いと差分の範囲を解決できず**検査は永久に skip して
  // 緑になる**）であって「専用ジョブが在るか」ではないため、ジョブ名だけ追随させる。
  ok('ci.yml の static-checks ジョブが fetch-depth: 0 を指定している（#497 の回帰）', () => {
    const fsA = require('fs');
    const ci = fsA.readFileSync(pathFb.resolve(__dirname, '../.github/workflows/ci.yml'), 'utf8');
    const start = ci.indexOf('\n  static-checks:');
    assert(start !== -1, 'static-checks ジョブが ci.yml に無い');
    const rest = ci.slice(start + 1);
    const nextJob = rest.search(/\n {2}[a-z][a-z0-9-]*:\n/);
    const bodyRaw = nextJob === -1 ? rest : rest.slice(0, nextJob);
    const body = bodyRaw
      .split('\n')
      .filter((l) => !l.trim().startsWith('#'))
      .join('\n');
    // fetch-depth が無いと差分の範囲を解決できず、検査は永久に skip して緑になる。
    assert(body.includes('fetch-depth: 0'), 'static-checks が fetch-depth: 0 を指定していない（範囲を解決できず skip する）');
    const runLines = body.split('\n').filter((l) => l.includes('run:'));
    assert(
      runLines.some((l) => l.includes('check-adr-index-sync.js --self-test')),
      'static-checks が check-adr-index-sync の自己試験を走らせていない',
    );
  });

  // 🔴 自己試験は合成した入力で判定規則を確かめるだけであり、**その副産物を CI の注意喚起として
  // 出してはならない**。lib/ci-annotate.js の notice()/warn() は console ではなく
  // **process.stdout.write へ直接書く**ため、console だけ差し替えた初版は本物の ::notice:: を
  // 漏らしていた（実測 2 件。AI レビューが検出）。無関係な IADR 番号に言及する notice が
  // 毎 PR で出続けると、**notice が読まれなくなる** —— IADR-0193 決定2 は「宣言すると notice が
  // 出るから黙って素通りにはならない」という前提の上に立っているため、これは統制の土台を崩す。
  ok('自己試験は CI アノテーションを漏らさない（GITHUB_ACTIONS=true で実行しても notice/warning ゼロ）', () => {
    const { execFileSync } = require('child_process');
    const out = execFileSync(process.execPath, [pathFb.join(__dirname, 'check-adr-index-sync.js'), '--self-test'], {
      cwd: pathFb.resolve(__dirname, '..'),
      env: { ...process.env, GITHUB_ACTIONS: 'true' },
      encoding: 'utf8',
      stdio: 'pipe',
    });
    const leaked = out.split('\n').filter((l) => l.startsWith('::notice::') || l.startsWith('::warning::'));
    assert(
      leaked.length === 0,
      `自己試験が CI アノテーションを ${leaked.length} 件漏らした:\n${leaked.join('\n')}`,
    );
  });

  // --- 必読規約の総量予算（NFR / #519 / IADR-0204） -------------------------------------------
  //
  // 🔴 **合算しないことが設計の核心である。** 「毎セッション必読」は読む主体によって中身が違い、
  // **1 つのセッションが Claude 用と AGENTS.md 用の両方を背負うことはない。**
  // 合算は**誰も背負わない量**を作る —— 実測で 2 回とも誤った（#519。`AGENTS.md` を足して
  // 90.2% / 91.5% と報告し、**着手条件 90% を満たしたことにしていた**。正しくは 82.7%）。
  const budget = require('./check-reading-budget.js');

  ok('必読規約の予算: 自己試験が通る（#519）', () => {
    const { execFileSync } = require('child_process');
    execFileSync(process.execPath, [pathFb.join(__dirname, 'check-reading-budget.js'), '--self-test'], {
      cwd: pathFb.resolve(__dirname, '..'),
      stdio: 'pipe',
    });
  });

  ok('必読規約の予算: 実ツリーが予算内である（#519 の回帰）', () => {
    const { execFileSync } = require('child_process');
    execFileSync(process.execPath, [pathFb.join(__dirname, 'check-reading-budget.js')], {
      cwd: pathFb.resolve(__dirname, '..'),
      stdio: 'pipe',
    });
  });

  // 🔴 **否定形。** 予算を実測より小さくすれば赤くなること＝**検査が実効している**ことを示す。
  // これが無いと「たまたま余裕があるから緑」と区別が付かない
  // （IADR-0200 の対照実験 2 / 3 が「緑のまま」で終わった形と同じ）。
  ok('必読規約の予算: 予算を縮めると赤くなる（実効している証拠。#519）', () => {
    const { execFileSync } = require('child_process');
    let failed = false;
    try {
      execFileSync(process.execPath, [pathFb.join(__dirname, 'check-reading-budget.js')], {
        cwd: pathFb.resolve(__dirname, '..'),
        env: { ...process.env, READING_BUDGET_BYTES: '1000' },
        stdio: 'pipe',
      });
    } catch {
      failed = true;
    }
    assert(failed, '予算を 1000 バイトへ縮めても緑だった。検査が母集合を 1 件も見ていない疑いがある');
  });

  // 🔴 **`AGENTS.md` が Claude Code の集合へ紛れ込むと、この 2 回の誤りが再発する。**
  ok('必読規約の予算: AGENTS.md は Claude Code の集合に入らない（#519 の回帰）', () => {
    const claude = budget.AGENT_SETS.find((s) => s.name === 'Claude Code');
    assert(claude, 'Claude Code の集合が無い');
    const { found } = budget.resolveSet(claude);
    assert(
      !found.includes('AGENTS.md'),
      'AGENTS.md が Claude Code の集合に入っている。同ファイルは「Claude 以外の AI エージェント」が読む',
    );
    assert(found.includes('CLAUDE.md'), 'CLAUDE.md が集合に無い');
    assert(
      found.some((f) => f.startsWith('.claude/rules/')),
      '.claude/rules/ の自動適用ルールが集合に無い',
    );
  });


  // --- 必読規約の予算の CI 配線（NFR / #524。IADR-0204 の残余リスク） -----------------------------
  //
  // 検査器はあるが CI に居ない、は「手で叩いたときだけ走る検査器」であり予算を守らせない。
  // 配線を機械で固定する（`run:` 行の存在しか見ないことは承知のうえで、挙動は上の実ツリー試験が見る）。
  // ［2026-08-21 追記 / IADR-0208 決定 6］旧 reading-budget ジョブは static-checks へ統合された。
  // 見るのは「自己試験と本検査の両方が配線されているか」なのでジョブ名だけ追随させる。
  //
  // ★ ジョブ本文の切り出しに `\n\n  ` を使わないこと。統合後のジョブは移設した由来コメントの前に
  //   空行を挟むため、最初の空行で切れて本検査を見落とす。次の**ジョブキー**までを取る。
  ok('ci.yml の static-checks が check-reading-budget.js を自己試験＋本検査で走らせる（#524）', () => {
    const fsK = require('fs');
    const ci = fsK.readFileSync(pathFb.resolve(__dirname, '../.github/workflows/ci.yml'), 'utf8');
    const start = ci.indexOf('\n  static-checks:');
    assert(start >= 0, 'static-checks ジョブが無い');
    const rest = ci.slice(start + 1);
    const nextJob = rest.search(/\n {2}[a-z][a-z0-9-]*:\n/);
    const body = nextJob === -1 ? rest : rest.slice(0, nextJob);
    assert(body.includes('check-reading-budget.js --self-test'), '自己試験が配線されていない');
    const runs = body.split('\n').filter((l) => l.includes('run:') && l.includes('check-reading-budget.js'));
    assert(runs.some((l) => !l.includes('--self-test')), '本検査が配線されていない（自己試験だけでは母集合を測らない）');
  });

  // 🔴 予算値の複製には出典が要る（運用ガイド §8「出典の無い複製は認めない」。planning#364）。
  // 値だけ直して出典を落とす退行を止める。
  ok('check-reading-budget.js の既定予算は 51,200 で、値の隣に正本（運用ガイド §8）の出典がある（#524）', () => {
    const fsK = require('fs');
    const src = fsK.readFileSync(pathFb.join(__dirname, 'check-reading-budget.js'), 'utf8');
    const lines = src.split('\n');
    const i = lines.findIndex((l) => /^const BUDGET_BYTES = /.test(l));
    assert(i >= 0, 'BUDGET_BYTES の定義が無い');
    assert(/\|\| 51200\)/.test(lines[i]), `既定値が 51200 でない: ${lines[i]}`);
    const near = lines.slice(Math.max(0, i - 8), i).join('\n');
    assert(/ai-implementation-workflow-guide\.md/.test(near) && /§8/.test(near), '値の隣（直前 8 行）に正本の出典が無い');
    assert(budget.BUDGET_BYTES === 51200 || process.env.READING_BUDGET_BYTES, 'エクスポートされた既定値が 51200 でない');
  });

  // --- #532: readPlanIds 拡張点（キット check-commit-messages.js が探す面） -------------
  //
  // この拡張点が無い間、`feat(SC-99)` は exit 0 で受理され恒久履歴へ載った（force push 禁止で
  // 事後修正できない面）。planning submodule を走査する planIds(root) は使えない——
  // commit-messages ジョブは submodule を取得せず、走査すると実在集合が空になり全 ID が違反になる。
  {
    const fsRp = require('fs');
    const osRp = require('os');
    const pathRp = require('path');
    const ttRp = require('./check-test-traceability.js');
    const cmRp = require('./check-commit-messages.js');

    ok('#532: readPlanIds が規約節のレンジを実在集合へ展開する（FR-01..21 / UC-01..07 / SC-01..03）', () => {
      const ids = ttRp.readPlanIds();
      assert.strictEqual(ids.length, 31, `実在集合の件数が違う: ${ids.length}`);
      for (const id of ['FR-01', 'FR-21', 'UC-01', 'UC-07', 'SC-01', 'SC-03']) {
        assert.ok(ids.includes(id), `${id} が実在集合に無い`);
      }
      // SC-13 / SC-16 は基盤（MSP）の画面への参照であり本リポの名前空間ではない。
      for (const id of ['SC-04', 'SC-13', 'SC-16', 'FR-22', 'UC-08']) {
        assert.ok(!ids.includes(id), `${id} が実在集合に混ざっている`);
      }
    });

    // ★ 実バイナリでの検出力。純関数が正しくても、キット側の loadExistingPlanIds が
    //   拡張点を見つけられなければ検査は skip のままになる（notice で緑のまま素通り）。
    ok('#532: 実在しない起点 ID の件名を違反として検出する（拡張点がキットから見えている）', () => {
      const ids = new Set(ttRp.readPlanIds());
      assert.strictEqual(cmRp.validateIdExistence('feat(SC-99): x', null, null, ids).length, 1);
      assert.strictEqual(cmRp.validateIdExistence('feat(FR-99): x', null, null, ids).length, 1);
      assert.strictEqual(cmRp.validateIdExistence('feat(SC-03): x', null, null, ids).length, 0);
      assert.strictEqual(cmRp.validateIdExistence('feat(FR-21): x', null, null, ids).length, 0);
      // キット側の入口（拡張点の解決）も通ること。null が返ると検査は skip へ落ちる。
      const loaded = cmRp.loadExistingPlanIds();
      assert.ok(loaded && loaded.size === 31, `loadExistingPlanIds が拡張点を解決していない: ${loaded && loaded.size}`);
    });

    // ★ fail-loud。規約側の破壊（節の改名・書式変更）を黙って skip すると
    //   「違反 0 件」という最も安全に見える出力で素通りする。
    ok('#532: 規約節を読めない／壊れているときは例外（黙って 0 件検査へ落ちない）', () => {
      assert.throws(() => ttRp.readPlanIds(pathRp.join(osRp.tmpdir(), 'no-such-rules-532.md')));
      const f = pathRp.join(fsRp.mkdtempSync(pathRp.join(osRp.tmpdir(), 'rp532-')), 'rules.md');
      fsRp.writeFileSync(f, '# 節が無い規約\n\n本文だけ\n');
      assert.throws(() => ttRp.readPlanIds(f), /節が見つかりません/);
      // 種別の欠落・範囲の不正も例外。
      assert.throws(() => ttRp.expandPlanIds({ FR: { from: 1, to: 3 }, UC: { from: 1, to: 2 } }));
      assert.throws(() => ttRp.expandPlanIds({ FR: { from: 5, to: 1 }, UC: { from: 1, to: 2 }, SC: { from: 1, to: 2 } }));
    });

    ok('#532: 規約ファイルが機械の単一情報源であることを明記している（節を消させない）', () => {
      const md = fsRp.readFileSync(pathRp.join(__dirname, '..', ttRp.RULES_FILE), 'utf8');
      const section = ttRp.planRangeSection(md);
      assert.ok(section !== null, `${ttRp.RULES_FILE} に「${ttRp.PLAN_RANGE_HEADING}」節が無い`);
      assert.match(section, /readPlanIds/, '節が機械の単一情報源であることを書いていない');
      assert.match(section, /SC-13/, 'SC-13 / SC-16 を実在集合へ入れない理由が書かれていない');
    });
  }

  // --- check-trace-blocks.js / gen-knowledge-graph.js -------------------------------
  //
  // project-planning ADR-0029 決定4「trace ブロック規約」の検査の実現手段として新設した
  // 2 本（NFR）。文法・分類ロジックの単一情報源は `scripts/lib/trace-blocks.js`
  // （両スクリプトが requires する）、計画 ADR レンジの読み手は `scripts/lib/plan-ranges.js`
  // （`check-test-traceability.js` の `readPlanIds()`/`planRangeSection()` を拡張点として再利用し、
  // `check-test-traceability.js` 自体は変更しない）。
  //
  // 他プロジェクト／他リポジトリの修飾子（`MSP:` 等）は個別名をハードコードしない
  // （利用者裁定・ADR-0029 決定9）。「英字+英数字の短縮名 + `:`」という形だけで external とみなす
  // 汎用規則になっていることを、未知の短縮名で確かめる。
  //
  // 本走（docs/ 実データ）の合否はここでは問わない —— docs/ は並行編集中で残違反が既知（作業報告に
  // 件数と一覧を記録する）。ここで固定するのは「クラッシュしない」「自己試験が通る」
  // 「CI に配線されている」という自己試験主体の契約である。
  {
    const fsTb = require('fs');
    const pathTb = require('path');
    const { execFileSync: execTb } = require('child_process');
    const REPO_ROOT_TB = pathTb.resolve(__dirname, '..');

    ok('check-trace-blocks: 自己試験が全件通る', () => {
      execTb(process.execPath, [pathTb.join(__dirname, 'check-trace-blocks.js'), '--self-test'], {
        cwd: REPO_ROOT_TB,
        stdio: 'pipe',
      });
    });

    ok('gen-knowledge-graph: 自己試験が全件通る', () => {
      execTb(process.execPath, [pathTb.join(__dirname, 'gen-knowledge-graph.js'), '--self-test'], {
        cwd: REPO_ROOT_TB,
        stdio: 'pipe',
      });
    });

    ok('lib/trace-blocks.js: 修飾子は個別リポジトリ名を問わない汎用規則である（ADR-0029 決定9）', () => {
      const tbLib = require('./lib/trace-blocks.js');
      assert.strictEqual(tbLib.classifyIdToken('MSP:FR-01').external, true);
      assert.strictEqual(tbLib.classifyIdToken('FR-01').external, false);
      // ハードコードした許可リストではないことの証拠: コードベースに存在しない未知の短縮名でも
      // 「英字+英数字 + `:`」の形さえ満たせば external になる（将来リポジトリが増えても変更不要）。
      assert.strictEqual(tbLib.classifyIdToken('SOMEFUTUREREPO:IADR-0001').external, true);
      assert.strictEqual(tbLib.classifyIssueToken('SOMEFUTUREREPO#1').external, true);
    });

    ok('lib/plan-ranges.js: 実ツリーの計画 ADR レンジを読める（check-trace-blocks / gen-knowledge-graph 共有の拡張点）', () => {
      const planRangesRp = require('./lib/plan-ranges.js');
      const r = planRangesRp.readPlanAdrRange();
      assert.ok(Number.isInteger(r.from) && Number.isInteger(r.to) && r.to >= r.from, `不正なレンジ: ${JSON.stringify(r)}`);
      assert.strictEqual(planRangesRp.isAdrInRange(`ADR-${String(r.to).padStart(4, '0')}`, r), true);
      assert.strictEqual(planRangesRp.isAdrInRange(`ADR-${String(r.to + 1).padStart(4, '0')}`, r), false);
    });

    ok('check-trace-blocks: 実データに対してクラッシュせず走る（合否は自己試験主体。docs/ は並行編集中）', () => {
      try {
        execTb(process.execPath, [pathTb.join(__dirname, 'check-trace-blocks.js')], { cwd: REPO_ROOT_TB, stdio: 'pipe' });
      } catch (e) {
        assert.strictEqual(e.status, 1, `異常終了（クラッシュ）の可能性がある: status=${e.status}\n${e.stderr}`);
      }
    });

    ok('gen-knowledge-graph: --json / --mermaid / --check の 3 面がいずれもクラッシュせず走る', () => {
      for (const args of [['--json'], ['--mermaid'], ['--mermaid', '--scope', 'docs/functional']]) {
        const out = execTb(process.execPath, [pathTb.join(__dirname, 'gen-knowledge-graph.js'), ...args], {
          cwd: REPO_ROOT_TB,
          stdio: 'pipe',
          encoding: 'utf8',
        });
        assert.ok(out.length > 0, `${args.join(' ')} の出力が空`);
      }
      try {
        execTb(process.execPath, [pathTb.join(__dirname, 'gen-knowledge-graph.js'), '--check'], {
          cwd: REPO_ROOT_TB,
          stdio: 'pipe',
        });
      } catch (e) {
        assert.strictEqual(e.status, 1, `--check が異常終了（クラッシュ）した可能性がある: status=${e.status}\n${e.stderr}`);
      }
    });

    ok('ci.yml: check-trace-blocks / gen-knowledge-graph の自己試験と本検査の両方が配線されている', () => {
      const ciYml = fsTb.readFileSync(pathTb.join(REPO_ROOT_TB, '.github', 'workflows', 'ci.yml'), 'utf8');
      assert.match(ciYml, /check-trace-blocks\.js --self-test/, 'check-trace-blocks の自己試験が ci.yml に無い');
      const ctbCount = (ciYml.match(/check-trace-blocks\.js/g) || []).length;
      assert.ok(ctbCount >= 2, `check-trace-blocks.js の呼び出しが ${ctbCount} 件（自己試験＋本検査で最低 2 件必要）`);
      assert.match(ciYml, /gen-knowledge-graph\.js --self-test/, 'gen-knowledge-graph の自己試験が ci.yml に無い');
      assert.match(ciYml, /gen-knowledge-graph\.js --check/, 'gen-knowledge-graph --check が ci.yml に無い');
    });

    ok('scripts/README.md: 本リポジトリ固有の表に check-trace-blocks.js / gen-knowledge-graph.js を記載している', () => {
      const readme = fsTb.readFileSync(pathTb.join(REPO_ROOT_TB, 'scripts', 'README.md'), 'utf8');
      assert.match(readme, /check-trace-blocks\.js/, 'scripts/README.md に check-trace-blocks.js の記載が無い');
      assert.match(readme, /gen-knowledge-graph\.js/, 'scripts/README.md に gen-knowledge-graph.js の記載が無い');
    });
  }
  // --- check-observability-assets: 業務メトリクスとダッシュボードの乖離検査（NFR-07 / #287 / IADR-0255） ---
  //
  // 🔴 ダッシュボードとコードの乖離は**エラーを出さない**。系列名がずれたパネルは空のグラフを描き、
  // 空のグラフは「異常が起きていない」と読める。**監視しているつもりで何も見ていない**状態が
  // 気付かれずに続くため、名前の一致を機械で止める。
  {
    const fsOa = require('fs');
    const pathOa = require('path');
    const { execFileSync: execOa } = require('child_process');
    const REPO_ROOT_OA = pathOa.resolve(__dirname, '..');
    const oa = require('./check-observability-assets.js');

    ok('check-observability-assets: 自己試験が全件通る', () => {
      execOa(process.execPath, [pathOa.join(__dirname, 'check-observability-assets.js'), '--self-test'], {
        cwd: REPO_ROOT_OA,
        stdio: 'pipe',
      });
    });

    ok('check-observability-assets: 実データ（ダッシュボード × 計器レジストリ）が一致している', () => {
      execOa(process.execPath, [pathOa.join(__dirname, 'check-observability-assets.js')], {
        cwd: REPO_ROOT_OA,
        stdio: 'pipe',
      });
    });

    ok('check-observability-assets: コード名 → Prometheus 名の変換は単純な置換 1 本である', () => {
      assert.strictEqual(oa.promPrefixOf('ast.trade_cycle.decisions'), 'ast_trade_cycle_decisions');
    });

    ok('check-observability-assets: 実レジストリから 9 計器を読める（読み取りが壊れたら気付く）', () => {
      const src = fsOa.readFileSync(
        pathOa.join(REPO_ROOT_OA, 'backend', 'Shared', 'AiStockTrading.Shared.Contracts',
          'Observability', 'BusinessMetricNames.cs'),
        'utf8');
      const names = oa.parseRegistry(src);
      assert.ok(names.length >= 9, `読めた計器が ${names.length} 件（9 件以上を期待）`);
      assert.ok(names.every((n) => n.startsWith('ast.')), `ast. 以外が混ざった: ${names}`);
    });

    ok('check-observability-assets: 業務ダッシュボードが 9 計器すべてを引いている', () => {
      const dash = JSON.parse(fsOa.readFileSync(
        pathOa.join(REPO_ROOT_OA, 'deploy', 'observability', 'dashboards', 'ai-stock-trading-business.json'),
        'utf8'));
      const series = oa.referencedSeries(dash);
      assert.ok(series.length >= 9, `引いている ast_* 系列が ${series.length} 件（9 件以上を期待）`);
    });

    ok('ci.yml: check-observability-assets の自己試験と本検査の両方が配線されている', () => {
      const ciYml = fsOa.readFileSync(pathOa.join(REPO_ROOT_OA, '.github', 'workflows', 'ci.yml'), 'utf8');
      assert.match(ciYml, /check-observability-assets\.js --self-test/, '自己試験が ci.yml に無い');
      const count = (ciYml.match(/check-observability-assets\.js/g) || []).length;
      assert.ok(count >= 2, `呼び出しが ${count} 件（自己試験＋本検査で最低 2 件必要）`);
    });

    ok('scripts/README.md: 本リポジトリ固有の表に check-observability-assets.js を記載している', () => {
      const readme = fsOa.readFileSync(pathOa.join(REPO_ROOT_OA, 'scripts', 'README.md'), 'utf8');
      assert.match(readme, /check-observability-assets\.js/, 'scripts/README.md に記載が無い');
    });
  }
};
