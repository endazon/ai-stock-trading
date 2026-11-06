---
title: IADR-0622 ボットの資格情報で届く REST の所有者の操作は、ボットの REST アダプタが呼ぶ経路の許可表とボット専用のロール trading-bot で絞る（段 1 は記録のみ・段 2 で拒否。gRPC の門はロールの条件を広げるだけ）
type: impl-adr
status: Accepted
related_ids: [NFR-06, FR-14, FR-10, UC-06, ADR-0047, IADR-0615, IADR-0611, IADR-0448, IADR-0449, IADR-0011, IADR-0098]
author: claude (Claude Code)
created: 2026-11-06
updated: 2026-11-06
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-06・FR-14「設定値の変更は Discord からは参照のみ」)
  - planning:projects/ai-stock-trading/07_adr/ADR-0047_discord-bot-token-is-service-identity-east-west.md (決定 1・3)
---

# IADR-0622: ボットの REST の許可表とボット専用のロール

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-11-06
- 決定者: オーナー裁定（2026-11-05。IADR-0615 決定 7「B → C」）／ オーケストレーター（設計の裁定 2026-11-06: 許可表は 21 本・基盤への起票・段の構成を分ける・REST は退避先に残す）／ Claude Code（起案・実装）

## 起点・関連

- issue: #1554。基盤のレルムの変更: MSP#2441。
- 前提: IADR-0615 決定 7（B）・IADR-0611（REST の所有者の判定の aud / azp）・IADR-0448 / IADR-0449（gRPC の所有者の面の azp）・ADR-0047（ボットのトークンはサービスの身元）。
- 実装仕様書: [`.ai-context/specs/20261106_1554_owner-bot-scope.md`](../specs/20261106_1554_owner-bot-scope.md)

## コンテキストと課題

ボットの機密クライアント（`ai-stock-trading-owner`）は、サービスアカウントにロール `trading-owner` だけを持つ。

- gRPC の所有者の面は azp がボットのクライアントであることを求め、中身もボットの操作だけである。
- REST の所有者限定の面はロール（と #1541 の aud / azp の許可集合。ボットの azp は含まれる）で通る。
- その結果、ボットの資格情報で、ボットが使わない REST の所有者の操作まで届く（リスク上限・取引ガード・設定・コスト・監視の設定・発注執行の照会・情報収集・監査など、8 サービスの所有者の群）。

FR-14 の「設定値の変更は Discord からは参照のみ」を、コマンドは守っているが資格情報は守っていない。

## 検討した選択肢

1. **経路の許可表（エンドポイントの印）＋ボット専用のロール（採用）** — 提供側の判定器 1 つで全サービスに効く。ボットのクライアントは azp で見分けられる（gRPC の門と同じ集合）。ロールの追加は誰も持っていないので、配備中の通信を止めずに段を進められる。
2. ロールだけを分ける（`trading-bot` を各経路のポリシーに書く） — ポリシーを経路ごとに分ける必要があり、OwnerOnly のグループの構造を崩す。許可表の漏れを機械的に突き合わせにくい。
3. ボットの REST を閉じて gRPC だけにする — REST は gRPC の不達時の退避先でもあるため、今は閉じない（裁定 Q4）。

## 決定

1. **許可表 `OwnerBotRoutes`**（共有の shim）を、ボットの REST アダプタが呼ぶ（メソッド, 経路）の 21 組とする。提供側は該当の経路に `.AllowOwnerBot()` の印を付ける。
2. **判定**（`OwnerTokenBindingHandler`。OwnerOnly・OwnerOrService の所有者の分岐）。
   - ボットのクライアント（azp が `Auth:GrpcOwnerClients`。既定 `ai-stock-trading-owner`）の所有者のトークンが、印の無い経路へ来た場合: `Auth:OwnerBot:Mode=Report`（段 1・既定）は Warning を残して通す。`Enforce`（段 2）は通さない。
   - **`trading-bot` は、ボットのクライアントかつ印の付いた経路でだけ通す**（段に関係なく、それ以外は拒む）。
   - そのうえで #1541 の aud / azp の判定を行う。段は `Auth:OwnerToken:Mode` で別に持ち、片方ずつ段 2 へ上げられる。
   - 利用者（BFF）のトークンとサービスの分岐は変えない。
3. ポリシーのロールの条件に `trading-bot` を足す。通すかどうかは 2 の判定器が決める。
4. **gRPC の所有者の面は、ロールの条件を `trading-owner` または `trading-bot` に広げるだけ**とする（azp の条件は従来どおり）。
5. chart は `global.ownerBotScope.mode` を、auth のサービスへ `Auth__OwnerBot__Mode` で描く（Report / Enforce 以外は描画で止める）。
6. 許可表の漏れを試験で止める。
   - ボットの REST アダプタの源泉から引いた経路 ＝ 許可表（`OwnerBotRouteCoverageTests`）。
   - 各提供側の本物の組み立ての印の付いた経路 ＝ 許可表のその提供側の分（`OwnerBotRoutesMarkedTests`）。
   - 印は 3 つの提供側の源泉にだけ現れ、数は許可表と同じ。
7. **段**:
   - ① 本決定（提供側がロールを受け入れ、許可表の外を記録する）。
   - ② 基盤のレルムで、ボットのクライアントに `trading-bot` を付ける（MSP#2441）。
   - ③ 段 1 の記録に「ボットの許可表の外」が出ないことを確かめる。
   - ④ `global.ownerBotScope.mode=Enforce`。
   - ⑤ 基盤のレルムで、ボットのクライアントから `trading-owner` を外す（MSP#2441）。
   - 単体実行向けのレルムの写しは ② の形に揃えた。

## 結果

- 良い影響: 段 2 の後は、ボットの資格情報が漏れても、届くのはボットが使う 21 の経路と gRPC の所有者の面だけになる。設定値の変更・発注先の切り替えには届かない。⑤ の後は、ボットのトークンは所有者のロールを持たない。
- 悪い影響: ボットに新しい REST の経路を足すたびに、許可表と印の更新が要る（試験が止める）。

## 残余・フォローアップ

1. ③ の確認と ④ の切り替えは、オーナーの判断で行う（記録の Warning に「ボットの許可表の外」が出ないこと）。
2. ⑤ の後、`trading-bot` だけを持つボットのトークンは、印の付いた経路と gRPC の所有者の面にしか届かない。⑤ の後に AST を本決定より前の版へ戻すと、ボットの操作は 403 になる（戻すなら ⑤ も戻す）。
3. 承認の面（IADR-0615 決定 7 の C）は別に行う。
4. 許可表の経路の中にも、ボットが使わない入力の形がある（例: `/reports` の旧一覧）。経路の単位で絞るまでとし、入力の形では絞らない。
