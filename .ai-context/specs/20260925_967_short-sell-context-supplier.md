---
title: 空売り文脈（ShortSellOrderContext）を本番で組む供給元を入れ、未約定の空売りを残数量 × 承認価格でエクスポージャへ算入する（#967）
type: spec
status: accepted
related_ids: [FR-10, FR-05, UC-06, ADR-0016, ADR-0019, ADR-0026, IADR-0111, IADR-0131, IADR-0144, IADR-0158, IADR-0159, IADR-0163, IADR-0346, IADR-0354, IADR-0397, IADR-0420, IADR-0425]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md (決定 2(a)・決定 3〔2026-08-06 改訂・同追記・2026-08-07 確定〕・決定 9・決定 10)
  - planning:projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md (PoC 項目 3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0026_short-fee-rate-unit-poc.md (PoC 項目 9＝ShortFeeRate の単位)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 (1)(3)(6))
---

# 仕様書: 空売り文脈の供給元と、未約定の空売りのエクスポージャ算入（#967）

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-10**（空売り専用統制 (1) 1 銘柄 equity の 10%・(3) 借株可否／照会不能なら空売りしない・(6) 空売り比率 50%）、FR-05（発注執行＝moomoo 接続の所在）
- ユースケース: UC-06（統制）
- 画面: なし
- 関連 ADR（計画）: **ADR-0016 決定 2(a)・決定 3（改訂・追記・確定）・決定 9・決定 10**、ADR-0019 PoC 項目 3、ADR-0026 PoC 項目 9
- 関連 IADR: IADR-0131（文脈が無ければ拒否）、IADR-0144 決定 3・5（照会は実弾ヘッダでのみ成功／キャッシュ・失敗時に即時リトライしない）、IADR-0158（一次ゲート＝`ShortPermit`・`ShortFeeRate` は写像しない）、IADR-0159 決定 5（偽の文脈を組まない）、IADR-0163 決定 2（不在が統制の無効を意味する依存は必須）、IADR-0346 決定 2（未約定の新規建てを残数量 × 承認価格で算入）、IADR-0354 決定 6（起きていない事実を理由にしない）、IADR-0397（組み立てガード）、IADR-0420（送り手の本物の型による契約テスト）
- 新設 IADR: **IADR-0425**
- 関連 issue: **#967**（本件）、#832（起点のトリアージ）、#829（未約定の算入）、#342（PoC）、#1000（実弾ヘッダでの照会の裁定＝本件で起票したフォローアップ）
- 裁定: #967 のコメント（2026-09-25・オーナー）——**供給元を今すぐ実装する（対処案 1〜3）。失敗・照会不能は今と同じく拒否。偽の文脈は組まない。`docs/blocked-tasks.md` の追跡先を #967 へ付け替える**

## 🔴 実測（コードで確認・`origin/develop` = `9b80cd72`・`git rev-parse --is-shallow-repository` → `false`）

| 箇所 | いま |
| --- | --- |
| `RiskManagementService/Features/RiskManagement/OrderScreeningService.cs:80-81` | `RiskEvaluator.Evaluate(intent, settings, snapshot, patternDetector, buyInBan:, stopOuts:)`。`shortSellContext` を渡さない（既定 null） |
| `RiskManagementService/Domain/ShortSellEvaluator.cs:93-98` | 文脈 null → `BorrowUnavailable` を立てて `return`。10% / 50%（`:149-161`）へ到達しない |
| `RiskManagementService/Domain/ShortSellEvaluator.cs:113-126` | 文脈があれば `ShortPermit=false` → `BorrowUnavailable`、`BorrowRateAnnual=null` → `BorrowUnavailable`（いずれも `return` しない＝後続の規則を評価する） |
| `OrderExecutionService/Infrastructure/ExternalServices/MMApiMoomooTradeClient.cs:781-786` | 取引ヘッダは `TrdEnv_Simulate` 固定・SIMULATE 口座の accId。`OnReply_GetMarginRatio` は空実装（`:912`） |
| `OrderExecutionService/Program.cs` | HTTP の業務エンドポイントは 0 本（ヘルスと自己申告のみ）。認証も未登録 |
| `RiskManagementService/Features/RiskManagement/PortfolioProjection.cs:245-277` | `ProjectWorkingEntries`＝当日承認・残数量 > 0 の新規建て（約定累計を同じ fills から引く） |
| `RiskManagementService/Infrastructure/ExternalServices/CachedCurrentPriceSource.cs` | 現在値は手元のキャッシュ（鮮度上限つき）。取れない銘柄はキーを落とす。時価評価が無効ならキャッシュは補充されない |

