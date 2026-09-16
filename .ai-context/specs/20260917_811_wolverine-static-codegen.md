---
title: Wolverine のハンドラ生成コードをビルド時に書き出し、稼働では TypeLoadMode.Static で読み込んで実行時コンパイルを止める
type: spec
status: done
related_ids: [NFR-01, ADR-0006, IADR-0129]
author: endazon (with Claude Code)
created: 2026-09-17
updated: 2026-09-17
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_infrastructure-and-deployment.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0013_messaging-follow-wolverine-kafka.md
---

# 仕様書: Wolverine の実行時コンパイルを止める（#811 射程 1）

## 起点

- #808（PR #810）で、512Mi 容器の OOMKilled の根本原因が **Wolverine の実行時 Roslyn コンパイル（`TypeLoadMode.Dynamic`）
  の作業メモリが glibc malloc に残る**ことだと実測した。暫定策 `MALLOC_ARENA_MAX=2` は per-thread アリーナの積み上がりを
  断つだけで、保持されるメモリは main アリーナ `[heap]` へ移る（配備後 20 分で audit 214 MB・risk 111 MB）。
  引き金（コンパイルそのもの）は残っている。
- #811 射程 1: Wolverine の本番推奨どおり**ビルド時に `codegen write` で生成コードを書き出し、稼働では
  `TypeLoadMode.Static` で読む**。射程 2（#782 コメントの是正・仕様書追記）と射程 3（#810 の実測記録）は別途扱う。
  **`deploy/helm/**` と `.github/workflows/helm.yml` は本作業で触らない**（malloc の閾値 env を並行作業が入れている）。
- 関連: NFR-01（可用性）／ ADR-0006（配備）／ ADR-0013（Wolverine 追随）／ IADR-0129 決定 6（`RuntimeCompilation` を参照する
  第 1 段階の判断。再評価条件「メモリが問題になった時点」は #808 で成立）。

## 前提の実測（Wolverine 6.24.5 / JasperFx 2.37.0 のソースで確認）

| 事実 | 出所 | 設計への影響 |
| --- | --- | --- |
| `UseWolverine` の configure 内で `CodeGeneration.TypeLoadMode` を設定すると `TypeLoadModeHasChanged` が立ち、JasperFx プロファイル（`ActiveProfile.GeneratedCodeMode`）に**上書きされない** | `WolverineOptions.ReadJasperFxOptions` | 環境名に依らず明示設定が効く。**稼働クラスタは `ASPNETCORE_ENVIRONMENT=Development`**（chart）なので、`CritterStackDefaults(x => x.Production.GeneratedCodeMode = Static)` の慣用形は**効かない**——採らない |
| ハンドラチェーンの型解決は**遅延**（`HandlerGraph.HandlerFor` → 1 通目で `InitializeSynchronously`）。Static で型が無いと `StaticTypeLoader` が `ExpectedTypeMissingException` を投げるが、それは**起動時ではなく 1 通目**である | `HandlerGraph.cs` / `StaticTypeLoader.cs` | 起動時に全型を表明する仕組みを**自前で**入れる（`ICodeFileCollection.AssertPreBuildTypesExist`）。表明が無いと「起動・readiness・キュー宣言は成功するのにメッセージだけ処理されない」（IADR-0129 決定 11 と同型の静かな失敗）になる |
| `codegen` コマンド中は `DynamicCodeBuilder.WithinCodegenCommand` が立ち、`WolverineRuntime` は外部トランスポートを stub して永続化も止める。`codegen write` はホストを **Build するだけで Start しない** | `GenerateCodeCommand.cs` / `WolverineRuntime.HostService.cs` | ビルド段で RabbitMQ は不要。ただし本リポの `Program.cs` は `Build()` 直後に **EF の `MigrateAsync()`** を走らせるため、そこだけ DB 無しで通す分岐が要る |
| `codegen write` は `Internal/Generated/WolverineHandlers/*.cs` に加えて **`GeneratedHandlerRegistry`**（発見済みハンドラ型の `typeof` 配列）を書く。Static ではこの登録簿を読んでアセンブリ走査も省く | `HandlerGraph.GeneratesCode.cs` / `HandlerRegistry.cs` | 「生成コードが在るか」の判定に**この型の有無**を使う（ハンドラ 0 件のサービスでも書かれる） |
| `RunJasperFxCommands(IHost, args)`: 引数なし／`run` はホストを起動して停止まで待つ（`IHostApplicationLifetime.ApplicationStopping` で抜ける＝SIGTERM で今までどおり止まる）。`--urls` 等の ASP.NET フラグは `RunAsync` へ素通し | `CommandLineHostingExtensions.cs` / `RunCommand.cs` | `app.Run()` を `return await app.RunJasperFxCommands(args)` に置き換えても稼働の挙動は変わらない |

## 設計

### 1. 共通ヘルパ（`WolverineExtensions.UseAiStockTradingRabbitMq`）でモードを 1 箇所で決める（IADR-0129 決定 4 の延長）

解決順（上が優先）:

