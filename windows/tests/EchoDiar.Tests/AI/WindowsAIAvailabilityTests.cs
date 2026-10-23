using EchoDiar.AI;
using EchoDiar.Core.Models;

namespace EchoDiar.Tests.AI;

// Windows AI (Phi Silica) cannot run on the Mac or the CI runners: these cover what the pickers show for each ready
// state and the system catalog entry. Swift has no unavailableReason test; these are new.
public sealed class WindowsAIAvailabilityTests
{
    [Theory]
    [InlineData(WindowsAIReadyState.Ready, false, false)]
    [InlineData(WindowsAIReadyState.NotReady, false, false)]
    [InlineData(WindowsAIReadyState.NotSupportedOnCurrentSystem, true, true)]
    [InlineData(WindowsAIReadyState.NotCompatibleWithSystemHardware, true, true)]
    [InlineData(WindowsAIReadyState.ApiUnavailable, true, true)]
    [InlineData(WindowsAIReadyState.CapabilityMissing, true, true)]
    [InlineData(WindowsAIReadyState.Locked, true, true)]
    [InlineData(WindowsAIReadyState.DisabledByUser, true, false)]
    [InlineData(WindowsAIReadyState.OSUpdateNeeded, true, false)]
    [InlineData((WindowsAIReadyState)7, true, false)]
    public void EachReadyStateHasAReasonAndVisibility(WindowsAIReadyState state, bool hasReason, bool hidden)
    {
        var availability = WindowsAIAvailability.Describe(state);

        Assert.Equal(hasReason, availability.Reason is not null);
        Assert.Equal(hidden, availability.Hidden);
    }

    [Fact]
    public void PhiSilicaIsASystemModelWithNothingToDownload()
    {
        var entry = ModelCatalog.Entry("windows-phi-silica");

        Assert.NotNull(entry);
        Assert.Equal(ModelKind.Llm, entry.Kind);
        Assert.Equal(ModelRuntime.WindowsAI, entry.Runtime);
        Assert.Null(entry.Repo);
        Assert.Null(entry.Revision);
        Assert.Equal(0, entry.DiskBytes);
        Assert.DoesNotContain(entry, ModelCatalog.VisibleEntries(new HashSet<ModelRuntime>()));
    }
}
