---
title: 実 LLM 接続の実運用化スライス（全量ログ・タイムアウト構成化・設定サーフェス）— Issue #11
type: spec
status: review
related_ids:
  - FR-04
  - FR-11
  - UC-01
  - UC-02
  - ADR-0003
  - ADR-0010
  - IADR-0017
  - IADR-0039
  - IADR-0055
  - IADR-0062
author: claude
created: 2026-07-17
plan_refs:
  - "../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04: AI 売買判断 / FR-11: 判断根拠の記録)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md (LLM ゲートウェイ: 送信可否・モデル選択・リトライ・トークン計測の一元化)"
related_specs:
  - "20260714_79_llm-egress.md（#125・HttpLlmCompletionClient の実装。本スライスの前提）"
  - "20260715_79_llm-cost-metering-impl.md（#79・費用計測。本スライスは計測点を変えない）"
  - "../adr/IADR-0062_llm-production-wiring.md（本スライスの設計判断）"
---

# 仕様書: 実 LLM 接続の実運用化スライス（Issue #11）

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-04**（生成 AI は方針とリスク制約の範囲内でのみ売買判断を行う）／**FR-11**（判断根拠の記録）
- ユースケース: **UC-01 / UC-02**
- 計画 ADR: **ADR-0003**（方針階層＋独立リスク管理）／**ADR-0010**（platform LLM ゲートウェイ）
- 実装 ADR: **IADR-0062**（本スライス）／IADR-0017（安全既定）／IADR-0039（多数決・二段）／IADR-0055（費用計測）
- Issue: [#11](https://github.com/endazon/ai-stock-trading/issues/11) の残スコープ「実 LLM 接続の実運用化」

## 目的・背景

#11 の中核（多数決・二段判断・PositionSizer 結線・費用計測）は PR #62/#96/#136 で実装済みであり、
`HttpLlmCompletionClient`（#125）で `POST {LlmGateway:BaseUrl}/complete` を呼ぶ経路も存在する。
本スライスは、その経路を**実運用へ載せるために足りない 3 点**に限定して埋める。

**現状の穴**:

1. **受け入れ基準「プロンプト・入出力・根拠が全量ログに残る」が未充足**。`TradeDecisionService` は
   action / rationale / 票数のみを記録し、**LLM へ送ったプロンプト本文と、LLM が返した生の出力を残していない**。
   実 LLM で誤判断が出たとき、入力と出力が無ければ事後に原因を再構成できない（FR-11 の目的が満たせない）。
2. **タイムアウトが 30 秒ハードコード**（`Program.cs`）。実運用のモデル・プロンプト長により適正値が変わる。
3. **設定サーフェスに `LlmGateway:*` の口が無い**。helm values / docker-compose / `.env.example` のいずれにも
   `LlmGateway__BaseUrl` が存在せず、**実結線しようにも設定する手段が無い**（コード上は対応済みなのに到達不能）。

## 調査で棄却した項目（実装しない・根拠）

推測で作り込まないため、着手前に実基盤のコードと ADR で検証し、次の 2 案を**棄却した**。

- **s2s サービストークンの伝播は行わない**。`reports` / `risk` の HttpClient と異なり `llm` には
  `AddAiStockTradingServiceToken` が無く当初はギャップと疑ったが、実基盤 `LlmGateway.Api` の
  `CompletionEndpoints.cs` の `/complete` に `RequireAuthorization()` は無く、platform 共有基盤に
  `FallbackPolicy` の定義も無い（＝匿名エンドポイント）。トークン付与は不要な作り込みになる。
- **呼び出し側リトライは行わない**。ADR-0010 が「ゲートウェイで機密区分に応じた送信可否判定・モデル選択・
  **リトライ**・トークン計測・監査ログを一元化する」と定めており、AST 側の二重リトライは ADR 違反かつ
  費用と遅延の二重化になる。

## 対象範囲

**対象（本 PR）**

- **全量ログ**（FR-11）: プロンプト本文と LLM 生出力を記録する。プロンプトは長大かつ保有ポジション・資金等の
  機微を含むため、**既定オフ**の明示ゲート（`LlmGateway:LogPrompts`）とする（IADR-0062 決定1）。
- **タイムアウトの構成化**: `LlmGateway:TimeoutSeconds`（既定 30＝現行値。不正・非正値は既定へ倒す）。
- **設定サーフェス**（PR 末尾の単一コミット）: helm values / docker-compose / `.env.example` /
  appsettings.Development に `LlmGateway__BaseUrl` ほかの口を空既定で開ける。**実キー・実 URL は投入しない**。
  base `appsettings.json` には置かない（IADR-0048 決定1 の挙動中立を保つ）。
- **死んだ秘密注入 `ANTHROPIC_API_KEY` の除去**（IADR-0062 決定6・ユーザー判断で当初の deferral を撤回）:
  コードが同変数を一切読まず ADR-0010（鍵は MSP ゲートウェイ側が保持し AST は鍵を持たない）と矛盾するため、
  注入している全箇所（helm values / docker-compose / `.env.example` / `scripts/k8s-local-deploy.sh` /
  関連ドキュメント）から除去する。併せて波及する共有 CI 配管（`scripts/validate-runtime-scaffold.js` の
  `SECRET_ENV_KEYS`）を整合させる。**GitHub Actions 用の同名シークレットは用途が別なので触らない**。

**対象外（後続）**

- **RAG 文脈（#18）**: ナレッジベース連携は本 PR の対象外（交差回避）。
- 実クラスタでの実 LLM 応答の実証（要 MSP 側 Anthropic 鍵・#22 デプロイ配線）→ **E2E / 後続**。
- 実データ供給（#14/#12/#13）・監査永続（#17）・費用統制の実値（#23/#79）。
- `Shared.Contracts` / RiskManagementService / ReportService / ConfigurationService / `TradingDefaults` は不変更。

## 設計

- `HttpLlmCompletionClient` に `logPrompts` を渡し、**送信前にプロンプト・受信後に生出力**を記録する。
  既定オフ時も**現行の警告ログ（Hold へ倒した理由）は不変**＝回帰なし。
- ログ本文は Information で出す（FR-11 の記録が目的であり、既定オフのゲートで露出を制御するため
  Debug に落として運用で拾えなくなる方が有害）。
- タイムアウトは `Program.cs` の `AddHttpClient("llm")` で構成から読む。

## 受け入れ基準（テストへの写像）

- [x] `LogPrompts=true` でプロンプト本文が記録される（FR-11）
- [x] `LogPrompts=true` で LLM 生出力が記録される（FR-11）
- [x] `LogPrompts` 既定（未設定）ではプロンプト・生出力を記録しない（機微の既定露出を避ける）
- [x] `LogPrompts=true` でも非 2xx / 送信拒否 / タイムアウトは Hold に倒れる（IADR-0017 の安全既定が不変）
- [x] **`LlmGateway:LogPrompts` の構成キーが Program.cs の配線を通って全量ログの有無を切り替える**
      （end-to-end。キー名のタイプミス・既定値の反転を検出する）
- [x] `LlmGateway:TimeoutSeconds` が未設定・不正・非正値のとき既定 30 秒に倒れる
- [x] `LlmGateway:BaseUrl` 未設定は Placeholder（実 LLM を呼ばない）＝**既存の選択テストが不変で緑**
- [ ] 実クラスタでの実 LLM 応答（要 MSP 側鍵）→ **本 PR 対象外・E2E/#22 後続**

## 検証

- `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` が緑。
- `dotnet format` を通す。
- 実 LLM 依存テストは作らない（CI は fake handler で完結。実基盤依存は上記のとおり後続へ明示分離）。
