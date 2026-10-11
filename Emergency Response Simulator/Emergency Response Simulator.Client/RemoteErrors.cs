namespace Emergency_Response_Simulator.Client;

public static class RemoteErrors
{
    /// <summary>The server's own words for a refusal, without SignalR's wrapping.</summary>
    public static string Message(Exception ex)
    {
        const string marker = "HubException: ";
        var at = ex.Message.LastIndexOf(marker, StringComparison.Ordinal);
        return at >= 0 ? ex.Message[(at + marker.Length)..] : ex.Message;
    }
}
