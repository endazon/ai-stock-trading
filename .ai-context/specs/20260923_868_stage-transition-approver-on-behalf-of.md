---
title: Discord 経由の段階遷移の承認者を「代理される利用者」で残し、承認者を特定できない遷移を拒否する
type: spec
status: accepted
related_ids: [FR-20, FR-11, FR-14, UC-06, ADR-0003, ADR-0008, IADR-0062, IADR-0240, IADR-0098, IADR-0134, IADR-0079, IADR-0383]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-20 段階ゲート / FR-11 監査 / FR-14 対話)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (確定・承認は利用者のみ)
  - planning:projects/ai-stock-trading/06_technical/07_discord-bot-design.md (認証・認可)
---

# 仕様書: Discord 経由の段階遷移で承認者が unknown のまま実資金ゲートの台帳に残る（#868）

## 起点

- [#868](https://github.com/endazon/ai-stock-trading/issues/868)。[#861](https://github.com/endazon/ai-stock-trading/issues/861)
  （報告書確定の確定者・[#774](https://github.com/endazon/ai-stock-trading/issues/774)）の監査が
  「同型の問題が段階遷移に残っている」と指摘し、IADR-0062 / IADR-0240 に「未是正」と書かれたまま追跡先が無かった分。
- 先行する是正の作法は **IADR-0240 決定 11**（要求の `onBehalfOf` ／ イベントの `AuthorizedBy` ／ `azp` が
  信頼クライアント一覧に載るときだけ採る）である。本件はこれを段階遷移へ写す。

## 🔴 実測（コードで確認・`origin/develop` = `d97b05b5`）

| 箇所 | いま |
| --- | --- |
| `RiskManagementService/Features/RiskManagement/RequestStageTransition/Endpoint.cs:41,53` | `RiskControlEndpoints.ActorOf(http)` を承認者に渡す |
| `RiskManagementService/Features/RiskManagement/RiskControlEndpoints.cs:128-129` | `http.User.Identity?.Name` が空なら **`"unknown"`** |
| `RiskManagementService/Domain/StageGate.cs:56` | 検査は `string.IsNullOrWhiteSpace(approval.ApprovedBy)` だけ ⇒ **`unknown` は通る** |
| `NotificationService/Infrastructure/ExternalServices/HttpStageGateController.cs` | 本文は `new { targetStage }` のみ（操作者を運んでいない） |
| `AuditService/Domain/AuditEntryFactory.cs:134-140` | 要約に `e.ApprovedBy` を生で出す（`unknown` もそのまま） |
| `Shared/AiStockTrading.Shared.Contracts/Events/StageTransitioned.cs` | `AuthorizedBy` に相当する欄が無い |

Bot の owner マップ機密クライアント（`client_credentials`・IADR-0062 決定 4 / IADR-0098）のトークンには**人が居ない**ため、
`/stage promote` で **FR-20 の実資金ゲートの承認記録が `unknown`（または `service-account-<clientId>`）で 7 年残る**。
kill switch・pause/resume・GFV 解除は理由欄に `actor=<利用者>` を併記しているが、**段階遷移の要求には理由欄が無い**。

## 射程

1. **Risk 側に代理の解決を置く**（`DelegatedActorResolver`・純関数）。IADR-0240 の `ConfirmingActorResolver` と
   同じ規律：本文の名前をそのまま信じず、**トークンの `azp` が構成 `RiskControls:DelegatedActor:TrustedClientIds`
   に一致するときだけ**採る。既定は空＝誰も信じない。
2. **段階遷移の要求へ `onBehalfOf` を足す**（末尾・任意）。`StageTransitioned` の末尾へ `AuthorizedBy`（任意・既定 null）を足す。
   台帳（`stage_transitions.ApprovedBy`）には**操作した利用者**が入る。
3. 🔴 **承認者を特定できない遷移を拒否する**（`unknown` を通さない）。`StageGate` の空検査だけでは通ってしまうため、
   **要求の入口で 400 に倒し、台帳を 1 行も書かず・イベントも出さない**。
4. **Bot が操作者を運ぶ**。`IStageGateController.RequestTransitionAsync` に `onBehalfOf` を**省略できない引数**として足し、
   `StageGateCommandHandler` が多層認証の結果（`AuthorizationResult.Actor`）を渡す。
5. **UserMapping の値域検証を Bot 起動時に行い、値域外を警告する**（#861 の監査が `'山田'` / `'dev owner'` の
   Rejected を実測）。Discord は唯一の窓口であり、値域外だと**恒常的に 400 になる**。
6. 監査要約・`/stage status` の履歴表示が、代理のときに**操作者と認可の主体の両方**を見せる。

### 射程外（この PR では触らない）

- **kill switch・pause/resume・GFV 解除の actor 欄**。#868 は「理由欄の併記で足りるか、構造化した欄へ揃えるかを決める」
  ことを求めており、**決定は IADR-0383 決定 5 に書く（＝理由欄の併記で足りる）**。コードは変えない。
- `stage_transitions` 表への `AuthorizedBy` 列の追加（**マイグレーションは行わない**）。IADR-0240 の先例（報告書）も
  永続は `Actor` だけで、認可の主体はイベントが運ぶ。台帳が持つべきは「誰が承認したか」であり、それは本 PR で正しくなる。
- 差し戻し（`/stage demote`）と昇格の**確認水準**（確認ボタンの有無）。変えない。
- 実 Keycloak のトークンで `azp` が届くことの実クラスタ検証（IADR-0240 と同じく単体テストはクレーム注入で代替。
  `docs/blocked-tasks.md` A-7a と同じ性質）。

## 🔴 母集合（走査したファイルと除外理由）

引いた語（追随の母集合を「誤りの側の文字列」で引く＝`traceability.repo.md` 規則 9）:

```
grep -rn "ActorOf(http)"                      backend --include=*.cs
grep -rn "ApprovedBy"                         backend frontend --include=*.cs --include=*.ts --include=*.tsx
grep -rn "StageTransitioned"                  backend --include=*.cs
grep -rn "RequestTransitionAsync"             backend --include=*.cs
grep -rn "onBehalfOf|OnBehalfOf|DelegatedActor|TrustedClientIds"  backend deploy docker-compose.yml
```

- **採る**: `RequestStageTransition/Endpoint.cs`（要求・発行）/ 新 `Features/RiskManagement/DelegatedActorResolver.cs` /
  `RiskManagementService/Program.cs`（構成の登録）/ `Shared.Contracts/Events/StageTransitioned.cs`（末尾追加）/
  `AuditService/Domain/AuditEntryFactory.cs`（要約）/ `NotificationService` の
  `IStageGateController.cs`・`HttpStageGateController.cs`・`OperateStageGate/StageGateCommandHandler.cs` /
  新 `NotificationService/Domain/DelegatedActorName.cs` / `Infrastructure/Steps/DiscordBotHostedService.cs`（起動時警告）/
  `docker-compose.yml` と `deploy/helm/.../values.yaml` `values-local.yaml`（信頼クライアントの注入）。
- **除外**: `ActorOf(http)` の他の 13 呼び出し（kill switch・pause・GFV・手仕舞い・設定変更・取り込み）——
  射程外の決定（IADR-0383 決定 5）。`RiskControlEndpoints.ActorOf` 自体も**変えない**（変えると 13 経路の actor の
  倒し方が黙って変わる）。
- **除外**: `frontend/`。画面からは段階遷移を承認しない（`grep -rn "stage-gate/transition" frontend` は 0 件）。
- **除外**: `stage_transitions` のマイグレーション（上記「射程外」）。
- **除外**: `ShortSellReleaseVerdictRideAlongTests` が固定する相乗り構造。**verdict も同じ入口を通る**ため
  承認者の解決は共通に効くが、構造（専用エンドポイントを作らない）は変えない。
- **導出値は走査ではなく計算し直した**: テスト ID の最大値（下記）。

## 受け入れ基準 → テストの写像

テスト ID は `docs/tests/FR-20_staged-gates-tests.md` の系列。**同書の最大は `T-128`**（走査で確認。
[#887](https://github.com/endazon/ai-stock-trading/issues/887) の重複採番を避けるため最大値の上から採る）。
本 PR は **T-129〜T-140** を使う。

| ID | 受け入れ基準 | 固定する内容 |
| --- | --- | --- |
| T-129 | Bot 経由の遷移で正しい承認者が残る | 信頼クライアントのトークン＋`onBehalfOf` ⇒ 台帳・イベントの `ApprovedBy` が利用者・`AuthorizedBy` がクライアント |
| T-130 | 同上（差し戻し） | `demote` でも同じ（安全方向でも承認者を残す） |
| T-131 | 🔴 なりすまし: 利用者トークン直叩き | `onBehalfOf` を**無視**（`ApprovedBy` はトークンの主体・`AuthorizedBy` は null）・警告ログ |
| T-132 | 🔴 なりすまし: 一覧外のクライアント | 同上 |
| T-133 | 🔴 なりすまし: `azp` の欠落 | 同上 |
| T-134 | 🔴 なりすまし: `azp` の大文字違い | 同上（`Ordinal` 比較。`AI-Stock-Trading-Owner` は一致しない） |
| T-135 | 🔴 信頼一覧の既定は空 | 未設定なら誰の `onBehalfOf` も信じない |
| T-136 | 🔴 値域外の `onBehalfOf` | 信頼クライアントが送ったら **400・台帳 0 行・イベント 0 通** |
| T-137 | 🔴 承認者を特定できない遷移 | 名前も `azp` も無いトークン ⇒ **400・台帳 0 行・イベント 0 通**（`unknown` を通さない） |
| T-138 | Bot が操作者を運ぶ | `StageGateCommandHandler` ⇒ `RequestTransitionAsync(target, actor)`・本文に `onBehalfOf` が載る |
| T-139 | UserMapping の値域外で起動時に警告 | 非 ASCII（`山田`）・空白入り（`dev owner`）・64 字超で Warning。正常値では出ない |
| T-140 | 契約の後方互換 | `StageTransitioned` の旧形式 JSON が `AuthorizedBy=null` で読める／位置引数の並びが変わらない |

## 否定形の 3 点セット（`docs/tests/README.md` §2）

- **境界値**: `onBehalfOf` の値域 1 字 / 64 字 / 65 字、空文字。
- **プロパティ**: 「代理が成立していないとき `AuthorizedBy` は常に null」「`Rejected` のときイベントは常に 0 通」。
- **否定形**: T-131〜T-137（迂回経路＝利用者トークン・一覧外・`azp` 欠落・大文字違い・一覧未設定・値域外・主体不明）。

## 実資金ゲートへの影響（🔴 必ず確認する）

**緩まない。** 本 PR が増やすのは拒否だけである。

- 承認者を特定できない遷移は**通らなくなる**（従来は `unknown` で通っていた）。
- 昇格の合格基準（`UnmetPromotionCriteria`）・確認ボタン・飛び級禁止・Stage 1 の警告は 1 バイトも変えない。
- `onBehalfOf` を**送れるのは owner クライアントの secret を持つ者だけ**であり、信頼一覧の既定は空。
  設定漏れは「代理が不成立＝従来どおりトークンの主体」に倒れ、**開放にはならない**。
