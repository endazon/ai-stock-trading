---
title: 選択中の損切りの実行機構を SC-02 で変更し、SC-03 と日報に表示する（#823）
type: spec
status: accepted
related_ids: [FR-06, FR-10, FR-11, FR-12, FR-16, UC-06, SC-02, SC-03, ADR-0040, IADR-0342, IADR-0413, IADR-0254, IADR-0269, IADR-0352, IADR-0422]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1「どの手法を選んでいるかは監査ログ・SC-03・日報に出す」・決定3)
  - planning:projects/ai-stock-trading/05_screens/01_screens.md (SC-02・SC-03・設定変更の一般則)
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md (日報 §4 リスク統制の記録)
  - planning:projects/ai-stock-trading/04_workflows/02_event-driven-trading.md (模擬取引口座で選べる手法 S0〜S3)
---

# 仕様書: 選択中の損切りの実行機構を SC-02 で変更し、SC-03 と日報に表示する（#823）

## 起点

- #823「選択中の損切り実行機構を SC-02 で変更・SC-03 と日報に表示する」。計画 ADR-0040 決定 1（「どの手法を選んでいるかは、
  監査ログ・SC-03・日報に出す」）・決定 3（変更は統制値と同じ経路。利用者の設定変更のみ・生成 AI は上書き不可）。
- 本 PR の割り当て: ブランチ `feat/FR-10-823-stop-method-ui`・IADR-0422・テスト ID `T-10-990`〜`T-10-999`。
- 受け入れ基準（issue）: 利用者が画面（SC-02）だけで手法を選べ（実弾では S0 以外を選べない表示が API の拒否と整合）、
  現在の手法（SC-03）と当日の手法（日報：承認時点の手法の集計）が画面と日報で読める。

## 計画書の確認（隣接クローン `../project-planning`・`9014e4f`・読み取り専用）

- ADR-0040 決定 1: S0（ブローカー側逆指値・既定）/ S1（ソフトウェア逆指値）/ S2（逆指値なしの建玉を許容）/ S3（他のブローカー側注文種別）。
  **本番（`TrdEnv=real`）では S0 以外を選べない。** 選択中の手法は監査ログ・SC-03・日報に出す。空売りの新規建ては実質 S0。
- 05_screens の SC-02 / SC-03（`fixed`）は**手法の項目をまだ持たない**（ADR-0040 の追随が画面設計へ入っていない）。
  「設定変更の一般則」（理由必須・監査ログ・版・今後追加する設定項目にも既定で適用）と「変更操作を持つ画面は SC-02 だけ・SC-03 は参照専用」は適用する。
- 04_report-templates の日報（`fixed`）は**手法の欄をまだ持たない**。§4「リスク統制の記録」は箇条書き（`- 項目: 値`）と
  `### 空売りの記録（当日）` 等の子節からなる。**計画に無い判断**（日報の表記の形）は、この既存書式に合わせて
  §4 の子節 `### 損切りの実行機構（当日）` として置く（IADR-0422 決定 3）。合わせられないものは無かった。

## develop で既に在るもの・残るもの（develop `a592b34f`・`git rev-parse --is-shallow-repository` → `false`）

