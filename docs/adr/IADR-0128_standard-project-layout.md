---
title: IADR-0128 標準プロジェクト構成は「Worker を Api / Infrastructure に割り、実体のある層だけを作る」形で実現する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0001
  - IADR-0046
  - ADR-0030 # platform（microservices-platform）側の計画 ADR
  - ADR-0019 # 同上（ユニット第一構成）
author: endazon (with Claude Code)
created: 2026-08-03
updated: 2026-08-03
plan_refs:
  - ../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md
  - ../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md
  - ../../planning/projects/microservices-platform/07_adr/ADR-0019_unit-first-repo-structure.md
---

# IADR-0128: 標準プロジェクト構成は「Worker を Api / Infrastructure に割り、実体のある層だけを作る」形で実現する

- 状態: Accepted
- 日付: 2026-08-03
- 決定者: endazon（方針）/ Claude Code（実装詳細の起案）

## 起点・関連

- 関連する計画書 ID: platform **ADR-0030**（アプリ層ライブラリ標準）・
  [12_backend-application-stack](../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md)（fixed・§プロジェクト構成）・
  platform ADR-0019（ユニット第一構成）・AST ADR-0001（platform 再利用）
- 関連する実装仕様書: [作業仕様書 20260803（#353）](../specs/20260803_353_standard-project-layout.md)
- 関連 issue: [#353](https://github.com/endazon/ai-stock-trading/issues/353)（親 [#345](https://github.com/endazon/ai-stock-trading/issues/345) / [#344](https://github.com/endazon/ai-stock-trading/issues/344)）
- 既存決定: [IADR-0001](IADR-0001_repo-structure-and-stack.md)（規約は基盤実装リポに揃える）・
  [IADR-0046](IADR-0046_unit-repo-layout.md)（`backend/Services/<Svc>/{src,tests}` レイアウト）

## コンテキストと課題

platform 12_backend-application-stack（fixed）は、サービス単位のプロジェクト構成を
`Api / Application / Domain / Infrastructure / Contracts / SharedKernel / Tests` と定めた。
現行は `Domain / Application / Worker` の 3 プロジェクトであり、**標準に `Worker` という単位が無い**。

標準へ揃えるにあたり、文面だけでは決まらない論点が 3 つある。

1. **`Worker` の中身をどう割るか**。現行の Worker には Program.cs・HTTP エンドポイント・EF Core の
   DbContext と Migration・メッセージング consumer・外部 API アダプタ・定期実行サービスが同居している。
2. **7 プロジェクトを常に作るのか、実体があるものだけ作るのか**。`SharedKernel`（Result 型の置き場）は
   本リポジトリに該当する実体が無い。`Contracts` は既に**ユニット単位で 1 つ**
   （`backend/Shared/AiStockTrading.Shared.Contracts`）存在し、サービス間で共有されている。
3. **標準に無いプロジェクト（`ConfigurationService.Client`）をどう扱うか**。

加えて、本再配置は 11 サービス・79 プロジェクトに及ぶ最大規模の移動であり、
**振る舞いを変えないこと**（テスト合格数 2256 の完全一致）が受け入れ条件である。

## 検討した選択肢

### 論点 1: Worker の分割

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A: ホスト＝Api、技術詳細＝Infrastructure（採用） | Program.cs・appsettings・`Foundation/Endpoints` のみ Api、残りは Infrastructure | ○ 規則が限定列挙で灰色地帯が無い。ADR-0030 の Api の定義（エンドポイント・DI 構成・ProblemDetails 変換）と一致 |
| B: Worker を丸ごと Api へ改名 | 1 コマンドで済む | × EF Core・consumer が Api に残り、**層の分離という目的を果たさない**（名前だけ標準になる） |
| C: Worker を丸ごと Infrastructure へ改名し Api を作らない | 同上 | × ホストが Infrastructure を名乗る。`Api` が欠落する |
| D: consumer を Api に置く（「メッセージの入口も入口である」） | — | × メッセージング技術に張り付いた実装であり、#354（Wolverine 移行）でハンドラが Application へ上がる余地を潰す |

### 論点 2: 空プロジェクトの扱い

| 案 | 内容 | 評価 |
| --- | --- | --- |
| E: 実体があるものだけ作る（採用） | SharedKernel は作らない。Contracts はユニット単位の 1 つで満たす | ○ ADR-0030 の但し書き「過度な共通化は避ける」・選定基準 2「標準機能優先」と整合 |
| F: 11 サービス × 7 を常に作る | 文面の見た目に最も忠実 | × 空の SharedKernel 11 個・空の Contracts 11 個が増える。**構成は標準に見えるのに規律は無い**という最悪の状態。restore/build/CI 時間も増える |
| G: SharedKernel を作り、そこに Result 型も同時に導入する | 実体を伴う | × 例外ベースの現行コードの広範な書き換え＝**振る舞いの変更**。合格数一致の受け入れ条件と両立しない |

### 論点 3: `ConfigurationService.Client`

| 案 | 内容 | 評価 |
| --- | --- | --- |
| H: そのまま残す（採用） | 標準外の第 8 のプロジェクトとして明示的に許容 | ○ 中身（HTTP クライアント・キャッシュ・fail-safe・DI 拡張・`AssumptionsChanged` consumer）が変わらない |
| I: `Contracts` へ改名 | 見た目は標準に収まる | × Contracts は「公開契約（proto・イベント・DTO）」の置き場。HTTP クライアントと consumer を入れると定義が壊れる |
| J: `Infrastructure` へ畳む | 標準に収まる | × 他サービスが参照する公開物が、設定サービス**内部**の技術詳細と同居する。IADR-0063 決定 3 が 1 箇所へ集約した意図（消費側が同じキャッシュ・無効化・fail-safe を書き写さない）を失う |

## 決定

1. **Worker は「ホスト」と「技術詳細」に割る（案 A）。** 振り分けは**限定列挙**とする。
   `Program.cs` / `appsettings*.json` / `Foundation/Endpoints/**` の 3 種類だけが **Api**、
   **それ以外はすべて Infrastructure**。判断を要する灰色地帯を作らない。
2. **実体があるプロジェクトだけを作る（案 E）。** 本 issue の時点で各サービスが持つのは
   `Api` / `Application` / `Domain`（実体があるサービスのみ）/ `Infrastructure` の 4 つ。
   - **`SharedKernel` は作らない。** ADR-0030 は SharedKernel を「Result / Error・共通基底
     （**過度な共通化は避ける**）」と定義しており、Result 型を導入していない現時点で作れば
     但し書きが避けよと言っている当のものになる。
   - **`Contracts` はユニット単位で 1 つ**（`AiStockTrading.Shared.Contracts`）を正とする。
     platform ADR-0019 決定 4「ユニット固有のイベント契約はユニット側の契約プロジェクトに置く」に従う。
     AST 自体が 1 つの可変機能ユニットであり、サービス間で共有されるイベント契約
     （`OrderApproved` は発注執行とリスク統制の双方が使う）は per-service Contracts では置き場を失う。
3. **命名は「層セグメントの置換 1 回」で導く。** フォルダ・アセンブリ名は `<Svc>.<Layer>`、
   名前空間は `AiStockTrading.<Short>.<Layer>[.<既存の下位階層>]`。`<Short>` は既存の `RootNamespace` から
   読み、新たに決め直さない。`Foundation` / `Composable` の下位階層は**変えない**
   （platform ADR-0018 の固定/可変区分に対応する既存の意味づけであり、本再配置と直交する）。
4. **テストプロジェクトは本番プロジェクトと 1:1 に保つ。** `<Svc>.Worker.Tests` は
   `<Svc>.Api.Tests` と `<Svc>.Infrastructure.Tests` に割る。テスト本文は `namespace` 行と `using` 行以外
   1 文字も変えない（表明の変更は本 issue で禁止）。
5. **`ConfigurationService.Client` は標準外の第 8 のプロジェクトとして残す（案 H）。**
   「サービスが他サービスへ公開するクライアントライブラリ」は 7 標準のどの層にも当たらない。
6. **Domain 層の外部依存ゼロは csproj の静的解析で機械的に強制する**
   （`backend/Tests/AiStockTrading.Architecture.Tests`）。検査は
   (1) Domain の `PackageReference` が 0 件 (2) `ProjectReference` が許可リスト
   （`*.Domain` / `*.SharedKernel` / `AiStockTrading.Shared.Contracts`）内 (3) **推移閉包上のすべての
   プロジェクトも `PackageReference` 0 件** (4) 発見数が 9 件以上（空振り防止）の 4 点。
7. **本再配置ではライブラリ標準（Riok.Mapperly / FluentValidation / Polly / ProblemDetails）を適用しない。**
   いずれも新規導入または既存コードの書き換えであり、受け入れ条件「再配置前と同一の合格数で green」と
   両立しない。後続 issue へ分割して起票する。

## 理由

- **限定列挙（決定 1）が要る**のは、11 サービス・多数のフォルダを別々のセッション（別エージェント）で
  移すためである。「だいたいこう」で運用すると、サービスごとに Api の中身が食い違い、
  結果として**標準に揃っていない状態が「揃った」ことになる**。
- **空プロジェクトを作らない（決定 2）**根拠は ADR-0030 の文面そのものにある。§プロジェクト構成の
  SharedKernel 行の但し書き、§決定 の選定基準 2（標準機能優先＝依存と構成要素を増やさない）、
  および「Result = SharedKernel の自前実装」という定義（＝SharedKernel は Result のための場所）である。
  3 つとも同じ方向を指している。
- **推移閉包まで見る（決定 6-(3)）**のは、検査 (1) だけでは `AiStockTrading.Shared.Contracts` へ
  EF Core を足す形の迂回を素通りさせるためである。Domain の依存規律は「Domain の csproj に何が書いてあるか」
  ではなく「Domain から到達できる範囲に何があるか」で決まる。
- **発見数の下限検査（決定 6-(4)）**は、探索が壊れて 0 件になったときに検査が**無条件に成功する**のを防ぐ。
  検査器そのものが静かに失効する経路を塞ぐという点で、[IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md) の
  「登録済み逸脱は実際に逸脱している」検査と同じ性質である。
- **ライブラリ標準を切り離す（決定 7）**のは、ProblemDetails 化が HTTP 応答本文を変え、
  マッピングの置換が生成コードの差を持ち込むためである。再配置と混ぜると、
  「テストが落ちたのは移動のせいか書き換えのせいか」が切り分けられなくなる。

## 結果

- 良い影響:
  - 層の境界がプロジェクト境界と一致し、**依存規律を機械検査できる**土台ができる
    （Domain は本 IADR で強制、Application は後続で追加可能）
  - EF Core・メッセージング・外部 API アダプタが Infrastructure に集まり、#354（Wolverine 移行）の
    影響範囲が 1 プロジェクトに収まる
  - Api（ホスト）が 3 種類のファイルだけになり、Program.cs の配線がレビューしやすくなる
- 悪い影響 / トレードオフ:
  - 大きな（ただし機械的な）移動差分が発生する。プロジェクト数は 79 → 102 に増える
    （**起草時の見積。全段階完了後の実測は 76 → 99**＝`backend/backend.slnx` の `<Project Path=` 実測値。
    作業仕様書 §8 のプロジェクト数表を正とする）
  - `docker-compose.yml` / `scripts/k8s-local-images.sh` / `scripts/validate-runtime-scaffold.js` の
    `<Svc>.Worker` → `<Svc>.Api` 追随が要る。移行中は**新旧レイアウトが混在**するため、
    `validate-runtime-scaffold.js` は両対応にする
  - 標準の 7 のうち 2 つ（Contracts の per-service 版・SharedKernel）が存在しない状態が残る。
    「標準に揃っている」の判定は本 IADR の決定 2 を併せて読む必要がある
- フォローアップ:
  1. 基盤リポ（microservices-platform）実装の実構成と突合し、per-service `Contracts` / `SharedKernel` の
     要否を確認する（IADR-0001 が「揃える先は基盤実装リポ」と定めているため）
  2. Result / Error 型（SharedKernel）の導入を独立 issue で起票する
  3. ライブラリ標準（Mapperly / FluentValidation / Polly / ProblemDetails）を 4 件に分けて起票する
  4. Application 層の依存規律の機械検査を追加する（現行の成立を実測したうえで）
  5. Domain → 他サービス Domain の参照 4 件の扱いを、サービス境界（platform ADR-0002）の観点で再検討する

## 関連

- Supersedes: なし（[IADR-0046](IADR-0046_unit-repo-layout.md) の `backend/Services/<Svc>/{src,tests}` レイアウトは
  そのまま有効であり、本 IADR はその `src/` 配下のプロジェクト割りを定める）
- Superseded by: なし
