using JasperFx.CodeGeneration;
using Wolverine;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR-01, IADR-0129 決定 6-2, #816: **ホストを実際に起動するテスト**を、プロセスの環境変数
// `WOLVERINE_TYPE_LOAD_MODE` から独立させる。
//
// なぜ要るのか（#816 の実測）: 共通配線 `UseAiStockTradingRabbitMq` は、呼び出し側が読み込み方式を
// 決めていないとき（`TypeLoadModeHasChanged` が偽）だけ環境変数を読む。稼働イメージと同じ `Static` を
// 環境に置いたまま本プロジェクトを走らせると、生成コードをコミットしない本リポでは起動時の表明
// （`WolverinePreGeneratedCodeAssertion`。決定 6-3）が `MissingTypeException` を投げ、**テストが固定して
// いる性質とは無関係に** 3 件が落ちた（PR #815 が直した 1 件と同型）。
//
// 🔴 **プロセス環境変数の退避・復元では直さない。** 退避・復元はプロセス全体の状態を触るため、
// 使うクラスを `ProcessEnvironmentCollection`（並列化なし）へ入れる必要がある。読み込み方式を
// **呼び出し側で明示する**ほうが射程が 1 ホストに閉じ、並列実行も保てる。
// 退避・復元が要るのは「環境変数を読む枝そのもの」を固定する
// `WolverineTypeLoadModeTests.共通配線の既定は_Dynamic_である` だけであり、そちらは据え置く。
internal static class WolverineTestOptions
{
    /// <summary>
    /// 読み込み方式を dev/test の既定（<see cref="TypeLoadMode.Dynamic"/>）へ固定する。
    /// 共通配線より**前に**呼ぶこと（`UseAiStockTradingRabbitMq` は明示設定を上書きしない）。
    /// </summary>
    public static WolverineOptions PinDynamicTypeLoadMode(this WolverineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 代入そのものが JasperFx の TypeLoadModeHasChanged を立てる（＝環境変数の枝へ入らない）。
        // 既定値と同じ値を代入しても立つことは #816 の実走（WOLVERINE_TYPE_LOAD_MODE=Static）で確かめた。
        options.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;

        return options;
    }
}