## 決定（詳細は IADR-0425）

1. **照会の担い手は発注執行**（moomoo の取引接続を唯一持つ）。`GET /order-execution/short-permit?symbol=&market=`（`OwnerOrService`）を足し、
   応答は `ShortPermitView`（`Status`＝Unknown / Permitted / NotPermitted・`UnknownReason`・`ObservedAt`）。**`ShortFeeRate` は運ばない**（単位未確定。IADR-0158 決定 3）。
2. **照会は発注に使っている口座（SIMULATE）のヘッダで行う。実弾ヘッダ（`TrdEnv_Real`）の照会経路は作らない。** 実弾ヘッダでの照会は
   IADR-0111（環境 1 軸）の部分改定と実弾の閂に触れる別の判断であり、本件の裁定の射程外（裁定は「SIMULATE では成功しない見込み・そのとき拒否」）。
   → SIMULATE では照会が失敗し Unknown ＝ 今と同じく拒否。
3. **照会の節約**（IADR-0144 決定 5）: 発注執行が (銘柄, 市場) ごとに成功は 60 秒・失敗は 30 秒キャッシュし（失敗時に即時リトライしない）、
   30 秒あたり 9 回の予算（ブローカーの上限 10 回より 1 回少なく）を失敗も含めて数える。予算切れは照会せず Unknown。米国株以外は照会せず Unknown。同じ銘柄の照会が走っている間の要求は相乗りする。
4. **リスク管理の受け手** `HttpShortSellBorrowSource`（`OrderExecution:BaseUrl`・サービストークン・5 秒）。非 2xx・例外・タイムアウト・欠落・未定義値・
   銘柄／市場の食い違いは Unknown。BaseUrl 未設定・不正は `UnavailableShortSellBorrowSource`（常に Unknown）。
5. **文脈の組み立て** `ShortSellContextSupplier`（必須依存として `OrderScreeningService` へ）。新規の売り建て（`Sell`×`Open`）の審査でだけ呼ぶ。
   - 借株可否が **Unknown → 文脈を組まない（null）**＝`BorrowUnavailable` で拒否（今と同じ）。
   - エクスポージャが **読めない → 文脈を組まない（null）**＝同じく拒否。**0 で埋めない**（none と unknown を区別する）。
   - `BorrowRateAnnual` は **常に null**（単位未確定。ADR-0016 決定 3 の 2026-08-07 確定＝案 A）→ 文脈が組めても `BorrowUnavailable` が立つ（全件拒否は続く）。
     ただし `return` しないので **10% / 50%・維持率・株価下限・逆指値必須が評価され、監査の理由に載る**。
   - 維持率は既存の `IMaintenanceMarginSnapshotSource`（既定は供給なし＝null）をそのまま渡す。権利確定日は供給元が無いので null（下の残余）。
6. **エクスポージャ**（基準通貨）: 保有建玉は **時価**（現在値 × 数量 × 建玉の加重平均約定時レート。SC-03 の空売り比率と同じ定義）、
   当日承認・未終端の新規建ては **残数量 × 承認価格（基準通貨）**（`PortfolioProjection.ProjectWorkingEntries` をそのまま使う＝二重計上しない）。
   保有建玉に現在値が 1 件でも無ければ unknown。建玉も未約定も無ければ known 0。
