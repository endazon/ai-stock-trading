---
title: 情報収集の縮退による新規建て停止を再起動に耐えさせ、既定を「不明なら止める」へ倒す
type: spec
status: approved
related_ids: [FR-01, FR-02, FR-10, FR-11, UC-01, ADR-0003, ADR-0009, ADR-0020, IADR-0150, IADR-0153, IADR-0198, IADR-0225, IADR-0249, IADR-0267]
author: claude
created: 2026-08-29
updated: 2026-08-29
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
related_specs:
  - 20260828_337_trading-cycle-and-screening.md
  - 20260828_336_information-collection-tiers-and-degradation.md
---

# 仕様書: 情報収集の縮退による新規建て停止の耐久化（#564）

## 起点

- 起点 issue: **#564**（`fix`。PR #561〔#337〕が [IADR-0249](../adr/IADR-0249_information-degradation-blocks-entries-wiring.md) の
  「結果」へ **fail-open 側の残余リスク**として明記していたものの消化）
- 起点 ID: **FR-01** / **FR-02** / **FR-10** / FR-11 / UC-01 / **ADR-0020 決定2・決定3** / ADR-0003 / ADR-0009
- 実測時点: 本リポ `claude/ast-implementation-issues-rzkoxb-w564` = `145446a`（`origin/develop`）
- 🔴 **issue 本文のパスは VSA 移送（#592〜、[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) の波）で古くなっている。**
  実測した現在地は次のとおり。

| 役割 | 現在のパス |
| --- | --- |
| 発行側（遷移時のみ発行） | `backend/Services/InformationCollectionService/Hosted/DegradationStateTracker.cs` |
| 受け側ハンドラ | `backend/Services/RiskManagementService/Infrastructure/Steps/InformationDegradationHandlers.cs` |
| 保持（プロセス内） | `backend/Services/RiskManagementService/Features/RiskManagement/IInformationDegradationStore.cs` |
| 判定 | `backend/Services/RiskManagementService/Domain/RiskEvaluator.cs` |

## 課題

情報収集の縮退（`InformationSourceDegraded.BlocksNewEntries=true`）は、リスク管理サービスの
プロセス内メモリ（`InMemoryInformationDegradationStore`）に保持される。イベントは**状態が遷移した
ときにしか発行されない**（発行側 `DegradationStateTracker` は継続中のサイクルを数えるだけで黙る）。

したがって **縮退が続いている最中にリスク管理サービスが再起動すると、次の遷移（回復 → 再欠測）まで
停止状態が届かない。** 空の集合は「縮退なし」と読まれ、**情報が欠測したまま新規建てが再開しうる。**

🔴 **本質は「不明」と「健全」を区別していないことである。** 集合が空であることが
「観測して健全だった」と「まだ何も聞いていない」の**両方**を表しているため、既定が fail-open に倒れている。
復元経路を足しても、**既定が「不明なら通す」のままなら受け入れ基準②を満たさない。**

## 検討した選択肢（issue の 3 案）

| 案 | 内容 | 採否 |
| --- | --- | --- |
| 1 | **定期再送**（発行側が現況を巡回ごとに発行し、受け側は鮮度つきで畳む） | **採用** |
| 2 | 受け側の起動時照会（リスク管理 → 情報収集へ HTTP で現況を問い合わせる） | 却下 |
| 3 | 永続化（停止状態を DB へ持たせる） | 却下 |

### 案 1 を採る理由

- **#513 の先例と同形である。** `FxSourceStatusTracker` は「遷移でしか記録が残らないため静かな期間に
  状態が引けない」という**同型の問題**を、**遷移が無い間も暦日ごとに 1 件の使用記録を出す**ことで解いた
  （[IADR-0225](../adr/IADR-0225_fx-source-daily-usage-record.md)）。本件は「静かな期間に**受け手が**状態を引けない」
  であり、**発行側が現況を定期的に出す**という同じ手が効く。
  - 🔴 **ただし抑止の鍵は先例と変える。** 先例は台帳の氾濫を抑えるため **暦日**で抑止したが、本件は
    **鮮度そのものが統制の入力**である（1 日 1 回では再起動後に最大 1 日 fail-open のままになる）。
    よって**巡回ごとに 1 件**とし、氾濫の抑止は「1 巡回 1 件」という上限で担保する。
