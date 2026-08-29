---
title: RiskManagementService を単一プロジェクト＋VSA 樹形へ移送する（W11 段 4-11・本波の最終）
type: spec
status: approved
related_ids: [NFR, IADR-0259, IADR-0263, IADR-0264, IADR-0265, IADR-0266, IADR-0050, IADR-0260]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
plan_refs: []
---

# 仕様書: RiskManagementService の単一プロジェクト＋VSA 移送（W11 段 4-11）

> **11 サービス移送波の 11 本目＝最後**であり、**本波で最大のサービス**である（`.cs` 339・csproj 8・
> migration 20 本）。1 本目（AuditService・[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)）・
> 2 本目（ConfigurationService・[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)）・
> 3〜10 本目で確定した判断の型をそのまま適用した。判断 1〜8 に新しい軸は生じなかったが、
> **本サービスだけが「他サービスから参照される側」であり、単一プロジェクト化がクロスサービス参照へ及ぼす
> 影響には先行 10 本に前例が無かった**ため、[IADR-0266](../adr/IADR-0266_cross-service-project-reference-extern-alias.md)
> を新設した（末尾「IADR の要否」参照）。

## 起点

- 起点 ID: **`NFR`（無採番）**。構造移送＝メタ作業であり、`.claude/rules/traceability.md`
  「起点 ID の種別」の無採番許容ケース **2** に当たる（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)
  が確定済みの判断を継承する。環流はしない）。
