---
title: 乖離の取り込みの窓口を Discord Bot にも置き、記録の内容（誰が・なぜ・観測値・取り込み前後の数量・操作者）を API 経由と揃える（#871）
type: spec
status: accepted
related_ids: [FR-10, FR-11, FR-14, UC-06, ADR-0003, ADR-0028, ADR-0041, IADR-0062, IADR-0079, IADR-0097, IADR-0182, IADR-0240, IADR-0350, IADR-0359, IADR-0383, IADR-0408, IADR-0423]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0041_out-of-system-trade-adoption-and-capital-baseline.md (決定 4: 取り込みの窓口は REST API と Discord Bot の両方。記録の内容は同じ)
  - planning:projects/ai-stock-trading/07_adr/ADR-0028_gfv-violation-clearing-and-reconciliation.md (決定 2: 人が調べて消す・誰が・いつ・なぜを監査へ)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 / FR-11 / FR-14)
---

# 仕様書: 乖離の取り込みを Discord Bot からも行えるようにする（#871）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-10（統制・取り込みは利用者のみ）、FR-11（監査台帳）、FR-14（Discord Bot の操作）
- ユースケース（UC）: UC-06（統制操作）
- 画面（SC）: なし（Discord の応答）
- 関連 ADR: **ADR-0041 決定 4**（窓口は owner の REST API と Discord Bot の両方。**コマンドの形は計画では定めない。記録の内容だけを揃える**）、
  ADR-0028 決定 2（人が調べて直す・監査へ残す）、ADR-0003（AI は統制を上書きできない＝取り込みは owner に限る）
- 関連 IADR: IADR-0350（取り込み API・拒否 8 種・OwnerOnly）、IADR-0383（`DelegatedActorResolver`・`onBehalfOf`・承認者不明の拒否）、
  IADR-0240 決定 11（`onBehalfOf` の作法の起点・#774）、IADR-0062（多層認証・owner マップ機密クライアント）、
  IADR-0097（確認フレーズのモーダル・モーダル ID の分離）、IADR-0182（GFV 解除の Discord 窓口＝最も近い同型）、
  IADR-0359（応答のメンション抑止）、IADR-0079（イベントの後方互換の追加）、IADR-0408 決定 3（送り手の本物の型による契約テスト）
- 新設 IADR: **IADR-0423**（本件の実装判断）
- 関連 issue: #871（本件）、#849（取り込み API の起点）、#868（`onBehalfOf` の段階遷移への適用）、#774（`onBehalfOf` の起点）

## 🔴 実測（コードで確認・`origin/develop` = `a592b34f`）

| 箇所 | いま |
| --- | --- |
| `RiskManagementService/Features/RiskManagement/AdoptPositionDrift/Endpoint.cs` | 操作者は `RiskControlEndpoints.ActorOf(http)`（トークンの名前 → 無ければ `unknown`）。本文は `symbol`・`market`・`reason` のみ。応答は**匿名型**（200 と 422 の両方） |
| 同上 | Bot の owner マップ機密クライアント（`client_credentials`）で呼ぶと、トークンに名前が無いため**操作者が `unknown` で台帳・イベント・監査に残る**（#868 と同じ構造） |
| `RiskManagementService/Features/RiskManagement/DelegatedActorResolver.cs` | 段階遷移（IADR-0383）で使用中。`azp` が信頼一覧に載るときだけ `onBehalfOf` を採る純関数。取り込みからは未使用 |
| `Shared.Contracts/Events/PositionDriftAdopted.cs` | `Actor`・`Reason`・前後の数量・観測時刻・`RealizedPnlRecorded` を持つ。`AuthorizedBy` に当たる欄は無い |
| `NotificationService/Domain/BotCommandParser.cs` | 取り込みのコマンドは無い（`/killswitch`・`/pause`・`/resume`・`/status`・`/stage`・`/gfv clear`・`/report` のみ） |
| `NotificationService/Features/Notifications/ClearGoodFaithViolations/GoodFaithViolationCommandHandler.cs` | 最も近い同型（台帳・記録を人が直す操作）。閂は「多層認証 → 解析 → 確認フレーズ → 理由必須 → Risk 呼び出し」。操作者は理由文へ `（Discord Bot 経由・actor=…）` を併記 |
| `NotificationService/Infrastructure/ExternalServices/DiscordNetBotGateway.cs` | `/gfv` はスラッシュコマンド → 確認ボタン（Danger）→ 理由＋確認フレーズのモーダル → ハンドラ。許可外にはボタンすら出さない |

