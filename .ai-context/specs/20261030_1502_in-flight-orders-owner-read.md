---
title: live の取引影響層の in-flight の門のために、承認済みで終端していない注文を risk-management の OwnerOnly の口と BFF の GET で読めるようにし、門を差し替える（#1502）
type: spec
status: done
related_ids: [NFR, IADR-0599, IADR-0600, IADR-0346, IADR-0529, IADR-0071]
author: claude (Claude Code)
created: 2026-10-30
updated: 2026-10-30
plan_refs:
  - MSP#2013 コメント 3 §5.2
---

# 仕様書: in-flight の門の情報源（#1502）

> 本仕様書は実装着手前に作成した（着手 2026-10-30）。基点は AST `origin/develop` `e0a22701`。門の差し替えは #1503（E-60）のマージ後に develop を取り込んでから行う。

## 起点

- #1502（#1418 / E-60 の子。親 #1410）。置き場は案 R（risk-management）をオーケストレーターが採った（2026-10-30）。
- 新規 IADR-0600。前提 IADR-0599 決定 2。

## 対象範囲

- risk-management:
  - `Features/RiskManagement/IInFlightOrderSource.cs`（供給源の口と、最小の欄の型）
  - `Infrastructure/Persistence/EfInFlightOrderSource.cs`・`InMemoryInFlightOrderSource.cs`
  - `Features/RiskManagement/GetInFlightOrders/`（`Endpoint.cs`・`InFlightOrdersService.cs`〔窓 2 日の名前付き定数〕・`InFlightOrderView.cs`）
  - `RiskControlEndpoints.cs`（OwnerOnly のグループへ登録）・`Program.cs`（DI）
- BFF: `backend/Bff/AiStockTrading.Bff.Endpoints/RiskControlsBffEndpoints.cs`（GET 1 本）。
- 門: `frontend/e2e/live/support/snapshot.ts` の in-flight の判定を照会へ差し替える（#1503 のマージ後）。
- 試験: risk-management（供給源の EF / InMemory の同じシナリオ・Npgsql の訳が UNION ALL・サービスの窓と写像・認可）、BFF（経路の表・素通し）、門の判定（L1）。
- 文書: IADR-0600、索引、`docs/tests/README.md` の「現状」（門が働くようになる）。
- 対象外: order-execution（全件の口は作らない）、MSP、画面、gRPC の読み取りの面（サービスの呼び手が無い）。

## 母集合（規則 9・10）

- BFF の経路の一覧を持つ箇所: `RiskControlsBffEndpoints.cs` と `BffPassThroughTests.cs` の `AllRoutes`（完全一致の試験がある）→ 両方へ足す。
- risk-management の読み取りの口の一覧: `RiskControlEndpoints.cs`（read / owner のグループ）→ owner へ足す。gRPC の読み取りの面（`risk_controls_read.proto`）はサービスの呼び手のための面で、OwnerOnly の口は載せない（除外）。
- 通信仕様書（`docs/api/openapi.yaml`）は risk-controls の経路を列挙していない（`working-entry-orders` も無い）ので足さない（除外）。
- 門の「#1502 が入るまで常に不成立」と書いた箇所（規則 10。#1503 の取り込み後に引き直す）: `snapshot.ts`・`live-trading-gate.spec.ts`・`playwright.live.config.ts` のコメント・`docs/tests/README.md` を直す（取り込み後に `git grep -E "#1502|IN_FLIGHT_SOURCE_MISSING|常に不成立|常に exit 2"` で引いた。`scripts/README.md` には該当の記述が無かった）。IADR-0599 は凍結記録で、IADR-0600 決定 4 が改める（索引で示す）。

## 受け入れ基準

1. 未約定の新規建てか処理中の決済が 1 件でもあれば、trading の門が exit 2 で終わる。どちらも 0 件で、市場が閉場していれば門を通る。
2. 照会の失敗・不明は不成立（exit 2）。
3. 追加する BFF の経路は GET だけで、取引オーナー以外は拒否する（後段の OwnerOnly の 403 をそのまま返す）。
4. 返す欄は銘柄・市場・種別・状態・承認時刻だけ（口座の識別子・数量・価格・金額・判断 ID を返さない）。
5. 窓は名前付きの定数（2 日）。窓の中の残骸で門が止まり得ることを IADR に書く。

## PoC 配備への影響

- risk-management と BFF（MSP の BFF が読み込む AST の BFF の部品）の再配備が要る。構成・スキーマの変更は無い（既存の表を読むだけ）。
- live の E2E の trading 層は、この再配備の後に門が照会できるようになる。それまでは門の照会が失敗して exit 2 になる（安全側）。
