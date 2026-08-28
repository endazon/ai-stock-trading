---
title: 報告サイクル・報告書テンプレートの再実装（#338）
type: spec
status: draft
related_ids: [FR-06, FR-07, FR-08, FR-16, FR-17, UC-03, UC-04, UC-05, ADR-0014, ADR-0015, ADR-0016, ADR-0017, IADR-0250, IADR-0251, IADR-0252, IADR-0253, IADR-0254]
author: 実装エージェント
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md
  - planning:projects/ai-stock-trading/04_workflows/03_reporting-cycle.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# 仕様書: 報告サイクル・報告書テンプレートの再実装（#338）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-06（報告書の階層管理）・FR-07（対話的確定）・FR-08（KB 保存 / RAG）・FR-16（数値はコード集計）・FR-17（前提条件バージョン）
- ユースケース（UC）: UC-03（月報）・UC-04（週報）・UC-05（日報）
- 画面（SC）: なし（報告書は基盤 UI / Discord。専用 SC を持たない）
- 関連 ADR: ADR-0014（用途別モデル）・ADR-0015（月報 ZDR モデル）・ADR-0016（空売り段階解禁・決定 15 報告書要件）・ADR-0017（フォールバック方針・決定 2 / 決定 4）
- 計画書リンク: `projects/ai-stock-trading/06_technical/04_report-templates.md`（fixed）/
  `projects/ai-stock-trading/04_workflows/03_reporting-cycle.md`（fixed）/ `INDEX.md` 決定 29・34・43・44・45

### 決定番号の実体（計画 INDEX で確認済み・2026-08-28）

| 決定 | 実体 |
| --- | --- |
| 29 | 目標値の構造化形式＝**YAML ブロック併記**（報告書 Markdown 内に人間可読の表と機械パース用 YAML を併記し、取引判断サービスは YAML のみを読む） |
| 34 | Stage 1 の期間カウント規則。**日報に当日の OpenD 稼働率と日数算入可否、月報に稼働率分布**を記載する |
| 43 | 報告書の機密区分＝**`internal`**（`internal` × ZDR 有効の構成） |
| 44 | スクリーニング層のコンテキスト超過時の縮退（分割 → RAG 削り → ニュース削り）。**月報に件数を、分割と切り詰めを分けて**記載する |
| 45 | 維持率割れによる自動縮小の算定規則。**発動を日報へ、発動回数を月報へ**記載する |

## 目的・背景

計画大改定で `06_technical/04_report-templates.md`（fixed）が大きく拡張された。現行の `ReportService` は
frontmatter ＋サマリ＋散文＋方針＋リスク統制の記録（維持率割れ自動縮小・強制買戻し推定・為替情報源・使用 LLM）
までを実装しているが、拡張後のテンプレートが求める次の記載事項を**まだ 1 つも出力していない**。

1. 機密区分 `confidentiality: internal`（決定 43）
2. 為替差損益の**独立行**（04_report-templates §数値の定義・§日報 §1・§月報 §1）
3. 月報 §5 三者比較（バックテスト / SIMULATE / 実弾）
4. 月報 §6 **統制作動状況**（「作動機会があり作動しなかった統制」と「作動機会そのものが存在しなかった統制」の分離）
5. 月報 §6.1 空売りの記録（借株コストの月次合計・ロング / ショート別・維持率の月内最低値）
6. 月報 §6.2 / 日報 §1 OpenD 稼働率（分布・当日値と Stage 1 日数算入可否）
7. 月報 §7 当月の LLM 利用実績（取引判断費用 / 報告書生成費用 / フォールバック発火 / 取引判断スキップ / **縮退件数**）
8. 日報 §1 モデル利用不能による取引判断スキップ回数（ADR-0017 決定 2）
9. 目標値の YAML ブロック併記（決定 29）

## 対象範囲

- **対象**: `ReportService`（Domain / Application / Infrastructure / Api）の描画・集計・供給。
  監査台帳（`GET /audit/events/by-type`）から引ける供給の結線。
