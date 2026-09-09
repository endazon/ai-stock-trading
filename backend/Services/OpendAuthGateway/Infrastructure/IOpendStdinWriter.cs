namespace OpendAuthGateway.Infrastructure;

/// <summary>#722, IADR-0320: FIFO への書き込み結果。</summary>
public enum StdinWriteOutcome
{
    /// <summary>1 行を書き終えた。</summary>
    Written,

    /// <summary>
    /// 読み手が居ない（FIFO が無い／<c>open</c> が <c>ENXIO</c>）。OpenD が落ちている状態であり、
    /// <b>待たずに 503 を返す</b>（塞がると呼び出し元の BFF ごと詰まる）。
    /// </summary>
    NoReader,

    /// <summary>この環境では FIFO へ書けない（Linux 以外・権限不足・I/O エラー）。</summary>
    Unavailable,
}

/// <summary>
/// #722, IADR-0320 決定 2: OpenD の標準入力（FIFO）へ<b>組み立て済みの 1 行</b>を書く口。
/// <para>
/// 実装は <see cref="FifoOpendStdinWriter"/> ただ 1 つで、試験は差し替えて
/// 「棄却されたときに<b>1 バイトも書かれない</b>こと」を観測する。
/// </para>
/// </summary>
public interface IOpendStdinWriter
{
    /// <summary>組み立て済みの 1 行（末尾 <c>\n</c> 込み・ASCII）を書く。</summary>
    StdinWriteOutcome WriteLine(string line);
}
