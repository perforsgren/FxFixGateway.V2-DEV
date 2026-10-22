using System.Text.RegularExpressions;
using FxFixGateway.Domain.Constants;
using FxFixGateway.Domain.ValueObjects;
using FxFixGateway.Infrastructure.Persistence;
using FxFixGateway.Infrastructure.QuickFix;
using Xunit;

namespace FxFixGateway.Tests
{
    public class SessionAndSchemaTests
    {
        private static SessionConfiguration Config(string key, string venue, bool reset) => new(
            connectionId: 1, sessionKey: key, venueCode: venue, connectionType: "initiator", description: "",
            fixVersion: "FIX.4.4", host: "localhost", port: 9000, senderCompId: "SWED", targetCompId: "BLP", heartBtIntSec: 30,
            useSsl: false, sslServerName: "", logonUsername: "", password: "", ackSupported: false, ackMode: "",
            reconnectIntervalSeconds: 30, startTime: TimeSpan.Zero, endTime: TimeSpan.Zero, useDataDictionary: false,
            dataDictionaryFile: "", isEnabled: true, createdUtc: DateTime.UtcNow, updatedUtc: DateTime.UtcNow, updatedBy: "",
            notes: "", resetSeqNumOnLogon: reset);

        /// <summary>The [SESSION] section of <paramref name="key"/> in the built configuration.</summary>
        private static string SessionSection(string content, string targetCompId)
            => content.Split("[SESSION]").Single(s => s.Contains($"TargetCompID={targetCompId}"));

        [Fact]
        public void A_session_that_keeps_its_sequence_numbers_does_not_reset_them()
        {
            var keep = Config("BBG_UAT", "BLOOMBERG", reset: false);
            var other = Config("VOLB_FIXHUB_DEV", "VOLBROKER", reset: true);
            other = new SessionConfiguration(2, other.SessionKey, other.VenueCode, "", "", "FIX.4.4", "localhost", 9001, "SWED",
                "TFSICAP", 30, false, "", "", "", false, "", 30, TimeSpan.Zero, TimeSpan.Zero, false, "", true,
                DateTime.UtcNow, DateTime.UtcNow, "", "");

            var content = new SessionSettingsBuilder(Path.GetTempPath()).BuildConfigFileContentPublic(new[] { keep, other });

            Assert.Contains("ResetOnLogon=Y", content.Split("[SESSION]")[0]);   // the default for every session
            var bbg = SessionSection(content, "BLP");
            Assert.Contains("ResetOnLogon=N", bbg);
            Assert.Contains("ResetOnLogout=N", bbg);
            Assert.Contains("ResetOnDisconnect=N", bbg);
            Assert.DoesNotContain("ResetOn", SessionSection(content, "TFSICAP"));
        }

        [Fact]
        public void Changing_host_port_or_enabled_keeps_the_reset_setting()
        {
            var keep = Config("BBG_UAT", "BLOOMBERG", reset: false);

            Assert.False(keep.WithHost("bbg-beta").ResetSeqNumOnLogon);
            Assert.False(keep.WithPort(9100).ResetSeqNumOnLogon);
            Assert.False(keep.WithEnabled(false).ResetSeqNumOnLogon);
            Assert.False(keep.WithSSLTunnel(true, "h", 1, 2, "s").ResetSeqNumOnLogon);
        }

        [Theory]
        [InlineData("BLOOMBERG", true)]
        [InlineData("bloomberg", true)]
        [InlineData(" BLOOMBERG ", true)]
        [InlineData("VOLBROKER", false)]
        [InlineData(null, false)]
        public void Bloomberg_sessions_are_known_by_their_venue_code(string? venue, bool expected)
            => Assert.Equal(expected, VenueCodes.IsBloomberg(venue));

        /// <summary>The columns of <paramref name="table"/> in its CREATE TABLE of the script.</summary>
        private static HashSet<string> ScriptColumns(string script, string table)
        {
            var body = Regex.Match(script, $@"CREATE TABLE IF NOT EXISTS fxvol\.{table} \((.*?)\n\) ENGINE", RegexOptions.Singleline).Groups[1].Value;
            return Regex.Matches(body, @"^\s+([a-z0-9_]+)\s+(BIGINT|VARCHAR|DATETIME|DATE|DECIMAL|INT|TEXT|LONGTEXT|TINYINT)", RegexOptions.Multiline)
                        .Select(m => m.Groups[1].Value).ToHashSet();
        }

        /// <summary>The column list of an INSERT statement.</summary>
        private static string[] InsertColumns(string sql)
            => Regex.Match(sql, @"INSERT INTO fxvol\.\w+ \(([^)]*)\)").Groups[1].Value.Split(',').Select(c => c.Trim()).ToArray();

        [Fact]
        public void The_repository_writes_only_columns_the_script_creates()
        {
            var script = File.ReadAllText(Path.Combine(Fix.RepoRoot, "docs", "sql", "20261022_bloomberg_rfq.sql"));

            var rfq = ScriptColumns(script, "bloomberg_rfq");
            var leg = ScriptColumns(script, "bloomberg_rfq_leg");
            Assert.Contains("ack_sent_utc", rfq);
            Assert.Contains("allocation_count", leg);

            Assert.DoesNotContain(InsertColumns(BloombergRfqRepository.UpsertSql), c => !rfq.Contains(c));
            Assert.DoesNotContain(InsertColumns(BloombergRfqRepository.LegSql), c => !leg.Contains(c));
            // Every column the message fills is written (the rest: id, status, ack, times, set by the repository's statements).
            Assert.Empty(rfq.Except(InsertColumns(BloombergRfqRepository.UpsertSql))
                            .Except(new[] { "id", "status_reason", "ack_quote_id", "ack_sent_utc" }));
            Assert.Empty(leg.Except(InsertColumns(BloombergRfqRepository.LegSql)).Except(new[] { "id" }));
        }
    }
}
