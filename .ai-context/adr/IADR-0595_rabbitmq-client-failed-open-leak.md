---
title: IADR-0595 RabbitMQ.Client を 7.2.2 へ上げ、開き損ねた接続が裏で自動回復を続けて起動の再試行のたびに接続が漏れる欠陥を外す
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0013, IADR-0129, IADR-0591]
author: claude (Claude Code)
created: 2026-10-29
updated: 2026-10-29
related_specs:
  - ../specs/20261029_1482_rabbitmq-failed-open-connection-leak.md
---

# IADR-0595: RabbitMQ.Client を 7.2.2 へ上げて、開き損ねた接続の漏れを外す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-29
- 決定者: Claude Code（#1482 の根本原因の特定と是正の形）

## 起点・関連

- 起点: #1482（PoC 2026-10-26。ブローカの準備前に起動した Worker の Wolverine が開始を再試行し、準備後に AST の接続が 121 本〔平常 22 本〕で残った）。
- 計画: ADR-0013。前提: IADR-0129（Wolverine の共通配線）・IADR-0591。

## コンテキスト

Wolverine 6.24.5 はブローカの開始を 5 秒ごとに 20 回まで再試行し、毎回接続を作り直す（失敗した試行の参照は捨てる）。接続の自動回復は常に有効である。
RabbitMQ.Client 7.1.2 の `AutorecoveringConnection.CreateAsync` は回復を購読した後に内側の接続を開き、開くのに失敗しても自分を閉じない。開き損ねの shutdown
（Library・541）が自動回復を起動し、捨てられた接続が裏で繋ぎ直しを続けて、ブローカの準備が整った時点で一斉に繋がる（チャネル 0 のまま残る）。
7.2.2 は開き損ねたら閉じて破棄する（どちらも逆コンパイルで確認した）。

## 決定

- `Directory.Packages.props` の `RabbitMQ.Client` を 7.1.2 → **7.2.2** へ上げる（CPM の推移ピン留めにより全サービスへ効く）。Wolverine の版は変えない。
- 欠陥の再現を単体の試験で固定する（偽のブローカでハンドシェイクを切り、開き損ねた後に接続の試みが**増え続けない**こと）。版を下げる・同じ欠陥のある版へ戻すと赤になる。
  ［2026-10-29 追記 / PR #1491 の CI］7.2.2 でも、開き損ねの shutdown で起動した回復の巡回が、CreateAsync が自分を閉じて止めるまでの間に 1 回走る余地がある
  （回復は間隔を待ってから繋ぎ直す。混んだランナーで閉じるまでが間隔より長いと走る。CI で開く試みが 2 回を実測。MSP#2302 の実測も同じ）。
  よって判定は MSP#2302 と同じ形にする: 開いた直後の数 n は 1 か 2、回復の間隔の 30 倍（3 秒）待った後も n のまま（増えない）。
  手元で 7.1.2 は 40 回とも赤（待つ間に 29〜30 回まで増えた。無負荷 20 回・CPU 負荷 20 回）、7.2.2 は 40 回とも緑（同）。

### 推移ピン留めの理由と、基盤との乖離

- **理由は不具合の是正であり、脆弱性（CVE）ではない。** 上流の修正は 7.2.0 の rabbitmq-dotnet-client#1857
  （「fix: prevent possible connection leak in AutorecoveringConnection during creation」。issue rabbitmq-dotnet-client#1856
  「Possible connection leak when trying to create first connection」）。
- 7.1.2 → 7.2.2 の変更履歴（CHANGELOG の v7.2.0・v7.2.1・v7.2.2）に、本リポの使い方を壊す変更は無い。公開 API の追加（`SslStreamCertificateContext`・
  抽象化パッケージ・OpenTelemetry の親子関係）と不具合の是正（回復ループが Dispose で止まらない・AutorecoveringConnection のメモリリーク・
  発行確認のセマフォ・回復でのバインディングの復元・`basic.consume` の継続待ちの超過で consumer を失う、など）である。
  依存パッケージの組は同じ（nuspec で確認）、アセンブリの版は 7.0.0.0 のまま。Wolverine.RabbitMQ 6.24.5 が参照する RabbitMQ.Client の
  型・メンバ 142 件を 7.2.2 に対して解決し、欠けは無かった（不足 1 件は検査用のプロセスに ASP.NET の共有フレームワークが無いことによる）。
- 🔴 **基盤（MSP）は WolverineFx.RabbitMQ 6.24.4 の推移で 7.1.2 のまま**（2026-10-29 に `dotnet list package --include-transitive` で確認）。
  CLAUDE.md の「パッケージの版は基盤と揃える」から外れる**乖離**であり、本 IADR で受容する（基盤も同じ欠陥を持つため、揃えるなら基盤側を上げる）。
  基盤の追随は MSP#2301 で起票された。**MSP#2301 が追随したら** `Directory.Packages.props` の注記を外して揃っていることを確かめる。

## 却下した案

- **ブローカの準備を待ってから Wolverine を開始する**: 準備の判定は接続を開くことになり同じ欠陥に乗る。運用中にも初回の開き損ねは起き得る。
- **Wolverine の再試行を止める（1 回で落として Pod の再起動に任せる）**: 漏れはプロセスの終了で消えるが、起動の回復力を落とし、原因（ライブラリの欠陥）は残る。

## 結果

- 起動の再試行で接続が漏れない（実ブローカでの本数は CI の Integration E2E と PoC の再起動で確かめる）。
- 残余: MSP も RabbitMQ.Client 7.1.2（WolverineFx.RabbitMQ 6.24.4 の推移）で同じ潜在の欠陥を持つ（上の「基盤との乖離」）。MSP 側の是正は MSP#2301。
- 7.1 → 7.2 は同じ主版の更新である。API の変更でビルドは壊れていない（全単体試験・Architecture が緑）。
