---
title: IADR-0423 乖離の取り込みの窓口を Discord Bot にも置く — `/drift adopt` は kill switch と同水準の確認を経て API を呼び、操作者は onBehalfOf で運んで理由文は加工しない
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-11, FR-14, UC-06, ADR-0003, ADR-0028, ADR-0041, IADR-0062, IADR-0079, IADR-0097, IADR-0182, IADR-0240, IADR-0350, IADR-0359, IADR-0383, IADR-0408]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0041_out-of-system-trade-adoption-and-capital-baseline.md (決定 4: 窓口は REST API と Discord Bot の両方・記録の内容は同じ)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 / FR-11 / FR-14)
---

# IADR-0423: 乖離の取り込みの Discord 窓口と、窓口に依らない記録の内容

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: claude（起票 #871。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: **ADR-0041 決定 4**（取り込みの窓口は owner の REST API と Discord Bot の両方。**コマンドの形は計画では定めない。
  記録の内容〔由来のラベル・操作者・理由・数量・観測時刻〕だけを揃える**）、ADR-0028 決定 2（人が調べて直し、誰が・なぜを監査へ残す）、
  ADR-0003（取り込みは owner に限る）、FR-10 / FR-11 / FR-14、UC-06
- 対象 Issue: #871
- 関連する実装仕様書: [20260925_871_discord-drift-adopt](../specs/20260925_871_discord-drift-adopt.md)
- 関連 IADR: [IADR-0350](IADR-0350_owner-approved-ledger-drift-adoption.md)（取り込み API・拒否の種類）、
  [IADR-0383](IADR-0383_stage-transition-delegated-approver.md)（`DelegatedActorResolver`・操作者を特定できない要求の拒否。
  同 IADR のフォローアップが「#871 が本 IADR の `DelegatedActorResolver` を使う」と予告していた）、
  [IADR-0240](IADR-0240_discord-report-review-window-and-idempotent-confirm.md) 決定 11（`onBehalfOf` の作法）、
  [IADR-0182](IADR-0182_gfv-violation-clearing.md)（GFV 解除の Discord 窓口＝最も近い同型）、
  [IADR-0062](IADR-0062_discord-bot-gateway-and-authorization.md)（多層認証・owner マップ機密クライアント）、
  [IADR-0097](IADR-0097_killswitch-disengage-confirmation-phrase.md)（確認フレーズのモーダル・モーダル ID の分離）、
  [IADR-0079](IADR-0079_event-backward-compat-contract-test.md)（イベントの後方互換の追加）、
  [IADR-0408](IADR-0408_report-positions-and-sizing-context-read-tolerance.md) 決定 3（送り手の本物の型による契約テスト）、
  [IADR-0359](IADR-0359_discord-allowed-mentions-at-the-exit.md)（応答のメンション抑止）

## コンテキストと課題

- 取り込み（IADR-0350）は `POST /risk-controls/position-drift/adopt`（OwnerOnly・理由必須）だけにあった。ADR-0041 決定 4 は、実害が
  「手仕舞い API が使えない状況」で出たことを理由に、**Discord Bot からも取り込めるようにする**と裁定した。
- Bot は owner マップ機密クライアント（`client_credentials`）のトークンでリスク管理を呼ぶ。そのトークンには**人が居ない**。
  取り込み API は操作者を `RiskControlEndpoints.ActorOf`（トークンの名前 → 無ければ `unknown`）で採っていたため、そのまま Bot から
  呼ぶと**台帳・監査の操作者が `unknown` になり、API 経由と記録の内容が揃わない**（#868 の段階遷移と同じ構造）。
- Bot の既存の破壊的操作（kill switch・GFV 解除）は、操作者を**理由文**へ `（Discord Bot 経由・actor=…）` と併記して運ぶ
  （IADR-0383 決定 5 が「理由欄の併記で足りる」と決めた）。

## 検討した選択肢

### 論点 1: 操作者をどう運ぶか

| 案 | 内容 | 判定 |
| --- | --- | --- |
| **① 本文の `onBehalfOf` で運び、リスク管理が `DelegatedActorResolver` で採る**（採用） | 段階遷移（IADR-0383）・報告書確定（IADR-0240 決定 11）と同じ作法 | **採用**。台帳の `Actor` が API 経由と同じく**操作した利用者**になる |
| ② GFV 解除と同じく理由文へ `actor=` を併記する | 理由欄はあるので IADR-0383 決定 5 の形でも運べる | **却下**。🔴 **ADR-0041 決定 4「どちらの窓口から行っても記録の内容は同じ」に反する**——同じ取り込みでも理由文が窓口で変わり、台帳の `Actor` は `unknown`／`client:<azp>` のまま。後から「どちらから取り込んだか」でしか読めない差が生まれる（決定 4 が名指しで避けた形） |

### 論点 2: 確認の水準

