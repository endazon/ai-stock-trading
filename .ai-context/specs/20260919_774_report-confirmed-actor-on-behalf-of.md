---
title: Discord 経由の報告書確定で確定者が unknown になる —— Bot が代理する利用者を本文で運び、報告書サービスが信頼クライアントに限って採る
type: spec
status: accepted
related_ids: [FR-09, FR-07, FR-14, UC-03, UC-04, UC-05, ADR-0003, IADR-0240, IADR-0062, IADR-0098, IADR-0134]
author: endazon (with Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-07「報告書は利用者の確定をもって有効になる」/ FR-09 通知 / FR-14「Discord からの操作」)
  - planning:projects/ai-stock-trading/06_technical/07_discord-bot-design.md (認証・認可「操作は対応する利用者の権限で実行する」)
  - planning:projects/microservices-platform/07_adr/ADR-0086_user-context-in-body-not-token-exchange.md (利用者文脈は本文で運ぶ・token exchange は採らない)
---

# 仕様書: Discord 経由の確定で確定者を正しく残す（#774）

## 起点

- #774（2026-09-11 04:36Z・稼働クラスタ）。Discord `/report action:approve` の結果通知が
  「（**unknown**・前提条件 v1）」と表示した。notification-service のログは `Actor=developer` を持っている。

## 原因（コードで確かめた）

1. Bot は owner マップ機密クライアント（`client_credentials`）のトークンで `POST /reports/{periodKey}/confirm` を呼ぶ
   （`HttpReportReviewController.ConfirmAsync`・IADR-0240 / IADR-0062 決定4 / IADR-0098）。本文は `{ expectedVersion }` だけ。
2. 報告書サービスの `ReportEndpoints.ActorOf(http)` は `http.User.Identity?.Name`（＝`preferred_username`）を採り、
   無ければ `"unknown"` に倒す。**機密クライアントのトークンは人を表さない**（名前クレームが在っても
   `service-account-<clientId>` であり、稼働環境では名前クレーム自体が無く `unknown` になった）。
3. その値が `ReportConfirmed.Actor` に載り、**通知本文（`NotificationFormatter`）と監査台帳（`AuditEntryFactory` の要約と
   ペイロード）の両方**へそのまま出る。**監査台帳も同じ問題を持っている**（通知だけの不具合ではない）。
4. Bot 側は多層認証で解決した利用者（`auth.Actor`＝UserMapping の Keycloak 利用者名）を持っているが、
   `IReportReviewController.ConfirmAsync` の引数に無く、報告書サービスへ渡っていない。

## 他の Bot 経由操作は操作者をどう残しているか（母集合・2026-09-19・origin/develop `747ff4ca`）

引き方: `grep -rn "ActorOf" backend --include=*.cs` と `NotificationService/Features/Notifications/*/` の全ハンドラ、
`Infrastructure/ExternalServices/Http*Controller.cs` の全アダプタ。

| 操作 | Bot → 権威への運び方 | 権威側の actor | 操作した Discord 利用者の残り方 |
| --- | --- | --- | --- |
| kill switch（起動・解除） | 本文 `reason = "Discord Bot 経由の操作（actor=<利用者>）"` | トークンの名前（同じく service account / unknown） | **理由欄（本文）に残る** |
| pause / resume | 同上 | 同上 | **理由欄に残る** |
| GFV 解除 | 本文 `reason = "<理由>（Discord Bot 経由・actor=<利用者>）"` | 同上 | **理由欄に残る** |
| 段階遷移（`/stage`） | 本文に理由欄が無い（`target` のみ） | 同上 | 🟡 **残らない**（本件の射程外。保留点として報告する） |
| 報告書の確定・差し戻し | 本文は版番号のみ | 同上 | 🔴 **残らない**（本件） |

既存の作法は **「認可の主体（トークンのクライアント）を actor に据えたまま、実際に操作した利用者を本文で運んで併記する」**
である（計画 MSP:ADR-0086 決定1「利用者文脈は本文で運ぶ」と同じ向き）。確定要求には理由欄が無いので、
**同じ作法を構造化した欄で行う**。差し戻し・提示の actor は永続化も発行もされない（`ReviewCommand.Actor` は
空でないことの検査にしか使われない——`grep -rn "command.Actor\|\.Actor" Services/ReportService` で確認）ため変更しない。

## どの層で解決するか

