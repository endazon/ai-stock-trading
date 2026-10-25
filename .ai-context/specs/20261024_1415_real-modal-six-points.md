---
title: REAL 警告モーダルの試験を計画の 6 点に揃える（古い「4 点」の名前と記述を直し、E2E に足りない点を足す）
type: spec
status: done
related_ids: [SC-02, FR-20, FR-10, UC-06, ADR-0040, IADR-0141, IADR-0161, IADR-0422]
author: claude (Claude Code)
created: 2026-10-24
updated: 2026-10-24
---

# 仕様書: REAL 警告モーダルを計画の 6 点に揃える（#1415 / E-23）

> 本仕様書は実装着手前に作成した（AST `origin/develop` `0be76dce`＝#1467 のマージ後）。

## 起点（トレーサビリティ）

- 起点: #1415（親 #1410 の網羅表 A-036）。
- 計画: SC-02「`moomoo REAL`（実弾）への切替時の警告モーダル」の **6 点**（planning `origin/main` で読んだ）。導入文は 2026-09-25 に「4 点」から
  「6 点」へ訂正されている（同書 変更履歴 2026-09-25）。**planning#816（closed）の裁定**: 実弾切替の警告モーダルの 6 点は改めない
  （実装は 6 点を満たす。⑥はモーダルの手前の関門である。同書 変更履歴 2026-10-22）。したがって計画は改まらず、**実装側の古い記述を直す**。
- ADR-0040 決定 1（⑥）。IADR-0141（明示確認）・IADR-0161 決定 4（⑤の文言）・IADR-0422（⑥の画面の関門）。新しい IADR は起こさない。

## 6 点と、それを固定している試験（是正後）

| 点 | 内容 | E2E（Playwright） | Vitest | サーバ |
| --- | --- | --- | --- | --- |
| ① | 実資金で執行される旨 | `broker-provider.spec.ts`「6 点のうち ①〜⑤」 | 「6 点のうち ①〜④」 | — |
| ② | 切替先と Stage の組み合わせ・段階ゲートを飛ばす旨 | 同上（**組み合わせの表示を足した**）／Stage 2 では警告を出さない（**新規**） | 同上 | — |
| ③ | equity と統制値の実額 | 同上（**4 行の値を足した**。従来は表の存在だけ）／取得できなければ切替できない（既存） | 同上 | — |
| ④ | 同意と「REAL」の入力 | 同上・確認操作の否定形（既存） | 既存 | IADR-0141 の 400 |
| ⑤ | 段階が実弾を既定とするまで発注されない旨 | 同上／Stage 2 では出さない（**新規**） | 既存 3 本 | — |
| ⑥ | 損切りの実行機構が S0 以外ならモーダルへ進ませない | `sc02-risk-settings.spec.ts`（#1412 で足した。既存） | `RiskSettingsPage.stopLossMethod.test.tsx`（既存） | 400（既存） |

E2E に足りなかったのは ②の組み合わせの表示・③の値・⑤と②の否定形（Stage 2）の 3 点だけである。⑥は #1412 で E2E に入っている（重複して足さない）。

## 是正の母集合（規則 9）

`grep -rn "4 点" frontend/src frontend/e2e docs .ai-context/adr` から、実弾切替のモーダルを指すものだけを拾った:

| 箇所 | 扱い |
| --- | --- |
| `frontend/e2e/broker-provider.spec.ts:83`（テスト名） | 直した |
| `RiskSettingsPage.brokerProvider.test.tsx:9 / 215 / 216 / 229`（冒頭・テスト名・注記） | 直した |
| `RiskSettingsPage.tsx:801 / 1032`（コメント） | 6 点を列挙する形へ直した（振る舞いは変えない） |
| `docs/screens/20260718_SC-02_risk-settings.md:112` | 6 点へ直し、訂正の日付を残した |
| `docs/screens/20260718_SC-02_risk-settings.md:325` | **直さない**: 2026-08-06 時点の記録（「#340 における本画面の扱い」）であり、当時の計画は 4 点だった |
| `docs/tests/FR-20_staged-gates-tests.md:158`（T-44） | 6 点へ直した |
| `.ai-context/adr/IADR-0141` | **直さない**: 凍結記録（2026-08-05 時点の 4 点） |

他の「4 点」（FR-15・IADR-0156 など）は別の事柄であり対象外。

## 検証（手元）

- `frontend/`: typecheck / lint / vitest / e2e:typecheck / Playwright 全件。
- 変異: ⑤の条件（`skipsStageGate`）を常に真にすると Stage 2 の試験が赤、③の 1 日の上限に 1 注文の値を出すと 6 点の試験が赤。
- 文書系: trace ブロック・リンク・計画 ID の修飾。
