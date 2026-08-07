---
title: 作業仕様書 — 発注先の既定を内蔵 paper へ倒し（allow-list）、REAL 照合規則と「段階が実弾を既定とするまで発注しない」を強制する（FR-20 の 2026-08-07 追記 (1)(2)(3)）
type: work
status: review
related_ids: [FR-20, FR-13, SC-02, UC-06, ADR-0016, IADR-0140, IADR-0141, IADR-0142, IADR-0161]
author: endazon (with Claude Code)
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/05_screens/01_screens.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
related_specs:
  - ../adr/IADR-0161_broker-provider-allow-list-resolution.md
  - ../adr/IADR-0140_broker-provider-axis.md
  - ../adr/IADR-0141_live-switch-explicit-confirmation.md
  - ../adr/IADR-0142_stage1-simulate-only-aggregation.md
  - ../functional/FR-20_staged-gates.md
  - ../tests/FR-20_staged-gates-tests.md
  - ../screens/20260718_SC-02_risk-settings.md
  - ../blocked-tasks.md
  - ../DEFINITION_OF_DONE.md
---

# 作業仕様書: 発注先の既定を内蔵 paper へ倒す（#422）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-20**（2026-08-07 追記 (1)(2)(3)）／**FR-13**（発注先の変更操作は SC-02 に置く）
- ユースケース（UC）: **UC-06**
- 画面（SC）: **SC-02**（リスク設定画面。実弾切替の警告モーダル）
- 関連 ADR: **ADR-0016 決定8**（段階別の商品種別・段階ゲート）／同 **決定10**（拒否理由を畳まない／分けない規律）
- 実装 ADR: **[IADR-0161](../adr/IADR-0161_broker-provider-allow-list-resolution.md)（本作業）**／
  [IADR-0140](../adr/IADR-0140_broker-provider-axis.md)（発注先 2 軸の分離・決定3・決定4）／
  [IADR-0141](../adr/IADR-0141_live-switch-explicit-confirmation.md)（実弾切替の明示確認・決定1・決定2）／
  [IADR-0142](../adr/IADR-0142_stage1-simulate-only-aggregation.md)（Stage 1 集計の allow-list）
