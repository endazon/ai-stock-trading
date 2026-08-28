---
title: 構造依存の検査器（実行環境スキャフォールド / キュー名一意性 / 受け入れ基準トレーサビリティ）を現行構成と VSA 統合後の両対応にする
type: spec
status: approved
related_ids: [NFR]
author: endazon (with AI assistance)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 仕様書: 構造依存の検査器を新旧両対応にする（VSA 移行の土台 3）

> 本仕様書は実装着手前に作成した。**本番コード（`backend/`）を 1 行も変更しない。**
> 変更してよいのは `scripts/` と `.ai-context/` だけである。
>
> 🔴 **2026-08-28 再開時に射程を訂正した。** 当初は `validate-runtime-scaffold.js` /
> `check-consumer-endpoint-names.js` の 2 本を対象とし、`check-test-traceability.js` は
> 「本 PR の射程外・残余リスクとして記録するに留める」としていた（下の「母集合の引き直し」節の
> 元の判定を参照）。**この判断は誤りだった**——同検査器は VSA 統合後の `<Svc>Service/Tests/` を
> 拾えないという同種の構造依存を持ち、かつ「部分移行時に静かに痩せる」という fail-open の性質を
> 現に持つ。**構造依存の検査器は 2 本ではなく 3 本であり、本 PR で 3 本とも直す。** 以下の本文は
> この訂正を反映して書き直してある（元の判定は取り消し線的に残さず、判断の記録は本注記と
> 「残余リスク」節・関連 IADR-0258 の「コンテキストと課題」節に残す）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（製品機能の追加ではない）
- 非機能要件（NFR）: **無採番の `NFR`**。判断は下の「起点 ID の判断」に書く。
- ユースケース（UC） / 画面（SC）: なし
- 関連 ADR: 計画 ADR-0013（メッセージング）／実装 IADR-0048（実行環境スキャフォールド）・
  IADR-0106 / IADR-0129（キュー名の一意性）・IADR-0127（0 件検査で緑にしない作法）・
  IADR-0128（標準プロジェクト構成）・IADR-0256（土台 1: Domain 依存規律の二重化）
- 参考にした基盤側の先例: 基盤 microservices-platform IADR-0282（本波の樹形の確定）・
  同 `scripts/check-unit-dependencies.js`（0 件走査の門を新旧どちらに置くかの設計判断。読み取り専用で参照のみ）

### 起点 ID の判断（無採番 `NFR` を使う根拠）

計画 `02_requirements/01_requirements.md` の非機能要件表を実際に読んだ。ID 列は
**`NFR-01`〜`NFR-17`** で、内訳は 性能 2 / 可用性 2 / セキュリティ 2 / 運用・保守 5 /
費用 4 / 拡張性 1 / 法規 1 である。**「検査器がリポジトリ構成の変更に追随する」に当たる番号は無い**
（最も近い `NFR-16`〔拡張性〕は「証券会社・情報源・LLM の差し替えをポートで抽象化する」であり、
製品の差し替え容易性の要件で、検査器の保守ではない）。

配布規約 `.claude/rules/traceability.md` の無採番 `NFR` の許容 2 場合のうち **2（ID 列はあるが
その作業に当たる番号が無い場合。規約整備・検査器の追加・文書統制といったメタ作業が典型）**に当たる。
同規約は「この場合は環流しない」「無理に近い番号を付けない」と定めるため、
**`NFR-16` 等を無理に当てず、無採番の `NFR` を使い、計画への環流も行わない。**

## 目的・背景

VSA 全面移行では `backend/Services/<Svc>/{src,tests}/<Svc>.{Api,Application,Domain,Infrastructure}/`
という 2 階層を畳み、`backend/Services/<Svc>Service/` の**直下**に
`Program.cs` / `appsettings*.json` / `Features/` / `Domain/` / `Infrastructure/` / `Common/` / `Tests/` を置く。
移行は 1 サービス = 1 PR で進むため、**混在期間が 11 PR ぶん続く。**

この期間に、パス構造を前提にした検査器が壊れる。壊れ方は 2 種類あり、危険度が違う。