7. **審査の非同期化**: `OrderScreeningService.Screen` を `ScreenAsync(decision, ct)` に置き換える（同期の入口を残さない＝供給元を通らない経路を作らない）。
   ハンドラは `await ScreenAsync`。
8. **結線テスト**: 本番の `Program.cs` の組み立てで、BaseUrl を与えると `HttpShortSellBorrowSource` が解決され、判断イベントを本番の Wolverine
   ハンドラへ流すと送り手の偽応答（Permitted）から組んだ文脈で **`ShortExposureExceeded` が立つ**（10% と 50% の両方）。BaseUrl 無しは Unknown ＝ 拒否。

## 母集合の引き直し（`traceability.md`「是正・追随の母集合の取り方」規則 1〜6・`traceability.repo.md` 規則 9・10）

**軸 1（誤りの側＝供給元が無い／文脈は組めない、の主張）**:
`git grep -n -E "借株照会の供給元|供給元が無いため.*(文脈|ShortSellOrderContext)|ShortSellOrderContext.{0,20}(組めない|組む本番)|shortSellContext が null|文脈が .?null.? である限り|文脈は .?null.?|今も組めない|供給元は未実装|供給元の実装は" -- . ':!CHANGELOG.md' ':!.ai-context/specs' ':!.ai-context/superpowers'`

**軸 2（名前の側）**: `git grep -n -E "空売り文脈|ShortSellOrderContext|IsShortPermit|TrdGetMarginRatio|GetMarginRatio" -- . ':!CHANGELOG.md' ':!.ai-context'`

**軸 3（呼び出しの側）**: `git grep -n "\.Screen(" -- backend`（67 件・10 ファイル。すべて `ScreenAsync` へ）／`git grep -n "new OrderScreeningService(" -- backend`（12 件）／
`git grep -n "IMoomooTradeConnection" -- backend`（実装 3 件＝本番・テスト 2・結合試験 1）

| 対象 | 扱い |
| --- | --- |
| `OrderScreeningService.cs:62-63`・`RiskEvaluator.cs:347`・`BuyInBanSupply.cs:7`・`ShortSellOrderContext.cs:7-11` | **追随**（供給元が入った事実へ） |
| `docs/functional/FR-10_risk-controls.md:274, 329-331, 338, 1197` | **追随** |
| `docs/tests/FR-10_risk-controls-tests.md:144, 2057`・`docs/tests/FR-19_trading-guards-tests.md:79` | **追随**（対象外の列挙・未検証の表） |
| `docs/blocked-tasks.md:740`（一次ゲートの行）・`:749`（事後推定の行の「借株照会の供給元（#331 / #342）」） | **追随**（#967 へ。SIMULATE では照会が成功しない・料率の単位待ちが残ることを書く） |
| `ShortSellingControlsTests.cs:664`・`BuyInInferenceTests.cs:324` | **追随**（文言。テスト自体は供給元なしの構成で組むので意味は保つ） |
| `.ai-context/adr/IADR-0158:140`・`IADR-0159:128,135,173`・`IADR-0304:126` | **除外**: 凍結記録（本文プロズを書き換えない）。IADR-0425 が後継の事実を記録する |
| `docs/blocked-tasks.md:738, 741, 743`・`Program.cs:287` | **除外**: 維持率の束・借株料の日次計上・縮小履歴の供給元の話であり、本件（借株可否）ではない |
| `IADR-0137:149`・`ReportAutoGeneratorTests.cs:448`・`RiskEvaluatorTests.cs:777` | **除外**: 語の一致のみ（別の供給元） |
| `ShortSellEvaluator.cs`・`ShortSellRelease.cs`・`StageProductPolicy.cs`・`AccountTypePolicy.cs`・`BuyInBanPolicy.cs` の言及 | **除外**: 文脈が null のときの規律・型の説明であり、供給元の有無を主張していない |

## 対象範囲

