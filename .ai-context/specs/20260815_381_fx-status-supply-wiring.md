---
title: 日報の為替欄の供給ポートを監査台帳へ結線する
type: spec
status: approved
related_ids: [FR-06, FR-10, FR-11, UC-06, ADR-0016, ADR-0022, IADR-0019, IADR-0133, IADR-0181, IADR-0196, IADR-0198]
author: endazon (with Claude Code)
created: 2026-08-15
updated: 2026-08-15
---

# 仕様書: 為替の情報源の状態を日報へ実際に出す（#381 の最後）

> 本仕様書は実装着手前に作成する。

## 起点

- 起点 issue: [#381](https://github.com/endazon/ai-stock-trading/issues/381)（**供給ポート結線＝残りの範囲**）
- 起点 ID: **FR-06**（報告）・**FR-10**（統制）・**FR-11**（監査）・**UC-06**
- 計画: ADR-0022（計画リポ） 決定1・決定2
- 前提: [IADR-0196](../adr/IADR-0196_fx-source-visibility.md) 決定2・決定3・決定4 / [IADR-0198](../adr/IADR-0198_fx-expired-visibility.md)

## 課題 — **表示側だけがあり、供給側が無い**

[PR #511](https://github.com/endazon/ai-stock-trading/pull/511) までで、為替の劣化は**発行・監査台帳・Discord・日報のレンダラ**まで揃った。
**しかし日報には今も何も出ない。**

| 部品 | 状態 |
| --- | --- |
| イベント発行（`PublishingFxSourceStatusNotifier`） | ✅ 本番の合成根に登録済み（**実際に発行されている**） |
| 監査台帳・Discord のハンドラ | ✅ 結線済み |
| 日報のレンダラ（`AppendFxSourceStatus`） | ✅ 実装済み |
| **供給ポート `IFxSourceStatusSource`** | 🔴 **実装が 1 つも無い。`ReportAutoGenerator` から呼ばれてもいない** |

> 🔴 **既定は `null` ＝「状態を照会できませんでした（要確認）」である。**
> **正直ではあるが、毎日必ず出るため慣れると読み飛ばされる**（IADR-0196 が残余リスクとして予告していた）。

## 調査でわかったこと（実測）

### ① 維持率割れの供給ポートは**今回の対象外である**（結線してはならない）

`IMarginReductionRecordSource` も未結線だが、**こちらは計画どおりの状態である。**

- `IMaintenanceMarginSnapshotSource` の実体は `UnavailableMaintenanceMarginSnapshotSource`（常に `null`）
- **`MaintenanceMarginReductionService.Evaluate()` の呼び出し元が本番に 1 つも無い**
- ゆえに `MaintenanceMarginReductionExecuted` は**今日 1 度も発火し得ない**

ポート自身が「権威源への結線は**発火元（#331 / #342）と同時に行う**。それまでの既定は空列＝『発動なし』であり、
**発動があり得ない現状では事実として正しい**」と明記している。**先に結線すると、発火し得ない経路の
照会失敗が「要確認」として毎日出る**——正しさが下がる。

> **前回のセッション記録に「2 つまとめて結線するのが筋」と書いたが、実測の結果これは誤りであった。**
> **FX は発火している／維持率割れは発火し得ない**——状況が違う。

### ② クレジット表記は**私的運用では法的義務ではない**

ADR-0022 §利用条件（2026-08-05 利用者裁定）は、日銀 API 側の義務（利用時の連絡・クレジット表示）が
**「本機能を使用したサービスを公開した場合」という条件付き**であり、**私的運用では発生しない**と裁定している。
**ただし「報告書の為替欄に出典を明記する」方針は維持する**とも書いている。

**したがって出典は「守るべき自己方針」であり、「証拠が無くても書かねばならない義務」ではない。** 決定5 の根拠。

## 決定

### 決定1: **権威源は監査台帳である**（判断サービスではない）

- 判断サービス側の状態（`FxSourceStatusTracker`）は **in-memory・プロセスごと**であり、**再起動で消える**。
  期間で引く照会の権威源にはできない。
- **監査台帳はイベント全量を JSON で 7 年保持する**（IADR-0019）。**期間の集計はここからしか復元できない。**

### 決定2: **監査台帳へ「種別 × 期間」の照会を足す**

既存の照会は**相関単位**（`GetByCorrelation`）と**直近 N 件**（`GetRecent`）しかない。

🔴 **`GetRecent(大きな limit)` を引いて絞る案は採らない。** 期間内の件数が limit を超えると
**古いものから静かに落ちる**——**取りこぼしても赤くならない**（本プロジェクトが繰り返している失敗の型）。

- `IAuditEventStore.GetByTypesInPeriod(types, fromInclusive, toExclusive)` を足す
- エンドポイント `GET /audit/events/by-type?from=&to=&types=`
- **認可は `OwnerOrService`**（ReportService からの s2s 照会。既存の `/risk-controls/*` と同じ形）。
  同グループの他 2 本は `OwnerOnly` のままとする——**必要な 1 本だけを開ける。**

### 決定3: **期間は JST 取引日 → UTC 区間へ写す**

供給ポートの引数は他と同じ `DateOnly from/to`（JST 取引日）。台帳の `OccurredAt` は `DateTimeOffset` のため、
**`[from 00:00 JST, to+1 日 00:00 JST)` の半開区間**へ写して引く。

> **半開区間にする。** 終端を `23:59:59` で閉じると**その日の最後の 1 秒が落ちる**。

### 決定4: **供給不達は `null`。空列（事象なし）と区別する**

ポートの契約どおり。**`IBuyInInferenceRecordSource` と同じ向き**であり、`IPeriodFillSource`（空列へ倒す）とは逆である。
理由も同じ——**劣化があったのに「ありません」と書くのは、劣化を隠したのと同じ結果になる。**

### 決定5: 🔴 **出典は「台帳に証拠のある情報源」だけを載せる。無い期間は載せない**

**遷移でしか発行しない**（IADR-0196 決定1）ため、**静かな期間はどの源を使ったのか台帳から証明できない。**

| 台帳にある記録 | 証明できること |
| --- | --- |
| `FxRateSourceFellBack(SourceName)` | **その源を使った** |
| `FxRateSourcePrimaryRestored(SourceName)` | **第一の源へ戻して使った** |
| `FxRateStale` / `PositionClosedWithStaleFxRate` | レートを使った（**源の名前は分からない**） |
| **記録なし** | 🔴 **何も分からない**（第一の源を静かに使った／為替を一度も使わなかった、の区別がつかない） |

**あわせて「劣化なし」の文言から「第一の情報源から取得できており」を外す。**
現行の文言は**証拠が支えていない主張**である——遷移が無いことは「第一の源を使った」ことを意味しない。

> **これは IADR-0196 決定3 と同じ規律を 1 段深く適用したものである。**
> あちらは「**照会できなかった**」を「切替なし」と書かないことを定めた。
> こちらは「**記録が無い**」を「第一の源で正常だった」と書かないことを定める。

## やること

1. `IAuditEventStore.GetByTypesInPeriod` ＋ EF 実装 ＋ InMemory 実装
2. `GET /audit/events/by-type`（`OwnerOrService`）
3. `HttpFxSourceStatusSource`（ReportService）＋ 合成根への登録（`Audit:BaseUrl`）
4. `ReportAutoGenerator` から供給を引き、`DraftRequest` → `ReportView` へ通す
5. レンダラの「劣化なし」文言を証拠に合わせて直す
6. 出典の導出（決定5）

## やらないこと

- **`IMarginReductionRecordSource` の結線**（調査①。発火し得ない）
- **新イベントの追加**（静かな期間の出典問題は残余リスクとして issue 起票する）
- **既存 2 本の監査照会の認可変更**（必要な 1 本だけ開ける）

## 受け入れ基準

- [ ] 期間内の 4 種のイベントが台帳から引ける（`FellBack` / `PrimaryRestored` / `Stale` / `ClosedWithStaleFxRate`）
- [ ] **否定形**: 期間外のイベントは含まれない（**半開区間の終端 1 秒を落とさない**）
- [ ] **否定形**: 照会失敗は `null`（空列＝事象なしと区別される）
- [ ] **否定形**: `Audit:BaseUrl` 未設定なら未供給（`null`）＝現行挙動
- [ ] 日報に切替・鮮度警告・停止・鮮度切れ決済が**実際に出る**（結線の実測）
- [ ] 出典は**証拠のある源だけ**が出る
- [ ] **否定形**: 記録の無い期間に「第一の情報源から取得できており」と書かない
- [ ] `AuditConsumerCoverageTests` / 契約基準が緑（イベントは増やしていないので追随不要）
- [ ] **変異試験で効きを実測する**（一致件数・`git diff` の中身・`error CS`=0 を確認）

## テスト方針

| 何を守るか | どう守るか |
| --- | --- |
| 取りこぼさないこと | 期間内の全種別が返る／**終端の 1 秒**が含まれる |
| 混ぜないこと（否定形） | 期間外・対象外の種別が返らない |
| 隠さないこと（否定形） | 照会失敗 → `null`（空列にしない） |
| 現行挙動を壊さないこと（否定形） | `Audit:BaseUrl` 未設定 → `null` |
| 証拠を超えて書かないこと（否定形） | 記録なし → 出典が空・「第一の情報源から」と書かない |
