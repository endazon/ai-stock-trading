---
title: IADR-0422 損切りの実行機構は SC-02 の専用フォームで選び（実弾の拒否は画面とサーバが同じ式）、SC-03 は発注先の隣に参照表示し、日報は監査台帳の承認が運ぶ手法を §4 の子節に数える
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-10, FR-11, FR-12, UC-06, SC-02, SC-03, ADR-0040, IADR-0342, IADR-0413, IADR-0199, IADR-0254, IADR-0352, IADR-0141, IADR-0344]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1「どの手法を選んでいるかは監査ログ・SC-03・日報に出す」・決定3)
  - planning:projects/ai-stock-trading/05_screens/01_screens.md (SC-02・SC-03・設定変更の一般則)
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md (日報 §4 リスク統制の記録)
---

# IADR-0422: 損切りの実行機構の選択 UI と表示（SC-02・SC-03・日報）

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: claude（起票 #823。利用者レビューは PR で受ける）

## 起点・関連

- 対象 Issue: #823（ADR-0040 決定 1 の表示要件と決定 3 の変更経路のうち、画面と日報の側）。
- 関連する実装仕様書: [20260925_823_stop-method-ui-and-daily-report](../specs/20260925_823_stop-method-ui-and-daily-report.md)
- 関連 IADR: [IADR-0342](IADR-0342_simulate-stop-loss-method-selection.md)（選択機構・設定点・承認への搭載。本 IADR は覆さず、同 IADR の残余リスク
  「表示と BFF の経路は #823」を引き受ける）、[IADR-0413](IADR-0413_stop-method-settings-provider-and-waiver-settlement.md)（設定側の判定は設定上の発注先）、
  [IADR-0199](IADR-0199_fx-status-supply-wiring.md)・[IADR-0254](IADR-0254_period-aggregation-authority-is-audit-ledger.md)（報告書が監査台帳を期間で引く作法）、
  [IADR-0352](IADR-0352_report-defers-on-transient-dependency-failure.md)（未供給の入力の記録）、[IADR-0141](IADR-0141_live-switch-explicit-confirmation.md)（BFF は統制を持たない・画面とサーバの同じ関門）

## コンテキストと課題

ADR-0040 は moomoo SIMULATE に限り損切りの実行機構を S0〜S3 から選べるようにし、「どの手法を選んでいるかは監査ログ・SC-03・日報に出す」と定めた。
選択機構・設定点・実弾の拒否（設定側 2 方向）・承認への搭載は #819（IADR-0342）で `develop` に在る。残るのは (a) 画面から選ぶ経路（BFF と SC-02）、
(b) SC-03 の表示、(c) 日報の表示である。計画の画面設計（SC-02 / SC-03）と日報テンプレートはいずれも**手法の欄をまだ持たない**（ADR-0040 の追随が入っていない）ため、
置き場所と書式は実装が決める必要がある。

## 検討した選択肢

### 日報の「当日の手法」の供給元

1. **日報を作る時点のリスク管理の設定値**（`GET /risk-controls/settings`）— 1 回の照会で済むが、日中に手法を変えた日を生成時点の 1 値で塗り潰す。**却下**。
2. **設定の変更履歴から時刻で推定**— 承認の審査と設定の保存が同時刻付近で逆転し得るうえ、履歴が読めない日は推定できない。**却下**。
3. **監査台帳の承認（`OrderApproved`）が運ぶ手法を数える**（採用）— 承認は審査時点の手法を自分で持っており（IADR-0342 決定 3）、台帳はイベント全量を保持している。
   報告書は既に同じ作法（`GET /audit/events/by-type`・JST 暦日の半開区間）で借株料・判断根拠を引いている。

### SC-02 の置き場所

1. リスク上限のフォームへ項目として足す— 上限の保存（版つき・全項目）と手法の保存（単項目・専用 API）が 1 つのボタンに混ざる。**却下**。
2. **専用フォームを発注先フォームの直後に置く**（採用）— サーバの API が単項目であり、発注先と組で読む設定（実弾の拒否が発注先に依存する）であるため。

## 決定

1. **BFF**: `PUT /bff/risk-controls/settings/stop-loss-method` を後段 `PUT /risk-controls/settings/stop-loss-method` への素通しとして登録する。統制（実弾の拒否・理由必須）は後段が実効し、
   BFF は判定しない（発注先の変更と同じ規律。IADR-0141）。
