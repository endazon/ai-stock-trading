---
title: IADR-0263 AuditService を単一プロジェクト＋VSA 樹形へ移送する（11 本の型を確定）
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0259, IADR-0261, IADR-0258, IADR-0256]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
---

# IADR-0263: AuditService を単一プロジェクト＋VSA 樹形へ移送する（11 本の型を確定）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-08-29
- 決定者: endazon（方針・[IADR-0259](IADR-0259_single-project-vsa-structure.md) の利用者裁定）/
  Claude Code（移送の実施と、11 本へ再利用する判断基準の起案）

## 起点・関連

- 起点 ID: **`NFR`（無採番）**。構造移送＝メタ作業であり、`.claude/rules/traceability.md`
  「起点 ID の種別」の無採番許容ケース 2 に当たる（[IADR-0259](IADR-0259_single-project-vsa-structure.md)
  が計画の非機能要件表を読んで確定済みの判断を継承する）。
- 関連する実装仕様書:
  [20260829_w11s4a_auditservice-vsa](../specs/20260829_w11s4a_auditservice-vsa.md)
- 上流: [IADR-0259](IADR-0259_single-project-vsa-structure.md)（樹形の確定）・
  [IADR-0261](IADR-0261_namespace-alignment-to-platform.md)（名前空間は先行整合済み。
  本移送は 1 行も変えない）・[IADR-0258](IADR-0258_structure-aware-checkers-dual-layout.md)
  （構造依存の検査器の新旧両対応）・[IADR-0256](IADR-0256_domain-dependency-inspection-by-source-scan.md)
  （Domain 依存規律のソース走査）

## コンテキストと課題

[IADR-0259](IADR-0259_single-project-vsa-structure.md) は 11 サービスの目標樹形を確定したが、
**Ports・Application.Services・State の振り分け規則**は「利用スライス数で判断する」という
基準線のみを示し、個別サービスでの適用は「移送時に判断し仕様書へ書く」としていた
（同 ADR の写像方針表）。AuditService は**この波の 1 本目**であり、AuditService 自身の樹形を
決めるだけでなく、**残り 10 本が同じ問いに毎回ゼロから答えずに済むよう、判断の型を確定する**
ことが本 IADR の目的である。

AuditService に固有の制約が 2 つあり、写像方針表の既定だけでは決まらなかった。

1. **`Domain/` を持たない**（[IADR-0259](IADR-0259_single-project-vsa-structure.md) の指示で
   「無いなら作らない」）——「複数スライスから使う業務ルールは `Domain/Services/`」という
   既定の置き場が使えない。
2. **操作単位の兄弟フォルダを持たない**（33 ハンドラ・1 照会エンドポイントが単一の集約
   `AuditEvents` に属し、`Features/<集約>/<操作>/` の 3 段目は決定1により採らない）——
   「`_Shared/`」という区分が指す「兄弟との共有」という前提が成立しない。

さらに、**プロジェクト境界が消えたことで `internal` 型の可視性を再設計する必要がある**
（旧構成は `InternalsVisibleTo` で Api/Infrastructure/Tests 間を開けていたが、単一プロジェクト化で
その配線自体が要らなくなる一方、Tests プロジェクトは今も別アセンブリのままである）。

## 検討した選択肢

### 論点 A: 集約内で複数スライスから使う Ports/Services/State の置き場（`_Shared/` を作るか）

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A1: 機械的に `Features/<集約>/_Shared/` を作る | 写像方針表の文言をそのまま適用 | ✕ 兄弟の操作フォルダが存在しないため「何と区別するための `_Shared`か」が空虚になる。基準例（NotificationService）も操作フォルダ無しでは `_Shared` を作っていない |
| **A2: 集約フォルダ直下に平らに置く（採用）** | `_Shared/` は「同じ集約内に複数の操作フォルダがあり、その間で共有するもの」を指すための区分と位置づけ、兄弟が無い間は作らない | ○ 基準例と一致 ○ 将来 3 段目（操作フォルダ）を採る決定が出たときに `_Shared/` を新設すればよく、今から空の区分を先回りする理由が無い |

