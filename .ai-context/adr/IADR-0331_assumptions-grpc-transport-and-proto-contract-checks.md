---
title: IADR-0331 全体前提条件の同期照会は REST と並走する gRPC 面を持ち、線上の 10 進は文字列・切替は構成・互換は検査器で守る
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - FR-17
  - UC-06
  - MSP:ADR-0029
  - MSP:ADR-0075
  - ADR-0001
  - IADR-0013
  - IADR-0046
  - IADR-0051
  - IADR-0063
  - IADR-0264
  - IADR-0284
  - IADR-0328
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# IADR-0331: 全体前提条件の同期照会は REST と並走する gRPC 面を持ち、線上の 10 進は文字列・切替は構成・互換は検査器で守る

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は planning へ issue で環流する（`feedback.yml` テンプレート）。

- 状態: **Accepted**
- 日付: 2026-09-11
- 決定者: Claude Code（実装）／ endazon

## 起点・関連

- 関連する計画書 ID: **`MSP/ADR-0029`**（同期通信の使い分け基準。🔴 本リポの裸の `ADR-0029` は資料再編であり別物）、
  **`MSP/ADR-0075`**（移行順序＝基盤先行の裁定）、`ADR-0001`（基盤再利用）、FR-17 / UC-06（全体前提条件の照会）
- 関連する実装仕様書: [`.ai-context/specs/20260911_745_configuration-assumptions-grpc.md`](../specs/20260911_745_configuration-assumptions-grpc.md)
- 前提: [IADR-0284](IADR-0284_east-west-grpc-scope-and-order-ruling.md)（射程 22 本・段 0〜6 の切り方）、
  [IADR-0328](IADR-0328_east-west-grpc-foundation-stage0.md)（段 0 の土台。**決定 4 が proto の置き場と
  「proto 互換検査器は段 1 へ移す」を、決定 5 が fail-safe 写像と「結合試験は段 1」を宣言している**）、
  [IADR-0063](IADR-0063_assumptions-versioned-resolution.md)（二段失効・fail-safe・安全既定）、
  [IADR-0264](IADR-0264_configurationservice-vsa-and-client-abolition.md)（`.Client` 廃止＝呼び出し元ごとの複製）、
  [IADR-0051](IADR-0051_service-to-service-auth.md)（s2s トークン）
- 関連 issue: #745（本件・段 1）、#584（傘・`Refs`。閉じない）、#526（`.Client` 廃止の起点）

## コンテキストと課題

段 0（IADR-0328）は土台だけを入れ、**gRPC を実際に話す経路は 1 本も無かった**。段 1 は
IADR-0284 決定 5 の表の 2 行目 —— **最初の 1 経路**（Configuration の `Assumptions` 同期照会）を通す。

射程・順序・一括移行の義務は IADR-0284 決定 1・2 と `MSP/ADR-0075` が固定済みで、本 ADR で緩めない。
決めるのは、**最初の proto を実際に書くときに初めて具体化する 8 点**である。

1. 提供側の面をどう置くか（評価器・認可を REST と共有するか）
2. **proto3 に `decimal` が無い**問題をどう写すか
3. 切替の鍵と既定（`Configuration:Grpc` の有無・既定 REST）をどう作るか
4. 切替の鍵に**不正な値**が書かれたときにどちらへ倒すか
5. proto 互換検査器を基盤からどこまで写すか
6. 「呼び出し元ごとの timeout / retry が効く」をどの粒度の試験で固定するか
7. 🔴 **proto を置く共有プロジェクトをどれにするか**（着手後の実測で判明した本リポ固有の制約）
8. 🔴 **protoc の生成 C# をカバレッジの分母に入れるか**（同じく着手後の実測で判明した）

## 検討した選択肢

