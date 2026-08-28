---
title: 情報収集の再実装 — 情報源 4 区分・欠測時の縮退 3 種・ニュース必須条件・一般 Web の発動条件
type: spec
status: approved
related_ids: [FR-01, FR-08, FR-09, FR-11, UC-01, ADR-0004, ADR-0005, ADR-0020, IADR-0220, IADR-0221, IADR-0222, IADR-0223, IADR-0224]
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0005_paid-datasource-policy.md
  - planning:projects/ai-stock-trading/06_technical/02_datasource-candidates.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
related_specs:
  - 20260717_information-collection-official-connectors.md
  - 20260807_381_fx-freshness-degradation.md
---

# 仕様書: 情報源の 4 区分と欠測時の縮退（#336）

## 起点

- 起点 issue: **#336**（親 #344 フェーズ 2）
- 起点 ID: **FR-01**（情報収集・Must）/ FR-08 / FR-09 / FR-11 / **UC-01** / **ADR-0020**（区分と縮退）/ ADR-0004（案A+）/ ADR-0005（有料化プロセス）
- 実測時点: 本リポ `claude/ast-implementation-issues-rzkoxb-w1c` = `0844b58`（origin/develop 起点）/ 計画リポ隣接クローン `666965a`
- 計画書の一次情報: `ADR-0020`（Accepted・2026-08-17）と `06_technical/02_datasource-candidates.md`「情報源の区分」（fixed）。**区分の割当表と欠測時の扱いの正は後者**である（ADR-0020 §決定 が明示）。

## 課題（ギャップ分析）

**11 サービスは実装済みであり、本作業はゼロからの再実装ではない。** 現行 `InformationCollectionService`
（`SourceAllowlist` / `CompositeInformationSource` / 5 コネクタ / `CollectionPollingService`）を実測して、
ADR-0020 の要求との差分を取った。

| # | 要求（ADR-0020 / 02_datasource-candidates） | 現状（実測） | 差分（本 PR で実装） |
| --- | --- | --- | --- |
| 1 | 情報源に **必須 / 推奨 / 任意 / 検証用途** の 4 区分を与える | **区分の概念が無い**。`CollectionSourceOptions.Provider` に列挙したソースが等しく有効化されるだけ | `InformationSourceCatalog`（区分・カテゴリ・欠測時の振る舞い・既定の有効/無効を持つ表）を新設 |
| 2 | **検証用途はライブの取引判断の入力にしてはならない** | 強制する仕組みが無い（許可リストは「受理してよい源」しか見ない） | 収集段で**検証用途区分のアイテムを破棄**する（構造で禁止する） |
| 3 | ニュース系は **Finnhub 企業ニュース と Google News RSS のいずれか 1 つ以上が生きていること** | **ニュース源が 1 つも無い**。`FinnhubInformationSource` は現在値（quote）のみ。Google News RSS 未実装（#300 が本 issue へ吸収） | `FinnhubCompanyNewsSource` / `GoogleNewsRssSource` を新設し、「いずれか 1 つ以上」判定を実装 |
| 4 | 欠測時の振る舞いは **3 種に限る**（サイクル中止 / 限定縮退 / 記録・通知のみ） | `CompositeInformationSource` が失敗をログするのみ。**どのソースが落ちたかを呼び出し側が知る手段が無い**（戻り値はアイテムの平坦な列） | ソース単位の成否（`SourceOutcome`）を返す `SourceFetchRunner` へ置き換え、`DegradationEvaluator` が 3 種を判定 |
| 5 | **限定縮退でも手仕舞い・損切りは止めない**（新規建てのみ停止） | 概念が無い | `CollectionDegradation` を新設。`BlocksNewEntries` は持つが、**手仕舞い・損切りを止める表現を型として持たない**（`ClosesAllowed` / `StopLossAllowed` は定数 `true`） |
| 6 | 欠測を**無言で空データとして渡さない**（取引判断の文脈へ明示） | 空のまま渡る | ニュース全滅時に「欠測している」ことを述べる `SourceStatus` 種別のドキュメントを KB へ書き、RAG 経由で判断文脈へ載せる |
| 7 | 欠測の**発生時刻・継続時間・該当サイクル数**を日報／月報へ | 記録が無い | `InformationSourceDegraded` / `InformationSourceRecovered` を遷移でのみ発行（FX 劣化＝IADR-0196 と同型）。監査台帳の**種別 × 期間照会**が集計経路 |
| 8 | **一般 Web の発動条件 4 件**・次回月報までの暫定・記録 | 無い | `GeneralWebActivationPolicy`（4 条件 AND・境界 5 営業日）と承認エンドポイント、`GeneralWebCollectionStateChanged` の記録 |
| 9 | 必須ソースの有料化は ADR-0005 のプロセスへ回し、判断まで**推奨へ一時降格** | 無い | `InformationSourceCatalog.DemoteToRecommended`（構成で降格・欠測時の扱いを「記録のみ」へ切替） |
| 10 | 定時収集（既定: 判断用は開場中 30 分毎・報告用は日次） | `CollectionOptions.PollIntervalSeconds` 既定 **1800 秒**（＝30 分）。**報告用の日次は未分離** | 既定は現行どおり据え置き。**報告用の日次収集は本 PR の対象外**（後述「対象外」） |
| 11 | Finnhub Free の**実効レート制限を実測**し監視銘柄数上限を逆算 | レート制限が `InformationSourceFactory` に**ハードコード**（`Limiter(30, 1分)` 等）。日次上限の概念が無い | **設定値へ外出し**し、日次上限は `null`＝**未実測**を既定とする。逆算は `FinnhubQuotaCalculator`（純関数）で用意し、**実測値の投入は後日**（後述「実環境依存の残件」） |

