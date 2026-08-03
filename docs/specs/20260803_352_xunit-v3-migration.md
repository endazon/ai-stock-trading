---
title: 作業仕様書 — xUnit v2 から v3 へ移行する
type: work
status: review
related_ids: [NFR, IADR-0001]
author: endazon (with Claude Code)
created: 2026-08-03
updated: 2026-08-03
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0010_dotnet-10-follow.md
related_specs:
  - ../adr/IADR-0001_repo-structure-and-stack.md
  - ./20260803_351_awesomeassertions-migration.md
  - ./20260802_344_reimplementation-preparation.md
  - ../DEFINITION_OF_DONE.md
---

# 作業仕様書: xUnit v2 → v3 移行（#352）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR。テスト基盤のフレームワーク更新）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: 計画 ADR-0001（platform 再利用）・ADR-0010（.NET 10 追随）／ platform ADR-0030（アプリ層標準＝テスト基盤は **xUnit v3**）
- 実装 ADR: [IADR-0001](../adr/IADR-0001_repo-structure-and-stack.md)（基盤リポに規約を揃える）
- 起点 issue: [#352](https://github.com/endazon/ai-stock-trading/issues/352)（親 [#345](https://github.com/endazon/ai-stock-trading/issues/345) / [#344](https://github.com/endazon/ai-stock-trading/issues/344)）

## 目的・背景

platform ADR-0030 はテスト基盤の標準を **xUnit v3**・AwesomeAssertions・NSubstitute・Testcontainers と
定めた。現行は xUnit **2.9.3**（`xunit` / `xunit.runner.visualstudio` 2.8.2）である。

本作業は [#345](https://github.com/endazon/ai-stock-trading/issues/345) の分割 **2/4**。[#351](https://github.com/endazon/ai-stock-trading/issues/351)（AwesomeAssertions 置換）の直後に着手する
（同じテストプロジェクト群に触れるため直列化して衝突を避ける、という #345 の分割方針に従う）。

#351 と異なり、本作業は**パッケージ ID の差し替えだけでは済まない**。v3 はテストアセンブリを
実行可能アセンブリ（`OutputType=Exe`）に変え、`IAsyncLifetime` のシグネチャを変え、
`ITestOutputHelper` の名前空間を移し、テストケースの既定実行順序まで変えた。**手当てを要した箇所を
後掲「破壊的変更への手当て」に全件列挙する**（issue #352 が明示した要求であり、本 issue の実体は
そこにある）。

## 対象範囲

- 対象:
  - `Directory.Packages.props`: `xunit` 2.9.3 → `xunit.v3` 3.2.2 ／ `xunit.runner.visualstudio` 2.8.2 → 3.1.5
  - 全テストプロジェクト（**39 `.csproj`**）の `PackageReference` 追随
  - v3 の破壊的変更への追随（後掲の分類 A〜E。実測で **5 分類・テストコード 8 ファイル ＋ `.editorconfig` 1 規則**）
  - `CLAUDE.md` / `docs/tech/tech-requirements.md` の記載追随
- 対象外:
  - プロジェクト構成 3 → 7 標準（**#353**）
  - MassTransit → Wolverine（**#354**）
  - アサーションライブラリの置換（**#351** で完了済み）
  - **Microsoft Testing Platform（MTP）ネイティブ実行への移行**（後掲「設計 / 実行モデルの選択」。
    CI・カバレッジ・フィルタの全面変更を伴うため、本 issue のスコープ外とする）
  - `AGENTS.md` / `.github/copilot-instructions.md`（実測で xUnit のバージョンへの言及が無く、変更不要）

## 設計

### バージョンの選択

nuget.org の flat container index（`https://api.nuget.org/v3-flatcontainer/<id>/index.json`）で
実在する安定版を確認し、**プレリリースを除く最新安定版**を採った。

| パッケージ | 移行前 | 採用 | 確認方法・根拠 |
| --- | --- | --- | --- |
| `xunit` | 2.9.3 | **削除** | v3 の本体パッケージ ID は `xunit.v3`。`xunit` は v2 系のまま更新されない |
| `xunit.v3` | — | **3.2.2** | flat container の最新安定版（`4.0.0-pre.*` はプレリリースのため不採用）。公開日 2026-01-14 |
| `xunit.runner.visualstudio` | 2.8.2 | **3.1.5** | 同上（`4.0.0-pre.*` は不採用）。nuspec の description が「v1, v2, v3 を実行できる」と明示 |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | 据え置き | VSTest 経路を維持するため必要（下記） |
| `coverlet.collector` | 6.0.4 | 据え置き | VSTest のデータコレクタとして従来どおり機能する（実測） |

### 実行モデルの選択（VSTest 経路の維持）

xunit.v3 **3.2.0 以降**、メタパッケージ `xunit.v3` は `xunit.v3.mtp-v1`（Microsoft Testing Platform v1）を
取り込む構成に変わった（`xunit.v3` 3.2.2 の nuspec を実確認）。すなわち v3 のテストアセンブリは
**それ自体が MTP 対応の実行可能アプリ**になる。

一方、本リポジトリの CI は VSTest 経路に依存している。

- `dotnet test --filter "Category!=Integration"`（`ci.yml`）／`--filter "Category=Integration"`（`integration.yml`）
- `--collect:"XPlat Code Coverage"`（coverlet.collector）→ `scripts/check-coverage.js` の floor 検査（#343）

MTP ネイティブ実行へ寄せると、フィルタ構文（`--filter` → `--filter-query` 等）とカバレッジ収集
（`Microsoft.Testing.Extensions.CodeCoverage`）が総取り替えになり、`check-coverage.js` が読む
Cobertura レポートの出力先も変わる。**本 issue はフレームワークの版を上げることが目的であり、
実行モデルまで同時に変えると失敗時の切り分けができない**。よって

- `Microsoft.NET.Test.Sdk` ＋ `xunit.runner.visualstudio` 3.1.5 を残して **VSTest 経路を維持**する
- MTP ネイティブ化は別途 issue として検討する（未決事項 2）

という方針を採った。この構成が実際に成立することは、移行前に最小の検証プロジェクト
（`xunit.v3` 3.2.2 ＋ `xunit.runner.visualstudio` 3.1.5 ＋ `Microsoft.NET.Test.Sdk` 17.14.1 ＋
`coverlet.collector` 6.0.4）を作って実測で確認した — `--filter` の両方向・`--collect:"XPlat Code Coverage"`
のいずれも従来どおり動作した。

### ビルド構成（`OutputType`）

v3 のテストプロジェクトは実行可能アセンブリになるが、**`.csproj` に `OutputType=Exe` を書く必要は無い**。
`xunit.v3.core` の MSBuild targets が `OutputType=Exe` と `GenerateProgramFile=false` を自動設定し、
エントリポイントも自動生成する（実測: `dotnet msbuild -getProperty:OutputType` が `Exe` を返す）。
したがって **39 個の `.csproj` の変更は `Include="xunit"` → `Include="xunit.v3"` の 1 行のみ**である。

## 破壊的変更への手当て（実測・全件）

「機械的置換で済まなかった箇所」を分類して列挙する。**テストの意味（何を検証しているか）は
1 件も変えていない**。

### 分類 A: `IAsyncLifetime` のシグネチャ変更（`Task` → `ValueTask`）

v3 の `IAsyncLifetime` は `Task InitializeAsync()` / `Task DisposeAsync()` から
**`ValueTask InitializeAsync()` ＋ `IAsyncDisposable` 由来の `ValueTask DisposeAsync()`** へ変わった。

| ファイル | 手当て |
| --- | --- |
| `backend/Tests/AiStockTrading.IntegrationTests/TradeExecutionPipelineE2ETests.cs` | `public async Task InitializeAsync()` → `public async ValueTask InitializeAsync()`／`DisposeAsync` 同様 |
| `backend/Tests/AiStockTrading.IntegrationTests/OrderExecutionPipelineE2ETests.cs` | 同上 |
| `backend/Tests/AiStockTrading.IntegrationTests/KeycloakOwnerOnlyEndpointE2ETests.cs` | 同上 |
| `backend/Tests/AiStockTrading.IntegrationTests/ServiceTokenSyncQueryE2ETests.cs` | 同上 |
| `backend/Tests/AiStockTrading.IntegrationTests/PositionDriftStateConcurrencyE2ETests.cs` | 同上 |

いずれも `async` メソッドの戻り値型のみの変更であり、`await` するコンテナ起動・破棄の手順は
一切触っていない（**コンテナのリーク防止のための try/catch も原形のまま**）。

### 分類 B: `ITestOutputHelper` の名前空間移動

v2 は `Xunit.Abstractions`、v3 は **`Xunit`**（`xunit.v3.extensibility.core`）に置かれる。
v3 には `Xunit.Abstractions` 名前空間そのものが存在しない。

| ファイル | 手当て |
| --- | --- |
| `backend/Services/BacktestService/tests/BacktestService.Domain.Tests/Calibration/Stage0CalibrationReportTests.cs` | `using Xunit.Abstractions;` を削除（`using Xunit;` が既にあるため追加不要） |

### 分類 C: `DataAttribute.GetData` のシグネチャ変更（属性を反射で読むテスト）

v2 の `DataAttribute.GetData(MethodInfo)` は `IEnumerable<object[]>` を返した。v3 は
`ValueTask<IReadOnlyCollection<ITheoryDataRow>> GetData(MethodInfo, DisposalTracker)` へ変わり、
引数を 1 つで呼ぶとコンパイルエラー（CS7036）になる。

| ファイル | 手当て |
| --- | --- |
| `backend/Shared/AiStockTrading.Shared.Contracts.Tests/EventMessageUrnTests.cs` | `[InlineData]` 属性群から固定済みイベント型の一覧を取り出す `PinnedEventTypes()` を、v3 が公開する `InlineDataAttribute.Data`（属性の実引数配列）から読む形へ変更 |

`InlineData` は 1 属性 = 1 行であり、`Data[0]` は v2 で `GetData()` が返していた 1 行目の 0 番目と同一である。
**取り出す値・検証内容は変わっていない**（URN 固定の母集合一致ガードはそのまま働く）。

### 分類 D: テストケース実行順序の変化が露出させた、既存テストの順序依存

v3 は既定のテストケース順序（`DefaultTestCaseOrderer` が用いる unique ID の算出）が v2 と異なる。
これ自体は API の破壊的変更ではないが、**順序に暗黙依存していた既存テストを破壊する**。実測で
`RiskManagementService.Worker.Tests` の 2 件が赤になった。

- 直接原因は 1 件のみ。`RiskControlEndpointsTests` は `IClassFixture` で 1 つの
  `WebApplicationFactory`（InMemory DB）をクラス内で共有する。
  `利用者は_kill_switch_を起動でき状態が永続化される` は kill switch を**起動したまま終わる**。
  v3 の順序ではその後に `利用者は_status_を照会でき現在状態が返る` が走り、
  「優先中の統制は pause」の表明が KillSwitch を観測して落ちた。
- もう 1 件（`利用者は一時停止と再開ができ状態が永続化される`）は**その連鎖**である。上の失敗で
  表明が例外送出した結果、末尾の後片付け（`/resume`）が実行されず pause が理由「様子見」のまま
  残留した。pause は冪等（停止中の再 pause は現状態を返すのみ）なので、後続テストが自分で設定した
  理由を観測できなかった。

| ファイル | 手当て |
| --- | --- |
| `backend/Services/RiskManagementService/tests/RiskManagementService.Worker.Tests/RiskControlEndpointsTests.cs` | `利用者は_status_を照会でき現在状態が返る` の Arrange に `/risk-controls/kill-switch/disengage` を 1 行追加し、**このテストが必要とする前提を自ら成立させる**ようにした |

**表明（Assert）は 1 行も変えていない**。追加したのは Arrange のみで、テストが何を検証しているかは不変である。
「順序に依存しない」という、テストが本来満たすべき性質を回復させただけである
（`利用者は_kill_switch_...` 側に後片付けを足す案も採れるが、末尾の後片付けは表明が落ちた瞬間に
飛ばされる — まさに 2 件目の失敗が示したとおり — ため、前提を Arrange で作る方を採った）。

### 分類 E: 新規アナライザ規則 xUnit1051（警告 1054 件）

`xunit.v3` は `xunit.analyzers` 1.27.0 を同梱する。新規則 **xUnit1051**
「`CancellationToken` を受け取る呼び出しには `TestContext.Current.CancellationToken` を渡せ」が
**1054 箇所・119 ファイル**で発火し、ビルドの警告ゼロを崩した。

| 対応 | 内容 |
| --- | --- |
| `.editorconfig` | `dotnet_diagnostic.xUnit1051.severity = none` を追加（理由をコメントで併記） |

規則は助言的（テストのキャンセル応答性の向上）であり、**検証の正しさには関わらない**。1054 箇所へ
機械的に引数を足すとオーバーロード解決やタイムアウト挙動が変わり得るため、「テストの意味を
変えない」という本移行の受け入れ条件と両立しない。採用の可否は独立した課題として扱う（未決事項 4）。

### 手当てが不要だった（＝機械的置換で済んだ）もの

調査したが該当が無かった／非互換に当たらなかった項目も、次に同じ移行をする人のために残す。

| 項目 | 実測 |
| --- | --- |
| `Assert.*` の API 変更 | **`Assert.` の呼び出しが 0 件**（#351 で全アサーションが AwesomeAssertions のため）。v3 で最も広く影響する破壊的変更を、直前の #351 が結果的に回避していた |
| `TheoryData` の型付け強化 | 使用 0 件のため無関係 |
| `MemberData` のデータソース型 | 1 件（`BffPassThroughTests.AllRoutes`＝`string[][]`）あるが、v3 でもコンパイル・実行とも通った（警告・エラーとも無し）。**先回りして `TheoryData<...>` へ書き換えることはしない**（不要な変更） |
| `[Fact]` / `[Theory]` / `[Trait]` / `[InlineData]` | 属性名・使用法とも互換。1772 / 133 / 7 / 480 件すべて無改修 |
| `IClassFixture` / `ICollectionFixture` / `[Collection]` | 互換。19 件すべて無改修 |
| `xunit.runner.json` | リポジトリに存在しない（既定設定のみ）ため対応不要 |
| `OutputType=Exe` の明示 | 不要（前掲「ビルド構成」） |
| `.github/workflows/ci.yml` / `integration.yml` | **変更不要**。VSTest 経路を維持したため、`dotnet test --filter "Category!=Integration" --collect:"XPlat Code Coverage"`（`ci.yml`）と `--filter "Category=Integration"`（`integration.yml`）の記述がそのまま機能する（実測で確認） |
| `AGENTS.md` / `.github/copilot-instructions.md` | xUnit のバージョンへの言及が無く、変更不要 |

## 受け入れ基準

issue [#352](https://github.com/endazon/ai-stock-trading/issues/352) の受け入れ基準をそのまま写す。

- [x] xUnit v2 への参照が残っていない
- [x] `dotnet test`（`Category!=Integration`）が **移行前と同一の合格数**で green
- [x] カバレッジ収集が従来どおり機能し、`scripts/check-coverage.js` の floor 検査が通る（#343）
- [x] `Category=Integration` のフィルタが v3 でも従来どおり効く（`integration.yml` の夜間実行で確認）
- [x] `dotnet format --verify-no-changes` が通る

## テスト方針

本作業はテストの意味を変えない移行であるため、**既存テストの合格数の一致**が受け入れの中心である。

| 確認 | 方法 | 結果 |
| --- | --- | --- |
| 移行でテストの意味が変わらない | 移行前後の合格数を比較 | **2256 → 2256**（Failed=0・39 アセンブリ）。**アセンブリ別の内訳も全 39 件が完全一致**（合計だけでなく内訳を突き合わせ、増減の相殺を排除した） |
| ビルドの健全性 | `dotnet build backend/backend.slnx --no-incremental` | 0 Warning / 0 Error |
| カバレッジ収集が機能する | `dotnet test --collect:"XPlat Code Coverage"` ＋ `node scripts/check-coverage.js` | 39 レポート・行カバレッジ **64.45%**（12047/18692・floor 62.00% を上回る） |
| `Category!=Integration` が効く | 上記実行で `AiStockTrading.IntegrationTests` の Integration テストが実行されないこと | 同アセンブリの実行数が移行前後で 5 件のまま一致（Integration の 7 件は不実行） |
| `Category=Integration` が効く | `dotnet test backend/Tests/AiStockTrading.IntegrationTests --filter "Category=Integration" --list-tests` | **7 件が選択された**。この環境には実インフラ（Docker）が無いため実走はせず、**フィルタが対象を選ぶこと**のみ確認した（実走は `integration.yml` の夜間実行） |
| 整形 | `dotnet format backend/backend.slnx --verify-no-changes` | 差分なし（終了コード 0） |
| リポジトリ検査 | `node scripts/scripts.test.js`（142 passed）/ `check-banned-libraries.js` / `check-test-traceability.js`（313 ファイル・25 ID） | すべて OK |
| xUnit v2 参照の残存 | `Directory.Packages.props` と全 `.csproj` の grep | `Include="xunit"` **0 件** / `Include="xunit.v3"` 39 件 |

## 計画書との差異

- 差異: なし。platform ADR-0030 が定める xUnit v3 へ移行した。
- ただし ADR-0030 は「xUnit v3」としか述べておらず、**実行モデル（VSTest / MTP）までは定めていない**。
  本作業は VSTest 経路を維持した（前掲「実行モデルの選択」）。この判断は ADR-0030 に反しない。

## 未決事項

1. **基盤リポとのバージョン整合** — `microservices-platform` の Central Package Management が指定する
   `xunit.v3` / `xunit.runner.visualstudio` の版と一致するかを確認する（#351 の未決事項 1 と同じ性質）。
   本セッションでは基盤リポが参照範囲外のため未確認。
2. **MTP ネイティブ実行への移行** — `xunit.v3` 3.2.x は MTP を既定に据えており、将来 VSTest 経路が
   非推奨になる可能性がある。移行するならフィルタ構文・カバレッジ収集・`check-coverage.js` の
   入力経路を同時に変える必要があり、独立した issue で扱うべきである。
3. **xUnit v2 の再混入防止（機械検査）** — `scripts/check-banned-libraries.js` に `xunit`（v2 の
   パッケージ ID）を登録すれば再混入を機械的に止められる（`Include="xunit"` は `xunit.v3` /
   `xunit.runner.visualstudio` を巻き込まず、`using Xunit;` は大文字始まりのため誤検出しない）。
   ただし同スクリプトは「platform ADR-0030 の棚卸しで**不採用**となったライブラリ」を対象とする
   設計であり、「同一ライブラリの旧メジャー」を載せるのは趣旨の拡張になる。本 issue のスコープ
   （移行に不可避な最小変更）を超えるため見送り、判断を別途仰ぐ。
4. **xUnit1051（`TestContext.Current.CancellationToken`）の採用可否** — 分類 E で抑止した規則。
   採用すればテストのキャンセル応答性が上がるが、1054 箇所の変更になる。独立した issue で
   「機械的に足してよい箇所」と「タイムアウト挙動を変える箇所」を仕分けたうえで判断する。
5. **テストの順序依存の棚卸し** — 分類 D で顕在化した 1 件は直したが、`IClassFixture` で可変状態を
   共有するテストクラスは他にもある（実測 19 箇所のフィクスチャ利用）。今回は v3 の順序で
   全 2256 件が green であることを確認したに留まり、**順序に依存しないことの機械的な保証は無い**。
   ランダム順序での実行を CI に足すか否かは別途検討する。

## 変更履歴

| 日付 | 内容 |
| --- | --- |
| 2026-08-03 | 初版作成（#352） |
