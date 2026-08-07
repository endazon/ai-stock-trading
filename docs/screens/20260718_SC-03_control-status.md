---
title: 画面仕様書（素案） — SC-03 承認・統制状態参照画面
type: screen
status: Draft
related_ids: [SC-03, FR-10, FR-20, FR-12, FR-13, UC-06, ADR-0008, ADR-0009, ADR-0016, ADR-0019, IADR-0140, IADR-0142, IADR-0154, IADR-0159, IADR-0162]
issue: 106
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-08-07
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
related_specs:
  - ../specs/20260718_106_frontend-risk-settings-and-controls.md
  - ../specs/20260805_334_broker-provider-axis.md
  - ../adr/IADR-0140_broker-provider-axis.md
  - ../adr/IADR-0142_stage1-simulate-only-aggregation.md
  - ../adr/IADR-0154_supply-availability-declared-by-server.md
  - ../adr/IADR-0162_unsupplied-metric-display-convention-all-screens.md
  - ../specs/20260807_424_unsupplied-metric-display-convention.md
  - ../specs/20260806_340_screens-reimplementation.md
  - ../adr/IADR-0084_frontend-risk-settings-and-control-status.md
---

# SC-03 承認・統制状態参照画面【素案】

> 起点: **FR-10**（取引統制）、**FR-20**（段階ゲート）、**UC-06**（設定変更・一時停止・緊急停止。本画面は当該統制の状態を閲覧する参照面）。
> 計画リポジトリ `05_screens/` は空のため SC-03 は素案（project-planning#33・#31 後続 で環流）。データ源は RiskManagementService
> `/risk-controls/status`・`/risk-controls/stage-gate`（OwnerOnly）。**参照中心**の画面。

## 画面の位置づけ

platform SPA 認証済みレイアウト配下に feature `sc03-controls` としてマウント（route `controls`・nav「統制状態」）。
破壊的操作（pause/resume・kill switch・段階遷移承認）は **#165 の Discord Bot 側と役割分担**し、本画面には置かない
（[IADR-0084](../adr/IADR-0084_frontend-risk-settings-and-control-status.md) 決定 2・安全既定）。

## アクセス制御

- 利用者（`trading-owner`）限定。`RequireRole anyOf=['trading-owner']`・権限外は `NotFound`（存在秘匿）。
- 実効認可はサーバ側（`/status`・`/stage-gate` = OwnerOnly）。権限外では構成 API を呼ばない。

## 構成要素

0. **維持率・空売りの現況（`ShortSellingStatusView`・#340・[ADR-0016] 決定3/7/9/15）** —— **本画面の最上位に置く。**
   計画（05_screens SC-03）の原文: 「マージンコールは口座を失う唯一の経路であり、現物取引には存在しなかった指標である」。
   - **維持率**: 現況・**適用される閾値**（自前 40% と規制要求の厳しい方。建玉の株価に依存する）・
     **回復目標**（＝適用閾値 + オフセット。既定 +5 ポイント）・**設定上の閾値**（設定値であり実測ではない）。
   - **空売り比率**: 現況と上限（既定 50%）。分母は**時価**であり取得原価では代用しない。
   - **保有ポジション**: 銘柄・市場・**方向（ロング / ショート）**・数量・平均取得単価・評価額・**借株料累計**。
   - **維持率割れによる自動縮小**: 3 統制とは**別の枠**（`role="region"`）に置く。「動かす」統制であること
     （利用者の承認を待たず AI を介さずに建玉を決済する）と、**縮小対象は必要証拠金の降順**であることを明記する。
     直近の発動履歴（発動日時・決済前後の維持率・閾値・回復目標・決済した建玉）。
   - **供給が無い値を「正常値」に見せない（[IADR-0154]）**: 応答は指標ごとに供給可否
     （`MetricAvailability`: `Available`(0) / `NotSupplied`(1) / `NotApplicable`(2)）を宣言する。
     画面は `NotSupplied` を「**取得できていません（供給元がありません）**」と警告表示し、`0` や「—」で
     正常値のように描かない。`NotApplicable`（建玉なし＝正常）とは文言を分ける。
     **未知の供給可否は未供給へ倒す**（値があるように見せない）。

     現時点で **`NotSupplied` になる項目**（`docs/blocked-tasks.md` と一致させること）:

     | 項目 | 供給が無い理由 |
     | --- | --- |
     | 維持率 | `UnavailableMaintenanceMarginSnapshotSource` が常に `null`。PoC 項目 3 で「実弾口座でのみ照会でき SIMULATE では照会 API 自体が失敗」と実測。**Stage 1 の全期間にわたって表示できない**（[ADR-0016] 決定7 の 2026-08-07 追記）。**これは不具合ではなく、供給が無いという事実の正しい表現である** |
     | 借株料の累計 | 累計を保持する型・ストア・イベントがコード全体に存在しない |
     | 自動縮小の発動履歴 | 発火元（維持率）が無く、履歴ストアも照会 API も無い |
     | 空売り比率 | 分母（建玉総額）は時価であり、現在値（`MarketData:EnableMarkToMarket`）は既定 false |
     | **強制買戻しの発生回数**（[ADR-0016] 決定15・#424） | 推定台帳（[IADR-0159]・#419）は入ったが、**推定が起きたときにしか行を書かない**ため、行数 0 は「観測が一度も届いていない」と「観測して 0 件だった」を区別できない。計画は本項目へ**「0 件と表示してはならない」**と名指ししている（[IADR-0162] 決定2） |

