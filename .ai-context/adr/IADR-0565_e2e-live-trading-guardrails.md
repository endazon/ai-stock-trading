---
title: IADR-0565 E2E の live 取引ガードレール——live では PUT broker-provider と POST /opend-auth/{code,resend} を送らず、閉場判定を trading の globalSetup に置き（開場中・in-flight ありは exit 2）、取引に影響する書き込みは閉場後だけ・スナップショットと復元・差分が残れば赤とし、層の定義は MSP の IADR-0569 に従う（AST は L3-L を持たない）
type: impl-adr
status: Accepted
related_ids: [NFR, SC-01, SC-02, SC-03, SC-04, FR-20, IADR-0087, MSP:IADR-0569]
author: claude (Claude Code)
created: 2026-10-21
updated: 2026-10-21
plan_refs:
  - MSP:docs/tests/E2E_STANDARDS.md（E2E の基準書。正本）
  - MSP:IADR-0569（層・タグ・project 名・E2E_LIVE / E2E_LAYER）
  - MSP#2013 コメント 3 §5（AST の取引影響層・閉場の判定・取り消しと復元・禁止事項）
related_specs:
  - ../specs/20261021_1411_e2e-standards-reference-and-live-guardrails.md
---

# IADR-0565: E2E の live 取引ガードレール（#1411）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-21
- 決定者: claude が起案（起点 #1411＝MSP#2013 の分割 E-01。親 #1410。レビューは PR で受ける）

## 起点・関連

- 起点 issue: #1411（親 #1410 の網羅表。禁止行 A-049・A-067、L3-T の 6 行）。設計の正本は MSP#2013（コメント 3 §5）。
- 関連する計画書 ID: NFR（無採番）。対象画面 SC-01〜SC-04、発注先の切替は FR-20（段階ゲート）の運用に直結する。
- 正本として従う決定: **MSP:IADR-0569**（E2E の実行層 L1/L2/L3/L3-T/L3-L、タグ、Playwright の project 名、opt-in の `E2E_LIVE` / `E2E_LAYER`）。
  同 IADR §影響は「AST 固有の決定（L3-T で送らない操作・閉場判定の置き場）は AST 側の IADR が持つ」とし、本 IADR がそれである。
- 前提として覆さない IADR: [IADR-0087](IADR-0087_frontend-playwright-e2e.md)（L1 は E2E ハーネス＋ `page.route` のモック）。
- 人が読む基準: `docs/tests/README.md` §6（MSP の基準書を正本として参照し、AST 固有の点だけを書く）。
- 作業仕様書: `.ai-context/specs/20261021_1411_e2e-standards-reference-and-live-guardrails.md`

## コンテキストと課題

MSP#2013 は E2E を CI のスタブ（L1）から稼働中の PoC（L2 以降）へ広げる。PoC の AST は**実際に取引している**
（米国株・moomoo。SIMULATE / paper）。live の E2E が書き込む設定は、次のサイクルの発注可否・新規建て・手仕舞いを変える。
MSP 側の IADR-0569 は層の名前と opt-in だけを決め、**「どの操作を送らないか」「いつなら書いてよいか」は可変ユニット側に残した**。
後続（E-20〜E-23・E-33 ほか）が live の spec と config を書く前に、送ってはならない操作と書き込みの時間の条件を 1 か所に決めておかないと、
spec ごとに判断が分かれ、1 本の綴り違いで実取引の環境が変わる。

BFF から画面経由で到達できる書き込みのうち、取引そのもの・オーナーの端末に作用するもの（MSP#2013 コメント 3 §5.1 の実測）:

| 操作 | 作用 | 戻せるか |
| --- | --- | --- |
| `PUT /bff/risk-controls/settings/broker-provider` | 実行環境の切替（SIMULATE / paper / REAL）。paper↔SIMULATE でも Stage 1 の算入に影響する | 戻しても切替の間の判断・算入は戻らない。発注先の変更履歴にも行が増える |
| `POST /bff/opend-auth/code`・`/resend` | オーナーの端末へ SMS が飛ぶ・OpenD の認証状態を変える | 戻せない |
| リスク上限・取引ガード・閉場前統制・テーマ集中・高ボラティリティ・Stage 1 最小件数・市場監視・監視銘柄（`e2e-` ラベルの追加と削除）・全体前提条件の更新 | 次サイクル以降の発注可否・新規建て・手仕舞い | 変更前の値を PUT すれば値は戻る（履歴の行は残る） |
| 損切り機構 `PUT …/settings/stop-loss-method` | REAL 中は S0 以外不可。保有中の建玉の損切りの仕方が変わる | 値を変えると、戻すまでの間の保護が戻らない。**現在値と同じ値の書き込みだけ**を L3-T に置く（決定 1） |

