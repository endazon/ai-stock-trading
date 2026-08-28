---
title: IADR-0264 ConfigurationService を単一プロジェクト＋VSA 樹形へ移送し、ConfigurationService.Client を廃止する（11 本の 2 本目・Domain を持つ場合の型）
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0259, IADR-0260, IADR-0261, IADR-0263, IADR-0063, IADR-0128]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
---

# IADR-0264: ConfigurationService を単一プロジェクト＋VSA 樹形へ移送し、`ConfigurationService.Client` を廃止する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-08-29
- 決定者: endazon（方針・[IADR-0259](IADR-0259_single-project-vsa-structure.md) の利用者裁定）/
  Claude Code（移送の実施と、`.Client` の処遇・`Domain` を持つサービスの型の起案）

## 起点・関連

- 起点 ID: **`NFR`（無採番）**。構造移送＝メタ作業であり、`.claude/rules/traceability.md`
  「起点 ID の種別」の無採番許容ケース 2 に当たる（[IADR-0259](IADR-0259_single-project-vsa-structure.md)
  が確定済みの判断を継承する。環流はしない）。
- **解消する issue**: [#526](https://github.com/endazon/ai-stock-trading/issues/526)
  （`ConfigurationService.Client` が標準構成から逸脱している）。**gRPC 化を除く**（後述「結果」）。
- 関連する実装仕様書:
  [20260829_w11s4b_configurationservice-vsa](../specs/20260829_w11s4b_configurationservice-vsa.md)
- 上流: [IADR-0259](IADR-0259_single-project-vsa-structure.md)（樹形・決定 2・決定 9）・
  [IADR-0263](IADR-0263_auditservice-vsa-migration-first-of-eleven.md)（1 本目で確定した 5 決定）・
  [IADR-0260](IADR-0260_shared-kernel-for-cross-service-domain-types.md)（共有カーネルの憲章）・
  [IADR-0261](IADR-0261_namespace-alignment-to-platform.md)（名前空間の先行整合）

## コンテキストと課題

[IADR-0263](IADR-0263_auditservice-vsa-migration-first-of-eleven.md) が確定した 5 決定は、
**AuditService の実態（`Domain/` を持たない・集約が 1 つ・他サービスから参照されない）に最適化した**
ものであり、同 ADR 自身が「次のサービスがこの前提を満たさない場合は機械的に流用しない」と
明記している。ConfigurationService には**同 ADR が想定していない事情が 2 つ**ある。

1. **`ConfigurationService.Client` を持つ。** 他サービスへ公開するクライアントライブラリであり、
   1 サービス = 1 プロジェクトでは置き場が無い（[IADR-0259](IADR-0259_single-project-vsa-structure.md) 決定 2）。
   **参照している csproj は 4 本 / 2 サービス**（`TradeDecisionService.{Api,Infrastructure}` /
   `CostControlService.{Api,Infrastructure}`）で、**その 2 サービスはまだ移送していない**。
2. **`ConfigurationService.Domain` を持つ**（AuditService は持たなかった）。

着手前の走査で、**この 2 つは独立ではない**ことが分かった。`.Client` は `ConfigurationService.Domain`
を `ProjectReference` して `VersionedAssumptions` を得ており、**単一プロジェクト化で
`ConfigurationService.Domain.csproj` が消えると、`.Client` からも呼び出し元からも到達できなくなる。**
すなわち **`VersionedAssumptions` の置き場を動かすことは、`.Client` を廃止してもしなくても避けられない。**

## 検討した選択肢

### 論点 A: `ConfigurationService.Client` を本 PR で廃止するか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A1: 本 PR で廃止する（採用）** | 6 ファイルを呼び出し元 2 サービスの `Infrastructure/ExternalServices/` へ移す。`.Client` / `.Client.Tests` を削除する | ○ [#526](https://github.com/endazon/ai-stock-trading/issues/526) と [IADR-0259](IADR-0259_single-project-vsa-structure.md) 決定 2 / 決定 9 が名指しで指示した形そのものである ○ 「1 サービス = 1 プロジェクト」を本 PR で満たす ✕ 未移送の 2 サービスのファイルを触る ✕ 6 ファイルが 2 箇所へ複製される（**ただし後述のとおり計画が承知で選んだ形**） |
| A2: 据え置き、Tests 統合だけ行う | `.Client` を第 2 の本番プロジェクトとして残す | ✕ ConfigurationService が**本番プロジェクトを 2 本持ったまま**残り、本波の目的（1 サービス = 1 プロジェクト）を満たさない ✕ **楽にならない** —— `VersionedAssumptions` の移送はどちらでも要り（前掲）、呼び出し元 4 csproj の参照張り替えも結局要る＝**同じ場所を 2 回触る** ✕ 追随 issue を新設して先送りするだけで、次に触るときの前提は今と変わらない |

**複製が計画の意図どおりであることを、思い込みでなく本文で確かめた。**
[#526](https://github.com/endazon/ai-stock-trading/issues/526) 本文は
「移設先は**呼び出し元ごと**であり、共有プロジェクトへ移すのは同じ問題を場所を変えて再現するだけになる。
**呼び出し元が複数あるなら、それぞれの `Infrastructure` に別々の値で置くのが正しい**」と書いており、
理由（「呼び出し先が固定すると合わない側が回避策を書く」）まで添えている。
[IADR-0259](IADR-0259_single-project-vsa-structure.md) 決定 9 も同じ移設先を名指ししている。
**したがって複製は本移送が生んだ負債ではなく、計画が選んだ設計である。**

### 論点 B: `VersionedAssumptions` の置き場

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B1: 呼び出し元ごとに型も複製する | 各 `Infrastructure/ExternalServices/` に同じ record を定義する | ✕ **同一の通信契約の型が 3 箇所に分かれる**（サーバ 1・クライアント 2）。#526 が複製を認めたのは**方針値（TTL・タイムアウト・fail-safe）**であって型ではない ✕ 版の番兵（`UnresolvedVersion = 0`）の意味がずれると、片側だけ「未解決」の解釈が変わる |
| **B2: `AiStockTrading.Shared.Kernel` へ移す（採用）** | 共有カーネル `Trading/` に置き、テストも `Shared.Kernel.Tests` へ移す | ○ 共有カーネルの憲章（[IADR-0260](IADR-0260_shared-kernel-for-cross-service-domain-types.md)）が定める「サービス境界をまたいで消費される型の置き場」に**まさに当たる** ○ 同カーネルには既に `TradingAssumptions`（本型が包む中身）がある ○ 型は 1 定義のまま |

**[IADR-0260](IADR-0260_shared-kernel-for-cross-service-domain-types.md) が `VersionedAssumptions` を
除外した理由は「消費側は認可された経路（`ConfigurationService.Client`）越しに使う」であり、
本 ADR がその経路を廃止する。除外の前提そのものが消えるため、判断を引き直した。**

### 論点 C: `Domain/` を持つサービスでの `Features/` と `Domain/` の切り分け

[IADR-0263](IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 1・2 は「`Domain/` を
持たないサービス」を前提としており、そのままは使えない。**基盤（MSP）の現物 2 例を読んで基準を得た。**

| 基準例 | `Domain/` にあるもの | `Features/<集約>/` にあるもの |
| --- | --- | --- |
| `NotificationService` | エンティティ（`NotificationEntities`）・値の組み立て（`NotificationMailBody`）・主体の解決（`NotificationSubject`） | ポート（`IEmailTransport` / `IEmailAddressResolver`）・DTO・エンドポイント・ストア・保持期間・ディスパッチャ |
| `AuthorizationService` | エンティティ（`AbacEntities`）・評価器（`AbacEvaluator`）・検証（`AbacValidation`） | エンドポイント（`AuthzEndpoints`） |

いずれも **`Domain/` は「フレームワーク・DI・I/O に触れず、業務概念そのものを表す型」だけ**であり、
**ポート・アプリケーションサービス・エンドポイント・DTO・ストアは `Features/<集約>/`** である。

## 決定

### 決定 1 — `ConfigurationService.Client` を本 PR で廃止し、呼び出し元 2 サービスの `Infrastructure/ExternalServices/` へ複製する

論点 A の A1。移設先は [IADR-0259](IADR-0259_single-project-vsa-structure.md) 決定 9 が指定する
`Infrastructure/ExternalServices/`（呼び出し元は未移送のため、旧樹形では
`src/<Svc>.Infrastructure/ExternalServices/`。移送時にそのままフォルダごと動かせる）。
**Wolverine ハンドラだけは [IADR-0263](IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 5 に従い
`Steps/`（名前空間 `<Svc>.Infrastructure.Steps`）へ置く。** gRPC 化・`Http.Resilience` /
`HybridCache` への置き換えは行わない（[IADR-0259](IADR-0259_single-project-vsa-structure.md) 決定 7・決定 9）。

### 決定 2 — `VersionedAssumptions` は `AiStockTrading.Shared.Kernel` へ移す（IADR-0260 の除外を引き直す）

論点 B の B2。[IADR-0260](IADR-0260_shared-kernel-for-cross-service-domain-types.md) の
「`VersionedAssumptions` は移さない」という判定は**本 ADR で覆す**。同 ADR の理由（認可された経路
＝共有クライアント越しに使う）が、決定 1 により成立しなくなったためである。
**移送でテストを消さない** —— `VersionedAssumptionsTests` は `Shared.Kernel.Tests` へそのまま移す
（[IADR-0260](IADR-0260_shared-kernel-for-cross-service-domain-types.md) が 3 型を移したときと同じ作法）。

**帰結として ConfigurationService は `Domain/` を持たなくなる**（残っていた型が 1 つだけで、それが出たため）。
[IADR-0263](IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 2 の裏返しを適用し、
**フォルダ移送そのものを理由に `Application.State` の型を `Domain/` へ昇格させることはしない**
——`AssumptionsChangeEntry` は 1 本目の `AuditEntry` と同じく `Features/<集約>/` に置く。

### 決定 3 — `Domain/` を持つサービスでは、型の性質で振り分ける（3 本目以降の型）

**`Domain/` に置くのは、フレームワーク・DI・I/O に触れず、業務概念そのものを表す型に限る**
——エンティティ・値オブジェクト・純粋な業務規則（評価器・検証器）。
**ポート（インターフェース）・アプリケーションサービス・エンドポイント・DTO・ストアは `Features/<集約>/`。**
基盤の現物（論点 C の表）がこの形である。

- 🔴 **移送で型の層を変えない。** 現に `Application` にある型を `Domain/` へ上げる／`Domain` にある型を
  `Features/` へ下ろすのは**設計判断**であり、フォルダ移送の射程外である
  （[IADR-0259](IADR-0259_single-project-vsa-structure.md) 決定 7 と同じ理由づけ）。
  移送では**元の層に対応するフォルダへ素直に置く**。
- **`Domain/` が空になったら作らない**（決定 2 の帰結。空の枠を先回りで作らない
  ＝[IADR-0263](IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 2 と同じ立場）。

### 決定 4 — 複製の内訳は呼び出し元ごとに変える（振る舞いを変えないため）

`AssumptionsChanged` を**購読しているのは費用統制サービスだけ**である（実測。取引判断サービスの
`UseAiStockTradingRabbitMq` はハンドラを持つアセンブリとして自 `Infrastructure` しか渡していない）。
**ハンドラを取引判断サービスへも複製すると、Wolverine のアセンブリ走査が新しい購読を発見し、
移送だけのはずの PR で振る舞いが変わる。** したがって:

- 取引判断サービス: ポート・HTTP クライアント・キャッシュ・既定プロバイダ・DI 拡張の **5 ファイル**
- 費用統制サービス: 上記 ＋ **`AssumptionsChangedHandler`**（計 6 ファイル）

**テストも同じ内訳で複製する**（実装が呼び出し元ごとに存在する以上、テストも呼び出し元ごとに要る。
テストを削らない）。

### 決定 5 — アーキテクチャ検査の下限は「サービス数」ではなく「実測」で読む

移送が 1 サービス進むたびに `*.Domain.csproj` は 1 件減り、Domain が空になったサービスは
**ソース領域の数え上げからも外れる**。`DomainLayerDependencyTests` / `DomainSourceDependencyTests` の
下限（いずれも 9 → 8）は**退行ではなく移送の正常な結果**であり、失敗メッセージにその旨を書く。
下限そのものは残す（0 件走査で無条件に緑になる経路は塞ぎ続ける）。

## 理由

- **決定 1 は「先送りしても前提が変わらない」ことを決め手にした。** 案 A2（据え置き）の唯一の利点は
  「1 PR = 1 サービスを厳格に守る」ことだが、`VersionedAssumptions` の移送と呼び出し元 4 csproj の
  張り替えは**どちらの案でも本 PR で要る**。据え置いても**次に触るときの難しさは減らず**、
  その間 ConfigurationService だけが本波の目的を満たさない状態で残る。
- **決定 2 は「除外の理由が消えたかどうか」で判断した。** 先行 ADR の結論だけを引き写すと、
  前提が変わったことに気付けない。**引用元の理由づけまで読んで、それがまだ成立するかを確かめる**。
- **決定 3 は基盤の現物から引いた。** [IADR-0259](IADR-0259_single-project-vsa-structure.md) の写像方針表は
  `Domain/Services/` を既定として挙げるが、基盤の実装 2 例はいずれもポートを `Features/` に置いており、
  **テンプレートの文字面ではなく実装の形に揃える**という同 ADR 決定 1 の作法をここでも踏襲した。
- **決定 4 は「移送で振る舞いを変えない」を機械的な複製より優先した。** 6 ファイルを両方へ均等に
  配るほうが対称で書きやすいが、Wolverine の発見規約の下では**対称に置くこと自体が購読の追加**になる。

## 結果

- 良い影響:
  - **[#526](https://github.com/endazon/ai-stock-trading/issues/526) の主要スコープを解消した**
    ——`*.Client` の廃止、キャッシュ・fail-safe・DI 拡張の呼び出し元 `Infrastructure` への移設、
    再発防止の検査（`ServiceClientProjectAbolishedTests`）。
  - **3 本目以降は決定 3 を適用するだけで `Domain/` の要否と中身を決められる。**
  - ConfigurationService の実測: csproj 10 → 2、`.cs` は移動のみ（`InMemoryAssumptionsStore.cs` に
    `using` を 1 行足したほかは namespace / using の書き換えのみ）、`has-pending-model-changes` は「変更なし」。
- 悪い影響・トレードオフ:
  - **未移送の 2 サービス（費用統制＝3 本目・取引判断＝9 本目）のファイルを本 PR で触った。**
    移送時には `src/<Svc>.Infrastructure/ExternalServices/` を `Infrastructure/ExternalServices/` へ
    動かすだけで済むよう、フォルダ名を移送後の形に合わせてある。
  - **クライアント実装が 2 箇所に複製された**（計画が承知で選んだ形だが、片方だけ直す事故は起こり得る）。
    片方だけの変更は**呼び出し元ごとに値を変えてよい**という設計の帰結でもあるため、
    機械的な同一性検査は置かない。
  - **`Domain/` を持つサービスでの実例を本 PR は残せなかった**（決定 2 の帰結で Domain が空になった）。
    決定 3 は基盤の現物から引いた基準であり、**AST 内での初適用は 3 本目以降になる**。
- フォローアップ:
  1. **gRPC 化は本 ADR の射程外**（[IADR-0259](IADR-0259_single-project-vsa-structure.md) 決定 9 が
     「トランスポートの変更＝振る舞いの変更」として切り出しを指示）。
     **[#584](https://github.com/endazon/ai-stock-trading/issues/584) を起票した**（起票前に既存 issue を
     `grpc` で検索し、0 件であることを確認済み）。
     [#526](https://github.com/endazon/ai-stock-trading/issues/526) の「呼び出し元ごとのタイムアウト・
     リトライを結合テストで固定する」と `Http.Resilience` / `HybridCache` への置き換えも同 issue へ送った
     （現行は呼び出し元ごとの単体テストで固定済み）。
  2. 3 本目（費用統制）・9 本目（取引判断）の移送では、本 PR が置いた
     `src/<Svc>.Infrastructure/ExternalServices/` をそのまま `Infrastructure/ExternalServices/` へ移す。

## 関連

- 上流: [IADR-0259](IADR-0259_single-project-vsa-structure.md)（樹形・決定 2・決定 9）・
  [IADR-0263](IADR-0263_auditservice-vsa-migration-first-of-eleven.md)（1 本目の型）
- 改定: [IADR-0260](IADR-0260_shared-kernel-for-cross-service-domain-types.md)（`VersionedAssumptions` の
  除外を本 ADR 決定 2 で引き直した。同 ADR の他の判定は不変）・
  [IADR-0063](IADR-0063_assumptions-versioned-resolution.md)（決定 3 の共有クライアント方式を廃止。
  決定 1/4/5/6 の内容＝HTTP 照会・二段失効・fail-safe の順序・安全既定は**呼び出し元へ移って存続する**）
- 作業仕様書: [20260829_w11s4b_configurationservice-vsa](../specs/20260829_w11s4b_configurationservice-vsa.md)
- Supersedes: なし（部分改定は上記「改定」）
- Superseded by: なし
