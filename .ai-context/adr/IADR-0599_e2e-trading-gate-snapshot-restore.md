---
title: IADR-0599 live の取引影響層（trading）の門は市場の閉場を Node で AST と同じ規則で判定し、in-flight は情報源が BFF に無いので経路（#1502）が入るまで常に不成立とする。スナップショット・突合・復元の本体は scripts に 1 つ置き、Playwright と単独実行で共有する
type: impl-adr
status: Accepted
related_ids: [NFR, SC-01, SC-02, IADR-0565, IADR-0588, IADR-0245, IADR-0380, MSP:IADR-0569]
author: claude (Claude Code)
created: 2026-10-30
updated: 2026-10-30
plan_refs:
  - MSP#2013 コメント 3 §5.2・§5.3（閉場の判定・取り消しと復元の手順）
related_specs:
  - ../specs/20261030_1418_e2e-trading-gate-snapshot-restore.md
---

# IADR-0599: live の取引影響層の門とスナップショット・突合・復元（#1418 / E-60）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-30
- 決定者: claude が起案（起点 #1418＝MSP#2013 の分割 E-60。親 #1410）。in-flight の扱いはオーケストレーターの判断（案 A：情報源が無いことを記録し、経路は子 issue #1502 へ分ける）。

## 起点・関連

- 起点 issue: #1418（E-60）。子 issue: #1502（in-flight を BFF から読む経路）。
- 実装する決定: IADR-0565 決定 2（閉場判定を `trading` の globalSetup に置き、開場中・in-flight ありなら exit 2）・決定 3（スナップショット・復元・差分が残れば赤）。同 IADR が「後続の変更が置く」とした実体である。
- 前提: IADR-0588（live の config・取引オーナーの state・readiness の門・teardown の失効）。MSP:IADR-0569（層・exit 2 の意味）。
- 規則の出所: `MarketSessions`（IADR-0245）・`MarketHolidays`（IADR-0380 決定 4）。

## コンテキスト

- 設計は「`GET /bff/risk-controls/status` の inFlight を読む」と書く。しかし `RiskStatusView` に in-flight に当たる欄は無い。近い口（risk-management の `/risk-controls/working-entry-orders`、order-execution の `/order-execution/in-flight-closes`）は、AST の BFF が中継していない（`RiskControlsBffEndpoints.cs` は経路を明示で列挙している）。live の E2E は BFF しか通れない。
- 市場時間と休場日の規則は C#（共有カーネル）にある。Playwright の globalSetup は Node で走るので、C# を呼べない。
- Playwright が kill されると globalTeardown は走らず、変えた値が戻らない。

## 決定

### 決定 1: 市場の閉場の門は `scripts/e2e-market-closed.js` に、AST と同じ規則を Node で写して置く

- 判定は「全対象市場が閉場しており、次の開場まで 60 分以上ある」。対象市場の既定は米国（PoC が取引している市場）。東証も指定できる（休場日は週末だけ＝C# と同じ射程）。
- 実装は「いま〜いま＋59 分の各分が、どの対象市場でも場中でない」で判定する。次の開場の時刻を逆算しない。タイムゾーンの逆変換を持たずに済み、夏時間の切替の日も同じ関数で正しい。ちょうど 60 分前は通す。
- 現地時刻への写像は `Intl.DateTimeFormat` のタイムゾーン（America/New_York・Asia/Tokyo）で行う。固定のオフセットで換算しない。
- 規則を 2 か所に置くことになるので、境界は `MarketSessionsTests` と同じ値で `scripts.repo.test.js` に固定する（前場・後場・閉場・半日・休場の 5 ケース）。2022〜2035 年の米国の休場日・半日取引日を C# の `MarketHolidays` と突き合わせ、170 日が一致することを実装時に確かめた（記録は作業仕様書）。
- CLI（`--now` で時刻を与えられる）としても使える。exit 0＝通す、exit 2＝前提未整備。

### 決定 2: in-flight の門は、情報源（#1502）が入るまで常に不成立とする

- 判断できないものを「無い」と読んで書き込みを始めない。in-flight の判定は、理由「BFF から読めない。#1502 で読み取り経路が入るまで trading 層は流さない」を返す関数として**門のスクリプト（`frontend/e2e/live/support/snapshot.ts`）**に置く。
- 仕様書・IADR の注意書きや、spec の skip 条件にはしない（skip の緑と区別がつかなくなる。門の exit 2 だけが「流れなかった」を正しく伝える）。
- #1502 は、この関数を「未約定の新規建てと処理中の決済がどちらも 0 件」の照会へ差し替える（照会の失敗・不明は不成立のまま）。

### 決定 3: スナップショット・突合・復元の本体は `scripts/e2e-trading-restore.js` に 1 つ置き、Playwright と単独実行で共有する

- 本体（GET の一覧・項目の取り出し・差分・復元・要約）は CommonJS で `scripts/` に置く。Playwright の globalSetup / globalTeardown（`snapshot.ts` / `restore.ts`）は `createRequire` で同じものを読む。中断時の単独実行（`node scripts/e2e-trading-restore.js --snapshot <file> --live`）も同じ本体を使い、要求の口は frontend の依存の Playwright の `APIRequestContext` である（storageState と証明書の扱いが live の config と同じになる）。
- 突合は項目ごとに行い、**同値の保存でも動く欄は比べない**（全体前提条件の版、閉場前の統制とテーマの保存値の有無と由来。閉場前の統制とテーマは効いている値だけを比べる）。同値の書き込みのテスト（A-026・A-048）が差分にならないためである。
- 復元するのは、要求の形が単純で、戻すことが取引を新たに動かさない項目に限る: リスク上限・Stage 1 最小件数・変動閾値・クールダウン・監視銘柄（増えた銘柄の削除と、消えた銘柄の足し戻し。個別操作で、全置換しない）。理由は `[e2e <runId> restore]`。
- 🔴 発注先・損切り機構は復元しない（送らない。IADR-0565 決定 1）。取引ガード・段階・高ボラティリティ・閉場前の統制・テーマ・全体前提条件も、推測で全置換の PUT を組み立てないために復元しない。これらに差分が残れば赤にして、オーナーが画面で戻す。
- 突合の後も差分か復元の失敗が残れば赤。globalTeardown は `process.exit` せずに例外を投げる。こうすると Playwright は、後ろの teardown（取引オーナーの失効）を走らせたうえで実行を失敗にする。単独実行は exit 1。差分の中身は成果物 `trading-restore-<runId>.json` に残し、ログには項目名だけを出す。

### 決定 4: config の配線

- `E2E_LAYER` に `trading` があるときだけ、globalSetup を `[readiness, snapshot]`、globalTeardown を `[restore, teardown]` にする（Playwright の配列は書いた順に走る。teardown も同じ順＝復元が失効より先）。
- runId は `E2E_RUN_ID`（英数字とハイフン 40 字以内）か時刻から作り、スナップショットの場所とともに環境変数で spec の worker と teardown へ渡す。

## 結果

- 良い点: 開場中・まもなく開場・in-flight の不明では、trading 層の spec は 1 本も流れない。変えた値は突合して戻り、戻らなければ必ず赤になる。中断しても単独で戻せる。
- 悪い点: 市場の規則を C# と Node の 2 か所に持つ（境界の試験と、実装時の全日の突合で固定した）。
- 残余リスク:
  - #1502 が入るまで、取引影響層は流れない（意図どおり）。
  - 復元しない項目に差分が残ったときは、人が画面で戻す必要がある。
  - 東証の祝日は C# と同じく射程外（週末だけ）。PoC の対象は米国。
