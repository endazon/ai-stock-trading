---
title: 計画 ADR-0030〜0055 の突合で見つかった残作業の台帳の仕分け（#1204）
type: spec
status: accepted
related_ids: [NFR, FR-05, FR-06, FR-10, FR-15, FR-16, FR-19, FR-20, ADR-0030, ADR-0033, ADR-0034, ADR-0038, ADR-0047, ADR-0049, ADR-0050, ADR-0051, ADR-0053, ADR-0054, IADR-0013, IADR-0067, IADR-0272, IADR-0335, IADR-0486, IADR-0502]
author: claude (Claude Code)
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0030_report-section-numbering-is-plan-canonical.md 〜 ADR-0054（各行の起点）
---

# 仕様書: 残作業の台帳（#1204）を 1 行ずつ仕分ける

## 起点となる計画書（トレーサビリティ）

- 起票: #1204（第 4 回全体監査 2026-10-07 の指摘 B-18・C-3・C-6）
- 各行の起点: 計画 ADR-0030 / 0033 / 0034 / 0038 / 0047 / 0049 / 0050 / 0051 / 0053 / 0054（下表）
- 計画リポの参照: `origin/main`（読み取り専用。ADR 本文を `git show` で読んだ）
- 本 PR の基点: `origin/develop` `2458fc95`

## 目的・背景

#1204 は「1 つの PR で直すものではない」台帳である。受け入れ基準は、各行が
**(a) 別 issue へ切り出した（番号）／(b) 直した（PR）／(c) 意図した保留として IADR に記録した** のいずれかになることである。
本仕様書は各行の仕分けとその根拠（現物の実測）を残す。**振る舞いを変える行はすべて (a)** とし、本 PR で直すのは
コメント・文書・試験の失敗時メッセージだけである（コードの振る舞いは変えない）。

## 対象範囲

- 本 PR で直す（b）: 行 11（`OrderAmendmentDispatcher` の駆動元のコメント）・行 12 の前半（「17 本」）・行 13 の前半（PlatformShim の「本番非使用」）
- 本 PR で記録する（c）: 新 IADR-0502（行 6・行 1 の後半・行 12 の後半の保留）、IADR-0067 / IADR-0013 への日付つき追記
- 対象外（触らない）: planning#741 の裁定対象 6 点（AST ADR-0040 決定 1・ADR-0049 決定 3・ADR-0050 決定 2、MSP ADR-0115〜0117、MSP FR-01/02）。
  ADR-0049 決定 4・ADR-0050 決定 1 の行は**実装しない**（行 6 は保留の記録、行 7 は起票のみ）。
- IADR-0304 は #1201 / PR #1216 が触るため本 PR では触らない（行 3 の前半）。

## 仕分けの結果（受け入れ基準: 全行が a / b / c のどれか）

