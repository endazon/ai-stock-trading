---
title: AuditService を単一プロジェクト＋VSA 樹形へ移送する（W11 段 4-1・11 本の型）
type: spec
status: approved
related_ids: [NFR]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
plan_refs: []
---

# 仕様書: AuditService の単一プロジェクト＋VSA 移送（W11 段 4-1）

> 本仕様書は着手前に作成した。**11 サービスを 1 サービス = 1 PR で移送する波の 1 本目**であり、
> AuditService は最小規模（.cs 34 / csproj 6 / migration 1）を理由に先頭へ選ばれた。
> ここで確立した手順・判断基準は [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)
> に集約し、**残り 10 本は同 IADR を参照するだけで済む**ことを狙う。

## 起点

- 起点 ID: **`NFR`（無採番）**。規約整備・構造移送＝メタ作業であり、
  `.claude/rules/traceability.md`「起点 ID の種別」の無採番許容ケース **2** に当たる
  （[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) が同じ判断を計画の
  非機能要件表 `NFR-01`〜`NFR-17` の全行を読んで確定済みであり、本件はその実施であるため
  再確認のみ行い、同じ結論とした。環流はしない）。
- 上流: [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定1（樹形）・
  決定4（Tests 統合）・決定6（移送波の完了までは新規コードも現行配置）、
  [IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md)（名前空間は先行して整合済み。
  本波では 1 行も変えない）、[IADR-0258](../adr/IADR-0258_structure-aware-checkers-dual-layout.md)
  （構造依存の検査器は新旧両対応済み）。

## 着手前に読んだもの

- `CLAUDE.md` / `.claude/rules/traceability.md` / `.claude/rules/traceability.repo.md` /
  `docs/DEFINITION_OF_DONE.md`
- `IADR-0259` / `IADR-0261` / `IADR-0258`（前掲）
- 基盤の実物 `/home/user/microservices-platform/src/platform/backend/Services/NotificationService/`
  （読み取り専用。樹形・csproj・Program.cs・Tests の作りを確認した。以下「基準例」）

## 対象範囲

- 対象: `backend/Services/AuditService/`（4 プロジェクト → 2 プロジェクトへ統合）、
  `backend/backend.slnx`、`docker-compose.yml`、`scripts/k8s-local-images.sh`
- 対象外: 他 10 サービス（次の PR 以降）、`backend/Shared/` `backend/TestSupport/`（据え置き集合）、
  Persistence / Migrations の名前空間（[IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md)
  で既に整合済みのため触らない）

## 着手前の実測（母集合）

| 項目 | 実測 |
| --- | --- |
| 移送前の .cs（src + tests） | 36（src 20・tests 16） |
| 移送前の csproj | 6（src 3・tests 3） |
| migration | 1 本（+ Designer + ModelSnapshot） |
| `internal` 型のうち Tests が直接参照するもの | 3（`AuditDbContext` / `AuditEventRow` / `EfAuditEventStore`） |
| `list-test-projects.js --count` | **52**（`git stash -u` で未追跡ファイルも除いた完全なクリーン状態で実測。
  🔴 タスク文の「51」は前提の実測値がやや古く、実際は 52 であった。以降の「52 → 50」は本仕様書の実測に基づく） |
| `AuditService` を参照する他サービス・横断テストの `ProjectReference` | 0 件（監査は他サービスから
  ProjectReference で参照されない。全文走査で確認済み） |
| `deploy/helm/.../pipeline.json` の AuditService 関連 consumer 参照 | 0 件（対象外） |

## 設計（移送の手順・7 段）

1. **`git mv` でフォルダを再配置する**（内容は変えない）。`src/<Svc>.{Api,Application,Infrastructure}/**`
   → ルート直下（下表「写像」）。`tests/<Svc>.{Api,Application,Infrastructure}.Tests/**` → `Tests/`
   （フラット化。基準例が `Tests/` 配下にサブフォルダを作らずフラットに 10 ファイルを置いていることを
   確認し、同じ形にした）。
2. **csproj を新規作成する**（`git mv` では中身が変わるため、旧 3 本を `git rm`・新 1 本を作成）。
   基準例の 3 行（`<Compile Remove="Tests/**" />` 等）を必ず含める。
3. **`namespace` 宣言・`using` を移送先フォルダへ合わせて書き換える**（Persistence / Migrations は
   [IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md) により据え置き）。
4. **`internal` → `public` の要否を、Tests が直接参照する型だけに絞って判定する**（基準例の
   `LogSanitizer.cs` が internal のまま・Tests から参照されないことを確認し、「Tests が直接
   構築・参照する型だけを public にする」を基準例の実際の運用と判断した。全面 public 化はしない）。
5. **`backend.slnx` / `docker-compose.yml` / `scripts/k8s-local-images.sh` を更新する**（基準例と
   同じ 2 行 `Folder` に単純化）。
6. **ビルド・テスト・`has-pending-model-changes` で移送の無事故を確認する。**
7. **検査器（`M4` / `T1`）が新樹形側を実際に走査したことを実測で確認する。**

## Ports / Application.Services / State の振り分け（判断とその理由）

親の指示（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) の写像方針表）は
Ports を「利用スライス数」で 4 通りに振り分けるとしている。AuditService には以下 2 点の
固有事情があり、判断を以下のとおり確定した。