0-2. **供給が無い値の表示規約（全画面共通・2026-08-07 追加／[IADR-0162]）** —— 3 状態を**両方向**に守る。

   | 状態 | 表示 | SC-03 の例 |
   | --- | --- | --- |
   | **供給が無い**（`NotSupplied`） | **「取得できていません（供給元がありません）」** | 維持率・借株料の累計・自動縮小の発動履歴・強制買戻しの発生回数 |
   | **対象なし**（`NotApplicable`） | 「該当なし（対象の建玉がありません）」 | 建玉が 1 件も無いときの空売り比率 |
   | **値が 0**（`Available` かつ 0） | **「0」**（正常値として表示する） | 空売り建玉が無い口座の空売り比率 0.0%・借株料 $0・発生回数 0 件（供給されている場合） |

   - **「—」は対象なし専用の記号である。** 未供給を「—」で描かない（[IADR-0162] 決定3）。
   - **正当な 0 を未供給へ倒さない**（逆方向の否定形）。「0 かどうかから供給有無を推測しない」は
     **両方向**に効く規律であり、片方向だけでは「供給されているのに取得できていませんと嘘をつく」向きが残る。

1. **統制状態（`RiskStatusView`）**: 3 統制（kill switch・日次損失ロックアウト・一時停止）の on/off、成立中で最優先の統制
   （`activeControl`）、新規建て停止（`newEntriesBlocked`）、ロックアウト解除日、運用段階、当日損益（実現＋含み＋合計）、
   上限使用率の入力（発注額/上限・DD/上限・保有数/上限）。
   **3 統制は優先順位つきの表**（1: kill switch ＞ 2: 日次損失ロックアウト ＞ 3: 一時停止）で描き、
   **優先統制**（`activeControl`）を明示する。各行に**発動主体**と**解除条件**を併記する（[ADR-0009]。
   日次損失ロックアウトはシステム自動発動であり利用者は解除できない）。
   **発注先（#334）**: 現在の発注先を運用段階の**隣に行を分けて**表示する（1 行に混ぜない。INDEX 決定 46）。
   **本画面は参照専用であり、変更は SC-02 で行う**（導線を置く）。
   内蔵 `paper` 稼働中は画面上部に警告バナー（必須 2 文言）を出し、統制状態のカード類に `paper・参考値` ラベルを付す。
2. **段階ゲート現況（`StageGateStatus`）**: 現段階・**段階の既定発注先**、昇格評価（`promotion`: 昇格先・可否・未充足基準）、
   撤退評価（`withdrawal`: 到達・停止提案・降格提案段階）。
2-2. **Stage 1 の進捗（#334・IADR-0142）**: 経過営業日数 / 目標・取引件数 / 最小件数を表示し、
   **内蔵 `paper` 稼働により算入されなかった営業日数を併記**する（例: 「経過 42 / 60 営業日（`paper` 稼働により 3 日を除外）」）。
   **moomoo `SIMULATE` の約定のみを集計している**旨の注記を置く。閾値は応答（`stage1Criteria`）から取り、画面に直書きしない。
2-3. **発注先の変更履歴（#334・FR-20 (2)）**: 日時・変更前後・変更者・理由を新しい順に一覧。
   設定変更履歴（`/risk-controls/settings/history`）から `changeType == BrokerProviderChanged`（7）だけを絞る。
3. **段階遷移履歴（`StageTransition[]`）**: 承認による昇格・差し戻しを新しい順に一覧（連番・from/to・種別・承認者・理由・日時）。

## データ取得（BFF `/bff/*` 経由・`apiFetch`・すべて読み取り）

| 操作 | 呼び出し | 応答/エラー |
| --- | --- | --- |
| 統制状態 | `GET /risk-controls/status` | `RiskStatusView`。404/失敗=縮退表示 |
| 段階ゲート | `GET /risk-controls/stage-gate` | `StageGateStatus`。失敗時はその領域のみ縮退 |
| 遷移履歴 | `GET /risk-controls/stage-gate/history` | `StageTransition[]`（`stage-gate` の `history` を用いても可） |
| 発注先の変更履歴 | `GET /risk-controls/settings/history` | `SettingsChangeEntry[]` を `changeType == 7` で絞る。失敗時はその領域のみ縮退 |
| 維持率・空売りの現況 | `GET /risk-controls/short-selling` | `ShortSellingStatusView`（#340・OwnerOnly）。失敗時はその領域のみ縮退し「値が無いのではなく、確認できていません」と明示する |

