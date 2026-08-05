---
title: 作業仕様書 — 運用段階と発注先（Broker Provider）の 2 軸分離（実弾切替の明示確認・paper 警告バナー・Stage 1 集計からの paper 除外）
type: work
status: review
related_ids: [FR-20, FR-12, FR-13, FR-10, FR-11, UC-06, SC-01, SC-02, SC-03, ADR-0008, ADR-0016, IADR-0111, IADR-0127, IADR-0136, IADR-0137, IADR-0140, IADR-0141, IADR-0142]
author: endazon (with Claude Code)
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - ../../planning/projects/ai-stock-trading/INDEX.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/05_screens/01_screens.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
related_specs:
  - ../adr/IADR-0140_broker-provider-axis.md
  - ../adr/IADR-0141_live-switch-explicit-confirmation.md
  - ../adr/IADR-0142_stage1-simulate-only-aggregation.md
  - ../adr/IADR-0111_broker-tier-selection.md
  - ../adr/IADR-0127_plan-conformance-known-deviation-registry.md
  - ../functional/FR-20_staged-gates.md
  - ../functional/FR-12_paper-trade.md
  - ../tests/FR-20_staged-gates-tests.md
  - ../tests/FR-12_paper-trade-tests.md
  - ../tests/README.md
  - ../DEFINITION_OF_DONE.md
  - 20260804_333_stage-gate.md
---

# 作業仕様書: 運用段階と発注先の 2 軸分離（#334）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-20**（段階ゲート・2 軸分離の主たる帰属）／ **FR-12**（内蔵 `paper` と警告バナー）／ **FR-13**（設定変更・理由必須・監査・版）
- ユースケース（UC）: **UC-06**（設定変更・現在状態の参照）
- 画面（SC）: **SC-01**（バナーのみ）／ **SC-02**（発注先の表示＋変更・実弾切替の警告モーダル）／ **SC-03**（発注先の参照・変更履歴）
- 関連 ADR: **ADR-0008**（段階ゲート）／ **ADR-0016**（段階別の商品種別）
- 実装 ADR: [IADR-0140](../adr/IADR-0140_broker-provider-axis.md)（`BrokerProvider` の導入と `TradeMode` の廃止）／
  [IADR-0141](../adr/IADR-0141_live-switch-explicit-confirmation.md)（実弾切替の明示確認をサーバ側でも強制する）／
  [IADR-0142](../adr/IADR-0142_stage1-simulate-only-aggregation.md)（Stage 1 集計から内蔵 `paper` を構造的に排除する）