## 検討した選択肢

| 論点 | 案 | 採否 |
| --- | --- | --- |
| 発注先・OpenD 認証 | A. 閉場後なら送ってよい（後で戻す） | 採らない。戻しても作用（算入・SMS・OpenD の状態）が戻らない |
| | B. paper↔SIMULATE だけ許す | 採らない。Stage 1 の算入に影響し、REAL を含む値の綴り違いと区別できない |
| | **C. live ではどの値へも送らない（L1 のモックだけで検証）** | **採る** |
| 閉場判定の置き場 | A. 各 spec の `beforeAll` | 採らない。1 本の書き忘れで開場中に書く。判定が spec の数だけ散る |
| | B. 実行する人の確認だけ（workflow の入力） | 採らない。機械の門が無い |
| | **C. `trading` project の globalSetup で 1 回判定し、満たさなければ何も実行しない** | **採る** |
| 門を満たさないとき | A. skip（緑） | 採らない。全 skip の緑と区別できない（MSP:IADR-0569 決定 4 の「前提未整備」） |
| | B. 失敗（exit 1） | 採らない。「書き込みが壊れた」赤と区別できない |
| | **C. exit 2（前提未整備）** | **採る**（MSP:IADR-0569 決定 4 と同じ扱い） |
| 後始末の検証 | A. 各テストの afterEach で戻すだけ | 採らない。afterEach の失敗・中断で戻らない値を誰も検知しない |
| | **B. 事前スナップショット ＋ afterEach の復元 ＋ globalTeardown の突合（差分が残れば赤）** | **採る** |

## 決定

### 決定 1 — live では `PUT broker-provider`（どの値へも）と `POST /opend-auth/{code,resend}` を送らない

- 対象は live の全層（`live-ro`・`live-rw`・`trading`）。`trading` 層でも送らない。
- 発注先の REAL 警告モーダルは、live では**チェックと文字入力を揃える手前まで**だけ操作し、送信ボタンが有効にならないことだけを見る（送信しない）。
  切替の成功・拒否の経路は L1（`installBff` のモック）だけで検証する。
- OpenD 認証は live では `GET /bff/opend-auth/state` の読み取り（L2）だけ。コード送信・再送の経路は L1 だけで検証する。
- 網羅表では禁止行 **A-049**（`PUT broker-provider`）と **A-067**（`POST /opend-auth/{code,resend}`）として持つ。テストとしては存在させず、
  **live の spec にこれらの呼び出しが無いことを機械の検査（grep）で固定する**。検査は live 用 config・`e2e/live/` を置く後続の変更が足す（それまで本決定は規約だけ）。
- 同じ理由で、live では次も送らない（L3-T でも同じ。決定 3 の復元つきの変更の例外にもならない）: 既存の監視銘柄の削除（自分が追加した `e2e-` ラベルのものの削除は除く。決定 3）、`PUT …/settings/stop-loss-method` を現在値と異なる値で送ること（現在値と同じ値の「無変更の書き込み」だけ許す）。

### 決定 2 — 閉場判定を `trading` project の globalSetup に置き、開場中・in-flight ありなら exit 2

- `trading` project（L3-T）の globalSetup が、spec を 1 本も走らせる前に次の 2 つを機械で確かめる。
  1. **全対象市場が閉場しており、次の開場まで 60 分以上ある**（半日取引日・休場日を含め、AST の市場時間の規則と同じ規則で判定する）。
  2. **統制状態 `GET /bff/risk-controls/status` に in-flight（約定待ち・手仕舞い中）が無い**。
- どちらかを満たさなければ **exit 2（前提未整備）**で終え、何も実行しない（skip の緑にも失敗の赤にもしない。MSP:IADR-0569 決定 4）。
- 判定は spec に書かない（各 spec の `beforeAll` へ散らさない）。判定の実体（市場時間を Node で持つスクリプト）は後続の変更が置く。
- `trading` は `E2E_LAYER` に明示されたときだけ走り（MSP:IADR-0569 決定 4）、cron は置かずオーナーの開始指示（`workflow_dispatch`・手元実行）だけで起動する。

