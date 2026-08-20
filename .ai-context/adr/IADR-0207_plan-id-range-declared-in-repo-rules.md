---
title: IADR-0207 FR/UC/SC の実在集合は本リポの規約ファイルへ宣言し、planning 走査を採らない
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0206]
author: claude (Claude Code)
created: 2026-08-18
updated: 2026-08-18
plan_refs:
  - planning:tools/impl-handoff-kit/repo-template/scripts/check-commit-messages.js
---

# IADR-0207: FR/UC/SC の実在集合は本リポの規約ファイルへ宣言し、planning 走査を採らない

- 状態: Accepted
- 日付: 2026-08-18
- 決定者: claude（起票 #532。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: NFR（無採番。検査器整備のメタ作業。`.claude/rules/traceability.md`「無採番 NFR を許す 2 つの場合」の場合 2）
- 関連する実装仕様書: [`docs/specs/20260818_532_read-plan-ids-extension-point.md`](../specs/20260818_532_read-plan-ids-extension-point.md)

## コンテキストと課題

キット配布物 `check-commit-messages.js` は、コミット件名・PR タイトルのスコープ ID の**実在性**を検査するために `check-test-traceability.js` の拡張点 `readPlanIds()` を呼ぶ。本リポは #530 で検査本体を移植したが拡張点が未実装で、FR / UC / SC は **notice 付き skip** のままだった。この間 `feat(SC-99)` のような実在しない起点 ID が exit 0 で受理され、スカッシュ後の恒久履歴へ載る（force push 禁止で事後修正できない）。

実在集合をどこから得るかが決定点である。`ADR` / `IADR` はファイルの有無で解決できるが、FR / UC / SC は計画リポにしか実体が無い。

## 検討した選択肢

1. **本リポの追跡ファイル（`.claude/rules/traceability.repo.md`）へレンジを宣言し、そこを読む**（採用。MSP#579 と同型）
2. planning submodule を走査して実在 ID を集める — `ci.yml` の `commit-messages` ジョブは checkout に `submodules` 指定が無く、submodule を取得しない（実測）。走査すると実在集合が空になり、キット版 `loadExistingPlanIds()` が `new Set(readPlanIds())` へ潰すため **全 ID が違反**になる。`null` を返して skip させても #530 以前と同じ無検査に戻る。**却下**
3. CI 側に submodule 取得を足して 2 を成立させる — 件名検査のためだけに全ジョブの取得コストを増やし、かつ**取得失敗が「静かに検査ゼロ」へ落ちる**経路を残す。**却下**

## 決定

1. 実在集合の単一情報源を **`.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」節**とする。レンジはバッククォート囲みの `FR-01..21` / `UC-01..07` / `SC-01..03` 形式で宣言し、走査基準の planning pin を併記する。
2. `scripts/check-test-traceability.js` に `RULES_FILE` / `PLAN_RANGE_HEADING` / `PLAN_KINDS` と `planRangeSection` / `parsePlanRanges` / `expandPlanIds` / `readPlanIds` を実装し export する（キットが探す拡張点の形）。
3. **fail-loud にする。** 規約ファイルが読めない・節が無い・種別が欠ける・範囲が不正のいずれも**例外**を投げる。**黙って 0 件検査へ落ちない。** 環境差（submodule 未取得等）ではなく**追跡下の規約ファイルの破壊**であるため、skip は誤りである。
4. `SC-13` / `SC-16` は実在集合へ入れない。計画 `05_screens/01_screens.md` に現れるが、いずれも**基盤（microservices-platform）の画面を明示的に参照する地の文**であり、本リポの名前空間の画面ではない。
5. レンジ宣言の更新は人手とする（走査基準 pin を節に明記して鮮度を可視化する）。

## 理由

拡張点の目的は「実在しない ID を恒久履歴へ載せない」ことであり、**検査が実効していることの確実性**が最優先である。選択肢 2・3 は実行環境（submodule の取得状態）に検査の有無が依存し、CI が緑でも検査が走っていない状態を作る。追跡下のファイルは CI でも手元でも必ず読めるため、読めないことを異常として扱える（決定 3 の fail-loud が成立する）。

MSP が #579 で同じ設計を先行実装しており、本決定で**キット拡張点の実装が両実装リポで同型に揃う**（運用ガイド §11 パリティ）。

## 残余リスク

- **宣言は人手更新である。** 計画側で FR / UC / SC が増えたとき宣言を更新しないと、新 ID を使う PR が「実在しない」と判定されて落ちる。**落ち方は fail であって黙る形ではない**ため、更新漏れは検知でき、対処は宣言の更新のみである。
- 規約節の書式（バッククォート囲みのレンジ表記・見出し文字列）が機械契約になった。整形の都合で崩すと例外で落ちる（決定 3 のとおり、これは意図した挙動である）。節そのものへ「機械の単一情報源である」旨を明記して自己文書化した。
