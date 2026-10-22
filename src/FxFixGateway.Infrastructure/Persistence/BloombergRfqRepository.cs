using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Domain.ValueObjects;
using MySql.Data.MySqlClient;

namespace FxFixGateway.Infrastructure.Persistence
{
    /// <summary>
    /// Persists Bloomberg FXGO Options RFQs in fxvol (script docs/sql/20261022_bloomberg_rfq.sql): bloomberg_rfq with
    /// bloomberg_rfq_leg, and bloomberg_rfq_event. A request is identified by (session_key, quote_req_id), as FXOhub's.
    /// The columns are listed once below (column → value) and the statements are built from those lists.
    /// </summary>
    public sealed class BloombergRfqRepository : IBloombergRfqRepository
    {
        private static readonly (string Column, Func<BloombergQuoteRequestDto, object?> Value)[] RfqColumns =
        {
            ("sender_sub_id",         d => d.SenderSubId),
            ("on_behalf_of_sub_id",   d => d.OnBehalfOfSubId),
            ("deliver_to_comp_id",    d => d.DeliverToCompId),
            ("market_segment_id",     d => d.MarketSegmentId),
            ("quote_type",            d => d.QuoteType),
            ("symbol",                d => d.Symbol),
            ("symbol_sfx",            d => d.SymbolSfx),
            ("security_id",           d => d.SecurityId),
            ("security_id_source",    d => d.SecurityIdSource),
            ("account",               d => d.Account),
            ("in_competition",        d => d.InCompetition),
            ("text",                  d => d.Text),
            ("trade_date",            d => d.TradeDate),
            ("side",                  d => d.Side),
            ("option_strategy",       d => d.OptionStrategy),
            ("strategy_name",         d => d.StrategyName),
            ("venue_mic",             d => d.VenueMic),
            ("venue_role",            d => d.VenueRole),
            ("venue_lei",             d => d.VenueLei),
            ("taker_deal_code",       d => d.TakerDealCode),
            ("taker_firm",            d => d.TakerFirm),
            ("taker_uuid",            d => d.TakerUuid),
            ("taker_trader",          d => d.TakerTrader),
            ("taker_lei",             d => d.TakerLei),
            ("maker_deal_code",       d => d.MakerDealCode),
            ("maker_firm",            d => d.MakerFirm),
            ("maker_lei",             d => d.MakerLei),
            ("spot_rate",             d => d.SpotRate),
            ("premium_currency",      d => d.PremiumCurrency),
            ("premium_delivery_date", d => d.PremiumDeliveryDate),
            ("premium_delivery_type", d => d.PremiumDeliveryType),
            ("exec_delta_hedge",      d => d.ExecDeltaHedge),
            ("hedge_trade_type",      d => d.HedgeTradeType),
            ("hedge_direction",       d => d.HedgeDirection),
            ("hedge_side",            d => d.HedgeSide),
            ("hedge_amount",          d => d.HedgeAmount),
            ("hedge_rate",            d => d.HedgeRate),
            ("hedge_date",            d => d.HedgeDate),
            ("hedge_fixing_date",     d => d.HedgeFixingDate),
            ("hedge_settl_method",    d => d.HedgeSettlMethod),
            ("hedge_settl_currency",  d => d.HedgeSettlCurrency),
            ("hedge_security_id",     d => d.HedgeSecurityId),
            ("hedge_upi_code",        d => d.HedgeUpiCode),
            ("settl_type",            d => d.SettlType),
            ("settl_currency",        d => d.SettlCurrency),
            ("market_type",           d => d.MarketType),
            ("quote_price_type",      d => d.QuotePriceType),
            ("solve_for_type",        d => d.SolveForType),
            ("solve_for_leg_ref_id",  d => d.SolveForLegRefId),
            ("upi_code",              d => d.UpiCode),
            ("allocation_count",      d => d.AllocationCount),
            ("parse_problems",        d => d.Problems.Count == 0 ? null : string.Join("\n", d.Problems)),
            ("raw_payload",           d => d.RawPayload),
        };

