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
};
