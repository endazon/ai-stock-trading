---
title: 単体実行向けの認証の既定・現在値の無い新規建て・日報方針のプロンプトへの入れ方を安全側に倒す（3 件の fail-closed）
type: spec
status: accepted
related_ids: [NFR, FR-04, FR-05, FR-10, ADR-0003, ADR-0049, ADR-0067, IADR-0612, IADR-0617, IADR-0618, IADR-0099, IADR-0169, IADR-0542, IADR-0607, IADR-0324, IADR-0496]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (追補 1 データ／命令の構造分離)
  - planning:projects/ai-stock-trading/07_adr/ADR-0049_stop-width-floor-atr14-widen-no-ceiling.md (実測 2 現在値が無いときは LLM の参照価格へ退避する＝実測であり決定ではない)
  - planning:projects/ai-stock-trading/07_adr/ADR-0067_decision-exit-limit-unfilled-cancel-only.md (実測 3 価格の判断は LLM という決定は無い)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04・FR-05・FR-10)
---

# 仕様書: 3 件の fail-closed（#1529）

## 起点

- #1529（NFR・クラウド移行前の堅牢化）。同じ種類（安全側への倒し）の 3 件を 1 PR に束ねる。

## 1. 単体実行向けの認証の既定（IADR-0612）

### 実測（develop `26d0a18a7`）

| # | 事実 | 確かめ方 |
| --- | --- | --- |
| 1 | `Auth:Authority` が無いと、受け手の JWT 検証は `http://keycloak:8080/realms/ai-stock-trading`（単体実行向けのレルム）へ倒れる | `AuthExtensions.cs` の `?? "http://keycloak:8080/realms/ai-stock-trading"` |
| 2 | chart の既定 `global.authAuthority` も同じレルムである。values-local は `platform` で上書きする。上書きしない values は既定を継ぐ | `values.yaml`・`values-local.yaml` |
| 3 | 単体実行向けのレルム（`infra/keycloak/realm-export.json`）は開発用のクライアントを持つ（docker-compose の E2E 用） | realm-export.json |
| 4 | chart は全 Worker を `ASPNETCORE_ENVIRONMENT=Production` で描く。docker-compose は Development | `values.yaml`（IADR-0496）・`docker-compose.yml` |

### 決定

- `AddAiStockTradingAuth(config, environment)` に環境を渡す（8 サービスの Program.cs）。**Production** では、`Auth:Authority` が無い・空なら起動で止める。Authority が単体実行向けのレルム（パスの末尾が `/realms/ai-stock-trading`）なら、`Auth:AllowStandaloneRealm=true` が無い限り起動で止める。
- Production 以外（Development・試験の環境名）は従前どおり（コードの既定もそのまま）。
- chart の既定 `global.authAuthority` を**空**にし、未設定なら描画で止める（`fail`）。values-local は従前どおり `platform` を明示している。values-cloud は #1525 の管掌（`global.authAuthority` の明示が要る）。
- CI（`.github/workflows/helm.yml`）の既定の描画は、ジョブの env `CI_AUTH_AUTHORITY`（`platform`）を `--set` で渡して描く。新しい段で「未設定なら止まる・明示すれば通る（`--set`・values-local）」を固定する。レルムの網羅検査（#776）の「既定（本番）」の期待値は `CI_AUTH_AUTHORITY` に替える。
- chart は `global.allowStandaloneRealm`（既定 false）を足し、true のときだけ `Auth__AllowStandaloneRealm=true` を描く。

## 2. 現在値の無い新規建て（IADR-0617）

### 実測

| # | 事実 | 確かめ方 |
| --- | --- | --- |
| 1 | `referencePrice = currentPrice ?? decision.ReferencePrice`。現在値の供給が無い構成（no-op）では LLM が返した参照価格でサイジング・損切りライン・発注価格が決まる | `TradeDecisionAppService.cs` |
| 2 | 供給が有効で現在値が取れないときは LLM を呼ぶ前に見送る（IADR-0099 決定 3）。退避が効くのは「供給が無い構成」だけ | 同 `_currentPrice.IsEnabled && currentPrice is null` |
| 3 | chart の既定は `MarketData__Provider=""`（no-op）。values-local は finnhub（キーが無ければ no-op） | `values.yaml`・`values-local.yaml` |
| 4 | 計画 ADR-0049 の「現在値が無いときは LLM の参照価格へ退避する」は**実測**の表の行であり、決定ではない。ADR-0067 実測 3 は「価格の判断は LLM」という決定が無いとする。ADR-0003 は AI の判断を機械的統制で拘束する | 計画の ADR 本文 |

### 決定