| 壊れ方 | 例 | 危険度 |
| --- | --- | --- |
| 赤くなる（fail-loud） | 期待するファイルが無い、と報告して落ちる | 🟡 気付ける |
| **黙って母集合が痩せる（fail-open）** | パスがマッチしないファイルを `continue` で捨て、**違反 0 件で緑になる** | 🔴 **気付けない** |

本 PR は後者を構造的に塞ぐ。**検査器が「1 件も読んでいない」ことと「違反が 1 件も無い」ことを
区別できる状態にする**（IADR-0127 / IADR-0256 決定 6 と同じ作法）。

## 母集合の引き直し（規則 1〜10・着手前に自分で引いた）

🔴 **「構造に依存する検査器はこの 2 本で全部か」を、指示を鵜呑みにせず自分で走査して確かめた。**
`scripts/` 全体（**拡張子で絞らず** `.sh` も含める。規則 3）を 7 軸で引いた。

| 軸 | 走査した文字列 | コマンド |
| ---: | --- | --- |
| 1 | `backend/Services` | `grep -rln "backend/Services" scripts/ .claude/hooks/` |
| 2 | `Services` ＋パス区切り | `grep -rln "Services['\"/,]\|Services\\\\" scripts/` |
| 3 | `/src/` ・ `'src'` ・ `"src"` | `grep -rln "/src/\|'src'\|\"src\"\|/src\b" scripts/` |
| 4 | 層名 `.Api` / `.Application` / `.Domain` / `.Infrastructure` / `.Worker` | `grep -rlnE "\.(Api\|Application\|Domain\|Infrastructure\|Worker)\b" scripts/` |
| 5 | `tests` ディレクトリ名 ・ `.Tests` | `grep -rlnE "['\"/]tests['\"/]\|\.Tests\b" scripts/` |
| 6 | `csproj` / `.slnx` / `appsettings` / `Program.cs` | `grep -rlnE "csproj\|\.slnx\|appsettings\|Program\.cs" scripts/` |
| 7 | `backend/` を含むパスのハードコード | `grep -rlnE "backend/" scripts/` |

**軸を 1 本で終わらせていない**（規則 5）。**誤りの側（現行構成に固有の `src/` ・層名接尾辞）から引いた**（規則 1）。
**`.js` へ絞らなかったため `k8s-local-images.sh` / `setup.sh` / `e2e-local-infra.sh` が母集合に入った**（規則 3）。

### 引いた結果（7 軸の和集合・全 12 件）と判定

