---
title: /policy の監視銘柄の入れ替え案を確認ボタンで適用する（案の銘柄だけ・楽観排他・SC-02 と同じ検証・内訳の報告）（#1025）
type: spec
status: accepted
related_ids: [FR-13, FR-14, FR-07, FR-03, UC-03, UC-06, SC-02, ADR-0042, ADR-0031, ADR-0003, IADR-0433, IADR-0431, IADR-0432, IADR-0088, IADR-0294, IADR-0420]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0042_discord-apply-ai-watchlist-proposal-and-revision-limit.md (決定 1・2)
  - planning:projects/ai-stock-trading/07_adr/ADR-0031_finnhub-rate-limit-minute-confirmed-daily-open.md (決定 2・3)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-13・FR-14)
---

# 仕様書: `/policy` の監視銘柄の入れ替え案を確認ボタンで適用する（#1025）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-13（監視銘柄の変更・理由必須・監査・楽観排他）、FR-14（Discord の窓口。例外を 1 つ足す）、FR-07
- ユースケース（UC）: UC-03（`/policy`）、UC-06（設定の変更）
- 画面（SC）: SC-02（同じ検証・同じ変更履歴）
- 関連 ADR: ADR-0042 決定 1・2（適用の条件・例外はこの 1 つ）、ADR-0031 決定 2・3（**利用者裁定 2026-09-26: 警告のみ**）、ADR-0003
- 関連 IADR: IADR-0433（本件）、IADR-0431 決定 4（改める）、IADR-0432（試行の台帳）、IADR-0088（監視銘柄 API）、IADR-0294（見積り）

## 利用者裁定（2026-09-26）

ADR-0031 の条件は選択肢 (b): 既存の Finnhub の見積りを**警告のみ**に留め、追加を拒否しない。確認の応答に
「推定 N 回/日（暫定上限 300 回/日を超過・警告のみ）」を出す。300 回/日の未実測の前提の見直しは計画側の issue で扱う。

## 母集合（規則 1〜6・9）

- FR-14 の例外を固定する箇所: `git grep -n "参照のみ\|表示のみ\|監視銘柄は変わ" -- backend docs` →
  `DiscordSettingsAreReadOnlyTests`（改める）、`BotCommandParser` の注記、`DiscordNetBotGateway` の `/policy` の説明、`IPolicyRevisionController`・
  `PolicyRevisionCommandHandler` の注記、報告書の本文の追記（`AppendRevisionRecord`）、`PolicyRevisionMessage` の注記（すべて改めた）。
- 監視銘柄の変更の経路: `git grep -n "MonitorWatchlistService\|/monitor/watchlist"` → SC-02（`WatchlistForm`）・市場監視の追加／削除。
  新しい口は `MonitorWatchlistService` に足し、検証の規則（重複・不在）を共有する。
- 配備: `git grep -n "DelegatedActor__TrustedClientIds\|Reports__BaseUrl" -- deploy` → values.yaml・values-local.yaml（notification・market-monitor に足す）、
  helm README の秘密鍵の表（読むサービスに market-monitor を足す）。
- 切替の保全表: 新しい列だけで表は増えない（#1024 で `policy_revision_attempts` を追加済み）。
- 除外: `docs/screens/20260909_SC-04_opend-auth.md`（「参照のみ」の一致は OpenD 認証の文脈）、`.claude/rules/traceability.repo.md`（レンジ）。

## 決定する挙動

1. `/policy`: Bot が `GET /monitor/watchlist` を照会し、`currentWatchlist` で運ぶ（失敗は null）。報告書サービスは米国の銘柄を LLM へ渡し、
   一覧を試行の台帳に記録する。応答は方針の全文に続けて、入れ替えの銘柄と理由を全文で（通を分けて）表示し、適用の条件を添える。
2. 確認ボタン `ast-policy-approve-<periodKey>-<version>`（文言に入れ替えの件数）→ `PolicyApprovalCommandHandler`:
   | 状況 | 挙動 |
   | --- | --- |
   | 確定できなかった（版落ち・二重押下・失敗） | 案を引かない・適用しない |
   | 案の照会に失敗 | 適用しない（照会の失敗と伝える） |
   | /policy の案でない・入れ替え無し・記録済み | 適用しない |
   | 案を作った時点の監視銘柄が分からない | 適用しない・内訳 `snapshot-unknown` を記録 |
   | 市場監視 200 | 適用・適用せず（理由）の内訳と Finnhub の推定（警告のみ）を出し、`applied` を記録 |
   | 市場監視 409 | 「案を作った後に監視銘柄が変わった」・`stale` を記録（1 件も適用されていない） |
   | 市場監視 400 等 | 「適用できませんでした」・`rejected` を記録 |
   | タイムアウト・例外・解釈できない 2xx | 「結果が分からない（設定画面で確認）」・`indeterminate` を記録 |
