using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpendAuthGateway.Common;
using OpendAuthGateway.Infrastructure;
using Xunit;

namespace OpendAuthGateway.Tests.Infrastructure;

/// <summary>
/// #722, IADR-0322 決定 2: 実 FIFO に対する書き込みの性質を固定する。
/// <para>
/// 🔴 <b>MSYS / Windows では FIFO の機序が再現できない</b>ため、Linux 以外では skip する
/// （偽の赤を出さない。<c>deploy/opend/entrypoint.test.sh</c> と同じ作法）。CI は ubuntu なので本体は必ず走る。
/// </para>
/// </summary>
public class FifoOpendStdinWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("opend-fifo-tests-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 後片付けの失敗で試験を落とさない。
        }

        GC.SuppressFinalize(this);
    }

    private FifoOpendStdinWriter CreateWriter()
    {
        var options = new OpendAuthOptions { RuntimeDirectory = _dir };
        return new FifoOpendStdinWriter(
            Options.Create(options),
            NullLogger<FifoOpendStdinWriter>.Instance);
    }

    private string FifoPath => Path.Combine(_dir, OpendAuthOptions.StdinFifoFileName);

    /// <summary>
    /// FIFO を作る。作れない環境（<c>mkfifo</c> が無い等）は skip する
    /// —— 機序が成立しない環境で偽の赤を出さない（entrypoint.test.sh と同じ能力探針の作法）。
    /// </summary>
    private void GivenFifo()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("mkfifo", [FifoPath]) { UseShellExecute = false })
                ?? throw new InvalidOperationException("mkfifo を起動できなかった。");
            process.WaitForExit();
            if (process.ExitCode != 0) Assert.Skip($"mkfifo が失敗した（exit={process.ExitCode}）。");
        }
        catch (SystemException ex)
        {
            Assert.Skip($"この環境では FIFO を作れない: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 読み手として FIFO を <c>O_RDWR</c> で開く（<c>entrypoint.sh</c> の <c>0&lt;&gt;</c> と同じ形）。
    /// これで書き手側の <c>open(O_WRONLY | O_NONBLOCK)</c> が <c>ENXIO</c> にならない。
    /// </summary>
    private FileStream OpenReader()
    {
        try
        {
            return new FileStream(FifoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (IOException ex)
        {
            Assert.Skip($"この環境では FIFO を読み手として開けない: {ex.GetType().Name}");
            throw;
        }
    }

    [Fact]
    public void 読み手が居れば1行が届く()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "FIFO の機序は Linux でしか再現できない");
        GivenFifo();
        using var reader = OpenReader();

        var outcome = CreateWriter().WriteLine("input_phone_verify_code -code=123456\n");

        outcome.Should().Be(StdinWriteOutcome.Written);
        ReadAvailable(reader).Should().Be("input_phone_verify_code -code=123456\n");
    }

    /// <summary>
    /// 🔴 <b>fd をキャッシュしていないこと。</b> OpenD が再起動すると <c>entrypoint.sh</c> は FIFO を
    /// 消して作り直すため、保持した fd は<b>誰も読まない孤児の inode</b> を指す。
    /// 書き込みは成功したように見えるのに OpenD へは永久に届かない、という壊れ方をする。
    /// </summary>
    [Fact]
    public void FIFOを作り直しても次の書き込みが届く()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "FIFO の機序は Linux でしか再現できない");
        GivenFifo();
        var writer = CreateWriter();
        using (var first = OpenReader())
        {
            writer.WriteLine("req_phone_verify_code\n").Should().Be(StdinWriteOutcome.Written);
            ReadAvailable(first).Should().Be("req_phone_verify_code\n");
        }

        // OpenD の再起動を模す（unlink → mkfifo）。
        File.Delete(FifoPath);
        GivenFifo();
        using var second = OpenReader();

        writer.WriteLine("input_pic_verify_code -code=ab12\n").Should().Be(StdinWriteOutcome.Written);
        ReadAvailable(second).Should().Be("input_pic_verify_code -code=ab12\n");
    }

    [Fact]
    public void FIFOが無ければ塞がらずに読み手不在を返す()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "FIFO の機序は Linux でしか再現できない");

        // OpenD が一度も起動していない状態。**待たずに**返ること（塞がると呼び出し元ごと詰まる）。
        var stopwatch = Stopwatch.StartNew();
        var outcome = CreateWriter().WriteLine("req_phone_verify_code\n");
        stopwatch.Stop();

        outcome.Should().Be(StdinWriteOutcome.NoReader);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void 読み手が居ないFIFOでも塞がらずに読み手不在を返す()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "FIFO の機序は Linux でしか再現できない");
        GivenFifo();

        // FIFO はあるが OpenD が落ちている状態＝ENXIO。O_NONBLOCK が無ければここで永久に塞がる。
        var stopwatch = Stopwatch.StartNew();
        var outcome = CreateWriter().WriteLine("req_phone_verify_code\n");
        stopwatch.Stop();

        outcome.Should().Be(StdinWriteOutcome.NoReader);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Linux以外では利用不可を返す()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "この検査は Linux 以外の環境の振る舞いを見る");

        CreateWriter().WriteLine("req_phone_verify_code\n").Should().Be(StdinWriteOutcome.Unavailable);
    }

    private static string ReadAvailable(FileStream reader)
    {
        var buffer = new byte[256];
        var read = reader.Read(buffer, 0, buffer.Length);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }
}
