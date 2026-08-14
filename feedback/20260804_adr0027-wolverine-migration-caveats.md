---
title: Wolverine 移行の 3 つの罠（RuntimeCompilation 必須／既定設定が fan-out を壊す／既定が internal 実装型に依存するハンドラを落とす）— ADR-0027・ADR-0030・12_backend-application-stack への追記提案
type: plan-feedback
status: accepted
category: 新たな制約(ADR要)
related_ids:
  - NFR
  - ADR-0027 # platform（microservices-platform）側の計画 ADR
  - ADR-0028 # 同上（RabbitMQ 継続・Kafka 併用基準）
  - ADR-0030 # 同上（バックエンドアプリケーション層ライブラリ標準）
  - ADR-0013 # ai-stock-trading 側の計画 ADR（Wolverine 移行への追随）
  - IADR-0129 # 本リポの実装 ADR（本フィードバックの発生源）
  - IADR-0106 # 同上（#258 の再発防止。本移行で Superseded）
source_repo: ai-stock-trading
source_ref: "refactor/NFR-wolverine-migration / #354 / docs/specs/20260803_354_wolverine-migration.md §2.2・§3・§13・§13.13 / docs/adr/IADR-0129_wolverine-messaging-topology.md"
author: endazon (with Claude Code)
created: 2026-08-04
dispatched: true
planning_issue: 181
---

# フィードバック: Wolverine 移行で実測した 3 つの罠（起動失敗・fan-out の破壊・ハンドラの生成失敗）