| # | 台帳の行 | 実測（`2458fc95`） | 仕分け |
| --- | --- | --- | --- |
| 1 | ADR-0030 フォローアップ 3: 日報 §6 振り返り | `ReportRenderer.cs:95` が `AppendNotImplemented`、理由 `:126`。IADR-0291:165 が「起票の要否は #615 の完了時に棚卸し」としたまま未起票 | (a) **#1218** |
| 1' | 同: 月報 §3 税金レビュー | `ReportRenderer.cs:70`、理由 `:123`。**IADR-0272 決定 3 が既に「前提整備〔年初来累積の権威源・口座区分・配当〕が無く着手できない節は受容として記録し先送り」と記録**（IADR-0291 は決定 1 だけを supersede。決定 3 は有効） | (c) IADR-0272 決定 3（既存）。IADR-0502 決定 2 で再確認し、外す条件を書く |
| 2 | ADR-0033 フォローアップ 3: 最初の見積り（トークン量の実測） | `Stage0RecordingBudget.cs:33-35` のトークン量は構成値（既定 0）で実測が無い。#690（所要時間）・#243（Opus 5 の出力トークン）はいずれも本件を明示しない | (a) **#1219**（`blocked:env`） |
| 3 | ADR-0034 フォローアップ 1（IADR-0304 への追認の追記） | #1201 / PR #1216 が IADR-0304 を変更中 | (b) **PR #1216**（重複させない） |
| 3' | ADR-0034 決定 5 ②（商品種別設定の変更を verdict 無効化の契機に） | `ShortSellRelease.cs:140-171` は ①未承認 ②期限 ③情報源 ④戦略 ID のみ。戦略 ID（`Stage0StrategyIdentity`）はモデルを含む＝契機 1 は成立、`EnabledProductTypes` は含まない＝契機 2 は不成立 | (a) **#1220** |
| 4 | ADR-0038 フォローアップ 2（写しのずれ検知）・4（401 事故件数） | IADR-0324 の追記（#776）が「突合の受け皿は基盤側」と書いたが、基盤側にも AST 側にも追跡が無い | (a) **#1221** |
| 5 | ADR-0047 段 6（REST 退役） | #753 で追跡済み。本行は参照のみ（issue 本文のとおり） | 参照のみ: **#753** |
| 6 | ADR-0049 決定 4: 1 注文上限の緩和の検討 | IADR-0486:110「本件の後に別途行う」。前提＝下限が効いていること。ATR(14) の下限は既定無効（IADR-0486 残余「有効化は未実施」）、丸めの向き（決定 3）は planning#741 の項 1 で裁定待ち。25% は計画 05_trading-assumptions §5 の値で、緩めるのは計画の変更 | (c) **IADR-0502 決定 1**（保留。外す条件を書く。planning#741 を待つ） |
| 7 | ADR-0050 決定 1 の残余: 利用者の成行の手仕舞い・自動縮小を S1 が取り消す | IADR-0466:107-109「未起案」。planning#741 の項 3 は**決定 2** であり本行とは別 | (a) **#1222**（実装しない） |
| 8 | ADR-0051 フォローアップ 1: 警告で銘柄ごとの有無を見る | IADR-0470:182「新たな配線が要る」 | (a) **#1223** |
| 9 | ADR-0053 フォローアップ 3: JST 暦日の入力と窓 | IADR-0492:116-117 の残余リスク | (a) **#1224** |
| 10 | ADR-0054 フォローアップ 2 の確認結果の記録先 | **PR #1211（#1196。マージ済み）が IADR-0318 に「日報のスキップ回数はスクリーニングの見送りを含む（確認結果）」を日付つきで追記済み**（IADR-0318:270-273） | (b) **PR #1211**（済み） |
| 11 | 結線の残り: `OrderAmendmentDispatcher` の駆動元のコメント | 下の「行 11 の実測」 | (b) **本 PR**（コメントと IADR-0067 の記録を実物に合わせる。新しい振る舞いは配線しない） |
| 12 | C-3: `UnwiredDiRegistrationTests` の「共有 4 本＝17 本」 | 本番プロジェクトは 12 サービス＋BFF＋共有 5 本（Contracts / Grpc / Infrastructure / Kernel / KnowledgeBase）＝**18 本**（`find backend -name '*.csproj'` から `*.Tests.csproj`・`TestSupport/`・`Tests/` を除いて実測）。下限 14 は不変 | (b) **本 PR**（失敗時メッセージの数え） |
| 12' | C-3: `StageProhibitsLiveTrading` のトートロジー | `RiskEvaluator.cs:99-102` ↔ `SizingContextService.cs:33`（`Mode: settings.Stage.Mode`）。#204 D-5 が「起票せず（安全側・計画上の穴でもない。効いている統制と数えない）」と判断済み | (c) **IADR-0502 決定 3** |
| 13 | C-6: PlatformShim の csproj ヘッダ「本番非使用」 | 下の「行 13 の実測」 | (b) **本 PR**（コメント・README と IADR-0013 の追記） |
| 13' | C-6: `FinnhubSharedKeyBudgetTests` は Helm 込み 57 を見ない | 試験はコード既定（30＋5×4＝50）だけ。Helm は 30＋12＋5×3＝57（`values.yaml:454-456`・chart README:268-269 で再計算して一致） | (a) **#1225** |

### 行 11 の実測（`OrderAmendmentDispatcher` は #141 / #152 から呼ばれるべきか）

