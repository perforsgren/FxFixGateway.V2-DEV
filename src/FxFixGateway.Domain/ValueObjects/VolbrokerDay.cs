using System.Globalization;

namespace FxFixGateway.Domain.ValueObjects
{
    /// <summary>
    /// Volbroker's day: Volbroker clears its requests at 17:00 New York, when the day changes there (Gareth, 22 Oct). A request's
    /// day ends at the first 17:00 New York after it was sent (its SendingTime, 52; the time we stored it when the message has
    /// none): 23:00 Stockholm, 22:00 in the weeks the US and Europe change their clocks on different dates. After that the
    /// request is gone at Volbroker: no Quote or Quote Cancel is sent on it, and our price on it is over (EXPIRED). OptionSuite's
    /// pricer counts the same end (RfqEndOfDay.VolbrokerEndUtc) from the same row (the request's first leg).
    /// </summary>
    public static class VolbrokerDay
    {
        /// <summary>17:00, New York time.</summary>
        public static readonly TimeSpan EndOfDay = new(17, 0, 0);

        /// <summary>The reason a version gets when Volbroker's day ends.</summary>
        public const string ClearedReason = "Volbroker cleared the request at 17:00 New York";

        private static readonly TimeZoneInfo NewYork = FindNewYork();

        /// <summary>
        /// When Volbroker clears a request sent at <paramref name="requestUtc"/>: the first 17:00 New York after it (a request at
        /// or after 17:00 belongs to the next day). 17:00 is never in a clock change (those are at 02:00).
        /// </summary>
        public static DateTime EndUtc(DateTime requestUtc)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(requestUtc, DateTimeKind.Utc), NewYork);
            var day = local.TimeOfDay < EndOfDay ? local.Date : local.Date.AddDays(1);
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day + EndOfDay, DateTimeKind.Unspecified), NewYork);
        }

        /// <summary>True when Volbroker has cleared a request sent at <paramref name="requestUtc"/> by <paramref name="nowUtc"/>.</summary>
        public static bool IsCleared(DateTime requestUtc, DateTime nowUtc) => nowUtc >= EndUtc(requestUtc);

        /// <summary>
        /// The request's time: its SendingTime (52, "yyyyMMdd-HH:mm:ss[.fff]", UTC) in <paramref name="rawPayload"/>, else
        /// <paramref name="receivedUtc"/> (when we stored it).
        /// </summary>
        public static DateTime RequestUtc(string? rawPayload, DateTime receivedUtc)
        {
            var sending = Tag(rawPayload, "52=");
            if (sending != null && DateTime.TryParseExact(sending, new[] { "yyyyMMdd-HH:mm:ss.fff", "yyyyMMdd-HH:mm:ss" }, CultureInfo.InvariantCulture,
                                                          DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
                return DateTime.SpecifyKind(t, DateTimeKind.Utc);
            return DateTime.SpecifyKind(receivedUtc, DateTimeKind.Utc);
        }

        private static string? Tag(string? raw, string key)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            foreach (var field in raw.Split('\u0001'))
                if (field.StartsWith(key, StringComparison.Ordinal))
                    return field.Length > key.Length ? field.Substring(key.Length) : null;
            return null;
        }

        private static TimeZoneInfo FindNewYork()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
            catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
        }
    }
}