- **対象外**:
  - `TradeDecisionService` の縮退（分割 / 切り詰め）の**発生源**。#337 の領域であり本 PR では触らない。
    本 PR は**受け口（未供給として明示する描画）だけ**を持つ。
  - `Shared.Contracts` への**新規イベント追加はしない**。したがって 7 レジストリ（`AuditEntryFactory` /
    `AuditEventHandlers` / `NotificationFormatter` / `NotificationHandlers` / `event-schemas.baseline.json` /
    `.ai-context/adr/README.md` / `NotificationTemplateGoldenTests`）と `EventMessageTypeNameTests` への追随は**発生しない**。
  - 実 KB / RAG 接続（FR-08 の受け入れ基準「確定報告書の**本文**が RAG 検索でヒットする」）。
    現行 `ReportKnowledgeMapper` は `Content: null` のカタログ登録のみであり、**本文を送る経路が platform 側に無い**
    （IADR-0069）。**実環境残件**として後述する。
  - 三者比較・OpenD 稼働率・為替差損益の**権威源への結線**。いずれも本リポ内に供給元が無い（後述）。
    本 PR は**集計の純関数と描画**を実装し、供給は `ReportView` の nullable プロパティを唯一の継ぎ目とする。

## 母集合の引き直し（`.claude/rules/traceability.md` 規則 1〜10）

**着手前に自分で引いた**。生の出力に対して判断しており、`head` / `sed` で切っていない。

| 軸 | 検索 | 生の結果 | 判断 |
| --- | --- | --- | --- |
| 1 | `grep -rn "new ReportView" --include=*.cs backend/`（obj 除く） | **1 件**（`ReportDraftService.cs:60`） | 追加プロパティは**すべて既定値つき**にすれば非破壊。テスト側は `with` 式で足す |
| 2 | `grep -rn "ReportRenderer.RenderMarkdown"`（同） | **49 件**（本番 1・テスト 48） | 本番の呼び出し点は 1 つ。既存テスト 48 件は「増えた節が既存の主張を壊さないか」の回帰網である |
| 3 | `grep -rn "new DraftRequest"`（同） | **2 件**（`ReportAutoGenerator.cs:100` / `ReportEndpoints.cs:96`） | `DraftRequest` の追加引数も既定値つきにする（2 箇所のうち手動生成の API 側は供給を持たない） |
| 4 | `ls backend/Services/ReportService/tests/*/` | 4 プロジェクト・**37 テストファイル** | 新規テストの置き場所は Domain（純関数）・Infrastructure（HTTP 供給）・Application（配線）に分ける |
| 5 | `grep -rn "LlmCostIncurred\|LlmFallbackFired\|TradeDecisionSkipped\|BorrowFeeAccrued" backend/Shared` | 契約は**既存**（追加不要） | 監査台帳の `EventType` は `nameof(型名)`。`by-type` で引ける |
| 6 | `grep -rn "切り詰め\|Truncat\|ScreeningInput\|分割回数" --include=*.cs backend/`（obj 除く） | **`AuditEntryFactory` の文字列切り詰め 21 件のみ**。スクリーニング縮退の型・イベントは**存在しない** | 決定 44 の縮退件数は**発生源が未実装**。#337 の供給を待つ受け口として実装する |
| 7 | `grep -rn "MapGet" AuditService.Api/.../AuditQueryEndpoints.cs` | `/audit/events/{correlationId}` / `/audit/events` / `/audit/events/by-type` の **3 本**。`by-type` のみ `OwnerOrService` | 新しい監査エンドポイントを足さずに済む（既存の `by-type` を再利用する） |

### 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| `backend/**/obj/**` | ビルド生成物。走査対象にすると同一ファイルを二重に数える |
| `src/ai-stock-trading` 相当の submodule | 本リポには存在しない（撤去済み） |
| `TradeDecisionService` 配下 | **#337 が同時に編集中**。触ると FIFO マージで衝突する（親エージェントの指示） |
| `docs/` 配下の機能仕様書 / テスト仕様書 | 網羅裁定 #211 の必須範囲は FR-10 / 12 / 15 / 19 / 20 であり、**FR-06 / 07 / 16 は必須外**。作業仕様書＋テストを正の記録とする |
| `Shared.Contracts` の 7 レジストリ | 新規イベントを追加しないため追随対象が生じない（軸 5 で確認） |

