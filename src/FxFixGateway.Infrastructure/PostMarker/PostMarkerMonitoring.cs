using Microsoft.Extensions.Logging;

namespace FxFixGateway.Infrastructure.PostMarker
{
    /// <summary>Where the PostMarker connection is — shown in the gateway UI.</summary>
    public enum PostMarkerConnectionState
    {
        /// <summary>The service has not started yet.</summary>
        Starting,

        /// <summary>"Enabled": false in appsettings.json.</summary>
        Disabled,

        /// <summary>No account (UserName, Password, SystemId) in fx_appsettings.json.</summary>
        NotConfigured,

        Connecting,

        Connected,

        /// <summary>The session broke (or Reconnect was clicked); waiting before the next connect.</summary>
        Reconnecting,

        /// <summary>The gateway is shutting down.</summary>
        Stopped
    }

    /// <summary>A PostMarker deal as listed in the gateway UI.</summary>
    public sealed record PostMarkerPayloadInfo(
        int SequenceNumber,
        string Status,
        DateTime ReceivedUtc,
        string Xml,
        bool Acknowledged,
        bool Accepted);

    /// <summary>One line in the gateway UI's PostMarker activity log (also written to the gateway log).</summary>
    public sealed record PostMarkerActivity(DateTime TimeUtc, LogLevel Level, string Message);
}
