---
title: east-west gRPC の段 2 —— Risk の読み取り経路を生成クライアントへ寄せ、REST と並走させる
type: spec
status: done
related_ids: [NFR, FR-10, FR-03, FR-04, FR-06, FR-20, FR-21, IADR-0284, IADR-0328, IADR-0331, IADR-0332, IADR-0352, IADR-0390, IADR-0399, IADR-0408, IADR-0420, IADR-0427, MSP:ADR-0029, MSP:ADR-0075]
author: endazon (with Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# 仕様書: east-west gRPC 段 2（Risk の読み取り経路）（#997）

## 起点

- issue #997（段 2 専用。受け皿は #753・`Refs`）。オーナー方針（#753 の 2026-09-25 コメント）:
  「段 2（Risk の読み取り経路）に着手する。段 1 と同じ切り方（提供側と消費側を同じ PR に入れ、REST は残す）で、
  段 2 用の issue を切ってから進める」。
- 雛形は段 1（#745 / [IADR-0331](../adr/IADR-0331_assumptions-grpc-transport-and-proto-contract-checks.md)）。
  土台は段 0（[IADR-0328](../adr/IADR-0328_east-west-grpc-foundation-stage0.md)）。射程と段の切り方は
  [IADR-0284](../adr/IADR-0284_east-west-grpc-scope-and-order-ruling.md) 決定 5。本 PR の判断は
  [IADR-0427](../adr/IADR-0427_risk-read-grpc-stage2.md)。
- 計画 ID: NFR（トランスポートの差し替え。振る舞いは変えない）。経路ごとの起点は FR-10（統制）・FR-03（損切り検知）・
  FR-04（判断）・FR-06（報告書）・FR-20（段階ゲート）・FR-21（観測の到達）。

## 射程の確定（IADR-0284 決定 5 の逐語と、引き直した母集合）

決定 5 の段 2 行（逐語）:

> | 2 | Risk 読み取り系（open-positions ×3・sizing-context・stage-gate ×2・fills・buy-in-inferences・session-uptime） | 1〜2 PR（提供側 1・消費側 3 サービス） |

### 母集合の引き方（規則 1〜6・9・10）

| 軸 | 引いたもの | 件数 |
| --- | --- | --- |
| 1（呼び出し先の側） | `backend/**/ExternalServices/*.cs`（テスト以外）の文字列 `"/risk-controls/…"` | 11 ファイル・21 箇所（Report 6・TradeDecision 2・MarketMonitor 1・Notification 5〔書き込み含む〕） |
| 2（構成の側） | `RiskManagement:BaseUrl` / `RiskManagement__BaseUrl`（全ファイル・拡張子で絞らない） | Program.cs 4（TD・MM・Report・Notification）・helm values 2・compose 1・凍結記録多数 |
| 3（登録の側） | `AddHttpClient("risk` | TD `risk`（5 秒）・MM `risk`（5 秒）・Report `risk-ledger`（10 秒・**依存先の門と観測つき**）・Notification 5（owner トークン） |
| 4（提供側の側） | `RiskControlEndpoints.cs` の `read.`（OwnerOrService）群 | 8 ルート: sizing-context・open-positions・working-entry-orders・fills・drift-adoptions・buy-in-inferences・session-uptime・stage-gate |

［2026-09-25 追記 / #997］**軸 1 の件数を訂正する。** 上表の「11 ファイル・21 箇所」は同じ行の内訳（Report 6・TradeDecision 2・
MarketMonitor 1・Notification 5 ＝ 14 ファイル）と矛盾していた（PR #1003 の監査が develop で 14 ファイルを実測）。引き直した値:
`MSYS_NO_PATHCONV=1 git grep -c -F '"/risk-controls/' origin/develop -- backend/Services` をテスト以外の
`Infrastructure/ExternalServices/` へ絞って **14 ファイル・20 箇所**（Report 6 ファイル・6 箇所／TradeDecision 2・3／MarketMonitor 1・1／
Notification 5・10）。数えたのは要求のパスを書く文字列リテラル（`"/risk-controls/…"` と `$"/risk-controls/…"`）であり、
ログ文言中の `GET /risk-controls/…` は含めない。🔴 当初の値は検索の出力と突き合わせずに書いた誤りである。なお Git Bash では
`MSYS_NO_PATHCONV=1` を付けないとこの検索語が書き換えられて 0 件になる（本追記の引き直しで実測）。移す／除外する経路の一覧
（下表と除外表）は変わらない。

### 段 2 で移すもの（決定 5 の 9 本のうち 8 本 ＋ 引き直しで見つかった 2 本）

| 射程表の行 | 呼び出し元 | クライアント | Risk のルート | rpc |
| --- | --- | --- | --- | --- |
| 1 | Report | `HttpPeriodFillSource` | `GET /fills` | `GetFills` |
| 2 | Report | `HttpBuyInInferenceRecordSource` | `GET /buy-in-inferences` | `GetBuyInInferences` |
| 3 | Report | `HttpOpenPositionSource` | `GET /open-positions` | `GetOpenPositions` |
| 4 | Report | `HttpOpenDUptimeSource` | `GET /session-uptime` | `GetSessionUptime` |
| 5 | Report | `HttpStageProgressSource` | `GET /stage-gate` | `GetStageGate` |
| 13 | TradeDecision | `HttpHeldPositionProvider.GetPositionAsync` | `GET /open-positions` | `GetOpenPositions` |
| 14 | TradeDecision | `HttpSizingContextProvider` | `GET /sizing-context` | `GetSizingContext` |
| 22 | MarketMonitor | `HttpPositionStore` | `GET /open-positions` | `GetOpenPositions` |
| （#934 で追加） | TradeDecision | `HttpHeldPositionProvider.GetWorkingEntryOrdersAsync` | `GET /working-entry-orders` | `GetWorkingEntryOrders` |
| （#870 で追加） | Report | `HttpPeriodDriftAdoptionSource` | `GET /drift-adoptions` | `GetDriftAdoptions` |

### 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| Notification `HttpStageGateController` の `GET /stage-gate`（行 19） | 決定 5 は「stage-gate ×2」と「消費側 3 サービス」が食い違う。同じクラスが OwnerOnly の書き込み 2 本を持ち、トークンは **owner マップ機密クライアント**（`AddDiscordOwnerToken`）である。gRPC で owner トークンを運ぶ形は段 5 の設計事項なので、**クラスごと段 5 で移す**。提供側の `GetStageGate` は段 2 で出す（IADR-0427 決定 1） |
| Notification `GET /status`（行 18）・`POST /position-drift/adopt`（#871） | OwnerOnly（段 5） |
| Notification の kill switch・pause/resume・good-faith・stage-gate の書き込み | OwnerOnly の書き込み（段 5） |
| BFF の中継（`RiskControlsBffEndpoints`） | 射程表に入っていない（BFF は REST） |
| テスト（`*/Tests/**`）内の `"/risk-controls/…"` | 母集合はプロダクションコード。既存テストは REST の試験として残す |
| helm values・compose | **既定は REST**（段 1 と同じ。既定描画を変えない）。有効化は提供側の `grpcPort` と呼び出し元の宛先を同じ変更で揃える運用事項 |
| `.ai-context/adr/*`・`.ai-context/specs/*` の既存記録 | 凍結記録（書き換えない）。IADR-0284 には日付つき追記を足す |

## 設計

詳細な判断と棄却案は IADR-0427。要点のみ。

1. **proto**: `backend/Shared/AiStockTrading.Shared.Grpc/Protos/aistocktrading/riskmanagement/v1/risk_controls_read.proto`。
   package `aistocktrading.riskmanagement.v1`・`csharp_namespace = "AiStockTrading.Shared.Grpc.RiskManagement.V1"`。
   service `RiskControlsRead` に rpc 8 本（上表）。
2. **原則 A**: スカラーはすべて `optional`（存在を持つ）。列挙はすべて `*_UNSPECIFIED = 0` を持ち、C# の列挙と**明示的に**写す
   （未指定・未知の番号は「不明」）。金額・率・日付・時刻は不変文化の文字列。**空・欠落の金額は 0 ではなく不明**（段 1 と異なる）。
   REST が「一覧が null」と「一覧が空」を分けていた箇所（稼働率の日次）は、存在を持つ入れ物 message で運ぶ。
3. **提供側**: RiskManagementService に `RiskControlsReadGrpcService`（新規ファイル）。REST と**同じサービス・純関数**を呼び、
   認可は**同じ** `OwnerOrService`。`from`・`to` の欠落（REST 400）は `INVALID_ARGUMENT`、逆順は REST と同じ扱い
   （fills・drift-adoptions は空、buy-in-inferences・session-uptime は `INVALID_ARGUMENT`）。Program.cs は 2 行
   （`AddAiStockTradingGrpcListener` と `MapGrpcService`）と using 1 行・csproj は参照 1 行。REST 面は不変。
4. **消費側**: `RiskManagement:Grpc`（宛先）を宣言したときだけ生成クライアントを 1 本（singleton）登録し、各ポートの工場が
   `GetService` で有無を見て `Grpc*` 実装を選ぶ（LlmGateway の段 1′ と同じ差し込み方）。宣言してあるのに使えない値は**起動時に落とす**
   （段 1 と同じ）。deadline の既定は各サービスの現行 `HttpClient.Timeout`（Report 10 秒・TD 5 秒・MM 5 秒）、試行回数の既定 1。
5. **解釈の単一化**: 行の検証（#943 / #957 の「識別できない行」「数量が正でない」等）は REST のアダプタの解釈をそのまま使う
   （`internal static` へ切り出して両輸送から呼ぶ）。gRPC は proto を**同じ nullable の行の形**へ写すだけにする。
6. **報告書の依存先の門と観測（#840）**: `risk-ledger` の `ReportDependencyHandler` と同じ判定（トークンが取れなければ送らない・
   失敗を一過性／恒常へ分ける）を gRPC の呼び出しでも行う。

## 受け入れ基準（#997）

- [x] proto と生成クライアント・サーバ実装。RiskManagementService に `MapGrpcService` 1 件。REST 面は不変
- [x] 呼び出し元 3 サービスが `RiskManagement:Grpc` の有無で切り替え、既定は REST。**本番の Program.cs から**解決して固定する
- [x] 原則 A: nullable だった項目の欠落が 0・false・空へ化けないことを項目ごとに試験で固定する
- [x] Report の門と観測が gRPC でも働く
- [x] proto 互換検査器の baseline を更新し、陰性対照（番号の付け替え）で赤になる
- [x] 結合試験（実 Kestrel h2c）で timeout / retry の陽性・陰性対照
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` と文書系検査器が通る

## テスト方針（テスト ID は T-10-1050〜T-10-1059。段 1 の試験は ID を持たないため、Risk の統制の仕様書に置く）

| ID | 置き場 | 観点 |
| --- | --- | --- |
| T-10-1050 | RiskManagementService.Tests | gRPC が REST と同じ値を返す（8 rpc。同じ評価器） |
| T-10-1051 | 〃 | 認可（未認証 `UNAUTHENTICATED`・ロール不足 `PERMISSION_DENIED`）と `from`・`to` の検証 |
| T-10-1052 | 各消費側 | 建玉（3 消費者）: 欠落・未指定が「保有なし」「ライン 0」へ化けない |
| T-10-1053 | TradeDecisionService.Tests | サイジング文脈: 残枠・資金の欠落は不明のまま、連敗数・DD・モード・上限の欠落は安全既定 |
| T-10-1054 | 〃 | 未約定の新規建て注文: 欠落は不明（「無い」へ化けない） |
| T-10-1055 | ReportService.Tests | 約定・取り込み・強制買戻し・稼働率・段階: 欠落と未指定の写し（未供給と空を分ける） |
| T-10-1056 | 各消費側 | 契約: 送り手の本物の型 → 提供側の写し → 受け手の写し の往復で値が保たれる |
| T-10-1057 | 各消費側 | 配線: 本番の Program.cs で宣言あり → `Grpc*`、無し → 従来、不正 → 起動時に例外。提供側は `MapGrpcService` を解決できる |
| T-10-1058 | 各消費側（結合） | 実 Kestrel h2c で deadline・再試行（陽性・陰性） |
| T-10-1059 | ReportService.Tests | 門と観測: トークンが取れなければ送らず未供給、失敗の一過性／恒常の分類 |

## 計画書との差異

- 差異: なし（射程・順序・一括移行の義務は IADR-0284 決定 1・2 と `MSP/ADR-0075` のまま）。
  決定 5 の段 2 行の読み方（Notification の GET を段 5 へ・追加 2 本を段 2 へ）は実装側の解釈であり、IADR-0427 決定 1 と
  IADR-0284 の日付つき追記に残す。

## 未決事項

- 稼働クラスタでの h2c 往復は未実測（段 1 と同じ。既定を変えないため）。
- 実効構成の自己申告（introspection）の `AddPortFromBaseUrl` は REST の構成しか見ない（段 1・段 1′ と同じ既存の欠落）。
  gRPC だけを宣言した構成では実装名を誤って申告する。段 6 までに別 issue で扱う。

## 着手後に判明した制約（母集合の引き直し。規則 10）

| 判明したこと | 実測 | 扱い |
| --- | --- | --- |
| proto の message 名を送り手の型名（`LedgerFill`）にすると、送り手の型による契約テストの判定（Architecture.Tests `CrossServiceReadContractTests`）が報告書の約定を「契約テストなし」と判定する | 一度そう名付けて同検査が赤（`HttpPeriodFillSource.GetFillsAsync -> /risk-controls/fills`） | 送り手の型名と衝突する 4 つ（`LedgerFill`・`OpenPosition`・`WorkingEntryOrder`・`BuyInInference`）を `PeriodFill` / `*Row` へ改名（IADR-0427 決定 3） |
| 報告書の `risk-ledger` は HttpClient の最外に門と観測（#840）を持ち、gRPC はその鎖を通らない | `Program.cs` の `AddReportDependencyGate("risk-ledger", …)` | 報告書の輸送が同じ判定を持つ（IADR-0427 決定 6・T-10-1059） |
| 取引判断・費用統制は段 1 の `GrpcChannel` を singleton で DI に持ち `GetRequiredService<GrpcChannel>()` で引く | `AssumptionsClientExtensions` | 輸送がチャネルを所有し、`GrpcChannel` を裸で登録しない（IADR-0427 決定 4） |
| 報告書の型 `OpenDUptimeRecord.Stage1CumulativeCountedDays` は `int?`（「供給しないなら null・0 と書かない」）だが、REST の受け手の DTO は `int` | `Domain/OpenDUptimeRecord.cs` と `HttpOpenDUptimeSource` | gRPC は欠落を null のまま運ぶ（REST の解釈は共有しない）。REST 側の同型の欠落は別 issue（IADR-0427 決定 3 の 🔴） |
| 段 2 以外の段で呼び出し元が増えている | 報告書 `HttpStopLossMethodUsageSource`（Audit・#823）、通知 `HttpPositionDriftAdoptionController`（Risk OwnerOnly・#871） | 本 PR では扱わない。IADR-0284 の追記に「各段は着手時に引き直す」と残した |

## 検証の記録（2026-09-25）

- `dotnet test`（`Category!=Integration`）: RiskManagementService・TradeDecisionService・MarketMonitorService・ReportService・Architecture.Tests
  の件数と結果は PR 本文に載せる。
- 変異注入 9 件（1 つずつ入れて実行し、実行ごとに書き戻した）はテスト仕様書の本節の表に載せた。
- `node scripts/check-proto-contracts.js`: `--update` 後 OK。陰性対照（`OpenPositionRow.entry_price` の番号を 5 → 9）で `[breaking]`。
- 文書系の検査器は Volta の shim を避けて実体の node（22.23.2）を本リポジトリの作業ツリーで実行した（shim を別リポジトリから
  呼ぶと `process.cwd()` 相対の走査をする検査器が別リポジトリを走査する —— 実際に一度そうなり、基盤側の破損リンクを拾った）。
