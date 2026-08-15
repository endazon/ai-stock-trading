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

  // --- check-doc-links: 同一ディレクトリ内の裸ファイル名リンク（NFR / #399 / IADR-0147） ---
  //
  // 判定が `./` `../` 始まりか `/` を含む形しか見ておらず、docs/adr/ の IADR 相互参照が使う
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

  // ---- 否定形の確認（誤検出しないこと）: N1〜N11。正の確認 5 件に対し 11 件 ----

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

  ok('check-doc-links[N11]: 未 populate な submodule 配下は従来どおり skip される', () => {
    // 裸ファイル名は必ず「その Markdown 自身のディレクトリ」に解決するため、裸ファイル名が
    // 未 populate submodule 配下へ落ちることは構造上あり得ない（その Markdown 自身が submodule
    // 内に必要になるが、未 populate＝空ディレクトリなので存在し得ない）。よってここで固定するのは
    // 「判定拡張が既存の skip 分岐を壊していないこと」であり、`/` 形と裸ファイル名の破損を
    // 同一フィクスチャに同居させて確認する。
    const root = fsDl.mkdtempSync(pathDl.join(osDl.tmpdir(), 'dl-sub-'));
    fsDl.writeFileSync(pathDl.join(root, '.gitmodules'), '[submodule "planning"]\n\tpath = planning\n\turl = x\n');
    fsDl.mkdirSync(pathDl.join(root, 'planning'), { recursive: true }); // 空＝未 populate
    const docs = pathDl.join(root, 'docs');
    fsDl.mkdirSync(docs, { recursive: true });
    fsDl.writeFileSync(
      pathDl.join(docs, 'a.md'),
      '# A\n\n- [plan](../planning/projects/x/07_adr/ADR-0001_a.md)\n' +
        '- [bare](IADR-9999_no-such.md)\n'
    );
    let out = '';
    try {
      execSync(`node ${JSON.stringify(pathDl.join(__dirname, 'check-doc-links.js'))} --dir ${JSON.stringify(docs)}`, {
        env: { ...process.env, DOC_LINKS_ROOT: root },
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'pipe'],
      });
      assert.fail('裸ファイル名の破損で exit 1 になっていない');
    } catch (e) {
      if (e && e.code === 'ERR_ASSERTION') throw e;
      out = String(e.stdout || '') + String(e.stderr || '');
    }
    assert.match(out, /IADR-9999_no-such\.md/, '裸ファイル名の破損は検出されること');
    assert.doesNotMatch(out, /ADR-0001_a\.md/, '未 populate submodule 配下は破損として挙げないこと');
    assert.match(out, /未 populate の submodule 配下 1 件/, '除外は黙って行わず件数を報告すること');
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

  // --- 環流記録の `status` 語彙（NFR / #477 / IADR-0188） ---
  //
  // **伝達の判定そのものは kit の `check-feedback-dispatched.js` が自己試験で守る**ため、
  // ここで二重に書かない（書くと kit 同期のたびに二重の追随が要る。IADR-0047 決定1）。
  //
  // **本リポが独自に守るのは `status` の語彙だけである。** kit の `feedback/README.md` は
  // 「**この語彙を検査する機械は無い。値の誤りは沈黙する**」と明記しており、実際に
  // **本リポは語彙外の `resolved` を 8 件持っていた**（planning#323 の裁定 2026-08-14 が名指しで是正を指示）。
  // 同じ kit を配った 2 リポジトリで語彙が割れた**同型 2 回目**であるため、番人を置く
  // （planning#296「検査器・規約の追加は同型の事故が 2 回から」）。
  //
  // **検査器ではなくリポジトリ固有の回帰テストとして置く。** kit 由来ファイルを改変せずに済み、
  // 語彙が kit 側で変わったときは本テストだけを追随させればよい。
  const pathFb = require('path');
  const fsFb = require('fs');

  /** kit `feedback/README.md` §`status` の語彙（4 値）。**増やすときは kit 側の節を先に直すこと。** */
  const FEEDBACK_STATUSES = ['open', 'awaiting-decision', 'accepted', 'rejected'];

  const feedbackRecords = () => {
    const dir = pathFb.resolve(__dirname, '..', 'feedback');
    return fsFb
      .readdirSync(dir)
      .filter((f) => f.endsWith('.md') && !['README.md', 'TEMPLATE.md'].includes(f))
      .map((f) => ({ file: f, text: fsFb.readFileSync(pathFb.join(dir, f), 'utf8') }));
  };

  const statusOf = (text) => {
    const m = text.match(/^---\r?\n([\s\S]*?)\r?\n---/);
    if (!m) return null;
    const s = m[1].match(/^status:[ \t]*(\S+)[ \t]*$/m);
    return s ? s[1] : null;
  };

  ok('実ツリー: 環流記録を 1 件以上読めている（0 件なら以降の主張は空振りである）', () => {
    assert.ok(feedbackRecords().length > 0);
  });

  // **語彙外の値の再混入を止める。** `resolved` はここで落ちる。
  ok('実ツリー: 環流記録の status が kit の語彙（4 値）の内にある（#477 の回帰）', () => {
    const bad = feedbackRecords()
      .map(({ file, text }) => ({ file, status: statusOf(text) }))
      .filter(({ status }) => !FEEDBACK_STATUSES.includes(status));
    assert.deepStrictEqual(bad, [], `語彙は ${FEEDBACK_STATUSES.join(' / ')} である`);
  });

  // **`status` に伝達の軸を混ぜない**（planning#323 の裁定）。伝達は下の 2 鍵が担う。
  ok('実ツリー: 環流記録が dispatched: を true / false のいずれかで持つ（#477 の回帰）', () => {
    const bad = feedbackRecords()
      .filter(({ text }) => !/^dispatched:[ \t]*(true|false)[ \t]*$/m.test(text))
      .map(({ file }) => file);
    assert.deepStrictEqual(bad, [], 'YAML 1.1 の no / off も偽になるため true / false に限る');
  });

  ok('実ツリー: 伝達済みの記録は planning_issue: を伴う（#477 の回帰）', () => {
    const bad = feedbackRecords()
      .filter(({ text }) => /^dispatched:[ \t]*true[ \t]*$/m.test(text))
      .filter(({ text }) => !/^planning_issue:[ \t]*\S+[ \t]*$/m.test(text))
      .map(({ file }) => file);
    assert.deepStrictEqual(bad, [], '伝達したなら到達先の番号が残っていること');
  });

  // kit 検査器の判定規則そのものが壊れていれば、実データに対する結果は意味を持たない。
  ok('実ツリー: kit の伝達検査器の自己試験が通る（#477 の回帰）', () => {
    const { execFileSync } = require('child_process');
    execFileSync(process.execPath, [pathFb.join(__dirname, 'check-feedback-dispatched.js'), '--self-test'], {
      stdio: 'pipe',
    });
  });

  // --- 計画 ID の修飾（NFR / #477 / IADR-0189） ---
  //
  // **CI ジョブだけでは足りない。** `plan-id-qualification` ジョブは `PLAN_ID_PREFIXES` を渡して
  // 走らせるが、**その環境変数を落とすと `PROJECT_PREFIXES` が空になり、検査は skip して緑になる**
  // （fail-open。「他プロジェクトを参照しないリポジトリ」のための正常な挙動である）。
  //
  // よって本テストは**環境変数を自前で明示的に与えて**呼ぶ。CI 側の env を消しても、こちらが赤くなる。
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

  ok('実ツリー: kit の計画 ID 修飾検査器の自己試験が通る（#477 の回帰）', () => {
    const { execFileSync } = require('child_process');
    execFileSync(process.execPath, [pathFb.join(__dirname, 'check-plan-id-qualification.js'), '--self-test'], {
      stdio: 'pipe',
    });
  });

  // --- キット追随（NFR / #492） ---
  //
  // 🔴 **ここで守るのは検査結果ではなく「検査が実効していること」である。**
  // `check-kit-sync.js` は planning が未 populate なら skip して緑になる（fail-open）。
  // CI の `kit-sync` ジョブは submodule を取得したうえで `--require-planning` を渡すことで
  // その fail-open を閉じているが、**どちらか一方でも落ちると検査は永久に skip し、
  // 「配線したのに一度も検査していない緑」が固定される**。CI 設定側を機械で固定する。
  ok('ci.yml の kit-sync ジョブが submodule を取得し --require-planning を渡している（#492 の回帰）', () => {
    const fsK = require('fs');
    const ci = fsK.readFileSync(pathFb.resolve(__dirname, '../.github/workflows/ci.yml'), 'utf8');
    const job = ci.slice(ci.indexOf('\n  kit-sync:'));
    const body = job.slice(0, job.indexOf('\n\n  ') === -1 ? job.length : job.indexOf('\n\n  '));
    assert(body.includes('submodules: recursive'), 'kit-sync ジョブが submodule を取得していない（取得しないと検査は skip する）');
    assert(body.includes('PLANNING_REPO_TOKEN'), 'kit-sync ジョブが planning 取得用トークンを渡していない');
    assert(
      body.includes('--require-planning'),
      '--require-planning が無い。未 populate 時に skip して緑になり、検査が実効しない',
    );
  });

  // --- 計画書実在検査が PR 段階で実効していること（NFR / #496） ---
  //
  // 🔴 **ここも守るのは検査結果ではなく「検査が実効していること」である。**
  // doc-links / test-traceability は planning が未 populate なら該当検査を skip して緑になる。
  // submodule の取得と `--require-planning` の両方が揃って初めて実効するため、CI 設定側を固定する。
  //
  // **この 2 ジョブは長らく「PR CI にはトークンが無い」という誤った前提で skip していた**（#496）。
  // 前提が誤りだったと分かった以上、**戻さないことを機械で担保する。**
  for (const job of ['doc-links', 'test-traceability']) {
    ok(`ci.yml の ${job} ジョブが submodule を取得し --require-planning を渡している（#496 の回帰）`, () => {
      const fsP = require('fs');
      const ci = fsP.readFileSync(pathFb.resolve(__dirname, '../.github/workflows/ci.yml'), 'utf8');
      const start = ci.indexOf(`\n  ${job}:`);
      assert(start !== -1, `${job} ジョブが ci.yml に無い`);
      const rest = ci.slice(start + 1);
      const nextJob = rest.search(/\n {2}[a-z][a-z0-9-]*:\n/);
      const bodyRaw = nextJob === -1 ? rest : rest.slice(0, nextJob);
      // 🔴 **コメント行を落としてから照合する。** 落とさないと、この設定を説明する
      // コメント自身（「--require-planning を必ず付ける」等）に一致して**素通りする**。
      // 初版が実際にそうなっており、変異試験（run 行からフラグを外す）で緑のままだった。
      // **語ではなく実行行を見る**——planning#319 知見3 と同型の罠である。
      const body = bodyRaw
        .split('\n')
        .filter((l) => !l.trim().startsWith('#'))
        .join('\n');
      const runLines = body.split('\n').filter((l) => l.includes('run:'));
      assert(body.includes('submodules: recursive'), `${job} が submodule を取得していない（planning 配下は検査されない）`);
      assert(body.includes('PLANNING_REPO_TOKEN'), `${job} が planning 取得用トークンを渡していない`);
      assert(
        runLines.some((l) => l.includes('--require-planning')),
        `${job} の run 行に --require-planning が無い。取得失敗時に skip して緑になり、検査が実効しない`,
      );
    });
  }

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

  ok('ci.yml の adr-index-sync ジョブが fetch-depth: 0 を指定している（#497 の回帰）', () => {
    const fsA = require('fs');
    const ci = fsA.readFileSync(pathFb.resolve(__dirname, '../.github/workflows/ci.yml'), 'utf8');
    const start = ci.indexOf('\n  adr-index-sync:');
    assert(start !== -1, 'adr-index-sync ジョブが ci.yml に無い');
    const rest = ci.slice(start + 1);
    const nextJob = rest.search(/\n {2}[a-z][a-z0-9-]*:\n/);
    const bodyRaw = nextJob === -1 ? rest : rest.slice(0, nextJob);
    const body = bodyRaw
      .split('\n')
      .filter((l) => !l.trim().startsWith('#'))
      .join('\n');
    // fetch-depth が無いと差分の範囲を解決できず、検査は永久に skip して緑になる。
    assert(body.includes('fetch-depth: 0'), 'adr-index-sync が fetch-depth: 0 を指定していない（範囲を解決できず skip する）');
    const runLines = body.split('\n').filter((l) => l.includes('run:'));
    assert(
      runLines.some((l) => l.includes('check-adr-index-sync.js --self-test')),
      'adr-index-sync が自己試験を走らせていない',
    );
  });

  ok('分類表の B は全件が理由を持ち、X 分類は追跡先の issue 番号を持つ（#492）', () => {
    const table = require('./kit-sync-classification.json');
    const entries = Object.entries(table.classes.B);
    assert(entries.length > 0, '分類 B が空である（表が壊れている疑い）');
    for (const [file, reason] of entries) {
      assert(typeof reason === 'string' && reason.trim() !== '', `${file} の分類理由が空である`);
      // 先頭が 4 種の番号（1〜4）か X であること
      assert(/^[1-4X][.．]/.test(reason.trim()), `${file} の分類理由が 4 種の番号でも X でもない: ${reason}`);
      // X（4 種に当たらない）は暫定であり、追跡先が無いと放置される
      if (reason.trim().startsWith('X')) {
        assert(/#\d+/.test(reason), `${file} は X 分類なのに追跡先の issue 番号が無い`);
      }
    }
  });

  ok('実ツリー: キット追随の検査が通る（#492 の回帰）', () => {
    const fsK = require('fs');
    const kit = pathFb.resolve(__dirname, '../planning/tools/impl-handoff-kit/repo-template');
    if (!fsK.existsSync(kit)) return; // planning 未 populate のローカル環境では skip（CI は --require-planning で落とす）
    const { execFileSync } = require('child_process');
    execFileSync(process.execPath, [pathFb.join(__dirname, 'check-kit-sync.js'), '--require-planning'], {
      cwd: pathFb.resolve(__dirname, '..'),
      stdio: 'pipe',
    });
  });
};