| # | ファイル | 構造依存の形 | 判定 | 理由 |
| ---: | --- | --- | --- | --- |
| 1 | `scripts/check-consumer-endpoint-names.js` | `^backend/Services/[^/]+/src/` に**マッチしないファイルを `continue` で黙って捨てる** | 🔴 **本 PR で直す** | 構造依存の 3 本の 1 本 |
| 2 | `scripts/validate-runtime-scaffold.js` | `hostDir()` が `<Svc>.Api` / `<Svc>.Worker` の 2 通りしか見ない | 🔴 **本 PR で直す** | 構造依存の 3 本の 1 本 |
| 3 | `scripts/detect-changed-areas.js` | **判定ロジックは `^backend/` 前置だけを見ており構造非依存**。自己試験の期待値だけが `src/` 形 | 🟢 **本 PR で自己試験のみ拡張** | 設計 §13.3 が土台 3 へ割り当てている。**テストデータの追加だけで判定ロジックを触らない**ため現行挙動は変わらない |
| 4 | 🔴 `scripts/check-test-traceability.js` | `testFiles()` が `e.name === 'tests' \|\| e.name.endsWith('.Tests')` でテストディレクトリを収集する。**統合後の `<Svc>Service/Tests/` は `'Tests' === 'tests'` でも `'.Tests'` 接尾でもなく、どちらにも当たらない** | 🔴 **本 PR で直す（構造依存の 3 本目）** | 下の「3 本目の発見」参照。**当初「射程外」としていたが訂正した** |
| 5 | `scripts/list-test-projects.js` | 分配がラウンドロビン。発見は内容ベースで構造非依存 | ⛔ **触らない** | 並行 PR（土台 2）の担当領域として明示的に禁止されている |
| 6 | `scripts/k8s-local-images.sh` | 11 行の `<svc>\|<csproj パス>\|<dll 名>` テーブルをハードコード | ⚪ 射程外 | 設計 §13.3 が**各サービス PR で 1 行ずつ**直すと割り当てている。移行前に先回りして直すと当たらない行が残る |
| 7 | `scripts/check-ci-latency.js` | `'backend/Services/X/src/X.cs'` は**テストデータの文字列のみ**。判定は `detect-changed-areas` へ委譲 | ⚪ 追随不要 | 実挙動に影響しない |
| 8 | `scripts/check-banned-libraries.js` | 走査は**拡張子**（`.cs` / `.csproj` / `.props`）で決まり、パス構造を前提にしない | ⚪ 追随不要 | 移行後も同じ母集合を取る |
| 9 | `scripts/check-tracked-session-timeout.js` | 許可リストが `backend/TestSupport/...` を指す。走査自体はリポジトリ全体 | ⚪ 追随不要 | `TestSupport/` は設計 §6 で**維持**が確定しており移動しない |
| 10 | `scripts/setup.sh` | `.slnx` を `find` で自動発見 | ⚪ 追随不要 | 構成非依存 |
| 11 | `scripts/e2e-local-infra.sh` | `backend/Tests/AiStockTrading.IntegrationTests/...` をコメントで案内 | ⚪ 追随不要 | 横断テストは設計 §6 で**維持**が確定 |
| 12 | `scripts/scripts.repo.test.js` / `scripts/README.md` | 上記の呼び出し側・説明 | 🟢 本 PR で追随 | 直した検査器に合わせる |

### 3 本目の発見（🔴 設計書 §10.3 の記述と実測が食い違う）

設計書 §10.3 は `check-test-traceability.js` を **⚪「`<Svc>Service.Tests/` は `.Tests` で終わるので無改修で拾う」**
と評価している。**実測はこれと合わない。**

- 収集条件は `e.name === 'tests' || e.name.endsWith('.Tests')`（`scripts/check-test-traceability.js`）。
- 設計 **§5.2 が確定したツリーは `backend/Services/<Svc>Service/Tests/`** であり、ディレクトリ名は **`Tests`** である。
  `'Tests' === 'tests'` は偽、`'Tests'.endsWith('.Tests')` も偽。**どちらにも当たらない。**
- すなわち §10.3 の ⚪ 判定は、§5.2 が採らなかった案（`<Svc>Service.Tests/` という兄弟ディレクトリ）を前提にしている。

🔴 **本 PR の射程に入れる（訂正）。** 当初案は次の 3 点を理由に射程外としていたが、いずれも
再検討の結果、射程へ入れる側へ倒した。

1. ~~指示が「射程を広げない」と明示している~~ → **再開時の指示で明示的に訂正された**
   （3 本目の存在と、両対応が必須である旨）。射程の広げ方自体は 2 の理由により正当化できる。
2. 🔴 **「fail-closed だから塞がなくてよい」は全滅ケースにしか効かない。** 必須 FR 5 件の参照が
   **全滅**すれば確かに赤で落ちるが、**部分移行時**（1 サービスだけ新樹形へ移送された状態）は
   別のサービスが必須 FR を参照していれば緑のままであり得る。これは「黙って母集合が痩せる」
   fail-open そのものであり、本 PR が塞ぐべき壊れ方に**当てはまる**。全滅時の fail-closed 性は
   「T1 のような下限検査が要らない」根拠にはならない——全滅を待たずに部分痩せを検出したいからである。
3. **`e.name === 'Tests'` を素朴に足すと `backend/Tests/` が新たに条件へ当たる問題は、位置
   （`Services/<Svc>/` の直下かどうか）まで見る判定にすれば解決する**（許可リストではなく
   「新樹形の実在位置」で判定する。除外リストへ反転した決定 2 と同じ思想）。「別の設計判断が要る」は
   事実だが、**その設計判断は本仕様書で下せる**——先送りする理由にならない。

