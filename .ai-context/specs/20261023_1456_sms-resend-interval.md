---
title: SC-04 の SMS 再送だけに 60 秒に 1 回の間隔をサイドカーで課し、サービス全体の 5 件 / 60 秒の内側に重ねる。拒否は残り秒数つきで画面まで届ける
type: spec
status: done
related_ids: [SC-04, FR-09, UC-06, NFR, IADR-0579, IADR-0322, IADR-0321, IADR-0566]
author: claude (Claude Code)
created: 2026-10-23
updated: 2026-10-23
---

# 仕様書: SMS 再送だけの間隔（60 秒に 1 回）

> 本仕様書は実装着手前に作成した（着手 2026-10-23。AST `origin/develop` `56150d7b`）。

## 起点（トレーサビリティ）

- 起点: #1456（追跡元 planning#826。裁定 planning#823・planning#825＝利用者裁定 2026-10-22 の案 C）。
- 計画: 05_screens §SC-04 主要素「SMS の再送要求」の 2 つの制限の表（①サービス全体のレート制限 60 秒に 5 件・
  ②再送だけの間隔 60 秒に 1 回）。完了記録 `10_feedback/20261022_sc04-resend-rate-limit.md`。計画 `origin/main` で確認。
- IADR: 新規 **IADR-0579**（置き場と拒否の形）。前提（覆さない）: IADR-0322 決定 4（①）・IADR-0321（BFF）・IADR-0566（呼び出し元の検証）。
- 計画との矛盾: なし。計画は「置き場は実装が IADR で決める」とした。

## 作業の範囲

1. サイドカー `backend/Services/OpendAuthGateway/`:
   - `Features/OpendAuth/ResendIntervalLimiter.cs`（新規）: 再送だけを数える間隔。①（`SubmissionRateLimiter`）を内側で呼び、
     **② の判定 → ① の取得 → ② の記録**を 1 つの lock の内で行う（並行する 2 本の再送が両方通らない）。
     ② で拒否したときは ① の枠を消費しない。① で拒否したときは ② を記録しない。
   - `SubmissionRateLimiter.CurrentWait()`（新規・消費しない覗き見）: ② の拒否で残り秒数を `max(②, ①)` にするため。
   - `PostResend` は `ResendIntervalLimiter` を通す。拒否は ① と同じ形（429・`Retry-After`・符号は ② が `resend_interval`、① が `rate_limited`）。
   - `OpendAuthOptions.ResendIntervalSeconds`（既定 60）。chart `opend.authGateway.resendIntervalSeconds` → `OpendAuth__ResendIntervalSeconds`。
   - 検証（`/verify`）は ② を通らない（① だけ）。
2. BFF `backend/Bff/AiStockTrading.Bff.Endpoints/`: 上流の 429 を、これまでの 503（`gateway_unavailable`）ではなく
   **409・`{"reason":"rate_limited","retryAfterSeconds":N}`・`Retry-After: N`** へ写す（投入・再送とも）。
   409 にするのは、基盤の `apiFetch` が本文を残すのが 400 / 409 だけだからである（基盤は無改修）。
3. 画面 `frontend/src/features/sc04-opend-auth/`: 409 の本文が `rate_limited` なら残り秒数を `role="alert"` で示す
   （再送「あと N 秒待ってから再送できます」・送信「上限に達した。N 秒後に」）。`ApiError` のスタブへ基盤と同じ `body` を足す。
4. 注記: BFF・画面・型の「暫定 60 秒に 1 回・実測待ち」は値が暫定である点で残す。「サーバ側が課す」は本変更で事実になる。

**範囲外**: ①② の実測（PoC セッションが再認証の機会に行う）。SC-04 の E2E（#1413 が未着手で `frontend/e2e/` に SC-04 の土台が無い。作らない）。
画面の再送ボタンを間隔のあいだ押せなくすること（計画は求めていない。サーバ側の拒否と残り秒数の表示で足りる）。

## 受け入れ基準と試験の写像

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| 1 | 再送を 60 秒以内に 2 回送ると 2 回目が拒否され、書かない | `ResendIntervalLimiterTests`・`OpendAuthEndpointsTests.再送は60秒以内の2回目を429で拒み書かない` |
| 2 | 窓の境界: 59 秒は拒否、60 秒は許可。`Retry-After` は残り秒数（切り上げ） | 同 `窓の境界`（Theory）・`Retry-After` の値 |
| 3 | 検証は ② の対象外（再送の直後でも投入は通る） | `検証は再送の間隔に数えない` |
| 4 | ① の合算は残る（投入 + 再送で 5 件 / 60 秒。② の拒否は ① を消費しない／① の拒否は ② を記録しない） | `ResendIntervalLimiterTests` の 2 件・`再送と投入は合算で上限に数える` |
| 5 | ② と ① が両方塞ぐときの残り秒数は長いほう | `両方が塞ぐときは長いほうの残りを返す` |
| 6 | BFF は上流の 429 を 409・`rate_limited`・残り秒数へ写す | `OpendAuthBffTests.上流のレート制限は残り秒数つきの409へ写す` |
| 7 | 画面は残り秒数を示す | `OpendAuthPage.test.tsx` の 2 件 |
| 8 | 変異で赤になる | 間隔の比較 `>=` → `>`、記録の順序（① の拒否でも記録）、BFF の写像を手で壊して赤を確かめる |

## 母集合（規則 9・10）

走査語: `60 秒に 1 回`・`レート制限`・`429`・`rate_limited`・`RateLimit`・`Retry`（`backend/` `frontend/src` `deploy/` `docs/`。`.ai-context/specs/`・`CHANGELOG.md` を除く）。

| # | 箇所 | 扱い |
| --- | --- | --- |
| 1 | `OpendAuthEndpoints.cs`（`PostResend` の doc「流量制限は投入と同じ枠を使う」） | ② を足した旨へ改める（① の合算は残る） |
| 2 | `OpendAuthOptions.cs`・`Program.cs` | 構成と登録を足す |
| 3 | `OpendAuthBffEndpoints.cs:85`・`OpendAuthUpstream.cs:115`（「レート制限は上流が課す（暫定 60 秒に 1 回）」） | 事実になる。① と ② を分けて書き、429 の写像を記す |
| 4 | `VerificationCodeForm.tsx`（「間隔はサーバ側が課します」）・`opendAuthQueries.ts:57`・`types/index.ts:137` | 文言は事実になる。`opendAuthQueries.ts` の注記に拒否の形を足す |
| 5 | `deploy/helm/.../values.yaml`・`templates/opend.yaml`（`rateLimit*`） | `resendIntervalSeconds` を足す |
| 6 | `OpendAuthBffTests.cs` `再送は本文を持たず上流の409を透過する`（「レート制限は上流が課す（暫定 60 秒に 1 回）。BFF はその 409 を透過する」） | 上流は 409 ではなく 429 を返す。注記を「入力待ちでない等の 409」へ直し、429 は新試験で押さえる |
| 7 | `OpendAuthEndpointsTests.窓あたりの上限を超えたら429で書かない`（再送を 3 連続） | ② に掛かるため、投入で ① を埋める形へ改める |
| 8 | `docs/` 配下の SC-04 の記述 | 下記の実測で決める |

除外: `.ai-context/specs/`（凍結）・`.ai-context/adr/IADR-0322`（凍結。② は新 IADR へ）・`CHANGELOG.md`（生成物）。