- 起点 issue: [#334](https://github.com/endazon/ai-stock-trading/issues/334)（親 [#344](https://github.com/endazon/ai-stock-trading/issues/344)）。
  置換: [#217](https://github.com/endazon/ai-stock-trading/issues/217)（単一 config フリップ設計）。後続: [#386](https://github.com/endazon/ai-stock-trading/issues/386)（Stage 1 集計の供給元）
- 計画書リンク: [INDEX 決定 46](../../planning/projects/ai-stock-trading/INDEX.md) ／
  [05_screens「運用段階（Stage）と発注先（Broker Provider）の表示規約（共通）」](../../planning/projects/ai-stock-trading/05_screens/01_screens.md) ／
  [02_requirements FR-12 / FR-13 / FR-20](../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md) ／
  [06_daytrading-review §4.1・§4.2](../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md)

## 計画一次情報との突合表

| # | 論点 | 計画原文（出典） | 着手前の実装 | 本作業での実装 |
| --- | --- | --- | --- | --- |
| 1 | 2 軸分離 | 「『運用段階（Stage）』と『**発注先**（`moomoo REAL` / `moomoo SIMULATE` / 内蔵 `paper`）』を**独立した 2 軸**として扱う」（INDEX 決定 46・FR-20） | `TradeMode`（Paper / Live）が段階に従属し、発注先の軸が存在しない | `BrokerProvider`（3 値）を新設し `TradeMode` を**廃止**。現在の発注先は `RiskManagementSettings.BrokerProvider`（段階と独立に保持） |
| 2 | 発注先の 3 値 | 「moomoo `REAL`（実弾）／ moomoo `SIMULATE`（ブローカーのデモ環境へ OpenD 経由で実際に発注する）／ 内蔵 `paper`（本システム内蔵の擬似約定。外部へ発注しない。FR-12）の 3 値」（FR-20） | 2 値（Paper / Live） | `BrokerProvider { InternalPaper = 0, MoomooReal = 1, MoomooSimulate = 2 }`。**序数 0 / 1 は旧 `TradeMode` の意味を保存**（IADR-0140 決定2） |
| 3 | 段階が定める動作モード | 「段階が定める動作モードは**既定の組み合わせを示すにとどまる**」（FR-20） | `StageSettings.Mode`（`TradeMode`）が段階の強制値 | `StageSettings.Mode` の**型のみ** `BrokerProvider` へ。Stage 1 の既定は `MoomooSimulate`（計画確定値）。現在の発注先は別軸のため段階変更で自動追随しない |
| 4 | Stage 1 の既定発注先 | 「**Stage 1: SIMULATE** … 3 か月の moomoo `SIMULATE`（OpenD 経由のデモ環境）による取引」（06_daytrading-review §4 表） | `TradeMode.Paper` | `BrokerProvider.MoomooSimulate`（計画適合レジストリ `Stage.Stage1BrokerProvider` の逸脱を解消） |
| 5 | 変更操作の置き場所 | 「**発注先の変更は SC-02 のみが持つ。**（SC-03 は参照専用）」（05_screens 共通規約） | 変更 UI なし | SC-02 に発注先変更フォーム。SC-03 は表示のみ＋ SC-02 への導線 |
| 6 | 理由必須・監査・版 | 「他のリスク設定と同様に**変更理由を必須**（1 文字以上）とし、**監査ログに記録**し、**版（楽観排他）の対象に含める**」（05_screens・FR-13） | ガード・上限のみ（発注先が無い） | `RiskSettingsService.UpdateBrokerProvider`（理由必須）＋ `SettingsChangeType.BrokerProvider`（**序数 7・末尾追加**）＋既存の設定行 `Version`（楽観排他）に相乗り |
| 7 | 実弾切替の 4 点提示 | 「①これ以降の注文は実際の資金で執行される旨 ②切替先と現在の Stage の**組み合わせが妥当か**（Stage 1 のままなら**段階ゲートを飛ばしている**旨）③現在の equity と、それに対する統制値の実額 ④**確認のための明示的な操作**（チェックボックスの同意と「REAL」の文字入力）」（05_screens SC-02） | なし | SC-02 の警告モーダル（4 点を必ず描画）。**「OK」1 押しでは通過できない** |
| 8 | 明示確認の強制点 | 「**既定の『OK』ボタン 1 押しで切り替えられてはならない**」（FR-20 (1)） | なし | **画面とサーバの二重**。サーバは `MoomooReal` への変更要求に「同意フラグ」と「`REAL` の文字入力」の両方が無ければ 400（IADR-0141） |
| 9 | 組み合わせの妥当性 | 「運用段階との組み合わせは**保存を妨げない**が、段階が想定する発注先と異なる場合は**警告を表示する**（例: Stage 1 のまま `moomoo REAL`）」（05_screens SC-02 入力表） | なし | 保存は妨げない。段階の既定と食い違う場合に警告文言を出す（`BrokerProviderChange.SkipsStageGate`） |
| 10 | 変更履歴 | 「**発注先の変更は日時・変更前後・理由を変更履歴と監査ログに残す**」（FR-20 (2)） | なし | 既存 `SettingsChangeEntry`（Actor / ChangeType / Reason / ChangedAt / Before / After）に記録。SC-02・SC-03 の両方から参照 |
| 11 | Stage 1 集計の除外 | 「Stage 1 の合格判定（経過営業日数・取引件数・統制違反件数）は `SIMULATE` の約定のみで集計し、**内蔵 `paper` の約定・稼働日数を算入してはならない**」（FR-20） | 発注先を区別する型が無い（区別不能） | `Stage1TradingDayObservation` / `Stage1FillObservation` に **`BrokerProvider` を必須**で持たせ、`Stage1Aggregation` が `MoomooSimulate` 以外を構造的に落とす（IADR-0142）。**集計の供給元は #386** |
| 12 | 除外営業日数の別掲 | 「`paper` で稼働した営業日は**除外日数として別に数え**、進捗表示に併記する」（FR-20・SC-03） | なし | `Stage1Progress.ExcludedInternalPaperDays`。SC-03 の経過営業日表示に併記 |
| 13 | `paper` 警告バナー | 「**SC-01 / SC-02 / SC-03 のすべてで画面上部に常時表示する**」「『デバッグモードです。外部へ発注していません』」「『この期間は Stage 1 の実績に算入されません』」（FR-12・05_screens 共通規約） | なし | `PaperModeBanner` を SC-01 / SC-02 / SC-03 の先頭へ配置。**必須 2 文言を定数として固定**しテストで写像 |
| 14 | `paper` ラベル | 「統制状態のカード類（勝率・取引件数・稼働率など）にも **`paper` である旨のラベル**（例: `paper・参考値`）」（05_screens 共通規約） | なし | SC-03 の統制状態カードに `paper・参考値` ラベル |
| 15 | 表示箇所 | 「表示箇所は **SC-02 と SC-03 の 2 画面**」「**全画面ヘッダーへの常時表示は行わない**」（05_screens 共通規約） | — | SC-02・SC-03 のみ。共通シェル（基盤側）へは一切触れない |
| 16 | 1 行に混ぜない | 「運用段階と発注先は**独立した 2 軸**（**1 行に混ぜて表示しない**）」（05_screens 共通規約） | — | 段階と発注先を別の `<dt>/<dd>` 行として描画（テストで固定） |
| 17 | 用語 | 「`SIMULATE` を『ペーパー』と呼ばない。内蔵 `paper` を『SIMULATE』『デモ取引』と呼ばない。**『ペーパー』の語を単独で使わない**」（05_screens 共通規約） | 画面ラベルが `Stage 1（ペーパー）` | 段階ラベルを計画の呼称（検証 / SIMULATE / 最小実弾 / 段階増額）へ是正 |

## 計画が沈黙している論点（実装が決めたこと）

| # | 論点 | 計画の記述 | 実装の決定と理由 | 記録先 |
| --- | --- | --- | --- | --- |
| A | `TradeMode` を残すか廃すか | 記述なし（計画は「発注先」という 1 つの軸しか定義していない） | **廃止**。「実資金か」を表す情報源が 2 つあると必ず食い違い、食い違った側が実弾を素通しする | IADR-0140 決定1 |
| B | 発注先の初期値 | 記述なし（段階ごとの既定は定めるが、システム初期状態の発注先は述べていない） | **`InternalPaper`**（外部へ一度も発注しない値）。初期段階が Stage 0 であることとも整合する | IADR-0140 決定4 |
| C | 明示確認をサーバでも要求するか | 記述は画面（SC-02）に対してのみ | **要求する**。画面だけの統制は API 直叩きで消える。「止める」統制を画面に置き去りにしない | IADR-0141 決定1 |
| D | 文字入力の照合文字列 | 「『REAL』の文字入力」とだけある（大文字小文字・前後空白の扱いは無し） | **前後空白を除いた上での完全一致（大文字小文字を区別する）**。`real` を受理すると「REAL の文字入力」という計画の字面から外れる | IADR-0141 決定2 |
| E | Stage 1 集計で `MoomooReal` の約定をどう扱うか | 「`SIMULATE` の約定のみで集計」とあるだけで `REAL` は名指しされていない | **算入しない**（`MoomooSimulate` だけを算入する許可制）。名指しの無い値を算入すると、将来の発注先追加が黙って合格証跡へ流れ込む | IADR-0142 決定2 |
| F | 実弾の発注先を選んだときに発注が通るか | 「組み合わせは保存を妨げない」（設定について）／FR-20 本文は段階が動作モードを「強制できる」 | **設定は保存できるが、段階が実弾でない限り発注は `StageProhibitsLiveTrading` で止める**（従来動作を維持）。安全側 | IADR-0140 決定5 |

## 実装範囲（本 PR）

### バックエンド

1. `AiStockTrading.Shared.Contracts.Trading.BrokerProvider`（3 値）新設・`TradeMode` 削除。参照箇所を機械的に置換（プロパティ名 `Mode` は据え置き＝序数と JSON キーを動かさない）。
2. `TradingDefaults`: Stage 1 の既定発注先を `MoomooSimulate` へ。`CreateSettings()` の初期発注先＝ `InternalPaper`。
3. `RiskManagementSettings.BrokerProvider`（init プロパティ・既定 `InternalPaper`）＋ JSON 直列化（旧行は既定へフォールバック）。
4. `BrokerProviderChange`（ドメイン純関数）: 実弾切替の判定・段階ゲート飛ばしの警告・明示確認の検証。
5. `RiskSettingsService.UpdateBrokerProvider(...)` ＋ `SettingsChangeType.BrokerProvider`（序数 7・末尾追加）。
6. `PUT /risk-controls/settings/broker-provider`（OwnerOnly・理由必須・明示確認）。`GET /risk-controls/status` に発注先・1 注文上限の実額を追加。
7. Stage 1 集計の構造化: `Stage1TradingDayObservation` / `Stage1FillObservation` に発注先を必須化、`Stage1Aggregation`（`MoomooSimulate` 以外を落とす）、`Stage1Progress.ExcludedInternalPaperDays`。
8. 計画適合レジストリから #334 担当の 2 行を削除。

### フロントエンド

9. `features/risk/contracts.ts`: `BrokerProvider` ラベル・選択肢、段階ラベルの是正、`brokerProvider` の型追加。
10. `features/shared/PaperModeBanner.tsx`: 必須 2 文言を定数で固定した警告バナー。SC-01 / SC-02 / SC-03 の先頭へ。
11. SC-02: 発注先の表示（段階と行を分ける）＋変更フォーム＋**実弾切替の警告モーダル**（4 点提示・チェックボックス＋「REAL」入力）。
12. SC-03: 発注先の参照表示・`paper` ラベル・発注先の変更履歴・Stage 1 進捗の除外日数併記。

### 範囲外（後続 issue）

- **Stage 1 集計の供給元**（日次稼働分数・約定の記録と `StagePerformance` への注入）＝ [#386](https://github.com/endazon/ai-stock-trading/issues/386)。本 PR は「混入し得ない型」までを作る。
- **発注実行経路への結線**（`RiskManagementSettings.BrokerProvider` → `BrokerSelection` / `BrokerFactory`）。現状の発注先は構成値（`Broker:Provider` / `Broker:Environment`・[IADR-0111](../adr/IADR-0111_broker-tier-selection.md)）が決めており、設定値は**まだ発注経路を動かさない**。実弾は [IADR-0111](../adr/IADR-0111_broker-tier-selection.md) の閂 0（`LiveTradingGate`）が未解禁のまま止める。
- フロントの既存契約ずれ（`RiskLimitSettings` の equity 比化・`CapitalCapRatio` 改称に画面が追随していない）は #329 / #333 由来であり本 PR では扱わない。

## 受け入れ基準 → テスト写像

| # | 受け入れ基準（計画） | テスト |
| --- | --- | --- |
| 1 | 発注先が 3 値である | `BrokerProviderTests.発注先は計画の3値である` ／ 計画適合検査 `BrokerProvider.Values` |
| 2 | 序数が旧 `TradeMode` の意味を保存する | `BrokerProviderTests.序数は旧TradeModeの意味を保存する`（境界値） |
| 3 | 段階を変更しても発注先は自動で変わらない | `RiskSettingsServiceTests.段階の変更は発注先を変えない`（否定形） |
| 4 | 発注先を変更しても段階は自動で変わらない | `RiskSettingsServiceTests.発注先の変更は段階を変えない`（否定形） |
| 5 | 実弾切替が「OK」1 押しで完了しない | `BrokerProviderChangeTests.実弾への切替は同意と文字入力の両方が揃うまで拒否される`（否定形・プロパティベース） ／ E2E `sc02` ／ vitest `RiskSettingsPage.brokerProvider` |
| 6 | 4 点が提示される | vitest `実弾切替のモーダルは4点を提示する` ／ E2E |
| 7 | 変更理由が空欄では保存できない | `RiskSettingsServiceTests.発注先の変更は理由が空なら拒否される`（否定形） |
| 8 | 版が競合した場合は保存されない | `EfRiskSettingsStoreTests`（既存の楽観排他）＋ `RiskControlEndpoints` の 409 写像 |
| 9 | Stage 1 進捗に `paper` の約定・稼働日が算入されない | `Stage1AggregationTests.内蔵paperの約定は算入されない` ／ `…paperの稼働日は算入されず除外日数として別掲される`（否定形） |
| 10 | `paper` 稼働中に SC-01/02/03 で 2 文言のバナーが出る | vitest `PaperModeBanner` ／ 各画面の `paper バナー` テスト |
| 11 | 発注先は SC-02・SC-03 に表示し、段階と 1 行に混ぜない | vitest（`dt`/`dd` の分離を検査） |

## 完了確認

- `dotnet build backend/backend.slnx` = 0 Warning / 0 Error
- `dotnet test backend/backend.slnx --filter "Category!=Integration"` 全件成功
- `dotnet format backend/backend.slnx --verify-no-changes`
- `frontend`: `npm run typecheck` / `npm run lint` / `npm test` / `npm run e2e:typecheck`
- `node scripts/check-commit-messages.js` / `check-test-traceability.js` / `check-doc-links.js` / `check-banned-libraries.js` / `check-coverage.js`
- 計画適合レジストリの #334 担当 2 行を削除し、削除前は赤・実装後は緑になることを実測する