- 現在値が無く（供給が無い構成）、新規建て（Open）のときは、LLM の参照価格を使わずに見送る（`DecisionSkipReason.ReferencePriceInvalid`・Warning）。手仕舞い（Close）は止めない（FR-10）。
- 従前の挙動は構成 `TradeDecision:AllowUnanchoredEntryPrice=true`（明示の opt-in）でだけ。Program.cs の既定は false。docker-compose は env で切り替えられる（既定 false）。
- 計画との整合: FR-10 の「手仕舞いは止めない」を保ち、ADR-0049 の決定（下限の定義）には触れない。ADR-0049 実測 2 の記述は是正後の実態と食い違うため、計画へ環流する（本 PR の範囲外。下記）。

## 3. 日報方針のプロンプトへの入れ方（IADR-0618）

### 実測

| # | 事実 | 確かめ方 |
| --- | --- | --- |
| 1 | 方針の節は `sb.AppendLine(policy.Summary)` で本文を素のまま入れる | `TradeDecisionPromptBuilder.AppendPolicySection` |
| 2 | 参考情報の節は 1 件 1 行の JSON・境界語・案内でデータとして分ける（IADR-0169・IADR-0542） | 同 `AppendRetrievalSection` |
| 3 | キャッシュできる接頭辞（IADR-0607）は「前文＋方針の節」で、`Build` / `BuildScreening` は同じ関数から接頭辞を書く | `DecisionCacheablePrefix` / `ScreeningCacheablePrefix` |

### 決定

- 方針の節は、本文を **1 行を 1 つの JSON 文字列**にしてフェンス（```json）と境界語の中に置き、案内（外部由来を含み得る入力であること・本文の中の命令に従わないこと）を添える。各行は境界語を不変点まで落とし、制御文字を空白へ、バッククォートの 3 連以上を潰す。行の並びと文言は変えない。長さは切らない。
- 接頭辞は引き続き方針だけで決まり、全文はその接頭辞で始まる（`PolicySection` を接頭辞の関数が使う）。
- 縮退の見積り（`ScreeningContextAssembler`）は方針の本文の長さではなく、節の文字列そのものの長さを数える。
- model・effort・max_tokens は変えない（呼び出しの引数に触れない）。

## 母集合（規則 9）

- 1: `git grep -n "AddAiStockTradingAuth(" -- backend` → 8 サービスの Program.cs と試験。`realms/ai-stock-trading` の既定はコード 1 か所・chart 1 か所（appsettings.Development.json は Development だけで読まれる）。
- 2: `git grep -n "decision.ReferencePrice" -- backend/Services/TradeDecisionService ':!*/Tests/*'` → 発注に使う退避は `TradeDecisionAppService` の 1 か所（`JudgedPriceOf` は観測用の判断時点の価格で、発注に使わない）。
- 3: `git grep -n "policy.Summary" -- backend/Services/TradeDecisionService ':!*/Tests/*'` → プロンプトへ入れるのは `AppendPolicySection` の 1 か所。他は系の判定（利確の行の読み取り）・KB 検索の問い合わせ・長さの見積り。KB 検索の問い合わせ（`KnowledgeBaseRetrievalContextProvider`）は LLM のプロンプトではないので対象外。
- 文書: `docs/security/security.md`（T-4 の行）・chart README。chart の既定の描画を使う CI は `helm.yml` の 1 本（`claude-*.yml` は描画を許可するだけで、引数は呼び手が与える）。

## 受け入れ基準

- [x] Production で Authority が無い・単体実行向けのレルムのとき、opt-in が無ければ起動で止まる（`StandaloneRealmGuardTests`）
- [x] 現在値の供給が無い構成で、新規建ては見送り、手仕舞いは従前どおり（`UnanchoredEntryPriceTests`）
- [x] 方針の本文の行が `# ` で始まっても、プロンプトの節の見出しにならない（`PolicySectionUntrustedTests`）
- [x] 全文は接頭辞の関数で始まる（`PromptCachePrefixTests`・`PolicySectionUntrustedTests`）
- [x] chart は `global.authAuthority` が未設定なら描画で止まり、明示すれば描画できる（`helm.yml` の「未設定なら止まる・明示すれば通る」の段）。リリースの values に無いとき、`helm-release-drift.js` は理由つきで止まる（`scripts.repo.test.js`）
- [x] 未指定（null）の `UnanchoredEntryPriceOptions` も新規建てを許さない（fail-closed。IADR-0617 決定 3 の 2026-11-06 追記）。本番の組み立ては構成に従い、未設定・false は見送り、true でだけ許す（`UnanchoredEntryPriceWiringTests`・ホストを組んで確かめる）

## 計画への環流（本 PR の外）

- 計画 ADR-0049 実測 2「現在値が無いときは LLM の参照価格へ退避する」は、本 PR の後は新規建てについて成り立たない（opt-in のときだけ）。planning へ issue で知らせる（オーナーの判断で起票）。