| 案 | 判定 |
| --- | --- |
| **kill switch・GFV 解除と同水準（確認ボタン → 理由＋確認フレーズのモーダル）**（採用） | **採用**。台帳を書き換える操作であり、#871 が「kill switch と同水準」を求めた。確認フレーズは kill switch と**同じ設定値**（GFV 解除と同じ。設定点を増やすと設定漏れの面が増える） |
| pause と同水準（確認ボタンのみ） | **却下**。pause は可逆だが、取り込みは台帳を書き換え、元へ戻す操作が無い |

### 論点 3: 確認の前に現在の乖離（台帳と観測の数量）を見せるか

| 案 | 判定 |
| --- | --- |
| 見せる | **見送り**。取り込み対象の乖離の現況を返す読み取り口がリスク管理に無く、新しい API が要る。本件の求め（窓口を足す・記録を揃える）を超える |
| **確認文面で「数量は観測が決める・減らす乖離だけ・実現損益は記録しない」を明記し、結果に前後の数量と観測を出す**（採用） | 利用者は結果で何が起きたかを確かめられる。受理不能ならリスク管理の理由がそのまま返る |

## 決定

### 決定 1: コマンドは `/drift adopt symbol:<銘柄コード> market:<japan|us>`。数量は取らない

- 副コマンドは `adopt` の 1 つだけ。解析は `BotCommandParser`（純関数）に置き、引数の過不足・書式外の銘柄コード（英数字・ピリオド・
  ハイフンの 1〜16 文字以外）・未知の市場・数量の付加はすべて `Unknown` へ倒す（台帳を書き換える操作を曖昧一致で起動させない）。
  銘柄コードの大小文字は変えない（台帳の値と突き合わせる値であり、推測で補正しない）。
- 🔴 **設定値の変更ではない。** FR-14 の「設定値の変更は Discord からは参照のみ（例外は kill switch と pause/resume）」の射程外であり、
  ADR-0041 決定 4 が Discord の窓口を明示した（`DiscordSettingsAreReadOnlyTests` の「例外は 2 系統だけ」の列挙には入れない。
  GFV 解除・段階遷移・報告書確定と同じ扱い）。同テストの「設定変更の試みでどのコントローラも呼ばれない」には新しいハンドラを加えた。

### 決定 2: 閂は kill switch・GFV 解除と同じ並び。止まればリスク管理を呼ばない

`PositionDriftAdoptionCommandHandler`: **多層認証 → コマンド解析（取り込み以外は拒否）→ 確認フレーズ（未設定は拒否）→ 理由必須 →
リスク管理の呼び出し**。Gateway はスラッシュコマンドで許可外にはボタンすら出さず、確認ボタン（Danger・対象を CustomId に載せる）→
理由＋確認フレーズのモーダル（ID は取り込み専用の接頭辞）→ ハンドラの順にだけ呼ぶ。**モーダルを経ずにハンドラへ入る経路は無い。**
閂で止まったときの応答は内部の層名を出さず、「台帳は変わっていません」と明示する。

### 決定 3: 操作者は `onBehalfOf` で運び、理由文は加工しない

- Bot は多層認証が解決した Keycloak 利用者名を本文の `onBehalfOf` に載せる（`IPositionDriftAdoptionController.AdoptAsync` の
  **省略できない引数**）。理由文は前後の空白だけを落として**そのまま**送る。
- リスク管理は要求の**末尾**へ `onBehalfOf`（任意）を足し、`DelegatedActorResolver`（IADR-0383）で解決する。
  **信頼するクライアントのトークン（`azp` が `RiskControls:DelegatedActor:TrustedClientIds` に載る）に限って**採り、
  利用者トークン直叩き・一覧外のクライアントでは無視する（他人の名前で取り込めない）。
- 台帳（`drift_adoptions.Actor`）・イベントの `Actor`・応答の `actor` は**操作した利用者**。

### 決定 4: 操作者を特定できない取り込みは 400 で拒否する（IADR-0383 決定 3 と同じ閂）

- 信頼クライアントが値域外の `onBehalfOf` を送った・名前も `azp` も無いトークン、はいずれも **400**。台帳を 1 行も書かず、イベントも出さない。
- `client:<azp>` は通す（人は分からなくても誰の資格で行われたかは残る）。
- API の利用者直叩きには影響しない（Keycloak の利用者トークンは名前を持つ）。**API 側の口・権限・拒否 8 種は変えていない。**

### 決定 5: イベントは認可の主体を末尾の任意項目で運ぶ。台帳の表は変えない

- `PositionDriftAdopted` の末尾へ `AuthorizedBy`（任意・既定 null）を足す（IADR-0079 の後方互換の追加。`event-schemas.baseline.json` にも載せた）。
  代理（Bot 経由）は `Actor`＝利用者・`AuthorizedBy`＝クライアント ID、利用者本人のトークンは `AuthorizedBy`＝null。
