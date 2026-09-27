---
title: 東西 gRPC の読み取りの潜在の残り —— 未指定の市場の既定値化・停止中の倍率の解釈失敗・10 進の桁あふれを是正する
type: spec
status: done
related_ids: [FR-01, FR-02, FR-04, FR-10, NFR, IADR-0031, IADR-0095, IADR-0331, IADR-0427, IADR-0446, IADR-0447]
author: endazon (with Claude Code)
created: 2026-09-27
updated: 2026-09-27
---

# 仕様書: 東西 gRPC の読み取りの潜在の残り（#1063）

## 起点

- issue #1063（PR #1062 の監査で出た、止めない残り 4 件）。判断は [IADR-0447](../adr/IADR-0447_grpc-read-latent-residuals.md)。
- 計画 ID: FR-02・FR-04（定時サイクルの監視銘柄）、FR-01・NFR（費用統制ゲート）、FR-10（サイジング文脈）。

## 項目と方針

| 項目 | 事実（`origin/develop` `2fb284c1`） | 方針 | 既定の振る舞いの変化 |
| --- | --- | --- | --- |
| A | `HttpWatchlistProvider.ToCycleWatchlist` の `r.Market ?? default` が、市場の欠けた行を日本として定時サイクルの判断対象へ入れる。REST では値域外の番号もそのまま通る。T-10-1545・T-10-1694 がそれを表明していた | 市場の欠けた・値域外の行を**落とす**（銘柄の空の行と同じ扱い・読めた行で判断を続ける）。REST・gRPC とも共有の規則で | **意図した厳格化**。REST の定時サイクルで、市場の欠けた行・値域外の行が判断対象から外れる。実在の送り手（市場監視）は市場を 0 / 1 で必ず書くため、稼働中は起きない |
| B | 費用統制ゲートで、停止の旗が読めても倍率が読めない（文字列・桁あふれ・型違い）と、gRPC は倍率の解釈の例外で、REST は本文の一括逆直列化の失敗で Normal（停止せず）へ倒れる。#915 の規則（停止は倍率に関わらず守る）より弱い | 項目ごとに読み、読めない倍率は「倍率なし」として `Map` へ渡す（停止は守る） | **意図した厳格化**。REST・gRPC とも「停止の旗が true で倍率が読めない」応答で停止するようになる。実在の送り手（費用統制）は倍率を数値で必ず書くため、稼働中は起きない |
| C | 取引判断の gRPC の厳格な口の試験（T-10-1694）が銘柄の無い行も混ぜ、市場の写しを単独で確かめていない（PR #1062 の監査で変異が生き残った） | 市場だけが欠けた応答・銘柄だけが欠けた応答を別の試験にする | なし（試験のみ） |
| D | `RiskManagementWire.Decimal` の `decimal.Parse` の OverflowException を呼び出し元が捕まえていない（FormatException のみ） | 桁あふれも FormatException にそろえ、既存の捕捉で安全既定へ倒す | 例外が外へ出ていた場合だけが変わる（残枠 0・不明・未供給・既知の値へ倒れる）。稼働中は起きない |

### 母集合（D。規則 9: 誤りの側の文字列で全ファイルを走査）

`git grep -n 'decimal.Parse' -- 'backend/Services/*/Infrastructure/ExternalServices/*.cs'`（テスト以外）で引いた gRPC の受け手の 10 進の読み取り:

| ファイル | 段 | 扱い |
| --- | --- | --- |
| `TradeDecisionService/.../RiskManagementGrpcTransport.cs`（`RiskManagementWire.Decimal`） | 段 2 | 是正（issue の名指し） |
| `ReportService/.../RiskManagementGrpcTransport.cs`（同） | 段 2 | 是正（同型） |
| `MarketMonitorService/.../RiskManagementGrpcTransport.cs`（同） | 段 2 | 是正（同型） |
| `TradeDecisionService/.../GrpcAssumptionsClient.cs`（`FromWire`） | 段 1 | 是正（同型。捕捉は FormatException のみ） |
| `CostControlService/.../GrpcAssumptionsClient.cs`（同） | 段 1 | 是正（同型） |
| `InformationCollectionService/.../CostControlGrpcTransport.cs`（`CostControlWire.IntervalMultiplier`） | 段 4 | B で `TryParse` へ（PR #1062 で桁あふれは捕捉済み） |
| `OrderExecutionService/.../MoomooBrokerOptions.cs` | — | 除外（構成値の読み取り。gRPC の受け手ではない） |

［2026-09-27 追記 / #1065］上表の A の「実在の送り手（市場監視）は市場を 0 / 1 で必ず書くため、稼働中は起きない」は不正確だった。
市場監視の全置換（`PUT /monitor/settings`）は未定義の市場を保存でき、初回シードの構成も列挙名でない番号を通していた（到達し得た）。
#1065 で入口を閉じた（全置換は 400・シードの構成は起動時に止める）。B の REST の数値の文字列の書式（`NumberStyles.Float` が前後の空白を許した）も
#1065 で以前の Web 既定と同じに戻した。判断の訂正は IADR-0447 の同日の追記。

## 受け入れ基準

- [x] A: 市場の欠けた・値域外の行は定時サイクルの判断対象から外れる（REST・gRPC）。T-10-1545・T-10-1694 を改訂し、改訂を表明する
- [x] B: 停止の旗が読めて倍率が読めない応答で停止する（REST・gRPC）。停止していない応答の扱いと、読めない本文の扱いは従来どおり（同じ試験を是正前のコードでも実行して緑であることで示す）
- [x] C: 市場の写しを単独で確かめる試験がある
- [x] D: 桁あふれの 10 進で例外が外へ出ず、各口の安全既定へ倒れる（取引判断・報告書・市場監視・前提条件 2 つ）
- [x] 新しい試験は是正前のコードで赤・是正後で緑（実測）
- [x] helm の既定描画・values-local の描画は不変
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` と文書系検査器が通る

## テスト方針（テスト ID は T-10-1710〜T-10-1713。develop の最大 T-10-1698 から間を空けた区画）

| ID | 置き場 | 観点 |
| --- | --- | --- |
| T-10-1710 | TradeDecisionService.Tests（REST） | A: 市場の欠けた・値域外の行を定時サイクルが落とす（T-10-1545 の改訂を含む） |
| T-10-1711 | TradeDecisionService.Tests（gRPC） | C（と A の gRPC 側）: 市場だけ・銘柄だけが欠けた応答を別々に |
| T-10-1712 | InformationCollectionService.Tests（REST・gRPC） | B: 停止の旗が読めれば倍率が読めなくても停止。変わらないこと（停止していない・読めない本文）も表明 |
| T-10-1713 | 取引判断・報告書・市場監視・費用統制 | D: 桁あふれの 10 進で例外を外へ出さない |

## 検証の記録（2026-09-27）

- 是正前のコードでの実測（本番のコードだけを `origin/develop` の版へ差し替えて新しい試験を実行し、`HEAD` の版へ書き戻した）:
  取引判断 15 件中 11 件赤、情報収集 21 件中 5 件赤（停止していない応答・読めない本文の 7 件は緑＝不変）、報告書・市場監視・費用統制は各 1 件赤。
- 件数・検査器・helm の描画の一致は PR 本文。
