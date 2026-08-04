---
title: IADR-0062 Discord Bot は Gateway 常駐＋多層認証とし、既定 no-op・owner トークンで kill switch を呼ぶ
type: impl-adr
status: Accepted
related_ids:
  - FR-09
  - FR-14
  - UC-06
  - ADR-0003
  - IADR-0016
  - IADR-0020
  - IADR-0051
author: claude
created: 2026-07-17
updated: 2026-07-17
plan_refs:
  - "../../planning/projects/ai-stock-trading/06_technical/07_discord-bot-design.md (fixed・接続方式/認証・認可/二重実行防止)"
  - "../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md (FR-09 通知 / FR-14 対話)"
---

# IADR-0062: Discord Bot は Gateway 常駐＋多層認証とし、既定 no-op・owner トークンで kill switch を呼ぶ

- 状態: Accepted
- 日付: 2026-07-17
- 決定者: claude / endazon

## 起点・関連

- 計画書: **FR-09**（通知）／**FR-14**（Discord からの対話・kill switch 起動）／**UC-06**（統制操作）
- 詳細設計: `06_technical/07_discord-bot-design.md`（**fixed**）— 接続方式・認証認可・二重実行防止・コマンド体系
- 関連 IADR: **IADR-0020**（通知の安全既定 no-op）／**IADR-0051**（s2s 認証）／**IADR-0016**（実弾防止ゲート）
- Issue: [#15](https://github.com/endazon/ai-stock-trading/issues/15)

## コンテキストと課題

#15 のアウトバウンド通知（PR #65・IADR-0020）は完了済み。残るのは **FR-14 の双方向 Bot** である。

本 Bot は**発注機能を持つシステムへの操作窓口**であり、`/killswitch` は全取引の即時停止という
最大級の副作用を持つ。詳細設計07 が fixed で定めた要件のうち、実装時に決めるべき点は以下。

1. Gateway 常駐（WebSocket）をどう実装し、**CI を実 Discord 非依存で緑に保つ**か
2. 多層認証（サーバー/チャンネル固定・ユーザーID許可リスト・Keycloak マッピング・確認ステップ）の**失敗時の既定**
3. kill switch は Risk の HTTP を呼ぶが、当該エンドポイントは **`OwnerOnly`**（`trading-owner`）である。
   IADR-0051 の s2s トークン（`trading-service`）では **403** になる。Bot はどの資格情報で呼ぶか
4. 「版番号付き冪等確定」の機構をどこに置くか（#14 の報告書確定と交差させずに）

## 検討した選択肢

### 論点1: Gateway 接続の実装と CI 非依存化

| 案 | 概要 | 評価 |
| --- | --- | --- |
| **A（採用）** | `IDiscordBotGateway` ポートを Application に定義し、Discord.Net 実装は Worker アダプタに隔離。既定は `NullDiscordBotGateway`（接続しない） | 純粋コア（認証・解析・冪等）を実 Discord なしで全数テストでき、CI が外部依存しない。IADR-0020 と同型 |
| B | Discord.Net を Application から直接使う | Application が外部 SDK に汚染され、単体テストに実 Gateway かモック SDK が必要。詳細設計07 の「Bot はステートレスな薄いフロントエンド」と不整合 |
| C | Interactions Endpoint（Webhook 受信） | 詳細設計07 が**明示的に不採用**（受信ポートの外部公開が非機能要件に反する）。逸脱不可 |

### 論点3: kill switch を呼ぶ資格情報

| 案 | 概要 | 評価 |
| --- | --- | --- |
| A | Risk の kill switch を `OwnerOrService` へ緩和し `trading-service` で呼ぶ | **却下**。IADR-0051 の最小権限（サービスに書き込み権限を与えない）を破る。自動処理が全停止を操作可能になる。Risk 側改修も必要 |
| **B（採用）** | Bot 専用の Keycloak 機密クライアントに **`trading-owner` ロールをマップ**し、`Notifications:Discord:OwnerAuth` セクションの client_credentials で呼ぶ | 詳細設計07「操作は対応する利用者の権限で実行する」と一致。Risk 側無改修。監査ログ上も本人操作として残る |
| C | 利用者の refresh token を Bot に保持 | 長期資格情報の保管が増え、失効・ローテーションの運用が重い。Vault 前提でも過剰 |

## 決定

1. **接続は Gateway 常駐（案A）**。`IDiscordBotGateway` をポート化し、Discord.Net 実装
   （`DiscordNetBotGateway`）は Worker の Composable/Adapters に隔離する。**既定は接続しない no-op**
   （`Notifications:Discord:Bot:Enabled=true` ＋トークン等が揃った時のみ有効化）。IADR-0020 と同一方針。
2. **Intents は最小構成**とする。`Guilds` のみを宣言し、**MessageContent Intent は要求しない**
   （本 PR のスコープはスラッシュコマンドのみ。自然文リプライ中継＝#14 交差のため対象外）。
3. **多層認証は純関数 `DiscordCommandAuthorizer` に集約**し、詳細設計07 の層を上から順に評価する。
   **すべての層は「不許可」を既定とする（fail-safe）**:
   - **DM は無条件で拒否**（詳細設計07「DM は不使用」）
   - **未設定は拒否**: GuildId / ChannelId / 許可ユーザーID が**空なら全拒否**（空＝全許可にしない）
   - Keycloak マッピングに無い Discord ユーザーは拒否（actor を特定できない操作はさせない）
4. **kill switch は Bot 専用 owner クライアントの client_credentials で呼ぶ（案B）**。
   PlatformShim の `ClientCredentialsTokenProvider` / `ServiceAuthOptions` は `public` のため、
   **PlatformShim 無改修**で Bot 専用セクションから再利用する（`ServiceAuth` セクションとは別系統に保ち、
   サービス用トークンと owner 用トークンを取り違えない）。資格情報が欠ければトークン無し＝401 に倒す。
5. **高リスク操作は2段階＋確認フレーズ**。`/killswitch` は確認ボタン → **確認フレーズ一致**を必須とする
   （`KillSwitchConfirmation` 純関数・大文字小文字と前後空白のみ正規化し、それ以外は厳密一致）。
   **確認フレーズ未設定時は起動を拒否する**（誤爆防止の閂を設定漏れで外さない）。
6. **版番号付き冪等確定は純粋機構 `VersionedConfirmationGuard` として実装**する。
   `対象ID＋版番号` で `Accepted / AlreadyConfirmed / Stale` を返す楽観ロック。本 PR では機構と全数テストのみを
   提供し、**報告書ドラフトへの結線は #14（ReportReviewStateMachine）側で行う**（交差回避）。
   kill switch 自体の冪等性は「起動済みなら状態を返すのみ」で担保する（詳細設計07 と一致）。

## 理由

- **安全既定の一貫性**: 本リポは「設定不備で実弾・実送信を試みない」（IADR-0016 / IADR-0020）を貫いてきた。
  Bot は kill switch という更に強い副作用を持つため、**同じ既定（無効・拒否）を全層に適用**するのが一貫する。
- **最小権限の維持**: 論点3 案A は実装は最短だが、「サービスに書き込み権限を与えない」という IADR-0051 の
  中核を崩す。Bot は**利用者の代理**であり、サービスではない。案B はその意味論をトークンに正しく反映する。
- **CI と実基盤の分離**: 実 Discord Gateway は外部 SaaS への WebSocket であり CI で張れない。ポート境界を
  Application に引くことで、**判断ロジック（認証・解析・冪等）は全数テスト**でき、実接続は E2E/手動に分離できる。

## 結果

- 良い影響:
  - 多層認証・確認フレーズ・冪等機構が**実 Discord なしで全数テスト可能**（CI 緑を維持）。
  - Risk 側無改修で kill switch を本人権限で操作でき、監査ログに本人として残る。
  - 既定 no-op のため、本 PR のマージで**実挙動は一切変わらない**（設定投入が有効化の唯一の引き金）。
- 悪い影響・トレードオフ:
  - Bot 専用 owner クライアントは**実質的に利用者権限を持つ機密資格情報**であり、漏洩時の影響が大きい。
    Vault 管理・ローテーションを運用要件とする（詳細設計07 の Vault 方針を踏襲。本 PR では config 経由）。
  - MessageContent Intent を取らないため、自然文リプライによる質疑は**本 PR では不可**（#14 で再検討）。
  - 実 Gateway 接続・スラッシュコマンド登録の実挙動は本 PR では未検証（後続 E2E）。
- フォローアップ:
  - `/report show` / `/report approve` の結線（#14 交差・`VersionedConfirmationGuard` を利用）
  - `/status` / `/pause` / `/resume`（Risk に pause 相当のエンドポイントが無く、別途設計が要る）
  - 実 Discord Gateway E2E・Bot 常駐の死活監視（詳細設計07 の未決事項）
  - Bot トークン／owner client secret の Vault 化（#132 の秘匿受け口と同型）

## 関連

- Supersedes: なし
- Superseded by: なし