| 項目 | 状態 | 根拠（着手時に読んだ位置） |
| --- | --- | --- |
| 手法の型 `StopLossExecutionMethod`（S0=0〜S3=3） | **済** | `Shared.Contracts.Trading`（#819 / IADR-0342 決定 1） |
| 設定点 `RiskManagementSettings.StopLossMethod`・変更 API `PUT /risk-controls/settings/stop-loss-method`（OwnerOnly・理由必須・履歴 `StopLossMethodChanged`=9） | **済** | `UpdateStopLossMethod/Endpoint.cs`・`RiskSettingsService.UpdateStopLossMethod` |
| 実弾の拒否（設定側・2 方向。発注先 moomoo REAL の間は S0 以外 400／S0 以外のまま REAL へは 400） | **済** | `StopLossMethodChange.Evaluate`・`BrokerProviderChange.Evaluate`（`StopLossMethodNotBrokerStop`） |
| 設定側の判定は設定上の発注先で行い、観測へ寄せない | **済（運用で揃える）** | IADR-0413 決定 1（#826 / PR #978） |
| 現在値の読み取り（`GET /settings`・`GET /status` の `stopLossMethod`） | **済** | `RiskStatusView.StopLossMethod`・フロントの型（`contracts.ts`）も項目を持つ |
| 承認への搭載（`OrderApproved.StopLossMethod`＝承認時点の手法） | **済** | IADR-0342 決定 3。監査台帳は `OrderApproved` をイベント全量 JSON で保持する（`AuditEntryFactory`） |
| **BFF の経路 `PUT /bff/risk-controls/settings/stop-loss-method`** | **残** | `RiskControlsBffEndpoints.cs` に無い（IADR-0342 残余リスク「BFF の経路も #823 で足す」） |
| **SC-02 の手法の選択 UI**（実弾では S0 以外を選べない表示） | **残** | `RiskSettingsPage.tsx` に無い。`useSave…` の mutation も無い |
| **SC-02 の発注先フォームが「S0 以外のまま実弾へ」を事前に止める表示** | **残** | サーバは 400 で拒否するが画面は送信してから落ちる |
| **設定の変更履歴の種別 9 の表示名** | **残（不具合）** | `CHANGE_TYPE_LABELS` が 0〜8 だけで、手法の変更が「不明(9)」と出る |
| **SC-03 の現在の手法の表示** | **残** | `ControlStatusPage.tsx` に無い |
| **日報の当日の手法（承認時点の手法の集計）** | **残** | `ReportRenderer` に無い。供給元も無い |

**既に満たされている受け入れ基準の側（API の拒否・承認への搭載・設定側の判定）は重ねて実装しない。**

## 設計

### 1. BFF（`RiskControlsBffEndpoints`）

- `PUT /bff/risk-controls/settings/stop-loss-method` → 後段 `PUT /risk-controls/settings/stop-loss-method` の素通し（既存 `ProxyAsync`）。
  統制（実弾の拒否・理由必須）は後段が実効し BFF へは持たせない（発注先の変更と同じ）。
- `BffPassThroughTests.AllRoutes` に足す（登録ルート集合の完全一致検査が同じ表を使う）。

### 2. SC-02（`StopLossMethodForm`）

- 「損切りの実行機構（変更）」パネルを発注先フォームの直後に置く（発注先と組で読む設定であるため）。
- 4 値をラジオで選ぶ。表示名は計画の表どおり（`S0 ブローカー側逆指値（既定）` / `S1 ソフトウェア逆指値` /
  `S2 逆指値なしの建玉を許容` / `S3 他のブローカー側注文種別`）。各手法に挙動の 1 行説明を添える（計画の「挙動」列の要約）。
- **実弾の拒否の表示**: 設定上の発注先が moomoo REAL の間は **S1〜S3 のラジオを無効化**し、理由（「発注先が実弾（moomoo REAL）の間は
  S0 以外を選べません」）を出す。判定は `isStopLossMethodPermittedOn(method, provider)`＝サーバの `StopLossMethodChange.IsPermittedOn` と同じ式
  （S0 か、発注先が実弾でない）。発注先の実弾判定は既存の `isLiveProvider` だけを通す。
- 理由必須（空白のみは不可）・変更なしは保存不可。400 はサーバの `details` をそのまま出す（他フォームと同じ `saveMessageOf`）。
- 常設の注記: S1〜S3 が効くのは moomoo SIMULATE の新規建てだけ・実際の発注先が SIMULATE でなければ見送り（通知あり）・空売りの新規建ては常に S0・
  手法の変更はそれ以後の承認からで既存の建玉には及ばない（FR-10 機能仕様書の既存の記述の要約）。
- **発注先フォームの逆方向**: 選択が moomoo REAL で現在の手法が S0 以外なら、`role="alert"` で「損切りの実行機構が S0 以外のため、実弾へ切り替えられません。
  先に S0 へ戻してください」を出し、切替の確認ボタンを無効化する（サーバの `StopLossMethodNotBrokerStop` と同じ条件・同じ対処）。
- 変更履歴の種別 9 に表示名「損切りの実行機構」を足す。
- mutation `useSaveStopLossMethod`（`{ method, reason }`）。成功後の再取得は既存の `useRiskSettingsMutation`（settings と status を無効化）。