### 規則 8（自己参照）への対処

本仕様書は軸 6 の検索語（`切り詰め` 等）を**本文に含む**。上表の「21 件」は**本仕様書を書く前**の値であり、
本仕様書をコミットすると走査結果は増える。**21 件（着手前・本書を含まない）→ 本書コミット後は本書のヒットが加算される**、
という引き算を明示する。判定に用いた値は着手前の 21 件である。

## 設計

### 継ぎ目は `ReportView` の nullable プロパティ 1 本に閉じる（IADR-0250）

既存の `MarginReductions` / `BuyInInferences` / `FxSourceStatus` / `LlmModelUsage` が確立した規律をそのまま踏襲する。

- **`null` ＝供給されていない（照会できていない）**
- **空の値 ＝事象が無かった（正当な 0）**
- 両者を**同じ描画へ潰さない**。潰すと「統制が働いて 0 件だった」と「そもそも記録が無い」が読み分けられなくなる。

新規プロパティ（すべて既定 `null`・既存呼び出しは非破壊）:

| プロパティ | 型 | 供給 |
| --- | --- | --- |
| `LlmUsage` | `LlmUsageRecord?` | **本 PR で結線**（監査台帳 `by-type`） |
| `BorrowFees` | `BorrowFeeRecord?` | **本 PR で結線**（監査台帳 `by-type`） |
| `FxTranslation` | `FxTranslationSummary?` | 未結線（供給元が本リポに無い） |
| `Uptime` | `OpenDUptimeRecord?` | 未結線（Stage 1 稼働台帳はリスク管理サービスが持ち、期間照会 API が無い） |
| `ThreeWayComparison` | `ThreeWayComparison?` | 未結線（バックテスト結果 × 発注先別実績の横断が要る） |
| `ControlActivations` | `ControlActivationReport?` | **本 PR で算出**（既に取得済みの証拠から純関数で導く） |

### 統制作動状況（月報 §6）— 本 issue の 🔴 中核

`ControlActivationCatalog.Evaluate(...)` を**純関数**として置く。入力は**既に `ReportView` が持っている証拠**
（維持率割れ自動縮小・強制買戻し推定・借株料計上・為替情報源の状態）だけであり、新しい供給を要求しない。

出力は統制ごとに 4 値のいずれか。

| 区分 | 意味 | 描画先 |
| --- | --- | --- |
| `Activated` | 作動機会があり、**作動した** | 「当月に作動した統制」 |
| `OpportunityWithoutActivation` | 作動機会があり、**作動しなかった**（＝統制違反 0 件を主張できる） | §6 の 1 番目の一覧 |
| `NoOpportunity` | **作動機会そのものが存在しなかった**（＝未検証） | §6 の 2 番目の一覧 |
| `NotSupplied` | 証拠が照会できていない | 「判定できなかった統制」（**上の 2 つのどちらにも入れない**） |

🔴 **`NoOpportunity` を `OpportunityWithoutActivation` へ混ぜない。** 計画の明文:
「どちらも『0 件』と報告すると**検証されたものと検証されなかったものの区別が失われる**。Stage 2 昇格の判断には後者の一覧が要る。」
🔴 **`NotSupplied` を `NoOpportunity` へ倒さない。** 「照会できていない」を「機会が無かった」と書くのは、
既存の `AppendFxSourceStatus` / `AppendBuyInInferences` が一貫して避けてきた形と同型である。

### 数値の LLM 非介在（FR-16）をアーキテクチャで担保する（IADR-0251）

- 集計はすべて `ReportService.Domain` の**純関数**（`PnlAggregator` / `LlmUsageAggregator` /
  `BorrowFeeAggregator` / `FxTranslationAggregator` / `ControlActivationCatalog`）が行う。
- LLM が受け取る文脈（`ReportNarrativeContext`）は**新しい集計値を 1 つも持たない**。
  したがって LLM は本 PR が追加した数値を**見ることも書き換えることもできない**。