**採った設計**（下の「設計 4」に詳述）は 2 段構えである。①`testFiles()` の収集条件へ
`backend/Services/<Svc>/Tests`（位置まで見る・大文字始まり）を追加し、新旧どちらの樹形からも
拾う。②`check-consumer-endpoint-names.js` の M4 と同型の **T1**（樹形ごとの条件付きの門）を新設し、
「実在するのに 0 件」の部分痩せを塞ぐ。**部分移行時に静かに痩せるリスクは、本 PR で解消する**
（IADR-0258 の残余リスクからは外れる）。

### 除外したものと、その理由（規則 6）

| 除外 | 理由 |
| --- | --- |
| `.github/workflows/ci.yml` | 🔴 並行 PR の担当領域として**触ることを禁止**されている |
| `scripts/list-test-projects.js` | 同上（土台 2） |
| `backend/Tests/AiStockTrading.Architecture.Tests/` | 同上（土台 1・IADR-0256 が既に入っている） |
| `docs/ai-workflow.md` | 同上 |
| `backend/**`（Architecture.Tests 以外） | **本 PR は本番コードを 1 行も動かさない**という射程の宣言 |
| `docs/` 配下の文書 | 設計 §13.3 が**土台 4** へ割り当てている。変更してよいのは `scripts/` と `.ai-context/` だけ |
| `.ai-context/adr/**`・`.ai-context/specs/**`（既存分） | **凍結記録**。本文プロズを後から書き換えない |
| `CHANGELOG.md` | 生成物 |
| `frontend/**` | バックエンド構成に依存しない |

## 設計

### 1. `validate-runtime-scaffold.js`: `hostDir()` を 3 通りへ

```
1) backend/Services/<Svc>/src/<Svc>.Api        （現行・標準構成 IADR-0128）
2) backend/Services/<Svc>/src/<Svc>.Worker     （旧・未移行）
3) backend/Services/<Svc>                      （VSA 統合後・新設）
```

- **判定順は 1 → 2 → 3。** `appsettings.json` が実在する最初の候補を返す。
- **3 つとも無ければ現行どおり `<Svc>.Api` の名前で報告する**（失敗メッセージは変わらない）。
- 現行ツリーでは 11 サービスすべてが候補 1 でヒットするため、**候補 3 は 1 度も評価されない＝挙動不変**。

### 2. `check-consumer-endpoint-names.js`: パス解決と母集合の下限

#### 2-1. 走査対象の判定を純関数へ切り出す

```
isProductionServiceFile(relPath):
  - "backend/Services/<Svc>/..." に当たること
  - サービス名より後ろのパス要素に "tests" / "Tests"（大小無視）が 1 つも無いこと
```

- 現行 `src/**` の 693 ファイルは**すべて当たる**（実測: `src/` 配下に `tests` / `Tests` 要素は 0 件）。
- 現行 `tests/**` の 401 ファイルは**すべて落ちる**（現行と同じ）。
- 統合後 `<Svc>Service/Program.cs` ・ `<Svc>Service/Features/**` は当たり、
  `<Svc>Service/Tests/**` は落ちる（設計 §5.2 のツリー）。

🔴 **「`src/` を許可し、それ以外も許可する」ではなく「テストを除外する」へ反転させる**のが要点である。
許可リスト側に統合後の形を足す書き方だと、**次に増える階層が黙って落ちる**。

#### 2-2. 走査ファイル数の下限（M3・新設）

既存のメタ検査は **M1（サービス数 ≥ 11）** と **M2（Wolverine 配線サービス数 ≥ 10）**だけである。
これだけでは足りない。**M1 は「1 ファイルでもマッチしたサービス」を数える**ため、
たとえばパス判定が痩せて各サービス `Program.cs` 1 本しか当たらなくなっても
**M1・M2 はともに通り、N2（トポロジの直接指定）の走査だけが静かに空振りする。**

