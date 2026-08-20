---
title: 強制買戻しの推定件数を報告サービスへ供給する照会 API と、観測の到達の記録（FR-21）
type: spec
status: approved
related_ids: [FR-10, FR-06, FR-21, FR-11, UC-06, ADR-0016, IADR-0159, IADR-0162, IADR-0176, IADR-0181]
author: endazon (with Claude Code)
created: 2026-08-08
updated: 2026-08-08
---

# 仕様書: 強制買戻しの推定件数の照会 API と観測の到達の記録

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#463](https://github.com/endazon/ai-stock-trading/issues/463)（**計画が名指しで起票を指示した唯一の項目**）
- 起点 ID: **FR-10**（リスク統制）／**FR-06**（報告書）／**FR-21**（観測の到達の記録）／**FR-11**／**UC-06**
- 起点の裁定: **ADR-0016 決定15 の 2026-08-07 確定**（planning `c2998a6`。質問票 第 14 回 Q9・裁定依頼 project-planning#241）

> **推定はリスク管理サービス、報告書は報告サービスで動き、両者を結ぶ照会 API が無い。**（…）
> - **この中間状態を許容する。** 日報・月報は「照会できませんでした（供給元がありません）」と表示し、**0 件とは書かない**。
> - **許容は恒久の免除ではない。** **実装側は照会 API を別 issue として起票すること。**

- **FR-21**（`planning/projects/ai-stock-trading/02_requirements/01_requirements.md` 行 50・**Must**・2026-08-07 新設）:

> **ブローカ建玉の観測が到達した事実を記録する**（最終観測時刻の永続化）。（…）**台帳だけでは「観測が一度も届いていない（異常）」と「観測は届いたが 0 件だった（正常）」を区別できない**。**観測の到達を別に記録して初めて、件数を正当な 0 として供給できる。**

## 実装の現状（実測 2026-08-08・`develop` = `f25edda`）

| 側 | 状態 | 実測した根拠 |
| --- | --- | --- |
| 推定（リスク管理） | **実装済み** | `IBuyInInferenceStore` / `EfBuyInInferenceStore`（台帳 `buy_in_inferences`）。`CountInferredBetween` / `GetInferredBetween` が既にある |
| 照会 API | **無い** | `RiskControlEndpoints.cs` に推定台帳を返す経路が無い |
| 報告書（報告サービス） | **常に未供給** | `Program.cs:141` が `UnsuppliedBuyInInferenceRecordSource`（常に `null`）を登録 |
| **FR-21（観測の到達の記録）** | **🔴 未実装** | `LastObserved` 系の永続化はコード全体で `Stage1SessionUptime`（別概念）のみ。`docs/blocked-tasks.md:441` が「**未起票**。『観測が届いた事実』を記録する経路が要る」と記録している |

**FR-21 が無い限り、照会 API を足しても受け入れ基準を満たせない。** 台帳が空のとき、
「観測が一度も届いていない（統制がまったく働いていない）」と「観測して 0 件だった（正常）」を
区別できず、**前者を 0 件と表示するのは計画が名指しで禁じた向き**である。
issue #463 自身も「供給が入ったことを理由に FR-21 を省くこと」を 🔴 として挙げている。

**したがって本作業は FR-21 の記録側を含む。**

## 決定

### 決定1: 観測の到達を単一行で永続化する（FR-21）

`BrokerPositionsObserved` が届いた事実（`ObservedAt`）を永続化する。計画の文言は
「**最終観測時刻の永続化**」であり、期間ごとの記録ではなく**単一の最終観測時刻**である。

- 新ポート `IPositionObservationArrivalStore`（`GetLastObservedAt()` / `Record(DateTimeOffset)`）。
- **単調前進のみ**（後着の古い観測で巻き戻さない）。順序保証の無いバスで巻き戻すと、
  「供給されていた」状態が後から「未供給」へ落ちる。
- 記録点は `BrokerPositionsObservedHandler`。**推定の有無に関わらず記録する** ——
  これが台帳（事象が起きたときにしか書かない）との唯一の違いであり、本要求の存在理由そのものである。
- 既定（未記録）は `null` ＝**未供給**（fail-safe）。

### 決定2: 照会は「観測の到達」と「推定行」を**同じ応答で**返す

別々の 2 エンドポイントにすると、呼び出し側が片方だけ見て 0 件と判断する経路が作れる。
**1 回の応答で両方を運び、判断を分離できないようにする。**

```
GET /risk-controls/buy-in-inferences?from=YYYY-MM-DD&to=YYYY-MM-DD
200 { "observationArrivedAt": <DateTimeOffset|null>, "inferences": [ ... ] }
```

- 認可は **`OwnerOrService`**（既存の読み取り系グループ。IADR-0051 / IADR-0176 と同水準）。
  **無認証の内部エンドポイントを増やさない。**
- `from` / `to` の省略・不正・逆順は **400**（黙って全期間を返さない）。

### 決定3: 報告側は「観測未到達」と「照会失敗」を**どちらも `null`** に倒す

`IBuyInInferenceRecordSource` の契約（`null`＝未供給）は既にそうなっている。HTTP アダプタは:

| 事象 | 返り値 |
| --- | --- |
| 通信断・タイムアウト・例外・非 2xx・不正応答 | **`null`**（未供給） |
| `observationArrivedAt` が `null`（観測が一度も届いていない） | **`null`**（未供給。**台帳が空でも 0 件にしない**） |
| `observationArrivedAt` があり推定 0 件 | **空列**（正当な 0） |
| `observationArrivedAt` があり推定あり | 推定行 |

**🔴 同居する `HttpPeriodFillSource` は失敗を空列へ倒す。向きが逆であることを実装コメントに明記する。**
あちらは報告書が発注判断を行わないため欠測が過大発注に繋がらないが、こちらは
**「強制買戻しは起きていない」という誤読が構造的に起こる**。同じファイル群の隣に逆向きの前例があるため、
後から「揃える」方向の整理で壊されやすい。

### 決定4: SC-03 への結線は**本作業に含めない**

`ShortSellingStatusService`（SC-03）も同じ理由で `NotSupplied` に固定されており、FR-21 が入れば
結線できるようになる。しかし **issue #463 の受け入れ基準は報告サービスの経路だけを対象としている**。
含めると本 PR の検査対象が広がり、SC-03 側の表示規約（IADR-0162）の再確認まで巻き込む。

**別 issue として起票する**（FR-21 の記録側が入ったことで解錠される旨を `docs/blocked-tasks.md` にも反映する）。

## 🔴 やってはいけないこと（issue の明示）

- **照会が失敗したときに空列（＝推定 0 件）へ倒すこと。**
- **供給が入ったことを理由に FR-21 を省くこと。**
- **照会エンドポイントを無認証にすること。**

## 計画への裁定依頼（起票済み・**裁定が下りて改定された**）

**FR-21 が定めるのは「最終観測時刻」という単一の値であり、報告期間を観測が覆っていたかは判定できない。**

例: 初回の観測が 2026-08-20 に届いた後、7 月分の月報を生成すると、
`observationArrivedAt` は非 null であるため **7 月の推定 0 件が「正当な 0」として表示される**。
実際には 7 月に観測は 1 度も届いていない。

- 期間ごとの観測到達を記録する（設計変更）のか、
- 最終観測時刻より前の期間は未供給とするのか、
- この粒度で許容するのか、

は**計画の裁定事項**である（実装で値・規則を発明しない）。

**［2026-08-08 追記］裁定が下りた。** [project-planning#292](https://github.com/endazon/project-planning/issues/292) に対し
**計画が FR-21 の粒度を「単一の最終観測時刻」から「観測が届いた取引日の集合（期間判定）」へ改定した**
（planning `d9c2014`）。受け入れ基準も「**報告期間が観測の届いた取引日で覆われている場合に限り**」へ改められ、
2 つの失敗モード（初回観測前の過去期間・途中停止）が明記された。**本作業は改定後の定義で実装する**
（決定1〜3 を改定へ追随させ、pin を `d9c2014` へ前進させた）。

## 影響範囲

| 層 | ファイル | 変更 |
| --- | --- | --- |
| Risk / Application | `Ports/IPositionObservationArrivalStore.cs`（新規） | FR-21 のポート |
| Risk / Infrastructure | `EfPositionObservationArrivalStore.cs`（新規）・`PersistenceRows.cs`・Migration | 単一行の永続化 |
| Risk / Infrastructure | `BrokerPositionsObservedHandler.cs` | 観測の到達を記録 |
| Risk / Api | `RiskControlEndpoints.cs`・`Program.cs` | 照会エンドポイント（`OwnerOrService`）・DI |
| Report / Infrastructure | `HttpBuyInInferenceRecordSource.cs`（新規） | HTTP アダプタ（失敗は `null`） |
| Report / Api | `Program.cs` | `Unsupplied…` を置き換え（BaseUrl 未設定なら `Unsupplied…` のまま＝fail-safe） |

## テスト（受け入れ基準の写像）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 報告サービスが推定件数を照会でき、通常表示になる | `HttpBuyInInferenceRecordSourceTests`（推定あり・観測到達あり） |
| 2 | **否定形（最重要）**: 照会失敗で `null`（0 件と表示しない） | 同（非 2xx / タイムアウト / 例外 / 不正応答の 4 経路） |
| 3 | **否定形**: 観測未到達なら台帳が空でも `null` | 同（`observationArrivedAt: null` ＋ 空列） |
| 4 | **否定形**: 無認証で叩けない | `RiskControlEndpoints` の認可テスト（既存の型に倣う） |
| 5 | FR-21: 推定の有無に関わらず観測の到達が記録される | `BrokerPositionsObservedHandler` のテスト（推定 0 件でも記録される） |
| 6 | FR-21: 単調前進（古い観測で巻き戻さない） | ストアのテスト |
| 7 | 観測到達あり＋推定 0 件は**空列**（正当な 0） | `HttpBuyInInferenceRecordSourceTests` |

## 対照実験（緑 → 赤 → 緑）

- `observationArrivedAt` の判定を外す（常に供給扱い）→ **否定形テストのみ赤**になることを実測する。
- 失敗時の返り値を `null` → `[]` に変える → **4 経路のテストが赤**になることを実測する。
- 単調前進のガードを外す → 巻き戻しテストのみ赤。

## 検証

- `dotnet build backend/backend.slnx`（0 Warning / 0 Error）／`dotnet test`／`dotnet format --verify-no-changes`
- CI ゲート一式