### 判断1: 「集約」は 1 つ（`AuditEvents`）であり、`_Shared/` は作らない

IADR-0259 決定1 は「`Features/<集約>/<操作>/` の 3 段目（操作単位のスライス分割）は採らない」と
確定している。AuditService は全 33 ハンドラ・1 照会エンドポイントが**単一の集約（監査台帳＝
`AuditEvents`）**に属し、操作単位の兄弟フォルダを持たない。`_Shared/` という区分は「同じ集約内に
複数の操作フォルダがあり、その間で共有するもの」を指すための区分であり、**兄弟が存在しない
AuditService では区分する対象が無い**。したがって `Features/AuditEvents/_Shared/` は作らず、
`Features/AuditEvents/` 直下へ平らに置く（名前空間も `AuditService.Features.AuditEvents` の
1 段で統一）。基準例（`NotificationService/Features/Notifications/`）も操作フォルダを持たず、
ポート（`IEmailAddressResolver` 等）を `_Shared/` なしで直下に置いており、この判断と一致する。

### 判断2: `Domain/` が無い制約下で、集約内の複数スライスから使う業務ロジックの置き場

`AuditService.Application.Services`（`AuditCorrelation` / `AuditEntryFactory` / `AuditSerialization`）
は、親の写像方針表が示す既定の置き場（「複数から使う業務ルールは `Domain/Services/`」）が使えない
——AuditService は**現状 Domain を持たず「無いなら作らない」**という制約が明示されているためである。

- **`AuditEntryFactory` は 33 個の `From(...)` オーバーロードを持ち、各オーバーロードは対応する
  1 ハンドラと 1:1 である**が、クラス全体としては**集約内の全ハンドラ（33 スライス相当）から
  使われる**——「1 ユースケースと 1:1 の手順書き」に当たるのは個々のオーバーロードであって
  クラス自体ではない。加えて `AuditCycleCompletenessTests` が**リフレクションで
  `AuditEntryFactory` の `From` オーバーロード数と契約イベント数の一致を機械検査**しており、
  ハンドラへインライン化すると**この完全性検査が成立しなくなる**（クラスが消えるため）。
  よって「インライン化して消す」は採らない。
- Ports の振り分け規則（「同一集約の複数スライス → `Features/<集約>/_Shared/`」）を、
  判断1 により `_Shared/` を作らない前提で読み替え、**`Features/AuditEvents/` 直下**へ置いた。
  `AuditCorrelation`（相関ID の決定的導出）・`AuditSerialization`（Detail の JSON 設定）も
  同じ理由（集約内の複数ハンドラ・複数イベント型から使われる）で同じ場所に置いた。
- `AuditEntry`（State/AuditEntry.cs）も同じ理由で `Features/AuditEvents/` に置いた——
  監査記録 1 件を表す型であり、Domain が無い制約下では「集約の中心的な値」として
  Features 直下に置くのが最も実態に近い。

### 判断3: 技術プリミティブ（`IClock`）は `Common/Abstractions/` へ、実装は同じ場所に並べる

`IClock` は親の指示が名指しした例（「技術プリミティブ（`IClock` 等）→ `Common/Abstractions/`」）
そのものであり、そのまま適用した。実装 `SystemClock` は写像方針表の既定（「`Adapters/` →
`Infrastructure/` の該当区分」）に従うと `Infrastructure/` 配下になるが、**`SystemClock` は
`DateTimeOffset.UtcNow` を返すだけで I/O・外部依存を一切持たない**——`Infrastructure/`
（`Persistence` / `Messaging` / `ExternalServices`）のどの区分にも実態として当てはまらない。
**抽象と実装を分断すると読み手が探す場所が増えるだけ**と判断し、`IClock` と同じ
`Common/Abstractions/` に置いた（判断の記録としてここに残す。基準例には対応物が無い
＝`TimeProvider.System` を直接使っており固有の抽象を持たないため、先例からは判断できなかった）。