- **`MIN_SCANNED_FILES = 550`**（着手時点の実測 693。`McpExposureNotDeclaredTests.MinimumScannedFiles`
  が 1,100 件超に対し 900 を置いたのと同じ比率感）。
- 下回ったら `[M3]` として**落とす**。
- **走査したファイル数を OK 出力へ明示する**（母集合を表明しないと下限が効いているか読めない。IADR-0256 決定 6）。

**M3 だけでは足りない。** M3 は「新旧の和」に対する絶対下限であり、**旧樹形が痩せて新樹形がまだ
1 件も無い**ような部分移行の初期段階（例: 旧 600 件・新 0 件で和は 600 のまま M3 を満たす）でも、
新樹形からは何も読めていない可能性を見逃す。そこで **M4**（樹形ごとの条件付きの門）を追加する。

- `layoutOf(relPath)` で各走査ファイルを `old` / `new` に仕分け、`listServiceDirs(root)` から
  各サービスディレクトリが `src/` を持つか（旧）持たないか（新）を判定して `dirs.old` / `dirs.new` を数える。
  **走査結果ではなくディレクトリの有無から `dirs` を引く**——走査結果から引くと「走査が壊れて
  0 件」と「その樹形のサービスがそもそも無い」を区別できない。
- **門**: `dirs[layout] > 0 && scanned[layout] === 0` の樹形があれば `[M4]` として**落とす**。
- 🔴 **静的な下限を置かない。** AST は新樹形の移送済みサービスが現時点で 0 件のため、新樹形側へ
  静的な門を置くと着手初日から CI が赤になる（基盤 microservices-platform との前提の違いは
  IADR-0258 決定 8「★ 0 件走査の門の置き場」参照）。

#### 2-3. 一時ツリーで実証できるようにする

`collectServices(root)` / `checkTree(root)` へ**省略可能なルート引数**を足し、
CLI では環境変数 `CONSUMER_ENDPOINT_NAMES_ROOT` で上書きできるようにする
（`validate-runtime-scaffold.js` の `RUNTIME_SCAFFOLD_ROOT` と同じ作法に揃える）。
**既定値は従来どおりリポジトリルート**であり、CLI の既定挙動は変わらない。

### 3. `detect-changed-areas.js`: 自己試験へ統合後のパス例を足す

判定ロジック（`^backend/` 前置）は**触らない**。自己試験に
`backend/Services/AuditService/Program.cs` ・ `backend/Services/AuditService/Features/X/Handler.cs` ・
`backend/Services/AuditService/Tests/XTests.cs` ・ `backend/Services/AuditService/appsettings.json` を足す。

### 4. `check-test-traceability.js`: `testFiles()` の新樹形対応 ＋ T1（構造依存の 3 本目）

#### 4-1. 収集条件へ新樹形の**位置**判定を足す

```
isNewLayoutServiceTestsDir(root, dir):
  - path.relative(root, dir) が正規化して "backend/Services/<Svc>/Tests" に一致すること
```

`walkTests()` の分岐を `e.name === 'tests' || e.name.endsWith('.Tests') || isNewLayoutServiceTestsDir(root, p)`
へ拡張する。**位置まで見る**ため、`backend/Tests/`（横断テスト）はこれに当たらず、現行どおり
素通りして配下の `*.Tests` プロジェクトだけを拾う——3 本目の発見・理由 3 で特定した衝突を
「名前だけでなく位置で判定する」ことで解消する。

#### 4-2. T1（樹形ごとの条件付きの門。M4 と同型）

- `serviceTestDirs(root)`: `backend/Services/` 直下の各サービスについて、`tests/`（旧）・`Tests/`（新）
  サブディレクトリの実在を数える（走査結果ではなくディレクトリの有無から引く——理由は M4 と同じ）。
- `serviceTestLayoutCounts(root, files)`: `testFiles()` の結果を新旧で仕分ける。
  `backend/Tests/`（横断テスト）配下は集計に含めない——T1 は「サービス配下テストの痩せ」だけを見る。