| 案 | 内容 | 採否 |
| --- | --- | --- |
| A | **Bot が確定要求の本文に操作者（`onBehalfOf`）を添え、報告書サービスが「信頼する機密クライアントのトークン」に限って採る** | **採用** |
| B | 報告書サービスがトークンのクレームから利用者を解決する | 却下。`client_credentials` のトークンに人は居ない。token exchange は計画 MSP:ADR-0086 決定2 が採らない（Keycloak 24 で preview） |
| C | 通知側で補う（Bot が確定した直後の `ReportConfirmed` に Bot の知る操作者を当てる） | 却下。**監査台帳が直らない**（監査は同じイベントを別に購読する）。Bot はステートレス前提（詳細設計07）で、相関をプロセス内に持つとレプリカ・再起動で外れ、**別人の確定に名前を当てる誤り**が起き得る |

## 決定

1. **要求の形**: `ConfirmReportRequest` の**末尾**へ `string? OnBehalfOf = null` を足す（任意・既存の呼び出しは無変更で通る）。
2. **誰の `OnBehalfOf` を信じるか**（なりすましの閉じ方）:
   - 報告書サービスは構成 `Reports:DelegatedActor:TrustedClientIds`（**既定は空＝誰も信じない**）を持つ。
   - トークンの **`azp`（authorized party）がこの一覧に一致するときだけ** `OnBehalfOf` を確定者として採る。
     Keycloak のアクセストークンは常に `azp` を持ち、`client_credentials` では当該クライアント ID になる。
   - **その値を送れるのは誰か**: OwnerOnly（`trading-owner`）を通り、かつ `azp` が owner マップ機密クライアントである
     トークンの持ち主＝**当該クライアントの secret を持つ者（Bot）だけ**。同クライアントは `standardFlowEnabled:false`・
     `directAccessGrantsEnabled:false`（`infra/keycloak/realm-export.json`・IADR-0098 決定1）で、**利用者トークンの `azp` には
     なり得ない**。利用者が SPA 経由で得るトークンは `azp=ai-stock-trading-dev` 等であり一覧に載らない。
   - **利用者トークン直叩きでは `OnBehalfOf` を無視する**（陰性対照）。確定は通し、確定者はトークンの本人のまま。
     無視したことは警告ログに残す（なりすましの試行が見えるように）。
   - 一覧が空（未設定）なら Bot からの `OnBehalfOf` も無視する（fail-safe。設定漏れで信頼を開かない）。
3. **値域**: `OnBehalfOf` は `\A[A-Za-z0-9._@+-]{1,64}\z`（IADR-0240 決定6 の 2026-09-19 追記に従い `\A…\z`）。
   信頼クライアントが値域外の値を送ったら **400**（確定しない）。**確定者を記録できない確定は行わない**
   （IADR-0062 決定3「actor を特定できない操作はさせない」と同じ向き）。Bot は失敗として利用者へ返し予約を解放する（既存の経路）。
4. **監査上の意味＝両方を残す**: `ReportConfirmed` の**末尾**へ `string? AuthorizedBy = null` を足す（IADR-0134 決定2 の
   「新設は常に末尾へ」と同じ規律）。
   - 代理確定: `Actor`＝実際に操作した利用者（`OnBehalfOf`）、`AuthorizedBy`＝認可の主体であるクライアント（`azp`）。
   - 利用者トークン直叩き: `Actor`＝`preferred_username`、`AuthorizedBy`＝null（従来どおり）。
   - 監査台帳はペイロード（全フィールド直列化）と要約の両方に出す。
5. **操作者が取れないとき**（信頼クライアントが `OnBehalfOf` を添えない＝旧版 Bot／名前クレームの無いトークン）:
   報告書サービスは `unknown` へ倒す前に **`client:<azp>`** を確定者とする（**誰の資格で確定されたかは分かる**）。
   `azp` も無い（Keycloak 以外のトークン）ときだけ従来の `unknown`。
   表示側（通知・監査要約）は、`Actor` が空または `unknown` のとき **「確定者不明」** と書く（内部の既定値を生で出さない）。
   旧版 Bot を 400 にしないのは、報告書サービスと通知サービスの配備順で**唯一の確定窓口が塞がる**のを避けるため。
6. **表示**: 代理確定は `（developer・ai-stock-trading-owner 経由・前提条件 v1）`。直叩きは従来どおり `（owner・前提条件 v1）`。
7. **配備**: 信頼クライアント ID は通知側 `Notifications__Discord__OwnerAuth__ClientId` と**同じ秘密鍵
   `ast-secrets/discord-owner-auth-client-id`** から `Reports__DelegatedActor__TrustedClientIds`（カンマ区切り。Bot 側
   `AllowedUserIds` と同じ書式）へ注入する
   （単一情報源。片方だけ変えて黙って `client:` 表示へ落ちるのを避ける）。helm `values.yaml` / `values-local.yaml`・
   `docker-compose.yml`。未設定（空）は fail-safe で無視。

## 影響範囲

