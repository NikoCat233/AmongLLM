namespace AmongLLM.GamePlugin.Tests;

public sealed class UtilityTests
{
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