| 論点 | 案 | 評価 |
| --- | --- | --- |
| 2（10 進） | **A（採用）: 線上は不変文化の 10 進文字列** | REST（`System.Text.Json` の `decimal`）と**同値**を保証できる。人が読める。線上が数バイト増える |
| | B: `double` | 🔴 `0.20315` が丸められる。**例外が 1 つも出ないまま**採算判定・費用上限判定の結果が REST と食い違う |
| | C: `int64` の最小単位（sub-units）＋ scale | 桁は保てるが**単位の合意が新しい暗黙知**になり、率（`0.0000001`）で scale がぶれる |
| | D: `google.type.Decimal` / well-known types | 該当する well-known type が無い（`google.type.Money` は通貨コード込みで率を表せない） |
| 4（不正値） | **A（採用）: 起動時に落とす（fail-loud）** | 段 0 の `Grpc:Port`（IADR-0328 決定 3）と同じ向き。綴り誤りが「gRPC が来ない」と区別できる |
| | B: REST へ黙って戻す | `Configuration:BaseUrl` の既存挙動とは揃うが、**切り替えたつもりで切り替わっていない**が観測できない |
| 5（検査器） | **A（採用）: 基盤の 852 行を判定ロジックそのままで移植し、走査の基点だけ直す** | 追随の費用が最小。基盤が実測で作った変異試験ごと写せる |
| | B: `buf` を導入する | 新しいツールチェーン（Go バイナリ・設定・CI キャッシュ）が増える。依存ゼロの検査器という本リポの作法から外れる |
| | C: 検査を置かない | 🔴 **proto の破壊的変更はコンパイルで止まらない**。ビルドは緑のまま古いピアだけが壊れる |
| 7（置き場） | **A（採用）: 共有プロジェクト `AiStockTrading.Shared.Grpc` を新設する** | Domain 到達可能性の縛りを受けない。名前空間も Domain の許可接頭辞から外れる |
| | B: `AiStockTrading.Shared.Contracts` へ置く（基盤の字義どおり） | 🔴 **アーキテクチャ検査が赤**（Domain から到達できる共有物に外部ライブラリが入る） |
| | C: `AiStockTrading.Shared.Infrastructure` へ置く（検査の失敗メッセージが示す先） | 設定管理・費用統制が同プロジェクトを参照していない。為替レート源ほかを巻き込む |

## 決定

### 決定 1 — 提供側は REST と同じ評価器・同じ認可を通す 1 つの `MapGrpcService` にする

`ConfigurationService/Features/Assumptions/GetAssumptions/GrpcEndpoint.cs` の
`AssumptionsGrpcService : Assumptions.AssumptionsBase` が **REST と同じ `AssumptionsService.GetCurrent()`** を呼ぶ。
認可はクラス属性 `[Authorize(Policy = OwnerOrService)]` ＝ REST の読み取りと**同一のポリシー**（IADR-0063 決定 2）。

- **評価器を 2 つにしない**（基盤の参照実装と同じ作法）。並走中に片方だけ直る形を作らない。
- **REST 面は 1 バイトも変えない**。撤去は段 6 の判断である。
- `Grpc:Port` 未設定なら h2c リスナは立たない（IADR-0328 決定 3）ので、**既定配備の挙動は変わらない**。
  `AddGrpc()` はリスナの有無に関わらず呼ばれる（そうしないと `MapGrpcService` が起動時に落ちる）。

### 決定 2 — 金額・率は線上で「不変文化の 10 進文字列」にする

proto3 に `decimal` は無い。`double` へ落とすと `0.20315`（譲渡益税率）や手数料率・上限額が 2 進浮動小数へ
丸められ、**同じ版なのに REST と gRPC で採算判定・費用上限判定の結果が変わり得る**。REST 側は
`System.Text.Json` が `decimal` を**桁を保ったまま** JSON 数値へ書いている。