3. 市場監視 `POST /monitor/watchlist/proposal-apply`（OwnerOnly）: 形の違反は 400、期待値と監視銘柄の集合が違えば 409、各銘柄は SC-02 と同じ規則、
   通った銘柄を 1 回の保存で適用、変更履歴は SC-02 と同じ形で 1 件ずつ、変更者は代理（信頼クライアント）。応答に推定（警告のみ）。
4. `/report approve` の確定は入れ替えを適用しない。

## 受け入れ基準 → テスト（T-10-1362〜T-10-1408 を予約。使用は 1362・1376〜1408。1363〜1375 は欠番）

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | FR-14 の例外は `/policy approve` だけ・銘柄を取らない・監視銘柄を変え得るポートは 1 つで照会と適用だけ | `DiscordSettingsAreReadOnlyTests`（T-10-1362・T-10-1335 の改訂・T-10-1334 の拡張） |
| 2 | 期待値と違えば適用しない・銘柄ごとの検証・形の違反・履歴の形・保存しない場合 | `WatchlistProposalApplyTests`（市場監視。T-10-1376〜1380） |
| 3 | 推定は警告のみ・対象外の構成では出さない | 同（T-10-1381） |
| 4 | 本番の組み立て: 変更者は本人・内訳・推定／409・400／サービス主体は 403 | 同（T-10-1382〜1384） |
| 5 | 案を作った時点の監視銘柄の記録・米国の銘柄だけを LLM へ・形式違反の一覧は受けない | `ReportPolicyRevisionServiceTests`（T-10-1385）・`LlmReportPolicyReviserTests`（T-10-1386） |
| 6 | 本番の組み立て: 確定した版の案の照会・内訳の記録は 1 回だけ・サービス主体は 403 | `PolicyRevisionWiringTests`（T-10-1387） |
| 7 | 越境の契約（市場監視・報告書の本物の型）と失敗・不明の区別 | `WatchlistApplyContractTests`（T-10-1388〜1395） |
| 8 | 確認ボタンの流れ（確定できたときだけ・台帳の案だけ・本人・内訳・失敗と不明・二重押下・認可） | `PolicyApprovalCommandHandlerTests`（T-10-1396〜1404） |
| 9 | `/policy` の照会と表示（理由の全文・照会できないとき） | `PolicyRevisionCommandHandlerTests`（T-10-1405〜1407） |
| 10 | 確認ボタンの接頭辞と件数 | `PolicyRevisionReplySenderTests`（T-10-1408） |

［2026-09-26 追記 / PR #1027 の監査］是正（IADR-0433 決定 7）:

| 所見 | 是正 | 試験 |
| --- | --- | --- |
| H1 再起動の後の古い版のボタンで確定されていない案を適用 | 照会・記録は「その版で確定」のときだけ（409）。確定の応答に `transitioned`・`version`、Bot は別の版で確定済みを確定と言わない | T-10-1415・T-10-1420・T-10-1421 |
| M1 「逆は起きない」の誤り・回復・見える化 | IADR の訂正、同じ版の押し直しで回復、確定の遷移で `ProposalConfirmedAt` | T-10-1415・T-10-1422 |
| M2 代理の否定形 | 一覧外のクライアント・利用者のトークン・空の一覧の試験（単体と本番の組み立て） | T-10-1418・T-10-1419 |
| M3 原則 A（null と空） | 照会の射影まで null／[]／有りを区別する試験 | T-10-1416 |
| L1 記録で任意の試行を塞げた・経路の偽り | 記録は確定された案の試行だけ。変更履歴の理由は代理なら「Discord の確認ボタンで適用・代理 …」、直接なら「利用者のトークンで直接適用」 | T-10-1415・T-10-1419 |
| L2 200 件の一覧が列を超える・200 件超で 400 | 列は text（#1026 と揃える）。200 件超は「分からない」 | T-10-1417 |
| L3 欠けた項目を黙って落とす | 一覧ごと「分からない」 | T-10-1420 の対 |
| L4 注釈とコードの食い違い | 注釈を直す | — |
| Info 名前だけの固定 | パスと名前付きクライアントの文字列の置き場所を固定 | T-10-1423 |