- `backend/Shared/AiStockTrading.Shared.Contracts/Events/ReportConfirmed.cs`（末尾に任意フィールド）＋ `event-schemas.baseline.json`
- `backend/Services/ReportService/Features/Reports/ConfirmReport/`（`Endpoint.cs`・新規 `ConfirmingActorResolver.cs`）・`Program.cs`（構成の読み取り）
- `backend/Services/NotificationService`: `IReportReviewController`・`HttpReportReviewController`・`ReportCommandHandler`・`NotificationFormatter`
- `backend/Services/AuditService/Domain/AuditEntryFactory.cs`
- `deploy/helm/ai-stock-trading/values.yaml`・`values-local.yaml`・`docker-compose.yml`
- `docs/api/events-and-ports.md`（フィールド一覧）
- `.ai-context/adr/IADR-0240`（決定 11 の日付つき追記）・`IADR-0062`（「監査ログに本人として残る」への訂正注記）・`.ai-context/adr/README.md` 索引行 2 本
- `deploy/helm/ai-stock-trading/README.md`（秘密鍵の利用サービス欄）・`docs/data/reports.md`
- proto 契約（`scripts/check-proto-contracts.js`）は**触らない**（`ReportConfirmed` は C# レコードの JSON 契約で proto ではない）。

## 受け入れ基準（テストへの写像）

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | Bot 経由（信頼クライアント＋`OnBehalfOf`）の確定で `ReportConfirmed.Actor` が利用者、`AuthorizedBy` がクライアント | `ReportConfirmActorTests`（発行イベントを assert） |
| 2 | **なりすましの否定形**: 利用者トークン（`azp` が一覧外／無し）が `OnBehalfOf` を送っても無視され、`Actor` は本人 | `ReportConfirmActorTests`・`ConfirmingActorResolverTests` |
| 3 | 一覧外クライアントの `OnBehalfOf` は無視／一覧が空なら owner クライアントでも無視 | `ConfirmingActorResolverTests`・`ReportConfirmActorTests` |
| 4 | 値域外の `OnBehalfOf`（改行・空白・長すぎ）は信頼クライアントでも 400 で確定しない | `ReportConfirmActorTests`・`ConfirmingActorResolverTests` |
| 5 | 操作者が取れないとき `unknown` ではなく `client:<azp>` | `ConfirmingActorResolverTests`・`ReportConfirmActorTests` |
| 6 | Bot は多層認証で解決した `auth.Actor` を確定要求へ載せる（HTTP 本文に `onBehalfOf`） | `ReportCommandHandlerTests`・`HttpReportReviewControllerTests` |
| 7 | 通知本文: 代理確定は「利用者・クライアント 経由」、`unknown`／空は「確定者不明」 | `NotificationFormatterTests`（既存のゴールデン `NotificationTemplateGoldenTests` は無変更で通る＝直叩きの文言は不変） |
| 8 | 監査要約: 代理確定は両方が載る。ペイロードに `AuthorizedBy` が載る | `AuditEntryFactoryTests` |
| 9 | 旧形式のイベント（`AuthorizedBy` 無しの JSON）が読める（後方互換） | `EventBackwardCompatibilityTests`（baseline 再生成）・`ReportConfirmedContractTests` |

## 未検証・保留

- **Discord のメンション注入は塞げていない**（#861 の監査が実測）。`onBehalfOf` の値域は `@` を許しているため
  `@everyone` / `@here` が通り、Webhook 送信は `allowed_mentions` を指定していない。値を送れるのは owner
  クライアントの secret を持つ者だけなので権限昇格ではないが、送信側で塞ぐ（#867）。
- **`/stage`（段階遷移）が同型の問題を持つ**。承認者がクライアント主体のまま `StageTransitioned.ApprovedBy` と
  台帳に残る。FR-20 の実資金ゲートの承認記録であり、報告書の確定より監査上の重みが大きい（#868）。
- **UserMapping の値が値域外だと確定が恒常的に 400 になる**。Bot 側は起動時に値域を検証していない（#868 に併記）。

- 実 Keycloak のトークンで `azp` が届くことは単体テストではクレーム注入で代替しており、**実クラスタでは未検証**
  （kubectl 禁止の射程）。`JwtBearer` の受信クレーム写像が `azp` を改名しないことだけは
  `ConfirmingActorResolverTests.JwtBearer_の受信クレーム写像は_azp_を改名しない` で固定した。
- `/stage`（段階遷移）は理由欄が無く、承認者が同じくクライアント主体になる。**本件では直さない**（別 issue 相当）。
- kill switch / pause / GFV の actor 欄もクライアント主体のまま（操作者は理由欄に残る）。構造化は本件の射程外。