### 3. SC-03（参照のみ）

- 「現 Stage ／ 発注先」パネルに「損切りの実行機構」の行を足す（`GET /status` の `stopLossMethod`。未知値は `不明(N)`）。
  S0 以外のときは「S0 以外は moomoo SIMULATE の新規建てにだけ効きます」を注記する。変更操作は置かない（参照専用の性質を変えない）。

### 4. 日報（§4 の子節 `### 損切りの実行機構（当日）`）

- **供給元は監査台帳の `OrderApproved`**（`GET /audit/events/by-type?types=OrderApproved`。OwnerOrService・JST 暦日の半開区間）。
  **承認時点の手法**は承認が運ぶ値であり、日報を作る時点の設定値ではない（途中で手法を変えた日を正しく数える）。
- 集計（純関数 `StopLossMethodUsage.From`）: **新規建て（`PositionEffect.Open`）の承認だけ**を数え（手仕舞い・自動縮小の承認は既定 S0 を運ぶだけで手法が効かない）、
  **DecisionId で重複を除き**（先勝ち）、手法ごとの件数を序数順に並べる。本文を復元できなかった記録は件数に含めず別に数える。
- 描画（日報だけ。週報・月報は計画が求めていない）:
  - 承認あり: `- **新規建ての承認（承認時点の手法）**: N 件 — S0 ブローカー側逆指値 a 件 / S2 逆指値なしの建玉を許容 b 件`
  - 承認なし: `- **新規建ての承認（承認時点の手法）**: なし（当日の新規建ての承認は 0 件）`
  - 未供給（照会不能）: `- **承認の記録を照会できませんでした（要確認）**: 「承認なし」とは区別しています。`
  - 固定の注記 1 行: 承認時点の手法であり発注・約定の件数ではない／S0 以外が効くのは moomoo SIMULATE の新規建てだけ（空売りは S0）。
  - 復元できなかった記録があれば件数を出す（上の件数に含めていない旨）。
- `ReportInput.StopLossMethods`（表示名「損切りの実行機構（承認の記録）」・日報だけに適用）を末尾に足し、未供給を記録・提示する（IADR-0352 の経路）。
- `Audit:BaseUrl` 未設定・不正は Unsupplied（常に null）。空（承認なし）へ倒さない。

## 受け入れ基準

- AC1（T-10-990）: BFF は `PUT /bff/risk-controls/settings/stop-loss-method` を後段へ素通しし（本文・ステータス透過）、匿名は 401。登録ルート集合と表が一致する。
- AC2（T-10-991）: SC-02 で手法を選び理由を入れて保存すると `PUT /risk-controls/settings/stop-loss-method` に `{method, reason}` が送られる。理由が空・変更なしでは保存できない。400 の `details` を表示する。
- AC3（T-10-992・否定形）: 設定上の発注先が moomoo REAL の間、S1〜S3 は選べず（無効化）理由が表示される。S0 は選べる。発注先が内蔵 paper / moomoo SIMULATE の間は 4 値とも選べる。
- AC4（T-10-993・否定形）: 手法が S0 以外のとき、発注先フォームで moomoo REAL を選ぶと理由つきの警告が出て切替の確認へ進めない。S0 のときは従来どおり。
- AC5（T-10-994）: 設定の変更履歴で種別 9 が「損切りの実行機構」と表示される（「不明(9)」にならない）。
- AC6（T-10-995）: SC-03 は現在の手法を表示名で出し（未知値は `不明(N)`）、S0 以外なら SIMULATE 限定の注記を出す。変更操作は無い。
- AC7（T-10-996）: 日報 §4 に `### 損切りの実行機構（当日）` が出て、新規建ての承認を承認時点の手法ごとに数える。手仕舞いの承認は数えない。同じ DecisionId は 1 件。
- AC8（T-10-997・否定形）: 承認の記録を照会できないときは「照会できませんでした」と書き、「なし」「0 件」と書かない。承認 0 件は「なし」と書く。週報・月報には出さない。
- AC9（T-10-998）: 供給元は監査台帳の `OrderApproved` を JST 暦日の半開区間で引き、列挙の文字列表現の本文を復元する。非 2xx・例外・null 応答は未供給（null）、壊れた 1 件は除外して数える。自動生成は未供給を `StopLossMethods` として記録する。