1. 呼び出し側が `options.CodeGeneration.TypeLoadMode` を**既に**設定している（`TypeLoadModeHasChanged`）→ 尊重する（テストの固定用。サービスの `Program.cs` では使わない）
2. 環境変数 **`WOLVERINE_TYPE_LOAD_MODE`**（`Static` / `Dynamic` / `Auto`。大小無視。不正値は起動時に `ArgumentException` で止める）
3. application assembly に `Internal.Generated.WolverineHandlers.GeneratedHandlerRegistry` が在る → `Static`
4. それ以外 → `Dynamic`（`dotnet test`・ローカル `dotnet run`・`codegen write` 自身はここ）

**`Static` に決まったときだけ** hosted service `WolverinePreGeneratedCodeAssertion` を登録する（`options.Services` へ。
`UseWolverine` は configure より前に `WolverineRuntime` を hosted service 登録するので、本 hosted service は
**runtime の起動＝`HandlerGraph.Compile` の後**に走る）。中身は `services.GetServices<ICodeFileCollection>()` の全件に
`AssertPreBuildTypesExist(services)`。欠けていれば `MissingTypeException`（欠けたファイル名つき）で**起動が失敗する**。
副作用として全ハンドラの型が起動時に attach され、1 通目のレイテンシも消える。

### 2. `Program.cs`（Wolverine を配線する 11 サービス）

- `app.Run();` → `return await app.RunAiStockTradingAsync(args);`（shim の共通終端）。引数なし／`run`／JasperFx の動詞は
  `RunJasperFxCommands` へ（`dotnet <dll> codegen write` を受けるため）、**`--` で始まる ASP.NET 流の引数は従来の `RunAsync` へ**。
  🔴 最初は `RunJasperFxCommands` を直接呼んだが、**`WebApplicationFactory` が `UseSetting`（HostSettings）を `--Key=Value`
  引数として `Main` へ渡す**ため、JasperFx が `run` を前置したうえで知らないフラグを終了コード 1 で拒否し、ホストが一度も
  起動せず TestServer が "The server has not been started" で落ちた（実測: RiskManagement 11 件・TradeDecision 23 件。
  develop の `Program.cs` に戻すと通る A/B で切り分けた）。素通しは shim に閉じる（`UsesJasperFxCommands`）。
- `MigrateAsync()` ブロック（7 サービス）を `if (JasperFxCommandLine.IsHostRun(args))` で囲む。
  `IsHostRun` は shim の純関数: 引数なし／先頭 `run`／先頭が `--`（ASP.NET フラグ）→ true、それ以外（`codegen` / `describe` 等）→ false。
- `OpendAuthGateway` は Wolverine を持たないので**触らない**（Dockerfile 側で codegen をスキップする）。

### 3. `backend/Dockerfile`（build 段の並べ替え）

```
restore → build（Release）→ [Program.cs に UseWolverine( があるときだけ] dotnet run --no-build -- codegen write → publish
```

- `dotnet run --project <csproj>` は作業ディレクトリをプロジェクト直下にするので、`ContentRootPath` 由来の出力先
  `<project>/Internal/Generated/` に書かれ、続く `publish`（`--no-build` を付けない）が再コンパイルして DLL に取り込む。
- 判定は `grep -q 'UseWolverine(' "$(dirname SERVICE_PROJECT)/Program.cs"`（`check-consumer-endpoint-names.js` が配線の
  有無に使うのと同じ信号）。opend-auth-gateway は同じ Dockerfile で作るがここで抜ける。
- runtime 段に **`ENV WOLVERINE_TYPE_LOAD_MODE=Static`** を焼く。生成コードの有無判定（解決順 3）だけに頼ると、
  codegen 段が黙って抜けた（例: 判定の grep が壊れた）イメージが Dynamic で静かに動いてしまう。env を焼いておけば
  そのイメージは**起動時に落ちる**（fail-loud）。`WolverineFx.RuntimeCompilation` の参照は**残す**（Dynamic の dev/test に要る。
  Static では Roslyn はロードされない）。
- `.dockerignore` に `**/Internal/Generated/` を足し、開発者がローカルで `codegen write` した古い生成物がコンテキスト経由で
  混入しないようにする。`.gitignore` にも同じパターンを足す（生成物はコミットしない。イメージのビルド時に毎回作る）。

### 4. 変えないもの

- `deploy/helm/**`・`.github/workflows/helm.yml`（並行作業と重ねない。chart の env は不変で、Static は**イメージが自分で**選ぶ）。
- `Directory.Packages.props` の版・`WolverineFx.RuntimeCompilation` の参照（コメントだけ現状に合わせる）。
- `ci.yml` の publish 乾式実行（`--no-build`・codegen なし＝Dynamic 経路のビルド検証として従来どおり）。

## 走査した母集合（規則 2・9・10）

