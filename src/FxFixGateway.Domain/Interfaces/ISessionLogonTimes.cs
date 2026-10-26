namespace FxFixGateway.Domain.Interfaces
{
    /// <summary>
    /// When each session last logged on, on this gateway's clock (the clock fxvol.quote_requests.received_utc is on). Volbroker
    /// keeps no RFQs over a logout (Keith, Volbroker, 26 Oct): a request received before its session's latest logon is gone there.
    /// </summary>
    public interface ISessionLogonTimes
    {
        /// <summary>The session's latest logon since the gateway started, or null when it has not logged on.</summary>
        DateTime? LastLogonUtc(string sessionKey);
    }
}
