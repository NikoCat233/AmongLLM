namespace AmongLLM.GamePlugin;

internal static class ButtonWait
{
    public const int DefaultTimeoutMs = 5000;

    public static IEnumerable<object?> UntilAvailable(Func<bool> available, Func<double> now, double overallDeadline, int timeoutMs = DefaultTimeoutMs)
    {
        if (timeoutMs is < 1 or > 60_000) throw new ArgumentOutOfRangeException(nameof(timeoutMs), "Button timeout must be between 1 and 60000 milliseconds.");
        double deadline = Math.Min(overallDeadline, now() + timeoutMs / 1000d);
        while (now() < deadline)
        {
            if (available()) yield break;
            yield return null;
        }
        throw new TimeoutException($"Button did not become available within {timeoutMs} ms or the sequence deadline.");
    }
}