## 振る舞い（安全既定）

- **破壊的操作の UI を持たない**（参照専用）。統制の変更入口は #165 の Bot に一元化。
- 数値 enum（`activeControl`/`stage`/`kind`/未充足基準/撤退理由）は表示ラベルへ写像し、未知値はフォールバック表示。
- 取得不能・権限外・BFF 未登録は安全側（縮退・存在秘匿）へ倒す。機微情報は権限外に載せない。
- 各領域（統制状態・段階ゲート・履歴）は独立に縮退する（一方の取得失敗が他方を巻き込まない）。

## テストとの対応（#340）

| 構成要素 / 受け入れ基準 | テスト |
| --- | --- |
| 維持率の未供給を「取得できていません」と明示し、統制が働いていない旨を警告する | `ControlStatusPage.shortSelling.test.tsx`「維持率が未供給のとき…」「維持率が未供給のとき 0% や — を…」 |
| 維持率の供給時に値・適用閾値・回復目標を表示する | 同「維持率が供給されているとき…」 |
| 回復目標のオフセットを応答から取り画面に直書きしない | 同「回復目標のオフセット（+5 ポイント）を応答から取り…」 |
| 空売り比率の「該当なし」と「取得できていません」を出し分ける | 同「空売り比率は建玉が無ければ…」 |
| 保有ポジションの建玉方向（ロング / ショート） | 同「保有ポジションに建玉の方向…」 |
| 借株料の累計を未供給として明示し 0 を表示しない | 同「借株料の累計は未供給として明示され、0 を表示しない」 |
| **強制買戻しの発生回数**を未供給として明示し「0 件」と表示しない（#424） | 同「強制買戻しの発生回数は未供給として明示され、0 件と表示しない」／ E2E `sc03-controls.spec.ts`「強制買戻しの発生回数を「0 件」に見せない」 |
| **正当な 0 を未供給へ倒さない**（発生回数 0 件・空売り比率 0.0%・借株料 $0） | 同「強制買戻しが供給されていれば 0 件を「0」として表示する」「供給されている 0（空売り比率 0.0% ・借株料 $0）を未供給として描かない」 |
| 維持率が **Stage 1 の全期間表示できない**事実を「不具合ではない」と明示する（#424） | 同「維持率が未供給のとき Stage 1 の全期間表示できない事実を「不具合ではない」と明示する」 |
| 供給が始まれば表示が追随する（画面に「未供給」を書き込んでいない） | 同「維持率が供給されているとき、値・適用閾値・回復目標（閾値 + 5pt）を表示する」（Stage 1 の注記も消える） |
| 自動縮小を 3 統制と別枠で「動かす」統制として描き、必要証拠金の降順を明記する | 同「維持率割れ自動縮小は 3 統制と別枠で…」 |
| 発動履歴が未供給のとき「発動なし」と表示しない | 同「発動履歴が未供給のとき…」 |
| 3 統制を優先順位順に表示し優先統制を明示する | `ControlStatusPage.test.tsx`「3 統制を優先順位順に表示し優先統制を明示する」 |
| **参照専用性**（入力要素・保存系ボタンが 1 つも存在しない・書き込み API を呼ばない） | `ControlStatusPage.readonly.test.tsx`（4 件）／ E2E `sc03-controls.spec.ts`「変更操作が 1 つも存在しない（参照専用）」 |
| 応答↔フロント契約（供給可否の宣言そのもの） | xUnit `FrontendContractFixtureTests`「空売り現況応答が…」／ `contracts.contract.test.ts`（実応答の未供給宣言・文言の固定） |
| `paper` の約定が Stage 1 進捗に算入されない旨の表示 | `ControlStatusPage.brokerProvider.test.tsx`（既存） |

## スコープ外（後続）

承認・差し戻し操作 UI（#165 Bot 側）、platform 合成点（features/BFF）登録、
**維持率・借株料の累計・自動縮小の発動履歴の供給元の実装**（実口座への接続が要る。#331 / #342）、
**強制買戻しの発生回数の供給**（推定台帳への照会 API と「観測が届いた事実」の記録経路が要る。#424 で
`NotSupplied` の宣言のみを行った。`docs/blocked-tasks.md` 参照）。

[ADR-0009]: ../../planning/projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md
[ADR-0016]: ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
[IADR-0154]: ../adr/IADR-0154_supply-availability-declared-by-server.md
[IADR-0159]: ../adr/IADR-0159_buy-in-post-hoc-inference.md
[IADR-0162]: ../adr/IADR-0162_unsupplied-metric-display-convention-all-screens.md
