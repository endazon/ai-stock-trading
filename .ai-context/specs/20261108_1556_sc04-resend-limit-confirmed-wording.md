---
title: 作業仕様書 — SC-04 の SMS 再送の制限の注記・コメントを「確定値」へ直す（#1556）
type: spec
status: done
related_ids: [SC-04, FR-09, UC-06, IADR-0579, IADR-0322]
author: claude
created: 2026-11-08
updated: 2026-11-08
---

# 作業仕様書 — SC-04 の再送の制限の記述を裁定へ合わせる（#1556）

> 着手 2026-11-08（develop 起点）。注記・コメント・画面の文言だけを変える。値（サービス全体 60 秒に 5 件・再送だけ 60 秒に 1 回）と挙動は変えない。新しい判断は無いため IADR は要らない。

## 起点

- planning#826 の利用者裁定（2026-11-06）で、SC-04 の再送の制限の 2 つの値を、実測を待たずに暫定値のまま確定値とした。算定根拠は無く、実測は将来の任意の確認に回った。計画への反映は planning#872。
- 実装の注記・コメント・画面の文言は「暫定値・実測待ち」のままだった（#1556）。

## 受け入れ基準

- AC1: 母集合のすべての「暫定値・実測待ち」の記述を、確定値（算定根拠は無い・利用者裁定 2026-11-06）へ直す。値は変えない。
- AC2: 画面の文言を変えたので、i18n のカタログ（`messages.po`・`messages.ts`）を `npm run i18n` で作り直す。
- AC3: SC-04 の単体試験（Vitest）が通る。文言を見る試験（Vitest・E2E）があれば合わせる。
- AC4: `docs/` 配下の表示テキストには計画の issue 参照を書かない（trace ブロック規約）。`updated:` を前進させる。

## 母集合（規則 9・10）

`git grep -n -E "暫定|実測待ち"` を、SC-04 の画面・BFF・認証サイドカー・ロケール・`docs/` に掛けて引き直した。

| 箇所 | 扱い |
| --- | --- |
| `backend/Bff/AiStockTrading.Bff.Endpoints/OpendAuthBffEndpoints.cs:86` | 直した |
| `backend/Bff/AiStockTrading.Bff.Endpoints/OpendAuthUpstream.cs:115` | 直した |
| `backend/Services/OpendAuthGateway/Common/OpendAuthOptions.cs:59` | 直した |
| `backend/Services/OpendAuthGateway/Features/OpendAuth/ResendIntervalLimiter.cs:17` | 直した |
| `frontend/src/features/sc04-opend-auth/api/opendAuthQueries.ts:57` | 直した |
| `frontend/src/features/sc04-opend-auth/types/index.ts:137` | 直した |
| `frontend/src/features/sc04-opend-auth/components/VerificationCodeForm.tsx:113` | 直した（画面の文言） |
| `frontend/src/locales/ja/messages.po`・`messages.ts` | `npm run i18n` で作り直した |
| `docs/screens/20260909_SC-04_opend-auth.md:78` | 直した（issue の表に無かった追加分。`docs/` なので計画の issue 参照は書かない） |

除外:

- `frontend/src/features/sc04-opend-auth/components/OpendAuthPage.tsx:147`（と対応するカタログの行）の「暫定手段」は、コマンドの実行権限についての別の話題なので対象外とした。
- `.ai-context/` の凍結記録は書き換えない。
- 文言を見る試験は、`git grep` で該当が無かった（Vitest・E2E とも）。

## 検証

- `npx vitest run src/features/sc04-opend-auth`: 3 files・27 tests passed。
- 文書の検査（check-trace-blocks・gen-knowledge-graph --check・check-doc-links・check-commit-messages・check-cross-repo-refs・check-plan-id-qualification）。