### 区分の割当（計画表の転記。実装するカタログの初期値）

`02_datasource-candidates`「区分の割当」表を写像した。**本サービスが収集主体でないもの**（moomoo のライブ市況・
為替の日銀/FRED 経路）も、判定器が扱えるようカタログには載せる（後述の「本サービスで観測できない源」を参照）。

| 名前 | カテゴリ | 区分 | 欠測時 | 備考 |
| --- | --- | --- | --- | --- |
| `moomoo` | market-live | 必須 | **サイクル中止** | 発注経路そのもの。**本サービスは可用性を観測しない**（後述） |
| `finnhub` | market-live | 必須 | 記録・通知のみ | 米国株の冗長系（市況面） |
| `finnhub-news` | news | 必須 | **限定縮退**（ニュース系） | ニュース系の第一 |
| `google-news` | news | 必須 | **限定縮退**（ニュース系） | ニュース系の代替 |
| `sec-edgar` | disclosure-us | 必須 | 記録・通知のみ | 24 時間以上の継続で通知（継続時間はイベントが持つ） |
| `fred` | macro | 必須 | 記録・通知のみ | 為替のフォールバック源としての統制は TradeDecision 側（IADR-0194/0196）が既に持つ |
| `finra-short` | supply-us | 必須 | **限定縮退（空売りのみ）** | 空売りの新規建てを止める。手仕舞い・買戻しは止めない |
| `gdelt` / `edinet` / `boj` | news-tone / disclosure-jp / macro-jp | 推奨 | 記録のみ | 既定で有効（構成が揃えば） |
| `tdnet-yanoshin` / `jpx-supply` / `e-stat` / `sec-edgar-13f` / `reddit` / `investing-rss` | — | 任意 | 記録のみ | 既定で無効 |
| `jquants` / `stooq` | verification | **検証用途** | — | **ライブ判断へ入れない**（収集段で破棄） |