| 走査 | 結果 | 扱い |
| --- | --- | --- |
| `grep -rn "app\.Run()" backend/Services/*/Program.cs` | 12 件（11 Wolverine サービス＋`OpendAuthGateway`） | 11 件を置換。`OpendAuthGateway` は Wolverine 無しのため対象外（Dockerfile 側でスキップ） |
| `grep -n "MigrateAsync" backend/Services/*/Program.cs` | 7 件（Audit / Configuration / CostControl / MarketMonitor / OrderExecution / Report / RiskManagement） | すべて `IsHostRun` で囲む |
| `grep -rn "TypeLoadMode\|codegen write\|RuntimeCompilation\|Internal/Generated\|Generated code for"`（`bin`/`obj`/`.git`/`.ai-context/specs`/`CHANGELOG.md` を除く） | IADR-0129（追記先）／`.ai-context/adr/README.md`（索引行）／`Directory.Packages.props`（コメント）／12 csproj の `PackageReference`（据え置き）／Tests csproj のコメント 8 件（テストは Dynamic のまま＝記述は正しい・据え置き）／`deploy/helm/.../deployment.yaml:126`（chart コメント。**本作業で触らない**） | 追記・更新は左のとおり |
| `grep -rln "dotnet publish\|k8s-local-images\|SERVICE_PROJECT"` | `backend/Dockerfile`・`docker-compose.yml`（同じ Dockerfile を使う→追随不要）・`scripts/k8s-local-images.sh`（build args 不変→追随不要）・`ci.yml`（乾式 publish。変えない）・chart README / values / `opend.yaml` / docs 3 件（イメージ名とタグの話で codegen に触れていない→追随なし） | 変更は Dockerfile のみ |
| `grep -rn "RunJasperFxCommands\|JasperFx" --include=*.cs --include=*.md` | IADR-0129 の 2026-09-16 追記（「`Program.cs`（`RunJasperFxCommands`）」と予告済み）と shim の `using` 1 件のみ | 予告どおりの実装 |
| 規則 10（自分の記述が新たに誤りにならないか） | IADR-0129 決定 6 本文「第 1 段階では `RuntimeCompilation` を参照する」は**歴史的記述として残し**、日付つき追記で「稼働は Static・参照は dev/test 用に残す」と言い直す。`Directory.Packages.props` の「RuntimeCompilation は必須である」は Dynamic 前提の記述なので条件を書き足す | 本文プロズは書き換えない（凍結記録の規約）。追記のみ |

除外: `CHANGELOG.md`（自動生成）、`.ai-context/specs/`（point-in-time）、`deploy/helm/**`（並行作業の領域）。

## 受け入れ基準 → 検証

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | モードの解決順（明示 > env > 生成物の有無 > Dynamic）と、不正な env が例外になること | `WolverineTypeLoadModeTests`（純関数 `ResolveTypeLoadMode` の Theory） |
| 2 | Static に決まったとき、生成コードを持たないアセンブリでは**ホストの起動が失敗する**（1 通目ではなく起動時） | `WolverineTypeLoadModeTests`: `TypeLoadMode.Static` を明示 → `StartAsync` が `MissingTypeException` で失敗 |
| 3 | Dynamic（既定）では従来どおり起動し、ハンドラが実行できる | 既存 `WolverineHandlerCodegenTests` / `WolverineTopologyTests` が緑のまま |
| 4 | `IsHostRun` が `codegen` / `describe` を false、引数なし / `run` / `--urls` を true と判定する。`UsesJasperFxCommands` が `--Key=Value` を false にし、`WebApplicationFactory` の HostSettings を使う既存 Wiring テスト（RiskManagement / TradeDecision）が緑に戻る | `JasperFxCommandLineTests`（Theory）＋ 既存 `SimulatorProfileWiringTests` / `LlmGatewayAuthWiringTests` 等 |
| 5 | `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が通る | ローカル実行（結果は PR 本文） |
| 6 | Dockerfile が `codegen write` を通し、イメージの起動ログに `The Wolverine code generation mode is Static ...` が出て、`Generated code for` が出ない。生成コードを消したイメージ相当（`WOLVERINE_TYPE_LOAD_MODE=Static` で登録簿が無い）は起動時に落ちる | audit-service を `poc811` タグでローカルビルドし、使い捨ての postgres / rabbitmq（ローカルに既在のイメージ。pull なし）と一緒に `nerdctl run --rm` で起動して確認。終了後に `rmi` |
| 7 | 稼働で 45 型の受信後も `[heap]` / アリーナが伸びない | **本 PR では未検証**（配備後に #811 へ記録。#810 の実測手順と同じ `/proc/1/smaps`） |

## 未検証

- 基準 7（配備後の実測）。
- `ci.yml` の乾式 publish は codegen を通さない（Dynamic 経路のビルド確認のまま）。イメージビルドの CI 検証は無い
  （従来どおり `scripts/k8s-local-images.sh` の手動実行）。
- opend-auth-gateway イメージは Wolverine を持たず、`WOLVERINE_TYPE_LOAD_MODE=Static` が焼かれても読まれない（無害）。
  ローカルでは未ビルド（Dockerfile の分岐は audit-service 側で `grep` の真の経路だけ実走）。

## 計画書との差異

- 差異: なし（ADR-0013 は Wolverine への追随を求め、Wolverine 自身の本番推奨に寄せる変更。ADR-0006 の配備方式・limit は不変）。
