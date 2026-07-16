---
title: IADR-0062 実 LLM 接続の実運用化は「既定オフの全量ログ」「タイムアウト構成化」「空既定の設定サーフェス」に限定し、s2s トークンとリトライは足さない
type: impl-adr
status: Accepted
related_ids:
  - FR-04
  - FR-11
  - ADR-0003
  - ADR-0010
  - IADR-0017
  - IADR-0039
  - IADR-0051
  - IADR-0055
author: claude
created: 2026-07-17
plan_refs:
  - "../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04: AI 売買判断 / FR-11: 判断根拠の記録)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md (LLM ゲートウェイ)"
---

# IADR-0062: 実 LLM 接続の実運用化のスコープと安全既定

- 状態: **Accepted**
- 日付: 2026-07-17
- 決定者: claude（実装・起案）

## 起点・関連

- 関連 ID: FR-04（AI 判断）／FR-11（判断根拠の記録）／ADR-0010（LLM ゲートウェイ）／IADR-0017（安全既定）／
  IADR-0039（多数決・二段）／IADR-0055（費用計測）
- Issue: [#11](https://github.com/endazon/ai-stock-trading/issues/11) 残スコープ「実 LLM 接続の実運用化」

## 文脈

`HttpLlmCompletionClient`（#125）で `POST {LlmGateway:BaseUrl}/complete` を呼ぶ経路は既に存在し、
`LlmGateway:BaseUrl` 未設定なら `PlaceholderLlmCompletionClient`（常に Hold）へ倒す安全既定も入っている。
一方で **(a)** 受け入れ基準の「プロンプト・入出力・根拠が全量ログに残る」が未充足、**(b)** タイムアウトが
ハードコード、**(c)** どの設定サーフェスにも `LlmGateway__BaseUrl` の口が無く**コード上は対応済みなのに
実結線が到達不能**、という 3 点が残っていた。

## 決定

### 決定1: 全量ログは「既定オフの明示ゲート」（`LlmGateway:LogPrompts`）とする

プロンプト本文と LLM の生出力を記録し、FR-11 の受け入れ基準を満たす。ただし**既定は記録しない**。

- **理由（既定オフ）**: プロンプトには保有ポジション・資金残枠・方針といった機微が載る。ログ基盤は
  取引ドメインより広い読者に流れるため、既定で全量を流すのは最小権限に反する。必要な運用者が明示的に
  有効化する形にする。
- **理由（Information で出す・Debug に落とさない）**: FR-11 の目的は「後から根拠を再構成できること」であり、
  既定オフのゲートで露出を制御している以上、有効化した運用でログ水準の都合により拾えなくなる方が有害。
- **不変条件**: 既定オフ時の挙動は現行と完全に等価（Hold へ倒す警告ログを含む）＝回帰なし。

### 決定2: タイムアウトを構成化する（`LlmGateway:TimeoutSeconds`・既定 30）

実運用ではモデル・プロンプト長で適正値が変わる。未設定・不正・非正値は**既定 30 秒＝現行値**へ倒す
（fail-safe。不正値でタイムアウト無限や 0 秒にはしない）。

### 決定3: s2s サービストークンを**足さない**

`reports` / `risk` の HttpClient は `AddAiStockTradingServiceToken`（IADR-0051）を付けているが、`llm` には**付けない**。

- **根拠**: 実基盤 `LlmGateway.Api` の `CompletionEndpoints.cs` の `/complete` に `RequireAuthorization()` は無く、
  platform 共有基盤に `FallbackPolicy` も定義されていない（＝匿名エンドポイント）。トークンは検証されないため、
  付けても認可は 1 ミリも強くならず、無意味な結合と資格情報の伝播先だけが増える。
- **再評価の条件**: MSP 側が `/complete` に認可を導入した時点で本決定を見直す（その時は IADR-0051 の
  横断ハンドラを `llm` にも適用するだけで済む）。

### 決定4: 呼び出し側リトライを**足さない**

- **根拠**: ADR-0010 が「ゲートウェイで…送信可否判定・モデル選択・**リトライ**・トークン計測・監査ログを
  一元化する」と定める。AST 側で重ねると ADR 違反であり、**LLM 呼び出しの費用が多重計上**され、
  同期クリティカルパス（取引判断）の遅延も倍化する。失敗は現行どおり Hold（取引しない）へ倒せば安全側に閉じる。

### 決定5: 設定サーフェスは空既定で口だけ開ける（base appsettings には置かない）

- helm values / docker-compose / `.env.example` / appsettings.Development に `LlmGateway__BaseUrl` ほかを
  **空既定**で追加する。空＝Placeholder（実 LLM を呼ばない）＝既存の fail-safe を維持し、
  **本 PR で実キー・実 URL は投入しない**。
- **base `appsettings.json` には置かない**。IADR-0048 決定1（base は挙動中立）に従い、fail-safe 選択キーは
  `appsettings.Development.json` と環境変数に置く（env=Testing のテストが安全既定のまま走ることを保つ）。

### 決定6: `ANTHROPIC_API_KEY` の死んだ注入は「誤解を招くコメントの是正」に留め、除去は後続とする

trade-decision には `ANTHROPIC_API_KEY` が helm values / docker-compose / `.env.example` から注入されているが、
**コードはこの変数を一切読まない**。ADR-0010 の設計ではプロバイダ鍵は MSP ゲートウェイ側の `Llm:ApiKey` が
保持し、**AST は鍵を持たない**。したがって本来は除去が正しい。

しかし除去には `scripts/validate-runtime-scaffold.js`（`SECRET_ENV_KEYS` に本キーを必須列挙しており、
`.env.example` から消すと**検査が失敗する**）と `scripts/k8s-local-deploy.sh`（`ast-secrets` の
`anthropic-api-key` を生成）の改修が要り、本スライスのスコープ（trade-decision と設定サーフェス）を越えて
共有 CI スクリプトへ波及する。#11 の残スコープの達成に必須でもない。

そこで本 PR では、**「実 LLM は未実装（#79）／空=呼ばない」という現状に反するコメントの是正**に留める
（実 LLM は `LlmGateway:BaseUrl` 経由であり、本キーは AST では未使用であることを明示する）。
誤ったコメントを残す方が「AST に鍵を置けばよい」という誤った規範を将来の実装者へ与えるため、
除去より先にまず**事実の明示**を優先する。除去自体は後続 issue とする。

## 影響

- 変更は `backend/Services/TradeDecisionService/**` と設定サーフェスに閉じる。`Shared.Contracts`・
  リスク管理・報告書・設定管理・`TradingDefaults` は不変更。新規イベントは足さない（監査 Consumer の追加も不要）。
- 既定挙動は不変（`LlmGateway:BaseUrl` 未設定＝Hold・ログ既定オフ・タイムアウト 30 秒）。

## 検討した代替案

- **常時全量ログ**: FR-11 は満たすが機微が既定で流れる。→ 不採用（決定1）。
- **s2s トークン付与**: 一見「他の同期照会と揃って綺麗」だが、検証されないトークンを配るだけ。→ 不採用（決定3）。
- **呼び出し側リトライ（Polly 等）**: 一過性障害に強くなるが ADR-0010 の一元化に反し費用が多重化。→ 不採用（決定4）。
- **`ANTHROPIC_API_KEY` を AST で読んで直接 Anthropic を呼ぶ**: ゲートウェイの越境統制（送信可否判定・監査）を
  迂回する。→ 不採用（ADR-0010 違反）。
- **本 PR で `ANTHROPIC_API_KEY` を除去する**: 正しいが共有 CI スクリプトへ波及する。→ 後続（決定6）。

## 未解決・後続

- **`ANTHROPIC_API_KEY` の死んだ注入の除去**（決定6）: helm values / docker-compose / `.env.example` から除去し、
  併せて `scripts/validate-runtime-scaffold.js` の `SECRET_ENV_KEYS` と `scripts/k8s-local-deploy.sh` の
  `anthropic-api-key` 生成を整理する。
- 実クラスタでの実 LLM 応答の実証（要 MSP 側 Anthropic 鍵・#22 デプロイ配線）→ E2E/後続。
- RAG 文脈（#18）・実データ供給（#14/#12/#13）・監査永続（#17）・費用統制の実値（#23/#79）。