- **受け側が「不明」を表現できるようになる。** 観測に**有効期間**を持たせれば、
  「観測が無い／失効した」＝**不明**を「健全」と区別でき、**不明は止める側へ倒せる**。
  これは同じサービス内の先例（`InMemoryBrokerAccountObservationStore`・[IADR-0153](../adr/IADR-0153_broker-account-type-supply-and-fail-closed.md) 決定3）と
  **同じ形**であり、リスク管理サービスに 2 つ目の作法を持ち込まない。
- **サービス間に同期依存を作らない。** 既存のイベント経路（Wolverine）に 1 種足すだけで済む。

### 案 2 を退ける理由

**リスク管理サービスが情報収集サービスへ同期的に依存する。** ADR-0003 はリスク管理を
「AI から独立した決定的な最終防衛線」と位置づけており、**停止の可否が他サービスの HTTP 応答に依存する**のは
その独立性を弱める。照会 API の新設・認可・タイムアウト時の既定（また「不明なら？」が現れる）も要る。
**起動時 1 回だけの照会では「起動後に情報収集が落ちた」を捕まえられない**——結局、鮮度の概念が要る。

### 案 3 を退ける理由

**発行側と受け側で二重の真実を持つ**（issue 本文の指摘どおり）。永続化しても
**発行側 tracker がプロセス内である事実は変わらず**、収集サービスが再起動すれば
「継続中の縮退」は遷移として再発行されないまま消える。**受け側だけを永続化しても取りこぼしは解消しない**
（IADR-0249 決定4 が既に記録している）。さらに **DB に残った古い停止が、回復イベントを取りこぼしたときに
永久に解けない**（fail-closed 側の恒久障害）リスクが増える。

## 決定（実装方針）

1. **契約イベントを 1 種新設する** —— `InformationSourceStateObserved(BlockingCategories, ValidFor, ObservedAt)`。
   - **現況の全量**（新規建てを止めるカテゴリの集合。空もあり得る）を毎巡回 1 件だけ運ぶ。
   - `BlocksNewEntries=false` の縮退は**載せない**（受け手が `Behavior` を再解釈して停止範囲を広げない、という
     IADR-0249 決定1 の規律をそのまま引き継ぐ）。
   - **`ValidFor`（この観測が有効な期間）を発行側が宣言する。** 受け手は収集の巡回間隔を知らないためである
     （`BrokerAvailabilityObserved.CoveredInterval` と同じ作法・[IADR-0150](../adr/IADR-0150_stage1-uptime-observation-and-session-hypotheses.md) 決定2）。
     **受け手側で上限クランプを掛ける**（宣言値をそのまま信じると、設定を誤ったときに統制が黙って無効化される）。
   - 🔴 名前空間は既存イベントと同じ `AiStockTrading.Shared.Contracts.Events`（Wolverine の識別子＝完全名。既存イベントは動かさない）。
2. **発行側**（`DegradationStateTracker`）は、遷移イベントに加えて**毎巡回 1 件の現況観測**を返す。
   観測は抑止しない（抑止＝鮮度の喪失であり、本件の目的に反する）。**巻き戻しは不要**（抑止状態を持たないため）。
3. **受け側**（`InMemoryInformationDegradationStore`）を**鮮度つき**にする。
   `BlocksNewEntries` は次のいずれかで **true**（＝止める）とする。
   - 停止カテゴリが 1 つ以上ある（従来どおり）
   - **まだ観測を 1 件も受け取っていない**（＝再起動直後・起動直後）
   - **最後の観測が失効した**（`now - ObservedAt > ValidFor`）
   すなわち **「新規建てを通してよいのは、有効な観測が『停止カテゴリなし』と言っているときだけ」** である。
4. **遷移ハンドラは残す**（`MarkDegraded` / `MarkRecovered`）。遷移は**即時に効く**べきであり、
   次の巡回まで待たない。ただし**遷移は鮮度を更新しない**——`Recovered` 1 件は「他のカテゴリも健全である」
   ことを保証しないためである。**鮮度を与えるのは現況観測だけ**とする。