- 線上は `string`。`ToString(InvariantCulture)` / `decimal.Parse(…, NumberStyles.Number, InvariantCulture)`。
- **空文字は proto3 の「未指定」であり `0` として読む**（REST の DTO 既定と同じ向き）。
- 🔴 **この写しは例外を出さない種類の誤りである。** 変異試験（`double` 経由へ変える）で落ちることを
  提供側の単体試験が固定する。**写しは提供側と呼び出し元の両方にあり、両方が同じ文字列を試験で名指しする。**
- 版の番兵は据え置き: **実在する版は 1 から**。`0` は未解決であり提供側は返さない・呼び出し元は取得不可として扱う。

### 決定 3 — 切替は呼び出し元の構成 `Configuration:Grpc` の有無だけで行い、既定は REST

`AssumptionsClientExtensions.AddAiStockTradingAssumptions` が**組み立て時に構成を読み**、
`IAssumptionsSource` を `GrpcAssumptionsClient`（宣言あり）か `HttpAssumptionsClient`（宣言なし）に据える。

| 構成キー | 既定 | 意味 |
| --- | --- | --- |
| `Configuration:Grpc` | 未設定 = **REST** | gRPC の宛先（例 `http://configuration-service:8081`） |
| `Configuration:GrpcTimeoutSeconds` | 5 | **試行ごとの** `CallOptions.Deadline`。REST の `HttpClient.Timeout` と同値 |
| `Configuration:GrpcMaxAttempts` | 1 | 試行回数。**既定は再試行しない**＝REST と同じ振る舞い |

- 🔴 **交換するのは `IAssumptionsSource` だけ**である。キャッシュ・TTL・二段失効・fail-safe
  （IADR-0063 決定 4/5）は共通のまま —— 切り戻しは**構成を外すだけ**でコードを変えない。
- 🔴 **再試行するのは `Unavailable` / `DeadlineExceeded` だけ。** `Unauthenticated` / `PermissionDenied` /
  `NotFound` / `InvalidArgument` は待っても変わらず、聞き直すぶんだけ**安全側既定へ倒れるまでの時間が延びる**
  （同期クリティカルパスである）。
- 🔴 **既定を「1 試行」にしたのは、トランスポートの差し替えで振る舞いを増やさないためである。**
  リトライは呼び出し元が明示的に `MaxAttempts` を上げたときだけ起きる（#584 が求めた
  「呼び出し元ごとの設定が実際に効く」の実体）。**gRPC 組み込みの retry ポリシー（`ServiceConfig`）は採らない**
  —— 段 0 の共通チャネル生成（IADR-0328 決定 1）へ手を入れずに済み、再試行の境界（試行ごとの deadline）が
  呼び出し元のコードとして読めるためである。
- s2s の資格情報が未整備のときは、**共有の `NoServiceAccessTokenProvider`**（IADR-0332 決定 6 / #746 が
  shim の `Foundation/Auth` へ置いた）へ倒す。**null 実装を呼び出し元ごとに書かない**
  （書かせると「未整備のとき例外」を書く実装が混ざる）。
- 🔴 **helm・compose の既定値は変えない。** 宛先を既定で書くと「既定は REST」が描画のバイト等価とともに崩れる。
  有効化するときは**提供側の `grpcPort` と呼び出し元の宛先を同じ変更で揃える**（片方だけだと常に安全側既定へ倒れる）。

### 決定 4 — 宣言してあるのに使えない宛先は起動時に落とす（`Configuration:BaseUrl` とは意図的に非対称）

`Configuration:Grpc` が絶対 URI でない・scheme が `http` でない場合は `InvalidOperationException` を投げる。
未設定・空・空白は「未宣言」であり REST（例外ではない）。

- `Configuration:BaseUrl` の不正値が `DefaultAssumptionsProvider` へ倒れるのは IADR-0063 決定 6 の**凍結済みの
  安全既定**であり、これを変えない。**新しい鍵だけを fail-loud にする。**
- 理由: 黙って REST へ戻ると「gRPC へ切り替えたつもりで切り替わっていない」が**綴り誤りと区別できない**。
  段 0 の `Grpc:Port`（IADR-0328 決定 3）が同じ理由で fail-loud を採っている。