- **門**: `dirs[layout] > 0 && counts[layout] === 0` の樹形があれば `[T1]` で落とす。
  **静的な下限を置かない**（AST は新樹形の移送済みサービスが現時点で 0 件のため、静的な門は着手初日
  から CI を赤にする。`check-consumer-endpoint-names.js` の M4 と同一の理由）。
- 既存の必須 FR 検査（1）・仕様書検査（2）とは独立に走らせる（どちらかが落ちても両方報告する）。

## 受け入れ基準

1. **現行構成で `validate-runtime-scaffold.js` が着手前と同一の出力・終了コードを返す。**
2. **現行構成で `check-consumer-endpoint-names.js` が着手前と同一の判定（11 サービス / Wolverine 10 件）を返す。**
   出力は既存 3 行を**バイト一致**で保ち、母集合の表明 1 行のみを末尾へ足す。
3. **統合後の構造を模した一時ツリー**（`/tmp` 配下）で、3 検査器すべてが正しく動く。
   - スキャフォールド検査: 11 サービスの `appsettings*.json` がサービスディレクトリ直下にある形で OK。
   - キュー名検査: `Program.cs` がサービスディレクトリ直下にある形で 11 サービス / Wolverine 10 件を検出。
     `Tests/` 配下は走査対象に入らない。
   - トレーサビリティ検査: `backend/Services/<Svc>/Tests/**` 配下の起点 ID 参照を収集する。
4. 🔴 **母集合を空にすると落ちる**（`[M1]` / `[M3]` / `[T1]`）。否定形のテストで固定する。
   **どちらの樹形も実在しない場合（サービスが 1 つも無い）は `[T1]` を誤発火させない**
   （T1 は「実在するのに 0 件」だけを見る門であり、「サービスが無いこと自体」を検査するのは
   `check-consumer-endpoint-names.js` の M1 の役割である）。
5. `--self-test` は壊れず、件数が増える。
6. `node scripts/scripts.test.js` ・ `dotnet build` ・ `dotnet test`（`IntegrationTests` の
   Docker 不在による 8 件を除く）・文書系検査がすべて通る。

## 対象範囲

- 対象: `scripts/validate-runtime-scaffold.js` / `scripts/check-consumer-endpoint-names.js` /
  `scripts/check-test-traceability.js` / `scripts/detect-changed-areas.js`（自己試験のみ）/
  `scripts/scripts.repo.test.js` / `scripts/README.md` / `.ai-context/adr/IADR-0258_*.md` /
  `.ai-context/adr/README.md` / 本仕様書
- 対象外: `backend/` の全て、`.github/workflows/`、`docs/`、`docker-compose.yml`、
  `scripts/list-test-projects.js`、`scripts/k8s-local-images.sh`

## 残余リスク

1. `MIN_SCANNED_FILES = 550` は着手時点の実測 693 に対する床である。`ConfigurationService.Client` の
   廃止（設計 §6.2）で複製が生じ件数は増える方向であり、**移行で床を割ることは無い**見込み。
2. 統合後の形は**まだ実在しない**。本 PR の実証は模擬ツリーによるものであり、
   最初のサービス移行 PR で実物により再確認する（`check-test-traceability.js` の T1 を含む）。
3. `check-test-traceability.js` の T1 は「樹形ごとの 0 件」しか見ない——**旧樹形のあるサービス 1 つが
   新樹形へ移送された瞬間に、そのサービス固有のテスト量が薄くなっても**（例: 旧 40 ファイル →
   新 5 ファイルへ実質的に痩せた場合）、**他サービスの旧樹形テストが残っていれば T1 は発火しない**。
   `check-consumer-endpoint-names.js` の M3（走査ファイル数の絶対下限）に相当する「厚み」の検査は
   本検査器には**意図的に入れていない**——必須 FR 参照の有無（既存の検査 1）がその代わりを担う
   （FR 参照が薄くなれば検査 1 が個別に落ちるため、量そのものの下限は不要と判断した）。
   （旧 IADR-0258 残余リスク 1「部分移行時は静かに痩せる」は、上記 T1 の新設により解消した。）