- **`boj` の扱い**: ADR-0020 決定 1 は日銀 時系列統計 API を推奨に置き、割当表は「日銀『外国為替市況（日次）』＝必須（為替の第一）」「日銀（為替以外）＝推奨」と分けている。**本サービスの `boj` コネクタはマクロ統計（為替以外）** であるため**推奨**とする。為替の第一／フォールバックの統制は TradeDecision の `BojFxRateSource` / `FallbackFxRateSource` が既に実装済みであり（IADR-0194 / IADR-0196）、二重に持たない。
- **推奨・任意の欠測**は「記録のみ」であり、必須の 3 種とは別である（決定 3 の「3 種」は**必須ソース**に対する規定）。

### 本サービスで観測できない源（限界の明示）

`moomoo`（ライブ市況・発注）と FINRA 空売りデータは**本サービスのコネクタとして存在しない**（前者は
`MarketMonitorService` / ブローカ経路、後者は未実装）。したがって「サイクル中止」「空売りの限定縮退」は
**判定器としては実装されテストされるが、実際の可用性信号はまだ結線されていない**。

- **黙って「統制が働いている」と書かない**（CLAUDE.md の統制記述ルール・planning#286 の裁定と同じ向き）。
- **暫定手段**: `moomoo` の可用性は既存の `BrokerAvailabilityObserved` が既に流れており、これを収集側へ引き込む
  結線は取引サイクルの結線（#337）の射程である。本 PR は**判定器と記録の側を用意する**に留める。
- **構成されていない必須ソースは「欠測」に数えない。** 数えると、外部接続しない安全既定（IADR-0022）のままで
  毎サイクルが中止になる。未構成は `UnconfiguredRequired` として**警告に出す**（記録はする・止めはしない）。
  未構成のままではそもそもアイテムが 0 件となり `InformationCollected` が出ないため、取引サイクルは動かない
  ——**止めるべきものは構造的に止まっている。**

## 対象範囲

- **対象**: 上表 1〜9 と 11 の「設定値への外出し」。`InformationCollectionService`（Domain / Application /
  Infrastructure / Api）、`Shared.Contracts`（新規イベント 3 種）、`AuditService`（新規イベントの台帳記録）、
  `TradeDecisionService`（RAG 注入側の許可語彙の追随 1 箇所）。
- **対象外**:
  - **報告用の日次収集の分離**（表 10）。収集間隔の用途別分離は SC-01 の収集パラメータ供給（IADR-0155）と
    報告サイクル（#338）に跨るため、本 PR では既定 30 分の判断用のみを維持する。
  - **月報テンプレートへの描画**。`ReportService` の期間集計・描画は #338 の射程。本 PR は**監査台帳の
    種別 × 期間照会（`GET /audit/events/by-type`。IADR-0199 決定 2）まで**を到達点とする。
  - **一般 Web の実収集コネクタ**。発動条件の判定と記録を実装し、**取得の実装は行わない**（承認前に
    取得経路を作らない。ADR-0020 決定 4 は「利用者の承認を得て暫定措置として用いる」であり、
    条件成立前にコネクタを持つ理由が無い）。
  - **Finnhub Free の実効レート制限の実測**（実 API が要る。後述）。
  - `Shared.KnowledgeBase` と `KnowledgeBaseWriterSink.ToDocument`（#520 で並行改修中のため触らない）。

## 設計

### 1. Domain（新設）

| 型 | 役割 |
| --- | --- |
| `SourceTier` | `Required` / `Recommended` / `Optional` / `VerificationOnly`（4 区分） |
| `MissingSourceBehavior` | `AbortCycle` / `LimitedDegradation` / `RecordAndNotifyOnly`（**3 種に限る**） |
| `InformationSourceDefinition` | 名前・カテゴリ・区分・欠測時の振る舞い・既定の有効/無効・空売り限定か |
| `InformationSourceCatalog` | 上表を初期値に持つ。`DemoteToRecommended(name)`（ADR-0005 決定 5 の一時降格） |
| `SourceOutcome` | ソース単位の成否（`Name` / `Succeeded`） |
| `CollectionDegradation` | 判定結果。`AbortCycle` / `BlocksNewEntries` / `BlocksShortEntries` / `NewsOutage` / `MissingRequired` / `UnconfiguredRequired` / `Notifications`。**`ClosesAllowed` と `StopLossAllowed` は常に `true`** |
| `DegradationEvaluator` | カタログ × 成否 → `CollectionDegradation`（純関数） |
| `GeneralWebActivationPolicy` | 4 条件の AND 判定。境界は**欠測 5 営業日以上**。`ProvisionalUntil` ＝ 翌月 1 日 00:00Z（次回月報） |
| `FinnhubQuotaCalculator` | 日次上限から監視銘柄数の上限を逆算する純関数。**上限が未実測（`null`）なら `null` を返す** |
| `DegradationNotice` | ニュース欠測を述べる `SourceStatus` 種別の収集情報を作る |

