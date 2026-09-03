---
title: IADR-0289 Features/<集約>/<操作>/ 3 段化の移送規則を確定し、Tests は本体の鏡写しにする
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0259
  - IADR-0276
  - MSP:ADR-0065
  - MSP:ADR-0068
author: endazon (with Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md
---

# IADR-0289: Features/<集約>/<操作>/ 3 段化の移送規則を確定し、Tests は本体の鏡写しにする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-09-03
- 決定者: endazon（方針）/ Claude Code（実測・起案）

## 起点・関連

- **起点 ID: `NFR`（無採番）。** ソースツリーの割り方＝規約整備のメタ作業であり、
  `.claude/rules/traceability.md` 無採番許容ケース 2 に当たる（[IADR-0259](IADR-0259_single-project-vsa-structure.md)・
  [IADR-0276](IADR-0276_claude-md-vsa-correction-and-hosted-placement.md) と同じ判断）。
- 関連する計画書 ID: platform `ADR-0065` 決定 1〜3・決定 6・決定 7／platform `ADR-0068` 決定 1〜5
- 関連する実装仕様書: [20260903_613_vsa-three-tier-risk-management](../specs/20260903_613_vsa-three-tier-risk-management.md)
- 関連 issue: [#613](https://github.com/endazon/ai-stock-trading/issues/613)（本 IADR は第 1 弾＝規則の確定と
  `RiskManagementService` の移送。残る 10 サービスは後続 PR）
- 関連 IADR: [IADR-0259](IADR-0259_single-project-vsa-structure.md)（単一プロジェクト＋VSA への移行）・
  [IADR-0276](IADR-0276_claude-md-vsa-correction-and-hosted-placement.md)（`Hosted/` を第 4 の頂点として現状維持）・
  [IADR-0261](IADR-0261_namespace-alignment-to-platform.md)（名前空間の整合）・
  [IADR-0256](IADR-0256_domain-dependency-inspection-by-source-scan.md)（Domain 依存規律のソース走査）

## コンテキストと課題

platform `ADR-0065` 決定 2 は `Features/<集約>/<操作>/` の 3 段を規範とし、決定 3 は `Tests/` を本体の
鏡写しにすることを求める。`ADR-0068` は「3 段目へ下ろすのは操作の処理であり、登録表は 2 段目に残す」
「ファイルの行き先は 1 つの操作にしか使われないかだけで決める」を定めた。

**AST は 11 サービスすべてが 2 段どまりである**（実測 2026-09-02: 集約ディレクトリ 11・操作ディレクトリ 0・
`Features/` の `.cs` 199 件）。`Tests/` も 418 件がほぼフラットである。

規則を当てはめるにあたり、AST 固有の 3 つの論点が実測で浮かんだ。

1. **`ADR-0068` の「操作」は登録表に登録された端点として書かれている**が、AST の 11 サービスのうち
   **HTTP 端点を持つのは 6 サービスだけ**である（Audit / Configuration / CostControl / MarketMonitor /
   Report / RiskManagement。残る 5 サービスは Wolverine 購読と `BackgroundService` だけで駆動される）。
2. **`Features/` のファイルの多くが `Infrastructure/`（`Persistence` / `Steps`＝メッセージハンドラ /
   `ExternalServices`）と `Hosted/` から使われている。** `ADR-0068` 決定 2 を字義どおり当てると、
   これらは「1 つの操作にしか使われない」を満たさない。
3. **`Tests/` の鏡写しの粒度**。`ADR-0065` 決定 3 は `Tests/Features/` ／ `Tests/Domain/` の 2 つしか
   例示していないが、AST の実テストは `Infrastructure/`（EF ストア・ハンドラ）と `Hosted/`
   （`BackgroundService`）にも多数ある。

## 検討した選択肢

### 論点 1: 操作の定義

1. **登録表の 1 端点を 1 操作とする（採用）** —— `ADR-0068` 決定 1・3 の字義。登録表を 2 段目に残し、
   ラムダ本体・その操作専用の要求レコード・私的ヘルパを `<操作>/Endpoint.cs` へ切り出す。
   基盤の着地形（`Features/Notifications/{NotificationEndpoints.cs, ListNotifications/Endpoint.cs,
   MarkRead/Endpoint.cs}`）と同型である。
2. **イベント購読・`BackgroundService` の 1 巡回も操作として 3 段目を作る** —— 却下（本 PR では採らない）。
   ハンドラは `Infrastructure/`（`ADR-0065` 決定 1 の `Messaging`）、`BackgroundService` は `Hosted/`
   （[IADR-0276](IADR-0276_claude-md-vsa-correction-and-hosted-placement.md) 決定 2）に置くことが既に
   決まっており、それらを `Features/` へ引き上げるのは**純粋な移送ではない**（置き場の決定の覆し）。
   HTTP を持たない 5 サービスに操作フォルダが生まれない点は §結果 に残余として書く。
3. **集約（2 段目）を切り直してから 3 段化する** —— 却下。AST は 1 サービス 1 集約（11 サービスで 11 集約）
   であり基盤（14 サービスで 27 集約）より粗いが、切り直しは「ビジネス能力の単位」の再判断であって
   3 段化とは別の作業である。混ぜると差分が読めなくなる。

### 論点 2: `Infrastructure/` ／ `Hosted/` から使われるファイル

1. **2 段目に残す（採用）** —— `ADR-0068` 決定 2 の対偶。呼び出し元が `Features/` の操作でない以上、
   「1 操作専属」は成り立たない。**判定は機械的に追える**（参照元のパスを見るだけ）。
2. **呼び出し元の `Hosted/`／`Steps/` を仮想的な操作とみなして下ろす** —— 却下。論点 1 の案 2 と同じ理由。
   加えて、`Infrastructure` が `Features/<集約>/<操作>/` を参照する形になり、参照方向の規律
   （`ADR-0065` 決定 7）を今より読みにくくする。

### 論点 3: `Tests/` の鏡写しの粒度と名前空間

1. **本体の樹形をそのまま写す（採用）** —— `Tests/Features/<集約>/<操作>/` ／ `Tests/Features/<集約>/` ／
   `Tests/Domain/` ／ `Tests/Infrastructure/<区分>/` ／ `Tests/Hosted/`。`Program.cs` に対応する
   配線テストとテスト土台（フィクスチャ）は `Tests/` 直下に残す。**プロジェクトは 1 本のまま**
   （`ADR-0065` 決定 3 が維持）。
2. **`Tests/Features/` と `Tests/Domain/` の 2 つだけを作り、Infrastructure/Hosted のテストは根に置く**
   —— 却下。「スライスを読む人が対応するテストを同じ経路で辿れる」という決定 3 の目的が半分しか満たされない。
3. **テストの名前空間も階層化する** —— 却下。テストの名前空間を折ると、移した全ファイルが共有土台
   （`RiskManagementService.Tests` の `TestDoubles` / `TestAuthHandler` /
   `RiskWorkerWebApplicationFactory` / `ShortSellReleaseFixtures`）の `using` を必要とし、
   **純粋な移送に無関係な差分が数百行増える**。鏡写しはフォルダの規範であり、テスト型はアセンブリ内で
   一意である。

## 決定

**決定 1 — 操作（3 段目）は登録表に登録された 1 端点とする。** `<集約>Endpoints.cs` は登録表として
2 段目に残し、`MapGroup` ／ タグ ／ グループ単位の認可・フィルタ ／ `Program.cs` から呼ぶメソッド名
（例 `MapRiskControlEndpoints`）／**登録の順序**を変えない。各操作フォルダは `Endpoint.cs` を持ち、
登録表には `read.MapGetSizingContext();` のような呼び出しだけが残る。

**決定 2 — ファイルの行き先は「1 つの操作にしか使われないか」だけで決める**（`ADR-0068` 決定 2 の踏襲）。
判定において **`Program.cs` からの DI 登録は参照元として数えない**（全ファイルが該当し、判定が空になる）。
**`Infrastructure/`・`Hosted/`・他サービスから使われるものは 2 段目に残す** —— 呼び出し元が `Features/` の
操作ではないためである。

**決定 3 — 1 つのファイルが複数操作の処理を含む場合、操作ごとの処理を切り出して 3 段目へ下ろし、
共通部分は 2 段目に残す**（`ADR-0068` 決定 3）。RiskManagement では `ActorOf`（書き込み系の全操作が使う）を
`RiskControlEndpoints` に `internal static` として残し、2 操作が使う `KillSwitchRequest` /
`PauseRequest` を 2 段目の独立ファイルへ出した。

**決定 4 — 名前空間はフォルダに合わせる**（`RiskManagementService.Features.RiskManagement.<操作>`。
[IADR-0261](IADR-0261_namespace-alignment-to-platform.md) の規則の延長）。**3 段目は 2 段目の入れ子であるため、
下ろしたファイルは 2 段目の共有型を `using` なしで見られる**（C# の名前解決が外側の名前空間へ及ぶ）。
追随が要るのは 3 段目を参照する側（登録表・`Program.cs`・テスト）だけである。

**決定 5 — `Tests/` は本体の樹形をそのまま写す。** `Tests/Features/<集約>/<操作>/` ／
`Tests/Features/<集約>/` ／ `Tests/Domain/` ／ `Tests/Infrastructure/<区分>/` ／ `Tests/Hosted/`。
`Program.cs` の配線テストとテスト土台は `Tests/` 直下に残す。**テストの名前空間は `<Svc>.Tests` のまま
据え置く**（既存の `Tests/Contracts` / `Tests/Manipulation` が持つ名前空間は移送前からの形として尊重する）。

**決定 6 — 契約とメッセージ URN に触れる型は動かさない。** `AiStockTrading.Shared.Contracts` の型と、
`EventMessageTypeNameTests` / `event-schemas.baseline.json` が固定するイベント型は本移送の対象外である
（`MessageUrn` は名前空間を含むため、動かすと wire 契約が壊れる）。

## 理由

- **決定 1・3** は `ADR-0068` の字義であり、基盤が「テスト件数を前後で完全に一致させて」実証済みの形である。
  本移送でも `RiskManagementService.Tests` は **1589 件 → 1589 件**で一致し、全アセンブリ合計も
  **5444 件 → 5444 件**で一致した（移送前 `develop` `322cb143`）。
- **決定 2** は判定を機械的に保つ。`ADR-0065` 決定 7 が「フォルダ境界はコンパイラより弱い。規律を宣言だけに
  委ねない」と定めた以上、**同じコードを見た 2 人が同じ結論に達する**判定でなければ規範として働かない。
- **決定 4** は churn を最小化する。**入れ子の名前空間は外側を自動で見る**ため、下ろすファイル自身には
  `using` の追加が要らない —— 実測で production 側の追随は `Program.cs` の 6 行と登録表の 26 行だけ、
  テスト側は 14 ファイルへの 1 行追加だけで済んだ。
- **決定 5** の名前空間据え置きは、「移送で壊れないこと」を最優先した判断である。テストの名前空間を折る
  利得（可読性）に対し、失敗リスク（共有土台の解決漏れが数百箇所）が釣り合わない。

## 結果

- **良い影響**
  - `RiskManagementService` に**操作フォルダ 26 個**ができ、`Features/` の深さ 2 が 0 でなくなった。
    ほかの 10 サービスへ当てる規則が実地で検証された。
  - `Tests/` が `Features/` ／ `Domain/` ／ `Infrastructure/` ／ `Hosted/` の鏡写しになり、
    スライスと同じ経路でテストへ辿れるようになった（プロジェクトは 1 本のまま）。
  - **公開面は 1 バイトも動いていない** —— ルート・認可ポリシー・応答形・`Program.cs` の DI 登録・
    wire 契約に差分が無く、既存の端点テスト（`RiskControlEndpointsTests` /
    `StageGateEndpointsTests` / `Contracts/FrontendContractFixtureTests`）が無改修で緑である。
- **悪い影響 / トレードオフ**
  - 🔴 **HTTP 端点を持たない 5 サービス（Backtest / InformationCollection / Notification /
    OrderExecution / TradeDecision）には、本規則を当てても操作フォルダが 1 つも生まれない。**
    決定 1 の案 2（イベント購読・`BackgroundService` の 1 巡回を操作とみなす）を採らない限り解けない。
    **これは #613 の受け入れ基準「全ユースケースが操作フォルダを持つ」を、この 5 サービスについては
    現状の置き場の決定のままでは満たせないという意味である。** 裁定が要る（§フォローアップ 1）。
  - **HTTP を持つ残り 5 サービスも、処理本体が `*Endpoints.cs` のラムダに書かれている**ため、
    移送は「切り出し」を伴う（`git mv` だけでは終わらない）。実測で 3 段目へ下ろせる既存ファイルは
    10 サービス 130 ファイル中 **1 件**（`CostControlService` の `MonthlyCostUsage.cs`）だけである。
  - `Features/RiskManagement/` の 2 段目には、26 の操作フォルダと並んで 40 余のファイル（ポート・
    複数操作が使うアプリケーションサービス・状態型）が残る。2 段目（集約）の粒度が粗いこと自体は
    本 IADR では動かさない。
- **フォローアップ**
  1. 🔴 **HTTP を持たない 5 サービスの扱いの裁定**（イベント購読・`BackgroundService` を操作とみなして
     `Features/<集約>/<操作>/` を作るか、`ADR-0068` の「操作」を HTTP 端点に限ると読んで
     3 段目を作らないか）。**後続 PR の前に決める。** 決めずに進めると、サービスごとに読みが割れる
     （`ADR-0068` §コンテキストが指摘した「3 本の PR が別々の読みを採る」の再演になる）。
  2. **残る 10 サービスの移送**（割り当て表は作業仕様書
     [20260903_613](../specs/20260903_613_vsa-three-tier-risk-management.md) §残る 10 サービスの割り当て表）。
  3. **`Domain/` を持たない 3 サービスの是正。** 実測では**真に Domain が無いのは `ConfigurationService`
     だけ**である（業務規則の正本は `AiStockTrading.Shared.Kernel.Trading` にあり、サービス固有なのは
     楽観排他という永続化の関心事だけ）。`AuditService`（`AuditEntry` / `AuditCorrelation` /
     `AuditEntryFactory`）と `NotificationService`（`DiscordCommandAuthorizer` /
     `KillSwitchConfirmation` / `VersionedConfirmationGuard` / `BotCommandParser` / `BotCommand` /
     `DiscordCommandContext`）は、**外部依存ゼロの業務規則型が `Features/` に紛れている分類漏れ**であり、
     `Domain/` へ切り出せる。本 PR では動かさない（`DomainSourceDependencyTests` の走査母集合が増える＝
     依存規律の検査対象が変わるため、独立した PR で扱う）。

## 追記（2026-09-03・第 2 弾 `ReportService` の移送）

> **追記の位置づけ**: 本 IADR の決定 1〜6 は変えない。§フォローアップ 1 として残していた裁定を確定させ、
> あわせて第 2 弾で判断が要った 2 点を記録する。以降の弾はこの追記を**参照するだけ**でよい
> （弾ごとに新しい IADR を起こさない）。

### 追記 1 — 「操作」は HTTP 端点に限る（§フォローアップ 1 の裁定・案 B を採用）

§検討した選択肢 論点 1 の**案 1（登録表の 1 端点＝1 操作）を、`Features/<集約>/<操作>/` を作る唯一の
根拠とする**。Wolverine 購読（`Infrastructure/Steps/`）と `BackgroundService`（`Hosted/`）は
**操作フォルダを作らない**。

- **根拠**: 置き場は既に決まっている —— ハンドラは platform `ADR-0065` 決定 1 の `Messaging`
  （本リポでは `Infrastructure/Steps/`）、`BackgroundService` は
  [IADR-0276](IADR-0276_claude-md-vsa-correction-and-hosted-placement.md) 決定 2 の `Hosted/` である。
  これらを `Features/` へ引き上げるのは **3 段化ではなく置き場の決定の覆し**であり、
  「純粋な移送」の枠を外れる。決定 2（`Infrastructure/`・`Hosted/` から使われるものは 2 段目に残す）とも
  一貫する —— 操作フォルダを作らない以上、それらの共有物は 2 段目に留まる。
- **#613 の受け入れ基準の読み**: 「全ユースケースが操作フォルダを持つ」は **HTTP 端点を持つ 6 サービス
  （Audit / Configuration / CostControl / MarketMonitor / Report / RiskManagement）に対する基準**として読む。
  **HTTP 端点を持たない 5 サービス（Backtest / InformationCollection / Notification / OrderExecution /
  TradeDecision）は「移送対象なし」**であり、操作フォルダが 0 個であることが本規則に照らした正しい姿である
  （`Tests/` の鏡写し〔決定 5〕は 11 サービスすべてに当てる）。
- **残余リスク**: 購読・常駐ジョブが増えても `Features/` の深さは 2 のままである。将来これを 3 段へ
  割りたくなったときは、置き場の決定（`ADR-0065` 決定 1・IADR-0276 決定 2）から改める必要がある。
  この曖昧さ自体は **platform `ADR-0065` 決定 2 の「操作」の語義**に由来するため、
  計画側へ明確化を環流する（planning へ `feedback` issue を 1 本起票）。

### 追記 2 — 1 操作専属の要求レコードは 3 段目へ下ろす（決定 3 の適用例の明確化）

第 1 弾の作業仕様書 §残る 10 サービスの割り当て表は、2 段目に残るものとして `ReportEndpoints.cs` を
「登録表（11 操作＋**5 DTO**＋`ReviewResult`/`RejectionMessage`/`ActorOf`）」と書いていた。
これは**移送前のファイルの中身の記述**であって、5 DTO をすべて 2 段目へ固定する指示ではない。

**決定 3 のとおり、1 操作しか使わない要求レコードは `<操作>/Endpoint.cs` へ同居させ、
2 操作以上が共有するものだけを 2 段目の独立ファイルへ出す。** 第 1 弾が
`KillSwitchRequest` / `PauseRequest` に対して採った扱いと同じである。
`ReportService` では `PnlSummaryRequest` / `DraftReportRequest` / `UpsertReportRequest` /
`ConfirmReportRequest` の 4 件を 3 段目へ下ろし、`ReviewCommandRequest`（present / request-changes の
2 操作が共有）だけを `Features/Reports/ReviewCommandRequest.cs` として 2 段目へ出した。

### 追記 3 — ソース位置に依存するテストデータは、テストと対で移す

決定 5 は「テスト土台（フィクスチャ）は `Tests/` 直下に残す」と定めたが、**テストのソースファイルからの
相対パスで書き戻すテストデータは、テストと離してはならない**。

`ReportService` の `ReportTemplateGoldenTests` は、読み取りを `AppContext.BaseDirectory/Golden/`
（出力ディレクトリ）から行う一方、`UPDATE_GOLDEN=1` の書き戻しを
`Path.GetDirectoryName(CallerFilePath)/Golden/`（**ソースツリー**）へ行う。テストだけを
`Tests/Domain/` へ移すと、更新モードが `Tests/Domain/Golden/` という存在しない場所へ静かに書く。

**採った形**: ゴールデン 6 ファイルをテストと対で `Tests/Domain/Golden/` へ移し、
`ReportService.Tests.csproj` の複写指定へ出力先を `Golden\` に固定する `Link` を足した
（出力ディレクトリからの読み取りパスは移送前と同一・ゴールデンの中身は 1 バイトも変えていない）。
**csproj の 1 行は移送に伴う機械的な追随であり、決定 4 の「追随が要るのは参照する側だけ」と同じ性質**である。

## 関連

- Supersedes: なし
- Superseded by: なし