- 現在の呼び出し元は `PositionCloseCancellationHandler`（#847）だけ（`grep -rn "OrderAmendmentDispatcher" backend/Services --include=*.cs` の本番側）。
- **#141（2026-07-20 クローズ）**: 自動リコンサイルは滞留した `Reserved` 予約を照会で「発注済み→確定／未発注→解放／不明→据え置き」に写す
  （`OrderReservationReconciler`・IADR-0074 / IADR-0092）。`Reserved` は注文 ID を持たず、**取り消す注文が無い**。クローズ時の受け入れ基準にも取消は無い。
- **#152（2026-07-18 クローズ）**: 一時停止は計画 ADR-0009 のとおり**新規建てだけを止めるゲート**（`RiskEvaluator`）であり、
  板に残った注文を取り消すことは計画（ADR-0009 に「取消」「未約定」の語は 0 件）にも実装（IADR-0075）にも無い。
- 「#141 / #152 が呼ぶ」は IADR-0067（2026-07-17、#154）が**両 issue の実装前に置いた見込み**であり、両 issue はその形を採らずに閉じた。
- **結論**: 両 issue が本クラスを呼ばないのは**未配線ではなく設計どおり**。コメント「依然として本クラスを呼んでいない／未配線」は
  「呼ぶべきだが呼んでいない」と読めるため誤り。**新しい駆動元は配線しない**（タスクの制約・計画に要求が無い）。
  時限取消は起票も計画の要求も無い見込みであり、「未実装」と書くと追跡があるように読めるので、駆動元の予定は無いと書く。

### 行 13 の実測（PlatformShim は本番で使われていないか）

- `PlatformShim` を `ProjectReference` する本番プロジェクト: 11 サービス（Audit / Backtest / Configuration / CostControl / InformationCollection /
  MarketMonitor / Notification / OrderExecution / Report / RiskManagement / TradeDecision）＋ `Shared.KnowledgeBase` ＝ 12 本（OpendAuthGateway と BFF は参照しない）。
- shim は配備で実際に動く配線を持つ: 例外応答（IADR-0496 決定 3: 「11 サービスが稼働で使う共通配線」）・gRPC の所有者の門
  （`Foundation/Auth/GrpcOwnerClientGate.cs`。Discord ボットの `azp` を確かめる。IADR-0448）。
- IADR-0013 の「本番非使用」は「#22 で platform 本体の Foundation へ差し替えた後の姿」であり、README と IADR-0013 自身が
  「#22 完了まで」の注意を書いていた。**#22 は 2026-07-20 に差し替えをせずにクローズ**（拡張規約 3 要求の充足でクローズ）。差し替えの追跡は無い。
- **結論**: 「本番非使用」はどの時点でも成り立たなくなった。コメント・README を実物に合わせ、IADR-0013 に日付つき追記で前提の失効を記録する。
  **改名・移動はしない**（名前空間 `AiStockTrading.TestSupport.PlatformShim.*` の変更は 12 本と試験に波及し、得るのは名前の正しさだけ）。

## 母集合（規則 9・10）

### 規則 9: 誤りの側の文字列で全文書を走査してから追随先を挙げる

| 走査（`git grep` / `grep -rn`、`bin/` `obj/` `CHANGELOG.md` を除く） | ヒット | 本 PR での扱い |
| --- | --- | --- |
| `#141`（リコンサイルの取消基点）・`#152`（pause による強制取消）・`強制取消`・`取消基点`・`時限取消` | `OrderAmendmentDispatcher.cs:15`・OES `Program.cs:222`・`OrderCancelled.cs:6`・`OrderAmendmentDispatcherTests.cs:25`・`OrderAmendmentServiceTests.cs:17`・IADR-0067（決定 6・結果・フォローアップ・2026-09-19 追記） | コード／試験のコメント 5 か所を直す。IADR-0067 は凍結記録のため本文を書き換えず日付つき追記で失効を記録。試験の理由文字列 `"pause による強制取消"`・`"時限取消"`（`AuditEntryFactoryTests` ほか）は任意の自由文字列の例であり主張を持たないので触らない。`.ai-context/specs/`（20260717_154 / 20260903_613 / 20260911_752）と IADR-0335:106 は point-in-time の記録のため触らない |
| `17 本`・`共有 4 本` | `UnwiredDiRegistrationTests.cs:48` | 直す（18 本）。`.ai-context/specs/20260911_752_*`:64,73 と IADR-0335:68 の「17 本」は 2026-09-11 時点の実測として正しい（同仕様書:73 が共有を `Contracts,Infrastructure,Kernel,KnowledgeBase` の 4 本と列挙しており、`Shared.Grpc` はまだ無い）ため触らない。IADR-0208:368 は別の数え（試験プロジェクト）で無関係 |
| `本番非使用` | PlatformShim csproj:1-3、サービス csproj 11 本（Audit:5,34 / Backtest:32 / Configuration:6,44 / CostControl:45 / InformationCollection:7,32 / MarketMonitor:7,40 / Notification:5,32 / OrderExecution:5,42 / Report:6,43 / RiskManagement:40 / TradeDecision:8）、`backend/TestSupport/README.md`、IADR-0011:58 / IADR-0013 / IADR-0048 / IADR-0049:6,25（`git grep -l 本番非使用 -- .ai-context/adr` の実出力。索引 README.md の行は本 PR の追記） | csproj のコメントと README を直す。IADR-0013 は日付つき追記。IADR-0048・IADR-0049 は frontmatter の注記と本文が IADR-0013 を引くだけ、IADR-0011:58 は #22 前の移動の記録なので、いずれも IADR-0013 の追記で足りる（凍結記録の本文は書き換えない。独立レビューの指摘で IADR-0011・0049 を母集合へ足した） |
| `57`（Finnhub）・`Helm 込み` | `values.yaml:454`・chart README:269 | 再計算して一致（30＋12＋5×3）。触らない（#1225 で機械検査にする） |

