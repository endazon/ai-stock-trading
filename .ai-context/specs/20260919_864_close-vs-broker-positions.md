---
title: 決済（Close）の発注をブローカーの実建玉と突き合わせ、保有 0 からの売り（裸のショート）を出さない
type: spec
status: accepted
related_ids: [FR-10, FR-05, FR-11, FR-09, UC-02, UC-06, ADR-0016, ADR-0003, IADR-0355, IADR-0118, IADR-0119, IADR-0210, IADR-0211, IADR-0117, IADR-0350, IADR-0351]
author: endazon (with Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10「手仕舞い・損切りは統制で止めない」・FR-05)
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_broker-adapter-and-paper-trading.md (空売りの禁止・ショート建玉の規律)
---

# 仕様書: 決済の発注をブローカーの実建玉と突き合わせる（#864）

## 起点

- #864（#860 の監査が見つけた出口側の穴。**コードで確認・未発生**）。
- 決済（`PositionEffect.Close`）の注文は、**ブローカーの実建玉と一度も突き合わされずに発注される**。
- 台帳とブローカーが乖離していると（#849。2026-09-18 に実際に発生し、**台帳 3,381 株 / ブローカー 0 株**になった）、
  決済注文は**ブローカー上では保有 0 からの売り＝裸の新規ショート**になる。空売りは方針で禁止されており、
  ショート建玉の規律も空売り固有の統制も、この経路では一切効かない（注文が「決済」として通るため）。

## 原因（現物で確認した）

自分で引いた結果である（記憶で挙げていない）。

| # | 事実 | 引いた場所 |
| --- | --- | --- |
| 1 | 決済の数量の出所は**台帳の射影**（`GET /risk-controls/open-positions`）であり、ブローカーの事実ではない | `PositionEffectResolver` / `TradeDecisionAppService`（IADR-0119 決定1・IADR-0351 決定6） |
| 2 | `isEntry = (PositionEffect == Open)` により Close は**発注前スクリーニングを素通り**する（FR-10 の実装であり、これ自体は正しい） | `RiskEvaluator` / `OrderScreeningService`（IADR-0004） |
| 3 | 🔴 **発注執行の `DispatchApprovedOrder` 配下に `IBrokerPositionSource` の参照が 0 件**である（発注前の建玉照合が無い） | `grep -rl IBrokerPositionSource backend` の結果は `ProtectiveStopGuard` / `BrokerPositionSnapshotService` / `MoomooBrokerAdapter` / `Program.cs` / 契約のポートだけで、`DispatchApprovedOrder/` は 1 件も無い |
| 4 | 利用者の手仕舞い API（`POST /risk-controls/positions/close`）も同じ台帳を根拠にするので、**同じ穴**を持つ | `PositionCloseService` |
| 5 | 損切りの実行は**ブローカー側の逆指値**であり、`StopLossTriggered` の購読は決済注文を**発行しない** | `StopLossTriggeredHandler`（「決済はブローカー側の逆指値が実行・システムは発注しない」・IADR-0210 決定5） |

事実 5 は本件の fail-safe の向きを決める材料である（後述）。

## 決定（詳細は IADR-0355）

1. **照合の場所は発注執行の 1 か所**（`OrderExecutionAppService.ExecuteAsync`）。決済を起こす経路（判断由来・
   利用者の手仕舞い・維持率割れの自動縮小）はすべてここへ集まるため、経路ごとに門を置かない。
   **予約（3 相の相 2）より前**に判定する（送らないと決めたら予約も取らない＝既存の「逆指値を張れない Open は
   予約の前に見送る」と同じ位置）。
2. **実建玉が注文数量に満たないときは、実建玉の範囲へ縮めて送る。0 なら送らない。**
   縮小は実在する建玉を手仕舞うだけであり、裸のショートを構造的に作れない。見送りに倒すと、
   実在する 100 株の手仕舞いまで塞いでしまう（FR-10 に正面から反する）。
