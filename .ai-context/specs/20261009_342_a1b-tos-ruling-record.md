---
title: A-1b（Hetzner 上の OpenD 常駐の規約適合）の利用者裁定を運用文書へ記録する
type: spec
status: accepted
related_ids: [ADR-0019, ADR-0006]
author: claude (Claude Code)
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md
---

# 仕様書: A-1b の利用者裁定の記録（#342）

## 起点となる計画書（トレーサビリティ）

- 計画 ADR-0019（PoC と Hetzner 確認の順序。A-1b は同 ADR の未決事項）
- 計画 ADR-0006（常駐先の候補。シンガポールを候補に挙げる）
- 起点 issue: #342（moomoo PoC。A-1b はクローズ条件の筆頭）

## 目的・背景

2026-10-09、利用者の指示で Hetzner の約款・Service Agreement と moomoo証券（日本法人）の API 利用規約を調査した（調査結果は #397 のコメント・#342 に要約）。結果を受けた**利用者裁定は「抵触しないと判断し進める」**。運用文書（`docs/`）は A-1b を未着手・最優先として扱っており、裁定と食い違う。

## 変更内容

1. `docs/blocked-tasks.md`
   - A-1b の見出しに裁定済みを示し、表へ「判断」「推奨（拠点はシンガポール）」「残り」の 3 行を足す。
   - A-1 の「最後に測った時点」を 2026-10-09 へ更新する。
   - 監査表（A-1 行）と優先順位表 1 位を、残る A-1a（契約と接続可否の実測）へ差し替える。
   - 「最終更新」へ本変更を追記する。
2. `docs/operations/operations.md` 前提条件 #7: ToS は判断済み・接続可否は未実測、と状態を分けて書く（全体は 🔴 未充足のまま）。
3. `docs/operations/live-trading-cutover-runbook.md` 解禁前チェックリスト #5: 確かめ方を「ToS は判断済み・残るのは接続確認」へ改める。

## 母集合の取り方（規則 9）

`grep -rn "A-1b\|ToS" docs README.md` で走査した。上記 3 ファイル以外のヒットは無い。`blocked-tasks.md` の 2026-09-03 の分離経緯（:46）・2026-08-05 の訂正ブロック（:246）は日付つきの過去の記録であり書き換えない。

## 受け入れ基準

- A-1b を「未着手」とする記述が `docs/` に残らない。
- 裁定の条件（固定 IP から直接接続し VPN／プロキシを挟まない）が判断の行に明記されている。
- A-1a（接続可否）は未実測のまま残り、前提条件 #7 は 🔴 未充足のまま。
- `node scripts/check-trace-blocks.js` ほか CI の文書検査が通る。

## 範囲外

- Hetzner の契約・接続可否の実測（A-1a。利用者の契約後）。
- 日本法人への書面照会（任意。A-8 の照会と 1 通にまとめられる）。
