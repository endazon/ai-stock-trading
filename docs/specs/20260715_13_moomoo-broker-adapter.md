---
title: moomoo ブローカアダプタ（SIMULATE・OpenD 経由）— Issue #13
type: spec
status: draft
related_ids:
  - FR-05
  - ADR-0002
  - IADR-0016
author: claude
created: 2026-07-15
updated: 2026-07-15
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md"
  - "../../planning/projects/ai-stock-trading/06_technical/03_moomoo-integration.md"
related_specs:
  - "20260714_124_opend-docker.md"
---

# 仕様書: moomoo ブローカアダプタ（Issue #13）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05（発注執行）／FR-12（ペーパー）
- 関連 ADR: **ADR-0002**（moomoo OpenAPI・OpenD 常駐）／**IADR-0016**（実弾防止・既定 paper）
- Issue: #13（moomoo アダプタ）／ #124（OpenD 常駐）

## 目的・背景

`IBrokerAdapter` の moomoo 実装を追加し、`Broker:Provider=moomoo` を解禁する（現状 `BrokerFactory` は例外で停止）。
発注は **OpenD（`opend:11111`）** 経由・**SIMULATE 限定**（`TrdEnv_Simulate`）。**実弾（`TrdEnv_Real`）は撃たない**。
SDK は NuGet `moomoo-api`（`MMAPI4Net` / `Moomoo.OpenApi.MMAPI_Trd`・protobuf コールバック型）。

## 対象範囲

**対象（本 PR）**
- `IMoomooTradeClient`（テスト可能な薄いポート）＋ SDK 非依存 DTO（`MoomooOrderRequest`/`MoomooOrderResult`）。
- `MoomooBrokerAdapter : IBrokerAdapter`: OrderIntent↔リクエストの写像・**SIMULATE 強制**・注文状態の写像。TDD（fake client）。
- `BrokerFactory` の moomoo 解禁（config ゲート）。`MoomooBrokerOptions`（OpenD host/port）。
- `MMApiMoomooTradeClient : IMoomooTradeClient`: 実 `MMAPI4Net` 結合（接続・口座取得・PlaceOrder/GetOrderList/
  ModifyOrder のコールバック→Task 相関）。**実 OpenD ＋ SIMULATE 口座での動作確認はユーザ環境（本セッションで実行不可）**。

**対象外**
- 実弾（`TrdEnv_Real`）・信用/差金決済・執行方針の高度化（マーケタブルリミットは初期実装、時間帯成行禁止は後続）。
- OpenD の起動/常駐（#124・常駐モデル）。

## 設計

- **シーム**: 検証可能なロジック（写像・SIMULATE 強制・状態変換）を `MoomooBrokerAdapter` に集約し fake `IMoomooTradeClient`
  で TDD。SDK 固有の protobuf/コールバック配線を `MMApiMoomooTradeClient` に隔離（実接続は live 検証）。
- **SIMULATE 強制**: `OrderIntent.Mode=Live` でも moomoo アダプタは `TrdEnv_Simulate` を用いる（本 PR は実弾を撃たない）。
  将来の実弾解禁は別 IADR＋明示 config で（IADR-0016 の後続）。
- **写像**: Market（Japan→`TrdMarket_JP` / UnitedStates→`TrdMarket_US`）・Side（Buy/Sell→`TrdSide_Buy/Sell`）・
  マーケタブルリミット（`OrderIntent.Price`）。状態: moomoo OrderStatus → `OrderStatus`（Submitted/Filling→Accepted/
  PartiallyFilled、FilledAll→Filled、Cancelled→Cancelled、Failed/Disabled→Rejected 等）。
- **fail-safe**: OpenD 未接続・エラーは `OrderStatus.Rejected` の終端注文で返す（PaperBrokerAdapter の不正注文と同様に
  フローを止めない）。実弾防止は BrokerFactory の config ゲート＋SIMULATE 強制で二重化。

## 受け入れ基準

- [x] `MoomooBrokerAdapter` が OrderIntent を SIMULATE リクエストへ写像し、client 応答を BrokerOrder へ変換（TDD）
- [x] Live 指定でも SIMULATE を用いる（実弾を撃たない）ことをテストで固定
- [x] `Broker:Provider=moomoo` が解禁され MoomooBrokerAdapter を返す（BrokerFactory テスト）
- [x] 実 OpenD（SIMULATE 口座）で発注→状態追跡→取消が動作（**2026-07-15 live 検証済**・#124 常駐 OpenD・RSA 暗号化）
      - 接続（暗号化）→ GetAccList で SIMULATE 口座 accId=724808 → PlaceOrder(AAPL x1 @1.00 buy)→ orderId 採番 →
        QueryOrder=Submitted → CancelOrder → Cancelled まで一巡を確認（port-forward `opend:11111`）。

## 実 OpenD 結合（live 検証で確定した SDK 事項・MMAPI4Net 10.8.6808）

- **接続**: `MMAPI.Init()` → `SetClientInfo/SetConnCallback/SetTrdCallback` → `InitConnect(host,port,encrypt)`。
- **暗号化必須（cross-network）**: moomoo は別 Pod 間（worker→opend）の trade 接続に **RSA 暗号化**を要求する
  （非暗号は OpenD が 127.0.0.1 listen のときのみ）。OpenD.xml `<rsa_private_key>`（PKCS#1 PEM ファイル）と
  クライアント `SetRSAPrivateKey(鍵の**内容**文字列)`＋`InitConnect(encrypt:true)` を同一鍵で対にする（#124 側で配備）。
- **口座取得**: `TrdGetAccList.C2S` は `userID` が protobuf required（`0`=現在ログインユーザ）。応答から
  `TrdEnv==TrdEnv_Simulate` の `AccID` を採用。
- **発注/取消**: `TrdPlaceOrder.C2S`／`TrdModifyOrder.C2S` は `packetID` が required（`MMAPI_Trd.NextPacketID()`）。
  `TrdHeader` に `TrdEnv_Simulate` + `AccID` + `TrdMarket` を設定。成否は `Response.RetType==0`。
- **相関**: 各リクエストは `uint nSerialNo` を返し、コールバック `OnReply_*(client,nSerialNo,Response)` で相関
  （`TaskCompletionSource` 辞書・15s タイムアウト）。

## テスト方針

- 単体（xUnit・20 件）: 写像（市場/売買/価格）・SIMULATE 強制・状態変換・fail-safe（client 例外→Rejected）・BrokerFactory 解禁。
- 実結合（`MMApiMoomooTradeClient`）は実 OpenD（SIMULATE）で live 検証済（2026-07-15・上記受け入れ基準）。
  常駐 OpenD 依存のため CI 単体には含めない（手動 opt-in で検証）。

## 計画書との差異

- 差異: なし（ADR-0002 の想定実装）。実弾は IADR-0016 の後続で別途解禁。

## 未決事項

- 執行方針（寄付き直後の成行禁止・実効スリッページ記録）は初期実装後の拡張。
- `MMApiMoomooTradeClient` の protobuf フィールド詳細は live 検証で確定・微調整。
