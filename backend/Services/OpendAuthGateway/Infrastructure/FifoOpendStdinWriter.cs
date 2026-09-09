using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using OpendAuthGateway.Common;

namespace OpendAuthGateway.Infrastructure;

/// <summary>
/// #722, IADR-0320 決定 2: OpenD の標準入力 FIFO（<c>/run/opend/stdin</c>）へ 1 行を書く実装。
/// <para>
/// 🔴 <b>fd をキャッシュしない。要求ごとに開いて閉じる。</b> OpenD が再起動すると
/// <c>entrypoint.sh</c> は FIFO を<b>消してから作り直す</b>ため、保持していた fd は
/// <b>誰も読まない孤児の inode</b> を指し続ける。書き込みは成功したように見えて、
/// OpenD には永久に届かない —— 検証コードが「入れたのに反応しない」という形で壊れる。
/// </para>
/// <para>
/// 🔴 <b><c>O_WRONLY | O_NONBLOCK</c> で開く。</b> 素の <c>O_WRONLY</c> は<b>読み手が現れるまで
/// <c>open</c> が塞がる</b>。OpenD が落ちている間の要求が全部そこで止まり、
/// 呼び出し元（BFF）のスレッドを道連れにする。<c>O_NONBLOCK</c> なら読み手が居なければ
/// 即座に <c>ENXIO</c> で返るので、<see cref="StdinWriteOutcome.NoReader"/> として 503 にできる。
/// </para>
/// <para>
/// 🔴 <b>1 回の <c>write</c> で 1 行を書く。</b> <c>PIPE_BUF</c>（Linux で 4096）以下の書き込みは
/// 不可分であり、他の書き手と行が混ざらない。バッファを挟むと分割され得るので
/// <c>bufferSize: 0</c> の <see cref="FileStream"/> を使う。
/// </para>
/// </summary>
public sealed class FifoOpendStdinWriter(IOptions<OpendAuthOptions> options, ILogger<FifoOpendStdinWriter> logger)
    : IOpendStdinWriter
{
    private const int OWronly = 0x0001;

    /// <summary><c>O_NONBLOCK</c>（Linux の値。musl / glibc とも 0o4000）。</summary>
    private const int ONonblock = 0x0800;

    /// <summary><c>ENOENT</c>: FIFO そのものが無い（OpenD がまだ作っていない）。</summary>
    private const int Enoent = 2;

    /// <summary><c>ENXIO</c>: FIFO はあるが読み手が居ない（OpenD が落ちている）。</summary>
    private const int Enxio = 6;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenFifo(string pathname, int flags);

    public StdinWriteOutcome WriteLine(string line)
    {
        // 本サイドカーは Linux コンテナ専用である（OpenD イメージは Ubuntu 22.04）。
        // 開発機（Windows / macOS）で起動したときは黙って成功しない。
        if (!OperatingSystem.IsLinux())
        {
            logger.LogWarning("opend-auth: FIFO への書き込みは Linux でのみ行える（この環境では利用できない）。");
            return StdinWriteOutcome.Unavailable;
        }

        var path = options.Value.StdinFifoPath;
        var fd = OpenFifo(path, OWronly | ONonblock);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno is Enxio or Enoent)
            {
                // OpenD が動いていない。待たずに諦める（呼び出し元は 503 を受ける）。
                logger.LogWarning("opend-auth: OpenD の標準入力に読み手が居ない（errno={Errno}）。", errno);
                return StdinWriteOutcome.NoReader;
            }

            logger.LogError("opend-auth: 標準入力 FIFO を開けない（errno={Errno}）。", errno);
            return StdinWriteOutcome.Unavailable;
        }

        using var handle = new SafeFileHandle((nint)fd, ownsHandle: true);
        try
        {
            // bufferSize: 0 ＝ 無バッファ。Write は 1 回の write(2) になる。
            using var stream = new FileStream(handle, FileAccess.Write, bufferSize: 0);
            stream.Write(Encoding.ASCII.GetBytes(line));
            stream.Flush();
            return StdinWriteOutcome.Written;
        }
        catch (IOException ex)
        {
            // 例外の内容に投入値は含まれない（line は組み立て済みだがログへ出さない）。
            logger.LogError(ex, "opend-auth: 標準入力 FIFO への書き込みに失敗した。");
            return StdinWriteOutcome.Unavailable;
        }
    }
}