**「手仕舞い・損切りを止める」を型として表現しない。** `CollectionDegradation` は新規建ての停止しか持たず、
決済側は定数の `true` である。これは FX 鮮度切れ（IADR-0197）で「ゲートで止めると出口まで塞がる」ことを
学んだ形と同じ規律であり、**フラグの組み合わせ次第で出口が塞がる状態を作らない**ためである。

### 2. Application

- `NamedInformationSource(string Name, IInformationSource Source)` —— **ソース名を実行時まで保持する**
  （現行はファクトリを出た瞬間に名前が消え、どれが落ちたか判定できなかった）。
- `SourceFetchRunner` —— 各ソースを順に呼び、**失敗をソース単位で隔離**して `SourceOutcome` を残す。
  現行 `CompositeInformationSource` の隔離ロジックを引き継ぎ、**同クラスは撤去する**（同じ責務を 2 実装に割らない）。
- `InformationCollectionService.CollectAsync` の流れ:
  1. `SourceFetchRunner.FetchAllAsync` → アイテム＋成否
  2. `DegradationEvaluator.Evaluate(catalog, outcomes)` → 縮退判定
  3. **検証用途区分のアイテムを破棄** → 許可リスト選別 → 正規化・サニタイズ（現行どおり）
  4. ニュース全滅なら**欠測の明示ドキュメント**を追加
  5. KB 保存 → `CollectionResult(ItemCount, Items, Degradation)`
- `DegradationStateTracker` —— 遷移でのみイベントを返す（`FxSourceStatusTracker` と同型）。継続中は黙り、
  復帰時に**発生時刻・継続時間・該当サイクル数**を載せて `InformationSourceRecovered` を返す。

### 3. Infrastructure

- `FinnhubCompanyNewsSource`（`/api/v1/company-news`）: 既存の Finnhub API キー・銘柄設定を共用する。
- `GoogleNewsRssSource`（`https://news.google.com/rss/search?q=...`）: キー不要。`XDocument` で RSS を解析。
- レート制限の**設定値化**: `Collection:Source:<源>:RateLimitPerMinute`（既定は現行のハードコード値と同値）。
  `Collection:Source:Finnhub:DailyRequestLimit` は **`null`＝未実測**を既定とし、設定されたときだけ
  監視銘柄数の上限を逆算してログへ出す。
- `InformationSourceFactory` は `IReadOnlyList<NamedInformationSource>` を返す（no-op も名前を持たない空集合で表す）。

### 4. Api

- `POST /internal/collection/general-web-activation`（**OwnerOnly**。利用者の承認そのものであるため
  run-once（OwnerOrService）とは非対称にする）: 4 条件を判定し、成立時のみ
  `GeneralWebCollectionStateChanged(Engaged: true, ProvisionalUntil: 次回月報)` を発行する。
  不成立は 400 と**満たしていない条件の列挙**を返す（推測で先行しないことを機械で担保する）。

### 5. イベント（`Shared.Contracts.Events`。後方互換の追加）

| イベント | 内容 |
| --- | --- |
| `InformationSourceDegraded` | カテゴリ・振る舞い・欠測ソース・新規建てを止めるか・発生時刻 |
| `InformationSourceRecovered` | 発生時刻・**継続時間**・**該当サイクル数**（受け手に引き算させない。IADR-0196 と同じ規律） |
| `GeneralWebCollectionStateChanged` | 発動／解除・理由・暫定期限。**発動と解除を 1 契約に置く**（受け手が 2 系統を購読しない） |