5. **逆行する観測は無視する**（`observedAt <= 最後の観測時刻`）。再配送・順序の入れ替わりで
   **古い現況が新しい遷移を消さない**ようにする（`InMemoryBrokerAccountObservationStore.Record` と同じ）。
6. **`RiskEvaluator` は変更しない。** 判定位置（`isEntry &&`）・拒否理由（`InformationSourceDegraded`）は据え置く。
   **決済（手仕舞い・損切り）は `isEntry` の短絡で構造的に通る**（受け入れ基準③）。
   - **拒否理由を増やさない**（「不明」に別の理由を立てない）。序数の追加・分類表・監査要約・段階ゲートの
     計上規則へ波及し、**同じ統制が 2 つの理由に割れて集計が分かれる**ためである。区別は
     ログと新イベント（台帳に全量が残る）で付ける。
7. **通知（Discord）は増やさない。** 通知は**選んだ事象だけ**を出す設計であり
     （`NotificationConsumerCoverageTests` の母集合はハンドラ側・IADR-0198 の記述どおり）、
     毎巡回のハートビートを通知へ流すと**統制の発動通知が定常のノイズに埋もれる**。

### 決めた値

| 値 | 決定 | 理由 |
| --- | --- | --- |
| 発行側が宣言する `ValidFor` | **実効巡回間隔 × 2**（下限 5 分） | 1 巡回ぶんの取りこぼし（発行失敗・再配送遅延）で統制が誤発動しない最小の余裕。既定の巡回 30 分に対し 60 分 |
| 受け側のクランプ | **1 分 〜 2 時間** | 上限は「宣言値を誤っても、鮮度の要求が事実上消えない」ための歯止め（IADR-0150 決定2 のクランプと同じ役割）。下限は 0・負値の宣言で恒久停止に落ちないため |

## 母集合の引き直し（規則 9・10）

**「追随する文書」を記憶で挙げない。誤りの側の文字列で全文書を走査してから挙げる。**

### 走査 1 — 本件で**誤りになる**既存の記述（規則 10）

`git grep -n "再起動時の取りこぼし\|再起動すると"`（追跡下の全ファイル）:

| 箇所 | 扱い |
| --- | --- |
| `RiskManagementService/Program.cs:136` | **是正**（「こちらだけ永続化しても取りこぼしは解消しない」→ 鮮度つき観測で解消したことを書く） |
| `.../Persistence/InMemoryInformationDegradationStore.cs:8` | **是正**（同上。残余リスクの記述そのものが本 PR で消える） |
| `.ai-context/adr/IADR-0249_...md:55,61` | **日付つき追記**（凍結記録の本文プロズは書き換えず、`［2026-08-29 追記 / #564］` で解消を追記する） |
| `.ai-context/adr/README.md:282`（IADR-0249 の索引行） | **追記**（索引は live。残余リスクが解消済みであることを書く） |
| `.ai-context/adr/IADR-0196_fx-source-visibility.md:173` | **据え置き**（為替の可視化の話であり本件の射程外） |
| `.ai-context/adr/IADR-0053_...md:84` | **据え置き**（OpenD の SPOF。無関係） |

`git grep -l "IADR-0249"` の 18 ファイルのうち、上記以外は次のとおり分類した。

- **是正**: `IInformationDegradationStore.cs`（遷移だけを前提にした説明）・`InformationDegradationHandlers.cs`・
  `PortfolioSnapshotBuilder.cs`・`RiskEvaluator.cs`（いずれもコメントの前提が変わる）
- **据え置き**: `RejectionReason.cs` / `RejectionReasonClassification.cs` / 同テスト 2 本（**拒否理由を増やさない**ため無変更）・
  `PortfolioSnapshot.cs`（1 ビットの意味は不変）
- **凍結**: `.ai-context/specs/20260828_337_trading-cycle-and-screening.md` は **point-in-time の記録**であり
  書き換えない（当時の記述と食い違うため。`.claude/rules/traceability.repo.md` の除外と同じ理由）

### 走査 2 — 契約イベントを 1 種足したときの**全数レジストリ**（規則 9）

**記憶で挙げず、直近に追加されたイベント（`FxRateSourceUsed`・#513）の文字列で全追跡ファイルを走査**して引いた
（`git grep -l "FxRateSourceUsed"`）。21 ファイルのうち、**全数を要求する**レジストリは次の 5 つ＋索引 1。