        private static readonly (string Column, Func<BloombergQuoteRequestLegDto, object?> Value)[] LegColumns =
        {
            ("leg_ref_id",               l => l.LegRefId),
            ("leg_security_id",          l => l.LegSecurityId),
            ("leg_upi_code",             l => l.LegUpiCode),
            ("leg_currency",             l => l.LegCurrency),
            ("leg_isin_product",         l => l.LegIsinProduct),
            ("cut_off",                  l => l.CutOff),
            ("expiry_date",              l => l.ExpiryDate),
            ("delivery_date",            l => l.DeliveryDate),
            ("call_put_currency",        l => l.CallPutCurrency),
            ("call_or_put",              l => l.CallOrPut),
            ("delta",                    l => l.Delta),
            ("strike_price",             l => l.StrikePrice),
            ("notional_amount",          l => l.NotionalAmount),
            ("option_style",             l => l.OptionStyle),
            ("position",                 l => l.Position),
            ("forward_rate",             l => l.ForwardRate),
            ("ccy1_deposit_rate",        l => l.Ccy1DepositRate),
            ("ccy2_deposit_rate",        l => l.Ccy2DepositRate),
            ("barrier_style",            l => l.BarrierStyle),
            ("barrier_level",            l => l.BarrierLevel),
            ("barrier_level2",           l => l.BarrierLevel2),
            ("barrier_direction",        l => l.BarrierDirection),
            ("touch_type",               l => l.TouchType),
            ("leg_hedge_amount",         l => l.LegHedgeAmount),
            ("leg_hedge_currency",       l => l.LegHedgeCurrency),
            ("leg_exec_delta_hedge",     l => l.LegExecDeltaHedge),
            ("leg_delta_hedge_side",     l => l.LegDeltaHedgeSide),
            ("leg_hedge_rate",           l => l.LegHedgeRate),
            ("leg_hedge_trade_type",     l => l.LegHedgeTradeType),
            ("leg_hedge_date",           l => l.LegHedgeDate),
            ("leg_hedge_fixing_date",    l => l.LegHedgeFixingDate),
            ("leg_hedge_settl_method",   l => l.LegHedgeSettlMethod),
            ("leg_hedge_settl_currency", l => l.LegHedgeSettlCurrency),
            ("allocation_count",         l => l.AllocationCount),
        };

        // A resend keeps the status and our acknowledgement; everything the message says is replaced.
        internal static readonly string UpsertSql =
            "INSERT INTO fxvol.bloomberg_rfq (session_key, quote_req_id, status, status_utc, "
            + string.Join(", ", RfqColumns.Select(c => c.Column)) + ", received_utc, updated_utc) VALUES (@session_key, @quote_req_id, 'INCOMING', @now, "
            + string.Join(", ", RfqColumns.Select(c => "@" + c.Column)) + ", @now, @now) ON DUPLICATE KEY UPDATE "
            + string.Join(", ", RfqColumns.Select(c => $"{c.Column} = VALUES({c.Column})")) + ", updated_utc = VALUES(updated_utc);";

        internal static readonly string LegSql =
            "INSERT INTO fxvol.bloomberg_rfq_leg (rfq_id, leg_no, " + string.Join(", ", LegColumns.Select(c => c.Column))
            + ") VALUES (@rfq_id, @leg_no, " + string.Join(", ", LegColumns.Select(c => "@" + c.Column)) + ");";

        private readonly string _connectionString;

