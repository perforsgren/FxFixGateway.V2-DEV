namespace FxFixGateway.Domain.Constants
{
    /// <summary>VenueCode values in fix_connection_config that change how the gateway handles a session.</summary>
    public static class VenueCodes
    {
        /// <summary>
        /// Bloomberg FXGO Options RFQ (FX FIXBook for Options RFS 4.1.5): the session's messages go to the Bloomberg RFQ
        /// service before any other venue's handling, and the session uses FIX44_Bloomberg.xml.
        /// </summary>
        public const string Bloomberg = "BLOOMBERG";

        public static bool IsBloomberg(string? venueCode)
            => string.Equals(venueCode?.Trim(), Bloomberg, StringComparison.OrdinalIgnoreCase);
    }
}
