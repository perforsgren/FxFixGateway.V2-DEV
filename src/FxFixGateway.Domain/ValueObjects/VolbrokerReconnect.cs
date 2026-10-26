namespace FxFixGateway.Domain.ValueObjects
{
    /// <summary>
    /// Volbroker keeps no RFQs over a logout (Keith, Volbroker, 26 Oct: "Our FIX does not persist RFQs if you log out"): a request
    /// received before its session's latest logon is gone at Volbroker, and a Quote on it is refused. A request resent with the
    /// same QuoteReqID counts from its latest arrival. received_utc may be stored in whole seconds, so a request counts as dropped
    /// only when it came at least a second before the logon (none comes between our logout and the logon, so none is missed).
    /// The OptionSuite pricer stops Send on the same rule (from fxvol.session_heartbeat.logon_utc).
    /// </summary>
    public static class VolbrokerReconnect
    {
        public static readonly TimeSpan Margin = TimeSpan.FromSeconds(1);

        /// <summary>True when the request (its latest arrival) came before the session's latest logon.</summary>
        public static bool IsDropped(DateTime lastReceivedUtc, DateTime? sessionLogonUtc)
            => sessionLogonUtc.HasValue && lastReceivedUtc + Margin <= sessionLogonUtc.Value;

        /// <summary>The reason on a version that is over because its request was dropped.</summary>
        public static string Reason(DateTime sessionLogonUtc)
            => $"Volbroker dropped the request when we logged on again at {sessionLogonUtc:HH:mm:ss}Z (it keeps no RFQs over a logout)";
    }
}