### 判断4: `InMemoryAuditEventStore` は `Infrastructure/Persistence/` へ

写像方針表の既定どおり、`Adapters/` → `Infrastructure/` の該当区分。`IAuditEventStore` の
もう一方の実装である `EfAuditEventStore` と同じ「永続化ポートの実装」という性質を持つため
`Persistence/` に揃えた（本番では未登録・Tests 専用の実装だが、**実装 = Infrastructure**
という区分自体は変わらない）。

## `internal` → `public` の判断（基盤への追随）

基準例 `NotificationDbContext` は `public class`（`internal` ではない）であり、`InternalsVisibleTo`
を 1 つも持たない。一方で同じ基準例の `LogSanitizer`（Tests から直接参照されない技術ヘルパ）は
`internal` のままである。**「全面 public 化」ではなく「Tests が直接参照する型だけ public にする」**
が基準例の実際の運用であると判断し、以下の 3 型のみ `internal` → `public` にした
（`InternalsVisibleTo` は新設しない・旧 csproj の 4 エントリはすべて削除）。

| 型 | 理由 |
| --- | --- |
| `AuditDbContext` | `Tests/AuditQueryEndpointsTests.cs` / `AuditWorkerWebApplicationFactory.cs` が
  `GetRequiredService<AuditDbContext>()` 等で直接参照する |
| `AuditEventRow` | `AuditDbContext.AuditEvents`（`public DbSet<AuditEventRow>`）が要求する
  （`DbSet<T>` の `T` は少なくとも同じ可視性が要る。CS0053 で強制される） |
| `EfAuditEventStore` | `Tests/EfAuditEventStoreTests.cs` が `new EfAuditEventStore(db)` で
  直接構築する |

`AuditDbContextFactory`（`dotnet ef` がリフレクションで発見。Tests は参照しない）・
`AuditQueryEndpoints`（Program.cs と同一アセンブリなので `internal` のままで解決する。
Tests は HTTP 経由でしか触らない）・`AuditSerialization`（Tests から直接参照されない）は
`internal` のまま据え置いた。

## Tests 統合（3 → 1）で変えていないことの証跡

- **中身は 1 行も変えていない**（`git mv` のみ・変更は namespace 宣言・using・完全修飾子の
  書き換えに限定）。移送後の合格件数 **116** は移送前の 3 プロジェクト合計（Api.Tests +
  Application.Tests + Infrastructure.Tests）と一致する。
- 具体的な差分（`git diff` で確認可能な範囲）:
  - `namespace AuditService.{Api,Application,Infrastructure}.Tests;` → `namespace AuditService.Tests;`
  - `using AuditService.Application.{Adapters,Ports,Services,State};` →
    `using AuditService.{Common.Abstractions,Features.AuditEvents,Infrastructure.Persistence};`
    （移送先に応じて分岐。上表「Ports / Application.Services / State の振り分け」参照）
  - `AuditEventConsumersTests.cs` の 8 箇所の完全修飾子
    `AuditService.Application.Services.AuditEntryFactory` → `AuditEntryFactory`
    （`using AuditService.Features.AuditEvents;` で解決するため短縮。呼び出し先・引数は不変）
- テストロジック（Assert・Arrange・Act）・アサーション文言・`[Fact]`/`[Theory]` の数は不変。

## 受け入れ基準

- [x] `dotnet build backend/backend.slnx` が 0 warning / 0 error で通る
- [x] `dotnet test backend/backend.slnx` の失敗が `AiStockTrading.IntegrationTests` の 8 件のみ
      （Docker 不在の環境制約）
- [x] `dotnet format backend/backend.slnx --verify-no-changes` が通る
- [x] `dotnet ef migrations has-pending-model-changes` が
      「No changes have been made to the model since the last migration.」を返す
- [x] `list-test-projects.js --count` が移送前より 2 少ない（実測 52 → 50。旧 3 本 → 新 1 本の
      差分と一致。タスク文の「51 → 49」との差は着手前の実測値の古さによるもので、
      **差分の大きさ（-2）自体は一致している**）