- `https` を弾くのは、メッシュ内の TLS を**サイドカーが終端する**（アプリから見た線上は平文 h2c）ためである。

### 決定 5 — proto 互換検査器は基盤から判定ロジックそのままで移植し、走査の基点だけ直す

`scripts/check-proto-contracts.js`（基盤の同名スクリプト 852 行の移植）。**変えたのは 3 点だけ**である。

1. 走査の基点とパス規約: 基盤 `src/<unit>/backend/Shared/<Project>/Protos/…` → 本リポ
   `backend/Shared/<Project>/Protos/…`（ユニットリポジトリレイアウト。IADR-0046）。
2. ユニット除外（`lib/excluded-units.js`）の撤去 —— 本リポは単一ユニットで除外対象が無い。

3. `Protos/` 直下のユニット名を **allowlist** にした（`aistocktrading` ＝自リポ所有／`platform` ＝**基盤所有の
   契約の写し**）。基盤の検査器は「プロジェクトのユニット」と一致するかを見るが、本リポは**単一ユニットで
   ありながら基盤所有の契約を写して持つ**（IADR-0332 / #746 が `platform/llmgateway/v1/completion.proto` を
   入れた）ため、その形では表せない。🔴 **allowlist であって「何でも通る」ではない** —— 綴り誤りや
   新しいユニットの持ち込みは R1 で落とす（通すと所有者不明の契約が静かに増える）。

- baseline `scripts/proto-contract-baseline.json`・allowlist `scripts/proto-breaking-allowlist.json`（空）。
- CI（`ci.yml` の `static-checks`）に `--self-test` と本走の 2 ステップ。
- 🔴 **走査の基点を取り違えると「0 件で全件合格」になる。** 基点そのものが基盤と違う移植であるため、
  自己試験に**段 1 の proto を名指しで拾う陽性対照**を足し、allowlist の陰陽 2 件も置いた（基盤には無い 3 件）。
  「0 件走査で緑を返さない」ガードは基盤から写したまま残す。
- **本 ADR は kit / 基盤とのバイト一致を課さない**（乖離は受容する。`ADR-0029` 決定 6）。

### 決定 6 — 「呼び出し元ごとの timeout / retry が効く」は実 Kestrel h2c を立てた結合試験で固定する

`{CostControl,TradeDecision}Service.Tests` に**実 Kestrel の h2c 専用ポート**（`HttpProtocols.Http2` だけ・
`IPAddress.Loopback:0`）を立て、`AddAiStockTradingAssumptions(config)` を**構成から通して**往復させる。

- 🔴 **単体では足りない。** モックした生成クライアントでは `CallOptions.Deadline` が実際に打ち切ることも、
  再試行が**新しい RPC を投げ直す**ことも観測できない —— どちらも輸送の性質だからである。
  #584 が「単体ではなく結合で固定せよ」と書いたのはこの点である。
- 🔴 **Testcontainers を使わない**（Docker 不要）。既定 CI（`Category!=Integration`）の PR でそのまま走る
  —— 回収先の夜間ジョブへ送ると、`retry` の退行が**マージ後にしか分からない**。
- 対照: 陽性（応答すれば解決／`Unavailable` 1 回なら `MaxAttempts=2` で成功）と
  陰性（黙る提供側は deadline で安全側既定・**既定では再試行しない**・恒久的な失敗は 3 回設定でも 1 回だけ・
  再試行し切っても例外を出さない）。**呼ばれた回数を数える**（再試行の有無は回数でしか観測できない）。

### 決定 7 — proto の置き場は共有プロジェクト `AiStockTrading.Shared.Grpc`（`Shared.Contracts` ではない）

基盤の実装ガイドと IADR-0328 決定 4 は「proto はユニットの**共有契約プロジェクト**へ置く」と定める。
本リポジトリでそれを字義どおり `AiStockTrading.Shared.Contracts` に適用すると**アーキテクチャ検査が赤くなる**。