### 論点 B: `Domain/` が無い場合の「複数スライスから使う業務ルール」の置き場

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B1: `Domain/Services/` を新設してしまう | 既定の置き場をそのまま使う | ✕ [IADR-0259](IADR-0259_single-project-vsa-structure.md) の明示指示（「Domain は無いなら作らない」）に反する。Domain の新設は「エンティティ・値オブジェクトを持つに至った」という別の判断が要り、フォルダ移送のついでに決めてよいことではない |
| B2: ハンドラへ機械的にインライン化する | 写像方針表の別の既定（「1 ユースケースと 1:1 の手順書きなら Handler へインライン化して消す」）を適用 | ✕ `AuditEntryFactory` の 33 オーバーロードは個々には 1:1 だが、クラス全体は集約内の全ハンドラから使われる。加えて `AuditCycleCompletenessTests` が**リフレクションでオーバーロード数と契約イベント数の一致を検査**しており、インライン化するとこの完全性検査の対象が消える |
| **B3: `Features/<集約>/` 直下に置く（採用）** | 論点 A の帰結（`_Shared/` を作らない）と整合させ、集約直下に平らに置く | ○ Domain を新設せずに済む ○ 完全性検査（リフレクション）が引き続き成立する ○ 集約が 1 つしかない現状の実態に最も近い |

### 論点 C: `internal` → `public` の可視性再設計

| 案 | 内容 | 評価 |
| --- | --- | --- |
| C1: 旧 `InternalsVisibleTo` の宛先をそのまま Tests プロジェクト名へ書き換えて存続させる | 配線をそのまま移す | ✕ [IADR-0259](IADR-0259_single-project-vsa-structure.md)「結果」節が「`InternalsVisibleTo` の多くが不要になり、層をまたぐために開けていた公開面が実際に閉じる」ことを良い影響として明記しており、機械的存続はこの意図に反する |
| C2: 全面 `public` 化する | 単純だが可視性の意味を失う | △ 基盤の実例（`NotificationDbContext`）はこの形だが、同じ基盤の `LogSanitizer`（Tests 非参照）は `internal` のまま——**基盤自身も全面 public 化はしていない** |
| **C3: Tests が直接参照する型だけを public にする（採用）** | 直接参照（`new X(...)` / 型引数としての `X` 等）が無い型は `internal` のまま据え置く | ○ 基盤の実際の運用（C2 の観測）と一致 ○ 公開面を必要最小に保つ（IADR-0259 の意図に沿う） ○ `InternalsVisibleTo` は不要（DI 経由の依存はインターフェース越しで済む） |

## 決定

### 決定 1 — `_Shared/` は「操作フォルダの兄弟が実在する場合」だけ作る

集約内に `Features/<集約>/<操作1>/` `Features/<集約>/<操作2>/` のような兄弟が実在し、
その間で共有するものがあるときに限り `Features/<集約>/_Shared/` を作る。**兄弟が存在しない
（操作単位のスライス分割を採っていない）サービスでは `_Shared/` を作らない**——集約フォルダ
直下に平らに置く。

### 決定 2 — `Domain/` を持たないサービスでは、集約内で複数スライスから使う業務ロジックは
`Features/<集約>/` 直下に置く（新規に `Domain/` を作らない）

写像方針表の既定（`Domain/Services/`）は「そのサービスが Domain を持つ、または持つべきだと
別途判断された」場合にのみ適用する。**フォルダ移送そのものを理由に Domain を新設しない**
——Domain の要否は別の設計判断（エンティティ・値オブジェクトの必要性）であり、本移送の射程外
（[IADR-0259](IADR-0259_single-project-vsa-structure.md) 決定7「本波では振る舞いを変えない」と
同じ理由づけ）。

### 決定 3 — 技術プリミティブは抽象・実装をともに `Common/Abstractions/` に置く

`IClock` のような、集約を跨いで使われ得る技術プリミティブ（外部 I/O を持たない）は
`Common/Abstractions/` に置く。**その実装が `Infrastructure/` の 3 区分（Persistence /
Messaging / ExternalServices）のいずれにも実態として当てはまらない場合**（I/O を持たない
薄い実装）は、写像方針表の既定（「Adapters/ → Infrastructure/ の該当区分」）より
「抽象と同じ場所に置く」ことを優先する。**I/O を持つ実装（DB・メッセージング・外部 HTTP）は
引き続き `Infrastructure/` の該当区分に置く**（本決定は薄い技術プリミティブの実装に限る）。

### 決定 4 — `internal` → `public` は「Tests プロジェクトが直接参照する型」だけに限る

移送後、Tests プロジェクトから直接参照される型（コンストラクタ呼び出し・型引数としての使用等。
DI コンテナ経由のインターフェース越しの解決は含まない）だけを `public` にする。
`InternalsVisibleTo` は新設しない。public 化が別の型へ連鎖する場合（例: `public DbSet<T>`
プロパティが `T` の可視性を要求する。CS0053）は、その連鎖もこの決定の範囲に含める。

### 決定 5 — Wolverine ハンドラ集合（Consumer/Steps）は Features へ動かさない

