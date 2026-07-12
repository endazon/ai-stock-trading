using Xunit;

// issue #82 / IADR-0049: 統合テストは環境変数（プロセスグローバル）で実基盤の接続情報を注入する
// （Worker の Program が CreateBuilder 時点で接続文字列/キュー/ブローカを読むため）。将来 Category=Integration の
// テストクラスを追加したとき、xUnit 既定のコレクション間並列実行で環境変数が競合しないよう、本アセンブリの
// 並列実行を無効化する（claude-review 指摘の予防的対処）。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