## テスト ID（割り当て T-10-990〜T-10-999 のうち T-10-990〜T-10-998 を使う）

origin/* 全 34 ブランチで `git grep -o "T-10-99[0-9]"` → `T-10-999` だけが 66 件（`scripts/scripts.repo.test.js` と
`20260923_887_test-id-duplicate-numbering.md` の**検査器の合成フィクスチャ**）。実 ID としての使用は 0 件だが、紛れを避けて 999 は使わない。

## 是正・追随の母集合（規則 9・10）

- 規則 9（「表示は #823」＝未実装を述べる記述）: `git grep -n "#823"`（`.ai-context/specs` を除き `T-10-823` を除く）→ 8 箇所。
  対象（本 PR で書き換える）: `contracts.ts:70`・`contracts.ts:112`・`RiskStatusView.cs:50`・`UpdateStopLossMethod/Endpoint.cs:10`。
  除外: `IADR-0342:62/89/143-144`・`.ai-context/adr/README.md:384`（凍結記録。「#823 で足す」は当時の予定として真。本 PR の IADR-0422 が引き継ぐ）、
  `IADR-0344:127` と `README.md:386`（**損切り到達の通知に建玉ごとの手法を出す**話であり、#823 の射程〔SC-02・SC-03・日報〕外。残余リスクへ）。
- 規則 9（種別 9 の表示名の欠落）: `git grep -n "CHANGE_TYPE_LABELS\|changeTypeLabel"`（frontend）→ 表は `contracts.ts` の 1 か所・使用は SC-02 の履歴 1 か所。
- 規則 9（BFF の全経路表）: `git grep -n "stage1-minimum-trade-count"`（BFF）→ `RiskControlsBffEndpoints.cs`・`BffPassThroughTests.cs` の 2 か所。
  基盤（`../microservices-platform`）には AST の経路表が無い（`git grep` 0 件）。
- 規則 10（自分の記述）: 日報の §4 が 1 子節増える → `Golden/daily-*.md` を引き直す。`ReportInput` を 1 つ足す →
  `ReportInputsTests` の適用表・`ReportSummary` の全種別テスト（`Enum.GetValues<ReportInput>()`）を引き直す。
  SC-02 に保存ボタンが 1 つ増える → E2E の「保存」ボタンの指定（アクセシブル名の完全一致）を引き直す。
  docs: `docs/screens/20260718_SC-02_risk-settings.md`・`20260718_SC-03_control-status.md`・`docs/functional/FR-10_risk-controls.md`（現在値の記述）・
  `docs/tests/FR-10_risk-controls-tests.md`。

## 規則 11（窓）

窓＝「手法の設定を変えた時刻」と「その日の承認の時刻」の間（日中に手法を変えた日）。

| 形 | 増える側（変更前の承認が後の手法で数えられる） | 減る側（変更後の承認が前の手法で数えられる） |
| --- | --- | --- |
| ① 日報の生成時点の設定値を「当日の手法」とする | 🔴 起きる（朝 S0・午後 S2 の日は全件 S2） | 起きない |
| ② 当日の設定変更履歴から時刻で推定する | 起きない | 🔴 起きる（承認の審査と保存の順序が同時刻付近で逆転し得る・履歴が読めない日は推定不能） |
| ③ 承認が運ぶ値を数える（採用） | 起きない | 起きない（審査時点の値を承認自身が持つ） |

③ は T-10-996（S0 と S2 が混在した日を両方数える）で実測する。

## 残余リスク

- 損切り到達の通知・免除の通知に**建玉ごとの手法**を出すことは本 PR の射程外（IADR-0344 残余リスク）。
- 日報の件数は**承認**の件数であり、発注執行の見送り（実際の発注先が SIMULATE でない等）・約定の有無は反映しない（注記で明示する）。
- 空売りの新規建ての承認は、承認が運ぶ手法（設定値）で数える。発注執行はこれを S0 で扱う（注記で明示する）。
- 計画側の画面設計（SC-02 / SC-03）と日報テンプレートは手法の欄をまだ持たない（ADR-0040 の追随）。本 PR は既存の書式に合わせて置いた。
