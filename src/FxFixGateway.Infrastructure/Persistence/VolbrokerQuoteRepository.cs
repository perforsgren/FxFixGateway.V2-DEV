using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Domain.ValueObjects;
using MySql.Data.MySqlClient;

namespace FxFixGateway.Infrastructure.Persistence
{
    /// <summary>
    /// Our price versions on Volbroker RFQs (fxvol.volbroker_rfq_price and volbroker_rfq_price_leg, keyed by the request's
    /// first leg in fxvol.quote_requests; script OptionSuite.Pricer.Wpf/Docs/Sql/20261022_volbroker_rfq_send.sql in
    /// OptionSuite.V2). The pricer writes a version (QUEUED) for a request its trader has picked up (fxvol.volbroker_rfq_state);
    /// this gateway takes it with a conditional update (QUEUED → SENDING), sends it and writes back what Volbroker answers.
    /// Every status change is a conditional update, so the pricer's withdraw and the gateway's send never overwrite each other.
    /// Versions the trader recorded by hand (QUOTED) are never touched.
    /// </summary>
    public sealed class VolbrokerQuoteRepository : IVolbrokerQuoteRepository
    {
        private readonly string _connectionString;

        public VolbrokerQuoteRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));
            _connectionString = connectionString;
        }

        // The time stipulation ran out. A version never sent is expired here; one Volbroker has is withdrawn with a Quote Cancel
        // (Volbroker removes it itself at its ValidityPeriod, in whole minutes: the cancel makes the exact time ours).
        public Task<int> ExpireAsync(DateTime nowUtc)
            => ExecuteAsync(@"
                UPDATE fxvol.volbroker_rfq_price
                SET status = 'EXPIRED', status_reason = 'its time ran out', status_utc = @Now, closed_utc = @Now
                WHERE status = 'QUEUED' AND valid_until_utc <= @Now;
                UPDATE fxvol.volbroker_rfq_price
                SET status = 'WITHDRAW', withdraw_reason = '" + VolbrokerPriceToCancel.TimeRanOut + @"', status_utc = @Now
                WHERE status IN ('SENDING', 'SENT', 'ACCEPTED') AND quote_id IS NOT NULL AND valid_until_utc <= @Now;", nowUtc);

        public Task<int> RejectOrphanedAsync(DateTime nowUtc)
            => ExecuteAsync(@"
                UPDATE fxvol.volbroker_rfq_price p
                INNER JOIN fxvol.volbroker_rfq_state s ON s.rfq_id = p.rfq_id
                SET p.status = 'REJECTED', p.status_reason = CONCAT('not sent: the request is ', LOWER(s.status), ' and no longer the trader''s'),
                    p.status_utc = @Now, p.closed_utc = @Now
                WHERE p.status = 'QUEUED'
                  AND (s.status <> 'PICKED_UP' OR s.picked_up_by IS NULL OR s.picked_up_by <> p.trader);", nowUtc);

        public async Task<IReadOnlyList<VolbrokerPriceToSend>> GetQueuedAsync(DateTime nowUtc)
        {
            const string priceSql = @"
                SELECT p.id, p.rfq_id, p.version, p.trader, p.trader_name, p.valid_until_utc, p.spot_low, p.spot_high, p.size_millions,
                       q.session_key, q.quote_req_id, q.security_id, q.received_utc, q.raw_payload
                FROM fxvol.volbroker_rfq_price p
                INNER JOIN fxvol.quote_requests q ON q.id = p.rfq_id
                WHERE p.status = 'QUEUED' AND (p.valid_until_utc IS NULL OR p.valid_until_utc > @Now)
                ORDER BY p.created_utc, p.id;";
            const string legSql = @"
                SELECT pl.price_id, pl.leg_no, pl.leg_symbol, pl.bid_vol, pl.offer_vol
                FROM fxvol.volbroker_rfq_price_leg pl
                INNER JOIN fxvol.volbroker_rfq_price p ON p.id = pl.price_id
                WHERE p.status = 'QUEUED' AND (p.valid_until_utc IS NULL OR p.valid_until_utc > @Now)
                ORDER BY pl.price_id, pl.leg_no;";

            var prices = new List<VolbrokerPriceToSend>();
            var byId = new Dictionary<long, List<VolbrokerPriceLegToSend>>();
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            await using (var cmd = new MySqlCommand(priceSql, connection))
            {
                cmd.Parameters.AddWithValue("@Now", nowUtc);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    var legs = new List<VolbrokerPriceLegToSend>();
                    var price = new VolbrokerPriceToSend
                    {
                        PriceId = r.GetInt64(r.GetOrdinal("id")),
                        RfqId = r.GetInt64(r.GetOrdinal("rfq_id")),
                        Version = r.GetInt32(r.GetOrdinal("version")),
                        Trader = r.GetString(r.GetOrdinal("trader")),
                        TraderName = Str(r, "trader_name"),
                        ValidUntilUtc = r.IsDBNull(r.GetOrdinal("valid_until_utc")) ? (DateTime?)null
                                      : DateTime.SpecifyKind(r.GetDateTime(r.GetOrdinal("valid_until_utc")), DateTimeKind.Utc),
                        SpotLow = Dec(r, "spot_low"),
                        SpotHigh = Dec(r, "spot_high"),
                        SizeMillions = Dec(r, "size_millions"),
                        SessionKey = r.GetString(r.GetOrdinal("session_key")),
                        QuoteReqId = r.GetString(r.GetOrdinal("quote_req_id")),
                        SecurityId = Str(r, "security_id"),
                        RequestUtc = RequestUtc(r),
                        Legs = legs,
                    };
                    prices.Add(price);
                    byId[price.PriceId] = legs;
                }
            }

            await using (var cmd = new MySqlCommand(legSql, connection))
            {
                cmd.Parameters.AddWithValue("@Now", nowUtc);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    if (!byId.TryGetValue(r.GetInt64(r.GetOrdinal("price_id")), out var legs)) continue;
                    legs.Add(new VolbrokerPriceLegToSend
                    {
                        LegNo = r.GetInt32(r.GetOrdinal("leg_no")),
                        LegSymbol = Str(r, "leg_symbol"),
                        BidVol = r.GetDecimal(r.GetOrdinal("bid_vol")),
                        OfferVol = r.GetDecimal(r.GetOrdinal("offer_vol")),
                    });
                }
            }
            return prices;
        }

        public async Task<bool> ClaimAsync(long priceId, DateTime nowUtc)
            => await ExecuteAsync(@"
                UPDATE fxvol.volbroker_rfq_price SET status = 'SENDING', status_utc = @Now
                WHERE id = @Id AND status = 'QUEUED';", nowUtc, priceId) == 1;

        public Task UnclaimAsync(long priceId, DateTime nowUtc)
            => ExecuteAsync(@"
                UPDATE fxvol.volbroker_rfq_price SET status = 'QUEUED', status_utc = @Now
                WHERE id = @Id AND status = 'SENDING';", nowUtc, priceId);

        public async Task MarkSentAsync(long priceId, string quoteId, DateTime sentUtc)
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = new MySqlCommand(@"
                UPDATE fxvol.volbroker_rfq_price
                SET quote_id = @QuoteId, sent_utc = @Now,
                    status = CASE WHEN status = 'SENDING' THEN 'SENT' ELSE status END, status_utc = @Now
                WHERE id = @Id;", connection);
            cmd.Parameters.AddWithValue("@Id", priceId);
            cmd.Parameters.AddWithValue("@QuoteId", quoteId);
            cmd.Parameters.AddWithValue("@Now", sentUtc);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task MarkRejectedAsync(long priceId, string reason, DateTime nowUtc)
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = new MySqlCommand(@"
                UPDATE fxvol.volbroker_rfq_price
                SET status = 'REJECTED', status_reason = @Reason, status_utc = @Now, closed_utc = @Now
                WHERE id = @Id AND status IN ('QUEUED', 'SENDING', 'SENT', 'ACCEPTED', 'WITHDRAW');", connection);
            cmd.Parameters.AddWithValue("@Id", priceId);
            cmd.Parameters.AddWithValue("@Reason", Trim(reason));
            cmd.Parameters.AddWithValue("@Now", nowUtc);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<IReadOnlyList<VolbrokerPriceToCancel>> GetWithdrawRequestsAsync()
        {
            const string sql = @"
                SELECT p.id, p.version, p.quote_id, p.trader, p.withdraw_reason, q.session_key, q.quote_req_id, q.received_utc, q.raw_payload
                FROM fxvol.volbroker_rfq_price p
                INNER JOIN fxvol.quote_requests q ON q.id = p.rfq_id
                WHERE p.status = 'WITHDRAW' AND p.cancel_sent_utc IS NULL AND p.quote_id IS NOT NULL
                ORDER BY p.status_utc, p.id;";
            var list = new List<VolbrokerPriceToCancel>();
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = new MySqlCommand(sql, connection);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new VolbrokerPriceToCancel
                {
                    PriceId = r.GetInt64(r.GetOrdinal("id")),
                    Version = r.GetInt32(r.GetOrdinal("version")),
                    QuoteId = r.GetString(r.GetOrdinal("quote_id")),
                    Trader = r.GetString(r.GetOrdinal("trader")),
                    Reason = Str(r, "withdraw_reason"),
                    SessionKey = r.GetString(r.GetOrdinal("session_key")),
                    QuoteReqId = r.GetString(r.GetOrdinal("quote_req_id")),
                    RequestUtc = RequestUtc(r),
                });
            return list;
        }

        public async Task<IReadOnlyList<VolbrokerLiveVersion>> GetLiveVersionsAsync()
        {
            const string sql = @"
                SELECT p.id, p.version, p.status, q.session_key, q.quote_req_id, q.received_utc, q.raw_payload
                FROM fxvol.volbroker_rfq_price p
                INNER JOIN fxvol.quote_requests q ON q.id = p.rfq_id
                WHERE p.status IN ('QUEUED', 'SENDING', 'SENT', 'ACCEPTED', 'WITHDRAW')
                ORDER BY p.id;";
            var list = new List<VolbrokerLiveVersion>();
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = new MySqlCommand(sql, connection);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new VolbrokerLiveVersion
                {
                    PriceId = r.GetInt64(r.GetOrdinal("id")),
                    Version = r.GetInt32(r.GetOrdinal("version")),
                    Status = r.GetString(r.GetOrdinal("status")),
                    SessionKey = r.GetString(r.GetOrdinal("session_key")),
                    QuoteReqId = r.GetString(r.GetOrdinal("quote_req_id")),
                    RequestUtc = RequestUtc(r),
                });
            return list;
        }

        // Volbroker cleared the request at 17:00 New York: the version is over, whatever it was waiting for (no Quote or Quote
        // Cancel goes out on a request Volbroker no longer has).
        public async Task<bool> MarkClearedAsync(long priceId, string reason, DateTime nowUtc)
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = new MySqlCommand(@"
                UPDATE fxvol.volbroker_rfq_price
                SET status = 'EXPIRED', status_reason = @Reason, status_utc = @Now, closed_utc = @Now
                WHERE id = @Id AND status IN ('QUEUED', 'SENDING', 'SENT', 'ACCEPTED', 'WITHDRAW');", connection);
            cmd.Parameters.AddWithValue("@Id", priceId);
            cmd.Parameters.AddWithValue("@Reason", Trim(reason));
            cmd.Parameters.AddWithValue("@Now", nowUtc);
            return await cmd.ExecuteNonQueryAsync() == 1;
        }

        // The request's time from its first leg's row: its SendingTime (52), else when the gateway stored it.
        private static DateTime RequestUtc(System.Data.Common.DbDataReader r)
            => VolbrokerDay.RequestUtc(Str(r, "raw_payload"), r.GetDateTime(r.GetOrdinal("received_utc")));

        public Task MarkCancelSentAsync(long priceId, DateTime nowUtc)
            => ExecuteAsync("UPDATE fxvol.volbroker_rfq_price SET cancel_sent_utc = @Now WHERE id = @Id;", nowUtc, priceId);

        public async Task<VolbrokerPriceRef?> ApplyQuoteResponseAsync(string? quoteId, string? quoteRespId, string quoteRespType, string reason, DateTime nowUtc)
        {
            var found = !string.IsNullOrEmpty(quoteId) ? await FindAsync("quote_id", quoteId) : null;
            found ??= !string.IsNullOrEmpty(quoteRespId) ? await FindAsync("quote_resp_id", quoteRespId) : null;
            if (found == null) return null;
            const string live = "('SENDING', 'SENT', 'ACCEPTED', 'WITHDRAW')";
            string sql = quoteRespType switch
            {
                // Entered: in the market. Volbroker keeps one price per interest and side and updates it, so an older version of
                // the request is replaced.
                "10" => @"UPDATE fxvol.volbroker_rfq_price SET status = 'ACCEPTED', ack_utc = @Now, status_utc = @Now
                          WHERE id = @Id AND status IN ('SENDING', 'SENT');
                          UPDATE fxvol.volbroker_rfq_price SET status = 'REPLACED', status_reason = CONCAT('replaced by v', @Version),
                                 status_utc = @Now, closed_utc = @Now
                          WHERE rfq_id = @RfqId AND version < @Version AND status IN " + live + ";",
                // Hit (an indicative price is confirmed by voice): done on this version.
                "1" => @"UPDATE fxvol.volbroker_rfq_price SET status = 'DONE', status_reason = @Reason, status_utc = @Now, closed_utc = @Now
                         WHERE id = @Id AND status IN " + live + ";",
                // Removed: timed out, spot out level reached, or removed (withdrawn when we were withdrawing it).
                "3" => @"UPDATE fxvol.volbroker_rfq_price
                         SET status = IF(status = 'WITHDRAW' AND COALESCE(withdraw_reason, '') <> '" + VolbrokerPriceToCancel.TimeRanOut + @"', 'WITHDRAWN', 'EXPIRED'),
                             status_reason = @Reason, status_utc = @Now, closed_utc = @Now
                         WHERE id = @Id AND status IN " + live + ";",
                // One side entered, the other failed: the side that went in is withdrawn, never left alone in the market.
                "11" or "12" => @"UPDATE fxvol.volbroker_rfq_price
                                  SET status = 'WITHDRAW', status_reason = @Reason,
                                      withdraw_reason = LEFT(CONCAT(@Reason, IF(@Type = '11', ' · the offer withdrawn', ' · the bid withdrawn')), 255),
                                      status_utc = @Now
                                  WHERE id = @Id AND status IN ('SENDING', 'SENT', 'ACCEPTED');",
                "6" or "13" or "14" or "15" or "99" => @"UPDATE fxvol.volbroker_rfq_price
                                                         SET status = 'REJECTED', status_reason = @Reason, status_utc = @Now, closed_utc = @Now
                                                         WHERE id = @Id AND status IN " + live + ";",
                _ => "",
            };
            await using (var connection = new MySqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                await using var cmd = new MySqlCommand(@"
                    UPDATE fxvol.volbroker_rfq_price SET quote_resp_id = COALESCE(@RespId, quote_resp_id) WHERE id = @Id;" + sql, connection);
                cmd.Parameters.AddWithValue("@Id", found.PriceId);
                cmd.Parameters.AddWithValue("@RfqId", found.RfqId);
                cmd.Parameters.AddWithValue("@Version", found.Version);
                cmd.Parameters.AddWithValue("@Type", quoteRespType);
                cmd.Parameters.AddWithValue("@RespId", (object?)quoteRespId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Reason", Trim(reason));
                cmd.Parameters.AddWithValue("@Now", nowUtc);
                await cmd.ExecuteNonQueryAsync();
            }
            return await FindAsync("id", found.PriceId);
        }

        public async Task<VolbrokerPriceRef?> ApplyQuoteStatusAsync(string quoteId, string quoteStatus, string? text, DateTime nowUtc)
        {
            var found = await FindAsync("quote_id", quoteId);
            if (found == null) return null;
            string? sql = quoteStatus switch
            {
                // Cancelled, or nothing left to cancel (already gone): withdrawn, or expired when we cancelled it at its time.
                "0" or "9" => @"UPDATE fxvol.volbroker_rfq_price
                                SET status = IF(COALESCE(withdraw_reason, '') = '" + VolbrokerPriceToCancel.TimeRanOut + @"', 'EXPIRED', 'WITHDRAWN'),
                                    status_reason = IF(@Status = '9', LEFT(CONCAT('no price left to cancel on Volbroker', IFNULL(CONCAT(': ', @Text), '')), 255),
                                                       COALESCE(@Text, IF(COALESCE(withdraw_reason, '') = '" + VolbrokerPriceToCancel.TimeRanOut + @"', 'its time ran out', status_reason))),
                                    status_utc = @Now, closed_utc = @Now
                                WHERE id = @Id AND status = 'WITHDRAW';",
                "5" or "99" => CancelFailedSql,
                _ => null,
            };
            if (sql != null)
                await ExecuteForAsync(sql, found, quoteStatus, (quoteStatus == "5" ? "cancel rejected by Volbroker" : "cancel failed at Volbroker")
                                                              + (string.IsNullOrEmpty(text) ? "" : ": " + text), nowUtc, text);
            return await FindAsync("id", found.PriceId);
        }

        public async Task<VolbrokerPriceRef?> ApplyBusinessRejectAsync(string quoteId, string refMsgType, string reason, DateTime nowUtc)
        {
            var found = await FindAsync("quote_id", quoteId);
            if (found == null) return null;
            if (refMsgType == "Z") await ExecuteForAsync(CancelFailedSql, found, "j", reason, nowUtc, null);
            else await MarkRejectedAsync(found.PriceId, reason, nowUtc);
            return await FindAsync("id", found.PriceId);
        }

        // Our Quote Cancel was refused: the price is still on Volbroker. A trader's withdraw goes back to ACCEPTED with the reason
        // (the trader can withdraw again); one at its time stays WITHDRAW with the reason (not sent again, or it would repeat
        // every round): Volbroker removes it at its ValidityPeriod.
        private const string CancelFailedSql = @"
            UPDATE fxvol.volbroker_rfq_price
            SET status_reason = LEFT(CONCAT(@Reason, ' · still on Volbroker'), 255), status_utc = @Now,
                status = IF(COALESCE(withdraw_reason, '') = '" + VolbrokerPriceToCancel.TimeRanOut + @"', 'WITHDRAW', 'ACCEPTED'),
                cancel_sent_utc = IF(COALESCE(withdraw_reason, '') = '" + VolbrokerPriceToCancel.TimeRanOut + @"', cancel_sent_utc, NULL),
                withdraw_reason = IF(COALESCE(withdraw_reason, '') = '" + VolbrokerPriceToCancel.TimeRanOut + @"', withdraw_reason, NULL)
            WHERE id = @Id AND status = 'WITHDRAW';";

        public async Task InsertEventAsync(string sessionKey, string? quoteReqId, string msgType, string eventType, string? detail, string rawPayload, DateTime nowUtc)
        {
            const string sql = @"
                INSERT INTO fxvol.volbroker_rfq_event
                    (session_key, quote_req_id, msg_type, event_type, detail, raw_payload, received_utc)
                VALUES
                    (@SessionKey, @QuoteReqId, @MsgType, @EventType, @Detail, @RawPayload, @Now);";
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@SessionKey", sessionKey);
            cmd.Parameters.AddWithValue("@QuoteReqId", (object?)quoteReqId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@MsgType", msgType);
            cmd.Parameters.AddWithValue("@EventType", eventType);
            cmd.Parameters.AddWithValue("@Detail", detail == null ? DBNull.Value : detail.Length > 512 ? detail.Substring(0, 512) : detail);
            cmd.Parameters.AddWithValue("@RawPayload", rawPayload);
            cmd.Parameters.AddWithValue("@Now", nowUtc);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task ExecuteForAsync(string sql, VolbrokerPriceRef found, string status, string reason, DateTime nowUtc, string? text)
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@Id", found.PriceId);
            cmd.Parameters.AddWithValue("@Status", status);
            cmd.Parameters.AddWithValue("@Reason", Trim(reason));
            cmd.Parameters.AddWithValue("@Text", (object?)Trim(text) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Now", nowUtc);
            await cmd.ExecuteNonQueryAsync();
        }

        // A version by its id, QuoteID or Volbroker's QuoteRespID (the newest when Volbroker's id repeats across versions).
        private async Task<VolbrokerPriceRef?> FindAsync(string column, object value)
        {
            var sql = @"
                SELECT p.id, p.rfq_id, p.version, p.status, p.withdraw_reason, q.session_key, q.quote_req_id
                FROM fxvol.volbroker_rfq_price p
                INNER JOIN fxvol.quote_requests q ON q.id = p.rfq_id
                WHERE p." + column + @" = @Value
                ORDER BY p.version DESC
                LIMIT 1;";
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@Value", value);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            return new VolbrokerPriceRef
            {
                PriceId = r.GetInt64(r.GetOrdinal("id")),
                RfqId = r.GetInt64(r.GetOrdinal("rfq_id")),
                Version = r.GetInt32(r.GetOrdinal("version")),
                Status = r.GetString(r.GetOrdinal("status")),
                WithdrawReason = Str(r, "withdraw_reason"),
                SessionKey = r.GetString(r.GetOrdinal("session_key")),
                QuoteReqId = r.GetString(r.GetOrdinal("quote_req_id")),
            };
        }

        private async Task<int> ExecuteAsync(string sql, DateTime nowUtc, long? id = null)
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@Now", nowUtc);
            if (id.HasValue) cmd.Parameters.AddWithValue("@Id", id.Value);
            return await cmd.ExecuteNonQueryAsync();
        }

        private static string? Trim(string? text) => text == null ? null : text.Length > 255 ? text.Substring(0, 255) : text;

        private static string? Str(System.Data.Common.DbDataReader r, string column)
        {
            var i = r.GetOrdinal(column);
            return r.IsDBNull(i) ? null : r.GetString(i);
        }

        private static decimal? Dec(System.Data.Common.DbDataReader r, string column)
        {
            var i = r.GetOrdinal(column);
            return r.IsDBNull(i) ? null : r.GetDecimal(i);
        }
    }
}
