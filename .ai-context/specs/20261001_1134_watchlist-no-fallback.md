---
title: 監視銘柄の権威源が読めないときは、取引判断の定時サイクルを構成の既定 watchlist へ倒さず、直前に読めた一覧か「不明」として見送る（#1134）
type: spec
status: accepted
related_ids: [FR-02, FR-13, FR-04, UC-01, ADR-0044, IADR-0475, IADR-0095, IADR-0435, IADR-0440, IADR-0446, IADR-0282, IADR-0352]
author: claude (Claude Code)
created: 2026-10-01
updated: 2026-10-01
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0044 (決定 2: 監視銘柄の一覧は権威源〔市場監視〕から読み、読めなければ「不明」)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-02 定時サイクル・FR-13 監視銘柄の変更)
---

# 監視銘柄の権威源が読めないときに構成の既定へ倒さない（#1134）

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-02（定時サイクルの判断対象）・FR-13（監視銘柄の変更が判断へ届く）・FR-04（判断のプロンプトの監視銘柄の節。本件では変えない）
- 計画 ADR: ADR-0044（監視銘柄の権威源は市場監視。読めなければ「不明」と書き、空の一覧や別の一覧で代えない）
- 関連する実装ADR: IADR-0095（決定 3: 照会失敗は構成の既定へ倒す＝**本件で改める**）／IADR-0435（情報収集の直前値・初回の固定リスト）／IADR-0440（プロンプトの口は不明を返す）／IADR-0446（gRPC 実装）／IADR-0282（初回シード）／IADR-0352（報告書の見送りと縮退）
- 新規 IADR: IADR-0475
- 起票: [#1134](https://github.com/endazon/ai-stock-trading/issues/1134)（low）

## 観測（issue 本文）

クラスタの一斉再起動（2026-09-30 09:59 UTC）の直後:

- trade-decision: 「監視銘柄（watchlist）を権威源から読めないため、既定 watchlist（構成）へフォールバックします」
- information-collection: 「サービストークンの取得で例外」× 11・「KB 保存に失敗（401）」× 3。Finnhub の対象は構成の固定リスト（AAPL のみ）
- report: 「生成を見送り … 1/5 回目」の後、10:01 に入力未供給のまま生成
- 閉場中のため実害なし。

問題: 場中に全 Pod が同時に再起動すると、判断対象が構成の既定 watchlist に切り替わる。ADR-0044（権威源は市場監視）と食い違う。

## 母集合（規則 9。origin/develop 58730639）

誤りの側の文字列で引いた。

1. `git grep -n "既定 watchlist" -- backend docs deploy ':!.ai-context/specs'`
   - 本体: `TradeDecisionService/Infrastructure/ExternalServices/HttpWatchlistProvider.cs`（注釈・警告文）・`GrpcWatchlistProvider.cs`（警告文）・`ConfigurationWatchlistProvider.cs`（注釈）・`Program.cs` 341 行（注釈）
   - 試験: `HttpWatchlistProviderTests.cs`・`MarketMonitorReadContractTests.cs`・`WatchlistProviderSelectionTests.cs`（注釈）・`MarketMonitorService/Tests/.../ReadContractWireFormatTests.cs`（注釈「既定 watchlist へ倒れる」）
   - 文書: `docs/screens/20260718_SC-02_risk-settings.md` 266 行・`docs/tests/FR-10_risk-controls-tests.md` 3670 行（T-10-1549。「既定 watchlist を使わない」＝プロンプトの口。真のまま）
2. `git grep -n -i "fallback" -- 'backend/Services/*/Features' 'backend/Services/*/Infrastructure' 'backend/Services/*/Program.cs' | grep -i -E "watch|監視銘柄"`
   → 取引判断の 3 ファイル（Http・Grpc・Configuration）と `Program.cs` の DI（`configFallback`）、情報収集の `CollectionSourceOptions.cs`（注釈）だけ。
3. 監視銘柄を読む消費側（`git grep -l "IWatchlistProvider\|IWatchlistReader\|IMarketMonitorWatchlistController\|IAsOfWatchlistSource" -- backend ':!*/Tests/*'`）:

| 消費側 | 読めないときの現行 | 判断に効くか | 本件の扱い |
| --- | --- | --- | --- |
| 取引判断の定時サイクル（`InformationCollectedHandler` → `IWatchlistProvider.GetWatchlistAsync`） | **構成 `TradeCycle:Watchlist` へ倒す** | **効く（判断対象そのもの）** | **改める**（直前に読めた一覧、無ければ不明＝見送り） |
| 取引判断のプロンプトの節（`GetAuthoritativeWatchlistAsync`） | null（不明） | 効く（節の中身） | 変えない（ADR-0044 決定 2 どおり。直前値も使わない） |
| Stage 0 の当時の監視銘柄（`IAsOfWatchlistSource`） | 再構成できない＝合否から外す | 記録のみ | 変えない（構成へ倒す経路は無い） |
| 情報収集の Finnhub 対象（`FinnhubSymbolSelector` ← `IWatchlistReader`） | 直前に読めた集合。一度も読めていなければ構成の固定リスト | **効かない**（収集のみ。判断対象は取引判断が決める） | **変えない**（下の「決定」の理由） |
| 通知の `/policy`（`IMarketMonitorWatchlistController`） | 照会失敗＝「適用できない案」 | 効かない | 変えない（固定リストを持たない） |
| 市場監視自身（権威源）・`MonitorSeedOptions` | — | — | 対象外（構成は初回シードであり代替ではない） |
| 報告書 | 監視銘柄を読まない（方針の改訂案の入れ替えは市場監視へ書くだけ） | — | 対象外 |

4. 構成の固定リストを「フォールバック」と書く運用文書: `git grep -n -E "フォールバック|fail-safe" -- deploy docs | grep -E "watchlist|監視銘柄|TradeCycle"`
   → `deploy/helm/ai-stock-trading/README.md` 203・509・511・527・532 行、`values.yaml` 717 行、`values-local.yaml` 146・345・430 行、`docs/screens/20260718_SC-02_risk-settings.md` 266 行。
   情報収集の「一度も読めていないときだけのフォールバック」（README 312・`values.yaml` 359・`values-local.yaml` 182）は本件で変えないので真のまま。
5. 永続の直前値は既にあるか: `git grep -n -i -E "last.?known|直前に読めた" -- backend ':!*/Tests/*'` → 情報収集の `FinnhubSymbolSelector._lastKnown`（プロセス内）だけ。取引判断には無い。永続のもの（DB・ファイル）はどこにも無い。

除外: `.ai-context/specs/`（凍結記録）・`.ai-context/adr/IADR-0095` の本文（凍結。追記で改める）・`CHANGELOG.md`。

## 決定（要約。正は IADR-0475）

1. **取引判断の定時サイクルは、権威源が読めないとき構成の既定へ倒さない。**
   - このプロセスで一度でも読めていれば、**直前に読めた一覧**（定時サイクルの寛容な読みの結果。空も事実として含む）で判断を続ける。
   - 一度も読めていなければ **不明**（`GetWatchlistAsync` が null）を返し、`InformationCollectedHandler` は**そのサイクルの判断をしない**。
   - 「直前に読めた一覧」は singleton の `WatchlistLastKnown`（プロセス内。供給口はスコープごとに作られるため外に置く）。永続化はしない（再起動の後は不明から始まる＝安全側）。
   - 警告は**障害ごとに 1 回**（読めた → 読めないへ変わったとき）。回復は Information で 1 回。照会そのものの失敗（非 2xx・例外・打ち切り）は従来どおり照会ごとに Warning（供給口の既存の行）。
2. **未結線（`MarketMonitor:BaseUrl` も `MarketMonitor:Grpc` も無い）では従来どおり構成が対象**（後方互換・権威源が無いので代替ではない）。
3. **プロンプトの口は変えない**（読めなければ null。直前の一覧も載せない＝「判断時点の監視銘柄」とは書けない）。
4. **情報収集は変えない**: 直前値は既にある（IADR-0435）。初回の固定リストは収集にしか効かず、取引判断は初回は不明で見送る（収集した銘柄が判断へ渡る経路が無い）。収集を見送る形は、Finnhub の 2 ソースが 0 銘柄を「取得成功・0 件」と返す（ニュースを「取得済み」と偽る）か、失敗に倒すと再起動のたびに「ニュース系の全滅」へ倒れ、別の設計変更が要る。→ オーナー確認事項に挙げる。
5. 報告書（「見送り 1/5 回目」→ 縮退版の生成）は IADR-0352 の設計どおり（見送りの上限・生成窓・未供給の明示）。本件の範囲外。
6. 情報収集のサービストークンの例外・KB の 401 は、トークンの取得失敗をキャッシュしない（次の要求で取り直す）ため自己回復する。認証の作り直しはしない（残余）。

## 窓（規則 11）

窓 = 「権威源を最後に読めた時刻（前の端）」と「いまのサイクル（後の端）」の間。プローブ（増える側・減る側）と形 3 通りを実測で突き合わせた（`WatchlistLastKnownTests` と変異）。

- 形 A（後の端だけ）: 今回読めなければ常に見送る（直前値を持たない）
- 形 B（前の端だけ）: 今回の読みを見ず、読めなければ「前の端」の値（是正前は構成＝起動時の既定）を使う
- 形 C（両端）: 今回読めれば今回の値。読めなければ直前に読めた値。一度も読めていなければ不明（**採用**）

| プローブ | 期待 | A | B（是正前＝構成） | C |
| --- | --- | --- | --- | --- |
| P1 コールドスタートで読めない（一斉再起動。#1134） | 構成の銘柄を判断しない・見送る | ○ | ×（構成の AAPL を判断） | ○ |
| P2 読めた後の一過性の打ち切り 1 回 | 直前の一覧で判断を続ける（全銘柄の 1 サイクル欠落を作らない） | ×（全銘柄見送り） | ×（構成へ切り替わる） | ○ |
| P3 増える側: 読めない間に銘柄が追加された | 追加分は読めるまで判断しない（安全側） | ○ | ○ | ○ |
| P4 減る側: 読めない間に銘柄が削除された | 削除分を判断し続けない | ○ | ×（構成に残っていれば判断） | △（直前の一覧に残る。下の残余） |
| P5 回復: 読めるようになった | 新しい一覧で再開し、以後の直前値も新しい一覧 | ○ | ○ | ○ |
| P6 読めて 0 件の後に読めない | 0 件（判断しない）を保つ。構成へ倒さない | ○ | ×（構成へ倒す） | ○ |

- C の P4 は「市場監視は動いていて変更を受け付けたが、取引判断からだけ読めない」窓に限られる。市場監視そのものが落ちている間は変更も受け付けない（変更の入口は市場監視の API・SC-02・Discord の `/policy` のいずれも市場監視へ書く）。プロンプトの節はこの間「不明」と書く。
- A は P2 で一過性の 5 秒の打ち切り 1 回が全銘柄の判断を 1 サイクル落とす。C を採る。

## 試験（T-10-1990〜T-10-1999）

| ID | 内容 |
| --- | --- |
| T-10-1990 | 一度も読めていない（503・例外・打ち切り・`null` 応答・`[null]`）なら定時サイクルの口は null（不明）。構成を参照しない |
| T-10-1991 | 読めたら読めた一覧（寛容な読みの結果）を返し、直前値として覚える |
| T-10-1992 | 読めた後に読めなければ直前に読めた一覧を返す |
| T-10-1993 | 回復すると新しい一覧を返し、以後の直前値も新しい一覧になる |
| T-10-1994 | 読めて 0 件の後に読めなければ 0 件（不明でも構成でもない） |
| T-10-1995 | 警告は障害ごとに 1 回（連続の失敗で積もらない）、回復で Information 1 回、次の障害で再び警告 |
| T-10-1996 | gRPC 実装も同じ（初回は不明、読めた後は直前値。提供側の 1 回目成功・2 回目失敗） |
| T-10-1997 | 定時サイクルのハンドラは不明なら判断も発行もしない（例外にしない） |
| T-10-1998 | 本番の組み立てで `WatchlistLastKnown` は singleton（スコープを跨いで同じ）。別スコープの供給口が直前値を返す |
| T-10-1999 | プロンプトの口は直前値を使わない（読めた後に読めなくなっても null） |

### 既存の試験の改め（規則 10。旧挙動を表明していたもの）

| 試験 | 旧の表明 | 改め |
| --- | --- | --- |
| `HttpWatchlistProviderTests` の「未取得 404／非 2xx 403／例外／不正 null 応答／タイムアウトは既定 watchlist へフォールバックする」5 本 | 構成の一覧を返す | 一度も読めていなければ null（T-10-1990） |
| 同「定時サイクル用の口は null の行を含む応答を従来どおり既定へ倒す」 | 構成の一覧・fallback 1 回 | null（T-10-1990） |
| 同 `CountingFallback`／`FakeFallback` を使う試験 | 「fallback を呼ばない」 | 供給口は fallback を持たなくなった。直前値の箱を渡す |
| `MarketMonitorReadContractTests` の `RecordingFallback` | 「既定 watchlist へ倒さない」 | 同上（読めた一覧の表明は不変） |
| `GrpcStage4ReadsTests.T_10_1694_読めなければ定時サイクルは構成の監視銘柄_プロンプトは不明` | 構成の一覧 | null（名前も改める） |
| `WatchlistInDecisionPromptTests` T-10-1544 | 定時サイクルの口は `STALEFIXED`（構成）を返す | null（プロンプトに構成が載らない表明は不変） |
| 偽の `IWatchlistProvider`（`InformationCollectedConsumerTests`・`WatchlistInDecisionPromptTests`） | 戻り値が非 null | 戻り値を null 許容へ |

## 自己変異（いずれも赤になること）

実測（`dotnet test` を監視銘柄・定時サイクル・gRPC・契約の試験に絞って変異ごとに実行。8/8 赤）:

| # | 変異 | 赤になる試験 |
| --- | --- | --- |
| M1 | 読めなければ構成へ倒す（是正前の形・形 B） | T-10-1990（8 例）・T-10-1995・T-10-1996・T-10-1544 |
| M2 | 直前値を覚えない（形 A） | T-10-1991・T-10-1992・T-10-1993・T-10-1994・T-10-1995・T-10-1996・T-10-1999 |
| M3 | 直前値を初回の値のまま更新しない | T-10-1993 |
| M4 | ハンドラが不明を空として扱わず例外にする（`!` で読み進める） | T-10-1997 |
| M5 | `WatchlistLastKnown` を scoped で登録 | T-10-1998 |
| M6 | 警告を毎回出す（障害ごとの 1 回にしない） | T-10-1995 |
| M7 | プロンプトの口も直前値へ倒す | T-10-1999 |
| M8 | 空の一覧を直前値として覚えない（0 件を「読めていない」と扱う） | T-10-1994 |

## 追随する文書

- `docs/screens/20260718_SC-02_risk-settings.md`（供給不達時の扱い）
- `docs/operations/operations.md` 障害対応（一斉再起動の直後に定時サイクルが判断しない）
- `deploy/helm/ai-stock-trading/README.md`・`values.yaml`・`values-local.yaml`（`TradeCycle:Watchlist` は未結線のときだけの対象）
- `docs/tests/FR-10_risk-controls-tests.md`（T-10-1990〜T-10-1999・trace ブロック）
- `.ai-context/adr/IADR-0095`（日付つき追記）・索引行・IADR-0475 の新設と索引行

## 残余・オーナー確認事項

- **情報収集のコールドスタートの固定リスト**: 判断には効かないため残した（上の決定 4）。収集も「不明は見送る」に揃えるなら、Finnhub の 2 ソースに「対象が不明」の失敗を足し、それを「ニュース系の全滅」と区別する設計が要る（別 issue）。
- **P4（減る側）**: 取引判断からだけ市場監視を読めない窓では、削除された銘柄を直前の一覧で判断し続ける。期限（例: 直前値を N 分で捨てる）は足していない。足すなら値をオーナーに決めてもらう。
- **メトリクス**: 定時サイクルの「不明で見送った」を数えるメトリクスは足していない（ログの警告のみ。取引判断に監視銘柄の出所のメトリクスの前例が無い）。
- **報告書の見送りの上限**（観測では 2 分後に縮退版を生成）は IADR-0352 の設計（見送りの上限・生成窓）であり本件の範囲外。
- **サービストークンの取得の例外**（Keycloak の起動待ち）は失敗をキャッシュせず次の要求で取り直すので自己回復する。依存の readiness を起動時に待つ形（issue の案 2）は採っていない。