### 規則 10: この変更で新たに誤りになる自分の記述

- 「本番プロジェクト 18 本」は `KnowledgeBase` / `Grpc` を含む今の数え。共有プロジェクトが増減すれば再び古くなる（下限 14 の判定は影響しない）。
  メッセージに「実際に見つかったのは: {0}」が併記されるため、ずれても読み手は実数を見られる。
- 「shim を参照する本番プロジェクトは 12 本」は csproj の参照の数え（本 PR の README・IADR-0013 追記）。**数を README に書かない**
  （増減で腐る導出値。規則 10）。README は「本リポから組む各サービス」と書き、数は本仕様書に point-in-time で残す。
- 「#141 / #152 は呼ばない（設計どおり）」は、将来 pause に「板の新規建てを取り消す」要求が計画に入れば誤りになる。
  IADR-0067 の追記に「計画が取消を求めたら駆動元として本クラスを呼ぶ」を外す条件として書く。
- 台帳の番号（#1218〜#1225）は本仕様書・IADR-0502・#1204 の本文の 3 か所に書く。**正は #1204 の本文**（チェックボックス）。

## 設計（本 PR の変更）

1. コメントの是正（振る舞い不変）: `OrderAmendmentDispatcher.cs`・OES `Program.cs`・`OrderCancelled.cs`・`OrderAmendmentDispatcherTests.cs`・`OrderAmendmentServiceTests.cs`。
2. `UnwiredDiRegistrationTests.cs:48` の失敗時メッセージ「共有 4 本＝17 本」→「共有 5 本＝18 本」（下限 14 は変えない）。
3. PlatformShim の csproj ヘッダ・サービス csproj のコメント・`backend/TestSupport/README.md` を「本リポから組む配備ではこの shim が実行時の配線そのもの」に直す。
4. IADR-0067・IADR-0013 に日付つき追記（`［2026-10-07 追記 / #1204］`）、索引行を追随。
5. 新 IADR-0502（保留の記録 3 件）と索引行（番号順の末尾。develop の最大は 0500、PR #1216 が 0501 を使う）。

## 受け入れ基準

1. #1204 の各行が a / b / c のどれかになり、本文のチェックが全部埋まる（本表）。
2. 本 PR は振る舞いを変えない: `dotnet build backend/backend.slnx` 警告 0、`dotnet format --verify-no-changes`、影響する試験（`AiStockTrading.Architecture.Tests`・OrderExecutionService.Tests の該当クラス）が緑。
3. 文書系の検査器（trace-blocks / knowledge-graph / adr-index-sync / adr-index-addendum-loss / cross-repo-refs / plan-id-qualification / reading-budget / commit-messages / scripts.test.js）が exit 0。

## 未決事項

- #1204 のクローズ: 本 PR と PR #1216 のマージ後（チェックがすべて埋まり、直した PR がマージ済みであること）。