3. **建玉を照会できない（`null`＝不明）ときは送らない**（見送り・`BrokerPositionsIndeterminate`）。
   選ばなかった側（不明でも送る）の害と、選んだ側の害は IADR-0355 決定3 に両方書く。
4. **能力の無いブローカー（内蔵 paper）では照合しない**（従来どおり）。判定は `IBrokerPositionSource` が
   DI に在るかどうか（既存の「構造的な非干渉」と同じ表現。`Program.cs` は moomoo 構成でだけ登録する）。
5. **乖離を見つけたら既存の `PositionReconciliationDrift` を発行する**（監査・Critical 通知の経路を再利用。
   新しい通知経路を発明しない）。

## 値の出所（自分で引いた結果）

| 項目 | 出所 | 取得できないとき |
| --- | --- | --- |
| ブローカーの実建玉（符号付き） | 既存ポート `IBrokerPositionSource.GetPositionsAsync`（moomoo アダプタが実装。**空列＝建玉ゼロ／null＝不明**を厳格に区別する契約） | **不明**（null）→ 見送り |
| 決済方向の実建玉 | `Sell` の決済はロング（`net > 0`）・`Buy` の決済はショート（`-net > 0`）。反対方向の建玉は 0 として扱う | — |
| 台帳側の数量 | 承認が運ぶ `intent.Quantity`（＝台帳の射影から導かれた決済数量） | — |

## 母集合（着手前に引いた。規則 9・10）

**誤りの側の文字列で全走査した結果**であり、記憶で挙げていない。

| 引いた語 | 当たり | 扱い |
| --- | --- | --- |
| `IBrokerPositionSource` | 契約ポート / `MoomooBrokerAdapter` / `BrokerPositionSnapshotService` / `ProtectiveStopGuard` / `Program.cs` / `PositionReconciliationOptions` | 参照側は不変。`Program.cs` に発注執行への配線を 1 か所足す |
| `OrderDispatchForgoneReason`（列挙の全参照） | 契約の列挙 / `NotificationFormatter.ReasonLabel` / `BusinessMetrics.RecordOrderDispatchForgone`（タグ） / `AuditEntryFactory`（列挙名をそのまま要約へ） / `NotificationFormatterTests` の `InlineData` | **末尾へ 2 値追加**（序数 4・5）。ラベルとテストの `InlineData` を追随させる。監査・メトリクスは列挙名／タグで自動追随 |
| 「保有 0 からの売り」「裸のショート」 | `.ai-context/adr/IADR-0351`（本文・索引行の「残る制約」。**対策は #864** と書いてある） | 本 PR で塞いだので**日付つき追記**を 1 行入れ、索引行も同じ日付で追随させる（凍結記録の本文プローズは書き換えない） |
| docs の「手仕舞い（Close）は統制で止めない」の表（スクリーニング「通さない」） | `docs/functional/FR-10_risk-controls.md` | **統制（スクリーニング）は通らないまま**だが、**発注執行に建玉の門ができた**ので節を追記する（表の「通さない」は正しいので変えない） |
| `T-10-4\d\d`（既存の最大値） | `T-10-482` | 新規は `T-10-494` から採る |
| `SoftwareStopExecutor` | `.ai-context/specs/20260919_848_...` の対象外リストのみ（**develop に型は無い**。#830 が未マージ） | 前例としては引けないので、**develop に在る前例**（`ProtectiveStopGuard` / `BrokerPositionSnapshotService`）で判断する |

**除外した理由**: `.ai-context/specs/` と `.ai-context/superpowers/` の確定済み記録は本文を書き換えない（凍結）。
`CHANGELOG.md` は自動生成。`docs/api/openapi.yaml` は本変更で API が増減しないため対象外。

## 受け入れ基準