🔴 **実測（本 PR で一度そうして落とした）**:
`AiStockTrading.Architecture.Tests.SharedProjectDependencyTests` は
「**Domain から到達してよい共有プロジェクト**（`Shared.Contracts` / `Shared.Kernel`）が**外部ライブラリへ依存しない**」を
csproj の推移閉包で強制している（platform ADR-0030 §基本方針 / IADR-0256）。`Grpc.*` / `Google.Protobuf` を
足した瞬間に「Domain は .NET 標準のみに依存する」が迂回可能になり、検査が違反 2 件を出した。
**基盤にはこの制約が無い**（基盤の共有契約プロジェクトは Domain 到達可能性の縛りを持たない）。

したがって **`backend/Shared/AiStockTrading.Shared.Grpc/`** を新設し、proto と生成物だけを置く。

- 🔴 **名前空間も `AiStockTrading.Shared.Contracts.*` にしない。** `DomainSourceScan.IsAllowedDomainNamespace` は
  当該接頭辞を Domain へ許すため、`…Shared.Contracts.Grpc.*` と名乗ると**生成型を Domain から `using` できてしまう**
  （csproj 側の検査はプロジェクトが別なので掛からない）。ルートは `AiStockTrading.Shared.Grpc` とする。
  proto の `csharp_namespace` は `AiStockTrading.Shared.Grpc.<Service>.V<N>` であり、
  検査器 R1 の「`.Grpc.<Service>.V<N>` で終わる」を満たす。
- **`*.Client` の復活ではない。** サービス公開クライアント（`<Svc>.Client`）は禁止のままで
  （`ServiceClientProjectAbolishedTests` が守る）、本プロジェクトは**サービスに属さない共有の契約**である。
- **Tests プロジェクトを持たない**（手書きコードが 1 行も無い）。契約は proto 互換検査器と両端の試験が守る。
- 既存の閉包検査はこの分離を**そのまま守り続ける** —— 誰かが `Shared.Contracts` / `Shared.Kernel` から
  本プロジェクトを参照すれば、推移閉包に `Grpc.*` の `PackageReference` が現れて赤くなる。

**基盤との乖離として受容する**（`ADR-0029` 決定 6）。段 2 以降も、**本リポジトリが所有する** proto は
本プロジェクトへ置く。🔴 **基盤が所有する契約の「写し」はここではない** —— 消費する側のプロジェクトへ置く
（IADR-0332 / #746 が `Shared.Infrastructure` へ入れた）。**所有者が置き場を決める**という規約は同じであり、
どちらの形かは決定 5 の allowlist（`aistocktrading` / `platform`）が機械で区別する。

### 決定 8 — protoc の生成 C# はカバレッジの分母から外す（`coverage-floor.json` の 3 つ目の除外）

生成物は `obj/` に落ちるが、**カバレッジのレポートには載る**（実測: 段 1 の proto 1 本で `Assumptions.cs` /
`AssumptionsGrpc.cs` の **751 行・被覆 348 行＝46.34%**）。

🔴 **今の実測では床を割っていない**（除外前 **86.00%** / 除外後 **87.10%**・`lineRateFloor` は 0.83）。
**「割るから外す」ではない。** 外す理由は、分母に生成コードを残すと **ratchet が「テストを増やしたか」ではなく
「proto を何本置いたか」で動く**ことであり、段 2〜6 で proto が増えるほど効く。EF のマイグレーション生成物を
外した IADR-0143 と**同じ理由・同じ扱い**である。

