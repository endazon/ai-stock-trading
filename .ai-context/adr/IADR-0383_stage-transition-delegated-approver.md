---
title: IADR-0383 段階遷移の承認者は Bot が本文で運び、リスク管理は「信頼するクライアントのトークン」に限ってそれを採る — 承認者を特定できない遷移は行わない
type: impl-adr
status: Accepted
related_ids: [FR-20, FR-11, FR-14, UC-06, ADR-0003, ADR-0008, IADR-0062, IADR-0079, IADR-0098, IADR-0134, IADR-0240, IADR-0281, IADR-0359]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-20 段階ゲート / FR-11 監査 / FR-14 対話)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (承認・確定は利用者のみ)
  - planning:projects/ai-stock-trading/06_technical/07_discord-bot-design.md (認証・認可)
---

# IADR-0383: 段階遷移の承認者を代理の構造化欄で運び、承認者不明の遷移を拒否する

- 状態: Accepted
- 日付: 2026-09-23
- 決定者: claude（起票 [#868](https://github.com/endazon/ai-stock-trading/issues/868)。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: **FR-20**（段階ゲート＝実資金ゲートの承認記録）／**FR-11**（監査・7 年保持）／
  **FR-14**（Discord からの対話）／**UC-06**／**ADR-0003**（承認は利用者のみ）／**ADR-0008**（段階ゲート運用）
- 対象 Issue: [#868](https://github.com/endazon/ai-stock-trading/issues/868)
  （[#861](https://github.com/endazon/ai-stock-trading/issues/861) の監査が指摘し、
  [IADR-0240](IADR-0240_discord-report-review-window-and-idempotent-confirm.md) 決定 11 が
  「`/stage` は未是正・射程外」と保留していた分）
- 関連する実装仕様書: [20260923_868_stage-transition-approver-on-behalf-of](../specs/20260923_868_stage-transition-approver-on-behalf-of.md)
- 関連 IADR: [IADR-0062](IADR-0062_discord-bot-gateway-and-authorization.md) 決定 3（actor を特定できない操作は
  させない）/ 決定 4（owner マップ機密クライアント）、
  [IADR-0240](IADR-0240_discord-report-review-window-and-idempotent-confirm.md) 決定 11（報告書確定で採った作法・**本 IADR はこれを写す**）、
  [IADR-0098](IADR-0098_owner-realm-client.md)（機密クライアントの性質）、
  [IADR-0079](IADR-0079_event-backward-compat-contract-test.md) / [IADR-0134](IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定 2（契約の末尾追加）、
  [IADR-0281](IADR-0281_short-sell-release-verdict-on-stage-gate-approval-ledger.md) 決定 1（同じ入口に相乗りする verdict）

## コンテキストと課題

稼働環境で、Discord `/stage promote` による段階遷移の承認者が `unknown` で台帳に残った。

Bot は owner マップ機密クライアント（`client_credentials`・IADR-0062 決定 4）のトークンでリスク管理の
OwnerOnly エンドポイントを呼ぶ。**そのトークンの主体は人ではない。** `RiskControlEndpoints.ActorOf` は
`Identity.Name` を採り、名前クレームが無ければ `"unknown"` に倒す。`StageGate.RequestTransition` の承認者検査は
`string.IsNullOrWhiteSpace` だけなので、**`unknown` は素通りする**。その値が `stage_transitions.ApprovedBy` と
`StageTransitioned.ApprovedBy` に残り、中央監査台帳（FR-11・7 年保持）へ流れる。

段階昇格は **FR-20 の実資金ゲートの承認記録**であり、報告書の確定（#774）より監査上の重みが大きい。
「誰が実資金への移行を承認したか」が `unknown` では、台帳の意味が失われる。

kill switch・pause/resume・GFV 解除は、操作者を**理由欄**（`…（actor=<利用者>）`）に併記しているため
復元できる。**段階遷移の要求には理由欄が無い。**

併せて、#861 の監査は **Bot の `UserMapping` の値が代理の値域を外れると確定が恒常的に 400 になる**ことを
実測した（`'山田'`＝非 ASCII、`'dev owner'`＝空白入り）。Discord は唯一の確定・承認の窓口であるため、
これは「たまに失敗する」ではなく「その利用者は何も確定・承認できない」を意味する。

## 検討した選択肢

### 論点 1: 承認者をどの層で解決するか

| 案 | 内容 | 判定 |
| --- | --- | --- |
| **① Bot が要求本文に操作者を添え、リスク管理が採る**（採用） | IADR-0240 決定 11 と同じ作法 | **採用**。同じ問題に同じ形で答える（作法が 2 つに割れない）。権威側で「信じてよいか」を判定できる |
| ② リスク管理がトークンのクレームから解決する | — | **却下**。`client_credentials` のトークンに人は居ない。token exchange は計画 MSP:ADR-0086 決定 2 が採らない |
| ③ 理由欄を段階遷移の要求に足し、`actor=` を併記する | kill switch と同じ形 | **却下**。承認者は**イベントで配られ監査要約に出る**値である。自由文から切り出す形は、パースの失敗が静かに `unknown` へ落ちる。IADR-0240 が構造化した欄を選んだのと同じ理由 |
| ④ 通知側・監査側で補う | — | **却下**。**台帳（`stage_transitions`）が直らない。** 権威が持つべき値を表示層で作らない |

### 論点 2: `unknown` の承認者をどう扱うか

| 案 | 内容 | 判定 |
| --- | --- | --- |
| **(a) 要求の入口で 400 に倒す**（採用） | 台帳を 1 行も書かず、イベントも出さない | **採用**。IADR-0062 決定 3「actor を特定できない操作はさせない」と同じ向き。**実資金ゲートは厳しくなる方向にしか動かない** |
| (b) `StageGate` の純ドメインで拒否する | 拒否理由（`StageGateCriterion`）を増やす | **却下**。序数が HTTP 経路と DB 列で往来する enum を、認証の都合で増やしたくない。**「承認者を特定できない」は遷移の合否ではなく要求の不備**であり、422（受理不能な遷移）ではなく 400 が正しい |
| (c) 従来どおり通す | — | **却下**。#868 の症状そのものである |

## 決定

### 決定 1: 承認者は Bot が本文（`onBehalfOf`）で運び、リスク管理は `azp` が信頼一覧にあるときだけ採る

- 要求 `POST /risk-controls/stage-gate/transition` の**末尾**へ `onBehalfOf`（任意）を足す。
- リスク管理は純関数 `DelegatedActorResolver`（`Features/RiskManagement/`）で解決する。
  IADR-0240 の `ConfirmingActorResolver` と**同じ規律の写し**である。
- 🔴 **なりすましの閉じ方**: 本文の名前をそのまま信じない。**トークンの `azp` が構成
  `RiskControls:DelegatedActor:TrustedClientIds`（カンマ区切り）に一致するときだけ**採る。当該クライアントは
  機密（secret 必須）・`standardFlowEnabled:false`・`directAccessGrantsEnabled:false`（IADR-0098 決定 1）で、
  **利用者トークンの `azp` にはなり得ない**——`onBehalfOf` を効かせられるのは**そのクライアントの secret を持つ者
  （Bot）だけ**である。利用者トークン直叩き・一覧外のクライアント・`azp` の欠落・`azp` の大文字違いは
  **無視**する（遷移は通し、承認者はトークンの主体。無視したことは警告ログへ残す）。
  **一覧の既定は空＝誰も信じない**（設定漏れで信頼を開かない）。
- 一覧は通知側 `Notifications:Discord:OwnerAuth:ClientId`・報告書側 `Reports:DelegatedActor:TrustedClientIds` と
  **同じ秘密鍵**（`ast-secrets/discord-owner-auth-client-id`）から注入する（単一情報源）。
- Bot 側の操作者は**着信の Discord ユーザー ID から多層認証が引いた Keycloak 利用者名**（IADR-0062 決定 3）であり、
  コマンド文字列からは採らない。`IStageGateController.RequestTransitionAsync` の `onBehalfOf` は
  **省略できない引数**にする（渡し忘れを型で止める）。

### 決定 2: 台帳は操作した利用者、イベントは両方を運ぶ

- `stage_transitions.ApprovedBy`（＝`StageTransition.ApprovedBy`）には**操作した利用者**が入る。
- `StageTransitioned` の**末尾**へ `AuthorizedBy`（任意・既定 null）を足す。代理承認は
  `ApprovedBy`＝操作した利用者・`AuthorizedBy`＝クライアント ID、利用者本人のトークンは
  `ApprovedBy`＝本人・`AuthorizedBy`＝null（IADR-0079 / IADR-0134 決定 2 の規律。後方互換の追加のみ）。
- **`stage_transitions` へ `AuthorizedBy` 列は足さない**（マイグレーションを行わない）。
  報告書（IADR-0240 決定 11）も永続は `Actor` だけで、認可の主体はイベントが運ぶ。台帳が答えるべき問いは
  「**誰が承認したか**」であり、それは本決定で正しくなる。「誰の資格で通ったか」は監査台帳（イベント由来）が持つ。
- 監査要約は代理のとき「`<利用者>・代理 <クライアント>`」と書き、`unknown`／空は**「承認者不明」**と書く
  （内部の既定値を生で出さない。ReportConfirmed と同型）。**過去の台帳には `unknown` の記録が現存する**ため
  表示側の倒し方は残す。

### 決定 3: 🔴 承認者を特定できない遷移は行わない（400）

名前クレームも `azp` も無いトークンでは承認者が `unknown` に倒れる。この状態の要求は**400 で拒否し、
台帳を 1 行も書かず、イベントも発行しない**。

- 判定は `DelegatedActor.IsUnidentified`。**`client:<azp>` は通す**——人は分からなくても
  「誰の資格で承認されたか」は残るためである（IADR-0240 決定 11 と同じ倒し方）。
- **相乗りの経路（空売り実弾解禁の verdict・IADR-0281 決定 1）も同じ入口を通る**ため、同じ閂が掛かる。
  片方だけ塞ぐと迂回路になる。
- **報告書の確定とは扱いが違う。** IADR-0240 決定 11 は旧版 Bot を 400 にせず `client:<azp>` へ倒した
  （配備順で唯一の確定窓口が塞がるのを避けるため）。段階遷移でも `client:<azp>` は通すので**この非対称は無い**
  ——拒否するのは「`azp` すら無い」形だけであり、これは Keycloak のトークンでは起きない。

### 決定 4: `UserMapping` の値域外は Bot の起動時に警告する（起動は止めない）

- 純関数 `DelegatedActorName`（`NotificationService.Domain`）が値域 `\A[A-Za-z0-9._@+-]{1,64}\z` と、
  対応付けの走査を持つ。`DiscordBotHostedService.StartAsync` が値域外の対応付けを**1 件ずつ**警告する。
- 🔴 **起動は止めない。** 1 件の設定ミスで Bot ごと落とすと、他の利用者の kill switch まで止まる。
  安全側は既に効いている——値域外の利用者の確定・承認は権威側が 400 で止める。
- **1 件目で打ち切らない**（直せるのは挙がったものだけである）。

### 決定 5: kill switch・pause/resume・GFV 解除の actor 欄は**変えない**（理由欄の併記で足りる）

#868 は「理由欄の併記で足りるか、構造化した欄へ揃えるかを決める」ことを求めた。**理由欄の併記で足りる**と決める。

- 3 操作はいずれも**理由を要求本文で運んでおり**、Bot が `…（actor=<利用者>）` を添えている。
  理由は台帳・イベント・監査要約のすべてに残るため、**操作者は復元できる**（#868 自身がそう述べている）。
- 段階遷移が違ったのは**理由欄が無かった**ことであり、「クライアント主体が actor 欄に入る」こと自体ではない。
- 構造化へ揃えると 3 サービス × 3 エンドポイントの契約変更になり、**得られるのは自由文からの切り出しが要らなく
  なることだけ**である。運用標準「検査器・規約の追加は同型の事故が 2 回起きたら」と同じ節度を、契約変更にも掛ける。
- **残余リスク**: 3 操作の `actor` 列は `client:<azp>`／`unknown` のままである。`unknown` は**理由欄に操作者が
  在るとき**にだけ許容される状態であり、理由欄を持たない新しい統制操作を足すときは本 IADR 決定 1・3 の形を採ること。
  🔴 **`ActorOf(http)` は変えていない**——変えると 13 経路の倒し方が黙って変わる。

## 理由

- **同じ問題には同じ形で答える。** IADR-0240 決定 11 が報告書確定で採った作法をそのまま写すことで、
  「代理を運ぶ経路」が系に 1 つだけ存在する状態を保つ。作法が 2 つに割れると、次に理由欄を持たない操作を
  足す人がどちらを見ればよいか分からなくなる。
- **信頼の水準を新しく足していない。** 信じるのは「Bot（＝OwnerOnly の資格を持つ機密クライアント）が正直で
  あること」であり、これは Bot が多層認証の結果に基づいて承認 API を呼ぶという既存の信頼と同じものである。
  本文の値を無条件に信じれば利用者トークンで他人の名前を実資金ゲートの承認者にでき、無条件に捨てれば承認者は
  永遠にクライアントのままになる。**「値を送れる者」を secret の保有者に限る**ことで、その間を取った。
- **実資金ゲートは厳しくなる方向にしか動かない。** 本 IADR が増やすのは拒否だけである（決定 3）。
  昇格の合格基準・確認ボタン・飛び級禁止・Stage 1 の警告は 1 バイトも変えていない。信頼一覧の既定は空であり、
  設定漏れは「代理が不成立＝従来どおりトークンの主体」に倒れる（開放にはならない）。

## 結果

- 良い影響:
  - Discord から承認した段階遷移の承認者が、**台帳・イベント・監査要約のすべてで利用者名になる**。
  - `unknown` の承認記録が**新たに生まれない**（実資金ゲートの台帳が FR-11 の意味を取り戻す）。
  - 値域外の `UserMapping` に**設定した時点で**気付ける（従来は押した人にしか見えない 400 だった）。
  - Discord へ返る 400 の説明が「HTTP 400」から権威側の文言になり、**直し方が分かる**。
- 悪い影響・トレードオフ:
  - 🔴 **値域（`\A[A-Za-z0-9._@+-]{1,64}\z`）が 3 箇所に同じ形で存在する**（通知の `DelegatedActorName`・
    報告書の `ConfirmingActorResolver`・リスク管理の `DelegatedActorResolver`）。片方だけ変えると、送り手が
    「通る」と判定した名前を受け手が弾く静かな破れが起きる。**機械検査は置いていない**——サービスを跨いで
    コードを共有しない方針であり、同型の事故はまだ 1 回も起きていない（運用標準「2 回起きたら検査器」）。
  - 🔴 **Discord のメンションは値域で塞げていない**（メール形式の利用者名のため `@` を許している）。
    出所は運用者設定の `UserMapping` であり権限昇格にはならない。塞ぐのは送信側の `allowed_mentions`（IADR-0359）。
  - `client:<azp>` という表示が承認者欄に入り得る（信頼一覧の設定漏れ時）。**人ではない値が承認者欄に入る**点は
    従来どおりだが、`unknown` よりは追える。
  - **実 Keycloak のトークンで `azp` が届くことは実クラスタでは未検証**（単体テストはクレーム注入で代替。
    IADR-0240 と同じ性質・`docs/blocked-tasks.md` A-7a）。
- フォローアップ:
  - 決定 5 の残余リスク（kill switch・pause・GFV の `actor` 列）。**新しい issue は立てない**——理由欄で
    復元できており、同型の事故が起きたときに本 IADR を改定する。
  - 乖離の取り込み（`POST /risk-controls/position-drift/adopt`）の Discord 窓口は
    [#871](https://github.com/endazon/ai-stock-trading/issues/871) が本 IADR の `DelegatedActorResolver` を使う。

## 関連

- Supersedes: なし（IADR-0240 決定 11 の「`/stage` は未是正」という保留を解く）
- Superseded by: なし
