using LlmRouter.Infrastructure.Providers;
using Microsoft.Extensions.Configuration;

namespace LlmRouter.Tests;

public sealed class GeminiProviderTests
{
    [Theory]
    [InlineData("gemini-2.5-flash")]
    [InlineData("gemini-2.5-pro")]
    [InlineData("gemini-2.0-flash")]
    [InlineData("gemini-2.0-flash-lite")]
    [InlineData("gemini-flash-latest")]
    [InlineData("gemini-pro-latest")]
    public void SupportedModels_IncludesCurrentGenerateContentModels(string model)
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        GeminiProvider provider = new(new HttpClient(), configuration);

        Assert.Contains(model, provider.SupportedModels);
    }

    [Fact]
    public void SupportedModels_DoesNotAdvertiseUnavailableLegacyModel()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        GeminiProvider provider = new(new HttpClient(), configuration);

        Assert.DoesNotContain("gemini-1.5-pro", provider.SupportedModels);
    }
}
