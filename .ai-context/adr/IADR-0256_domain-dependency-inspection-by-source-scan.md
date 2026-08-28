---
title: IADR-0256 Domain 層の依存規律はソース走査でも検査し、csproj 方式と二重化する
type: impl-adr
status: Accepted
related_ids: [NFR]
author: endazon (with Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-library-selection.md
---

# IADR-0256: Domain 層の依存規律はソース走査でも検査し、csproj 方式と二重化する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 起点 ID: **`NFR`（無採番）**。検査器の追加＝メタ作業であり、`.claude/rules/traceability.md` の
  無採番許容ケース 2 に当たる。計画の非機能要件表（`NFR-01`〜`NFR-17`）を実際に読み、
  本作業に当たる番号が無いことを確認した（判断の全表は作業仕様書に残した）。
- 守る制約: **platform ADR-0030 §基本方針「Domain 層は外部ライブラリへ依存しない（.NET 標準のみ）」**、
  **platform ADR-0019 決定 4**（ユニット単位の契約プロジェクト）。**制約そのものは本 ADR で変えない。**
- 関連する実装仕様書: [20260828_w9f1_architecture-tests-dual-inspection](../specs/20260828_w9f1_architecture-tests-dual-inspection.md)
- 関連 IADR: [IADR-0128](IADR-0128_standard-project-layout.md)（標準プロジェクト配置。決定 3 の
  `<Svc>.<Layer>` 命名が現行検査の唯一の識別子であり、決定 6 が本検査の元になった）、
  [IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md)（既知逸脱を明示レジストリで
  持ち、解消したら削除させる作法。決定 5 はこれに倣う）

## 背景・課題

Domain 層の依存規律は現在 `AiStockTrading.Architecture.Tests` の
`DomainLayerDependencyTests` が **csproj の静的解析**で強制している。層を識別する情報は
**プロジェクト名の接尾辞 `.Domain`** ただ 1 つである。

Vertical Slice Architecture への移行（1 サービス = 1 プロジェクト化）を行うと、**この識別子が消滅する**。
結果として起きるのは「テストが落ちる」ことではない——**検査対象が 0 件になり、
`violations.Should().BeEmpty()` が中身を見ずに緑になる**。**失効が失敗メッセージに現れない。**

これは本リポジトリが繰り返し扱ってきた劣化の形であり（`check-consumer-endpoint-names.js` が
パス正規表現に合わないファイルを黙って `continue` する件と同型）、移行そのものより
**移行によって静かに失われる保証**のほうが重い。

## 検討した選択肢

| 案 | 評価 |
| --- | --- |
| **A: `Domain/` 配下のソース走査（採用）** | 新規パッケージ依存が 0。既存テスト（文字列走査・ソース解析）と同じ作法。「被検査コードを `ProjectReference` しない」という現行 csproj のコメントが明示する設計方針を保てる。失敗メッセージが「どのファイルのどの `using` か」を直接指せる |
| B: NetArchTest / ArchUnitNET | 新規パッケージ依存が要り（ADR-0030 の選定基準にライセンス持続性がある）、アセンブリを読むために**被検査コードへの参照が必要**になる。1 アセンブリ化後は「Domain 型が EF Core 型を参照しない」を**拒否リスト**で書くことになり、新しいライブラリが入った瞬間に素通りする（現行は許可リストで fail-closed） |
| C: 自作 Roslyn アナライザ | 作成・配布・バージョン管理のコストが検査 1 件に対して過大 |
| D: Domain を別 csproj のまま残す | 利用者確定の「1 サービス = 1 プロジェクト」に反する |

## 決定

### 決定 1: 旧方式を消さず、**二重化**する

ソース走査は**コンパイラより弱い**。`global using`（`ImplicitUsings` が入れる `System.*`）と
ソースジェネレータが生成した参照は見えない。csproj 方式は「宣言」を見るためこの点で強い。
**どちらも単独では十分でないので、移行期間中は両方を走らせる。**
旧方式の退役は移行完了後の仕上げ PR で、**新方式が 11 サービスを数えていることを確認してから**行う。

### 決定 2: 検査対象の単位は csproj ではなく **Domain ソース領域**（ディレクトリ）とし、新旧の形の**和集合**で数える

現行 `backend/Services/<Svc>Service/src/<Svc>Service.Domain/` と
移行後 `backend/Services/<Svc>Service/Domain/` の**両方**を数える。
移行は 1 サービスずつ進むため期間中は必ず混在し、**片方だけを数えると移行が進むほど検査対象が痩せる**。
下限 9 件（Audit / Notification は Domain を持たない）は移行の前後で成立する。

### 決定 3: 検査は 5 つ。(b) は **許可リスト**、(c) は **CPM から導いた母集合**

| # | 検査 | 方式 |
| ---: | --- | --- |
| (a) | 探索が空振りしていない | Domain ソース領域 ≥ 9 |
| (b) | `using` の許可リスト | `System[.*]` / `AiStockTrading.<任意>.Domain[.*]` / `AiStockTrading.Shared.Contracts[.*]` / `AiStockTrading.Shared.Kernel[.*]` |
| (c) | 完全修飾での迂回を塞ぐ | 禁止トークンがソースに現れない |
| (d) | 他サービスの名前空間を参照しない | 既知の逸脱を除く |
| (e) | 共有プロジェクトの csproj 静的解析（存続） | `Shared.Contracts` / 将来の `Shared.Kernel` とその推移閉包の `PackageReference` が 0 件 |

🔴 **(c) の禁止トークンを手で書かない。** `Directory.Packages.props` の `PackageVersion Include=` から
機械的に導く。拒否リストを手で書くと、**次に足されたパッケージが素通りする**。

### 決定 4: (c) の母集合は **2 系統**にする（CPM だけでは足りない）

パッケージ ID と名前空間の根は一致しないことがある。**実測: パッケージ `WolverineFx` の名前空間は
`Wolverine`** である。ID だけから導くと `Wolverine.IMessageBus` の完全修飾参照が (c) を素通りする。

CamelCase 分割で補う案は退けた —— `OpenTelemetry` → `Open`、`SSH.NET` → `S` / `SS` のような
**危険なほど短いトークン**を生み（実測で 68 トークン中に `S` / `Asp` / `Open` / `Rabbit` が現れた）、
`quote.Open.Value` のような正当な記述を誤検出する。

代わりに **リポジトリ全体の `using` が実際に import している名前空間の根**を第 2 の母集合とする。
**これも走査由来であり、手書きの拒否リストではない。** 実測 63 トークン
（CPM 由来 57 ＋ 実 import の根 14 を重複排除）。

照合は **修飾名の先頭としての出現**に限る（直前が `.` または識別子文字なら一致とみなさない）。
名前空間の根が修飾名の途中に現れることはなく、この制約が正当なメンバアクセスの誤検出を防ぐ。

### 決定 5: 既知の逸脱は**ファイル単位の明示一覧**で許容し、**一覧が腐ったら落ちる**ようにする

Domain から他サービスを参照している箇所が現時点で **5 ファイル**ある
（`BacktestCostModel.cs` / `Stage0Promotion.cs` / `CostGovernor.cs` / `LlmUsageRecord.cs` / `PnlAggregator.cs`）。
`AiStockTrading.Shared.Kernel` の新設で解消される前提であり、それまでは一覧で許容する。

🔴 **一覧に無い違反が増えたら落ちる**だけでなく、**一覧の行が解消済みになっても落ちる**。
許容一覧は「増やすほど検査が弱くなる」ものであり、解消済みの行が残り続けると
**本当に増えたときに区別が付かなくなる**。

> **設計時の数え（4 件）と実測（5 ファイル）が食い違った。** 設計は csproj の `ProjectReference` を
> 数えており（プロジェクト間の辺 4 本）、代表ファイルを 1 つずつ挙げていた。
> **検出はファイル単位で行うため、一覧もファイル単位で持つ。**
> 母集合を自分で引き直したから気付けた差である。

### 決定 6: 「0 件検査で緑」を**構造的に**防ぐ

(a) の下限に加え、走査した母集合が空でないことを個別に表明する
（ファイル 120 ≥ 100 / `using` 80 ≥ 60 / 禁止トークン 63 ≥ 30 / 共有プロジェクト ≥ 1）。
さらに **トークン導出の 2 系統が両方効いていること**を、`Microsoft.EntityFrameworkCore`（CPM 由来）と
`Wolverine`（実 import 由来）の双方を含むことで対に押さえる。件数の下限だけでは片系統の故障に気付けない。

### 決定 7: 照合器はすべて**純関数として切り出し**、否定形を対で置く

実ツリーの違反は現時点で 0 件である。**照合器が常に「違反なし」を返すよう壊れても、
実ツリー走査のテストは緑のままである。** `using` パーサ・許可判定・トークン照合器・他サービス照合器・
CPM 解析器のそれぞれについて、肯定形と否定形を `[Theory]` で固定する。

加えて、**実ツリーへ一時的に違反を仕込んで実際に赤くなること**を 6 種類（(a)〜(e) ＋ 一覧の腐り）
について実測し、元へ戻したことを確認した。

## 影響

- **本番コードは 1 行も変わらない。** プロジェクト構成も変わらない。
- テストケースは 19 → 66（テストメソッド 12 → 29）。既存 12 メソッドは変更していない。
- `RepositoryLayout` に `DomainSourceDirectories` / `SharedProjectFiles` / `ServiceShortName` が増えた。
  `DomainProjectFiles` は変えていない。
- 推移閉包の探索が 2 箇所に存在する（旧 `DomainLayerDependencyTests` と新 `SharedProjectDependencyTests`）。
  **意図した重複である** —— 旧方式を退役させた瞬間に (e) の検査まで一緒に消えてはならない。

## 残余リスク

- **ソース走査はコンパイラより弱い**（`global using`・ソースジェネレータ）。現時点では旧方式が併走して塞いでいるが、
  **旧方式を退役させると穴が開く**。退役の判断は仕上げ PR で行い、そのとき改めて記録する。
- **フォルダ名 `Domain` に依存する。** 改名すれば空振りする。(a) の下限が捕まえる。
- **(c) の第 2 母集合はリポジトリの現状に依存する。** 誰も import していない外部名前空間はトークンに入らない。
  穴は「まだ 1 度も使われていないライブラリ」に限られる。
- 既知の逸脱 5 件は本 ADR では**解消しない**。解消は `Shared.Kernel` 新設の PR が担う。
- 🔴 **(c) / (d) はコメントも含めて全文を走査する。** 検出を fail-closed にするための意図した設計である
  （完全修飾での迂回はコメントと構文上区別できず、区別しようとするとパーサを持つことになる）。
  **帰結として、Domain 層のソースへ「このライブラリは使わない」「あのサービスは参照しない」といった
  禁止対象の名前を含む説明コメントを書くと、誤検出で CI が赤くなる。**
  回避は「名前を書かずに説明する」ことである（本 PR 自身が `McpExposureNotDeclaredTests` で同じ制約に当たり、
  クラス名を書かない文へ書き換えて解消した）。**この赤は検査の欠陥ではなく、fail-closed の代償として受け入れる。**
  次にこれで赤くなった人が原因を探さずに済むよう、ここへ明記する。