| # | レジストリ | 何を要求するか |
| --- | --- | --- |
| 1 | `AuditEntryFactory.From` | 監査写像の全数（`AuditCycleCompletenessTests.監査写像は契約イベントの全数をカバーする`） |
| 2 | `AuditEventHandlers` | 監査コンシューマの全数（`AuditConsumerCoverageTests`） |
| 3 | `AuditCycleCompletenessTests.Samples()` | 実走標本の全数（`標本は契約イベントの全数と完全に一致する`） |
| 4 | `EventMessageTypeNameTests` の `InlineData` | Wolverine 識別子の固定（`識別子固定の対象はイベント型の母集合と完全に一致する`） |
| 5 | `event-schemas.baseline.json` | 契約の基準登録（`全イベントが基準に登録されている`。`UPDATE_EVENT_BASELINE=1` で再生成） |
| 6 | `.ai-context/adr/README.md` | IADR 索引（`check-adr-index-sync.js` が本文と索引行の同時変更を要求） |

**除外（全数ではないと実測で確認したもの）**:

| 除外 | 実測 |
| --- | --- |
| `NotificationFormatter` / `NotificationHandlers` | `InformationSourceDegraded` / `Recovered` の**いずれも通知ハンドラを持たない**（`grep` 実測 0 件）。通知は選んだ事象だけを出す設計であり、全数レジストリではない |
| `AuditEntryFactoryTests` / `AuditEventConsumersTests` | `InformationSource` の出現 0 件（イベントごとの個別テストであって全数ではない） |
| `ReportService/ControlActivationCatalogTests` | 同 0 件（拒否理由のカタログであり、本 PR は拒否理由を増やさない） |
| `docs/api/openapi.yaml` | イベントは HTTP 契約に現れない |

### 走査 3 — `InMemoryInformationDegradationStore` の生成点（コンストラクタ変更の影響）

`git grep -n "new InMemoryInformationDegradationStore()\|IInformationDegradationStore, InMemoryInformationDegradationStore"`
で **20 箇所 / 12 ファイル**。うち **縮退を関心に持たない 18 箇所**は、テストダブル
`FakeInformationDegradation.Affirmed()`（＝有効な観測があり停止カテゴリなし）へ置き換える
（`FakeBrokerAccountObservations.NotObserved()` と同じ作法。実物のストアを使い続けると、
**縮退と無関係なテストが「観測が無いので止まる」で落ちる**）。
残り 2 ファイル（`InformationDegradationScreeningTests` / `InformationDegradationConsumerTests`）は
**実物を使い続け**、鮮度の観測を明示的に投入する。

### 導出値の再計算（規則 10）

- **テストプロジェクト数**（`node scripts/list-test-projects.js --count`）は本 PR で**変わらない**（新規プロジェクトを作らない）。
  カバレッジ検証時に**実測して**レポート件数と突き合わせる（記憶の数を書かない）。
- **契約イベント数**は 45 → 46。**数を書いた記述は置かない**（全数テストが母集合から引くため、どこにも数は要らない）。

## 実装計画

| # | ファイル | 変更 |
| --- | --- | --- |
| 1 | `Shared.Contracts/Events/InformationSourceStateObserved.cs` | **新規**。現況観測の契約 |
| 2 | `InformationCollectionService/Hosted/DegradationStateTracker.cs` | 現況観測を毎巡回 1 件返す。`ValidFor` はコンストラクタで受ける |
| 3 | `InformationCollectionService/Hosted/CollectionPollingService.cs` | tracker へ実効の有効期間（巡回間隔 × 2・下限 5 分）を与える |
| 4 | `RiskManagementService/Features/RiskManagement/IInformationDegradationStore.cs` | `ApplyObservation` を追加。契約（不明は止める）を明記 |
| 5 | `RiskManagementService/Infrastructure/Persistence/InMemoryInformationDegradationStore.cs` | 鮮度つきへ。`TimeProvider` を要求・クランプ・逆行観測の無視 |
| 6 | `RiskManagementService/Infrastructure/Steps/InformationDegradationHandlers.cs` | 現況観測のハンドラを追加 |
| 7 | `RiskManagementService/Program.cs` | `TimeProvider` を渡す登録へ |
| 8 | `AuditService`（写像・ハンドラ） | 監査台帳への記録（全数レジストリ 1・2） |
| 9 | 各全数レジストリ・テスト | 走査 2 の表のとおり |
| 10 | `docs/functional/FR-10_risk-controls.md` / `docs/tests/FR-10_risk-controls-tests.md` | FR-10 は必須範囲。統制の向き（不明は止める）と 3 点セットを追記 |
| 11 | `.ai-context/adr/IADR-0267_*.md` ＋ 索引 | 本決定の記録 |

