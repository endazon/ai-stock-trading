using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.TestSupport.Composition.Tests;

// NFR, #947, IADR-0397: 組み立てガードのエンジン自体の否定形／肯定形。
// 各規則が「赤にすべき形で赤・正当な形で緑」になることを、小さな DI で固定する。
// （本番アセンブリの役はこの試験アセンブリ自身が務める。W3 の全体は各サービスのガードと変異の再注入が示す。）
public class CompositionWiringGuardTests
{
    private static readonly Assembly Here = typeof(CompositionWiringGuardTests).Assembly;

    private static CompositionReport Inspect(IServiceCollection services)
    {
        var provider = services.BuildServiceProvider();
        return CompositionWiringGuard.Inspect(provider, services, Here, Here);
    }

    // ---- W1 省略可能依存の未解決（PR #919 の形） ----

    [Fact]
    public void W1_省略可能な依存が未登録なら所見になる()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ServiceWithOptionalReporter>();

        var report = Inspect(services);

        report.Findings.Select(f => f.Key).Should().Contain(
            $"W1 {typeof(ServiceWithOptionalReporter).FullName}(reporter)");
    }

    [Fact]
    public void W1_省略可能な依存が登録されていれば所見にならない()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ServiceWithOptionalReporter>();
        services.AddSingleton<IReporter, RealReporter>();

        var report = Inspect(services);

        report.Findings.Should().BeEmpty();
        report.OptionalParametersInspected.Should().Be(1);
    }

    // ---- W2 渡し忘れ（PR #918 の形） ----

    [Fact]
    public void W2_ファクトリがnullを渡し組み立てはその型を解決できるなら所見になる()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReporter, RealReporter>();
        services.AddSingleton(_ => new ServiceWithOptionalReporter(reporter: null));

        var report = Inspect(services);

        report.Findings.Select(f => f.Key).Should().Contain(
            $"W2 {typeof(ServiceWithOptionalReporter).FullName}.reporter");
    }

    [Fact]
    public void W2_組み立てがその型を持たないnullは正当な不在として所見にならない()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => new ServiceWithOptionalReporter(reporter: null));

        var report = Inspect(services);

        report.Findings.Should().BeEmpty();
        report.FieldsInspected.Should().Be(1, "依存フィールドは辿っている（空振りではない）");
    }

    [Fact]
    public void W2_内部の既定NoOpへ落ちているなら所見になる()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => new ServiceWithNoOpFallback(reporter: null));

        var report = Inspect(services);

        report.Findings.Select(f => f.Key).Should().Contain(
            $"W2 {typeof(ServiceWithNoOpFallback).FullName}._reporter");
    }

    [Fact]
    public void W2_組み立てが明示的に選んだNoOpは所見にならない()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReporter, NoOpReporter>();
        services.AddSingleton<ServiceWithNoOpFallback>();

        var report = Inspect(services);

        report.Findings.Should().BeEmpty();
    }

    [Fact]
    public void W2_装飾が包むNoOpは組み立ての選択として所見にならない()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReporter>(_ => new DecoratingReporter(new NoOpReporter()));

        var report = Inspect(services);

        report.Findings.Should().BeEmpty();
    }

    [Fact]
    public void W2_依存を辿って奥のnullも見つける()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReporter, RealReporter>();
        services.AddSingleton(_ => new ServiceWithOptionalReporter(reporter: null));
        services.AddSingleton<Outer>();

        var report = Inspect(services);

        report.Findings.Should().ContainSingle(f => f.Rule == CompositionWiringGuard.W2);
    }

    // ---- W0 組めない（unknown ≠ none） ----

    [Fact]
    public void W0_組み立てから解決できない本番型は所見になる()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReporter>(_ => throw new InvalidOperationException("構成が無い"));

        var report = Inspect(services);

        report.Findings.Should().ContainSingle(f => f.Rule == CompositionWiringGuard.W0)
            .Which.Detail.Should().Contain("構成が無い");
    }

    // ---- W3 の土台: テストアセンブリの参照型の読み取り ----

    [Fact]
    public void W3_テストアセンブリが参照する外部型はTypeRefから読める()
    {
        _ = typeof(ServiceCollection); // TypeRef を 1 行作る
        var names = CompositionWiringGuard.ReferencedTypeNames(Here);

        names.Should().Contain(typeof(ServiceCollection).FullName!);
        names.Should().Contain(typeof(CompositionWiringGuard).FullName!);
    }

    // ---- allowlist はラチェット ----

    [Fact]
    public void allowlistに無い所見は赤になる()
    {
        var report = new CompositionReport(
            [new CompositionFinding("W1", "W1 X(y)", "detail")], 1, 0, 1, 1, 0);

        var act = () => report.AssertNoUnexpectedFindings(new Dictionary<string, string>());

        act.Should().Throw<CompositionGuardException>().WithMessage("*W1 X(y)*");
    }

    [Fact]
    public void 実体を失ったallowlist行は赤になる()
    {
        var report = new CompositionReport([], 1, 0, 1, 1, 0);

        var act = () => report.AssertNoUnexpectedFindings(
            new Dictionary<string, string> { ["W1 X(y)"] = "#1: 理由" });

        act.Should().Throw<CompositionGuardException>().WithMessage("*実体を失った*W1 X(y)*");
    }

    [Fact]
    public void 理由にissue番号の無いallowlist行は赤になる()
    {
        var report = new CompositionReport([new CompositionFinding("W1", "W1 X(y)", "detail")], 1, 0, 1, 1, 0);

        var act = () => report.AssertNoUnexpectedFindings(
            new Dictionary<string, string> { ["W1 X(y)"] = "理由だけ" });

        act.Should().Throw<CompositionGuardException>().WithMessage("*issue 番号*");
    }

    [Fact]
    public void 理由とissue番号のあるallowlist行で所見が一致すれば緑()
    {
        var report = new CompositionReport([new CompositionFinding("W1", "W1 X(y)", "detail")], 1, 0, 1, 1, 0);

        var act = () => report.AssertNoUnexpectedFindings(
            new Dictionary<string, string> { ["W1 X(y)"] = "#947: 理由" });

        act.Should().NotThrow();
    }

    [Fact]
    public void 母集団が下限を割れば赤になる()
    {
        var report = new CompositionReport([], RootsComposed: 0, HandlersComposed: 0, FieldsInspected: 0, 0, 0);

        var act = () => report.AssertPopulation(minRoots: 1, minFields: 1);

        act.Should().Throw<CompositionGuardException>().WithMessage("*空振り*");
    }

    [Fact]
    public void 伝送の境界の空実装は呼ばれたら投げる()
    {
        var stub = TransportStub.Create<IReporter>();

        var act = () => stub.Report("x");

        act.Should().Throw<NotSupportedException>().WithMessage("*外界へ出ない*");
    }

    // ---- 試験用の小さな本番型 ----

    public interface IReporter
    {
        void Report(string message);
    }

    public sealed class RealReporter : IReporter
    {
        public void Report(string message)
        {
        }
    }

    public sealed class NoOpReporter : IReporter
    {
        public void Report(string message)
        {
        }
    }

    public sealed class DecoratingReporter(IReporter inner) : IReporter
    {
        public void Report(string message) => inner.Report(message);
    }

    public sealed class ServiceWithOptionalReporter(IReporter? reporter = null)
    {
        public void Run() => reporter?.Report("run");
    }

    public sealed class ServiceWithNoOpFallback(IReporter? reporter = null)
    {
        private readonly IReporter _reporter = reporter ?? new NoOpReporter();

        public void Run() => _reporter.Report("run");
    }

    public sealed class Outer(ServiceWithOptionalReporter inner)
    {
        public void Run() => inner.Run();
    }
}
