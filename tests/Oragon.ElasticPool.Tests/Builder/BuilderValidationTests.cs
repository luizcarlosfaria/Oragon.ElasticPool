using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Oragon.ElasticPool.Builder;
using Oragon.ElasticPool.Hooks;
using Oragon.ElasticPool.Tests.TestSupport;
using Xunit;

namespace Oragon.ElasticPool.Tests.Builder;

public class BuilderValidationTests
{
    private static IServiceProvider EmptyProvider() => new ServiceCollection().BuildServiceProvider();

    [Fact]
    public void Build_WithoutFactory_ThrowsInvalidOperationException()
    {
        var sp = EmptyProvider();
        var builder = ElasticObjectPoolFactory.Build<Resource>(sp)
            .WithBounds(minSize: 0, maxSize: 1, initialSize: 0);

        Action act = () => builder.Build();

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Factory*");
    }

    [Theory]
    [InlineData(-1, 1, 0)]   // MinSize < 0
    [InlineData(0, 0, 0)]    // MaxSize < 1
    [InlineData(2, 1, 1)]    // MinSize > MaxSize
    [InlineData(0, 5, 6)]    // InitialSize > MaxSize
    [InlineData(2, 5, 1)]    // InitialSize < MinSize
    public void Build_WithInvalidBounds_Throws(int min, int max, int initial)
    {
        var sp = EmptyProvider();
        var builder = ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(min, max, initial);

        Action act = () => builder.Build();

        act.Should().Throw<Exception>()
           .Where(e => e is ArgumentOutOfRangeException || e is InvalidOperationException);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(0, 5, 3)]
    [InlineData(1, 10, 5)]
    public void Build_WithValidBounds_Succeeds(int min, int max, int initial)
    {
        var sp = EmptyProvider();
        var builder = ElasticObjectPoolFactory.Build<Resource>(sp)
            .Factory((s, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(min, max, initial);

        using var pool = builder.Build();

        pool.MaxSize.Should().Be(max);
        pool.MinSize.Should().Be(min);
    }

    [Fact]
    public void Factory_WithNullDelegate_ThrowsArgumentNullException()
    {
        var sp = EmptyProvider();
        var builder = ElasticObjectPoolFactory.Build<Resource>(sp);

        // Cast disambiguates the sync vs async Factory overloads — both throw ArgumentNullException
        // on null, this test pins the async overload's behavior.
        Action act = () => builder.Factory((FactoryDelegate<Resource>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WithFailurePolicy_Null_ThrowsArgumentNullException()
    {
        var sp = EmptyProvider();
        var builder = ElasticObjectPoolFactory.Build<Resource>(sp);

        Action act = () => builder.WithFailurePolicy(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WithTimeProvider_Null_ThrowsArgumentNullException()
    {
        var sp = EmptyProvider();
        var builder = ElasticObjectPoolFactory.Build<Resource>(sp);

        Action act = () => builder.WithTimeProvider(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void MaxWaiterCount_Negative_ThrowsArgumentOutOfRangeException()
    {
        var sp = EmptyProvider();
        var builder = ElasticObjectPoolFactory.Build<Resource>(sp);

        Action act = () => builder.MaxWaiterCount(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Factory_NullServiceProvider_Throws()
    {
        Action act = () => ElasticObjectPoolFactory.Build<Resource>(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