- 発注執行: `IMoomooTradeConnection.GetMarginRatio`・`IMoomooShortPermitClient`（`MMApiMoomooTradeClient` が実装）・`ShortPermitQueryService`・エンドポイント・認証の登録
- リスク管理: `IShortSellBorrowSource`・`HttpShortSellBorrowSource`・`UnavailableShortSellBorrowSource`・`ShortExposureProjection`・`ShortSellContextSupplier`・`OrderScreeningService.ScreenAsync`・`TradeDecisionMadeHandler`・`Program.cs`
- 配備: `values.yaml` に order-execution の `auth: true` と risk-management の `OrderExecution__BaseUrl`（**空＝未結線**）を足し、`values-local.yaml` の risk-management にも同じ空の値を写す（helm はリストを置換する。挙動は変えない）
- 文書: 機能仕様書・テスト仕様書・blocked-tasks・IADR-0425・索引

## 対象外

- 実弾ヘッダ（`TrdEnv_Real`）での照会（決定 2）。権利確定日の供給元。維持率の束の供給元。`ShortFeeRate` の単位確定（ADR-0026 PoC 項目 9）。

## 受け入れ基準 → テスト（T-10-1020〜T-10-1039）

| ID | 基準 |
| --- | --- |
| T-10-1020 | エクスポージャ: 保有の空売り・ロングは時価、未約定の新規建ては残数量 × 承認価格、約定済みの分は二重に数えない |
| T-10-1021 | エクスポージャ: 保有建玉に現在値が無ければ unknown／建玉も未約定も無ければ known 0 |
| T-10-1022 | 文脈: 借株可否 Permitted／NotPermitted なら組む（`BorrowRateAnnual` は常に null・権利確定日 null・禁止期限を載せる） |
| T-10-1023 | 文脈: 借株可否 Unknown・エクスポージャ unknown なら組まない（null）。売り建て以外では照会しない |
| T-10-1024 | 審査: 文脈を判定コアへ渡し、10% / 50% で `ShortExposureExceeded` が立つ（`BorrowUnavailable` も立つ） |
| T-10-1025 | 受け手: 送り手の本物の型の直列化を読み、Permitted / NotPermitted / Unknown を写す（契約テスト） |
| T-10-1026 | 受け手: 非 2xx・例外・タイムアウト・欠落・未定義値・銘柄／市場の食い違いは Unknown |
| T-10-1027 | 結線: 本番の組み立てで BaseUrl ありは `HttpShortSellBorrowSource`、無しは `UnavailableShortSellBorrowSource` が解決される |
| T-10-1028 | 結線: 本番の Wolverine ハンドラ経由で 10% 超・50% 超の売り建てが `ShortExposureExceeded` で拒否される |
| T-10-1029 | 結線: 供給元が Unknown のとき、本番構成でも `BorrowUnavailable` で拒否され 10% / 50% は評価されない（今と同じ） |
| T-10-1030 | 送り手: 照会の結果（Permitted / NotPermitted / 欠落は Unknown）・米国株以外と未構成は照会しない |
| T-10-1031 | 送り手: 成功は 60 秒・失敗は 30 秒キャッシュし、その間は照会しない |
| T-10-1032 | 送り手: 30 秒あたり 9 回の予算を失敗も含めて数え、超えたら照会せず Unknown |
| T-10-1033 | 送り手: 本物の `Program.cs` の本文が応答型の web 既定の直列化と一致する。匿名は 401・値域外は 400 |
| T-10-1034 | 送り手: `MMApiMoomooTradeClient` が `TrdGetMarginRatio` を発注と同じ口座のヘッダで送り、非成功は例外 |
| T-10-1035 | 送り手: moomoo 構成の本番の組み立てで、照会サービスが照会ポート（OpenD クライアント）を受け取る（PR #1001 監査 N1） |
| T-10-1036 | 送り手: 同じ銘柄の照会が走っている間の要求は相乗りし、照会は 1 回。先に待ちをやめた要求の打ち切りは共有の照会を止めない（PR #1001 監査 N2・再監査） |
| T-10-1037 | 送り手: 失敗のログ出力が例外を投げても相乗りの登録は解かれ、失敗はキャッシュされる（PR #1001 再監査。解除は finally） |
