---
title: IADR-0259 AST バックエンドも単一プロジェクト＋VSA/DDD フォルダ構成へ移行する（MSP:IADR-0282 と同一樹形）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0001
  - IADR-0046
  - IADR-0128
  - IADR-0256
  - IADR-0257
  - ADR-0001
  - MSP:IADR-0282
  - MSP:ADR-0019
  - MSP:ADR-0030
author: endazon (with Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md
---

# IADR-0259: AST バックエンドも単一プロジェクト＋VSA/DDD フォルダ構成へ移行する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: endazon（方針・2026-08-28 裁定）/ Claude Code（設計詳細の起案）

## 起点・関連

- **起点 ID: `NFR`（無採番）。** 本件は規約整備・文書統制＝メタ作業であり、
  `.claude/rules/traceability.md`「起点 ID の種別」の無採番許容ケース **2**（ID 列はあるが当たる番号が無い）に
  当たる。計画の非機能要件表（`NFR-01`〜`NFR-17`）を全行読んで確認した（判断の全表は作業仕様書）。
  **NFR-16（拡張性）が最も近いが当たらない** —— 本件はソースツリーの割り方であって、外部依存の
  差し替え可能性ではない。**環流はしない**（ケース 2）。
- **利用者裁定 2026-08-28。** AST バックエンドを単一プロジェクト＋VSA（1 サービス = 1 プロジェクト）へ
  全面移行する。裁定の内訳:
  1. **機能ウェーブを完走してから、単独の波として実施する。**
  2. **AST に適用する。**
  3. **`AiStockTrading.Shared.Contracts` は別プロジェクトとして維持する。**
  4. **`Common/Result.cs` は作らない。**
  5. **`BackgroundService` はルート直下 `Hosted/` に置く。**
  6. **依存規則の強制は NsDepCop と自前検査器の併用とする。**

  > 🔴 **裁定 6 の訂正（2026-08-28・同日中。MSP 整合の確認による）。** 裁定 6 は「MSP と作りを
  > 合わせる」という本 ADR の骨子（次項）と食い違うため、**NsDepCop は導入せず、自前検査器
  > （[IADR-0256](IADR-0256_domain-dependency-inspection-by-source-scan.md) のソース走査）のみで
  > 強制する**へ訂正する。実測: NsDepCop は本リポにも MSP にも未導入（本リポの出現は
  > [IADR-0255](IADR-0255_business-metrics-and-dashboards.md) の散文 1 箇所のみ・MSP は 0 件）で、
  > `MSP:IADR-0282` 決定 2 も「`check-unit-dependencies.js` の規則 3 を名前空間走査版へ書き換える」
  > とだけ定め、NsDepCop の採用は無い。**裁定そのものを黙って落とさず、ここに訂正として記録する**
  > （反映は決定 3）。
- 🔴 **同日、基盤（microservices-platform・MSP）でも同じ裁定が出ている。** これは本 ADR の骨子を決める事実である。
  `MSP:IADR-0282`「サービスは単一プロジェクト＋VSA/DDD フォルダ構成とする（8 要素プロジェクト実体化の撤回）」が
  `Accepted` で存在し、`MSP:IADR-0280`（8 要素標準の実体化）の決定 1〜5・7 を supersede している
  （決定 6＝DDD 基底型は存続）。**したがって本移行は基盤からの逸脱ではなく、基盤との整合である。**
- 関連 IADR: [IADR-0001](IADR-0001_repo-structure-and-stack.md)（リポ構成と技術スタックは基盤実装リポに揃える）・
  [IADR-0046](IADR-0046_unit-repo-layout.md)（ユニットリポジトリレイアウト）・
  [IADR-0128](IADR-0128_standard-project-layout.md)（標準プロジェクト構成＝ 4 層）・
  [IADR-0256](IADR-0256_domain-dependency-inspection-by-source-scan.md)（Domain 依存規律のソース走査・土台 1）・
  [IADR-0257](IADR-0257_ci-test-sharding-lpt-by-scan.md)（CI シャードの LPT 分配・土台 2）
- 関連する実装仕様書: [20260828_w9f4_vsa-migration-policy-and-docs](../specs/20260828_w9f4_vsa-migration-policy-and-docs.md)
- 関連 issue: [#526](https://github.com/endazon/ai-stock-trading/issues/526)（`*.Client` 廃止）・
  [#527](https://github.com/endazon/ai-stock-trading/issues/527)（Tests 1 プロジェクト化）・
  [#528](https://github.com/endazon/ai-stock-trading/issues/528)（8 要素の空枠 ＋ `.gitkeep`）・
  MSP#1021（`MSP:IADR-0282` を含む PR）・planning#490（2026-08-27 の 8 要素実体化の裁定。前提が変わった）

## コンテキストと課題

現行の AST バックエンドは [IADR-0128](IADR-0128_standard-project-layout.md) が定めた
`<Svc>.{Api,Application,Domain,Infrastructure}` の 4〜5 プロジェクト構成である
（`backend/backend.slnx` の `<Project Path=` が **102 件**・実測 2026-08-28）。

**着手時に MSP を実地に確認したことで、当初の前提が誤りだったことが分かった。** 当初は
「計画 `12_backend-application-stack.md` の 8 要素標準を基盤は採り、AST だけがそこから外れる」と
理解していたが、**MSP も同じ日に 8 要素の物理実体化を撤回している**。読み取り専用で確認した実測は次のとおり。

| 確認したこと | 実測（`/home/user/microservices-platform`・`origin/develop` = `9ae1136`） |
| --- | --- |
| `MSP:IADR-0282` | `status: Accepted`。`MSP:IADR-0280` 決定 1〜5・7 を supersede |
| 移送の実体 | `src/knowledge/backend/Services/FeedbackService/` に `FeedbackService.csproj` / `Program.cs` / `Features/Feedback/` / `Domain/` / `Infrastructure/Persistence/{Migrations}` / `Tests/FeedbackService.Tests.csproj` が実在 |
| 依存方向の検査 | `scripts/check-unit-dependencies.js` が **規則 3-③（名前空間走査版）を実装済み**で、規則 3-①②（csproj 版）と**新旧併走**している |
| 名前空間 | `FeedbackService.{Domain,Features,Infrastructure,Tests}`（`.Api` を落としたルート名前空間）。`ModelSnapshot` の `modelBuilder.Entity("FeedbackService.Domain.AnswerFeedback"` と一致 |

したがって本 ADR が答えるべき問いは、当初想定した「逸脱の射程」ではなく次の 3 つである。

1. **MSP の樹形（`MSP:IADR-0282` 決定 1）を AST でどう引くか**（別案を作らない）。
2. **4 層をやめたときに失われる保証（Domain の依存規律・テストの層分け）をどう保つか。**
3. **AST 固有の制約（EF Core の `ModelSnapshot`・34 本の migration）をどう扱うか。**

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A: MSP の樹形をそのまま引く（採用）** | `MSP:IADR-0282` 決定 1 の樹形を AST の名前へ写す。AST 固有の追加は `Hosted/` のみ | ○ **`IADR-0001`（規約は基盤実装リポに揃える）を満たす** ○ 2 リポの規約が 1 つで済み、AST が MSP へ submodule 配置されたときに構成が並ばない ○ 検査器の作り（名前空間走査）も同じ形に寄せられる |
| B: AST 独自の VSA 樹形を設計する | 実測に合わせて階層名を選ぶ | × 揃える先があるのに独自案を作る理由が無い。**同じ問いに 2 つの答えが並ぶ**（AST が MSP へ組み込まれる前提と噛み合わない） |
| C: 4 層を維持する（移行しない） | 現状維持 | × 利用者裁定に反する。かつ MSP が移行するので、**揃える先の側が変わる** |

## 決定

### 決定 1 — 標準樹形は `MSP:IADR-0282` 決定 1 をそのまま引く

```
backend/Services/<Name>/
├── <Name>.csproj                  # 単一プロジェクト（.Api 接尾辞を廃止）
├── Program.cs                     # 合成ルート
├── appsettings*.json
├── Features/<集約>/<操作>/        # Vertical Slice: Endpoint.cs / Command.cs|Query.cs / Handler.cs
├── Domain/                        # エンティティ・値オブジェクト・ドメインイベント
├── Infrastructure/
│   ├── Persistence/               # DbContext・Migrations/
│   ├── Authentication/
│   ├── Messaging/                 # Wolverine のハンドラ・発行/購読アダプタ
│   └── ExternalServices/          # HTTP クライアント等の外部接続アダプタ
├── Hosted/                        # 🔴 AST 固有: BackgroundService（ルート直下。利用者裁定）
├── Common/                        # サービス固有の横断関心（Exceptions/・Behaviors/）
└── Tests/
    └── <Name>.Tests.csproj        # テストは独立プロジェクト（同一 csproj には入れられない）
```

- `<Name>` は現行のサービスフォルダ名（`AuditService` 等）であり、`AuditService.Api` は `AuditService` になる。
- **HTTP 面を 1 本も持たないサービスでは `Endpoint.cs` が存在しない**（実測で 4 サービスが該当）。
  樹形は典型例であり、全ファイル必須の宣言ではないと読む。
- **`Foundation/` / `Composable/` の区分は廃止し、`Features/` ほかへ吸収する**（`MSP:IADR-0282` 決定 1 と同じ）。
  AST には両区分が実在する（実測 **25 ディレクトリ**）。区分の意図だった「合成点の明示」は
  `Program.cs` と `Infrastructure/` が担う。
- 🔴 **`Features/<集約>/<操作>/` の 3 段目（操作単位のスライス分割）は採らない。** `MSP:IADR-0282` 決定 1 の
  樹形図はテンプレートとして `<操作>/` の 3 段目を示すが、**MSP 自身の移送実装（波 4.5・
  `/home/user/microservices-platform` の `.ai-context/specs/20260828_wave45-vsa-migration.md`、
  `status: done`）は集約フォルダ直下で止めている**（同仕様書「残件（本波の射程外）」:
  「操作単位のスライス分割（`Features/<集約>/<操作>/{Endpoint,Command,Handler}` の 3 分割）はしていない。
  `IADR-0282` 決定 4 が『器の移送まで』と定めており、端点は集約フォルダ直下に 1 枚のまま」）。
  **AST もテンプレートの文字面ではなく MSP の実装の深さに揃え**、`Features/<集約>/` までとする
  （深さを揃えることが整合である）。

### 決定 2 — 共有プロジェクトは維持し、`Shared.Kernel` を新設する

- **維持**: `AiStockTrading.Shared.Contracts`（利用者裁定 3。サービス間契約はユニットの Shared に置く＝
  `MSP:IADR-0282` 決定 1 と同じ立場）・`Shared.Infrastructure` / `Shared.KnowledgeBase` /
  `Bff.Endpoints` / `TestSupport.{PlatformShim,Messaging,ContractFixtures}` /
  `Tests/{Architecture.Tests,IntegrationTests}`（横断テストは統合しない）。
- **新設**: `AiStockTrading.Shared.Kernel`（土台 5）。サービス境界をまたいで消費される型の置き場とし、
  現在サービス跨ぎの `ProjectReference` になっている参照を集約する。
- **廃止**: `ConfigurationService.Client`（#526）。1 サービス = 1 プロジェクトでは残す置き場が無い。
- **`Common/Result.cs` は作らない**（利用者裁定 4）。MSP は `Platform.Shared.Kernel` を使い続けるという
  理由で同じ結論に達しているが、**AST の理由は違う** —— AST は planning にも MSP にも依存しないため
  `Platform.Shared.Kernel` を引かない。**AST が Result を採るときの置き場は `AiStockTrading.Shared.Kernel`** とし、
  MSP と同じ形（サービス個別ではなくユニット単位の Shared に置く）へ寄せる。
  なお現行 AST に Result 型は 1 つも無く、エラー伝搬は例外ベースである。**本波では導入しない。**

### 決定 3 — 参照方向はフォルダ（名前空間）で規範化し、自前検査器（IADR-0256）のみで強制する（NsDepCop は導入しない）

- 規律は `MSP:IADR-0282` 決定 2 と同じにする: `Domain` は `Features` / `Infrastructure` /
  `Common.Behaviors` を using しない。`Features` は `Domain` / `Infrastructure` / `Common` を使ってよい。
  `Infrastructure` → `Features` は禁止。**Domain の外部ライブラリ不使用（ADR-0030 §基本方針）は不変**である。
- **自前検査器**は [IADR-0256](IADR-0256_domain-dependency-inspection-by-source-scan.md) が定めた
  ソース走査（`Domain/` 配下の `using` 許可リスト＋ CPM 由来のパッケージ名走査）を存続させ、
  移行中は csproj 方式と並置する（MSP の `check-unit-dependencies.js` が規則 3-①②と 3-③を併走させているのと同型）。
- 🔴 **NsDepCop は導入しない（起点・関連の裁定 6 を訂正）。** 起草時点の利用者裁定 6 は「NsDepCop と
  自前検査器の併用」だったが、着手中に MSP を実地確認した結果と食い違うため訂正する。
  実測: **NsDepCop は本リポにも MSP にも未導入**（本リポの出現は
  [IADR-0255](IADR-0255_business-metrics-and-dashboards.md) の散文 1 箇所のみ・MSP は 0 件）。
  MSP 自身は `MSP:IADR-0282` 決定 2 で「`check-unit-dependencies.js` の規則 3 を名前空間走査版へ
  書き換える」とだけ定めており、**NsDepCop の採用は無い**。「MSP と作りを合わせる」（本 ADR の骨子）
  と「NsDepCop を足す」は両立しないため、**自前検査器（ソース走査）のみで強制する**。
  コンパイル時の宣言的強制という NsDepCop の利点は失うが、**MSP も同じ弱さを受容している**ため
  AST だけが追加の強度を持つ理由が無い。将来この強度が要ると判断されたら、「検査器・規約の追加は
  同型事故 2 回から」の条件で個別に起票する。

### 決定 4 — Tests はサービスごと 1 プロジェクトとし、`<Name>/Tests/` に置く

- #527 のとおりサービス単位のテストを 1 プロジェクトへ統合し、`Features/` `Domain/` のフォルダで分ける。
  MSP の `Services/FeedbackService/Tests/FeedbackService.Tests.csproj`（実在確認済み）と同形。
- 本番 csproj は `<Compile Remove="Tests/**" />`（`Content` / `None` も同様）を持ち、
  **推移閉包にテスト系パッケージが現れないこと**を機械検査する（本番コンテナイメージに xunit を
  入れない）。MSP の `FeedbackService.csproj` も同じ 3 種の `Remove` を持つ（実在確認済み。
  コメント「入れ子のままだと二重コンパイルで CS0246 が出る」）。
- **横断テスト**（`Architecture.Tests` / `IntegrationTests` / `Shared.*.Tests` / `TestSupport.*.Tests`）は統合しない。
- 実測（AST・2026-08-28）: 現行のテスト csproj は **51 個**
  （サービス個別 `<Svc>.{Api,Application,Domain,Infrastructure}.Tests` が **43 個**〔サービスあたり 3〜5〕＋
  横断 `Architecture.Tests` / `IntegrationTests` / `Shared.{Contracts,Infrastructure,KnowledgeBase}.Tests` /
  `TestSupport.{Messaging,PlatformShim}.Tests` / `Bff.Endpoints.Tests` が **8 個**）。
  移行後はサービス個別の 43 個が 11（サービス数）へ統合され、横断 8 個と合わせて**合計 19 個**になる
  （#527 はこの帰結に吸収される）。

### 決定 5 — 🔴 撤回・確定: 名前空間も MSP へ完全整合する。ただし移送波そのものでは変えない

> **本決定は [作業仕様書](../specs/20260828_w9f4_vsa-migration-policy-and-docs.md) 起草時点の
> 「AST は移送波では `namespace` を 1 行も変えない」という決定（旧文面は下記「撤回前の記録」）を
> 撤回する。撤回するのは「変えない」という言い切りであり、「フォルダ構造の再編そのものでは
> 変えない」という原則（下記②）は維持したまま存続する。**

**① 撤回前の記録（当時の判断。書き換えず残す）**: 起草時点は、EF Core の `ModelSnapshot` が
エンティティ型を完全修飾名の文字列で持つこと（実測:
`modelBuilder.Entity("AiStockTrading.MarketMonitor.Infrastructure.Foundation.Persistence.CooldownRow"`）を
根拠に、「移送波では `namespace` を 1 行も変えない」とし、「MSP がこの整合をどう検証したかは
本リポからは確認できていない」としていた。

**② 撤回する根拠: MSP が同じ問題を既に解いている。** `/home/user/microservices-platform`
（`origin/develop` = `9ae1136`。`git rev-parse --is-shallow-repository` は `true` のため
`git log`/`git blame` は出典に使わず、作業ツリーの現物のみを確認した）の波 4.5（アーキ移送波。
`.ai-context/specs/20260828_wave45-vsa-migration.md`、`status: done`）が、**14 サービス全数の移送で
EF Migrations の CLR 型名文字列を機械的に書き換える**という同じ問題を既に解いている。実測（同仕様書
「🔴 罠」4・「実測」表・「検証」節）:

| 確認したこと | 実測 |
| --- | --- |
| 移送したサービス数 | 14（knowledge 10 / platform 4） |
| 型名文字列を書き換えたファイル数 | **59**（`Designer.cs` / `ModelSnapshot.cs` の `modelBuilder.Entity("<Svc>.Domain.X")` 文字列。「差分は引用符の中だけ」） |
| `MigrationId`（クラス名・`[Migration("...")]` 属性） | **変えていない**（`__EFMigrationsHistory` は `MigrationId` と `ProductVersion` で突合するため、名前空間の変更に耐える） |
| 移送後のテスト結果 | 19 テストプロジェクトすべて緑・**件数は移送前と全一致**（knowledge 1057 / platform 987） |

🔴 **前回の「MSP がどう検証したかは確認できていない」は誤りだった訂正**: 波 4.5 の作業仕様書は
本リポから読み取り専用で確認できる。ただし確認できたのは**型名書き換えの実施と `MigrationId` 不変
という設計**までである。同仕様書の「検証」節（`dotnet build` / `dotnet test` / `dotnet format
--verify-no-changes` / 各種検査器 / `scripts.test.js`）には **`dotnet ef migrations add` の
空差分確認は含まれていない**。MSP は「レシピの実施」を実測記録しているが、
「`migrations add` が実際に空を返した」という明示的な実行記録は**見つからない**。AST はこの空白を
決定 5 の合否判定（③）で自ら埋める。

AST 側の対象規模も実測した（2026-08-28）:

| 対象 | 実測 |
| --- | ---: |
| EF エンティティ（`*ModelSnapshot.cs` の `modelBuilder.Entity("..."` 出現数） | **35 / 35** がすべて `AiStockTrading.<Short>.Infrastructure.Foundation.Persistence.*` 配下 |
| `*ModelSnapshot.cs` | **7 個**（サービス数と一致） |
| 対象 migration（`Migrations/` 配下、`ModelSnapshot` / `.Designer.cs` を除く） | **35 個**（RiskManagementService が 20） |
| `namespace` 宣言（`backend/**/*.cs`） | **1,289 箇所**（`grep -rhoE '^\s*namespace\s+[A-Za-z0-9_.]+' backend --include="*.cs" \| wc -l`。file-scoped 1,210 ＋ block-scoped 79） |
| csproj（`backend/Services/*/src/**/*.csproj`） | **43 個すべてが `RootNamespace` を明示**（＝フォルダと名前空間は既に切り離せるが、今回は切り離さず揃える） |

**③ 決定**:
1. **AST も同じレシピを踏む。** 移送時に `Designer.cs` / `ModelSnapshot.cs` の CLR 型名文字列
   （`AiStockTrading.<Short>.Infrastructure.Foundation.Persistence.*` を含む完全修飾名）を新しい
   名前空間へ機械的に書き換える。`MigrationId`（クラス名・`[Migration("...")]` 属性）は**変えない**
   （`__EFMigrationsHistory` は `MigrationId` で突合するため整合が壊れない）。
2. **目標の名前空間はルート `<Svc>Service` へ揃える**（`MSP:IADR-0282` 決定 3「ルート名前空間は
   `<Name>`」と同じ規則）: `AiStockTrading.<Short>.<Layer>.*` → `<Svc>Service.*`。`.Api` 接尾辞の廃止
   （決定 1）と対にする。
3. **合否判定（受け入れ基準）**: 変換後、各 DbContext で `dotnet ef migrations add __VerifyNoDrift` を
   実行し、生成される `Up`/`Down` が**空**であること。空でなければ移送に誤りがある。
   （②で述べたとおり MSP 自身の検証記録にはこの確認が無いため、AST は自リポの受け入れ基準として
   明示する。）
4. **実施は本 ADR の PR では行わない。** 別 PR（移送波の中の専用の段・想定 `IADR-0261`）で実施する。
   **本 ADR は決定だけを記録し、実施はしない**（`backend/` を 1 行も変えないという本 PR の対象範囲は
   [作業仕様書](../specs/20260828_w9f4_vsa-migration-policy-and-docs.md) のとおり）。

**④ 撤回しない部分（構造再編そのものでは変えない）**: 決定 6 が定める「移送波（`Features/` などへの
構造再編）の完了までは、新規コードも現行配置で書く」原則は不変である。**構造の移動と名前空間の
改名は別の波に分ける**——③の名前空間整合は構造再編（決定 1〜4）が完了し安定した後の、独立した
専用の波として実施する。この意味で [IADR-0046](IADR-0046_unit-repo-layout.md) 決定 3
（`{src,tests}` の 2 段を集約する再編そのものでは名前空間・アセンブリ名を変えない）とは矛盾しない
——決定 3 が扱うのはその場の構造移動であり、③はそれとは別の後続の波である。

**⑤ 帰結**: フォルダ `Foundation/` を廃止しても、③の波が実施されるまでは名前空間の
`...Infrastructure.Foundation....` が残る（**意図した期間限定の混在**）。③の完了後は AST も MSP と
名前空間の見え方が揃う。

### 決定 6 — 移送波の完了までは、新規コードも現行配置で書く

`MSP:IADR-0282` 決定 4-3 の教訓をそのまま引く。**「新規は新様式」を先行させると同一サービス内に
二重構造が生じ、移送の照合（テスト件数・ファイル数の完全一致）が壊れる。** 移送は専用の波が
サービス単位で一括変換する。**移行中は新旧のサービスが混在する**ため、検査器は両対応にする
（土台 1・2・3 で実施済み／実施中）。

### 決定 7 — 本波では振る舞いを変えない

Result 型の導入・`IMessageBus.InvokeAsync` によるローカルディスパッチ化・gRPC 生成クライアントへの移行・
FluentValidation / Riok.Mapperly / ProblemDetails の適用は**いずれも行わない**。すべて後続 issue へ分割する
（[IADR-0128](IADR-0128_standard-project-layout.md) 決定 7 と同じ論法。移送と混ぜると
「テストが落ちたのは移動のせいか書き換えのせいか」が切り分けられなくなる）。

### 決定 8 — 既存 IADR の扱い

- **[IADR-0128](IADR-0128_standard-project-layout.md) を Superseded する。** 決定 1〜6 はいずれも
  「層をプロジェクト境界で表す」ことを前提としており、1 プロジェクト化で前提ごと成立しない。
  決定 5（`ConfigurationService.Client` を第 8 のプロジェクトとして残す）は結論を逆転させる。
  一部だけ差し替える「改定」にすると、どの決定が生きているかを読み手が判別できない。
- **[IADR-0046](IADR-0046_unit-repo-layout.md) は改定（部分）に留める。** 失効するのは決定 1 のうち
  `{src,tests}` の 2 段だけである。決定 2（props の import-chain フォールバック）・決定 3（その場の
  構造再編では名前空間を変えない。決定 5④参照）・決定 4（CI のパスは `backend/backend.slnx` 系）と、
  `backend/` 直下の `Services/` `Shared/` `TestSupport/` は存続する。
- **[IADR-0001](IADR-0001_repo-structure-and-stack.md) は改定しない。満たされる。** 揃える先である MSP が
  同じ樹形へ移行しているため、本移行は IADR-0001 の**遵守**である。ただし同 ADR は「揃える」の射程を
  書いていないため、**射程はプロジェクト割りを含む**ことを本 ADR で明示する
  （net10.0 / C# 13 / CPM / slnx / xUnit v3 はいずれも存続）。🔴 **名前空間プレフィックス
  `AiStockTrading` は決定 5③（想定 `IADR-0261`）の完了までの暫定であり、恒久の合意ではない**
  ——MSP 自身のルート名前空間（例 `FeedbackService.Domain`）には会社/リポジトリ名の接頭辞が無く、
  決定 5②はこれに揃える。CLAUDE.md の命名規約はこの前提で読む。

### 決定 9 — 既存 issue の帰結

| issue | 帰結 |
| --- | --- |
| #526（`*.Client` 廃止） | **移行に吸収する。** `ConfigurationService.Client` の中身を呼び出し元 2 サービスの `Infrastructure/ExternalServices/` へ移す。**gRPC 化は行わない**（トランスポートの変更＝振る舞いの変更）。gRPC 化のみ別 issue へ切り出す |
| #527（Tests 1 プロジェクト化） | **移行に吸収する**（決定 4） |
| #528（8 要素の空枠 ＋ `.gitkeep`） | 🔴 **無効化される。** 8 要素を**物理プロジェクトとして持たない**ため、「実体が無い要素の枠を可視化する」規則の適用対象にならない。`backend/` に `.gitkeep` の枠は置かない。**MSP も同じ立場である**（`MSP:IADR-0282` は `MSP:IADR-0218` の「`.gitkeep` の枠」を全廃した）。クローズ理由は `not planned`（2026-08-28 の裁定で方針が変わった）とし、本 ADR へリンクする |

### 決定 10 — 計画への環流は MSP の起票に合流する。AST から重複起票しない

計画 `12_backend-application-stack.md`（fixed）の 8 要素**プロジェクト**標準とは構成単位が異なる
（8 つの**関心**は保つが、物理プロジェクトにしない）。**この差は MSP も同じ立場であり、
`MSP:IADR-0282` 決定 5 が「planning へ環流 issue を起票し、計画側の改定を依頼する」と定めている。**

- 🔴 **AST から重複起票しない。** MSP の環流に合流し、必要なら**その issue へ AST の事実（本 ADR・
  #528 の無効化）をコメントで補足する**。
- 起票が要ると判断する場合も、**着手前に planning の既存 issue を必ず検索する**（重複起票の防止。
  検索語の候補: `Vertical Slice` / `8 要素` / `12_backend-application-stack` / `planning#490`）。
- planning#490（2026-08-27 の実体化の裁定）は本裁定により**前提が変わっている**。MSP 側の環流が
  その旨を扱う。

## 理由

- **`IADR-0001` を満たす形が変わったのではなく、揃える先が動いた。** 本 ADR の骨子を
  「基盤標準からの逸脱」と書きかけたが、MSP を実地に読んで**誤りと分かった**。
  **記憶や設計書の前提ではなく、揃える先の現物を見て決める**のが本 ADR の一次的な作法である。
- **決定 3 が無ければ、本移行は統制の後退である。** Domain 層の外部依存ゼロは
  platform ADR-0030 §基本方針が定めた確定制約であり、**プロジェクト境界が消えても制約は消えない**。
  「実装していない」と「実装してはならない」は別である。
- **決定 5 が最も重い。** 移行の失敗形のうち、唯一**本番データを失わせ得る**のがここである。
  `__EFMigrationsHistory` は `MigrationId` と `ProductVersion` しか持たないため名前空間の変更に耐えるが、
  `ModelSnapshot` は FQN 文字列で型を指すため耐えない。**この非対称性は文書化しないと必ず忘れられる。**
- **決定 6 は「照合可能性」のための規律である。** 移送の受け入れ条件はテスト件数と合格数の完全一致であり、
  移送中に新様式のコードが増えると、その一致が「移送が正しかった」ことの証拠にならなくなる。

## 結果

- 良い影響:
  - **AST と MSP の構成規約が 1 つになる。** AST が MSP へ submodule 配置されたときに 2 つの樹形が並ばない。
  - プロジェクト数が大きく減り（現行 102）、restore / build / CI の時間が下がる。
  - `InternalsVisibleTo`（実測 47 件）の多くが不要になり、層をまたぐために開けていた公開面が実際に閉じる。
  - 1 つの操作に関わるコードが 1 フォルダに集まり、変更の影響範囲がフォルダ境界と一致する。
- 悪い影響 / トレードオフ:
  - **層の依存規律がプロジェクト境界で守られなくなる。** 決定 3 の検査（自前のソース走査。
    NsDepCop は導入しないと訂正した）に全面的に依存する。
  - 自前のソース走査は**コンパイラより弱い**（`global using` やソースジェネレータの生成物が見えない）。
    **NsDepCop を導入しないと訂正した**ため、この弱さは配備で埋めない——**MSP も同じ弱さを
    受容している**ことを根拠に据え置く（決定 3）。
  - 🔴 **名前空間がフォルダ構造と対応しない状態が、決定 5 の③（専用の波）が完了するまで残る**
    （`...Infrastructure.Foundation....`）。MSP は改名済みであるため、それまでの間は
    **この 1 点だけ 2 リポの見え方が揃わない**（期間限定。決定 5 参照）。
  - 計画 `12_backend-application-stack.md` の 8 要素**プロジェクト**標準との差は、計画側が改定されるまで残る。
- フォローアップ:
  1. **名前空間の完全整合を独立した波として実施する**（決定 5③。想定 `IADR-0261`。
     `dotnet ef migrations add __VerifyNoDrift` の空差分確認を受け入れ基準とする）。
  2. Result 型の導入（`AiStockTrading.Shared.Kernel` へ）を独立 issue で起票する。
  3. ローカルディスパッチの Wolverine `InvokeAsync` 化を独立 issue で起票する。
  4. gRPC 生成クライアントへの移行（#526 の残余）を独立 issue で起票する。
  5. `TestSupport.PlatformShim` の誤称（本番 Api が参照している）の改名を独立 issue で起票する。
  6. #528 を `not planned` でクローズする（決定 9）。

## 関連

- Supersedes: [IADR-0128](IADR-0128_standard-project-layout.md)（標準プロジェクト構成＝ 4 層）
- 改定: [IADR-0046](IADR-0046_unit-repo-layout.md)（決定 1 の `src/` `tests/` 2 段のみ。
  slnx 頂点・props の import-chain・CI のパスは存続。決定 3〔再編そのものでは名前空間・
  アセンブリ名を変えない〕も**その場の構造移動については**存続し、決定 5④参照）
- 遵守（改定しない）: [IADR-0001](IADR-0001_repo-structure-and-stack.md)（揃える先である MSP が
  同じ樹形へ移行するため、本移行は同 ADR の遵守である）
- 存続: [IADR-0256](IADR-0256_domain-dependency-inspection-by-source-scan.md)（自前検査器）・
  [IADR-0257](IADR-0257_ci-test-sharding-lpt-by-scan.md)（CI シャード）
- 基盤側: `MSP:IADR-0282`（同一樹形の裁定）・`MSP:IADR-0280`（同 ADR が supersede した 8 要素実体化）
- Superseded by: なし
