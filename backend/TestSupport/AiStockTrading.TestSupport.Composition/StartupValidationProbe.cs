using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AiStockTrading.TestSupport.Composition;

/// <summary>
/// 🔴 NFR, #1437, IADR-0571: 本番の組み立て（Program.cs）の<b>起動時の検証</b>（<c>ValidateOnStart</c>）の結果を、
/// <c>WebApplicationFactory</c> のホスト破棄との競合なしに取り出す。
/// <para>
/// minimal hosting の <c>WebApplicationFactory</c> は、アプリの入口（<c>app.Run</c>）を別スレッドで走らせ、試験側スレッドは
/// 組まれたホストの <c>IHostApplicationLifetime</c> を引いて起動完了を待つ。起動の検証で失敗するとアプリ側は
/// <c>RunAsync</c> の <c>finally</c> でホストを<b>破棄してから</b>例外を返すため、試験側がその後に問い合わせると
/// 検証の例外（<see cref="OptionsValidationException"/>）ではなく <see cref="ObjectDisposedException"/> を受け取る。
/// どちらになるかはスレッドの走る順で決まる（#1437 の実測: Integration E2E 2026-10-22 14:35Z で後者）。
/// </para>
/// <para>
/// ここでは <see cref="IHostLifetime"/> を差し替え、ホストが起動の検証へ進む<b>直前</b>（<c>WaitForStartAsync</c>）に、
/// 本番と同じ <see cref="IStartupValidator"/> をホストのコンテナから引いて走らせ、結果を記録する。記録は試験側が
/// 起動を待つ経路と独立しているので、走る順に依らず検証の例外そのものを読める。ホストの起動はそのまま続け、
/// 起動が止まった（例外が出た）ことも併せて返す。
/// </para>
/// </summary>
public static class StartupValidationProbe
{
    /// <summary>起動の検証の結果と、起動そのものの結果。</summary>
    /// <param name="ValidationFailure">起動の検証（<see cref="IStartupValidator"/>）が投げた例外。通れば <c>null</c>。</param>
    /// <param name="StartFailure">ホストの起動（<c>factory.Services</c>）が投げた例外。起動すれば <c>null</c>。
    /// 型は上記の競合で決まり得るため、型ではなく有無だけを見る。</param>
    /// <param name="ValidatorPresent">組み立てに起動の検証が 1 つでも登録されていたか。</param>
    public sealed record Outcome(Exception? ValidationFailure, Exception? StartFailure, bool ValidatorPresent);

    /// <summary>
    /// ファクトリが組むホストを起動し、起動の検証の結果を返す。起動に成功したホストは <paramref name="onStarted"/> へ渡す
    /// （ファクトリの破棄と同時に破棄される）。
    /// </summary>
    public static Outcome ProbeStartupValidation<TEntryPoint>(
        this WebApplicationFactory<TEntryPoint> factory,
        Action<IWebHostBuilder>? configure = null,
        Action<IServiceProvider>? onStarted = null)
        where TEntryPoint : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        var recorder = new Recorder();
        using var derived = factory.WithWebHostBuilder(builder =>
        {
            configure?.Invoke(builder);
            builder.ConfigureServices(services =>
                services.AddSingleton<IHostLifetime>(sp => new ValidatingLifetime(sp, recorder)));
        });

        Exception? startFailure = null;
        IServiceProvider? started = null;
        try
        {
            started = derived.Services;
        }
        catch (Exception ex)
        {
            startFailure = ex;
        }

        if (!recorder.Ran)
            throw new InvalidOperationException(
                "起動の検証の直前に到達しなかった（IHostLifetime の差し替えが効いていない）。検査を空で通さない", startFailure);

        if (started is not null)
            onStarted?.Invoke(started);

        return new Outcome(recorder.Failure, startFailure, recorder.ValidatorPresent);
    }

    // 書き手はアプリ側スレッド（WaitForStartAsync）。Failure・ValidatorPresent は Ran（volatile）より前に書くので、
    // 読み手は必ず Ran を先に読んでから他を読む（ProbeStartupValidation がその順を守る）。
    private sealed class Recorder
    {
        public volatile bool Ran;
        public Exception? Failure;
        public bool ValidatorPresent;
    }

    private sealed class ValidatingLifetime(IServiceProvider services, Recorder recorder) : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken)
        {
            var validator = services.GetService<IStartupValidator>();
            recorder.ValidatorPresent = validator is not null;
            try
            {
                validator?.Validate();
            }
            catch (Exception ex)
            {
                recorder.Failure = ex;
            }

            recorder.Ran = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