> **送付済み（2026-08-04）。** 計画リポジトリへ `plan-feedback` ラベル付き Issue として起票した:
> [endazon/project-planning#181](https://github.com/endazon/project-planning/issues/181)。
> 宛先は計画リポジトリ `project-planning` であり、裁定の対象は同リポ内の
> `projects/microservices-platform/`（基盤側）の文書である。
> 以降のトリアージ・裁定は当該 Issue で行う。本書は実装リポジトリ側の控えである。

## 種別

新たな制約（ADR 要）。**ADR-0027 の決定そのものへの異議ではない**（Wolverine 移行は AST 側でも
[ADR-0013](../planning/projects/ai-stock-trading/07_adr/ADR-0013_messaging-follow-wolverine-kafka.md) として追随し、
全 10 サービスの移行を完了した）。報告するのは、**ADR-0027 の理由「移行手順を標準化できる」が前提とする
「標準手順」に、実測で判明した 3 つの必須事項が含まれていない**ことである。いずれも
**ビルドもユニットテストも緑のまま本番だけが壊れる**類の罠であり、基盤リポジトリの移行でも同じ経路を踏む。

## 起点となる計画書

- 機能要求（FR）: —（非機能。メッセージング基盤の移行）
- ユースケース（UC）: —（AST 側では UC-01・UC-02 の取引判断 → 発注の連鎖が影響を受ける）
- 画面（SC）: —
- 関連 ADR: **platform ADR-0027**（Wolverine 移行・Accepted）、platform ADR-0028（RabbitMQ 継続）、
  platform ADR-0030（バックエンドライブラリ標準）、ai ADR-0013（AST の追随）
- 計画書リンク:
  - `projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md`
  - `projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md`
  - `projects/microservices-platform/06_technical/12_backend-application-stack.md`（fixed・§Application 層／§Infrastructure 層のライブラリ表）

## 現状（計画書の記述 / As-Is）

ADR-0027 §理由:

> - MassTransit からの公式移行ガイドがあり、**移行手順を標準化できる**

ADR-0027 §結果（悪い影響 / トレードオフ）:

> - Wolverine の**ランタイムコード生成の挙動理解が必要**

`12_backend-application-stack.md` のライブラリ表に載っている Wolverine 関連パッケージは次の 3 つである。

| ライブラリ | 層 | 採否 |
| --- | --- | --- |
| Wolverine | Application | ★採用 |
| WolverineFx.RabbitMQ | Infrastructure | ★採用 |
| WolverineFx.Kafka | Infrastructure | ★採用 |

上記のいずれにも、次の 3 点は記載が無い。

1. `WolverineFx.RuntimeCompilation`（または事前コード生成）が**無いとホストが起動しない**こと。
2. Wolverine の**既定のルーティング規約が pub/sub（fan-out）を壊す**こと、およびそれを回避する必須設定。
3. Wolverine 6 の既定 `ServiceLocationPolicy.NotAllowed` が、**`internal` な実装型に依存するハンドラを
   「1 通目の受信時」に落とす**こと（起動もヘルスチェックもキュー宣言も consumer 接続も成功したまま、
   メッセージだけが無言で処理されない）。

## 問題点 / あるべき姿（To-Be）

**問題点**: 「移行手順を標準化できる」という理由は、標準手順に上記 3 点が含まれて初めて成立する。
1 は起動時に落ちるため**必ず気づく**（時間の損失で済む）。しかし **2 と 3 は気づかない**。ビルドは通り、
ユニットテストも緑で、ブローカにキューも作られ、consumer も付く。壊れるのは「どのプロセスがメッセージを
受け取るか」（2）と「受け取ったメッセージを処理できるか」（3）だけであり、
**例外もログも出ないまま（3 はサービスのログにだけ出る）業務イベントが消える**。
とくに 3 は、**トポロジを実ブローカで検査するテストを通過したうえで**壊れるため、
「キューがあり consumer も付いている」ことを根拠に正常と判断すると見逃す（AST で実際に見逃した）。

AST では同じ形の事故が MassTransit 時代に一度実際に起きている（AST #258 / [IADR-0106](../docs/adr/IADR-0106_consumer-endpoint-name-uniqueness.md)）。
取引判断イベントが承認・拒否・エラーのいずれにも現れず消失し、原因特定に時間を要した。
**Wolverine の既定では、その事故が「偶然」ではなく「構造的に必ず」起きる**（後述 2-a）。

**あるべき姿**: ADR-0027 の §結果 または §決定 に、移行の**必須設定**として 3 点を明記する。
併せて `12_backend-application-stack.md` のライブラリ表へ `WolverineFx.RuntimeCompilation` を追加する。

## 実装で判明した経緯

- AST は基盤の可変機能ユニットであり、ai ADR-0013 が platform ADR-0027 / ADR-0028 への追随を確定している。
- AST [#354](https://github.com/endazon/ai-stock-trading/issues/354)（MassTransit → Wolverine 移行）を 3 段階で実施し、
  **全 10 サービス**（メッセージングを持つ全サービス）の移行を完了した。
  記録: 作業仕様書 `docs/specs/20260803_354_wolverine-migration.md`、実装 ADR
  [IADR-0129](../docs/adr/IADR-0129_wolverine-messaging-topology.md)。
- 下記はいずれも **Wolverine 6.24.5 / net10.0 を実際に構成し、起動・トポロジを印字して確認した実測**である
  （ドキュメントからの推測ではない）。再現手順は作業仕様書 §2.3 に記載した。

### 1. `WolverineFx.RuntimeCompilation` が無いとホストが起動しない

既定の `TypeLoadMode.Dynamic` のまま `UseWolverine(...)` したホストは、**起動時に例外で停止する**。

> `Wolverine is running in TypeLoadMode.Dynamic, ... but no IAssemblyGenerator (Roslyn) is registered.
> Core WolverineFx no longer ships the runtime compiler.`

Wolverine 6 系はコア本体（`WolverineFx`）からランタイムコンパイラ（Roslyn）を**別パッケージへ分離**している。
ADR-0027 §結果 の「ランタイムコード生成の挙動理解が必要」はこの領域に触れているが、
**「別パッケージの参照が要る」という具体は書かれていない**ため、パッケージ表だけを見て移行すると必ず躓く。

回避策は 2 つあり、運用上の性質が異なる。

| 案 | 内容 | 代償 |
| --- | --- | --- |
| **A（AST 採用）** | `WolverineFx.RuntimeCompilation` を参照する | 起動時に Roslyn でコード生成する（起動時間・メモリ・コンテナサイズに影響） |
| B | `dotnet run -- codegen write` で事前生成し `TypeLoadMode.Static` | 生成コードをサービス数だけ版管理・再生成する運用が増える |

AST は第 1 段階で案 A を採った（[IADR-0129](../docs/adr/IADR-0129_wolverine-messaging-topology.md) 決定 6。
再評価条件＝起動時間・コンテナサイズが問題になった時点、または生成コードの管理コストが読める時点）。
**基盤側は本番のコンテナ起動特性の要求が AST より厳しい可能性があり、案 B を標準に据える判断もあり得る。
どちらを標準とするかは基盤側で決めていただきたい。**

### 2. 既定のルーティング規約が pub/sub（fan-out）を壊す

**2-a. キュー名がメッセージ型のみから導かれる（別サービスの購読が構造的に競合する）**

Wolverine の RabbitMQ conventional routing は、リスニングキュー名を**メッセージ型だけ**から導く
（`NamingSource.FromMessageType`。既定の識別子は `messageType.ToMessageTypeName()`＝namespace 込みの完全名。
実測で `typeof(int)` は `System.Int32` を返した）。**ハンドラのクラス名は一切関与しない。**

結果、**同じイベントを購読する別サービスは必ず同一キューを共有する**。RabbitMQ は 1 キューの複数 consumer へ
round-robin で配るため、pub/sub のつもりが **competing consumer（取り合い）へ退行**する。

- MassTransit の既定（キュー名＝consumer クラス名）では、これは「クラス名が偶然一致したときだけ」起きた。
  AST #258 はまさにその形（2 サービスが `TradeDecisionMadeConsumer` という同名クラスを持っていた）。
- **Wolverine では「必ず」起きる。**AST の実測では、2 サービス以上が購読するイベントが **21 型中 19 型**あり、
  既定のまま移行すれば 19 経路で同時に同じ事故が発生する状態だった。
- **MassTransit 時代の対策（クラス名をサービス跨ぎで一意にする）は完全に無効化される。**
  AST は IADR-0106 の改名（`TradeDecisionMadeBaselineConsumer`）を持っていたが、Wolverine では
  キュー名が同一になるため効果がゼロになる。**同じ対策を持つ基盤リポジトリでも同様に無効化されるはずである。**

**2-b. 既定の conventional local routing が発行をプロセス内に閉じ込める**

Wolverine の既定では、発行しようとしたメッセージ型に**自プロセス内のハンドラが存在すると、ルートが
ローカルキューだけになる**。実測（ルート解決結果の印字）:

```
routes for TradeDecisionMade: MessageRoute(local://tradedecisionmade/)     # 既定
（opts.Policies.DisableConventionalLocalRouting() を付けると RabbitMQ exchange の sender が選ばれる）
```

AST には該当箇所が現に存在した。**リスク統制サービスは `OrderApproved` を発行し、同時に `OrderApproved` を
購読している**（台帳計上・注文アクティビティ射影）。既定のまま移行すると、承認された発注が発行元プロセス内で
台帳に載るだけで**発注執行サービスへ一通も届かず、発注が一件も執行されない**。
`StopLossTriggered`（損切り）も同型である。**この退行はビルドもユニットテストも緑のまま起こる。**

**AST が採った対策**（[IADR-0129](../docs/adr/IADR-0129_wolverine-messaging-topology.md) 決定 1〜4）:

| 決定 | 内容 |
| --- | --- |
| 1 | リスニングキュー名を **`<ServiceName>.<メッセージ型名>`** にする（`QueueNameForListener`）。一意性の根拠を「人間が付けるクラス名」から、既に一意な `ServiceName`（OpenTelemetry / Serilog と同じサービス識別子）へ移す |
| 2 | **exchange は既定のまま**（メッセージ型ごとの fanout を共有）。`PrefixIdentifiers(...)` は exchange まで前置するため**採ってはならない**。発行側が自分専用の exchange へ送り、購読側が別 exchange を待つことになり、**退行 A を避けたつもりでもっと悪い形（誰にも届かない）になる** |
| 3 | **`DisableConventionalLocalRouting()` を全サービスで必須**とする（2-b の対策） |
| 4 | 決定 1〜3 を**共通ヘルパ 1 つに封じ込め**、サービス側の `Program.cs` はそれを 1 行呼ぶだけにする（トポロジの選択肢をサービス側に残さない）。素の `UseConventionalRouting(` / `ListenToRabbitQueue(` / `PrefixIdentifiers(` の直接呼び出しは静的検査（`scripts/check-consumer-endpoint-names.js` の N1〜N3）が CI で禁止する |

**補足（同じ「既定が意味を変える」系の実測。参考情報）**

| 事象 | 既定 | AST の対処 |
| --- | --- | --- |
| デッドレターキュー | **全キュー共有の `wolverine-dead-letter-queue` 1 本**に集約される | `<queue>_error` を明示指定（MassTransit 時代の「どのキューで失敗したか」が読める運用手順を保つ・IADR-0129 決定 5） |
| ハンドラ型の可視性 | **public でなければ受け付けない**。`Discovery.IncludeType(typeof(内部型))` で明示指定しても `Handler types must be public, concrete, and closed types` で拒否される（＝`InternalsVisibleTo` による回避は成立しない） | ハンドラ型だけを `public sealed` にする（IADR-0129 決定 9） |
| キュー名の総入れ替え | 移行でキュー名が**全部変わる**ため、旧キューが consumer 不在でブローカに残る | 削除 Runbook を用意（`docs/operations/wolverine-queue-cleanup-runbook.md`。AST では旧 47 本 → 新 45 本） |

### 3. 既定 `ServiceLocationPolicy.NotAllowed` が internal な実装型に依存するハンドラを 1 通目で落とす

**Wolverine 6 で既定が変わった項目**である（5.x は `AllowedButWarn`、6.x は **`NotAllowed`**）。
Wolverine はハンドラの実行コードを生成する際、依存を「生成コード内で `new` する」か
「`IServiceProvider` から解決する（service location）」かを、**依存の具象型が public かどうか**で決める。
`internal` な実装型は生成コードから `new` できないため**必ず** service location になり、`NotAllowed` は
これを `InvalidServiceLocationException` で拒否する。

```
Wolverine.Configuration.InvalidServiceLocationException: Found service locations while generating code
for Message Handler for <イベント型>, but ServiceLocationPolicy.NotAllowed is in effect.
  Concrete type <...>EfExecutedOrderStore is not public, so requires service location
```

**「インフラ実装は internal、外に出すのはポート（interface）だけ」という DI の書き方は広く使われており、
基盤の標準構成（IADR-0128 相当の層構成）でも自然に採られる。**その構成では移行後に**全ハンドラが**この状態になる。

危険なのは**失敗する時点**である。この検査はハンドラの実行コードが組み立てられる**1 通目の受信時**に走る
（`TypeLoadMode.Dynamic`）。したがって次がすべて成功したまま、処理だけが落ちる。

| 観測できるもの | 実測 |
| --- | --- |
| ホスト起動・ヘルスチェック | 成功 |
| キュー・DLQ の宣言（AutoProvision） | 成功 |
| consumer の接続（ブローカ上の consumer 数） | 成功（≥ 1） |
| 発行 → exchange → binding → キュー投入 | 成功（別ホストから投げるとキューに滞留する） |
| ハンドラの実行 | **失敗**（例外はサービスのログにのみ出る） |
| デッドレターキューへの退避 | **起きない**（チェーン組み立て前の失敗のため再試行・DLQ の対象にならない） |

AST では、この 1 点だけで**実基盤 E2E の「メッセージ配送を伴うテスト」が全滅**した
（8 件中 3 件失敗＝配送を伴う全件。症状は「発注が一件も執行されない」）。
一方、配送を伴わない 5 件（認可・DB 並行性ほか）は合格し、**トポロジ検査（キュー実在・consumer 数・DLQ 実在）も通過した**。

**AST が採った対策**（[IADR-0129](../docs/adr/IADR-0129_wolverine-messaging-topology.md) 決定 11）:
共通ヘルパで `options.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed` を設定する。
代替（実装型を全部 public にする）は内部実装隠蔽を壊すため採らなかった（AST では該当 25 型）。
`AlwaysUseServiceLocationFor<T>()` による型ごとの opt-in も、登録漏れが同じ静かな失敗を再発させるため採らなかった。

**再発防止の要点（基盤側でも同じことが要る）**: 「配線を名前で照合するテスト」（キュー名・型名・送信先 URI の固定）は
**ハンドラを一度も起動しないため本欠陥をすり抜ける**。AST は
「**internal な具象型に依存する public ハンドラを、共通配線したホスト上で実際に起動する**」テストを追加した
（ブローカ不要。外部トランスポートを stub にしても生成経路は同じ）。

## 提案（計画への反映案）

- 反映先候補: **ADR-0027 への追記**（主）／`12_backend-application-stack.md` のライブラリ表への 1 行追加（従）。
  **新 ADR は要さないと考える**（決定の変更ではなく、確定済みの決定を実施するうえでの必須事項の明記のため）。
  ただし提案 (1) で案 B（事前生成）を基盤標準に据える場合は、運用（生成コードの版管理）の決定を含むため
  新 ADR が適切かもしれない。

**(1) `WolverineFx.RuntimeCompilation` の明記**

- `12_backend-application-stack.md` §Infrastructure 層（または Application 層）のライブラリ表に 1 行追加:

  | ライブラリ | 用途 | 採否 | 備考 |
  | --- | --- | --- | --- |
  | WolverineFx.RuntimeCompilation | ハンドラのランタイムコード生成（Roslyn） | ★採用 | Wolverine 6 系はコア本体からコンパイラを分離しており、**無いと `TypeLoadMode.Dynamic` の既定で起動時に停止する**。代替は `codegen write` による事前生成＋`TypeLoadMode.Static` |

- ADR-0027 §結果 の「ランタイムコード生成の挙動理解が必要」に、**別パッケージ参照が必須である**旨を 1 文添える。

**(2) 既定が fan-out を壊すことと、必須設定の明記**

ADR-0027 §決定（または §結果）へ、次の趣旨を追記していただきたい。

> **移行時の必須設定**: Wolverine の RabbitMQ conventional routing は既定でキュー名を**メッセージ型のみ**から
> 導くため、同じイベントを購読する複数サービスが同一キューを共有し、pub/sub が competing consumer へ退行する。
> リスニングキュー名に**サービス名を前置**すること（exchange 名は既定＝メッセージ型の fanout のままとし、
> `PrefixIdentifiers` で exchange まで前置しないこと）。また既定の conventional local routing は、発行元プロセスに
> 同じ型のハンドラがあると発行をプロセス内へ閉じるため、**`DisableConventionalLocalRouting()` を全サービスで
> 適用**すること。これらは共通ヘルパへ封じ込め、逸脱を静的検査で禁止する。

- 併せて、ADR-0018（宣言的パイプライン）の「MassTransit 表記は Wolverine で読み替える」という備考について、
  **トポロジ生成の意味が読み替えでは吸収されない**（キュー名の導出規則が別物である）ことを注記いただけると、
  基盤側の実装者が「読み替えれば済む」と誤解しない。

**(3) 既定 `ServiceLocationPolicy` の明記**

ADR-0027 §結果 の「ランタイムコード生成の挙動理解が必要」へ、次の趣旨を添えていただきたい。

> Wolverine 6 の既定 `ServiceLocationPolicy.NotAllowed` は、**`internal` な実装型に依存するハンドラの
> 生成コードを拒否する**。層構成として「実装は internal・公開するのはポートだけ」を採る場合は、
> 共通配線で `ServiceLocationPolicy.AlwaysAllowed` を設定するか、実装型を public にするかを**移行時に決める**こと。
> 失敗は起動時ではなく**最初のメッセージ受信時**に起き、キュー宣言・consumer 接続・配送がすべて成功したまま
> 処理だけが落ちるため、トポロジの検査では検出できない。

**(4)（任意）移行チェックリストの共有**

AST の作業仕様書 §2.2（キュー名導出規則の新旧対応表・すべて実測）・§3（fan-out 経路の機械的列挙と保存設計）・
§13.13（実基盤 E2E の失敗から根本原因までの診断経路）は、
基盤側の移行でもそのまま使える形になっている。必要であれば計画側の技術文書へ転記いただいて構わない。
とくに「**移行前に、イベント型 → 購読サービスの対応表を機械的に作り、移行後もそれが保存されることを
検査可能にする**」という手順は、退行 A・B の両方に効く。

## 影響範囲

- **microservices-platform 側**:
  - 文書: ADR-0027 への追記、`12_backend-application-stack.md`（fixed）のライブラリ表への 1 行追加。
  - 実装: 基盤実装リポの移行がまだであれば、**移行前に本件を反映しておくと事故を 1 回分避けられる**。
    既に移行済みであれば、(2-a)(2-b) に該当する経路がないかの**点検**をおすすめする
    （点検の勘所: ① 同一イベントを 2 つ以上のサービスが購読しているか ② 発行元サービス自身が同じ型を
    購読しているか。①②のいずれかがあり、キュー名にサービス識別子が入っていなければ該当する。
    ③ ハンドラの依存に `internal` な実装型があるか。あれば `ServiceLocationPolicy` の決定が要る）。
  - ADR-0028（RabbitMQ 継続）には影響しない。Kafka 併用時のトポロジは本件の範囲外である
    （Kafka はトピックとコンシューマグループの概念が別であり、同じ結論にはならない）。
- **ai-stock-trading 側**: 追加作業なし。対策は [IADR-0129](../docs/adr/IADR-0129_wolverine-messaging-topology.md) として
  実装・検査器・実 broker E2E まで揃っており、#354 で完了している。
  基盤側が別の標準（例: 案 B の事前生成、あるいは別のキュー命名規則）を採る場合は、
  ai ADR-0013（基盤へ追随する）に従って AST 側を合わせる必要があるため、**裁定の結果を AST へ戻していただきたい**。
- **判定基準への影響**: 本件が未反映のあいだ、「ADR-0027 に沿って移行した」という主張は
  **fan-out が保存されていることの確認も、ハンドラが実際に実行できることの確認も含まない**。AST では
  `scripts/check-consumer-endpoint-names.js`（静的検査・N1〜N3）と `Category=Integration` の実 broker E2E
  （1 通の発行が購読する全サービスへ届くことを受動宣言と実配送の 2 段で確認）を判定条件にしている。
  **さらに罠 3 を受けて、「ハンドラの生成コードを実際に通す」ユニットテストを判定条件に加えた**
  （名前の照合だけでは、キューも consumer も揃ったまま処理が落ちる状態を緑と判定してしまうため）。
