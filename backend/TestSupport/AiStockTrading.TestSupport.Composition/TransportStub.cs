using System.Reflection;

namespace AiStockTrading.TestSupport.Composition;

/// <summary>
/// NFR, #947, IADR-0397: 組み立てガードが<b>伝送の境界</b>（外界へ出る最下層のクライアント）だけを差し替えるための空の実装。
/// どのメンバを呼んでも <see cref="NotSupportedException"/> を投げる —— ガードは組み立てを作って辿るだけで、
/// 外界へは 1 度も出ない（呼ばれたらガード側の前提が壊れている）。
/// <para>
/// 伝送より上（アダプタ・業務クラス）は本物のまま組ませる。上を偽物にすると、偽物の上の配線しか検査できない。
/// </para>
/// </summary>
public class TransportStub : DispatchProxy
{
    /// <summary><typeparamref name="T"/>（インタフェース）の空の実装を作る。</summary>
    public static T Create<T>()
        where T : class => Create<T, TransportStub>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        throw new NotSupportedException(
            $"組み立てガードは外界へ出ない（{targetMethod?.DeclaringType?.Name}.{targetMethod?.Name} が呼ばれた）");
}
