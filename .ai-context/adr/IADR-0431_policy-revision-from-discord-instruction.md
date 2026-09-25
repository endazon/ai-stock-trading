---
title: IADR-0431 Discord の自由文の指示から AI が方針の改訂案を作り、報告書の新しい版として提示する（確定は利用者・監視銘柄は案の表示のみ）
type: impl-adr
status: Accepted
related_ids: [FR-07, FR-14, FR-13, FR-04, UC-03, UC-04, UC-05, ADR-0003, IADR-0240, IADR-0115, IADR-0120, IADR-0116, IADR-0169, IADR-0420, IADR-0062, IADR-0359]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-07・FR-13・FR-14)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-03 基本フロー 4)
  - planning:projects/ai-stock-trading/04_workflows/03_reporting-cycle.md (対話的確定のシーケンス)
  - planning:projects/ai-stock-trading/06_technical/07_discord-bot-design.md (コマンド体系・設定値は参照のみ・二重実行防止)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (追補 2026-08-10)
---

# IADR-0431: Discord の自由文の指示から AI が方針の改訂案を作る

- 状態: Accepted
- 日付: 2026-09-26
- 決定者: claude（起票 [#1016](https://github.com/endazon/ai-stock-trading/issues/1016)。利用者要望 2026-09-26「AI が案を作り、利用者が確定する」。利用者レビューは PR で受ける）

## 起点・関連

- 対象 Issue: #1016
- 関連する実装仕様書: [20260926_1016_policy-revision-from-discord](../specs/20260926_1016_policy-revision-from-discord.md)
- 関連 IADR: [IADR-0240](IADR-0240_discord-report-review-window-and-idempotent-confirm.md)（Discord の報告書レビュー・版番号付き冪等確定・OnBehalfOf）、
  [IADR-0115](IADR-0115_report-auto-generation-scheduler.md) 決定 4（「LLM による方針提案は、Discord 経由の対話と合わせて別途設計する」＝本 IADR がその設計）、
  [IADR-0120](IADR-0120_report-kind-purpose-and-parent-policy-feedforward.md)（種別ごとの purpose・上位方針）、[IADR-0116](IADR-0116_report-draft-discord-notification.md) 決定 3（投稿本文の無害化は発行側）、
  [IADR-0169](IADR-0169_rag-context-injection-defense.md)（注入対策）、[IADR-0420](IADR-0420_cross-service-read-contract-convention-and-guard.md)（越境の契約テスト）

## コンテキスト

利用者は方針を変えたいとき、Discord から自由文で指示し、AI に方針（と監視銘柄）を策定し直させたい。計画は Discord からの
**報告書の修正指示（自然文）**を FR-14・07_discord-bot-design・UC-03 基本フロー 4 で認めているが、実装は差し戻し（状態の遷移）
しか持たず、自由文から方針を作り直す経路が無かった（IADR-0115 決定 4 が「別途設計する」と保留していた）。

一方、**監視銘柄の変更を Discord から適用することは計画が禁じている**（FR-14「設定値の変更は Discord からは参照のみ」、07
「監視銘柄 … は Discord からは参照のみ」）。

## 決定

### 決定 1: 案の実体は報告書の新しい版であり、確定は既存の版番号付き確定だけが行う

`POST /reports/policy-revisions`（OwnerOnly）が案を作り、対象の報告書へ**新しい版のドラフト**として保存して提示（承認待ち）する。
確定はしない。Discord の確認ボタン（既存の `ast-report-approve-<periodKey>-<version>`）が版番号付きで確定し、確定者は
OnBehalfOf（IADR-0240 決定 11）で残る。**別の保留状態（有効期限つきの提案表）は持たない**——報告書の版番号が既に
「1 期間 1 つ・古い版は確定できない・確定は 1 度」を保証し、次の改訂で前の案のボタンは版不一致になる。

- 対象: 会話キー省略時は当日（JST）の日報。既存の未確定の報告書はその方針を土台に改訂する。確定済みは改訂しない（409）。
- 新規は**当日（JST）の日報だけ**。土台は直近の確定済み日報（方針・`BasedOn`・`AssumptionsVersion` を引き継ぐ。数値を発明しない）。
  確定済み日報が無ければ作らない。**より新しい日付の確定済み日報があれば作らない**（最新の確定済み日報は開始日の最大で決まるため、
  確定しても方針に効かない報告書を作らない）。新規に作ると、その日の自動生成は既存の行を踏まない規則でスキップされる（応答で知らせる）。
- 本文の末尾へ「利用者の指示による方針の改訂（版 N）」を追記する（指示者・時刻・指示の原文・案・入れ替え案・説明）。
  確定時に KB へ保存され、改訂の監査記録になる。

### 決定 2: LLM の出力は厳格な JSON。1 つでも外れたら案全体を捨て、失敗は「案なし」で返す

スキーマは `policySummary`（必須・2000 文字）・`watchlistChanges`（追加 5・除外 5 まで・米国のティッカー書式・理由 200 文字・重複不可）・
`rationale`（任意・1000 文字）。**部分採用しない。** 未知の項目は読まない（スキーマ外の操作を表現されても実行経路が無い）。
LLM の未構成・失敗・タイムアウト・送信拒否・拒否・禁止モデル・形式違反はすべて**案なし**（502）で、**何も保存しない**。
散文ドラフトのようにプレースホルダへ倒さない（方針は確定すると取引に効くため、「作れなかった」を定型の方針文に化けさせない）。

### 決定 3: 輸送・purpose・費用は散文ドラフトと共用し、指示はデータとして 1 行 JSON で渡す

輸送は Program.cs の `ResolveReportLlmTransport`（gRPC → REST → 無し）を散文ドラフトと共用する。purpose は種別から
（`report-daily` 等。**新しい purpose を作らない**——基盤の割当表に無い値は無音で DefaultModel へ落ちる）。費用は本文の扱いと独立に
計上し（報告書の LLM 費用は月次上限の対象外・実績は月報）、割当逸脱は通知する。上限は `Reports:PolicyRevision:TimeoutSeconds`（既定 60 秒）。
プロンプトは ADR-0003 追補の対策 1（データ／命令の構造分離）に従い、現在の方針・上位方針・利用者の指示を**フェンス内の 1 行 JSON 文字列**で渡す
（U+2028 / U+2029 も明示的にエスケープ）。指示は認証済みの利用者本人のものだが、出力形式と権限は指示で変えられないと明示する。

### 決定 4: 監視銘柄の入れ替え案は提示と記録だけ。Discord からは適用しない

案の入れ替え案は Discord の表示と報告書の本文にだけ現れる。**通知サービスに監視銘柄を変える口を置かない**
（`IPolicyRevisionController` は `ReviseAsync` 1 つ・監視銘柄のポートを持たないことを `DiscordSettingsAreReadOnlyTests` が固定）。
適用は設定画面（SC-02。FR-13: 理由必須・監査・楽観排他）で利用者が行う。Discord からの適用は計画の改定（planning への環流）を待つ。

### 決定 5: Discord は `/policy instruction:<自由文> period:<任意>`。投稿本文は発行側で無害化する

- 多層認証 → 解析（`/policy` / `/policy <periodKey>`。指示は RawCommand に載せず別の引数で運ぶ）→ 指示の検証（空・1000 文字超は LLM を呼ばない）。
- 応答の方針・理由・説明は報告書サービスが `ReportSummarySanitizer` を通して返す（IADR-0116 決定 3 と同じ位置）。Bot は
  メンション抑止の出口（IADR-0359）で投稿し、1800 文字に収める。
- 呼び出しは専用の名前付き HttpClient（90 秒）。**4xx/5xx は案なし・タイムアウトと例外と解釈不能な 2xx は「不明」**として伝え、
  不明のときは `/report show` で確かめるよう促す（保存された可能性を「失敗」と言わない）。
- 自由文の窓口を「ドラフト通知へのリプライ」ではなくスラッシュコマンドの引数にしたのは、リプライが MessageContent Intent を要する
  （IADR-0062 決定 2 の最小 Intents を崩す）ため。07 §リスク・未決事項は「スラッシュコマンドの登録 … は実装時に確定する」としている。

## 却下した案

- **確定時に入れ替え案を市場監視へ適用する**: FR-14・07 が Discord からの設定値の変更を禁じている。計画外。
- **提案表（有効期限・1 件のみ）を別に持つ**: 報告書の版番号が同じ保証を既に持つ。二重の状態は食い違いの源になる。
- **形式違反の出力から読める部分だけ採る**: 取り違え（追加と除外・銘柄の誤記）が案に紛れ、利用者のレビューを素通りしやすい。
- **失敗時に「現状維持」の定型方針を案にする**: 利用者が案と読んで確定し得る（原則 A）。
- **任意の会話キーで新しい報告書を作る**: 未来日のキーは自動生成と衝突し、同日の 2 本目は最新の確定済み日報の判定を不定にする。

## 残余リスク

- **改訂の回数に上限が無い。** 利用者（多層認証を通った本人）だけが呼べるが、報告書の LLM 費用は月次上限の対象外であり、
  連打すれば費用が積み上がる。計画側の費用統制の射程を環流する（下書きは PR 本文）。
- 実 Discord での `/policy` の疎通（登録・入力補完・長い応答の Defer）は AI セッションでは確かめられない（A-7a の実機確認に追加）。
- 新規に当日の日報を作ると、その日の自動生成の日報（取引結果の集計）は作られない。応答で知らせるが、集計が要る日は
  自動生成の後に既存の日報を改訂する運用が要る。
- 監視銘柄の入れ替え案を適用するには、利用者が設定画面で別途操作する必要がある（計画どおり）。
