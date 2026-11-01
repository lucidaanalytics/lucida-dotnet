namespace Lucida;

/// <summary>Raised by <see cref="LucidaClient.ImportEventsAsync"/> when the import cannot start or the server rejects a chunk.</summary>
public sealed class LucidaException(string message) : Exception(message);

/// <summary>Debug logging and the error callback, shared by the event and flag paths. Never throws.</summary>
internal sealed class Reporter(bool debug, Action<string>? debugSink, Action<Exception>? onError)
{
    public void Debug(string msg)
    {
        if (!debug) return;
        if (debugSink is not null) debugSink("[lucida] " + msg);
        else Console.Error.WriteLine("[lucida] " + msg);
    }

    public void Error(string msg, Exception? cause = null)
    {
        Debug(msg + (cause is null ? "" : ": " + cause.Message));
        if (onError is null) return;
        try { onError(cause ?? new InvalidOperationException(msg)); }
        catch (Exception) { /* a failing callback must not break delivery */ }
    }
}