- 退行防止として「**散文を差し替えても数値節がバイト単位で一致する**」プロパティ的テストを置く
  （否定形「LLM が数値を書かない」の対の肯定形＝「数値はコード集計値のとおり出る」も同じテストで固定する）。

### 目標値の YAML ブロック併記（決定 29・IADR-0252）

方針節の直後に ```yaml フェンスを 1 つ出す。**取引判断サービスは YAML のみを読む**（Markdown 表はパースしない）。

- 機械が確実に知っている値だけを書く: `report_type` / `period` / `based_on` / `assumptions_version` /
  `confidentiality` / `status`。
- **売買条件（対象・条件・上限）は構造化された供給が無い。**
  よって `trading_conditions: null` と**明示**し、`trading_conditions_note` に「本文の方針は散文であり構造化されていない」
  と書く。🔴 **散文から条件を推測して YAML へ書き起こさない**——それは「数値・条件を機械が発明する」ことであり FR-16 に反する。
- **確定していない報告書の YAML は `status: draft`** であり、取引判断サービスが読んでも方針として採らない
  （確定済み日報の照会は `ReportService.GetConfirmedDailyPolicy` が唯一の経路であり、本 YAML はその経路を増やさない）。

### 既存の不具合論点

| 論点 | 現状 | 本 PR の対応 |
| --- | --- | --- |
| #282 報告書散文の LLM 費用計上 | PR #555 で `PublishingLlmUsageReporter` が `LlmCostIncurred(purpose)` を発行するようになり**計測点はできた** | **月報 §7 へ用途別の実測値を描く**（計上された費用が誰にも見えない状態を解消する）。取引判断費用と報告書生成費用を `LlmCostScope.IsGoverned` で分け、報告書生成は種別（monthly / weekly / daily）別に出す |
| #310 確定後の前置き文残留 | `ReportPolicyDraft.Substance` が生成文言を畳む（IADR-0125） | **回帰テストを増設**。加えて YAML ブロックが**確定後も「未確定」を名乗らない**こと（`status` が `ConfirmedAt` に従う）を固定する |
| #308 縮退時プレースホルダ | `ReportNarrativeTimeouts`（種別別上限）＋ `PlaceholderReportNarrativeDrafter` | **プレースホルダで生成した報告書は `LlmModelUsage` が `null`** ＝「散文生成に使用した LLM」節が出ない、という既存規律を回帰テストで固定する（節が出ないこと**と**、供給されたときに出ることの対で固定する） |
| #315 縮退時プレースホルダ（同上） | 同上 | 同上。加えて**プレースホルダ散文でも数値節は完全に出る**ことを固定する（散文の縮退が数値の欠落へ波及しない） |

## 受け入れ基準

- [ ] frontmatter に `confidentiality: internal` が出る（全報告書種別・決定 43）
- [ ] 日報・月報のサマリに**為替差損益の独立行**が出る。供給が無い場合は 0 ではなく**未供給**と明示する
- [ ] 日報のサマリに **OpenD 稼働率**（＋ Stage 1 日数算入可否）と**取引判断スキップ回数**が出る
- [ ] 月報に §三者比較の表が出る。**空欄（該当なし）と値 0 を区別**した表記になる
- [ ] 月報の統制作動状況が「**作動機会があり作動しなかった統制**」と「**作動機会そのものが存在しなかった統制**」を
      **別の一覧**として出す。両者を混ぜない。判定できなかった統制は**さらに別の一覧**へ出す
- [ ] 月報に **OpenD 稼働率の分布**（100% / 50〜99% / 50% 未満の日数と Stage 1 累計算入日数）が出る
- [ ] 月報に**当月の LLM 利用実績**が出る（取引判断費用と消費率 / 報告書生成費用の種別別 /
      フォールバック発火の用途別・原因別 / 取引判断スキップ回数 / **分割回数と切り詰め件数**）
- [ ] 月報に**借株コストの月次合計**（銘柄別内訳・**料率未供給日**を 0 と混ぜない）が出る
- [ ] 方針節の直後に **YAML ブロック**が併記され、`status` が確定状態に従う
- [ ] 数値は**すべてコード集計値**であり、散文を差し替えても数値節が変わらない
- [ ] 未確定の方針が取引へ適用されない（既存の `GetConfirmedDailyPolicy` の回帰）
- [ ] 無応答時の既定が「直近確定方針の継続」である（既存 `ReportNoResponsePolicy` の回帰）

## テスト方針

| # | 種別 | 内容 |
| --- | --- | --- |
| T-1 | **ゴールデン** | 日報 / 週報 / 月報 × 代表データで Markdown 全文を固定（`ReportTemplateGoldenTests`）。**供給あり**と**供給なし**の 2 系統を持つ |
| T-2 | 単体（境界値） | `LlmUsageAggregator`: 用途の分別（`report-*` / `trade-decision` / 用途なし）・消費率の 0 除算・境界（上限ちょうど） |
| T-3 | 単体（境界値） | `BorrowFeeAggregator`: 合計・銘柄別・**未供給日を 0 と混ぜない** |
| T-4 | 単体（境界値・プロパティ） | `FxTranslationAggregator`: 為替差損益＝Σ(額 ×(期末レート − 認識時レート))。レート同一なら必ず 0（プロパティ）・符号の対称性 |
| T-5 | 単体（否定形＋対の肯定形） | `ControlActivationCatalog`: 「作動機会なし」が「違反 0 件」の一覧へ**入らない**こと（否定形）と、機会があった統制が**そちらへ入る**こと（肯定形） |
| T-6 | 描画 | 各節の**未供給**表記が「0 件」「なし」と別文言であること／供給時に正しい数値が出ること（対で固定） |
| T-7 | 状態遷移 | 未確定方針が `GetConfirmedDailyPolicy` から返らない／確定後は返る（対）。無応答時の継続既定。確定後に「未確定」の語が本文へ残らない |
| T-8 | 配線 | `Program.cs` を起こし、`ILlmUsageRecordSource` / `IBorrowFeeRecordSource` が **No-op のままでない**ことを固定（`LlmGovernanceWiringTests` の先例に倣う） |
| T-9 | HTTP 供給 | 監査台帳の応答から復元できること・**非 2xx / 壊れた JSON / タイムアウトはすべて `null`（未供給）へ倒す**こと |
| T-10 | LLM 非介在 | 散文だけを差し替えた 2 つの描画で、**数値節が完全一致**する |

## 計画書との差異

- 差異: **あり**（いずれも計画の誤りではなく、**供給元が実装されていないことに起因する未達**）
  1. **三者比較（月報 §5）**: バックテスト結果（`BacktestService`）と発注先別の実績（`RiskManagementService`）を
     横断する集計 API が無い。本 PR は**表の枠と未供給表記**までを実装する。
  2. **OpenD 稼働率（日報 / 月報 §6.2）**: 稼働分数の積み上げは `RiskManagementService.Domain.Stage1SessionUptime` が
     持つが、**期間で引く照会 API が無い**（`GET /risk-controls/...` に該当エンドポイントが無いことを確認済み）。
  3. **為替差損益**: 認識時レートと期末レートの対を持つ供給が無い（`PeriodTradeFill.Price` は基準通貨へ換算済みで、
     **換算前の外貨額と換算レートを保持していない**）。集計の純関数は実装し、供給は残件とする。
  4. **縮退件数（分割 / 切り詰め）**: 発生源が #337 の領域。受け口のみ実装する。
  5. **FR-08 の RAG ヒット**: `ReportKnowledgeMapper` は本文を送らない（platform の `POST /documents` が本文を受けない・
     IADR-0069）。**確定報告書の本文が RAG でヒットする**という受け入れ基準は**実環境残件**である。
- いずれも「未供給」と明示して描画するため、**読み手が『0 件』と誤読する経路は作らない**。
  計画側への環流は不要（計画の記述に誤りは無く、実装側の供給が未整備なだけである）。

## 未決事項

- 為替差損益の供給を作るには、台帳の約定へ「換算前の外貨額」と「換算に用いたレート」を持たせる必要がある。
  これは取引台帳（リスク管理サービス）の schema 変更であり、本 PR の範囲を超える。**別 issue で起票すべき残件**として記す。