| # | 基準（#864 記載） | 写像 |
| --- | --- | --- |
| 1 | 台帳に建玉があり、ブローカーに無い状態で Close を発注しても、**ブローカーへ売り注文が出ない**（または実建玉の範囲に収まる） | T-10-494（0 株＝発注 0 回）・T-10-495（部分＝実建玉の数量で 1 回） |
| 2 | その事実が監査・通知に残る | T-10-496（見送りイベント＋乖離イベントが出る）・T-10-500（通知のラベル） |
| 3 | 台帳とブローカーが一致している通常時は**挙動が変わらない** | T-10-497（一致・過剰保有・paper・Open） |
| 4 | 照会不能のときの扱いが IADR に理由つきで残る | IADR-0355 決定3（選ばなかった側の害を明記） |
| 5 | 🔴 既存の「二重決済でショート化しない」テスト（T-10-402 / 403 / 406 / 407 / 408）が**緑のまま** | 全テスト実走の出力で示す |

## 監査の指摘の反映（#873・2026-09-19）

| # | 指摘 | 反映 |
| --- | --- | --- |
| B1 | 決定3 の根拠①「損切りはブローカー側の逆指値が担う」は、**逆指値を持たない 2 種類の建玉**（S2 で建てた建玉・乖離の取り込みでできた建玉）に当てはまらない。それらは照会不能のあいだ出口が無い | 根拠①を「ブローカー側逆指値を持つ建玉については」と限定。IADR-0355 の残余リスクと `docs/functional/FR-10_risk-controls.md` に明記し、**追随 issue #879 を起票**（決定3 自体は覆さない。覆すと裸のショートを許すため裁定が要る） |
| N1 | `Evaluate` が同一 (銘柄, 市場) を**符号付きでネット**するため、両建てでは正当な決済を止める（実測: ロング +300・ショート −100 でショートの買い戻しが「建玉なし」・ロングの売り決済が 200 株へ縮む） | **方向ごとに数える**形へ改め、回帰テスト T-10-506 を追加。報告する乖離の数量だけはネット（定期突合と同じ物差し） |
| N2 | `IBrokerPositionSource` が例外を投げると `ExecuteAsync` ごと落ちる | 例外も**不明へ寄せる**（`try/catch` → `Indeterminate`）。IADR の残余リスクに記録 |
| N3 | 縮小で削られた残数量が 30 分の窓で「処理中の決済」として押さえられ、再手仕舞いと取り込みを塞ぐ | IADR の残余リスクに記録（恒久ロックではないが、直後に人が動かせない時間がある） |
| N6 | 序数 4・5 が機械に固定されていない | `StopLossMethodContractTests` に序数と総数（6）の assertion を追加（T-10-507） |
| N7 | `DriftOf` の `BrokerOnly` 分岐が到達不能 | 分岐を削り、**到達しない理由**をコメントに残した |
| 採番 | 併走の #830（S1）が T-10-483〜493 を先に使う | 本 PR を **T-10-494〜505** へ振り直し（追加分は T-10-506・507） |

## 変更しないもの

- `RiskEvaluator` / `OrderScreeningService` の `isEntry`（決済は統制で止めない。FR-10）。
- 損切りの実行機構（ブローカー側逆指値）と `ProtectiveStopGuard`（**既に建玉と突き合わせている**）。
- 保護逆指値が張れなかった建玉の成行手仕舞い（`CloseUnprotectedPositionAsync`）。根拠が**ブローカーが返した
  約定数量そのもの**であり、台帳ではない（ここに門を置くと、いま作った無保護の建玉を解消できなくなる）。
- 乖離の検知・取り込み（`position_drift_state` / 取り込み API）。**イベントを再利用するだけ**で、
  取り込みの前提になる「報告済み」の追跡状態は動かさない（リスク管理は本イベントを購読していない）。
- DB スキーマ・EF マイグレーション・API・Helm/values。

## 作業手順

1. 失敗するテストを先に書く（是正前は赤になることを実走で確かめる）。
2. 契約（列挙 2 値）→ 判定の純関数 → 発注執行の結線 → 発行（ハンドラ）→ 通知ラベル → DI の順に実装する。
3. `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` / 静的検査を実走し、出力を PR へ貼る。
4. 仕様書・IADR・索引行・docs（機能仕様書・テスト仕様書）を追随させる。