2. **SC-02**: 「損切りの実行機構（変更）」の専用フォームを発注先フォームの直後に置く。4 値のラジオ（表示名は計画の表の「ID ＋ 手法」）に挙動の 1 行説明を添え、
   理由必須・変更なしは保存不可とする。**実弾の拒否は画面とサーバが同じ式を持つ**——`isStopLossMethodPermittedOn(method, provider)`＝「S0 か、設定上の発注先が moomoo REAL でない」
   （サーバの `StopLossMethodChange.IsPermittedOn` と同形）。設定上の発注先が moomoo REAL の間は S1〜S3 のラジオを無効化し、理由を表示する。
   **逆方向**（S0 以外のまま moomoo REAL へ）は発注先フォームが同じ式で止め、`role="alert"` でサーバの `StopLossMethodNotBrokerStop` と同じ対処（先に S0 へ戻す）を示し、切替の確認へ進ませない。
   設定の変更履歴の種別 9（`StopLossMethodChanged`）に表示名「損切りの実行機構」を足す（従来は「不明(9)」と出ていた）。
3. **日報**: §4「リスク統制の記録」の子節 `### 損切りの実行機構（当日）` として置く（§4 の既存の書式——`### …（当日）` の子節と `- **項目**: 値` の箇条書き——に合わせる。日報だけ）。
   - 供給は監査台帳の `OrderApproved`（JST 暦日の半開区間）。**新規建て（`PositionEffect.Open`）の承認だけ**を、**DecisionId で重複を除いて**、承認が運ぶ手法ごとに数える（純関数 `StopLossMethodUsage.From`）。
     本文を復元できなかった記録は件数に含めず別に数えて書く。
   - 書式: 承認あり `- **新規建ての承認（承認時点の手法）**: N 件 — S0 ブローカー側逆指値 a 件 / S2 逆指値なしの建玉を許容 b 件`／承認 0 件は「なし」を明記／照会不能は
     「照会できませんでした（要確認）」と書き「なし」と書かない。固定の注記 1 行（承認の件数であり発注・約定の件数ではない・S0 以外が効くのは moomoo SIMULATE の新規建てだけ・空売りは S0）。
   - 照会不能は `ReportInput.StopLossMethods`（日報だけに適用）として未供給の入力に記録・提示する（IADR-0352 の経路）。`Audit:BaseUrl` 未設定・不正は Unsupplied（常に null）。
4. **SC-03**: 「現 Stage ／ 発注先」パネルの発注先の行の直後に「損切りの実行機構」の行を置く（`GET /status` の `stopLossMethod`・未知値は `不明(N)`）。S0 以外なら「S0 以外の手法は
   moomoo SIMULATE の新規建てにだけ効く（空売りは S0）」を注記する。変更操作は置かない（参照専用の性質を変えない）。

## 理由

- 日報の「当日の手法」は**当日に走った手法**を読ませるためのものであり（ADR-0040 決定 1「どの手法で走ったかが読めなければ観測結果を解釈できない」）、
  承認が審査時点の値を運ぶ設計（IADR-0342 決定 3）がそのまま答えを持っている。新しい契約・新しい照会口は要らない。
- 画面の即時提示とサーバの実効を同じ式にするのは、片方だけ変えると「画面は選ばせるのにサーバが 400」「画面は止めるのにサーバは受理」のどちらかへ黙って崩れるためである
  （発注先の「REAL」照合と同じ規律。IADR-0141）。
- 計画の書式を持たない欄を、計画の既存の書式（§4 の子節）に合わせて置けば、後で計画が欄を定めたときに置き換えるだけで済む。

## 結果・残余リスク

- 良い影響: 利用者は画面だけで手法を選べる（API を直接叩く必要が無くなった）。SC-03 と日報で「いまの手法」「当日に走った手法」を読める。
  手法の変更履歴が「不明(9)」と出なくなった。
- **日報の件数は承認の件数**であり、発注執行の見送り（実際の発注先が moomoo SIMULATE でない等）・約定の有無・空売りの S0 扱いを反映しない（注記で明示）。
- **損切り到達・免除の通知に建玉ごとの手法を出すこと**は本 IADR の射程外（IADR-0344 残余リスクのまま）。
- 計画の画面設計（SC-02 / SC-03）と日報テンプレートは手法の欄をまだ持たない。計画が欄を定めたら書式を揃え直す。
- 設定上の発注先と実際の発注先の食い違い（IADR-0413 決定 1）は画面でも解消しない。SC-02 の注記は「実際の発注先が moomoo SIMULATE でなければ見送り」と書くにとどめる。
