---
title: 作業仕様書 — フロント↔バックエンドの契約ずれ（equity 比化・比率化の退行）を是正し、実応答の契約フィクスチャで再発を機械検出する
type: spec
status: done
related_ids:
  - FR-10
  - FR-20
  - SC-02
  - SC-03
  - IADR-0146
author: endazon (with Claude Code)
created: 2026-08-05
updated: 2026-08-05
related_specs:
  - "./20260804_329_risk-control-core.md"
  - "./20260804_333_stage-gate.md"
  - "./20260805_334_broker-provider-axis.md"
  - "../adr/IADR-0146_backend-response-contract-fixtures.md"
  - "../adr/IADR-0130_equity-ratio-risk-limits.md"
  - "../adr/IADR-0136_stage-orderable-cap-ratio.md"
  - "../../docs/DEFINITION_OF_DONE.md"
---

# 作業仕様書: フロント↔バックエンドの契約ずれの是正と、実応答フィクスチャによる再発防止

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-10**（リスク統制の上限）・**FR-20**（段階ゲート／発注先）
- 画面（SC）: **SC-02**（リスク設定）・**SC-03**（統制状態参照）
- ユースケース（UC）: UC-06（設定変更・統制状態の参照）
- 関連 ADR: 計画 [ADR-0008]・[ADR-0009]／実装 [IADR-0130](../adr/IADR-0130_equity-ratio-risk-limits.md)（equity 比化）・
  [IADR-0136](../adr/IADR-0136_stage-orderable-cap-ratio.md)（総資金比化）・[IADR-0140](../adr/IADR-0140_broker-provider-axis.md)
- 計画書リンク: `planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md` §5、
  `planning/projects/ai-stock-trading/05_screens/01_screens.md`
