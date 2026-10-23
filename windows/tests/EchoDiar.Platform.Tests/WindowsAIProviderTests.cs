using EchoDiar.AI;
using EchoDiar.Platform.AI;

namespace EchoDiar.Platform.Tests;

// The runner has no NPU and this test exe has no Windows AI runtime: the provider must report "not supported" and fail
// a job with that reason, never throw from the availability check.
public sealed class WindowsAIProviderTests
{
    [Fact]
    public async Task ReportsNotSupportedWhenWindowsAIIsUnavailable()
    {
        var provider = new WindowsAIProvider();

        var (reason, hidden) = provider.Availability();
        var error = await Assert.ThrowsAsync<LLMException>(() =>
            provider.WithLoadedModelAsync(WindowsAIProvider.ModelID, _ => Task.FromResult(0), TestContext.Current.CancellationToken));

        Assert.NotNull(reason);
        Assert.True(hidden);
        Assert.Equal(reason, error.Message);
    }
}
