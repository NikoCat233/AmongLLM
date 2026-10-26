using AmongLLM.Server.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace mcp_server.Tests;

public sealed class ServerConsoleFormatterTests
{
    [Fact]
    public void UsesAnsiOnlyForAnAttachedColoredConsole()
    {
        Assert.True(ServerConsoleFormatter.ShouldUseColor(LoggerColorBehavior.Default, outputRedirected: false, noColor: null, term: "xterm-256color"));
        Assert.False(ServerConsoleFormatter.ShouldUseColor(LoggerColorBehavior.Default, outputRedirected: true, noColor: null, term: "xterm-256color"));
        Assert.True(ServerConsoleFormatter.ShouldUseColor(LoggerColorBehavior.Enabled, outputRedirected: true, noColor: null, term: "xterm-256color"));
        Assert.False(ServerConsoleFormatter.ShouldUseColor(LoggerColorBehavior.Enabled, outputRedirected: false, noColor: "1", term: "xterm-256color"));
        Assert.False(ServerConsoleFormatter.ShouldUseColor(LoggerColorBehavior.Default, outputRedirected: false, noColor: null, term: "dumb"));
        Assert.False(ServerConsoleFormatter.ShouldUseColor(LoggerColorBehavior.Disabled, outputRedirected: false, noColor: null, term: "xterm-256color"));
    }

    [Fact]
    public void ConsoleLoggerBindsColorBehaviorAndWritesAnsiToTheConsolePipeline()
    {
        var originalOutput = Console.Out;
        var originalNoColor = Environment.GetEnvironmentVariable("NO_COLOR");
        var originalTerm = Environment.GetEnvironmentVariable("TERM");
        var originalAnsiRedirect = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION");
        using var capturedOutput = new StringWriter();
        Console.SetOut(capturedOutput);
        Environment.SetEnvironmentVariable("NO_COLOR", null);
        Environment.SetEnvironmentVariable("TERM", "xterm");
        Environment.SetEnvironmentVariable("DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION", "1");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:Console:FormatterOptions:ColorBehavior"] = "Enabled"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole(options => options.FormatterName = ServerConsoleFormatter.FormatterName)
                    .AddConsoleFormatter<ServerConsoleFormatter, ServerConsoleFormatterOptions>();
            });
            services.Configure<ServerConsoleFormatterOptions>(configuration.GetSection("Logging:Console:FormatterOptions"));

            using (var provider = services.BuildServiceProvider())
            {
                Assert.Equal(LoggerColorBehavior.Enabled,
                    provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServerConsoleFormatterOptions>>().Value.ColorBehavior);
                Assert.Equal(LoggerColorBehavior.Enabled,
                    provider.GetServices<ConsoleFormatter>().OfType<ServerConsoleFormatter>().Single().ColorBehavior);
                provider.GetRequiredService<ILoggerFactory>().CreateLogger("test").LogInformation(ServerLogEvents.CommandCompleted,
                    "Game command send_rpc; outcome=success; result=submitted=true.");
            }

            var output = capturedOutput.ToString();
            Assert.Contains("\u001b[36m", output);
            Assert.Contains("outcome=\u001b[32msuccess\u001b[0m", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_COLOR", originalNoColor);
            Environment.SetEnvironmentVariable("TERM", originalTerm);
            Environment.SetEnvironmentVariable("DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION", originalAnsiRedirect);
            Console.SetOut(originalOutput);
        }
    }
}