- 起点 issue: [#389](https://github.com/endazon/ai-stock-trading/issues/389)（原因 [#329](https://github.com/endazon/ai-stock-trading/issues/329) / [#333](https://github.com/endazon/ai-stock-trading/issues/333)）
- 実装 ADR: [IADR-0146](../adr/IADR-0146_backend-response-contract-fixtures.md)

## 背景と問題

[#329]（equity 比化）と [#333]（総資金比化）でバックエンドのプロパティ名が変わったが、**フロントの契約型が追随していない**。
BFF（`RiskControlsBffEndpoints`）は `/risk-controls/*` を素通しするため、変換で吸収される余地は無い。

| フロント `frontend/src/features/risk/contracts.ts` | バックエンドの実プロパティ | 由来 |
| --- | --- | --- |
| `RiskLimitSettings.maxOrderAmount` | `RiskLimitSettings.MaxOrderAmountRatio` | #329 |
| `RiskLimitSettings.maxDailyOrderAmount` | `RiskLimitSettings.MaxDailyOrderAmountRatio` | #329 |
| `StageSettings.capitalCap` | `StageSettings.CapitalCapRatio` | #333 |

**意味も変わっている。** 旧 `maxOrderAmount` / `capitalCap` は**金額**、新しい `*Ratio` は **equity / 総資金に対する比率**
（0.25 / 1.50 / 0.30）である。

**CI が緑のまますり抜けた理由が本 issue の要点である。** フロントのテストは API をインラインのモックで置き換えており、
そのモックは**フロント自身の型で書かれている**。つまりフロントが「こう返ってくるはずだ」と思っている形を、フロント自身が
作って、それを検証している。バックエンドの実型とは一度も突き合わせていない。

## 対象範囲

- **対象**
  1. `contracts.ts` の 3 キー（上表）をバックエンドの実プロパティ名へ是正する。
  2. **読み取り側（表示）の単位**を是正する。比率を金額として表示・入力させない。
  3. `contracts.ts` の**全キー**をバックエンドの実型と突合し、結果を記録する（下表 §全キー突合）。
  4. **再発防止の機構**を導入する（[IADR-0146](../adr/IADR-0146_backend-response-contract-fixtures.md)・案 B＝契約フィクスチャ）。
  5. 既存テストのモックを実型（＝契約フィクスチャ）に接地させる。
- **対象外（[#362](https://github.com/endazon/ai-stock-trading/issues/362) の範囲）**
  - SC-02 のリスク上限フォームの**入力 UX の作り直し**（割合入力・`0.25` / `25%` の表現決定・現在 equity での実額併記・
    「1 注文上限に 35 のような値が入らない」バリデーション）。
  - **PUT のペイロードのキー名**。後述の理由により**変えない**。

### PUT の保存経路を 400 のまま維持する（意図的な設計判断）

`PUT /risk-controls/settings/limits` の本文は今も**旧名（`maxOrderAmount` / `maxDailyOrderAmount`）**で送る。
サーバの `RiskLimitSettings` は `MaxOrderAmountRatio` 等を `required` で要求するため、**保存は 400 で拒否される**。
これは #362 が明示的に選んだ**安全側の状態**であり、本 issue で変えてはならない。

> 入力欄が「金額」のまま `maxOrderAmountRatio` を送ると、利用者が従来どおり `35000` と入れたときに
> **equity の 35,000 倍**が上限として設定される。統制が事実上無効化された状態で保存が成功するくらいなら、
> 拒否される方が安全側である（#362 本文・IADR-0130 決定）。

したがって本作業は**読み取り（GET）と表示の是正に限り**、書き込み（PUT）の形は #362 が入力 UX を作り直すときに
併せて変える。フォームの当該注記とコード内コメントに理由を残す。

## 全キー突合（受け入れ基準 3・実測）

`frontend/src/features/risk/contracts.ts` と `frontend/src/features/monitor/contracts.ts` の**全インタフェース・全キー**を、
バックエンドの実型と 1 対 1 で突き合わせた結果。

| フロント型 | 由来（バックエンド） | キー数 | 結果 |
| --- | --- | --- | --- |
| `RiskLimitSettings` | `RiskManagement.Domain.RiskLimitSettings` | 8 | **2 件ずれ**（`maxOrderAmount` / `maxDailyOrderAmount` → `*Ratio`）。他 6 件一致 |
| `BannedSymbol` | `RiskManagement.Domain.BannedSymbol` | 4 | 一致 |
| `TradingGuardSettings` | `RiskManagement.Domain.TradingGuardSettings` | 5 | 一致 |
| `StageSettings` | `RiskManagement.Domain.StageSettings` | 3 | **1 件ずれ**（`capitalCap` → `capitalCapRatio`）。他 2 件一致 |
| `RiskManagementSettings` | `RiskManagement.Domain.RiskManagementSettings` | 4 | キー名は一致。ただしサーバは **`shortSell`（`ShortSellSettings`）も返す**が、フロントは宣言していない（**欠落**・後述） |
| `SettingsChangeEntry` | `RiskManagement.Application.State.SettingsChangeEntry` | 6 | 一致 |
| `RiskStatusView` | `RiskManagement.Application.State.RiskStatusView` | 19 | 一致（**`maxOrderAmount` / `maxDailyOrderAmount` は equity から解決済みの実額**であり、設定側の同名キーとは別物。**是正対象ではない**） |
| `PromotionAssessment` | `RiskManagement.Domain.PromotionAssessment` | 3 | 一致 |
| `WithdrawalAssessment` | `RiskManagement.Domain.WithdrawalAssessment` | 4 | 一致 |
| `StageTransition` | `RiskManagement.Domain.StageTransition` | 7 | 一致 |
| `Stage1Progress` | `RiskManagement.Domain.Stage1Progress` | 3 | 一致 |
| `Stage1GateCriteria` | `RiskManagement.Domain.Stage1GateCriteria` | 3 | 一致 |
| `StageGateStatus` | `RiskManagement.Application.State.StageGateStatus` | 7 | 一致 |
| `MonitoredSymbol` | `MarketMonitor.Domain.MonitoredSymbol` | 2 | 一致 |
| `MonitorSettingsChangeEntry` | `MarketMonitor.Application.State.MonitorSettingsChangeEntry` | 6 | 一致 |

**ずれは 3 件のみ**（issue が起点として挙げた 3 キーと一致し、それ以外の**キー名のずれは無い**）。

**別種の指摘（キー名のずれではない）**: `RiskManagementSettings` の `shortSell`（[#329] 第 2 段階で追加）を
フロントが宣言していない。TypeScript の構造的部分型では未宣言キーは**読めないだけ**であり画面は壊れないが、
「サーバが返す全キーが型に現れる」という保証は無いことを意味する。本作業では**契約フィクスチャに実応答をそのまま
固定する**ことで、少なくとも「フロントが宣言した部分がサーバの実応答を満たすか」は機械検査の対象になる。
未宣言キーの検出（＝逆方向）は本機構の守備範囲外であり、[IADR-0146](../adr/IADR-0146_backend-response-contract-fixtures.md)
の「残余リスク」に明記する。

## 設計

### 1. 契約型の是正（読み取り側）

- `RiskLimitSettings`: `maxOrderAmountRatio` / `maxDailyOrderAmountRatio`（equity 比。0.25 / 1.50）へ改名。
- `StageSettings`: `capitalCapRatio`（総資金比。Stage 2 は 0.30）へ改名。
- `RiskStatusView.maxOrderAmount` / `maxDailyOrderAmount` は**触らない**（解決済みの実額であり、SC-03 の上限表示・
  使用率表示・SC-02 の実弾切替モーダル③がこれを使う。改名すればそれらを壊す）。

### 2. 表示の単位是正（SC-02 / SC-03）

- SC-02 のリスク上限フォームの当該 2 項目のラベルを **「1注文発注額上限（equity 比）」「1日発注額上限（equity 比/日）」**
  へ改める。値は比率をそのまま表示する（`0.25`）。**`0.25` と `25%` のどちらの表現を採るかは #362 の裁定事項**であり、
  本作業では決めない（比率を金額と誤読させないことだけを担保する）。
- SC-02 の「運用段階と発注先（参照）」に **「段階の発注可能額（総資金比）」** を追加する。`capitalCapRatio` は
  これまで**どこにも表示されていなかった**（型とモックにしか存在しなかった）。表示することで、以後この値のずれは
  画面の描画結果として検出される。
- SC-03 の段階ゲート表示にも同じ行を追加する（参照専用）。
- 保存が現状 400 で拒否されることをフォーム内に注記する（利用者に沈黙の失敗を見せない）。

### 3. 再発防止の機構（本題・[IADR-0146](../adr/IADR-0146_backend-response-contract-fixtures.md)）

**案 B（契約フィクスチャ）を採用**する。選定と棄却の理由は IADR-0146 に記す。

```
backend（実 HTTP 応答）──▶ frontend/src/features/risk/contract-fixtures/*.json ──▶ frontend（型・テスト）
        ①xUnit が突合                    （コミット済みの正）              ②tsc / vitest が突合
```

- **①バックエンド側**: `RiskWorkerWebApplicationFactory`（実エンドポイント・実 JSON 設定）で
  `/risk-controls/settings`・`/status`・`/stage-gate`・`/settings/history` を実際に叩き、応答 JSON を
  コミット済みフィクスチャと比較する（`FrontendContractFixtureTests`）。**バックエンドのプロパティ名を変えると
  ここが赤になる。**
- **②フロント側（コンパイル時）**: `contractFixtures.ts` がフィクスチャ JSON を `import` し、契約型の変数へ代入する。
  TypeScript は JSON から構造を推論するため、**キー名・型が契約型を満たさなければ `npm run typecheck` が赤になる**。
  フィクスチャを再生成して①を緑に戻しても、②が赤になるので黙って通せない。
- **②フロント側（実行時）**: SC-02 / SC-03 のコンポーネントテストのモックを、**インライン literal ではなく
  契約フィクスチャから作る**。画面が描画する値がフィクスチャ由来になるため、キーが消えれば描画が壊れてテストが落ちる。
  「フロントが自分で作ったモックを自分で検証する」構造をここで断つ。
- タイムスタンプ（`DateTimeOffset`）だけは実行のたびに変わるため、正規化してからフィクスチャに落とす
  （**キー名の列挙による正規化はしない**。`T` を含む ISO 日時として値の形から判定する＝人手の登録簿を作らない）。

### 4. 否定形テスト（検査器が「効かない」方向に壊れていないこと）

検査器は正しく赤くなることより、**赤くなるべきときに緑のままである**壊れ方が危険である（CI が緑なので誰も気付けない）。
[IADR-0143] / [IADR-0145] の先例に従い、**否定形テストを正の確認と同数以上**置く。

- バックエンド: 比較器 `ContractFixtureComparer` に対し、**キー改名・キー欠落・型変更・ネストしたキーの改名・
  配列要素のキー改名**を与えて「不一致として検出する」ことを固定する（＝改名を見逃さない）。
  併せて「空白・改行の差だけでは不一致にしない」正規化の正の確認も置く。
- フロント: `contracts.contract.test.ts` の末尾に `@ts-expect-error` を用いた**コンパイル時の否定形**を置く
  （当初は `contract-drift.type-test.ts` を別ファイルで作る設計だったが、フィクスチャの正の確認と
  同じ関心事であり分離する利点が無いため 1 ファイルに集約した）。
  旧キー（`maxOrderAmount` / `capitalCap`）の形は契約型を満たさない——満たしてしまうなら `@ts-expect-error` が
  「エラーが出ていない」として `typecheck` を落とす。

## 受け入れ基準

- [x] `maxOrderAmountRatio` / `maxDailyOrderAmountRatio` / `capitalCapRatio` がフロントで正しく読める
- [x] 比率と金額の取り違えが無い（表示・入力・PUT の各段。PUT は #362 の裁定により**旧名のまま＝400 を維持**し、
      その理由をコードと本書に明記する）
- [x] `contracts.ts` の他のキーについても実型と突合し、結果を報告する（上表 §全キー突合）
- [x] 再発防止の機構を IADR で決めて導入し、**バックエンドのプロパティ名を変えると CI が赤になる**ことを
      変異検査の実測（赤→緑）で示す
- [x] 否定形: モックを実型に合わせただけでは検出できない、という現状の弱さが解消されている
      （＝フロントのテストが自分で作ったモックを検証している構造を断つ）

## テスト方針

| 受け入れ基準 | 写像先 |
| --- | --- |
| 3 キーが読める | `contractFixtures.ts` の型付け（tsc）＋ `contracts.contract.test.ts` の値検査 |
| 比率/金額の取り違えが無い | `RiskSettingsPage.test.tsx`（equity 比のラベルと 0.25 の表示）・`ControlStatusPage.test.tsx`（実額表示は不変） |
| 全キー突合 | `FrontendContractFixtureTests`（実応答＝フィクスチャ）＋ `contractFixtures.ts`（フィクスチャ⊨契約型） |
| 再発防止機構 | `FrontendContractFixtureTests`（4 エンドポイント）＋変異検査の実測 |
| 否定形 | `ContractFixtureComparerTests`（改名・欠落・型変更・ネスト・配列）＋ `contracts.contract.test.ts` 末尾（`@ts-expect-error`） |

## 計画書との差異

- 差異: なし。計画（05_trading-assumptions §5）が定める「**割合で定義し固定額では持たない**」に、フロントの
  表示側をようやく追随させる作業である。

## 未決事項

- **比率の画面表現（`0.25` か `25%` か）**は #362 の裁定事項として残す。本作業では比率をそのまま表示し、
  ラベルで単位を明示するに留める。
- **PUT の形**は #362 が入力 UX を作り直す時点で是正する。それまで保存は 400 のまま（安全側）。
- フロントが**宣言していない**サーバ応答キー（`shortSell`）の検出は本機構の守備範囲外
  （[IADR-0146](../adr/IADR-0146_backend-response-contract-fixtures.md) 残余リスク）。

[#329]: https://github.com/endazon/ai-stock-trading/issues/329
[#333]: https://github.com/endazon/ai-stock-trading/issues/333
[ADR-0008]: ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
[ADR-0009]: ../../planning/projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md
[IADR-0143]: ../adr/IADR-0143_coverage-denominator-generated-code-exclusion.md
[IADR-0145]: ../adr/IADR-0145_permission-denial-fixability-classification.md
