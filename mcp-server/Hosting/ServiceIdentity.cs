namespace AmongLLM.Server.Hosting;

public static class ServiceIdentity
{
    public const string Name = "AmongLLM";
    public static string Id { get; } = Guid.NewGuid().ToString("D");
    public static DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
}