## 決定（詳細は IADR-0423）

1. **コマンド**: `/drift adopt symbol:<銘柄コード> market:<japan|us>`。数量は受け取らない（API と同じく観測が決める）。
2. **確認**: kill switch・GFV 解除と同水準——スラッシュコマンド → 確認ボタン（Danger・銘柄と市場を CustomId に載せる）→
   **理由＋確認フレーズ（kill switch と同じ設定値）のモーダル** → ハンドラ。**モーダルを経ずに Risk を呼ぶ経路は無い。**
3. **閂の並び**（ハンドラ）: 多層認証 → コマンド解析（取り込み以外は拒否）→ 確認フレーズ → 理由必須 → Risk 呼び出し。
   いずれかで止まれば **Risk を呼ばない**（＝台帳は変わらない）。
4. **操作者**: 多層認証が解決した Keycloak 利用者名を**本文の `onBehalfOf`** で運ぶ（段階遷移・報告書確定と同じ作法）。
   Risk は `DelegatedActorResolver` で解決し、**台帳の `Actor`＝操作した利用者**、イベントの `AuthorizedBy`＝認可の主体（クライアント ID）。
   🔴 **理由文は加工しない**（GFV のように `actor=` を併記しない）——窓口で理由文が変わると、ADR-0041 決定 4 の
   「記録の内容は同じ」が崩れ、後から「どちらから取り込んだか」でしか読めない差になる。
5. **Risk 側の API**: 本文の**末尾**へ `onBehalfOf`（任意）を足す。利用者本人のトークンでは無視される（従来どおり本人が操作者）。
   値域外の代理指定・**操作者を特定できないトークン**は 400 で拒否し台帳を書かない（IADR-0383 決定 3 と同じ閂）。
   応答（200・422）は匿名型から**名前付きの型**へ置き換え（JSON は同じ）、200 に `actor`（記録した操作者）を**末尾追加**する。
6. **イベント**: `PositionDriftAdopted` の**末尾**へ `AuthorizedBy`（任意・既定 null）を足す（IADR-0079 の後方互換の追加）。
   監査要約は `（<操作者>・代理 <クライアント>）`、通知は `操作者 <操作者>・<クライアント> 経由` と書く。**台帳の表（`drift_adoptions`）は変えない**
   （マイグレーションなし。段階遷移・報告書と同じく「誰の資格で通ったか」はイベントが運ぶ）。
7. **拒否の理由**: Risk の 422 本文 `error`（何が足りないか・どうすれば通るかの文言）を**そのまま**利用者へ返す。
   400 の本文も返す。401/403 は owner 設定の確認を促す。**失敗を成功に見せない。**

## 受け入れ基準

1. Discord から取り込みが実行でき、監査台帳に API 経由と同じ内容（誰が・なぜ・観測値・取り込み前後の数量・操作者）が残る。
   - Risk: 信頼クライアントのトークン＋`onBehalfOf` で取り込むと、台帳の `Actor`・イベントの `Actor` は代理される利用者、`Reason` は
     送った理由文のまま、`AuthorizedBy` はクライアント ID（T-10-983）。
   - 通知: ハンドラは理由文を加工せず、`onBehalfOf` に多層認証の結果を渡す（T-10-988）。
2. 許可外の利用者は実行できない（多層認証の拒否で Risk を呼ばない。T-10-988）。利用者トークン直叩きで他人の名前を
   `onBehalfOf` に載せても採られない（T-10-984）。
3. 確認を経ずに台帳が変わらない（確認フレーズ不一致・未入力・未設定、理由の欠如で Risk を呼ばない。T-10-988）。
   Discord の実行経路はモーダル送信からだけハンドラへ入る（IADR-0423 に記録。Gateway は Discord.Net 依存のため単体テスト対象外）。
