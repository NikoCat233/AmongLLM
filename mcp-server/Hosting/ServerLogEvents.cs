using Microsoft.Extensions.Logging;

namespace AmongLLM.Server.Hosting;

public static class ServerLogEvents
{
    public static readonly EventId ServerStarted = new(1998, nameof(ServerStarted));
    public static readonly EventId PortFallback = new(1999, nameof(PortFallback));
    public static readonly EventId WebSocketAccepted = new(2000, nameof(WebSocketAccepted));
    public static readonly EventId RegistrationRejected = new(2001, nameof(RegistrationRejected));
    public static readonly EventId GameInstanceRegistered = new(2002, nameof(GameInstanceRegistered));
    public static readonly EventId GameInstanceReconnected = new(2003, nameof(GameInstanceReconnected));
    public static readonly EventId GameInstanceDisconnected = new(2004, nameof(GameInstanceDisconnected));
    public static readonly EventId WebSocketTransportError = new(2005, nameof(WebSocketTransportError));
    public static readonly EventId CommandQueued = new(2100, nameof(CommandQueued));
    public static readonly EventId CommandSent = new(2101, nameof(CommandSent));
    public static readonly EventId CommandCompleted = new(2102, nameof(CommandCompleted));
    public static readonly EventId CommandClientError = new(2103, nameof(CommandClientError));
    public static readonly EventId CommandTimedOut = new(2104, nameof(CommandTimedOut));
    public static readonly EventId CommandCancelled = new(2105, nameof(CommandCancelled));
    public static readonly EventId CommandTransportError = new(2106, nameof(CommandTransportError));
}