- パターンは **ユニット名で引く**: `**/obj/**/aistocktrading/**/*.cs` と `**/obj/**/platform/**/*.cs`
  （`reason` は必須。G3）。🔴 **プロジェクト名で引かない** —— 生成物は `obj/<構成>/<TFM>/<unit>/…` へ
  `ProtoRoot` 相対で落ちるので、**置き場が変わっても追随する**（自リポ所有は `Shared.Grpc`／基盤所有の写しは
  消費側プロジェクト）。ユニット名の集合は決定 5 の allowlist と同じである。
  ［初版は `**/AiStockTrading.Shared.Grpc/obj/**/*.cs` の 1 本だったが、**#746 が持ち込んだ写しの生成物
  （計装 743 行・被覆 63 行＝8.48%）を取りこぼしていた**（AI レビューが検出）。同じ理由づけが写しにも
  等しく当たるので一般化した。実測: 一般化後 **87.76%**（24037/27390 行）・除外率 31.75%（12742/40132 行）。］
- **G4（除外は自動生成の部分集合）は素通りしない**: `findSourceFiles` は `obj/` を走査しないので、
  この除外が手書きを飲み込む経路は構造的に存在しない（かつ protoc の出力は `<auto-generated />` を持つ）。
- **`**/obj/**/*.cs` のような広いパターンにはしない** —— 将来ほかの生成物（EF・ソースジェネレータ）を巻き込む。
  **ユニットごとに 1 エントリ**とし、新しいユニットの proto を持ち込むときに**明示的に足させる**
  （検査器の allowlist を足す作業と対になる）。

## 理由

- **並走の正が REST のまま動かない。** 既定の構成を 1 つも変えていないので、段 1 のマージで
  挙動が変わるサービスは無い（切替は opt-in・切り戻しは構成の削除）。
- **静かに壊れる 2 つを機械で止めた。** 10 進の丸め（例外が出ない）と proto の破壊的変更
  （コンパイルが通る）は、いずれも**赤くならないまま結果だけが変わる**種類の誤りである。
- **消費者 0 の抽象を書いていない。** 段 0 が繰り延べた 2 点（互換検査器・結合試験）は、
  検査対象と往復経路が実在するこの段で初めて意味を持つ（IADR-0328 決定 4・5 の履行）。

## 結果

- 良い影響: 段 2 以降は「proto を足す → 提供側に `MapGrpcService` → 呼び出し元の `IAssumptionsSource` 相当を
  差し替える」の反復になる。**互換の後戻りは検査器が止める**ようになった。
- 悪い影響・トレードオフ:
  - **写像が 2 箇所にある**（提供側と呼び出し元 2 サービス）。`.Client` を廃した以上これは意図した複製だが、
    線上表現がずれると静かに壊れるため、**両側の試験が同じ文字列（`"0.20315"` 等）を名指しする**形で縛った。
  - **実配備での h2c 往復は未実測**（既定を変えていないため、稼働クラスタでは 1 度も走らない）。
  - 線上が数バイト増える（10 進を文字列で運ぶ）。同期照会 1 本・キャッシュ TTL 5 分の頻度では無視できる。
  - **共有プロジェクトが 1 本増えた**（決定 7）。基盤の「共有契約プロジェクトへ置く」からの乖離であり、
    基盤の実装ガイドを読んだ人が `Shared.Contracts` を探して見つけられない。proto 側と csproj 側の
    双方にその理由をコメントで置いた。
- フォローアップ:
  1. **段 2 以降**（Risk 読み取り → Audit → Report/MarketMonitor/CostControl → Notification 書き込み）を
     順に起票する。段 6（REST 撤去）まで #584 は閉じない。
  2. `Microsoft.Extensions.Http.Resilience` / `HybridCache` への置換（#584 併記）は依然として別 PR
     —— 本 ADR の retry は**呼び出し元の明示ループ**であり、ライブラリの置き換えではない。
  3. 実配備で有効化するときは、**提供側の `grpcPort` と呼び出し元の宛先を同じ変更で揃える**。

## 関連

- Supersedes: なし（IADR-0328 決定 4・5 が段 1 へ繰り延べた 2 点を**履行する**ものであり、覆さない）
- Superseded by: なし
