---
title: 単一プロジェクト＋VSA 構成の方針を IADR で確定し、必読規約と docs を追随させる（土台 PR 4）
type: spec
status: approved
related_ids: [NFR]
author: endazon (with Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md
---

# 仕様書: 単一プロジェクト＋VSA 構成の方針確定（IADR-0259）と規約・文書の追随

> 本仕様書は着手前に作成した。本件は **`backend/` を 1 行も変更しない**。
> 変更してよいのは `CLAUDE.md` / `docs/` / `.ai-context/` のみである。

## 起点

- 起点 ID: **`NFR`（無採番）**。本件は**規約整備・文書統制＝メタ作業**であり、
  `.claude/rules/traceability.md`「起点 ID の種別」の無採番許容ケース **2**
  （「ID 列はあるが、その作業に当たる番号が無い場合」）に当たる。
  着手前に計画の非機能要件表（`projects/ai-stock-trading/02_requirements/01_requirements.md`）を
  実際に読み、`NFR-01`〜`NFR-17` の全行を確認した。

  | ID | 分類 | 内容 | 本件との関係 |
  | --- | --- | --- | --- |
  | NFR-01 / 02 | 性能 | 発注所要時間・サイクル所要時間 | 無関係 |
  | NFR-03 / 04 | 可用性 | 稼働率・障害時の振る舞い | 無関係 |
  | NFR-05 / 06 | セキュリティ | 認証情報の保管・発注機能へのアクセス | 無関係 |
  | NFR-07 | 運用・保守 | 可観測性 | 無関係 |
  | NFR-08〜11 | 運用・保守 | 保持期間・パージの安全既定 | 無関係 |
  | NFR-12〜15 | 費用 | データ費用・LLM 費用・インフラ費用・総額 | 無関係 |
  | NFR-16 | 拡張性 | **証券会社・情報源・LLM の差し替え**をポートで抽象化 | **近いが当たらない。** 本件はソースツリーの割り方と文書統制であって、外部依存の差し替え可能性ではない。無理に付けると監査が「NFR-16 の実装」と数えてしまい、無採番より劣化する |
  | NFR-17 | 法規・規約 | 利用規約遵守 | 無関係 |

  → **ケース 2 に当たる。環流はしない**（計画側に不足があるわけではない。メタ作業のための番号を
  計画の非機能要件表へ新設しないことは計画側で裁定済み）。
- 利用者裁定 **2026-08-28**: AST バックエンドを単一プロジェクト＋VSA（1 サービス = 1 プロジェクト）へ
  全面移行する。①機能ウェーブ完走後に単独実施 ②**AST に適用**する ③`Shared.Contracts` は
  別プロジェクトとして維持。
  追加裁定: **`Common/Result.cs` は作らない** / **`BackgroundService` はルート直下 `Hosted/`** /
  **依存規則の強制は NsDepCop と自前検査器の併用**。
- 🔴 **前提の訂正（着手中に判明）。** 当初は「基盤（MSP）の 8 要素標準から AST だけが外れる」という
  骨子で起草していたが、**誤りである**。`/home/user/microservices-platform`（`origin/develop` = `9ae1136`）を
  読み取り専用で実地に確認した結果、**MSP も同じ 2026-08-28 のオーナー裁定で同一の樹形へ移行中**である。

  | 確認したこと | 実測 |
  | --- | --- |
  | `MSP:IADR-0282`「サービスは単一プロジェクト＋VSA/DDD フォルダ構成とする（8 要素プロジェクト実体化の撤回）」 | `status: Accepted`。`MSP:IADR-0280` の決定 1〜5・7 を supersede（決定 6 は存続） |
  | MSP の移送実体 | `src/knowledge/backend/Services/FeedbackService/` に `FeedbackService.csproj` / `Program.cs` / `Features/Feedback/` / `Domain/` / `Infrastructure/Persistence/{Migrations}` / `Tests/FeedbackService.Tests.csproj` が実在 |
  | MSP の依存方向検査 | `scripts/check-unit-dependencies.js` に **規則 3-③（名前空間走査版）が実装済み**で、規則 3-①②（csproj 版）と**新旧併走**している |
  | MSP の名前空間 | `FeedbackService.{Domain,Features,Infrastructure,Tests}`（`.Api` を落としたルート名前空間）。`ModelSnapshot` の `modelBuilder.Entity("FeedbackService.Domain.AnswerFeedback"` と**一致**している |

  → したがって本件は**基盤からの逸脱ではなく、基盤との整合**である。**`IADR-0001`（規約は基盤実装リポに
  揃える）は満たされる。** 「逸脱だから planning へ環流が要る」という論は成り立たない。
- **計画（`12_backend-application-stack.md` の 8 要素標準）との差は残る**が、これは MSP も同じ立場であり、
  **`MSP:IADR-0282` 決定 5 で MSP 側が planning へ改定依頼を起票する方針**である。
  **AST から重複起票しない**（起票が要ると判断する場合も、事前に planning の既存 issue を必ず検索する）。
- 先行する土台 PR: [IADR-0256](../adr/IADR-0256_domain-dependency-inspection-by-source-scan.md)（土台 1・マージ済）・
  [IADR-0257](../adr/IADR-0257_ci-test-sharding-lpt-by-scan.md)（土台 2・マージ済）。土台 3（構造依存検査器の
  両対応）は並行 PR で `IADR-0258` を確保している。

## 対象範囲

- 対象:
  1. 新 IADR **`IADR-0259`** を `Accepted` で追加する（単一プロジェクト＋VSA の方針確定・MSP との整合の明示）。
  2. `.ai-context/adr/README.md` の索引へ 1 行追加する。
  3. 既存 IADR の状態・関連節の更新（`IADR-0128` を Superseded、`IADR-0046` へ改定注記）。
  4. `CLAUDE.md`「技術スタック別ルール」のレイアウト記述を更新する（**同量削減とセット**）。
  5. `docs/` の追随（下記「母集合」の判定に従う）。
- 対象外（この PR で触らない）:
  - `backend/` 配下のすべて（**コードは 1 行も動かさない**）。
  - `scripts/`（`scripts/README.md` を含む）・`.github/workflows/`・`docker-compose.yml`：
    土台 3 と各サービス PR の担当であり、**並行 PR と競合する**。
  - ルート `README.md`・`.gitleaksignore`：本 PR の変更許可範囲外（`CLAUDE.md` / `docs/` / `.ai-context/` のみ）。
    追随先として実在するので、**除外として下表に明記する**。

## 母集合の引き直し（`.claude/rules/traceability.md` 規則 1〜10・`traceability.repo.md` 規則 9・10）

**issue 本文や設計書の「反映先」リストを転記せず、着手時に自分で引いた。** 誤りの側（＝移行によって
古くなる記述の側）から引き、軸を 6 本立てた（規則 1・2・5）。パスの除外だけで取り、拡張子・行フィルタで
絞っていない（規則 3・4）。

| 軸 | 何を引いたか | コマンド |
| ---: | --- | --- |
| 1 | サービスのパス前置 | `grep -rln "backend/Services" docs/` |
| 2 | 層プロジェクト名（`.Tests` 付き含む） | `grep -rlnE '\.(Api\|Application\|Domain\|Infrastructure)(\.Tests)?\b' docs/` |
| 3 | `{src,tests}` の 2 段・slnx | `grep -rlnE '(Services/[A-Za-z]+(Service)?/(src\|tests))\|/src/\|backend\.slnx' docs/` |
| 4 | 構成を語る日本語（誤りの側の語彙） | `grep -rlnE '4 層\|四層\|レイヤ\|層構成\|プロジェクト構成\|標準構成\|8 要素\|Worker' docs/` |
| 5 | 現行フォルダ規約の下位階層 | `grep -rlnE 'Foundation/Endpoints\|Composable\|/Ports/\|/Adapters/' docs/` |
| 6 | 検査器・CI の記述（土台 1・2 で既に変わったもの） | `grep -rnE 'シャード\|shard' docs/` / `grep -rn 'Architecture.Tests' docs/` |

**軸 1〜5 の和集合は 19 ファイル**（軸 1 が 7・軸 2 が 13・軸 3 が 7・軸 4 が 11・軸 5 が 3）。
軸を 1 本で終えていたら（軸 1 だけなら）`docs/data/` `docs/functional/` `docs/tests/FR-*` を落としていた。

### 追随する（本 PR）

| ファイル | 何が古くなるか | 対応 |
| --- | --- | --- |
| `CLAUDE.md` §技術スタック別ルール | 「サービスは `backend/Services/<ServiceName>/{src,tests}`」が移行先と食い違う | 移行先ツリーと**新旧混在**を明記。同量削減とセット |
| `docs/tech/tech-requirements.md` §プロジェクト構成 | **7 標準へ揃える**と宣言し 4 層ツリーを図示している（規約そのものの記述） | VSA を目標構成として書き直し、移行中の混在と旧構成を併記。依存規律の記述を**二重化後の実態**（ソース走査＋csproj）へ更新 |
| `docs/tests/README.md` §5 | テストの層＝本番プロジェクトと 1:1 の 4 種類（規約の記述） | サービスごと 1 テストプロジェクト（`<Svc>Service/Tests/`）を目標として併記 |

### 追随しない（除外）と、その理由

| ファイル / 群 | 除外理由 |
| --- | --- |
| `docs/how-to/local-run.md`（`TradeDecisionService/src/...Api`） | **今は正しい。** 当該サービスが移行するまで実在するパスであり、先回りして直すと**当たらない手順**になる。各サービス PR で直す |
| `docs/security/security.md` / `docs/integration/20260718_msp-frontend-integration-requirements.md` / `docs/operations/{banned-symbol-unlock,live-trading-cutover}-runbook.md` / `docs/data/risk-management-aggregates.md` / `docs/functional/FR-15_backtest.md` / `docs/tests/FR-10_*.md` / `docs/tests/FR-15_backtest-tests.md` / `docs/tests/FR-19_trading-guards-tests.md` | 同上。**具体の現行パス・プロジェクト名の引用**であり、規約の宣言ではない。移行が済んだサービスの PR で 1 件ずつ直す（先回りの一括書き換えは、混在期間中ずっと「文書だけ嘘」になる） |
| `docs/tech/system-architecture.md` / `docs/infra/infra.md` / `docs/observability/observability.md` / `docs/operations/operations.md` の `Worker` | **偽陽性。** これらの `Worker` は **Helm / k8s のワークロード**（10 Worker）であり、旧プロジェクト名 `<Svc>.Worker` ではない。VSA でもデプロイ単位は 1 サービス 1 コンテナのまま変わらない |
| `docs/functional/FR-19_trading-guard.md` の「レイヤリング」 | **偽陽性。** 相場操縦の手口（自己レイヤリング）であり層構成と無関係 |
| `docs/functional/FR-12_paper-trade.md` の `frontend/src/...` | **偽陽性。** フロントエンドのパスであり backend の構成に依存しない |
| `docs/ai-workflow.md` の `backend-test (1..4)` | **土台 2 で既に追随済み**（シャード 7 → 4）。本 PR で触らない |
| ルート `README.md` | 構成図を持つが、**本 PR の変更許可範囲外**（`CLAUDE.md` / `docs/` / `.ai-context/` のみ）。軸 1・2 にはヒットしない（実測）ため差分は小さい。仕上げ PR で追随する |
| `scripts/README.md` / `scripts/*.js` / `.github/workflows/` / `docker-compose.yml` / `.gitleaksignore` / `.github/ISSUE_TEMPLATE/ai-implementation.yml` | 土台 3・各サービス PR・仕上げ PR の担当。**並行 PR が同一ファイルを触っている**ため本 PR では触らない |
| `.ai-context/adr/**`（`IADR-0128` を除く） / `.ai-context/specs/**` | **凍結記録・point-in-time の記録**。本文プロズを後から書き換えない。`IADR-0128` は Superseded の表示のみ（状態欄と日付つき追記ブロック）を更新する —— `traceability.repo.md`「Superseded / Deprecated な ADR を引用するときの書式」が**状態欄は凍結の対象外**と定める |
| `CHANGELOG.md` | 生成物。コミット件名を書き換えず `scripts/changelog-overrides.json` で是正する規約 |

### 規則 10（是正で新たに誤りになる自分の記述）の引き直し

本 PR は「4 層を前提とする記述」を「VSA が目標・移行中は混在」へ変える。よって**是正後の語**
（`Vertical Slice` / `Features/` / `<Svc>Service/`）でも引き直し、二重宣言・食い違いが無いことを確認した。
実測は報告に載せる。

## 決定（本 PR で確定させること）

1. **樹形は `MSP:IADR-0282` 決定 1 をそのまま引く**（別案を作らない）。
   `Services/<Name>/` 直下に `<Name>.csproj`（**`.Api` 接尾辞を廃止**）・`Program.cs`・
   `Features/<集約>/<操作>/`・`Domain/`・`Infrastructure/{Persistence,Authentication,Messaging,ExternalServices}`・
   `Common/`・`Tests/<Name>.Tests.csproj`。
   AST 固有の追加は **`Hosted/` をルート直下に置く**ことだけである（利用者裁定。`BackgroundService` の置き場。
   設計書が置いていた `Infrastructure/Hosted/` は採らない）。
2. **`IADR-0128` は Superseded とする。** 同 IADR の決定 1〜6 はいずれも「層をプロジェクト境界で表す」ことを
   前提にしており、1 プロジェクト化で**前提ごと成立しなくなる**。決定 5（`ConfigurationService.Client` を
   第 8 のプロジェクトとして残す）は**結論を逆転**させる。一部だけを差し替える「改定」では、読み手が
   どの決定が生きているかを判別できない。**`MSP:IADR-0282` が `MSP:IADR-0280` を supersede したのと同型**である。
3. **`IADR-0046` は改定（部分）に留める。** 失効するのは決定 1 のうち `{src,tests}` の 2 段だけであり、
   決定 2（props の import-chain フォールバック）・決定 4（CI のパスを `backend/backend.slnx` 系に保つ）と、
   `backend/` 直下の `Services/` `Shared/` `TestSupport/` は**そのまま生きる**。
   決定 3（名前空間・アセンブリ名を再編で変えない）は**下の 6 で射程を分けて扱う**。
4. **`IADR-0001` は改定しない。満たされる。** 揃える先である MSP が同じ樹形へ移行しているため、
   本移行は IADR-0001 の**遵守**である。ただし「揃える」の射程が書かれていない点は変わらないので、
   IADR-0259 で**射程（プロジェクト割りを含む）を明示**する。
5. **`Foundation/` / `Composable/` の区分は AST でも廃止し `Features/` ほかへ吸収する**（`MSP:IADR-0282` 決定 1）。
   AST には両区分が**実在する**（実測: `backend/Services/*/src/*/{Foundation,Composable}` が **25 ディレクトリ**）。
6. 🔴 **名前空間は MSP と扱いが分かれる。両方を事実として書く。**
   - MSP 決定 3 は**ルート名前空間を `<Name>` へ改名**する（`.Api` を落とす）。実測でも
     `FeedbackService.Domain` で `ModelSnapshot` の FQN 文字列と一致している。
   - AST は **移送波では `namespace` を 1 行も変えない**。EF Core の `ModelSnapshot` がエンティティ型を
     **完全修飾名の文字列**で持つためである（実測: `modelBuilder.Entity("AiStockTrading.MarketMonitor.Infrastructure.Foundation.Persistence.CooldownRow"`。
     `ModelSnapshot` は **7 サービス**分ある）。名前空間を変えると次の `migrations add` が
     全テーブルの drop & create を生成し得る。
   - **MSP がこの整合をどう検証したか（`migrations add` の空差分確認の有無）は、本リポからは確認できていない。**
     憶測で「MSP では問題が無かった」と書かない。AST は自リポの制約に基づいて判断を維持する。
   - 帰結として、**フォルダ `Foundation/` を廃止しても名前空間の `...Infrastructure.Foundation....` は残る**。
     これは意図した混在であり、正規化は独立の波とする。
7. **移送波の完了までは、新規コードも現行配置で書く**（`MSP:IADR-0282` 決定 4-3 の教訓を引く）。
   「新規は新様式」を先行させると同一サービス内に二重構造が生じ、移送の照合が壊れる。
8. **依存規則の強制は NsDepCop と自前検査器の併用**（利用者裁定）。IADR-0256 の自前検査器は存続する。
   **NsDepCop は本リポにも MSP にも未導入である**（実測: `nsdepcop` の出現は本リポの散文 1 箇所のみ、
   MSP は 0 件）。**導入自体は本 PR では行わない**（コードを 1 行も動かさないため）。移送波で配備する。

## 受け入れ基準

- `node scripts/check-reading-budget.js` が緑（`CLAUDE.md` の増減内訳を報告に示す）。
- `node scripts/check-trace-blocks.js` が緑。`docs/` の新規記述に計画 ID・IADR・仕様書名・
  修飾付き issue 参照を**表示テキストとして書かない**。
- `node scripts/check-adr-index-sync.js` / `node scripts/check-doc-links.js` /
  `node scripts/gen-knowledge-graph.js --check` が緑。
- `git diff --name-only origin/develop | grep -c '^backend/'` が **0**。

## リスク・残余

- `IADR-0258` は並行 PR（土台 3）が確保している。索引行の追加は衝突する前提で、
  マージ時に FIFO で解消する。
- `docs/tech/tech-requirements.md` / `docs/tests/README.md` が `backend/Tests/AiStockTrading.PlanConformance.Tests`
  を挙げているが、**実ツリーには存在しない**（`ls backend/Tests/` は Architecture / Integration の 2 件）。
  これは VSA 移行とは独立した既存の齟齬である。**本 PR で書き直す行に含まれる分だけ実測へ合わせ**、
  それ以外（`docs/tests/README.md` の当該行）は**触らない** —— 行を消すことは「このプロジェクトは
  存在すべきでない」と主張することであり、経緯の確認が要る。別途起票する。

## 追記（2026-08-28・セッション再開分。利用者裁定 2026-08-28・確定の反映）

前回セッションは IADR-0259・IADR-0046/0128 の supersede 注記・`CLAUDE.md` の技術スタック節までを
書いており、`docs/tech/tech-requirements.md` / `docs/tests/README.md` の追随（対象範囲 5）が未着手のまま
残っていた。本追記はその完了と、利用者からの追加裁定 (a)〜(f) の反映を記録する。

### (a) 名前空間の完全整合（IADR-0259 決定 5 の撤回・確定）

`.ai-context/adr/IADR-0259_single-project-vsa-structure.md` 決定 5 を全面書き換えた。**撤回したのは
「移送波では `namespace` を 1 行も変えない」という言い切り**であり、「その場の構造再編そのものでは
変えない」（決定 6・IADR-0046 決定 3）という原則は維持した。撤回の根拠は MSP の波 4.5
（`/home/user/microservices-platform` の `.ai-context/specs/20260828_wave45-vsa-migration.md`、
`status: done`）を実地に確認したことである。読み取り専用クローンであり
`git rev-parse --is-shallow-repository` は `true` のため、参照は作業ツリーの現物のみに限った
（`git log`/`git blame` は出典に使っていない）。

実測（本リポジトリ・AST 側の規模）を差し替えた。当初の作業指示に含まれていた数値のうち、**そのまま
書き写さず実地に検算し直したものが 2 つある**（規則 7「数値を直したら関連ファイルを全走査し直す」に
準じ、指示された数値を鵜呑みにせず自前で再計算した）。

| 項目 | 指示に含まれていた数値 | 実測（本セッション） | 判断 |
| --- | ---: | ---: | --- |
| EF エンティティ数 | 35 / 35 | **35 / 35**（`grep -c 'modelBuilder\.Entity("' backend/**/*ModelSnapshot.cs` の合計。RiskManagementService は 21） | 一致。採用 |
| `*ModelSnapshot.cs` | 7 個 | **7 個** | 一致。採用 |
| 対象 migration | 35 個（RiskManagement 20） | **35 個（RiskManagement 20）** | 一致。採用 |
| csproj の `RootNamespace` 明示 | 43 個すべて | **43 個すべて**（`backend/Services/*/src/**/*.csproj` が対象。他形態の csproj は無関係） | 一致。採用 |
| `namespace` 宣言数 | 1,516 箇所 | **1,289 箇所**（`grep -rhoE '^\s*namespace\s+[A-Za-z0-9_.]+' backend --include="*.cs" \| wc -l`。file-scoped 1,210 ＋ block-scoped 79。2 通りの数え方で相互検算済み） | **不一致。実測値を採用**し、指示側の数値は使わなかった（本リポジトリの規則は「数値は記憶や指示ではなく実測で決める」ため） |
| テスト csproj 数（50 → 19） | 50 個 → 19 個 | 実在する `*.Tests.csproj` は **50 個**と一致するが、`AiStockTrading.IntegrationTests.csproj`（`*.Tests.csproj` の命名でないため素通りしていた）を含めると**実際は 51 個**。移行後の見込み値「19」は「サービス 11 ＋横断 8」の内訳と一致し、この 8 個には `IntegrationTests` が**含まれている**必要がある | **51 → 19 が正確な対応。IADR には 51 を実測値として書いた**（50 は「サービスあたり 3〜5」という近似の言い方としてはそのまま成立するため、決定 4 の実測表では両方が分かるように書いた） |

MSP 側の裏付け（`/home/user/microservices-platform`、`origin/develop` = `9ae1136`）も実地に確認した:
波 4.5 の作業仕様書（`status: done`）は「型名文字列を書き換えたファイル数 59」「`MigrationId` は変えない」
「移送後 19 テストプロジェクトすべて緑・件数一致」を実測記録している。ただし**同仕様書の「検証」節には
`dotnet ef migrations add` の空差分確認が含まれていない**（`dotnet build`/`dotnet test`/`dotnet format
--verify-no-changes`/各種検査器/`scripts.test.js` のみ）。これは IADR-0259 の起草時点の「MSP がどう検証
したかは確認できていない」という記述自体が**部分的に誤りだった**ことを意味する——「レシピの実施」は
確認できたが、「migrations add の空差分」という個別の実行記録は無かった。IADR-0259 決定 5②にこの
訂正を明記し、AST 側の受け入れ基準（決定 5③）としてこの空白を自ら埋めた。

`MSP:IADR-0282` 決定 4 が「操作単位のスライス分割は『器の移送まで』の対象外」と定め、波 4.5 の
「残件」節が実装でもこれを実施していないと確認できたため、決定 1 へ `Features/<集約>/` までとする
注記を追加した（(c) 反映）。

### (b) NsDepCop の撤回（起点の利用者裁定 6 を訂正）

IADR-0259 起点・関連の利用者裁定一覧の直後、決定 3、`結果` 節、`.ai-context/adr/README.md` の索引行を
すべて更新した。**裁定そのものは削除・書き換えず、日付つきの訂正注記として残した**（黙って落とさない
という利用者からの明示指示に従う）。実測（本セッションで再確認）:

```
$ grep -ril nsdepcop . --include="*.md"
./.ai-context/adr/IADR-0255_business-metrics-and-dashboards.md   # 散文 1 箇所（本文の一般論）
```

MSP 側は `grep -rn "nsdepcop\|NsDepCop"` で 0 件（設定ファイル・散文とも）。

### (c) `Features/<集約>/<操作>/` の 3 段目は採らない

MSP `FeedbackService/Features/Feedback/FeedbackEndpoints.cs` を実地確認し、集約フォルダ直下に
1 ファイルが置かれているだけで `<操作>/` の下位フォルダは存在しないことを確認した。決定 1 へ反映済み。

### (d) `Tests/` は別 csproj・50→19（51→19 に訂正）

`FeedbackService.csproj` の `<Compile Remove="Tests/**" />` を実地確認。決定 4 の実測表に反映済み
（上表参照）。

### (e) `CLAUDE.md` のラチェット

削った箇所: `## 仕様書` 節の「機能仕様書・テスト仕様書の必須範囲」ブロック（951 バイト）を、
`docs/README.md` が正本である旨のポインタ（369 バイト）へ圧縮した——このブロックは
`docs/README.md` の同一裁定（網羅裁定 #211）を**そのまま複写**しており、CLAUDE.md 自身が
直前の行で「種別の一覧は `docs/README.md` が正本である。ここへ複写しない」と述べているにもかかわらず、
その直後で複写していたという矛盾を解消する副産物も得た。差し引き **582 バイト削減**。

技術スタック節（C# / .NET）への追加は (a)(b)(c) の反映で `+299` バイト前後。

検算:

```
$ wc -c CLAUDE.md
17488 CLAUDE.md
$ git show HEAD:CLAUDE.md | wc -c
17490
```

**17488 ≤ 17490（HEAD）。ラチェット順守（差分 -2 バイト）。**

### (f) IADR-0046 / IADR-0128 の supersede 書式確認

`traceability.repo.md`「Superseded / Deprecated な ADR を引用するときの書式」と突き合わせた。

- **旧 ID を残し、後継を併記**: `.ai-context/adr/README.md` の IADR-0046 行・IADR-0128 行はいずれも
  旧 ID をそのまま残し、`IADR-0259` を注記として併記している。ID の付け替えは無い。
- **注記に起票 ID・日付**: 両ファイルの追記ブロックはいずれも `［2026-08-28 追記 / IADR-0259（利用者
  裁定 2026-08-28）］` を持つ。
- **`updated:` の前進**: IADR-0046 は `2026-07-12` → `2026-08-28`、IADR-0128 は `2026-08-03` → `2026-08-28`
  に前進済み（`git diff` で確認済み）。
- **凍結の射程**: 対象は IADR（`.ai-context/adr/`）であり、`.ai-context/specs/` の凍結記録には触れていない。
- 本セッションで IADR-0046 の追記ブロックを 1 か所追加訂正した——IADR-0259 決定 5 の撤回により
  「決定 3 は IADR-0259 決定 5 と同じ方向であり、覆されていない」という記述が**決定番号の意味変化により
  不正確になる**ため、「その場の構造移動については」という限定を追加し、名前空間の完全整合は
  決定 5③（想定 IADR-0261）という別の後続波であると明記した。これも旧 ID を残した追記であり、
  本文を書き換えたのではない。

### 母集合の引き直し（規則 9・10。本追記分）

**誤りの側の文字列から全走査し直した**（記憶や前セッションの記述を転記しない）。

| 走査 | コマンド | 結果 | 対応 |
| --- | --- | --- | --- |
| NsDepCop の残存 | `grep -rn "nsdepcop\|NsDepCop" --include="*.md" .` | `.ai-context/adr/IADR-0259*`（起点・決定 3・結果。訂正済み）／`.ai-context/adr/README.md`（索引。訂正済み）／`.ai-context/specs/20260828_w9f4_*`（本仕様書自身。凍結記録の追記のみ許容される作業仕様書のため、旧記述はそのまま残し本追記で訂正の経緯を書いた）／`IADR-0255`（無関係の一般論。対象外） | 訂正対象はすべて反映済み |
| `namespace...変えない` の残存 | `grep -rln "namespace.*変えない" --include="*.md" .` | `CLAUDE.md`（訂正済み）／`IADR-0259`／`IADR-0046`／`.ai-context/adr/README.md`（いずれも訂正済み）／`.ai-context/specs/20260803_353_standard-project-layout.md`（IADR-0128 の凍結記録。**対象外**——本文プロズを書き換えない規約、かつ IADR-0128 自体が Superseded 表示済みのため当時の記述として妥当） | `.ai-context/specs/20260803_353_*` は意図的に除外（凍結記録） |
| `IADR-0128` の可視参照 | `grep -rln "IADR-0128" --include="*.md" docs/ .claude/` | `docs/tech/tech-requirements.md`（trace ブロックのみ・本文に無し）／`docs/tests/README.md`（同）／`docs/integration/20260718_msp-frontend-integration-requirements.md` `docs/tests/FR-15_backtest-tests.md` `docs/how-to/local-run.md`（いずれも当初の母集合表で「除外」と判定済みの現行パス言及。**対象外**） | 追加対応不要（trace ブロックは表示テキストでないため規約違反ではない） |
| `#526`/`#527`/`#528` の参照 | `grep -rln "#526\b\|#527\b\|#528\b" --include="*.md" .` | `.ai-context/adr/README.md` / `IADR-0259` のみ | 追加対応不要 |

### docs/ 追随の完了

対象範囲 5（母集合の和集合 19 ファイルのうち「追随する」3 件）を本追記で完了した。

- `docs/tech/tech-requirements.md`: §プロジェクト構成を新（VSA）／旧（4 層）の併記へ書き直した。
  依存規律の記述を「csproj 静的解析（旧）＋ソース走査（新）の二重化・NsDepCop は導入しない」へ更新。
  trace ブロックへ `IADR-0259` と `MSP:IADR-0282` を追加。**この行を書き直す過程で `PlanConformance.Tests`
  の実在しない参照（残余リスク節）にも気付いたため、この 1 行に限って実測（`backend/Tests/` の実際の
  2 件）へ合わせた**——`docs/tests/README.md` 側の同参照は方針どおり触れていない。
- `docs/tests/README.md`: §5 の表へ「新」列を追加し、旧構成の列と併記した。**`PlanConformance.Tests` の
  既存記述（69・73・109 行目）は文字どおり変更していない**（残余リスク節の既定どおり）。trace ブロックへ
  `IADR-0259` を追加。
- 両ファイルとも `updated:` を `2026-08-28` へ前進。
- 除外（`追随しない`）の判定は前回セッションの表をそのまま踏襲し、再確認はしていない
  （対象ファイル自体を今回触っていないため、母集合の再走査は不要と判断した）。