- 起点 issue: [#422](https://github.com/endazon/ai-stock-trading/issues/422)
- 計画 submodule: **`06fa163`**（本作業では更新しない。取り込む追記は既に pin 済み）

## 目的・背景

計画 FR-20 に 2026-08-07 の裁定（質問票 第 13 回 Q5・Q6・補問 Q14）が反映され、次の 3 点が明記された。

> **(1) 設定は保存できるが、発注は段階が実弾を既定とするまで拒否する。** Stage 1 のまま実弾を選んでも
> 発注は行われない。**この旨を SC-02 の警告モーダルにも含める。**
>
> **(2) 確認文字列「REAL」の照合は、前後空白のみを除いた完全一致とし大文字小文字を区別する**（`real` は受理しない）。
>
> **(3) 発注先の初期値は内蔵 `paper` とする。設定ストアの旧行（本項目を持たない場合）も同じ既定へ落とす**
> （「読めない行は実弾」に倒れないようにするため）。

(1) と (2) は実装済みであり（後述の「現況調査」）、本作業の主眼は **(3) の構造的担保**と、
**(1) の旨を画面に出すこと**、および**退行防止テストの追加**である。

### 現況調査（実装前に確認した既存の挙動）

| 裁定 | 現況 | 本作業でやること |
| --- | --- | --- |
| (1) 発注の拒否 | **既に止まっている。** `RiskEvaluator` が `intent.Mode == MoomooReal && Stage.Mode != MoomooReal` で `StageProhibitsLiveTrading`（クラス B）を返す（`RiskEvaluator.cs` 69-72 行） | **重複実装をしない。** 「保存は成功し発注は拒否される」を**同時に固定する**テストを足す |
| (1) 画面表示 | **未実装。** モーダル②は「段階ゲートを飛ばしています」としか書いておらず、**発注が行われないことは書いていない**。むしろ「飛ばせる」と読める | モーダル②と一覧の警告に**「段階が実弾を既定とするまで発注は行われません」**を足す |
| (2) 照合規則 | **既に完全一致・`StringComparison.Ordinal`**（`BrokerProviderChange.LiveAcknowledgementPhrase` / 画面 `phrase.trim() === LIVE_ACKNOWLEDGEMENT_PHRASE`） | 全角 `ＲＥＡＬ` の境界値テストを足し、**計画が沈黙**していた旨の注記を**裁定済み**へ改める |
| (3) 初期値 | `RiskManagementSettings.BrokerProvider` の既定は `InternalPaper`。JSON に**キーが無い**行も `?? InternalPaper` で paper へ落ちる | **`null` 以外の異常値が抜けている**（後述）。allow-list へ作り替える |

### (3) の欠陥（本作業の主眼）

設定ストアは**単一行の JSON**（`risk_settings.Json`）である。読み取りは
`dto.BrokerProvider ?? BrokerProvider.InternalPaper` であり、**`null` と欠落しか見ていない**。

| 永続行の値 | 現況の読み取り結果 | 裁定が求める結果 |
| --- | --- | --- |
| キーが無い（旧行） | `InternalPaper` | `InternalPaper` |
| `null` | `InternalPaper` | `InternalPaper` |
| `0` / `1` / `2` | 対応する 3 値 | 同左 |
| **`7`（未知の序数）** | **`(BrokerProvider)7`** — enum の範囲外がそのまま流れる | `InternalPaper` |
| **`"MOOMOO REAL"`（文字列）** | **`JsonException` を送出**し、設定行全体が読めなくなる（`GetCurrent` が 500） | `InternalPaper` |
| **`"MoomooReal"`（文字列）** | 同上（例外） | `MoomooReal`（正準名の完全一致） |
| **`true` / `{}`（別の型）** | 同上（例外） | `InternalPaper` |

`??` は **deny-list**（「`null` だけを弾く」）である。裁定が「読めない行は実弾」という表現をわざわざ
使ったのは、この形が未知の値を素通しにするからである。本リポジトリには同型の **allow-list** の先例
（`HistoricalBarSourceFactory.ResolveProvider` — 既知の provider が構成の妥当性を満たしたときだけ
実アダプタを返し、未知はすべて no-op）があり、それに揃える。

**未知の序数 `(BrokerProvider)7` は「実弾ではない」ため直ちに実弾を撃つわけではない**が、
`Enum` の範囲外の値がドメインを流れることは、将来 4 値目（例: 別ブローカの `REAL`）が末尾へ足された
瞬間に意味が変わる。「今は当たらない」ことを安全性の根拠にしない。

## 対象範囲

### やること

1. **allow-list による発注先の解決**（`BrokerProviderResolution`）。3 値の明示一致のみを受理し、
   それ以外（`null` / 欠落 / 未知の序数 / 未知の文字列 / 大小文字違い / 別の型）は**すべて内蔵 `paper`**。
2. 設定ストアの JSON 読み取りを allow-list 経由にする（`BrokerProviderJsonConverter`）。
   **マイグレーション（既存行に列を足す場合の既定値）と読み取り時のフォールバックの両方**を押さえる。
3. SC-02 の警告モーダル（と一覧側の警告）に **「段階が実弾を既定とするまで発注は行われない」旨**を含める。
4. 退行防止テスト（後述）。
5. 文書更新（機能仕様書 FR-20・テスト仕様書 FR-20・画面仕様書 SC-02・IADR-0161・索引）。

### やらないこと（issue の「やらないこと」に一致）

- **`LiveTradingGate.LiveTradingReleased` に触れること**（実弾を止めている唯一の閂。本作業の対象外）。
- 発注先と運用段階の 2 軸分離そのものの変更（利用者裁定 2026-08-02・確定済み）。
- **拒否理由コードの新設** —— `StageProhibitsLiveTrading`（クラス B）が既にあり意味が一致する。
  ADR-0016 決定10 の「原因も解除条件も異なるものだけを分ける」規律に照らして**同一**である
  （原因＝段階が実弾を既定としない／解除条件＝段階の昇格）。
- verdict（空売り実弾解禁の確認記録）の実装 → **#388**。
- `StageSettings.Mode`（段階の**既定**発注先）の読み取り規則の変更 → 後述「未決事項」。

## 設計

### 1. `BrokerProviderResolution`（allow-list・単一情報源）

配置: `backend/Shared/AiStockTrading.Shared.Contracts/Trading/BrokerProviderResolution.cs`
（`BrokerProvider` enum と同じ場所。「どの 3 値が既知か」を 1 か所にする）。

| API | 規則 |
| --- | --- |
| `Default` | `BrokerProvider.InternalPaper`（**外部へ一度も発注しない唯一の値**。FR-20 (3)） |
| `IsKnown(BrokerProvider)` | 3 値の明示一致のみ `true` |
| `Resolve(BrokerProvider?)` | 3 値の明示一致 → その値。**それ以外（`null` 含む）→ `Default`** |
| `Resolve(string?)` | **前後空白のみを除いた完全一致（`Ordinal`）**で `"0"/"1"/"2"` と正準名 `InternalPaper` / `MoomooReal` / `MoomooSimulate` のみ。**それ以外 → `Default`** |

文字列側も**大文字小文字を区別する**（(2) と同じ規律）。`"MOOMOO REAL"` / `"moomooreal"` / `"real"` は
いずれも一致せず `Default` へ落ちる。

`switch` は**既知の 3 値を明示的に列挙**し、`_ => Default` で閉じる。**`Enum.IsDefined` に置き換えない**
——4 値目が足された瞬間に「未知だが定義済み」が allow-list を素通りするからである
（`IsDefined` は「enum に書いてあるか」であり「本規則が承認したか」ではない）。

### 2. 設定ストアの読み取り（マイグレーションと読み取りの両面）

- **マイグレーション（既存行に列を足す場合の既定値）**: 設定は単一行 JSON であり **DDL 上の列を足さない**。
  「列を足す」に相当するのは JSON へキーを足すことであり、**旧行はキーを持たないまま残る**
  （既存行を書き換える移行は行わない＝行の中身は変えず読み方で吸収する）。
  したがって既定値は**読み取り時に**与える。`SettingsDto.BrokerProvider` は `BrokerProvider?` の
  **省略可能パラメータ（既定 `null`）**であり、キーが無ければ `null` のまま allow-list へ入り `Default` になる。
- **読み取り時のフォールバック**: `BrokerProviderJsonConverter`（`RiskSettingsSerialization` 内）が
  **どのトークンが来ても例外を投げず** allow-list を通す。
  - 数値 → `Resolve((BrokerProvider)n)`（`int` に収まらない数値も `Default`）
  - 文字列 → `Resolve(string)`
  - `null` → `Default`
  - **それ以外（真偽値・オブジェクト・配列）→ トークンを読み飛ばして `Default`**
  - 書き込みは**現行どおり数値**（`WriteNumberValue((int)value)`）。ワイヤ形式を変えない。
- **新規インストール**: `TradingDefaults.CreateSettings()` の `BrokerProvider` は `InternalPaper`
  （`RiskManagementSettings` のプロパティ既定）。`EfRiskSettingsStore.GetCurrent` はこれをシードする。

**「読めない行は例外」も採らない。** 例外はフェイルクローズに見えるが、実際には
**設定行全体が読めなくなり統制値・ガード設定・段階もろとも失われる**（`GetCurrent` が 500 を返し、
リスク判定そのものが動かなくなる）。裁定は「同じ既定へ落とす」と明言しており、そちらへ従う。

### 3. `BrokerProviderChange.Evaluate` の未定義値検査

`Enum.IsDefined` → `BrokerProviderResolution.IsKnown` へ置き換える（既知の 3 値の単一情報源）。
**振る舞いは変えない**——書き込み（変更要求）は**黙って `paper` へ倒さず `UnknownProvider` で拒否する**。
読み取り（既に書かれてしまった行）と書き込み（これから書く値）で扱いを変えるのは意図的である:
書き込みで黙って倒すと「実弾を選んだのに paper になった」という説明のつかない状態遷移が生まれる。

### 4. SC-02 の警告（FR-20 (1)）

`RiskSettingsPage.tsx` の 2 か所に、段階が実弾を既定としないときだけ次の旨を出す。

> **段階が実弾を既定とするまで、実弾の注文は発注されません**（発注要求は段階ゲートが拒否します）。
> 発注先の設定は保存できますが、それだけでは実弾の発注は始まりません。

- **段階が既に実弾（Stage 2 / 3）のときは出さない。** その場合は実際に発注されるため、
  出すと**嘘になる**（狼少年にもなる）。条件は既存の `skipsStageGate` と同一。
- 一覧側（フォーム内の `role="alert"`）にも同じ趣旨を足す。**モーダルは必須**（裁定の名指し）。

## 受け入れ基準

- [ ] 発注先の解決が allow-list であり、`null` / 欠落 / 未知の序数 / 未知の文字列 / 大小文字違い /
      別の型のいずれも**内蔵 `paper`** になる（例外を投げない）
- [ ] 新規インストール（未シードの設定ストア）の初期値が**内蔵 `paper`** である
- [ ] 発注先の列（JSON キー）を持たない旧行が**内蔵 `paper`** として読まれる
- [ ] Stage 1 × `moomoo REAL` で**保存は受理され**、**発注は `StageProhibitsLiveTrading` で拒否される**
- [ ] `real` / `Real` / `ＲＥＡＬ`（全角）は受理されず、` REAL `（前後空白）は受理される
- [ ] チェックボックスのみ／文字入力のみでは切替ボタンが有効にならない
- [ ] 警告モーダルに「段階が実弾を既定とするまで発注は行われない」旨が含まれ、**消すとテストが赤くなる**
- [ ] `LiveTradingGate.LiveTradingReleased` は `false` のままである

## テスト計画（退行防止・新規テストケース ID は FR-20 テスト仕様書へ採番）

| ID | 内容 |
| --- | --- |
| T-75 | 未知の序数・未知の文字列・大小文字違い・別の型・`null` が**すべて内蔵 `paper`** へ落ちる（allow-list のプロパティ） |
| T-76 | 発注先のキーを持たない旧行・値が `null` の行が**内蔵 `paper`** として読まれ、例外を投げない |
| T-77 | 新規インストール（未シードの設定ストア）の初期値が**内蔵 `paper`** である |
| T-78 | Stage 1 × `moomoo REAL`: **保存は受理**され、**同じ設定での発注は `StageProhibitsLiveTrading` で拒否**される |
| T-79 | `ＲＥＡＬ`（全角）・`ＲeＡl` は受理されない（(2) の境界値。既存 T-42 の拡張） |
| T-80 | 警告モーダルに「段階が実弾を既定とするまで発注は行われない」旨が含まれる。段階が既に実弾なら出さない |

## 影響範囲

| 層 | ファイル |
| --- | --- |
| 共有契約 | `Shared/AiStockTrading.Shared.Contracts/Trading/BrokerProviderResolution.cs`（新規） |
| 永続化 | `RiskManagementService.Infrastructure/Foundation/Persistence/RiskSettingsSerialization.cs` |
| ドメイン | `RiskManagementService.Domain/BrokerProviderChange.cs`・`RiskManagementSettings.cs`（注記） |
| 画面 | `frontend/src/features/sc02-risk-settings/RiskSettingsPage.tsx` |
| テスト | Contracts.Tests / RiskManagement Domain・Infrastructure・Api Tests / frontend vitest / e2e |

## 未決事項・残余リスク

- **`StageSettings.Mode`（段階の既定発注先）の読み取りは本作業で変えていない。** 未知の序数が入っても
  `!= MoomooReal` となり実弾は止まるため**安全側**であるが、allow-list ではない。
  裁定 (3) は「発注先の初期値」＝現在の発注先の軸を対象としており、範囲を広げない
  （`docs/blocked-tasks.md` へ登録）。
- **allow-list の文字列表現は本リポジトリの永続形式では現れない**（設定 JSON は数値で往復する）。
  手編集・外部ツール由来の行を想定した保険であり、**正準名以外は受理しない**。
- 実弾は `LiveTradingGate`（閂 0）が起動時に止めており、本作業でその扱いは変わらない。

## 変更履歴

| 日付 | 変更 | 理由 |
| --- | --- | --- |
| 2026-08-07 | 新規作成 | [#422](https://github.com/endazon/ai-stock-trading/issues/422)（FR-20 の 2026-08-07 追記 (1)(2)(3)） |
