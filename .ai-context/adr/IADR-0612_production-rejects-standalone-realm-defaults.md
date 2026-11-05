---
title: IADR-0612 chart の既定の Authority を空にして未設定なら描画で止め、Production では単体実行向けのレルム（ai-stock-trading）を指すなら明示の opt-in が無い限り起動で止める
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0011, IADR-0324, IADR-0496, IADR-0098]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
---

# IADR-0612: 単体実行向けの認証の既定を Production で使わせない

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-11-05
- 決定者: Claude Code（実装）／ オーケストレーター（単体実行向けの設定が本番の構成へ漏れないよう fail-closed にする、の指示）

## 起点・関連

- issue: #1529（3 件の 1 つ）
- 前提: IADR-0324（認証レルムを MSP レルムへ統一。values-local）・IADR-0496（chart は Production で描く）・IADR-0098（開発用の client secret を止める）。
- 実装仕様書: [`.ai-context/specs/20261105_1529_fail-closed-auth-price-policy.md`](../specs/20261105_1529_fail-closed-auth-price-policy.md) §1

## コンテキストと課題

受け手の JWT 検証は、`Auth:Authority` が無いと単体実行向けのレルムへ倒れる。chart の既定 `global.authAuthority` も同じレルムで、上書きしない values はそれを継ぐ。単体実行向けのレルムは docker-compose の E2E 用に開発用のクライアントを持つ。本番の構成で上書きを忘れても、静かに動いてしまう。

## 検討した選択肢

1. **Production でだけ、Authority の明示と単体実行向けのレルムの拒否を起動時に確かめる（採用）** — docker-compose（Development）と試験の環境名は変わらない。
2. **chart の既定の `global.authAuthority` を空にし、未設定なら描画で止める（採用・2026-11-05 の監査の指摘で 1 に併せる）** — 描画の時点で気付ける（起動まで待たない）。既定の描画を使う CI の検査は、配備のレルムを明示して描くよう改める。
3. コードの既定を消すだけ — chart の既定が同じレルムを渡すので塞がらない。採らない。

## 決定

1. `AddAiStockTradingAuth(config, environment)`。environment が Production のとき `StandaloneRealmGuard.Validate` が確かめる: Authority が無い・空 → 例外。パスの末尾が `/realms/ai-stock-trading`（大小文字・末尾の / を問わない）→ `Auth:AllowStandaloneRealm=true` が無ければ例外。
2. 認証を登録する 8 サービスの Program.cs は `builder.Environment` を渡す（試験が走査して固定する）。
3. chart は `global.allowStandaloneRealm`（既定 false）を足し、true のときだけ `Auth__AllowStandaloneRealm=true` を描く。
4. chart の既定 `global.authAuthority` は空。未設定なら `templates/deployment.yaml` の先頭で描画を止める。CI の既定の描画はジョブの env `CI_AUTH_AUTHORITY` を `--set` で渡し、「未設定なら止まる・明示すれば通る」を段で固定する。

## 結果

- values-local（platform）は変わらない。Authority を明示しない構成は描画で止まり、単体実行向けのレルムを明示した Production の構成は起動で止まる（fail-closed）。
- `RequireHttpsMetadata=false` は変えない（クラスタ内の Keycloak は http）。

## 残余

1. 認証を持たない発信者（trade-decision・notification・backtest）の token エンドポイントは `global.authAuthority` から導く。受け手が上の確かめで単体実行向けのレルムを拒むため、そのレルムのトークンは受け手で通らない（発信者の側では止めていない）。
2. values-cloud（別のセッション）は `global.authAuthority` を配備のレルムへ明示する必要がある。