### 決定 3 — 取引に影響する書き込みは閉場後だけ。スナップショット・復元・差分が残れば赤

- 取引に影響する書き込み（コンテキストの表の 3・4 行目。損切り機構は同値の書き込みに限る（決定 1）。SC-01 の全体前提条件・SC-02 のリスク設定の更新、監視銘柄の追加と削除）は **`trading` 層（L3-T）でだけ**行う。`live-rw`（L3）には置かない。
- **事前スナップショット**: globalSetup（決定 2 の門を通った後）が対象の設定を GET で全て読み、成果物に保存する（秘密は含まない）。
- **各テスト**: 変更前の値を取り、変更 → 画面で反映を確認 → afterEach で変更前の値を理由 `[e2e <runId> restore]` で PUT する（版は再読込した最新を使う）。
  監視銘柄は自分が追加した `e2e-` ラベルのものだけを削除する。現在値と同じ値の PUT（同値の書き込み）は復元を要しない。
- **globalTeardown**: スナップショットと現在値を突合し、差分があれば再度 PUT で復元し、**それでも差分が残れば赤**（exit 1・差分を成果物に出す）。残った項目はオーナーが画面で戻す。
- 中断（Playwright の kill）に備え、復元はスナップショットを入力に単独でも実行できる形にする（後続の変更が置く）。
- 変更履歴・監査の行は消せない。理由欄の `[e2e <runId>]` で人が識別できるようにし、履歴を見るテストは「自分の行が増えた」ことだけを見る。

### 決定 4 — 層の定義は MSP の IADR-0569 に従う。AST は L3-L を持たない

- 層（L1 / L2 / L3 / L3-T / L3-L）・タグ（`@live-ro` `@live-rw` `@trading` `@llm`・`@p0`〜`@p2`）・project 名・`E2E_LIVE` / `E2E_LAYER` の意味は
  **MSP:IADR-0569 が正本**であり、AST は名前を変えず、別の綴りを足さない。変えるには MSP 側の IADR を改める。
- **AST は L3-L（`@llm`・project `llm`）を持たない**。AST の画面操作で LLM を直接呼ぶものが無い（取引判断は収集イベント起点、報告書は Discord と定時）。
  `@llm` の spec を AST に置かない。
- AST の L1 は MSP の L1（ビルド済み SPA の preview）と違い、**E2E ハーネス（`frontend/e2e/harness/`）＋ `installBff`（`frontend/e2e/fixtures.ts`）**で動く（IADR-0087）。
  層としては L1 であり、PR の必須チェックの扱いは MSP:IADR-0569 決定 1 と同じ。
- 射程外: kill switch・`/pause` `/resume`・段階遷移・方針の承認・手仕舞い発注・position-drift の採用は Discord Bot 専用で BFF に無いため、Playwright の対象にしない（.NET 統合テストとオーナーの Discord 確認に委ねる）。

## 理由

- 戻せない作用（発注先の切替の間の算入、SMS、OpenD の状態）は、閉場後でも「試して戻す」が成立しない。送らないことでしか守れない。
- 閉場判定を globalSetup の 1 か所に置けば、spec の書き忘れで開場中に書き込むことが構造的に起きない。exit 2 にすれば「前提が揃わなかった」が緑と赤のどちらにも紛れない。
- 復元を afterEach だけに頼ると、失敗・中断で戻らない値が黙って残り、次の取引サイクルがそれで動く。突合して赤にすれば、残ったことが必ず人に届く。
- 層の名前を AST で別に決めると、MSP と AST で `--grep`・`testIgnore` が食い違い、黙って 0 件になる（MSP:IADR-0569 §コンテキスト）。

## 結果

- 本 PR で変わるのは文書だけ（`docs/tests/README.md` §6・本 IADR・索引）。Playwright の config・spec・ワークフロー・必須チェックは変わらない。
- 後続の live の spec・config・スクリプトは本 IADR の決定 1〜3 を受け入れ基準に入れる。
- 🔴 決定 1 の grep 検査・決定 2 の閉場判定・決定 3 の突合は、それを置く後続の変更までは**規約だけ**で、機械では守られていない。それまで live の `trading` 層を流さない（流す経路自体がまだ無い）。

## 関連

- MSP:IADR-0569 / `MSP:docs/tests/E2E_STANDARDS.md`
- IADR-0087（L1 のハーネス）
- #1410（網羅表）・MSP#2013（設計）
