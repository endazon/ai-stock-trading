---
title: IADR-0284 east-west 同期照会の gRPC 化は射程 22 本（＋基盤待ち 4 本）を確定し、基盤の先例が無い間は着手せず移行順序の裁定を計画へ環流する
type: impl-adr
status: Accepted
related_ids:
  - FR-17
  - NFR
  - MSP:ADR-0029
  - MSP:ADR-0075
  - ADR-0001
  - IADR-0001
  - IADR-0051
  - IADR-0061
  - IADR-0063
  - IADR-0259
  - IADR-0264
author: endazon (with Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# IADR-0284: east-west 同期照会の gRPC 化は射程 22 本（＋基盤待ち 4 本）を確定し、基盤の先例が無い間は着手せず移行順序の裁定を計画へ環流する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は planning へ issue で環流する（`feedback.yml` テンプレート）。

- 状態: **Accepted**（起案 2026-09-03 → 確定 2026-09-03。計画側の裁定 `MSP/ADR-0075` により Proposed から確定。§追記参照）
- 日付: 2026-09-03
- 決定者: Claude Code（実測・起案）／ endazon（裁定は planning 側・`MSP/ADR-0075`）

## 起点・関連

- 関連する計画書 ID: FR-17（全体前提条件の照会元）、**`MSP/ADR-0029`**（同期通信の使い分け基準。基盤側の計画 ADR。
  2026-08-04 追記〔planning#180〕を含む）、`ADR-0001`（基盤再利用）
- 関連する実装仕様書: [`.ai-context/specs/20260903_584_grpc-scope-decision.md`](../specs/20260903_584_grpc-scope-decision.md)
- 関連 issue: #584（本件）、#526（`.Client` 廃止の起点）、PR #585（`IADR-0264`）、planning#180（`*.Client` を作らない裁定）、
  MSP#441（基盤側の再実装 issue。gRPC 未着手のままクローズ）、`MSP:IADR-0122`（proto を正本にする条件を繰延）

> 🔴 **`ADR-0029` の名前空間。** 本リポの裸の `ADR-0029` は資料再編（`.ai-context/` 分離）である。本 ADR が扱う gRPC/REST の
> 基準は **基盤（microservices-platform）側の計画 ADR** であり、本文では `MSP/ADR-0029` と修飾する。#584 本文の
> 「計画 `ADR-0029`」はこれを指す。

## コンテキストと課題

`IADR-0264` 決定 1 で `ConfigurationService.Client` を廃止し、中身を呼び出し元 2 サービスの `Infrastructure/ExternalServices/` へ
移した。`IADR-0259` 決定 9 は「gRPC 化は行わない（トランスポートの変更＝振る舞いの変更）。gRPC 化のみ別 issue へ切り出す」と定め、
#584 が切り出された。#584 は「`MSP/ADR-0029` に照らして gRPC 化／REST 継続を判断する。**REST 継続なら根拠を IADR に残して閉じてよい**」と書く。

しかし `MSP/ADR-0029` は次を明記する。

- §決定: 「サービス間の同期呼び出し（east-west）: gRPC + Protobuf 契約を標準とする。proto 契約は呼び出される側のサービスが所有し、
  共有契約として公開する」「例外を設ける場合は本 ADR を改定せず、対象経路を明記した新 ADR を起票する」
- 2026-08-04 追記: 「サービス公開クライアント（`*.Client`）は作らない。呼び出し側は gRPC 生成クライアントを用いる」
  「**既存の REST による east-west 同期呼び出しは、本 ADR の基準に該当するものをすべて gRPC へ移行する**（一括対応とする。作業の分割は妨げない）。
  『内部同期は gRPC』という基準に照らすと現行の REST 同期呼び出しは過渡的な状態であり、残す実益がないためである」

`MSP/ADR-0029` は AST にも及ぶ——`ADR-0001`（基盤の拡張・基盤無改修）と `IADR-0001`（リポ構成・規約を基盤に揃える）により、
計画 `12_backend-application-stack.md`（fixed）§プロジェクト構成「`*.Client` は標準に加えない。サービス間の同期呼び出しは ADR-0029 に従い
gRPC 生成クライアントへ寄せる」を `IADR-0259` / `IADR-0264` が既に前提として採用している。

したがって決めるべきは 3 つである: (1) **#584 の「REST 継続で閉じてよい」は採れるか**、(2) **射程は #584 の 2 本か ADR の言う east-west 全部か**、
(3) **今、実装へ入るか**。

### 実装の現状（起案前の確認・2026-09-03・develop `7c110ae9`）

| 事実 | 実測 |
| --- | --- |
| `.proto` | 本リポ 0 件・MSP 0 件 |
| gRPC パッケージ | 本リポ CPM 0 件。MSP は `Grpc.AspNetCore` / `Grpc.Net.Client` / `Grpc.Tools` 2.83.0 の**バージョン宣言のみ**（参照 `.csproj` 0 件・`MapGrpcService` / `AddGrpcClient` 0 件） |
| MSP の先例 | **無い**。MSP#441 は「ADR-0029 の内部 gRPC は完全未着手」と実測してクローズ。`MSP:IADR-0122` は「`.proto` 0 件のため proto を正本にできない。east-west が gRPC へ移行した時点で切り替える」と繰延 |
| ADR-0029 フォローアップ「proto 契約の配置と versioning 規約を実装ガイドへ落とす」 | 未履行 |
| Kestrel / Helm | HTTP/2 設定 0 件。全 Worker `containerPort: 8080` の 1 ポート。平文 HTTP/2（h2c）は ALPN 不在で `Http1AndHttp2` では HTTP/1.1 に落ちるため、gRPC 用の Http2 専用ポートが別に要る |
| s2s トークン | `AddAiStockTradingServiceToken(this IHttpClientBuilder, …)`（`ServiceTokenHandler` ＝ `DelegatingHandler`）。`AddGrpcClient<T>()` も `IHttpClientBuilder` を返すため等価配線が可能 |
| east-west 同期クライアント | `Infrastructure/ExternalServices/Http*` **24 本** ＋ 第 2 軸（`AddHttpClient` 登録）で `Shared.KnowledgeBase` の **2 本**（母集合の引き方と除外は作業仕様書） |

## 検討した選択肢

| 案 | 内容 | 工数 | 退行リスク | 計画整合 |
| --- | --- | --- | --- | --- |
| A | ADR-0029 どおり AST 内 22 本を一括 gRPC 化（段階分割） | 土台＋提供側 6 サービス（21 エンドポイント）＋消費側 22 本＋テスト 27 ファイル＋REST 撤去＝6〜8 PR | fail-safe の写像ミスは**統制が緩む向き**（残枠 0・空列・Normal の意味を `RpcException.StatusCode` で再現）。認可 19 箇所の写し忘れ。h2c 配備 | 逐語に合致。**ただし基盤に現物が無く、AST が慣行を先に決める**（`IADR-0001` / `IADR-0259`「揃える先の現物を見て決める」と逆転） |
| B | REST 継続を「対象経路を明記した新 ADR」として計画へ求める | 環流 1 件 | なし | 例外規定の形式は満たすが、根拠（頻度・fail-safe）は ADR-0029 が「経路ごとの裁量判断を排除する」ために採らなかった軸 |
| **B′** | **着手せず、移行順序の裁定を環流する。REST は例外ではなく「過渡状態の継続」** | 環流 1 件＋本 ADR | なし | ADR-0029 に反しない（移行義務を認め、例外を求めない）。裁定後に A へ進める切り方を先に用意する |
| C | Assumptions 2 本を先行し先例を作る | 土台の固定費を 2/22 本のために払う | A と同じ（範囲小） | 「分割は妨げない」に合致するが、**AST が先例を作る問題は A と同じ**。2 本 gRPC・20 本 REST の混在が長期化 |

## 決定

### 決定 1 — #584 の「REST 継続で閉じてよい」は採らない。判定基準は `MSP/ADR-0029` の境界基準に固定する

判定は「(i) 同期、(ii) east-west、(iii) proto は呼び出される側が所有」の**境界だけで機械的に決める**。頻度・レイテンシ・ペイロード・fail-safe の有無は
ADR-0029 の**理由**であって基準ではなく、判定に使うと ADR が排除した「経路ごとの裁量判断」を実装側で復活させることになる。
**REST 残置の根拠が立つ経路は 26 本中 0 本**である。#584 の当該記述は計画と衝突するため採らず、環流する（決定 4）。

### 決定 2 — 射程は #584 の 2 本ではなく east-west 同期 26 本。うち AST 内 22 本が AST の作業、AST→MSP 4 本は基盤待ち

| 区分 | 本数 | 内訳 | 扱い |
| --- | --- | --- | --- |
| AST 内 east-west | **22** | 提供側 6 サービス: Risk 14 エンドポイント（open-positions ×3 消費・sizing-context・stage-gate ×2・fills・buy-in-inferences・session-uptime・kill-switch ×2・pause/resume/status・good-faith-violations/clear・stage-gate/transition・withdrawal/evaluate）、Audit 1（`events/by-type` ×4 消費）、Configuration 1（`assumptions` ×2 消費）、Report 4（daily-policy・review・confirm・request-changes）、MarketMonitor 1（watchlist）、CostControl 1（costs/state） | **基準に該当**。proto を AST が所有できる |
| AST→MSP east-west | **4** | LlmGateway `POST /complete` ×2（TradeDecision・Report）、DocumentService `POST /documents`、RetrievalService `POST /search` | **基準に該当するが、proto の所有者は MSP**（ADR-0029「呼び出される側が所有」）。MSP に proto 0 件のため AST 単独では移行不能。`IADR-0061` の「`/complete` は匿名」も MSP 側の契約 |
| 基準外（第三者 API・IdP） | — | Finnhub・Stooq・FRED・Discord Webhook・Keycloak トークン取得 | 射程外（east-west でも north-south でもない） |

行ごとの根拠（頻度・fail-safe・エンドポイント）は作業仕様書 §射程表。

### 決定 3 — 今は実装に入らない（B′）。REST は「例外」ではなく「移行待ちの過渡状態」

- 基盤（MSP）に **現物が無い**——proto の置き場・versioning・h2c ポート・認可の写し方のどれも決まっておらず、ADR-0029 自身のフォローアップ
  （実装ガイド）も未履行。AST が先に決めると、後から MSP が別の形を採ったとき **22 本ぶんの揃え直し**が生じる。
  `IADR-0259` は「記憶や設計書の前提ではなく、揃える先の現物を見て決める」を一次的な作法とした。
- 案 B（例外 ADR を求める）は採らない。AST の呼び出しプロファイル（人手・分単位・全 24 本が例外を外へ出さない fail-safe）は
  「ADR-0029 の理由が AST に当たらない」という**事実の報告**として計画へ渡すが、実装側から例外を自認しない。
- 案 C は土台の固定費を 2 本のために払い、かつ A と同じ「先例を AST が作る」問題を抱えるため採らない。
- #584 は **`Refs`（閉じない）**。裁定待ちの `blocked` とし、`docs/blocked-tasks.md` の運用に従う。

### 決定 4 — 計画へ環流する（衝突・射程表・順序の裁定依頼）

planning へ feedback issue を起票する（起票前に `ADR-0029 gRPC` / `gRPC` / `east-west` で既存を検索し、該当は planning#180〔クローズ済・本追記の起点〕のみで
**同件は 0 件**）。求める裁定は 3 点: (1) 移行の**順序**（基盤先行→AST 追随／AST 先行／同時）、(2) AST→MSP 4 本の proto 公開時期、
(3) AST のプロファイルを踏まえてなお一括移行か、対象経路を明記した例外 ADR か。

- 環流先: **planning#520**（`https://github.com/endazon/project-planning/issues/520`）

### 決定 5 — 裁定で AST が着手する場合の切り方（先に固定しておく）

裁定が「AST 先行」または「基盤先行の現物が出た後の追随」となったとき、次の順で PR を切る（各段は独立にマージでき、REST は撤去まで並走させる）。

| 段 | 内容 | PR 粒度 |
| --- | --- | --- |
| 0 土台 | CPM に `Grpc.AspNetCore` / `Grpc.Net.Client` / `Grpc.Tools` / `Google.Protobuf`（MSP と同版 2.83.0 系）。Kestrel の Http2 専用ポート（h2c）と Helm `Service` / `Deployment` の第 2 ポート・readiness。`AddGrpcClient<T>().AddAiStockTradingServiceToken(config)` の等価配線を `PlatformShim` で確認。**fail-safe 写像の共通方針**: `Unavailable` / `DeadlineExceeded` / `Unauthenticated` / `PermissionDenied` / `Internal` → 各クライアントの既存の安全側既定（`null` / `[]` / 残枠 0 / `Normal` / LKG）、`NotFound` → 既存の「未供給」。deadline は `CallOptions.Deadline` へ（現行の `HttpClient.Timeout` 5〜10 秒を保つ）。proto 互換検査（`buf breaking` 相当）を CI へ | 1 PR（コード変更は土台のみ・消費者 0） |
| 1 | Configuration `Assumptions`（射程表の行 11・行 24。#584 名指し）。提供側 `AssumptionsService`・消費側 2 本・`CachedAssumptionsProvider` は不変（`IAssumptionsSource` の実装差し替えのみ） | 1 PR |
| 2 | Risk 読み取り系（open-positions ×3・sizing-context・stage-gate ×2・fills・buy-in-inferences・session-uptime） | 1〜2 PR（提供側 1・消費側 3 サービス） |
| 3 | Audit `events/by-type`（Report 4 本） | 1 PR |
| 4 | Report `daily-policy`・MarketMonitor `watchlist`・CostControl `costs/state` | 1 PR |
| 5 | Notification の OwnerOnly 書き込み 5 本（kill switch・pause・stage-gate・good-faith・report review）。人手経路のため最後。owner マップトークンは `AddDiscordOwnerToken` を同様に `IHttpClientBuilder` へ | 1 PR |
| 6 | REST エンドポイントの撤去（消費者 0 を `check-consumer-endpoint-names.js` 同型の走査で確認してから） | 1 PR |

- **proto の置き場**: 呼び出される側が所有（ADR-0029）→ `backend/Services/<Provider>/Contracts/Proto/<service>.proto`。消費側は
  `<Protobuf Include="../../<Provider>/Contracts/Proto/x.proto" GrpcServices="Client" Link="Proto/x.proto" />`。
  イベント契約（`Shared.Contracts`）とは分ける。**基盤先行の裁定なら MSP の置き場へ揃える**（本行は上書きされる）。
- **生成物はコミットしない**（`Grpc.Tools` がビルド時に `obj/` へ生成。CPM・ビルドの再現性は既存と同じ）。
- **結合テスト**: `AiStockTrading.IntegrationTests`（Testcontainers＝Docker）に gRPC 経路の s2s E2E を 1 本足す（`ServiceTokenSyncQueryE2ETests` と同型）。
  ローカルは `scripts/e2e-local-infra.sh` 経路。#584 が併記する「呼び出し元ごとのタイムアウト・リトライが効くことの結合テスト」は段 1 で満たす。
- `Microsoft.Extensions.Http.Resilience` / `HybridCache` への置換は**トランスポート変更と別 PR**（振る舞いの変更を混ぜない。`IADR-0259` 決定 9 の趣旨）。

## 理由

- **境界基準を崩さない。** ADR-0029 の価値は「経路ごとの裁量判断と構成の揺れを排除する」ことにあり、実装側が頻度で例外を切ると価値そのものを壊す。
- **揃える先が無いのに揃えない。** AST は基盤の拡張であり（`ADR-0001`）、規約は基盤に揃える（`IADR-0001`）。基盤が gRPC の現物を持たない今、
  AST が先に作った形は「揃えた」のではなく「先に決めた」であり、後で揃え直す費用を AST が払う。
- **実装せず環流する**のは、推奨（B′）が ADR-0029 の一括移行と時期の点で食い違うためである（義務は認め、時期と順序を計画に委ねる）。

## 結果

- 良い影響: 射程が 26 本で確定し、以後「2 本だけ」「REST でよい」という揺れが消える。裁定後に着手する順序が固定されている。
- 悪い影響・トレードオフ: REST（過渡状態）が裁定まで続く。#584 が blocked に入る。
- フォローアップ:
  1. planning の裁定を受けて本 ADR を Accepted へ確定するか改定する（裁定 → `docs/blocked-tasks.md` 解除 → 決定 5 の段 0 から着手）。
  2. MSP が proto を公開したら AST→MSP 4 本を決定 2 の「基盤待ち」から外して段を追加する。
  3. `MSP:IADR-0122` の「east-west が gRPC へ移行した時点で proto を正本にする」は AST でも同じ扱いになる（段 0 の互換検査で受ける）。

## 追記（2026-09-03・計画側裁定 `MSP/ADR-0075` を受けて Proposed → Accepted）

planning#520 の環流（§決定4）に対し、計画側が `MSP/ADR-0075`
（`projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md`。2026-09-03 確定）で裁定した。
本 IADR の決定 1〜4（境界基準に固定・射程 22 本＋基盤待ち 4 本・今は着手しない・環流する）は**裁定と同じ向き**であり、覆らない。

- **移行の順序は基盤先行**とする。MSP が proto の置き場（共有契約プロジェクト）・versioning 規約・h2c 用ポートの扱い・
  s2s トークンの写し方の現物を作り、AST はそれに追随する（proto の所有者は呼び出される側という §決定 2 の基準どおり）。
- `MSP/ADR-0029` フォローアップ（proto 契約の配置と versioning 規約を実装ガイドへ落とす）の履行を、移行着手の**先行条件**とし、
  **期限を 2026-11-30** に置く。**期限までに履行されなければ、基盤先行（決定 1）そのものを見直す**（覆り得るのはこの一点のみ）。
- **一括移行の義務は緩めない。例外 ADR は起こさない。** AST の 22 本は基準に該当する。理由（頻度・低レイテンシ）の一部が
  AST の呼び出しプロファイルに当たらないことは、基準を緩める理由にならない。
- **AST→MSP の 4 本**（LlmGateway `POST /complete` ×2・DocumentService `POST /documents`・RetrievalService `POST /search`）は、
  MSP が該当 proto を公開した時点で移行する。それまでの REST 継続は例外ではなく、`MSP/ADR-0029` 自身が「過渡的」と呼ぶ状態の継続である。
- **#584 の「REST 継続で閉じてよい」は採らない**（本 IADR 決定1 と同じ結論。実装側の IADR で REST 継続を自認する余地は無い）。
- **「基盤先行」は MSP 自身の east-west 移行を含む**（MSP にも `.proto` は 1 件も無い実測。AST は「基盤が先例を作ること」を待つのであって
  「基盤が AST のために proto を書くこと」を待つのではない）。
- **本 IADR の決定 5**（裁定で着手する場合の切り方。段 0 土台 → 段 1 Assumptions → … → 段 6 REST 撤去）は**変更しない**。
- #584 は `Refs`（閉じない）のまま。`blocked:decision` を外し `blocked:env`（他リポジトリ〔MSP〕の実装待ち）へ張り替え、
  待ち先を「MSP が `MSP/ADR-0029` フォローアップ（実装ガイド）を履行すること（期限 2026-11-30）」とする。
  `docs/blocked-tasks.md` B-4 の該当行を追随させた。

## 関連

- Supersedes: なし
- Superseded by: なし
- 前提: `IADR-0259` 決定 9（切り出し）・`IADR-0264` 決定 1（`.Client` 廃止）・`IADR-0063`（同期照会・fail-safe の順序。決定 1/4/5/6 は gRPC 化後も不変）・
  `IADR-0051`（s2s。`ServiceTokenHandler` を gRPC でも再利用）
- 採番注記: 当初 `ls .ai-context/adr | sort | tail -1` ＝ 0281 → 0282 で起票した。PR #639（watchlist シード・`IADR-0282_watchlist-config-seed.md`）が先に develop へ
  マージして 0282 を確保し、0283 は PR #647 が予約したため、**先着尊重で 0284 へ改番した**（2026-09-03。`.claude/rules/traceability.md`「採番衝突時の改番手順」）。