        public BloombergRfqRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));
            _connectionString = connectionString;
        }

        public async Task<BloombergRfqStored> UpsertQuoteRequestAsync(string sessionKey, BloombergQuoteRequestDto dto, DateTime receivedUtc)
        {
            const string storedSql = @"
                SELECT id, status, ack_sent_utc FROM fxvol.bloomberg_rfq
                WHERE session_key = @session_key AND quote_req_id = @quote_req_id;";
            const string deleteLegsSql = "DELETE FROM fxvol.bloomberg_rfq_leg WHERE rfq_id = @rfq_id;";

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                await using (var cmd = new MySqlCommand(UpsertSql, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@session_key", sessionKey);
                    cmd.Parameters.AddWithValue("@quote_req_id", dto.QuoteReqId);
                    cmd.Parameters.AddWithValue("@now", receivedUtc);
                    foreach (var (column, value) in RfqColumns)
                        cmd.Parameters.AddWithValue("@" + column, value(dto) ?? DBNull.Value);
                    await cmd.ExecuteNonQueryAsync();
                }

                BloombergRfqStored stored;
                await using (var cmd = new MySqlCommand(storedSql, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@session_key", sessionKey);
                    cmd.Parameters.AddWithValue("@quote_req_id", dto.QuoteReqId);
                    await using var reader = await cmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                        throw new InvalidOperationException($"Bloomberg RFQ {dto.QuoteReqId} not found after it was stored.");
                    stored = new BloombergRfqStored(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetDateTime(2));
                }

                await using (var cmd = new MySqlCommand(deleteLegsSql, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@rfq_id", stored.Id);
                    await cmd.ExecuteNonQueryAsync();
                }

                var legNo = 0;
                foreach (var leg in dto.Legs)
                {
                    legNo++;
                    await using var cmd = new MySqlCommand(LegSql, connection, transaction);
                    cmd.Parameters.AddWithValue("@rfq_id", stored.Id);
                    cmd.Parameters.AddWithValue("@leg_no", legNo);
                    foreach (var (column, value) in LegColumns)
                        cmd.Parameters.AddWithValue("@" + column, value(leg) ?? DBNull.Value);
                    await cmd.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
                return stored;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task MarkAckSentAsync(long rfqId, string quoteId, DateTime sentUtc)
        {
            const string sql = @"
                UPDATE fxvol.bloomberg_rfq
                SET ack_quote_id = @quote_id, ack_sent_utc = @now, updated_utc = @now
                WHERE id = @id;";

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@id", rfqId);
            cmd.Parameters.AddWithValue("@quote_id", quoteId);
            cmd.Parameters.AddWithValue("@now", sentUtc);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<bool> MarkCancelledAsync(string sessionKey, string quoteReqId, string reason, DateTime receivedUtc)
        {
            const string sql = @"
                UPDATE fxvol.bloomberg_rfq
                SET status = 'CANCELLED', status_reason = @reason, status_utc = @now, updated_utc = @now
                WHERE session_key = @session_key AND quote_req_id = @quote_req_id;";

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@session_key", sessionKey);
            cmd.Parameters.AddWithValue("@quote_req_id", quoteReqId);
            cmd.Parameters.AddWithValue("@reason", reason);
            cmd.Parameters.AddWithValue("@now", receivedUtc);
            return await cmd.ExecuteNonQueryAsync() > 0;
        }

        public async Task InsertEventAsync(string sessionKey, string? quoteReqId, string msgType, string eventType,
                                           string? detail, string rawPayload, DateTime receivedUtc)
        {
            const string sql = @"
                INSERT INTO fxvol.bloomberg_rfq_event
                    (session_key, quote_req_id, msg_type, event_type, detail, raw_payload, received_utc)
                VALUES
                    (@session_key, @quote_req_id, @msg_type, @event_type, @detail, @raw_payload, @now);";

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@session_key", sessionKey);
            cmd.Parameters.AddWithValue("@quote_req_id", (object?)quoteReqId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@msg_type", msgType);
            cmd.Parameters.AddWithValue("@event_type", eventType);
            // The column holds 1024 characters; a longer detail (an exception's text) is cut there, the raw message is whole.
            cmd.Parameters.AddWithValue("@detail", detail == null ? DBNull.Value : detail.Length > 1024 ? detail[..1021] + "..." : detail);
            cmd.Parameters.AddWithValue("@raw_payload", rawPayload);
            cmd.Parameters.AddWithValue("@now", receivedUtc);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