- 🔴 **それ以外の項目（操作者・理由・前後の数量・観測値・観測時刻・実現損益が未記録であること）は窓口に依らず同じ**である
  （T-10-983 が、同じ利用者が API から取り込んだ場合と Bot から取り込んだ場合の記録を比べて固定する）。
- 監査要約は `（<操作者>・代理 <クライアント>）`、通知は `操作者 <操作者>・<クライアント> 経由`（段階遷移・報告書確定と同じ書き方）。
  過去の `unknown` は監査要約で「操作者不明」と書く。
- 台帳の表（`drift_adoptions`）へ `AuthorizedBy` 列は足さない（マイグレーションなし。段階遷移・報告書と同じく、台帳が答えるのは
  「誰が取り込んだか」であり、「誰の資格で通ったか」はイベント由来の監査台帳が持つ）。

### 決定 6: 応答を名前付きの型にし、受け手の契約テストを送り手の本物の型で書く

- 200 の本文を匿名型から `PositionDriftAdoptionResponse`、422 の本文を `PositionDriftAdoptionRejectionBody` にした（JSON は同じ）。
  200 には記録した操作者 `actor` を**末尾追加**した（窓口が代理の成立を確かめられる）。要求型 `PositionDriftAdoptionRequest` も公開型にした。
- 受け手（Bot）の契約テストは送り手の本物の型を web 既定で直列化した本文を読ませ（T-10-989。逆向きの要求本文も本物の要求型で読み戻す）、
  送り手の本物の Program.cs が出す本文がその型の直列化と一致することを T-10-985 が固定する（IADR-0408 決定 3 の形）。
- 受理不能（422）の `error`（何が足りないか・どうすれば通るかの文言。IADR-0350）は、Bot が**そのまま**利用者へ返す。

### 決定 7: 配備はリスク管理 → 通知の順

- 通知を先に配備すると、窓の間の Discord 経由の取り込みは旧リスク管理が `onBehalfOf` を読み飛ばし、台帳の操作者が `unknown` になる
  （作業仕様書 規則 11 の (b)）。リスク管理を先に配備すれば窓の間は旧 Bot に `/drift` が無く、この記録は生じない。
- 窓が生じても見えるよう、Bot は 200 の応答に `actor` が無いとき「操作者が記録されたかを応答から確認できません（リスク管理が旧版の可能性）」を添える。

## 理由

- **ADR-0041 決定 4 の本体は「記録の内容は同じ」である。** 窓口を足すこと自体より、窓口で記録が変わらないことのほうが重い
  （計画は「窓口で記録が変わると、後から『どちらから取り込んだか』でしか読めない差が生まれる」と書く）。操作者を構造化した欄で運び、
  理由文を加工しないことで、台帳・イベント・監査のどの面でも API 経由と同じ値が入る。
- **同じ問題には同じ形で答える。** 「Bot のトークンに人が居ない」問題には、段階遷移・報告書確定で既に `onBehalfOf` ＋
  `DelegatedActorResolver` という答えがある。新しい作法を作らない。
- **確認は既存の最も重い水準へ揃える。** 取り込みは台帳を書き換え、戻す操作が無い。kill switch・GFV 解除と同じ閂・同じ確認フレーズにした。

## 結果

- 良い影響:
  - 手仕舞い API や画面が使えない状況でも、Discord から乖離を解ける（#849 の実害の形）。
  - Discord 経由でも台帳の操作者は利用者名になり、API 経由と記録の内容が揃う。
  - 拒否の理由（観測が古い・乖離が無い・減らす乖離だけ等）が Discord の応答にそのまま出る。
- 悪い影響・トレードオフ:
  - 🔴 **実 Discord での疎通は未検証**（Gateway は Discord.Net の型に依存し単体テストの対象外。判断＝認可・解析・確認・理由はすべて
    Discord.Net 非依存のハンドラに置いて単体テストで固定した）。`docs/blocked-tasks.md` A-7a の再測定手順に項目を足した。
  - 確認ボタンを押す前に、現在の乖離（台帳と観測の数量）を Discord で見られない（論点 3）。結果の文面と受理不能の理由で代える。
  - kill switch・pause・GFV 解除の操作者欄は `client:<azp>`／`unknown` のまま（IADR-0383 決定 5 の残余リスクのまま。本件は取り込みだけを揃えた）。
  - `DelegatedActorResolver` を使う操作が 2 つ（段階遷移・取り込み）になった。値域の 3 箇所重複（IADR-0383 の残余リスク）は変わらない。
- フォローアップ: なし（見送った論点 3 は、乖離の現況を返す読み取り口が別件で要るときに併せて扱う）。

## 関連

- Supersedes: なし（IADR-0350 決定 2 の口・権限・拒否は変えない。操作者の採り方だけを `DelegatedActorResolver` へ寄せた）
- Superseded by: なし
