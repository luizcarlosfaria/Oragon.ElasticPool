using AwesomeAssertions;
using Oragon.ElasticPool.RabbitMQ.Builder;
using Xunit;

namespace Oragon.ElasticPool.RabbitMQ.Tests;

public class BuilderOptionsTests
{
    [Fact]
    public void ConnectionBuilder_ExposesSweepTuning()
    {
        var builder = new ElasticConnectionPoolBuilder()
            .WithSweepInterval(TimeSpan.FromSeconds(2))
            .WithShrinkOnUtilizationPercent(0.25)
            .WithShrinkTargetUtilizationPercent(0.80)
            .WithShrinkBatchSize(4)
            .WithShrinkCooldownWindows(1);

        builder.SweepInterval.Should().Be(TimeSpan.FromSeconds(2));
        builder.ShrinkOnUtilizationPercent.Should().Be(0.25);
        builder.ShrinkTargetUtilizationPercent.Should().Be(0.80);
        builder.ShrinkBatchSize.Should().Be(4);
        builder.ShrinkCooldownWindows.Should().Be(1);
    }

    [Fact]
    public void ChannelBuilder_ExposesSweepTuning()
    {
        var builder = new ElasticChannelPoolBuilder()
            .WithSweepInterval(TimeSpan.FromSeconds(2))
            .WithShrinkOnUtilizationPercent(0.25)
            .WithShrinkTargetUtilizationPercent(0.80)
            .WithShrinkBatchSize(4)
            .WithShrinkCooldownWindows(1);

        builder.SweepInterval.Should().Be(TimeSpan.FromSeconds(2));
        builder.ShrinkOnUtilizationPercent.Should().Be(0.25);
        builder.ShrinkTargetUtilizationPercent.Should().Be(0.80);
        builder.ShrinkBatchSize.Should().Be(4);
        builder.ShrinkCooldownWindows.Should().Be(1);
    }

    [Fact]
    public void SweepTuning_ValidatesInputs()
    {
        var connection = new ElasticConnectionPoolBuilder();
        var channel = new ElasticChannelPoolBuilder();

        connection.Invoking(b => b.WithSweepInterval(TimeSpan.Zero))
            .Should().Throw<ArgumentOutOfRangeException>();
        connection.Invoking(b => b.WithShrinkCooldownWindows(-1))
            .Should().Throw<ArgumentOutOfRangeException>();
        connection.Invoking(b => b.WithShrinkOnUtilizationPercent(-0.1))
            .Should().Throw<ArgumentOutOfRangeException>();
        connection.Invoking(b => b.WithShrinkOnUtilizationPercent(1.1))
            .Should().Throw<ArgumentOutOfRangeException>();
        connection.Invoking(b => b.WithShrinkTargetUtilizationPercent(0))
            .Should().Throw<ArgumentOutOfRangeException>();
        connection.Invoking(b => b.WithShrinkTargetUtilizationPercent(1.1))
            .Should().Throw<ArgumentOutOfRangeException>();
        connection.Invoking(b => b.WithShrinkBatchSize(0))
            .Should().Throw<ArgumentOutOfRangeException>();
        channel.Invoking(b => b.WithSweepInterval(TimeSpan.Zero))
            .Should().Throw<ArgumentOutOfRangeException>();
        channel.Invoking(b => b.WithShrinkCooldownWindows(-1))
            .Should().Throw<ArgumentOutOfRangeException>();
        channel.Invoking(b => b.WithShrinkOnUtilizationPercent(-0.1))
            .Should().Throw<ArgumentOutOfRangeException>();
        channel.Invoking(b => b.WithShrinkOnUtilizationPercent(1.1))
            .Should().Throw<ArgumentOutOfRangeException>();
        channel.Invoking(b => b.WithShrinkTargetUtilizationPercent(0))
            .Should().Throw<ArgumentOutOfRangeException>();
        channel.Invoking(b => b.WithShrinkTargetUtilizationPercent(1.1))
            .Should().Throw<ArgumentOutOfRangeException>();
        channel.Invoking(b => b.WithShrinkBatchSize(0))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
