---
title: IADR-0155 SC-01 §2 収集パラメータは変動閾値を項目単位の部分更新で変え、収集間隔は供給が無いことを画面に明示する
type: impl-adr
status: Accepted
related_ids: [FR-03, FR-11, FR-13, UC-06, SC-01, IADR-0146, IADR-0151, IADR-0141, IADR-0134]
author: endazon (with Claude Code)
created: 2026-08-06
updated: 2026-08-06
---

# IADR-0155: SC-01 §2 収集パラメータ（変動閾値・収集間隔）の実装方針

- 状態: Accepted
- 日付: 2026-08-06
- 決定者: 実装（Claude Code）／ 起点 [#340](https://github.com/endazon/ai-stock-trading/issues/340)
- 作業仕様書: [20260806_340_screens-reimplementation](../specs/20260806_340_screens-reimplementation.md)

## 起点・関連

- 関連する計画書 ID: **FR-13**（設定変更）・**FR-03**（価格変動検知）・**FR-11**（監査ログ）・**UC-06**・**SC-01 §2**
- 計画書リンク: `planning/projects/ai-stock-trading/05_screens/01_screens.md` §2 収集パラメータ（FR-13）
- 関連する実装 ADR: [IADR-0151](./IADR-0151_risk-limit-percent-input-and-bounds.md)（百分率入力と値域）・
  [IADR-0141](./IADR-0141_live-switch-explicit-confirmation.md) 決定1（画面だけの関門は API 直叩きで消える）・
  [IADR-0146](./IADR-0146_backend-response-contract-fixtures.md)（契約フィクスチャ）・
  [IADR-0134](./IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定2（序数不変）
- 環流記録: [20260806_sc01-sc03-unsupplied-screen-items](../../feedback/20260806_sc01-sc03-unsupplied-screen-items.md)

## コンテキストと課題

計画 SC-01 §2 は「**変動閾値・収集間隔（ConfigurationService 由来）**の閲覧・変更」を求め、
バリデーションを「正値・下限/上限の範囲内」、アクションを「§1 と同じく検証→反映→監査ログ→通知」と定める。
備考は「[#19](https://github.com/endazon/ai-stock-trading/issues/19)（ConfigurationService の設定ストア拡張）
完了後に実装する」であり、**#19 は 2026-07-20 にクローズ済み**である。

しかし実装の現況は計画と 3 点で食い違う（`develop` 9b0f96f で実測）。

1. **変動閾値は ConfigurationService に無い。** `MarketMonitorService` の
   `MarketMonitorSettings.MovementThresholdRatio`（`GET/PUT /monitor/settings`・OwnerOnly）が権威である。
   ConfigurationService が持つのは `/assumptions`（全体前提条件）だけである。
2. **収集間隔はどこにも設定ストアが無い。** 実測では起動時構成
   （`Collection:PollIntervalSeconds`＝情報収集の巡回間隔／`Monitor:PollIntervalSeconds`＝市場監視の巡回間隔）
   であり、**読み書きするエンドポイントも設定ストアも存在しない**。
3. **既存の `PUT /monitor/settings` は全置換である。** 変動閾値・クールダウン・監視銘柄を一括で受け取り、
   `MonitoredSymbols` を送り漏らすと**監視銘柄が消える**。
4. **既存の `PUT /monitor/settings` は理由も履歴も持たない。** 計画が求める「監査ログ」を満たさない。

## 検討した選択肢

| 論点 | 案 | 評価 |
| --- | --- | --- |
| 変動閾値の変更経路 | A: 既存の全置換 `PUT /monitor/settings` を画面から使う | 棄却 |
| | **B: 項目単位の部分更新エンドポイントを新設する** | **採用** |
| 収集間隔 | C: 入力欄を作り、保存は既存の起動時構成へ（できない） | 棄却 |
| | D: 設定ストア・エンドポイント・動的再読込を新設する | 棄却（本 issue の範囲外） |
| | **E: 入力欄を作らず、変更できない事実を画面に明示する** | **採用** |
| 値域の具体値 | F: 計画に無いので検証しない | 棄却 |
| | **G: 実装が値域を決め、根拠を残して計画へ環流する** | **採用** |

### A（全置換を画面から使う）の棄却理由

**送り漏らした瞬間に監視銘柄が消える。** 同型の危険は既に `GuardUpdateRequest.ConfiguredAccountType` で
経験しており（禁止銘柄を 1 件足しただけで口座種別の設定が消える。#375）、そこでは nullable 化で回避した。
画面が「変動閾値だけを変えたい」場面で watchlist 全体を送り直す設計は、同じ穴を新しく作ることになる。

### C（動かない入力欄を置く）の棄却理由

**「変更できたつもり」を生む。** 統制設定において最も危険なのは「設定したはずの値と実際の値が違う」状態で
ある。押せば保存されたように見えるのに実際の巡回間隔が変わらない画面は、値を偽装するのと同じ害を持つ。

### F（検証しない）の棄却理由

計画は「正値・下限/上限の範囲内」と**検証そのものは要求している**。値が無いことを理由に検証を落とすと、
`0`（＝すべての変動が閾値超過となりイベント駆動サイクルが常時発火する）を保存できてしまう。

## 決定

### 決定1: 変動閾値は**項目単位の部分更新**で変える（他の項目を巻き込まない）

`PUT /monitor/settings/movement-threshold`（OwnerOnly・`{ movementThresholdRatio, reason }`）と
`PUT /monitor/settings/cooldown` を新設する。クールダウン・監視銘柄は保持する。
既存の全置換 `PUT /monitor/settings` は**残す**（既存の消費者を壊さない）。

### 決定2: 変更は**理由必須**とし、監視設定の変更履歴（既存の `IMonitorSettingsChangeLog`）へ記録する

`MonitorSettingsChangeType` に `MovementThresholdChanged`（2）・`CooldownChanged`（3）を**末尾追加**する
（序数不変・[IADR-0134] 決定2）。**監視銘柄と同じ 1 本の台帳**へ載せる——監視設定の履歴を 2 本に分けると、
SC-01 §2 と SC-02 が別々の履歴を見て食い違う。照会は `GET /monitor/settings/history` を新設する
（`/monitor/watchlist/history` は同じ台帳の別名として残す）。

### 決定3: 変動閾値の値域は**実装が決め、根拠を残して計画へ環流する**

`MonitorSettingsBounds`（`RiskLimitBounds`＝[IADR-0151] 決定2 と同じ扱い）:

| 項目 | 値域 | 根拠 |
| --- | --- | --- |
| 変動閾値 | **0 超 0.50 以下**（比率） | 0 以下ではあらゆる変動が閾値超過となり監視が統制として働かない（イベント駆動サイクルが常時発火し LLM 費用が暴走する）。50% 超では 1 日で半値になるほどの変動でしか発火せず、価格変動検知（FR-03）が事実上無効になる |
| クールダウン | **0 以上 24 時間以下** | 24 時間を超えると同一銘柄が 1 営業日に 1 度も再判定されず、日中の再エントリー機会を構造的に失う |

**実効はサーバ側である。** 画面にも同じ表を持つが、それは利用者への即時提示であり、画面だけの関門は
API 直叩きで消える（[IADR-0141] 決定1 と同じ判断）。**値はサーバと画面で一致していなければならない。**

### 決定4: 画面の入力は**百分率**、ワイヤは**比率**（SC-02 と同じ規律）

`0.03` を `3 %` として入力させる。変換は `percentTextToRatio` / `ratioToPercentText`（`risk/contracts`）
だけを通す（[IADR-0151] 決定1）。**空欄・非数値を黙って 0 として送らない**——0 は危険側の設定である。

### 決定5: 収集間隔は**入力欄を作らず、変更できない事実を画面に明示する**

「収集間隔は本画面から閲覧・変更できません。現在の実装では起動時の構成値であり、値を読み書きする経路が
ありません」と表示する。テストは**入力欄が存在しないこと**を否定形で固定する。
計画（FR-13・SC-01 §2）との差異は環流記録で計画へ返す。

**設定ストアを新設しない理由**は「面倒だから」ではない。収集間隔を動的に変えるには、
(a) 永続ストア、(b) エンドポイント、(c) **稼働中の `BackgroundService` が値の変更を読み直す機構**
の 3 つが要り、とくに (c) は巡回中のタイミングと絡む挙動変更である。**画面の再実装の範囲を超えた機能追加**
であり、供給が無いことを正しく表現したうえで別 issue に委ねる。

### 決定6: 由来サービスの差異（ConfigurationService ではなく MarketMonitorService）はコードと画面に明記する

計画は「ConfigurationService 由来」と書いているが実装は MarketMonitorService である。**黙って合わせない。**
SC-01 は 2 つのサービスを消費することになるため、§2 は §1 の取得可否に**連動させず独立して縮退**する
（片方の障害・BFF 未結線を巻き込まない・fail-safe。SC-02 の監視銘柄セクションと同じ方針）。

### 決定7: `/monitor/settings` にも契約フィクスチャの関門を張る

[IADR-0146] は当時 RiskManagementService の 4 エンドポイントに限り、他サービスへの横展開を
フォローアップとしていた。SC-01 §2 が新たに `/monitor/settings` を読むため、ここで横展開する。
比較器（`ContractFixtureComparer`）とフィクスチャ入出力（`ContractFixtureStore`）は
**共有テスト支援プロジェクト `AiStockTrading.TestSupport.ContractFixtures` へ移す**——
サービスごとに写経すると、比較器の否定形テストが 1 か所にしか無いのに規則だけが増える。

## 理由

- **部分更新は「送り漏らしで設定が消える」経路を構造的に塞ぐ。** 全置換 PUT を画面から使う設計は、
  同じ穴を新しく作ることになる。
- **動かない入力欄を作らないことが、統制設定における誠実さである。** 「変更できたつもり」は、
  値を偽装するのと同じ害を持つ。
- **値域は計画が要求しており、具体値の不在は検証を落とす理由にならない。** 実装が決めて根拠を残し、
  計画へ返す（`RiskLimitBounds` の先例と同じ）。

## 結果

- 良い影響:
  - 変動閾値の変更が**理由・前後値つきで履歴に残る**（従来の全置換 PUT は履歴を残さなかった）。
  - 監視銘柄が変動閾値の変更で消える経路が無い。
  - 収集間隔について「変更できると思ったのにできない」という誤解が生じない。
- 悪い影響・トレードオフ:
  - エンドポイントが増える（全置換 PUT と部分更新の 2 系統が併存する）。全置換は既存の消費者のために残す。
  - SC-01 が 2 サービス（Configuration / MarketMonitor）を消費する。独立縮退で緩和する。
- **残余リスク（明記）**:
  1. **収集間隔は依然として変更できない。** 画面には出るが「変更できない」としか書けない。
     計画（FR-13）が求める機能は満たされていない（環流済み・別 issue）。
  2. **値域の具体値（0.50 / 24 時間）は実装の判断である。** 計画側の裁定で変わり得る。
     サーバと画面の 2 か所に同じ値があり、片方だけ変えると挙動が食い違う（テストで固定）。
  3. **`MonitorSettingsChangeType` の序数は HTTP 経路で往来する。** 末尾追加の規律を破ると
     既存履歴のラベルが入れ替わる（`RejectionReason` と同型のリスク。[IADR-0134] 決定2）。
  4. **全置換 `PUT /monitor/settings` は依然として理由も履歴も持たない。** 画面からは使わないが、
     API を直接叩けば履歴を残さずに変動閾値を変えられる。塞ぐには全置換の廃止か理由必須化が要る（未着手）。

## 関連

- Supersedes: なし
- Superseded by: なし