追加に伴い必須となる追随（**母集合の規則 9・10**）: 監査ハンドラ 3 本・`AuditEntryFactory` 3 写像・
`EventMessageTypeNameTests` の 3 行・イベント契約ベースライン（`UPDATE_EVENT_BASELINE=1`）。

## 母集合の引き直し（`.claude/rules/traceability.repo.md` 規則 9・10）

**誤りの側・変更の側から引いた。** 走査は `git grep`（追跡下の全ファイル・拡張子で絞らない）で行い、
生の出力を判断に用いた（規則 3・7）。

| 軸 | 走査 | 件数 | 扱い |
| --- | --- | --- | --- |
| 1. 情報源名を列挙する箇所 | `git grep -l -i finnhub` | **79 ファイル** | 新ソース名（`finnhub-news` / `google-news`）を足すと追随が要るのは**語彙を列挙している 6 箇所**: `SourceAllowlist.Default` / `RetrievalSourcePolicy.Default` / `appsettings.Development.json` / `.env.example` / `docker-compose.yml` / `deploy/helm/.../values*.yaml`。**残りは既存ソースの実装・テスト・IADR・仕様書**であり、新ソースの追加で誤りにならない（凍結記録である `.ai-context/` は当時の記述として残す） |
| 2. 収集許可語彙と RAG 注入語彙の対応 | `git grep -n "SourceAllowlist\|RetrievalSourcePolicy"` | 実装 2 ＋ 検査 1（`RetrievalSourceVocabularyTests`） | **機械検査がある**。片側だけ足すと落ちるため、両方＋自リポ文書種別（`collection-status`）を同時に直す |
| 3. レート制限の数値 | `git grep -n "Limiter(\|60 回/分\|10 回/秒"` | `InformationSourceFactory` 5 箇所＋コメント（`FinnhubQuoteClient` / IADR-0064） | 外出しするのは**本サービスの 5 箇所**のみ。共有の `FinnhubQuoteClient`（市況・IADR-0068）は別枠の予算であり触らない |
| 4. イベント追加に追随する箇所 | `git grep -n "GetEventTypes()"` と監査カバレッジ | 4 ファイル | `EventTypeDiscovery` を母集合とする検査が 3 本（後方互換・識別子・監査カバレッジ）。3 本すべてを満たす |
| 5. 「1 巡回 = 1 `InformationCollected`」の前提 | `git grep -n "InformationCollected"` | 実装 3 ＋ テスト 5 ＋ 契約 1 | サイクル中止時に**発行しない**分岐を足すだけで、既存の意味は変えない |

**除外したものと理由**（規則 6）:

- `CHANGELOG.md` —— 生成物。コミット件名から再生成される（是正が要るなら `changelog-overrides.json`）。
- `.ai-context/specs/` の既存仕様書 —— **point-in-time の記録**であり、当時の記述を後から書き換えない。
- `frontend/` —— 情報源の区分を表示する画面は計画に無い（SC-01〜03 のいずれにも欄が無い）。
- 計画リポジトリ —— 読み取り専用。差異があれば issue で環流する（本 PR では差異なし）。

**自己参照の除外（規則 8）**: 軸 1 の 79 ファイルは**本仕様書を書く前**の数である。本書自身が `finnhub` を
含むため、コミット後に同じ走査を行うと 80 ファイルになる（79 → 自己参照 1 を足す → 80）。

## 受け入れ基準（issue #336 §退行防止）