## テスト計画（統制系の 3 点セット）

**発行側**（`DegradationStateTrackerTests`）

- 肯定形: 縮退が**続いている間**（遷移が無い巡回）も、現況観測が**毎巡回 1 件**返り、停止カテゴリを載せる
- 肯定形: 縮退が無い巡回でも観測は返る（**空の停止カテゴリ**を明示的に運ぶ＝「健全」の宣言）
- 否定形: 観測は遷移イベントを**置き換えない**（初回は遷移＋観測の 2 件）

**受け側**（`InformationDegradationStoreFreshnessTests` 新設）

1. **境界値**: `ValidFor` ちょうどは有効・1 tick 超で失効（`BrokerAccountObservationStoreTests` と同じ形）。
   クランプの境界（下限 1 分未満・上限 2 時間超）
2. **プロパティベース**: (観測の有無) × (失効の有無) × (停止カテゴリの有無) の**全 8 通り**で
   **「`BlocksNewEntries=false` になるのは『有効な観測 ∧ 停止カテゴリなし』のときだけ」**が常に成り立つ
3. **否定形**:
   - **再起動直後（観測なし）は新規建てを止める**（＝受け入れ基準②の本体）
   - 対の肯定形: 有効な観測（停止カテゴリなし）を受けたら通る
   - **逆行する観測は新しい状態を消さない**
   - **遷移だけでは鮮度が回復しない**（`MarkRecovered` で集合が空になっても、観測が無ければ止まったまま）

**結線**（`InformationDegradationConsumerTests` / `InformationDegradationScreeningTests`）

- **受け入れ基準①**: 再起動を模した**空のストア**へ、**遷移イベントを 1 件も与えず**現況観測だけを届けると
  新規建ての停止が**復元される**
- **受け入れ基準③（回帰）**: 縮退中でも**決済は承認される**（既存の否定形テストを維持）

## 受け入れ基準との対応

| issue の基準 | 満たす手段 |
| --- | --- |
| ① 縮退継続中に再起動しても停止が復元される | 毎巡回の現況観測（決定1・2）＋ 結線テスト |
| ② 復元できない場合は止める側に倒す（否定形＋対の肯定形） | 鮮度つきストア（決定3）＋ 否定形／肯定形／プロパティの 3 点セット |
| ③ 決済は止まらない | `RiskEvaluator` を変更しない（決定6）＋ 既存否定形テストの回帰 |

## 残余リスク

- **抑止・鮮度はプロセスごと**（in-memory）。リスク管理を水平展開すると、各インスタンスが個別に観測を受ける
  （fan-out のため各インスタンスへ届く＝統制としては正しく働く）。**収集サービスを水平展開すると
  観測が多重に出る**（台帳の行数が増えるだけで、統制の向きは変わらない）。
- **費用統制が Halted の巡回では観測が出ない**（収集そのものを行わないため）。`ValidFor` を過ぎれば
  新規建ては止まる。**これは意図した向き**である（新しい情報を取れていないのだから新規建てはしない）。
- **監査台帳の行数が増える**（30 分巡回で 1 日 48 行）。`BrokerAvailabilityObserved`（probe ごと 1 行）と同じ桁であり、
  既に受容している水準である。
- **収集サービスが External トリガ（K8s CronJob）のとき、`ValidFor` の基礎になる `Collection:PollIntervalSeconds` は
  cron の周期と自動では一致しない。** ずれた場合は**観測が早く失効し新規建てが止まる**（安全側）。
  運用としては両者を揃える必要があり、`appsettings` へ注記する。