- [x] `check-consumer-endpoint-names.js` の `M4` が新樹形側を実走査した
      （OK 出力に「新樹形 18 件（サービスディレクトリ: … 新 1 件）」を確認）
- [x] `check-test-traceability.js` の `T1` が新樹形側を実走査した
      （OK 出力に「新樹形 16 件（サービスディレクトリ: … 新 1 件）」を確認）
- [x] `coverage-floor.json` の床（79.00%）を割らない（実測は本文末尾）
- [x] 検査器一式（`scripts/README.md` 掲載分）が緑

## 計画書との差異

- 差異: なし。本件は構造移送のみで振る舞いを変えていない（IADR-0259 決定7）。

## 残り 10 サービスへの申し送り（踏んだ落とし穴・再利用可能な手順）

1. **`git stash` は既定で untracked を残す。** `list-test-projects.js --count` の「移送前」を
   測るときは `git stash -u`（`-u` 必須）を使うこと。忘れると新プロジェクトの csproj が
   「移送前」の計測に紛れ込み、差分が 1 つズレる（本 PR で実際に踏んだ）。
2. **`internal` → `public` は「Tests が直接参照する型」だけに絞る。** 基準例
   （`NotificationService`）は全面 public 化ではなく、`LogSanitizer` のような Tests 非参照の
   技術ヘルパは `internal` のまま残している。**先に `grep` で Tests からの直接参照
   （`new X(...)` / `typeof(X)` / ジェネリック引数としての `X`）を洗い出してから**
   可視性を変えること——DI 経由（`GetRequiredService<IFoo>()` でインターフェース越し）は
   実装型を public にする必要が無い。
3. **`AuditDbContext.AuditEvents`（`public DbSet<T>`）のように、1 つの public 化が連鎖することがある。**
   `CS0053`（型のアクセス修飾子の不一致）はビルドが教えてくれるので、まず 1 つ public にして
   ビルドし、連鎖要求をコンパイラに拾わせるとよい。
4. **`Domain/` を持たないサービスでは、Ports/Services/State の既定の置き場（`Domain/Services/`
   等）が使えない。** 本 PR の判断（集約内で複数スライスから使うものは `Features/<集約>/`
   直下、集約を跨がない技術プリミティブは `Common/Abstractions/`）を既定線として使ってよいが、
   **サービスによっては Domain を持つ／複数集約を持つ場合があるため、機械的に流用せず
   都度サービスの実態を見て判断すること**（本仕様書の「判断1〜4」を型として読み、
   結論だけをコピーしない）。
5. **`Features/<集約>/<操作>/` の 3 段目は作らない**（IADR-0259 決定1・基準例の波 4.5 実装も
   未実装）。集約が 1 つしかないサービスでは `_Shared/` も作る必要が無い（判断1）。
   複数集約を持つサービスでは `_Shared/` が要る場合がある——**その場合のみ**作ること。
6. **`AuditEventHandlers.cs` のような Wolverine ハンドラ集合は、たとえ「1 ユースケース = 1
   ハンドラクラス」の形をしていても、`Infrastructure/Steps/` へ置く**（親の写像方針表が
   明示。Features へ動かさない）。名前空間が既に `<Svc>.Infrastructure.Steps` の形なら
   フォルダを合わせるだけで済む（IADR-0261 が先行して整合済み）。
7. **クロスリポ参照・他サービスからの `ProjectReference` の有無を必ず全文走査で確認する。**
   AuditService は他サービスから参照されず `pipeline.json` にも consumer 参照が無かったため
   単純だったが、**クライアントライブラリを持つサービス**（例: `ConfigurationService.Client`。
   IADR-0259 決定2 で「1 サービス = 1 プロジェクトでは残す置き場が無い」ため廃止対象）は
   この段で追加の設計判断が要る。
8. **`dotnet ef migrations has-pending-model-changes` は `--project` と `--startup-project` の
   両方に新しい単一プロジェクトのパスを渡す。** 旧 `.Api` / `.Infrastructure` の分離が無くなった
   ため両オプションとも同じパスになる。
9. **`backend.slnx` の `Folder` は `src/` `tests/` の 2 段サブフォルダを廃し、
   基準例と同じ「`Folder` 直下に 2 `Project`」の形に単純化する。**
10. **`docker-compose.yml` の `SERVICE_PROJECT` / `SERVICE_DLL` と
    `scripts/k8s-local-images.sh` の同じマッピング行を必ず両方更新する**（片方だけ直すと
    ローカル k8s だけ壊れたまま気付かない）。
