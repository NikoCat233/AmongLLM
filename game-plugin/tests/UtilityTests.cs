namespace AmongLLM.GamePlugin.Tests;

public sealed class UtilityTests
{
    [Fact]
    public void MissingButtonStopsAtFiveSecondsByDefault()
    {
        double now = 0;
        using var wait = ButtonWait.UntilAvailable(() => false, () => now, 30).GetEnumerator();
        Assert.True(wait.MoveNext());
        now = 4.999;
        Assert.True(wait.MoveNext());
        now = 5;
        Assert.Throws<TimeoutException>(() => wait.MoveNext());
    }

    [Fact]
    public void ExplicitButtonTimeoutAndEarlierSequenceDeadlineAreRespected()
    {
        double now = 0;
        bool found = false;
        using var longer = ButtonWait.UntilAvailable(() => found, () => now, 30, 12000).GetEnumerator();
        Assert.True(longer.MoveNext());
        now = 6;
        Assert.True(longer.MoveNext());
        found = true;
        Assert.False(longer.MoveNext());
        using var shorter = ButtonWait.UntilAvailable(() => false, () => now, 7, 12000).GetEnumerator();
        Assert.True(shorter.MoveNext());
        now = 7;
        Assert.Throws<TimeoutException>(() => shorter.MoveNext());
    }

    [Fact]
    public void NestedStructWritesPropagateBackThroughParentProperties()
    {
        var target = new ExampleObject { Position = new Position { X = 1, Y = 2 } };
        PropertyPath.Write(target, "Position.X", _ => 7f);
        Assert.Equal(7f, target.Position.X);
        Assert.Equal(2f, target.Position.Y);
        Assert.Equal(7f, PropertyPath.Read(target, "Position.X"));
        Assert.Throws<InvalidOperationException>(() => PropertyPath.Write(target, "ReadOnlyPosition.X", _ => 3f));
        Assert.Throws<ArgumentException>(() => PropertyPath.Write(target, "Position..X", _ => 3f));
    }

    [Fact]
    public async Task ScriptAwaitsResumeOnlyWhenUnityContextIsDrained()
    {
        using var context = new UnityThreadContext(() => false);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int continuationThread = -1;
        var script = context.Run(async () => { await resume.Task; continuationThread = Environment.CurrentManagedThreadId; });
        resume.SetResult();
        await Task.Delay(40);
        Assert.False(script.IsCompleted);
        int drainThread = Environment.CurrentManagedThreadId;
        context.Drain();
        await script;
        Assert.Equal(drainThread, continuationThread);
    }

    [Fact]
    public void CancellationDiscardsPendingScriptCallbacks()
    {
        bool cancelled = false;
        bool invoked = false;
        using var context = new UnityThreadContext(() => cancelled);
        context.Post(_ => invoked = true, null);
        cancelled = true;
        context.Drain();
        Assert.False(invoked);
    }

    public struct Position { public float X; public float Y; }
    public sealed class ExampleObject
    {
        public Position Position { get; set; }
        public Position ReadOnlyPosition => Position;
    }
}