各ハンドラが「1 ユースケース = 1 ハンドラクラス」の形をしていても、
[IADR-0259](IADR-0259_single-project-vsa-structure.md) の写像方針表が明示するとおり
`Infrastructure/Steps/` に置く。名前空間が既に `<Svc>.Infrastructure.Steps` の形であれば
（[IADR-0261](IADR-0261_namespace-alignment-to-platform.md) により先行整合済み）、
フォルダを合わせるだけで済む。

## 理由

- **決定1・2 は「今ある構造を素直に表す」ことを優先した。** 先回りで `Domain/` や `_Shared/`
  という区分を作ると、**区分の意図（層の分離・スライス間の共有）を持たない空の箱**が生まれ、
  次にその箱を見た人が「なぜここに何も無いのか／何を入れるべきか」を再度調べる負債になる。
  実態が生まれた時点（Domain を持つ判断が出た時点・操作フォルダに分割する判断が出た時点）で
  区分を新設すればよい。
- **決定3 は「抽象と実装を分断すると読み手の探索コストが増える」ことを優先した。** `Common/`
  は「サービス固有の横断関心」の置き場であり、I/O を持たない技術プリミティブの実装はここに
  収めても `Infrastructure/` の意図（技術統合の境界）を損なわない。
- **決定4 は基盤の実際の運用を観測してから決めた。** 「全面 public 化」という一見単純な規則
  ではなく、基盤自身が「Tests が要る分だけ public にする」という選別を行っていることを
  `LogSanitizer` の実例で確認し、それに揃えた。**設計書の想定（IADR-0259 起草時点）を鵜呑みに
  せず、基盤の現物を見て判断する**という IADR-0259 自身の作法をここでも踏襲した。
- **決定5 は完全性検査（リフレクション）を壊さないことを優先した。** `AuditEntryFactory` を
  インライン化しないのと同じ理由で、ハンドラ集合を機械的に発見できる 1 箇所（アセンブリ走査の
  対象）に留めることが、既存のテスト資産（`AuditConsumerCoverageTests` 等）の前提を保つ。

## 結果

- 良い影響:
  - **残り 10 本のサービス移送が、本 IADR の決定 1〜5 を適用するだけで判断を再現できる**
    （Domain の有無・操作フォルダの有無・技術プリミティブか I/O 実装かを見れば機械的に
    振り分けられる）。
  - `internal` → `public` の判断基準が明確になり、**必要最小限の公開面**を保ったまま
    Tests プロジェクトのコンパイルが通る。
  - AuditService の実測: `.cs` 36 → 変わらず（移動のみ）、csproj 6 → 2、
    テスト 116 件（移送前 3 プロジェクト合計と一致）、`has-pending-model-changes` は
    「変更なし」。
- 悪い影響・トレードオフ:
  - **決定1・2 は「Domain を持つサービス」「複数集約を持つサービス」に出会うと、都度あらためて
    判断が要る**——本 IADR は「Domain が無い・集約が 1 つ」という AuditService の実態に
    最適化した決定であり、次のサービスがこの前提を満たさない場合は機械的に流用しない
    （作業仕様書「残り 10 サービスへの申し送り」5 に明記）。
  - **決定3（技術プリミティブの実装を `Common/` に置く）は、I/O の有無という主観混じりの
    判定を要る。** 境界事例（薄いキャッシュ・ID 生成器等）が出た場合は個別 IADR での
    補足が要り得る。
- フォローアップ:
  1. 次のサービス移送（W11 段 4-2 以降）は、本 IADR 決定 1〜5 を適用しつつ、
     Domain の有無・集約数・`internal` 参照パターンをサービスごとに再確認して仕様書へ書く
     （申し送り 4 参照）。
  2. `ConfigurationService.Client` のようなクライアントライブラリを持つサービスの移送は
     本 IADR の射程外であり、[IADR-0259](IADR-0259_single-project-vsa-structure.md) 決定2
     （置き場が無いため呼び出し元へ吸収）に従って個別に設計する。

## 関連

- 上流: [IADR-0259](IADR-0259_single-project-vsa-structure.md)（樹形の確定・写像方針表）
- 前提: [IADR-0261](IADR-0261_namespace-alignment-to-platform.md)（名前空間の先行整合）・
  [IADR-0258](IADR-0258_structure-aware-checkers-dual-layout.md)（検査器の新旧両対応）
- 作業仕様書: [20260829_w11s4a_auditservice-vsa](../specs/20260829_w11s4a_auditservice-vsa.md)
- Supersedes: なし
- Superseded by: なし