4. 拒否（観測が古い・乖離が無い・数量の増加など）の理由が利用者に分かる形で返る（Risk の 422 本文 `error` をそのまま表示。T-10-989）。
5. API 側（`POST /risk-controls/position-drift/adopt`・OwnerOnly）はそのまま残る（既存 T-10-461〜470・482・543〜545 が緑）。
6. 送り手（リスク管理）の応答を受け手（通知）が読む部分に、送り手の本物の型を送り手の JSON 設定（web 既定）で直列化した契約テストを足す
   （T-10-989）。送り手の本物の Program.cs が出す本文が応答型を web 既定で直列化したものと一致することを固定する（T-10-985）。

テスト ID: 割り当て範囲 **T-10-983〜T-10-989**（`git grep` で origin/* 全ブランチに未使用を確認済み）。FR-10 テスト仕様書へ新節で載せる。

| ID | 置き場 |
| --- | --- |
| T-10-983 | `RiskManagementService.Tests/.../AdoptPositionDrift/PositionDriftAdoptionDelegatedActorTests.cs` |
| T-10-984 | 同上（否定形: 値域外・特定不能・信頼外の代理指定） |
| T-10-985 | `RiskManagementService.Tests/.../ReadContractWireFormatTests.cs`（本物の Program.cs の本文） |
| T-10-986 | `Shared.Contracts.Tests/PositionDriftAdoptedContractTests.cs`・`AuditEntryFactoryTests`・`NotificationFormatterTests`（`AuthorizedBy` の後方互換と表示） |
| T-10-987 | `NotificationService.Tests/Domain/BotCommandParserTests.cs`（`/drift adopt` の解析） |
| T-10-988 | `NotificationService.Tests/.../AdoptPositionDrift/PositionDriftAdoptionCommandHandlerTests.cs`（閂） |
| T-10-989 | `NotificationService.Tests/.../RiskControlOperationReadContractTests.cs`（契約）＋ `HttpPositionDriftAdoptionControllerTests.cs`（本文・状態コード） |

## 母集合（規則 9〜11）

### 規則 9: 追随先（誤りの側の文字列で走査）

- **取り込みの操作者の出所**: `git grep -n "ActorOf(http)" -- backend` = 25 箇所。本件で変えるのは取り込み（`AdoptPositionDrift/Endpoint.cs`）の 1 箇所だけ。
  他の 24 箇所（kill switch・pause・GFV・設定変更など）は IADR-0383 決定 5（理由欄の併記で足りる・`ActorOf` は変えない）のとおり触らない。
- **`PositionDriftAdopted` の生成と購読**: `git grep -ln PositionDriftAdopted -- backend ':!*/Migrations/*'` = 22 ファイル。
  - 生成（`new PositionDriftAdopted(`）: 本番はリスク管理のサービス 1 箇所。テストは監査 3・通知 2・発注執行 1。**末尾の任意引数の追加なので変更不要**。
  - 購読: 監査（要約へ代理を出す＝変更）、通知（本文へ経由を出す＝変更）、発注執行（保護記録の追随。数量と銘柄だけ読む＝変更不要）。
  - 契約: `event-schemas.baseline.json`（`AuthorizedBy` を足して保護対象へ入れる。`ReportConfirmed` と同じ扱い）、`EventMessageTypeNameTests`（型名は不変＝変更不要）。
- **Bot のコマンドの列挙**: `git grep -n "GoodFaithViolationClear\|BotCommandKind\." -- backend/Services/NotificationService` の出現先
  （`BotCommand.cs`・`BotCommandParser.cs`・各ハンドラ・`DiscordSettingsAreReadOnlyTests`・`BotCommandParserTests`）。
  `DiscordSettingsAreReadOnlyTests` の「どのコントローラも呼ばれない」に新しいハンドラを加える（設定変更の試みが取り込みへ落ちないこと）。
  「参照のみの例外は kill switch と一時停止/再開だけ」のテストは**設定値の変更**の例外を固定するものであり、取り込み（台帳の是正。ADR-0041 決定 4 が
  射程外と確定）は GFV 解除・段階遷移・報告書確定と同じく同テストの列挙に入れない。
- **配線**: `Program.cs`（名前付き HttpClient とハンドラの登録）・`DiscordBotGatewayFactory.Create`・`DiscordNetBotGateway` のコンストラクタ。
  `CompositionWiringGuardTests` が配線の抜けを見る。
- **文書**: `git grep -ln "position-drift/adopt\|乖離の取り込み" -- docs .ai-context/adr` →
  `docs/functional/FR-10_risk-controls.md`（取り込みの節に Discord の窓口を足す）、`docs/api/events-and-ports.md`（`PositionDriftAdopted` の行に `AuthorizedBy`）、
  `docs/operations/operations.md`（保護注文の追随の障害対応。窓口に依らない＝変更不要）、IADR-0350・IADR-0360（凍結記録＝変えない）、
  IADR-0383 フォローアップ（「#871 が本 IADR の `DelegatedActorResolver` を使う」＝本件で事実になる。凍結記録のため本文は変えない）。
  `docs/blocked-tasks.md` A-7a の再測定手順（実 Discord の確認項目）に `/drift adopt` を足す。
- **除外**: `.ai-context/specs/`（凍結記録）、`frontend/`（SC-03 は取り込みの件数を読むだけで窓口に依らない）。

### 規則 10: この変更で新たに誤りになる自分の記述

走査語: `REST API のみ`・`窓口`・`OwnerOnly・理由必須`・`本文は \`symbol\``・`ActorOf`（`docs/`・`.ai-context/adr/`・`backend/**/*.cs` のコメント）。

- `docs/functional/FR-10_risk-controls.md` の「本文は `symbol`・`market`・`reason` のみ」: **誤りになる**（`onBehalfOf` が加わる）→ 書き換える。
- `AdoptPositionDrift/Endpoint.cs` 冒頭コメント（操作者の出所）と `RiskControlEndpoints.cs` の登録コメント: Discord 経由を書き足す。
- `BotCommandParser.cs`・`BotCommand.cs` 冒頭の「扱うのは …」の列挙: `/drift adopt` を足す。
- `DiscordNetBotGateway.cs` の「判断は KillSwitchCommandHandler に委ねる」等の列挙: 新しいハンドラを足す。
- IADR-0383 決定 5 の残余リスク「理由欄を持たない新しい統制操作を足すときは本 IADR 決定 1・3 の形を採ること」: 取り込みは理由欄を持つが
  決定 1・3 の形を採る。矛盾ではない（理由欄を**加工しない**ことを優先した。IADR-0423 に理由を書く）。凍結記録のため変えない。
- 導出値: テスト件数は「検証」で計算し直す。

### 規則 11: 窓（配備順の時間差）

変更はリスク管理（要求の `onBehalfOf`・応答の `actor`・イベントの `AuthorizedBy`）と通知（新コマンド）に跨る。
**増える側のプローブ**＝Discord 経由の取り込みで台帳に**利用者名**が残る件数。**減る側のプローブ**＝Discord 経由の取り込みで台帳に
`unknown`／`client:<azp>` が残る件数（記録の内容が API 経由と揃わない件数）。

| 形 | 窓の間の Discord 経由の取り込み | 増える側 | 減る側 |
| --- | --- | --- | --- |
| (a) リスク管理を先に配備 → 通知 | 旧 Bot には `/drift` が無い（実行できない） | 0（窓の後に正しく増える） | **0** |
| (b) 通知を先に配備 → リスク管理 | 新 Bot が `onBehalfOf` を送るが、旧リスク管理の要求型は未知の項目を読み飛ばす（System.Text.Json の既定）。操作者は `ActorOf`＝`unknown` | 0 | **窓の間の取り込み件数ぶん増える**（台帳・監査に `unknown`） |
| (c) 同時 | Pod の入れ替わり順に依る（(a)(b) の混在） | — | 0〜(b) と同じ |

**形は (a) を採る**（リスク管理 → 通知の順に配備する）。加えて、(b)(c) の窓が生じても**見えるように**する:
新 Bot は 200 応答に `actor` が無い（＝旧リスク管理）とき、成功の文面に「操作者が記録されたかを応答から確認できません（リスク管理が旧版の可能性）」を添える。
イベントの `AuthorizedBy` は旧購読者が読み飛ばし（増えも減りもしない）、新購読者は旧発行者の欠落を null と読む（T-10-986）。
（実測: 旧要求型 `PositionDriftAdoptionRequest(string? Symbol, Market? Market, string? Reason)` は `onBehalfOf` を持たず、
Risk の Program.cs は `UnmappedMemberHandling` を変えていない＝読み飛ばし。本件の T-10-989 の「応答に actor が無い」テストが新 Bot 側の表示を固定する。）

## 射程外（見送り）

- 確認ボタンを出す前に**現在の乖離（台帳と観測の数量）を照会して表示する**こと。取り込みの読み取り口（乖離の現況）が無く、Risk 側に新しい読み取り API が要る。
  確認文面に「数量は最新の観測で決まる・減らす乖離だけ」を明記し、結果の文面に前後の数量を出すことで代える（残余リスクとして IADR-0423 に書く）。
- kill switch・pause・GFV 解除の操作者欄の構造化（IADR-0383 決定 5 のまま）。
- 台帳（`drift_adoptions`）への `AuthorizedBy` 列の追加（マイグレーションなし）。
- 実 Discord での疎通確認（AI セッションでは行えない。`docs/blocked-tasks.md` A-7a の再測定手順へ項目を足す）。

## 検証

（2026-09-25・`origin/develop` a592b34f の上の作業ツリーで実行）

- `dotnet build backend/backend.slnx -v q`: 0 警告 0 エラー（初回の 1 回だけ共有ディスクの容量不足で 32 エラーになった。空きを戻した後の
  `--no-incremental` の再ビルドを含め、以後はすべて 0 警告 0 エラー）。
- `dotnet format backend/backend.slnx --verify-no-changes`: 差分なし（exit 0）。
- `dotnet test`（触ったサービスと共有契約）:
  - `RiskManagementService.Tests`: 1987/1987 合格（本件の新規 T-10-983〜985 を含む）
  - `NotificationService.Tests`: 668/668 合格（T-10-986 の通知分・T-10-987〜989・`DiscordSettingsAreReadOnlyTests` の拡張・`CompositionWiringGuardTests` を含む）
  - `AuditService.Tests`: 206/206 合格（T-10-986 の監査分）
  - `AiStockTrading.Shared.Contracts.Tests`: 473/473 合格（T-10-986 の後方互換・`EventBackwardCompatibilityTests`）
  - `OrderExecutionService.Tests`: 867/867 合格（`PositionDriftAdopted` の購読側）
  - `AiStockTrading.Architecture.Tests`: 173/173 合格（通知の Domain が共有契約の `Market` を参照する）
- 変異注入（1 つずつ入れて実行し、実行ごとに元へ戻した）:
  - リスク管理の取り込みの操作者を `DelegatedActorResolver` から従来の `ActorOf(http)` へ戻す: 取り込み系 28 件中 3 件赤
    （T-10-983 の 2 件・T-10-984 の信頼一覧未設定。Bot 経由の操作者が `unknown` に戻る＝本件の起点の事象）。
  - 通知のハンドラが理由文へ `（Discord Bot 経由・actor=…）` を併記する（GFV の形）: 36 件中 1 件赤（T-10-988 の「理由文は加工しない」）。
  - 通知のハンドラの確認フレーズの閂を外す: 36 件中 7 件赤（T-10-988 の確認フレーズ不一致・未設定の否定形）。
- 文書・検査器: `check-trace-blocks`・`check-doc-links`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-test-traceability`
  （テスト ID の重複の増加なし・採番の最大値 T-10-989）・`check-adr-index-addendum-loss`・`check-adr-index-sync --range=origin/develop..HEAD`・
  `check-commit-messages`・`check-reading-budget`・`gen-knowledge-graph --check`: いずれも exit 0（コミット後に再実行して確認）。
- **実 Discord での確認はしていない**（AI セッションではできない）。`docs/blocked-tasks.md` A-7a の再測定手順 ⑤ に手順を足した。