- [x] **区分 × 欠測の判定テーブルテスト**（必須欠測 → サイクル中止 or 縮退の分岐・ニュース系の「いずれか 1 つ以上」判定）
- [x] **縮退中でも手仕舞い・損切り経路が生きていることのテスト**（否定形: 新規建てのみが止まる）
- [x] **一般 Web 発動条件 4 件の境界テスト**と、**発動記録が月次の期間集計へ届くこと**のテスト
- [x] **コネクタ単位のフェイク／録画ベーステスト**（外部 API を CI で叩かない）
- [x] 検証用途区分がライブ判断の入力に入らないこと
- [x] 一時降格（ADR-0005 決定 5）で欠測時の扱いが「記録のみ」へ切り替わること

> 🔴 **「新規建てのみが止まる」の到達点を誤読しないこと。** 上の 2 つ目で担保しているのは
> **`CollectionDegradation` の型が決済を止める表現を持たないこと**と、**縮退時も手仕舞い・損切りの
> 経路が生きていること**である。**`BlocksNewEntries` を読んで新規発注を拒否するゲートは、本作業では
> 下流へ結線していない**（`TradeDecisionService` / `RiskManagementService` / `OrderExecutionService`
> のいずれからも参照ゼロ。実測）。現状、新規建ての抑止は `DegradationNotice` が KB へ書く文言を
> 判断 LLM が読んで自制することに委ねられており、**コードレベルの強制ではない**。
> 構造的な結線は **#337 の射程**である（IADR-0221「結果」節に同旨を記録済み）。

## テスト方針

統制系であるため**境界値テーブル・プロパティベース・否定形の 3 点セット**を揃える（`docs/tests/README.md`）。

| 受け入れ基準 | テスト |
| --- | --- |
| 区分 × 欠測の判定 | `DegradationEvaluatorTests`（`[Theory]` の判定テーブル。区分 4 × 振る舞い 3 の組み合わせ） |
| ニュース「いずれか 1 つ以上」 | 同上（片方生存 / 両方欠測 / 片方のみ構成 の 3 形） |
| **手仕舞い・損切りが生きる（否定形）** | `DegradationEvaluatorTests` のプロパティベース（**全組み合わせ**で `ClosesAllowed` / `StopLossAllowed` が真）＋ 収集経路の否定形 |
| 一般 Web 4 条件 | `GeneralWebActivationPolicyTests`（**4 営業日は不成立・5 営業日で成立**の境界、各条件の単独欠落 4 形） |
| 月次集計への到達 | `AuditEntryFactoryTests` ＋ `AuditEventStorePeriodQueryTests` 相当（種別 × 期間で引けること） |
| コネクタ | `FinnhubCompanyNewsSourceTests` / `GoogleNewsRssSourceTests`（**録画した応答**を返すフェイク `HttpMessageHandler`） |
| 検証用途の排除 | `InformationCollectionServiceTests`（`stooq` のアイテムが KB へ届かない） |

## 計画書との差異

- 差異: **なし**（実装できない点・計画の誤りは見つからなかった）。
- ただし**計画への環流候補が 1 件**ある: ADR-0020 決定 1 は日銀 時系列統計 API を一括で「推奨」に置くが、
  `02_datasource-candidates` の割当表は「為替（日次）＝必須／為替以外＝推奨」と分けている。**表が正**と
  ADR 自身が定めているため実装は表に従い矛盾は生じないが、**決定 1 の列挙だけを読むと取り違える**。
  文言の整合は計画側の判断であり、本 PR では実装しない（環流の要否は報告に残す）。

## 未決事項 / 実環境依存の残件

- 🔴 **Finnhub Free の実効レート制限は未実測である。** 公称 60 回/分に対し第三者検証で「1 日およそ 300 回」の
  観測がある（計画の出典 3）が、**これは実測値ではない**。本 PR は**日次上限を設定値（既定 `null`＝未実測）
  として外出しする**に留め、`FinnhubQuotaCalculator` は上限が未設定なら**銘柄数上限を返さない**。
  実測は実 API キーでの試行が要るため本 PR の射程外であり、結果は `/plan-feedback` で計画へ環流する。
- `moomoo` の可用性と FINRA 空売りデータの結線（前掲「本サービスで観測できない源」）。
- 月報テンプレートへの描画（#338）。
