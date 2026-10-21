using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Domain.ValueObjects;
using MySql.Data.MySqlClient;

namespace FxFixGateway.Infrastructure.Persistence
{
    /// <summary>
    /// Our price versions on FXOhub RFQs (fxvol.fxohub_rfq_price and fxohub_rfq_price_leg, script
    /// docs/sql/20261018_fxohub_rfq_price.sql). The pricer writes a version (QUEUED) for a request its trader has picked up;
    /// this gateway takes it with a conditional update (QUEUED → SENDING), sends it and writes back what FXOhub answers.
    /// Every status change is a conditional update, so the pricer's withdraw and the gateway's send never overwrite each other.
    /// </summary>
    public sealed class FxoQuoteRepository : IFxoQuoteRepository
    {
        private readonly string _connectionString;

        public FxoQuoteRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));
            _connectionString = connectionString;
        }

        public Task<int> ExpireAsync(DateTime nowUtc)
            => ExecuteAsync(@"
                UPDATE fxvol.fxohub_rfq_price
                SET status = 'EXPIRED', status_reason = 'its time ran out', status_utc = @Now, closed_utc = @Now
                WHERE status IN ('QUEUED', 'SENDING', 'SENT', 'ACCEPTED') AND valid_until_utc <= @Now;", nowUtc);

        public Task<int> RejectOrphanedAsync(DateTime nowUtc)
            => ExecuteAsync(@"
                UPDATE fxvol.fxohub_rfq_price p
                INNER JOIN fxvol.fxohub_rfq r ON r.id = p.rfq_id
                SET p.status = 'REJECTED', p.status_reason = CONCAT('not sent: the request is ', LOWER(r.status), ' and no longer the trader''s'),
                    p.status_utc = @Now, p.closed_utc = @Now
                WHERE p.status = 'QUEUED'
                  AND (r.status <> 'PICKED_UP' OR r.picked_up_by IS NULL OR r.picked_up_by <> p.trader OR r.traded_utc IS NOT NULL);", nowUtc);

        public async Task<IReadOnlyList<FxoPriceToSend>> GetQueuedAsync(DateTime nowUtc)
        {
            const string priceSql = @"
                SELECT p.id, p.rfq_id, p.version, p.trader, p.valid_until_utc, p.spot_ref,
                       r.session_key, r.quote_req_id, r.symbol, r.security_type, r.option_strategy, r.premium_del,
                       r.exec_delta_hedge, r.hedge_trade_type
                FROM fxvol.fxohub_rfq_price p
                INNER JOIN fxvol.fxohub_rfq r ON r.id = p.rfq_id
                WHERE p.status = 'QUEUED' AND (p.valid_until_utc IS NULL OR p.valid_until_utc > @Now)
                ORDER BY p.created_utc, p.id;";
            const string legSql = @"
                SELECT pl.price_id, pl.leg_no, pl.bid_vol, pl.offer_vol, pl.strike, pl.delta_pct,
                       l.leg_symbol, l.leg_id, l.leg_qty_millions, l.maturity_date, l.strike_price, l.leg_side, l.leg_type,
                       l.tenor_code, l.leg_currency, l.cutoff, l.settl_date, l.call_put_currency
                FROM fxvol.fxohub_rfq_price_leg pl
                INNER JOIN fxvol.fxohub_rfq_price p ON p.id = pl.price_id
                INNER JOIN fxvol.fxohub_rfq_leg l ON l.rfq_id = p.rfq_id AND l.leg_no = pl.leg_no
                WHERE p.status = 'QUEUED' AND (p.valid_until_utc IS NULL OR p.valid_until_utc > @Now)
                ORDER BY pl.price_id, pl.leg_no;";

            var rows = new List<(FxoPriceToSend Price, List<FxoPriceLegToSend> Legs)>();
            var byId = new Dictionary<long, List<FxoPriceLegToSend>>();
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            await using (var cmd = new MySqlCommand(priceSql, connection))
            {
                cmd.Parameters.AddWithValue("@Now", nowUtc);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    var legs = new List<FxoPriceLegToSend>();
                    var price = new FxoPriceToSend
                    {
                        PriceId = r.GetInt64(r.GetOrdinal("id")),
                        RfqId = r.GetInt64(r.GetOrdinal("rfq_id")),
                        Version = r.GetInt32(r.GetOrdinal("version")),
                        Trader = r.GetString(r.GetOrdinal("trader")),
                        // Null = good till cancelled (GTC): no ValidUntilTime is sent.
                        ValidUntilUtc = r.IsDBNull(r.GetOrdinal("valid_until_utc")) ? (DateTime?)null
                                      : DateTime.SpecifyKind(r.GetDateTime(r.GetOrdinal("valid_until_utc")), DateTimeKind.Utc),
                        SpotRef = r.GetDecimal(r.GetOrdinal("spot_ref")),
                        SessionKey = r.GetString(r.GetOrdinal("session_key")),
                        QuoteReqId = r.GetString(r.GetOrdinal("quote_req_id")),
                        Symbol = Str(r, "symbol"),
                        SecurityType = Str(r, "security_type"),
                        OptionStrategy = Str(r, "option_strategy"),
                        PremiumDel = Str(r, "premium_del"),
                        ExecDeltaHedge = Str(r, "exec_delta_hedge"),
                        HedgeTradeType = Str(r, "hedge_trade_type"),
                        Legs = legs,
                    };
                    rows.Add((price, legs));
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
                    legs.Add(new FxoPriceLegToSend
                    {
                        LegNo = r.GetInt32(r.GetOrdinal("leg_no")),
                        BidVol = r.GetDecimal(r.GetOrdinal("bid_vol")),
                        OfferVol = r.GetDecimal(r.GetOrdinal("offer_vol")),
                        Strike = r.GetDecimal(r.GetOrdinal("strike")),
                        DeltaPct = r.GetDecimal(r.GetOrdinal("delta_pct")),
                        LegSymbol = Str(r, "leg_symbol"),
                        LegId = Str(r, "leg_id"),
                        QtyMillions = Dec(r, "leg_qty_millions"),
                        MaturityDate = Date(r, "maturity_date"),
                        RequestStrike = Dec(r, "strike_price"),
                        LegSide = Str(r, "leg_side"),
                        LegType = Str(r, "leg_type"),
                        TenorCode = Str(r, "tenor_code"),
                        LegCurrency = Str(r, "leg_currency"),
                        Cutoff = Str(r, "cutoff"),
                        SettlDate = Date(r, "settl_date"),
                        CallPutCurrency = Str(r, "call_put_currency"),
                    });
                }
            }
            return rows.Select(x => x.Price).ToList();
        }

        public async Task<bool> ClaimAsync(long priceId, DateTime nowUtc)
            => await ExecuteAsync(@"
                UPDATE fxvol.fxohub_rfq_price SET status = 'SENDING', status_utc = @Now
                WHERE id = @Id AND status = 'QUEUED';", nowUtc, priceId) == 1;

        public Task UnclaimAsync(long priceId, DateTime nowUtc)
            => ExecuteAsync(@"
                UPDATE fxvol.fxohub_rfq_price SET status = 'QUEUED', status_utc = @Now
                WHERE id = @Id AND status = 'SENDING';", nowUtc, priceId);

        public async Task MarkSentAsync(long priceId, string quoteId, DateTime sentUtc)
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = new MySqlCommand(@"
                UPDATE fxvol.fxohub_rfq_price
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
                UPDATE fxvol.fxohub_rfq_price
                SET status = 'REJECTED', status_reason = @Reason, status_utc = @Now, closed_utc = @Now
                WHERE id = @Id AND status IN ('QUEUED', 'SENDING', 'SENT', 'ACCEPTED', 'WITHDRAW');", connection);
            cmd.Parameters.AddWithValue("@Id", priceId);
            cmd.Parameters.AddWithValue("@Reason", Trim(reason));
            cmd.Parameters.AddWithValue("@Now", nowUtc);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<IReadOnlyList<FxoPriceToCancel>> GetWithdrawRequestsAsync()
        {
            const string sql = @"
                SELECT p.id, p.quote_id, p.trader, p.withdraw_reason, r.session_key, r.quote_req_id
                FROM fxvol.fxohub_rfq_price p
                INNER JOIN fxvol.fxohub_rfq r ON r.id = p.rfq_id
                WHERE p.status = 'WITHDRAW' AND p.cancel_sent_utc IS NULL AND p.quote_id IS NOT NULL
                ORDER BY p.status_utc, p.id;";
            var list = new List<FxoPriceToCancel>();
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = new MySqlCommand(sql, connection);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new FxoPriceToCancel
                {
                    PriceId = r.GetInt64(r.GetOrdinal("id")),
                    QuoteId = r.GetString(r.GetOrdinal("quote_id")),
                    Trader = r.GetString(r.GetOrdinal("trader")),
                    Reason = Str(r, "withdraw_reason"),
                    SessionKey = r.GetString(r.GetOrdinal("session_key")),
                    QuoteReqId = r.GetString(r.GetOrdinal("quote_req_id")),
                });
            return list;
        }

        public Task MarkCancelSentAsync(long priceId, DateTime nowUtc)
            => ExecuteAsync("UPDATE fxvol.fxohub_rfq_price SET cancel_sent_utc = @Now WHERE id = @Id;", nowUtc, priceId);

        public async Task<FxoPriceRef?> ApplyQuoteStatusAsync(string quoteId, string quoteStatus, string? text, DateTime nowUtc)
        {
            var found = await FindAsync(quoteId);
            if (found == null) return null;
            string? sql = quoteStatus switch
            {
                // Accepted: in the market. FXOhub keeps one live quote per side, so an older version of the request is replaced.
                "0" => @"UPDATE fxvol.fxohub_rfq_price SET status = 'ACCEPTED', ack_utc = @Now, status_utc = @Now
                         WHERE id = @Id AND status IN ('SENDING', 'SENT');
                         UPDATE fxvol.fxohub_rfq_price SET status = 'REPLACED', status_reason = CONCAT('replaced by v', @Version),
                                status_utc = @Now, closed_utc = @Now
                         WHERE rfq_id = @RfqId AND version < @Version AND status IN ('SENDING', 'SENT', 'ACCEPTED', 'WITHDRAW');",
                "3" => @"UPDATE fxvol.fxohub_rfq_price SET status = 'WITHDRAWN', status_reason = COALESCE(@Text, status_reason),
                                status_utc = @Now, closed_utc = @Now
                         WHERE id = @Id AND status IN ('SENDING', 'SENT', 'ACCEPTED', 'WITHDRAW');",
                "5" or "9" => @"UPDATE fxvol.fxohub_rfq_price SET status = 'REJECTED',
                                status_reason = CONCAT(IF(@Status = '9', 'quote not found', 'rejected'), IFNULL(CONCAT(': ', @Text), '')),
                                status_utc = @Now, closed_utc = @Now
                         WHERE id = @Id AND status IN ('SENDING', 'SENT', 'ACCEPTED', 'WITHDRAW');",
                "7" => @"UPDATE fxvol.fxohub_rfq_price SET status = 'EXPIRED', status_reason = COALESCE(@Text, 'expired at FXOhub'),
                                status_utc = @Now, closed_utc = @Now
                         WHERE id = @Id AND status IN ('SENDING', 'SENT', 'ACCEPTED', 'WITHDRAW');",
                _ => null,   // 10 = pending: no change
            };
            if (sql != null)
            {
                await using var connection = new MySqlConnection(_connectionString);
                await connection.OpenAsync();
                await using var cmd = new MySqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@Id", found.PriceId);
                cmd.Parameters.AddWithValue("@RfqId", found.RfqId);
                cmd.Parameters.AddWithValue("@Version", found.Version);
                cmd.Parameters.AddWithValue("@Status", quoteStatus);
                cmd.Parameters.AddWithValue("@Text", (object?)Trim(text) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Now", nowUtc);
                await cmd.ExecuteNonQueryAsync();
            }
            return await FindAsync(quoteId);
        }

        public async Task<FxoPriceRef?> ApplyBusinessRejectAsync(string quoteId, string reason, DateTime nowUtc)
        {
            var found = await FindAsync(quoteId);
            if (found == null) return null;
            await MarkRejectedAsync(found.PriceId, reason, nowUtc);
            return await FindAsync(quoteId);
        }

        private async Task<FxoPriceRef?> FindAsync(string quoteId)
        {
            const string sql = @"
                SELECT p.id, p.rfq_id, p.version, p.status, r.session_key, r.quote_req_id
                FROM fxvol.fxohub_rfq_price p
                INNER JOIN fxvol.fxohub_rfq r ON r.id = p.rfq_id
                WHERE p.quote_id = @QuoteId;";
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@QuoteId", quoteId);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            return new FxoPriceRef
            {
                PriceId = r.GetInt64(r.GetOrdinal("id")),
                RfqId = r.GetInt64(r.GetOrdinal("rfq_id")),
                Version = r.GetInt32(r.GetOrdinal("version")),
                Status = r.GetString(r.GetOrdinal("status")),
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

        private static DateTime? Date(System.Data.Common.DbDataReader r, string column)
        {
            var i = r.GetOrdinal(column);
            return r.IsDBNull(i) ? null : r.GetDateTime(i);
        }
    }
}
