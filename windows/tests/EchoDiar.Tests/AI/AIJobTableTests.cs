using EchoDiar.AI;

namespace EchoDiar.Tests.AI;

// The AI job table's queue policy (Swift AIJobTableTests): one job per conversation key, the library chat (null) is
// its own key, and only an in-app job waiting behind an earlier in-app job reads as queued.
public sealed class AIJobTableTests
{
    [Fact]
    public void SecondStartOnABusyKeyIsRefusedWhileOtherKeysStart()
    {
        var table = new AIJobTable<string>();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.True(table.Start(first, AIJobTable.Kind.Summary, usesInAppModel: false, "summary"));
        Assert.False(table.Start(first, AIJobTable.Kind.Question, usesInAppModel: false, "question"));
        Assert.True(table.Start(second, AIJobTable.Kind.Question, usesInAppModel: false, "other"));
        Assert.True(table.Start(null, AIJobTable.Kind.LibraryQuestion, usesInAppModel: false, "library"));
        Assert.False(table.Start(null, AIJobTable.Kind.LibraryQuestion, usesInAppModel: false, "library again"));

        Assert.Equal("summary", table.EntryFor(first)?.Payload);
        Assert.Equal("library", table.EntryFor(null)?.Payload);
        Assert.Equal(new HashSet<Guid> { first, second }, table.BusyIds);
    }

    [Fact]
    public void OnlyTheLaterInAppJobIsQueuedUntilTheFirstFinishes()
    {
        var table = new AIJobTable<string>();
        var firstInApp = Guid.NewGuid();
        var cloud = Guid.NewGuid();
        var secondInApp = Guid.NewGuid();
        table.Start(firstInApp, AIJobTable.Kind.Summary, usesInAppModel: true, "a");
        table.Start(cloud, AIJobTable.Kind.Summary, usesInAppModel: false, "b");
        table.Start(secondInApp, AIJobTable.Kind.Question, usesInAppModel: true, "c");

        Assert.False(table.IsQueued(firstInApp));
        Assert.False(table.IsQueued(cloud));
        Assert.True(table.IsQueued(secondInApp));

        table.Finish(firstInApp);

        Assert.False(table.IsQueued(secondInApp));
    }
}