- 上流: [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)（樹形・写像方針表。決定 5＝`Hosted/` は
  ルート直下）・[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)（1 本目の 5 決定。
  特に決定 3＝技術プリミティブは `Common/Abstractions/`、決定 4＝`internal`→`public` は最小限）・
  [IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定 3（Domain / Features の
  切り分け基準と 🔴 注記「移送で型の層を変えない」）・
  [IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)（検査の下限の動的化。本 PR は手で触っていない）

## 着手前に読んだもの

- `CLAUDE.md` / `.claude/rules/traceability.md` / `.claude/rules/traceability.repo.md` / `docs/DEFINITION_OF_DONE.md`
- [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) / [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) /
  [IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) / [IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)
- 先行の作業仕様書 `.ai-context/specs/2026082*vsa*.md`（**本ブランチから読める 9 本**）と、
  10 本目 `20260829_w11s10_reportservice-vsa.md`（**develop 未マージ**のため
  `git show claude/ast-implementation-issues-rzkoxb-w11s10:...` で読んだ。読めないものを読んだことにしない）
- 移送済みの実物（樹形・csproj・`Common/` の中身の手本）:
  `backend/Services/{AuditService,ConfigurationService,CostControlService,BacktestService,MarketMonitorService,
  NotificationService,InformationCollectionService,OrderExecutionService,TradeDecisionService}/`
- 基盤（`/home/user/microservices-platform`・読み取り専用）の `Features/` 名の実例

## 対象範囲

- 対象（着手前に想定した範囲）: `backend/Services/RiskManagementService/`（8 csproj → 2 csproj）、
  `backend/backend.slnx`、`backend/Tests/AiStockTrading.IntegrationTests/`
  （**`Aliases="RiskManagementWorker"` 付き 2 参照 → 1 参照**・`.cs` の `using` 追随）、
  `docker-compose.yml`、`scripts/k8s-local-images.sh`、`docs/operations/banned-symbol-unlock-runbook.md`
- **走査で範囲へ加えたもの**: `backend/Services/TradeDecisionService/`（csproj 2 本・`.cs` 10 件）と
  `backend/Services/BacktestService/Tests/`（csproj 1 本・`.cs` 1 件）＝クロスサービス参照の張り替えと
  extern alias（判断 9）／`.ai-context/adr/` の**リンク先** 7 ファイル／`scripts/scripts.repo.test.js` の
  実ファイル参照 2 行／`docs/tests/FR-10_*.md`・`docs/tests/FR-19_*.md`・`docs/security/security.md`・
  `docs/tech/tech-requirements.md`
- 対象外: `backend/Shared/` `backend/TestSupport/`（据え置き集合）、`.ai-context/` の**散文**（凍結記録）、
  `backend/Services/ReportService/`（10 本目・別 PR で並行移送中。本ブランチの base には未マージ）

## 親から渡された前提の数え直し（規則 9・10。**転記しない**）

| 項目 | 親の前提 | 自分で数え直した実測（base `00458d7`） | 判定 |
| --- | --- | --- | --- |
| `.cs` 件数 | 339 | **339**（`git ls-files` で数えた。src 229・tests 110） | **一致** |
| csproj | 8 | **8**（src 4・tests 4） | **一致** |
| migration | 20 本 / 関連 41 ファイル | **20 本**（`[Migration]` 付き）／**41 ファイル**（20×2 ＋ `RiskManagementDbContextModelSnapshot.cs`） | **一致** |
| `: DbContext` 継承 | 1 | **1**（`RiskManagementDbContext`） | **一致** |
| `: BackgroundService` 継承 | 3 | **3**（`QuoteRefreshService` / `WithdrawalEvaluationService` / `ObservedDrawdownRefreshService`。`Program.cs` の `AddHostedService<>` 3 箇所と全数突合） | **一致** |
| `IntegrationTests.csproj` の該当行 | 35・36 行目・同一 alias | **35・36 行目・ともに `Aliases="RiskManagementWorker"`** | **一致** |
| csproj 34 行目のコメントの `Foundation` セグメント | 「古い」 | **古い**（実 `namespace` は `RiskManagementService.Infrastructure.Persistence`。`Foundation` はフォルダ名にのみ残る） | **一致** |
| `Infrastructure/Steps/` の要否 | 「自分で数えろ」 | **要る**（`Composable/Steps/` に 14 ファイル・`namespace RiskManagementService.Infrastructure.Steps` 14 件） | — |

**結論: 親の前提はすべて一致した。**

## 着手前の母集合の引き直し（`.claude/rules/traceability.repo.md` 規則 1〜10）

**母集合は記憶で挙げず、誤りになる側の文字列で全追跡ファイルを走査して引いた**（規則 1・2・9・10）。
走査語は 4 通り: ① `RiskManagementService\.(Api|Application|Domain|Infrastructure)`
② `Services/RiskManagementService/src/` ③ `\.\.\./` で始まる省略パス ④ `Composable|Foundation`。

### 走査で引いた母集合と処置

| 対象 | 実測 | 処置 |
| --- | --- | --- |
| `backend/backend.slnx` | `Project` 8 本 ＋ サービスの `Folder` 宣言 | **是正**（2 本へ置換・フォルダ宣言は削除） |
| `backend/Tests/AiStockTrading.IntegrationTests/AiStockTrading.IntegrationTests.csproj` 35・36 行 | **2 行が同一 alias `RiskManagementWorker`** | **是正**（**2 行 → 1 行**へ畳む。`Aliases` は保持。34 行目の古いコメントも実態へ直す） |
| `backend/Tests/AiStockTrading.IntegrationTests/PositionDriftStateConcurrencyE2ETests.cs:4` | `using RiskManagementService.Application.Ports;`（**alias 無し**＝推移参照の global alias に依存） | 🔴 **是正必須**（単一プロジェクト化で推移参照が消え global には見えなくなる。`using RiskManagementWorker::RiskManagementService.Features.RiskManagement;` へ） |
| `backend/Tests/AiStockTrading.IntegrationTests/*.cs` の `RiskManagementWorker::…Infrastructure.Persistence.{RiskManagementDbContext,EfPositionDriftStateStore}`（型エイリアス 3 件・2 ファイル） | 3 件 | **不変**（`Infrastructure.Persistence` は移送で変わらない） |
| `RiskManagementWorker::Program`（4 箇所・3 ファイル） | 4 件 | **不変** |
| 🔴 `backend/Services/TradeDecisionService/TradeDecisionService.csproj:25` | `RiskManagementService.Domain.csproj` への `ProjectReference` | **是正**（`..\RiskManagementService\RiskManagementService.csproj` へ）。[IADR-0260](../adr/IADR-0260_shared-kernel-for-cross-service-domain-types.md) が「残るクロスサービス参照」として記録している 2 本のうち 1 本 |
| 🔴 `backend/Services/BacktestService/Tests/BacktestService.Tests.csproj:19` | 同上（テスト専用） | **是正**（同上） |
| `docker-compose.yml` | `SERVICE_PROJECT` / `SERVICE_DLL` | **是正** |
| `scripts/k8s-local-images.sh` | `risk-management-service` の csproj / dll | **是正** |
| 🔴 `docs/operations/banned-symbol-unlock-runbook.md:91-92` | `Services/RiskManagementService/src/RiskManagementService.Application/...` の直書き | **是正**（**未移送だったので今まで正しかった**。移送で誤りになる） |
| `docs/tech/tech-requirements.md:99-100` | 名前空間規約の説明 | **据え置きが正しい**（親の実測確認済み。名前空間は変わらない層のみを例示） |
| `docs/security/security.md:96` | `TestSupport` の言及 | **据え置きが正しい**（据え置き集合・ファイル実在） |
| 🔴 `docs/tests/FR-10_risk-controls-tests.md` / `FR-10_risk-guard-core-tests.md` / `FR-19_trading-guards-tests.md` | **旧テストプロジェクト名**（`RiskManagementService.{Api,Application,Domain,Infrastructure}.Tests`）**22 箇所** | 🔴 **是正**（4 プロジェクトが 1 本になり、書かれている名のアセンブリは実在しなくなる。移送済みサービスの同型記述は `docs/` に 0 件＝先行 10 本は残していない。`OrderExecutionService.Tests` の実例が同ファイルにある） |
| 🔴 `docs/security/security.md:178` | `RiskManagementService.Application/State/SettingsChangeEntry.cs`（ソースパス） | 🔴 **是正**（同じ表の 1 行上が `AuditService/Features/AuditEvents/AuditEntry.cs`＝移送後の形。**同じ表の中で新旧が混ざる**） |
| 🔴 `docs/tech/tech-requirements.md:101` | 「**アセンブリ名・プロジェクト名は変えていない**（`RiskManagementService.Domain.csproj` のまま）」 | 🔴 **是正**（例示した csproj が消える。**旧構成が残る `ReportService.Domain.csproj` へ差し替え**＝主張は真のまま例だけ生きた物へ。同 98-100 行の名前空間の説明は**据え置きが正しい**） |
| `docs/data/risk-management-aggregates.md:20,106` | `RiskManagementService.Domain`（**名前空間**） | **据え置きが正しい**（名前空間は不変） |
| `.ai-context/specs/` `.ai-context/adr/` の**散文**の同パターン | specs 20 ファイル・adr 14 ファイル | **凍結記録のため未更新**（`.claude/rules/traceability.repo.md` の除外規定。point-in-time の記録） |
| 🔴 `.ai-context/adr/` の **Markdown リンク先**（`](../../backend/Services/RiskManagementService/src/…)`） | **7 ファイル 11 リンク**（IADR-0002/0003/0004/0005/0006/0008 の `Domain/` 直下 10 件 ＋ IADR-0086 の `RiskControlEndpoints.cs`） | 🔴 **是正必須**（散文は凍結でも**リンク先は生きている**。`node scripts/check-doc-links.js` が落ちる。先例: MarketMonitorService 移送（#590）が `IADR-0090` のリンクを同じ理由で直している。後述「想定外」2） |
| `.gitleaksignore` | fingerprint 中の履歴上のパス | **未更新が正しい**（`<commit>:<当時のパス>:<rule>:<line>`。書き換えると誤検知が復活する） |
| `scripts/scripts.repo.test.js` | 7 件。**うち 5 件は glob / `pathService` の合成パス文字列**、🔴 **2 件（997・1001 行）は `fs.existsSync` で実ファイルの存在を主張する**（`check-coverage` の自動生成判定の実物検査） | **合成パス 5 件は未更新が正しい**（実ツリーを参照しない。1 本目からの既定で `AuditService/src/…` も残っている）／🔴 **実ファイル 2 件は是正必須**（走査だけでは区別できず、`node --test scripts/scripts.test.js` が落ちて初めて判った。後述「想定外」3） |
| `backend/Tests/AiStockTrading.Architecture.Tests/DomainSourceDependencyTests.cs` | `IsAllowedDomainNamespace` の**文字列判定の入力例** | **未更新が正しい**（`RiskManagementService.Domain` / `.Infrastructure.Persistence` は移送後も実在） |
| `backend/Tests/AiStockTrading.Architecture.Tests/SharedKernelIsLeafTests.cs` | 共有カーネルの葉性検査 | 名前空間参照のみ・**不変** |
| `deploy/helm/ai-stock-trading/files/pipeline.json` | consumer FQN | `RiskManagementService.Infrastructure.Steps.*`＝**不変**（`Steps` の名前空間は変えない） |
| `backend/Tests/` 配下の `RiskManagementService` **裸文字列**（`Path.Combine`・リテラル。規則 4） | 上記以外に 0 件 | — |
| 型エイリアス `using X = …;`（9 本目の発見の再走査） | サービス内 0 件・`IntegrationTests` に 3 件（前掲・不変） | — |

### 走査で見つかった「想定外」（走査だけでは出ず、ビルド／検査器が初めて出したもの）

🔴🔴 **1. サービス本体が実行可能プロジェクトになったことで `Program` が衝突する（本 PR 最大の是正）。**
`RiskManagementService.Domain.csproj` を参照していた 2 プロジェクト（`TradeDecisionService` /
`BacktestService.Tests`）の参照先を `RiskManagementService.csproj` へ張り替えた瞬間、
**`CS0433: The type 'Program' exists in both …` が 24 件**出た。全サービスが `Program.cs` 末尾に
`public partial class Program { }` を持つためである。**移送前は参照先がクラスライブラリだったので起きなかった。**
処置と根拠は判断 9・[IADR-0266](../adr/IADR-0266_cross-service-project-reference-extern-alias.md)。
🔴 **`Aliases` は推移参照へ伝播せず、`TradeDecisionService.Tests` では 2 回目のビルドで再発した**
（1 回目のエラー集合には現れない）。

🔴 **2. 凍結記録（`.ai-context/adr/`）でも「リンク先」は生きている。**
散文は書き換えないが、`](../../backend/Services/RiskManagementService/src/…)` は
`node scripts/check-doc-links.js` が実在を検査する。**7 ファイル 11 リンクが赤になった。**
`.ai-context/` を一律「凍結だから触らない」と判定すると落ちる。**先例が MarketMonitorService の
移送（#590）に実在した**（`IADR-0090` のリンクを同じ理由で直している）——
**先行 PR の diff を見に行って初めて「据え置きではない」と分かった。**

🔴 **3. `scripts/scripts.repo.test.js` の `RiskManagementService` は合成パスだけではない。**
10 本目までの仕様書は同ファイルを一律「glob の合成パス文字列・未更新が正しい」と判定してきたが、
**997・1001 行は `assert.ok(fs.existsSync(designer) && fs.existsSync(body), '検査対象の実ファイルが無い')`
であり実ツリーを指す**（`check-coverage` の自動生成判定を**実物**で検査するために、意図的に
モックでなく実ファイルを使っている箇所である）。**`node --test scripts/scripts.test.js` が落ちて初めて判った。**
同ファイル内の他 5 件（`pathService` の入力・`matchesAny` の入力）は合成パスであり据え置いた
——**同じファイルの中で処置が割れる。ファイル単位で「据え置き」と決めない。**

**4. 使われていない `using` エイリアスが、廃止したフォルダ名を主張していた。**
`Tests/RiskWorkerWebApplicationFactory.cs` の `using Composable = RiskManagementService.Infrastructure;`
は**参照 0 件の死んだ行**で（同ファイル内の `Composable.` の出現は自分のコメントのみ）、
`Composable/` フォルダを本 PR が廃止することでコメントごと偽になる。**削除してビルドが緑のままであることで
未使用を証明したうえで**、コメントと合わせて 2 行を落とした。

**5. 一括置換スクリプトは対象外構文の出現数を前後で突合した**（9 本目で `using var` を壊した事故の再発防止）。
リポジトリ全追跡 `.cs`（1310 件）で実測: `using var` **689 → 689**／`using (` **107 → 107**／
`await using` **69 → 69**／`namespace` 宣言行 **1298 → 1298**／型エイリアス `using X = ` **51 → 51**／
`extern alias` **6 → 17**（判断 9 で 11 ファイルへ追加＝意図した増加）／`.cs` **1310 → 1310**。
素の `using X;` 行だけが **4834 → 4749**（重複の除去と、移送で自分の名前空間になった行の削除）。

## 設計

### 判断 1: 集約は 1 つ・名前は `RiskManagement`（`Features/RiskManagement/`）

**AST 内の先行実例に揃えた。** 本リポの移送済み 9 サービスのうち **6 サービス**が
「サービス名から `Service` 接尾辞を落とした形」を採っている（`BacktestService`→`Backtest` /
`CostControlService`→`CostControl` / `InformationCollectionService`→`InformationCollection` /
`MarketMonitorService`→`MarketMonitor` / `OrderExecutionService`→`OrderExecution` /
`TradeDecisionService`→`TradeDecision`）。残り 3 は `AuditEvents` / `Assumptions` / `Notifications`。

**HTTP ルート（`/risk-controls`）へ寄せる案（`RiskControls`）は採らない。** 先行実例で
ルートと集約名が食い違うケース（`CostControlService`: ルート `/costs`・集約 `CostControl` ／
`MarketMonitorService`: ルート `/monitor`・集約 `MarketMonitor`）は、**いずれもサービス名側を選んでいる**。
基盤（MSP）の `Features/` 名（`Notifications` / `Documents` / `Search` 等）はルート名詞に一致するが、
**AST 側の既定が確立している以上、11 本目だけ別の規則へ寄せる理由が無い**（同じ問いに 2 つの答えを並べない）。

操作フォルダの兄弟（3 段目のスライス分割）は採らない（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定 1）ため
`_Shared/` も作らない（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 1）。

### 判断 2: `Domain/` と `Features/RiskManagement/` の切り分け（[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定 3 の適用）

基準（**Domain ＝フレームワーク・DI・I/O に触れず業務概念そのものを表す型。ポート・アプリケーション
サービス・エンドポイント・DTO・ストアは `Features/<集約>/`**）と 🔴 注記（**移送で型の層を変えない**）を
そのまま適用した。

| 元 | 件数 | 置き場 |
| --- | ---: | --- |
| `src/*.Domain/`（`Manipulation/` 6 を含む） | 44 | **`Domain/`**（`Domain/Manipulation/` の入れ子は名前空間 `RiskManagementService.Domain.Manipulation` に対応するため維持） |
| `src/*.Application/Ports/`（`IClock` 以外の 25） | 25 | **`Features/RiskManagement/`** |
| `src/*.Application/Services/` | 26 | **`Features/RiskManagement/`** |
| `src/*.Application/State/` | 17 | **`Features/RiskManagement/`** |
| `src/*.Api/Foundation/Endpoints/RiskControlEndpoints.cs`（要求 DTO 10 種を同居） | 1 | **`Features/RiskManagement/`** |

### 判断 3: `IClock` / `SystemClock` に加え、`TradingDay` も `Common/Abstractions/`

`IClock`（`Ports/`）と `SystemClock`（`Adapters/`）は
[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 3 のそのままの適用
（先行 9 本すべてと同じ）。

**`TradingDay`（`Adapters/`・`public static class`）も同じ場所へ置いた。** 理由:

- **I/O も DI も持たない薄い技術プリミティブ**であり、`Infrastructure/` の 3 区分（Persistence /
  ExternalServices / Steps）の**いずれにも実態として当てはまらない**——これは決定 3 が
  「抽象と同じ場所に置く」を優先すると定めた条件そのものである。
- **`SystemClock.Today` が `TradingDay.Of` を呼ぶ**（`IClock.Today` の実装が `TradingDay` に一本化されている・
  `#463` / `IADR-0181`）。**`Common/Abstractions/` に置かれる `SystemClock` の依存先である**以上、
  `Infrastructure/` へ落とすと `Common` → `Infrastructure` の逆流が生まれる。
- 実利用は `Common/Abstractions`（`SystemClock`）・`Features/`（3 サービス）・`Infrastructure/Steps`
  （`BrokerPositionsObservedHandler`）に跨る。**`Features/` へ置くと `Infrastructure` → `Features` になる**
  （[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定 3 が禁止する向き）。
  3 者から引ける場所は `Common/` だけである。
- **`Domain/` へ上げない**（決定 3 🔴 注記「移送で型の層を変えない」。元は Application 層）。

### 判断 4: ストア実装・既定実装は本番実装の Infrastructure 区分に合わせて対で置く

先行 9 本と同じ規則（本番実装のある区分へ、既定／代替／デコレータも同居させる）。

| 元 | 件数 | 置き場 | 根拠 |
| --- | ---: | --- | --- |
| `src/*.Infrastructure/Foundation/Persistence/`（DbContext・Factory・Ef ストア 19・行定義・シリアライズ） | 23 | **`Infrastructure/Persistence/`** | 名前空間 `…Infrastructure.Persistence` が既に一致（フォルダのみ移動） |
| `src/*.Infrastructure/Migrations/` | 41 | **`Infrastructure/Persistence/Migrations/`** | 先行 9 本と同じ。名前空間 `…Infrastructure.Migrations` は**不変** |
| `src/*.Application/Adapters/InMemory*`（21） | 21 | **`Infrastructure/Persistence/`** | `Ef*` と対（`InMemoryCostLedger` / `InMemoryAuditEventStore` の先例）。**`InMemoryBrokerAccountObservationStore` / `InMemoryInformationDegradationStore` は `Ef*` を持たないが、それ自体が本番実装の状態ストアである**（どちらも「永続化しない」ことが設計意図） |
| `src/*.Application/Adapters/OrderActivityProjection.cs` | 1 | **`Infrastructure/Persistence/`** | `InMemoryOrderActivityStore` と `EfOrderActivityStore` が**共有する純ヘルパ**（本文コメントが明記）。両利用者が Persistence にある |
| `src/*.Application/Adapters/SimulatorProfileRiskSettingsStore.cs` | 1 | **`Infrastructure/Persistence/`** | `IRiskSettingsStore` のデコレータ。内側は `EfRiskSettingsStore`（Persistence） |
| `src/*.Infrastructure/Composable/SimulatorProfileOptions.cs` | 1 | **`Infrastructure/Persistence/`** | 上のデコレータの有効化だけを担う Options。**`Infrastructure/` 直下にファイルを置く樹形は先行 9 本に 1 例も無い** |
| `src/*.Application/Adapters/{LedgerPortfolioStateProvider, ManipulativeOrderPatternDetector, UnavailableMaintenanceMarginSnapshotSource, WeekendBusinessCalendar}` | 4 | **`Infrastructure/ExternalServices/`** | 供給ポートの実装・既定アダプタ。I/O が無くても本番実装と同じ区分へ置く先例（`WeekdayMarketSchedule` / `PlaceholderPositionStore`。w11s5 判断 4） |
| `src/*.Infrastructure/Composable/MarketData/CachedCurrentPriceSource.cs` | 1 | **`Infrastructure/ExternalServices/`** | `ICurrentPriceSource` の実装（`QuoteCache` 越しの現在値供給） |
| `src/*.Infrastructure/Composable/Steps/` | 14 | **`Infrastructure/Steps/`** | Wolverine ハンドラ（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 5）。名前空間 `…Infrastructure.Steps` は**不変** |

**`Infrastructure/Messaging/` は作らない**（先行 9 本に 1 つも無い）。**`Common/Behaviors/` `Common/Exceptions/` も
作らない**（実体が無い——本サービスは `*ConcurrencyException` のような Application 直下の例外型を持たない）。

### 判断 5: `BackgroundService` 3 件と、その専用 Options 2 件は `Hosted/`

[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定 5（利用者裁定・`Hosted/` はルート直下）の適用。

| 型 | 元 | 置き場 |
| --- | --- | --- |
| `QuoteRefreshService` | `Infrastructure/Composable/MarketData/` | **`Hosted/`** |
| `WithdrawalEvaluationService` ＋ `WithdrawalEvaluationOptions` | `Infrastructure/Composable/StageGate/` | **`Hosted/`** |
| `ObservedDrawdownRefreshService` ＋ `ObservedDrawdownRefreshOptions` | `Infrastructure/Composable/StageGate/` | **`Hosted/`** |

Options 2 件は **元の層が `Infrastructure/<層>/` 直下**（w11s8 申し送り 4 の判定基準「**元の層で判断する**」）
であり、常駐ジョブ専用であるため w11s5 の `MonitorOptions` と同型で `Hosted/` へ同居させる。
`SimulatorProfileOptions` は常駐ジョブ用ではないため判断 4 のとおり Persistence へ置く（同じ Options でも
**用途で分かれる**）。

### 判断 6: `internal` → `public` は「Tests が直接参照する型」＋ CS0053 連鎖に限る（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 4）

`^internal ` の行頭アンカーに加え **`^\s+internal `（インデントされたメンバー宣言）も別途走査した**
（w11s8 申し送り 1）。実測: 行頭 **62 件**・インデント **0 件**（メンバー単位の見落としは本サービスでは発生しない）。

🔴 **最小集合は grep で見積もらず、コンパイラに決めさせた。** 手順:

1. 語による見積もり（テスト側に型名が現れるか）で 48 型を `public` 化 → ビルド緑。
2. **その 48 型をすべて `internal` へ戻し**、クリーンビルドの `CS0122`（保護レベル）・`CS0053`
   （不整合なアクセシビリティ）だけを頼りに、**エラーが名指しした型だけ**を `public` へ戻す作業を
   収束するまで反復した（**3 ラウンド**: 5 型 → 21 型〔`*Row` の CS0053 連鎖〕→ 22 型 → 緑）。
3. 結果は **48 宣言行**。語による見積もりと**偶然一致した**が、**内訳は違った**
   （`BrokerProviderUpdateRequest` は語では当たるが**コンパイル上は不要**で `internal` のまま。
   逆に `EfPortfolioLedgerStore` などは語の出現数が少なく見積もりでは弱かった）。
   **「テストに名前が出る」と「テストがコンパイル上必要とする」は別物である。**

### 判断 7: 🔴 `InternalsVisibleTo` は 5 エントリすべて削除する（(a) を採る）

旧 csproj の `InternalsVisibleTo` は 5 件（`.Api` に 1・`.Infrastructure` に 4）。うち
**`AiStockTrading.IntegrationTests` 宛の 1 件**（`#305` / `IADR-0124` の並行トークン E2E のため）が
本サービス固有の論点である。親が提示した 2 案のうち **(a)（2 型を `public` にして撤去）を採った。**

**決め手は「(b) を採っても公開面は 1 バイトも狭まらない」という実測である。**

| 型 | 単体テスト（`RiskManagementService.Infrastructure.Tests`）からの直接参照 | 統合テストからの直接参照 |
| --- | --- | --- |
| `RiskManagementDbContext` | **12 ファイル**（`new RiskManagementDbContext(...)` / `DbContextOptionsBuilder<RiskManagementDbContext>`） | 2 ファイル |
| `EfPositionDriftStateStore` | **1 ファイル**（`EfPositionDriftStateStoreTests`） | 1 ファイル |

すなわち**どちらの型も、統合テストとは無関係に、単体テストのために
[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 4 で `public` 化される。**
`InternalsVisibleTo Include="AiStockTrading.IntegrationTests"` を残しても**開く対象がもう無い**ため、
(b) は「公開面を広げない」という利点を実際には持たない。したがって
**先行 10 本と同じく全撤去**とする（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)「結果」節が
良い影響として挙げた「層をまたぐために開けていた公開面が実際に閉じる」に沿う）。

`public` 化の一覧と、`internal` のまま据え置いたものは「public 化の内訳」節に記す。

### 判断 8: 名前空間の書き換え（**フォルダだけを動かし、要らない書き換えはしない**）

[IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md) で `RiskManagementService.*` へ先行整合済み。
フォルダ移動に伴い変えたのは以下のみ。

| 旧 | 新 |
| --- | --- |
| `RiskManagementService.Application.Ports`（`IClock` 除く）/ `.Services` / `.State` / `RiskManagementService.Api.Endpoints` | `RiskManagementService.Features.RiskManagement` |
| `RiskManagementService.Application.Ports`（`IClock`）/ `.Application.Adapters`（`SystemClock` / `TradingDay`） | `RiskManagementService.Common.Abstractions` |
| `RiskManagementService.Application.Adapters`（`InMemory*` 21・`OrderActivityProjection`・`SimulatorProfileRiskSettingsStore`） | `RiskManagementService.Infrastructure.Persistence` |
| `RiskManagementService.Infrastructure`（`SimulatorProfileOptions`） | `RiskManagementService.Infrastructure.Persistence` |
| `RiskManagementService.Application.Adapters`（残り 4） / `RiskManagementService.Infrastructure.MarketData`（`CachedCurrentPriceSource`） | `RiskManagementService.Infrastructure.ExternalServices` |
| `RiskManagementService.Infrastructure.MarketData`（`QuoteRefreshService`）/ `RiskManagementService.Infrastructure.StageGate`（4） | `RiskManagementService.Hosted` |
| テスト 4 種（`.Api.Tests` / `.Application.Tests` / `.Domain.Tests` / `.Infrastructure.Tests`） | `RiskManagementService.Tests` |
| `.Api.Tests.Contracts` | `RiskManagementService.Tests.Contracts` |
| `.Application.Tests.Manipulation` / `.Domain.Tests.Manipulation` | `RiskManagementService.Tests.Manipulation` |

🔴 **不変**: `RiskManagementService.Domain` / `.Domain.Manipulation` / `.Infrastructure.Persistence` /
`.Infrastructure.Migrations` / `.Infrastructure.Steps`。
→ **`RiskManagementDbContextModelSnapshot` と 20 個の `*.Designer.cs` が持つエンティティ FQN
`"RiskManagementService.Infrastructure.Persistence.*Row"` は 1 文字も変わらない。**
→ **`deploy/helm/ai-stock-trading/files/pipeline.json` の consumer FQN（`…Infrastructure.Steps.*`）も不変。**

### 判断 9: 🔴 クロスサービス参照は extern alias で通す（[IADR-0266](../adr/IADR-0266_cross-service-project-reference-extern-alias.md)）

**本サービスだけが「他サービスから参照される側」である**（[IADR-0260](../adr/IADR-0260_shared-kernel-for-cross-service-domain-types.md)
が「残るクロスサービス参照」と記録した 2 本）。参照先が Web SDK の実行可能プロジェクトになるため
`Program` が衝突する（想定外 1）。処置:

| ファイル | 変更 |
| --- | --- |
| `backend/Services/TradeDecisionService/TradeDecisionService.csproj` | 参照先を `..\RiskManagementService\RiskManagementService.csproj` へ張り替え、`Aliases="RiskManagementWorker"` を付与 |
| `backend/Services/BacktestService/Tests/BacktestService.Tests.csproj` | 同上（`..\..\` 起点） |
| `backend/Services/TradeDecisionService/Tests/TradeDecisionService.Tests.csproj` | 🔴 **同じプロジェクトを明示参照して `Aliases` を付与**（推移参照には別名が伝播しないため） |
| `.cs` **11 ファイル**（TradeDecisionService src 4 ／ 同 Tests 6 ／ BacktestService Tests 1） | 先頭に `extern alias RiskManagementWorker;`、`using RiskManagementService.Domain;` を `using RiskManagementWorker::RiskManagementService.Domain;` へ |

**テスト本文・アサーションは 1 行も変えていない。** 別名は `AiStockTrading.IntegrationTests` が
**この同じアセンブリ**に既に与えている `RiskManagementWorker` に揃えた（1 アセンブリ 1 名）。
振る舞いが変わらないことの実測（`appsettings*.json` の非伝播・Wolverine の発見範囲）は
[IADR-0266](../adr/IADR-0266_cross-service-project-reference-extern-alias.md) 決定 3。

## 目標樹形

```
backend/Services/RiskManagementService/
├── RiskManagementService.csproj    (Sdk="Microsoft.NET.Sdk.Web")
├── Program.cs / appsettings.json / appsettings.Development.json
├── Domain/                          44（うち Manipulation/ 6）
├── Features/RiskManagement/         69（ポート 25・サービス 26・状態 17・エンドポイント 1）
├── Common/Abstractions/             3（IClock / SystemClock / TradingDay）
├── Infrastructure/
│   ├── Persistence/                 47（+ Migrations/ 41）
│   ├── ExternalServices/            5
│   └── Steps/                       14
├── Hosted/                          5
└── Tests/RiskManagementService.Tests.csproj + 110 .cs（Contracts/ 2・Manipulation/ 5 を含む）
```

（`Infrastructure/Messaging/` ・ `Common/Behaviors/` ・ `Common/Exceptions/` は実体が無いので作らない。）

## 実測（移送前後の突合）

すべて base `00458d7`（develop に 9 本目＝TradeDecisionService がマージされた直後）に対する値である。
**10 本目（ReportService）は本ブランチの base に含まれない**ため、本ツリーでは ReportService だけが旧構成のまま残る。

| 項目 | 移送前 | 移送後 | 判定 |
| --- | ---: | ---: | --- |
| 追跡 `.cs`（サービス配下） | 339（src 229・tests 110） | **339**（src 229・tests 110） | **一致**（`git mv` のみ・追加削除 0） |
| csproj | 8 | **2** | 目標どおり |
| `[Fact]` / `[Theory]` 属性 | 1045 | **1045** | **一致** |
| migration ファイル | 41 | **41**（全件 base と `sha256` 一致） | **一致** |
| `[Migration("…")]` の件数 | 20 | **20** | **一致** |
| `node scripts/list-test-projects.js --count` | 26 | **23** | 旧 4 → 新 1 の差 −3 と一致 |

### テスト件数（旧プロジェクトが消える前に個別 `dotnet test` で実測）

| テストアセンブリ | 移送前 | 移送後 |
| --- | ---: | ---: |
| `RiskManagementService.Api.Tests` | 136 | — |
| `RiskManagementService.Application.Tests` | 415 | — |
| `RiskManagementService.Domain.Tests` | 694 | — |
| `RiskManagementService.Infrastructure.Tests` | 229 | — |
| **`RiskManagementService.Tests`** | — | **1474** |
| 合計 | **1474** | **1474** |

### `using` の追随（`git diff --cached -M -U0 00458d7` の追加行から機械的に数えた）

**手で数えず、実装とテストの両方を母集合にした。** 追加された素の `using X;` 行は
**233 行 / 151 ファイル**（内訳: `Features.RiskManagement` 127・`Infrastructure.Persistence` 43・
`Common.Abstractions` 37・`Infrastructure.ExternalServices` 16・`Domain` 5・`Hosted` 4・`Infrastructure.Steps` 1）。
このほとんどは**旧名前空間の `using` の置き換え**である。**「1 ファイルの `using` 本数が純増した」もの**は
**13 ファイル（実装 1・テスト 12）**、**うち削除を伴わない純粋な追加は 6 ファイル（すべてテスト）**:

| 種別 | ファイル | 追加した `using` |
| --- | --- | --- |
| 実装 | `Infrastructure/ExternalServices/ManipulativeOrderPatternDetector.cs` | `Features.RiskManagement` ＋ `Common.Abstractions`（旧 `Application.Ports` が 2 つへ割れた） |
| テスト | `Tests/{ControlViolationAggregation,Stage1LiveProviderOrderRejection,StageGateLedger,StageGate,StageTransitionCriteriaCarriage}Tests.cs` | `RiskManagementService.Domain`（**祖先名前空間の暗黙解決が消えた分**） |
| テスト | `Tests/BrokerProviderEndpointTests.cs` | `Features.RiskManagement`（同上。加えて**部分修飾名** `Application.State.SettingsChangeType` を 2 箇所で素の型名へ） |
| テスト | `Tests/{LedgerPortfolioStateProvider,MarketDataWiring,MoomooFillControlRegression,OrderScreeningService}Tests.cs`・`Tests/Manipulation/{ManipulativeOrderPatternDetector,OrderScreeningManipulation}Tests.cs` | 旧 `Application.Adapters` 1 本が Persistence / ExternalServices / Common.Abstractions へ割れた分 |

🔴 **本サービスでは実装側の純増が 1 ファイルに留まった**（10 本目の ReportService より少ない）。
理由は実測で確認した: `RiskManagementService` は移送前から `Domain` が `Application.*` の**兄弟**であり
**祖先ではなかった**ため、実装ファイルは既に明示の `using RiskManagementService.Domain;` を持っていた。
壊れたのは「テストが `<Svc>.<層>.Tests` から祖先 `<Svc>.<層>` を暗黙に引いていた」経路が主である。
**この差は事前の grep では出ない**（壊れる前のソースにその `using` は無い）——
**クリーンビルドのエラー数の推移で確かめた: 32 → 43 → 19 → 1 → 0。**

## 検証（実行と出力）

キャッシュ全消去 → `dotnet restore` → `--no-restore` フルビルドの順で実施した。

- **ビルド**: `Build succeeded. / 0 Warning(s) / 0 Error(s) / Time Elapsed 00:00:46.16`
- **テスト**: `dotnet test backend/backend.slnx --no-build` —— **失敗は `AiStockTrading.IntegrationTests` の
  `Failed: 8, Passed: 5, Total: 13` のみ**で、8 件すべてが
  `Failed to connect to Docker endpoint at 'unix:///var/run/docker.sock'`（環境制約）。
  **`RiskManagementService.Tests` は 1474/1474 全緑。**
  **`extern alias` で解決している 3 つの `PositionDriftStateConcurrencyE2ETests` も
  「ビルドは通り Docker で落ちている」ことを確認した**（コンパイルエラーは 0）。
- **`dotnet format backend/backend.slnx --verify-no-changes`**: exit **0**（出力なし）
- **`dotnet ef migrations has-pending-model-changes`**（`--project` / `--startup-project` ともに
  `backend/Services/RiskManagementService`）: `No changes have been made to the model since the last migration.` / exit **0**
- **カバレッジ**: `cov/` を作り直して `find cov -name coverage.cobertura.xml | wc -l` = **23** =
  `node scripts/list-test-projects.js --count`。`node scripts/check-coverage.js --root cov` は
  **行カバレッジ 82.50%（16504/20006 行）/ floor 79.00%** で exit **0**（`coverage-floor.json` は未編集）。
- **検査器**（各々の終了コードを直接確認。パイプで潰していない）: `check-doc-links` 0（579 件）／
  `check-trace-blocks` 0（41 件）／`check-cross-repo-refs` 0（1888 件）／`check-plan-id-qualification` 0（1932 件）／
  `check-test-traceability` 0（463 ファイル・起点 ID 29 種）／`check-consumer-endpoint-names` 0（11 サービス・本番 `.cs` 694 件）／
  `check-banned-libraries` 0／`check-banned-settled-cash-sources` 0／`check-observability-assets` 0／
  `check-reading-budget` 0／`check-workflow-job-refs` 0／`check-action-versions` 0／`check-ai-workflow-config` 0／
  `check-adr-index-sync` 0／`gen-knowledge-graph --check` 0／
  `validate-pipeline-config --self-test` 0 と実データ 0（`steps=5, events=7`）／`validate-runtime-scaffold` 0（Worker 11）／
  `node --test scripts/scripts.test.js` 0／`node --test scripts/scripts.repo.test.js` 0

## 計画書との差異

- 差異: なし。本件は構造移送のみで振る舞いを変えていない（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定 7）。

## IADR の要否

### 作らないもの（判断 1〜8）

判断 1〜8 のすべてが [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) の写像方針表・
[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) の 5 決定・
[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定 3 から機械的に導ける。

- 判断 1（集約名）は「機械的規則より実例を優先する」という既存の運用指示の適用であり、新しい設計軸ではない。
- 判断 3（`TradingDay` を `Common/Abstractions/`）は [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)
  決定 3 の条件（I/O を持たず `Infrastructure/` の 3 区分に当てはまらない薄い技術プリミティブ）の
  **そのままの適用**であり、新しい判定基準を作っていない。
- 🔴 **判断 7（`InternalsVisibleTo` 全撤去）も新しい軸ではない。** 親は「新しい軸だと考えるなら
  `IADR-0266` を作ってよい」としたが、**実測の結果 (a)/(b) の選択が公開面に差を生まないと分かった**ため、
  決めるべき論点そのものが消えた（判断 7 の表）。
  [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 4 を統合テストへ
  適用しただけであり、**決定 4 の「Tests プロジェクト」に統合テストが含まれることは同決定の文言から
  直接読める**（拡張ではない）。差が無い選択に ADR を立てると、**後から読む人に「ここには判断があった」と
  誤読させる**。

### 作ったもの — [IADR-0266](../adr/IADR-0266_cross-service-project-reference-extern-alias.md)（判断 9）

**採番は親が予約した `IADR-0266` を用い、主題を差し替えた。** 判断 9（クロスサービス参照）は
**先行 10 本に前例が無い**（本サービスだけが「他サービスから参照される側」だった）。
選択肢に恒久解（共有カーネルへの移設）が実在し、それを**採らない**と決めた以上、
**採らなかった理由と残余リスクを記録しないと、次に触る人が同じ検討をゼロからやり直す**。
`.ai-context/adr/README.md` の索引行も同時に追加した。

---

## ［2026-08-29 追記］develop 取り込みで作動した検査器の退役（IADR-0265 フォローアップ）

**本仕様書の本文は base `00458d7` に対する記録であり、そこでは検出できなかった事象がある。**
親が `origin/develop`（`c9d3d5a`＝ReportService 移送 #599 を含む）を取り込んだ時点で、
`AiStockTrading.Architecture.Tests.DomainLayerDependencyTests.Domain_プロジェクトの探索が空振りしていない`
が落ちた。

### なぜ base では緑で、合流で赤くなるのか

この検査の下限は `RepositoryLayout.UnmigratedServicesWithDomainProjectCount`（未移送で
`src/*.Domain` を持つサービスの実測）から動的に導かれる。

| ツリー | 未移送で Domain を持つサービス | 判定 |
| --- | ---: | --- |
| base `00458d7`（ReportService 未移送） | **1** | 緑 |
| develop 取り込み後（ReportService 移送済み ＋ 本 PR で RiskManagement 移送） | **0** | **赤** |

🔴 **これは退行ではなく、`IADR-0265` が仕込んだ設計どおりの作動である。** 0 件になった時点で
「`*.Domain.csproj` が 1 本も残っていない＝csproj 静的解析は入力集合が空で構造的に何も検査できない」
ため、黙って「違反なし」の緑を返さず、次にすることを名指しして落ちる。

**「合流でしか赤くならないテスト」の 4 例目**である（既知の 3 種＝`NotificationTemplateGoldenTests` /
`AuditCycleCompletenessTests` / `event-schemas.baseline.json`＋`EventMessageTypeNameTests` は
いずれも「他ブランチが母集合へ足す」型だったが、**本件は「他ブランチが母集合から引く」型**で向きが逆である）。

### 実施した退役

`IADR-0265` の宣言どおり退役させた。**利用者の明示的な承認を得て実施している**（テストファイルの
削除は既定で禁止されているため）。

| 対象 | 措置 |
| --- | --- |
| `DomainLayerDependencyTests.cs`（4 `[Fact]` ＋ 6 `[Theory]` ケース） | 削除 |
| `RepositoryLayout.DomainProjectFiles` | 削除（利用者は本クラスのみだった） |
| `RepositoryLayout.UnmigratedServicesWithDomainProjectCount` | 同上 |
| `RepositoryLayout.CountsAsUnmigratedServiceWithDomainProject` | 同上 |
| `RepositoryLayout.ServiceProjectFiles` / `SharedProjectFiles` / `ProjectFile` | **残す**（`ServiceClientProjectAbolishedTests` / `SharedProjectDependencyTests` が使用中） |

🔴 **enforcement は減っていない。** 削除した 3 検査（外部ライブラリ依存・プロジェクト参照の許可リスト・
推移閉包）は、`*.Domain.csproj` が 0 本になった時点で**入力集合が空**であり、残しても永久に
0 件走査で無条件に緑になる——本リポジトリが各所で潰してきた「静かに失効した検査器」そのものになる。
後継は `DomainSourceDependencyTests`（`IADR-0256` で二重化のために新設）であり、Domain の**ソース**を
**新旧樹形の和集合**で走査して、①`using` 許可リスト ②CPM 由来の禁止トークン（**完全修飾での迂回を塞ぐ**
＝旧・推移閉包検査の役割）③他サービス参照 を検査する。**走査件数・`using` 数・禁止トークン数の
3 つの下限検査を持つため「0 件走査で緑」にならない。**

### 実測

- `AiStockTrading.Architecture.Tests` **88 → 78**（削除した 10 ケースと完全一致）・**失敗 0**
- 全量 `dotnet test` の失敗は **8 件・全て `AiStockTrading.IntegrationTests`**（Docker 不在の環境制約）
- `dotnet build` 0 Warning / 0 Error ／ `dotnet format --verify-no-changes` exit 0
- カバレッジ **82.50%**（16504/20006）/ floor 79.00%・**レポート 20 件 = テストプロジェクト 20 件**
- `has-pending-model-changes`「変更なし」／ migration 関連 **41 ファイルすべて base と byte 一致**
