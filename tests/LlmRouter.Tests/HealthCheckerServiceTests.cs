using LlmRouter.Core.Abstractions;
using LlmRouter.Infrastructure.BackgroundServices;
using LlmRouter.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace LlmRouter.Tests;

public sealed class HealthCheckerServiceTests
{
    [Fact]
    public async Task ExecuteAsync_PollsProvidersAndUpdatesHealthTracker()
    {
        ILlmProvider healthyProvider = Substitute.For<ILlmProvider>();
        healthyProvider.Name.Returns("HealthyOne");
        healthyProvider.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));

        ILlmProvider failingProvider = Substitute.For<ILlmProvider>();
        failingProvider.Name.Returns("FailingOne");
        failingProvider.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<bool>(new HttpRequestException("down")));

        IHealthTracker tracker = Substitute.For<IHealthTracker>();
        IOptions<ProvidersOptions> options = Options.Create(new ProvidersOptions
        {
            HealthCheckEnabled = true,
            HealthCheckIntervalSeconds = 60
        });

        using HealthCheckerService service = new(
            [healthyProvider, failingProvider],
            tracker,
            NullLogger<HealthCheckerService>.Instance,
            options);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(4));
        await service.StartAsync(cts.Token);

        // Wait up to 3s for staggered startup (0ms for index 0, 2000ms for index 1).
        await Task.Delay(TimeSpan.FromMilliseconds(2500));
        await service.StopAsync(CancellationToken.None);

        tracker.Received().UpdateHealth("HealthyOne", true);
        tracker.Received().UpdateHealth("FailingOne", false);
    }
}
