using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Domain.ValueObjects;
using MySql.Data.MySqlClient;

namespace FxFixGateway.Infrastructure.Persistence
{
    /// <summary>
    /// Persists TP ICAP FXOhub RFQs in fxvol (script docs/sql/20261018_fxohub_rfq.sql): fxohub_rfq with fxohub_rfq_leg,
    /// the current depth per request in fxohub_rfq_quote / fxohub_rfq_quote_entry, and fxohub_rfq_event.
    /// Separate tables, so the Volbroker tables read by other applications are untouched (see Database.md).
    /// </summary>
    public sealed class FxoRfqRepository : IFxoRfqRepository
    {
        private readonly string _connectionString;

        public FxoRfqRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));
            _connectionString = connectionString;
        }

        public async Task<long> UpsertQuoteRequestAsync(string sessionKey, FxoQuoteRequestDto dto, DateTime receivedUtc)
        {
            const string upsertSql = @"
                INSERT INTO fxvol.fxohub_rfq
                    (session_key, quote_req_id, status, status_utc,
                     broker_id, trader_id, trade_date, exec_delta_hedge, hedge_trade_type, premium_del,
                     symbol, security_type, option_strategy, strategy_name, quote_request_type, quote_type, side,
                     transact_time_utc, valid_until_utc, expire_time_utc, net_delta, net_vega, is_own_request, text,
                     raw_payload, received_utc, updated_utc)
                VALUES
                    (@SessionKey, @QuoteReqId, 'INCOMING', @Now,
                     @BrokerId, @TraderId, @TradeDate, @ExecDeltaHedge, @HedgeTradeType, @PremiumDel,
                     @Symbol, @SecurityType, @OptionStrategy, @StrategyName, @QuoteRequestType, @QuoteType, @Side,
                     @TransactTime, @ValidUntil, @ExpireTime, @NetDelta, @NetVega, @IsOwn, @Text,
                     @RawPayload, @Now, @Now)
                ON DUPLICATE KEY UPDATE
                    broker_id = VALUES(broker_id), trader_id = VALUES(trader_id), trade_date = VALUES(trade_date),
                    exec_delta_hedge = VALUES(exec_delta_hedge), hedge_trade_type = VALUES(hedge_trade_type),
                    premium_del = VALUES(premium_del), symbol = VALUES(symbol), security_type = VALUES(security_type),
                    option_strategy = VALUES(option_strategy), strategy_name = VALUES(strategy_name),
                    quote_request_type = VALUES(quote_request_type), quote_type = VALUES(quote_type), side = VALUES(side),
                    transact_time_utc = VALUES(transact_time_utc), valid_until_utc = VALUES(valid_until_utc),
                    expire_time_utc = VALUES(expire_time_utc), net_delta = VALUES(net_delta), net_vega = VALUES(net_vega),
                    is_own_request = VALUES(is_own_request), text = VALUES(text), raw_payload = VALUES(raw_payload),
                    updated_utc = VALUES(updated_utc);";

            const string idSql = "SELECT id FROM fxvol.fxohub_rfq WHERE session_key = @SessionKey AND quote_req_id = @QuoteReqId;";
            const string deleteLegsSql = "DELETE FROM fxvol.fxohub_rfq_leg WHERE rfq_id = @RfqId;";
            const string legSql = @"
                INSERT INTO fxvol.fxohub_rfq_leg
                    (rfq_id, leg_no, leg_id, leg_symbol, leg_qty_millions, maturity_date, strike_price, leg_side,
                     tenor_code, tenor, settl_date, leg_type, option_style, cutoff, pit_type, delivery_date, leg_delta,
                     atm_type, leg_currency, currency2, call_put_currency, settl_currency)
                VALUES
                    (@RfqId, @LegNo, @LegId, @LegSymbol, @LegQty, @MaturityDate, @Strike, @LegSide,
                     @TenorCode, @Tenor, @SettlDate, @LegType, @OptionStyle, @Cutoff, @PitType, @DeliveryDate, @LegDelta,
                     @AtmType, @LegCurrency, @Currency2, @CallPutCurrency, @SettlCurrency);";

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                await using (var cmd = new MySqlCommand(upsertSql, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@SessionKey", sessionKey);
                    cmd.Parameters.AddWithValue("@QuoteReqId", dto.QuoteReqId);
                    cmd.Parameters.AddWithValue("@Now", receivedUtc);
                    cmd.Parameters.AddWithValue("@BrokerId", Db(dto.BrokerId));
                    cmd.Parameters.AddWithValue("@TraderId", Db(dto.TraderId));
                    cmd.Parameters.AddWithValue("@TradeDate", Db(dto.TradeDate));
                    cmd.Parameters.AddWithValue("@ExecDeltaHedge", Db(dto.ExecDeltaHedge));
                    cmd.Parameters.AddWithValue("@HedgeTradeType", Db(dto.HedgeTradeType));
                    cmd.Parameters.AddWithValue("@PremiumDel", Db(dto.PremiumDel));
                    cmd.Parameters.AddWithValue("@Symbol", Db(dto.Symbol));
                    cmd.Parameters.AddWithValue("@SecurityType", Db(dto.SecurityType));
                    cmd.Parameters.AddWithValue("@OptionStrategy", Db(dto.OptionStrategy));
                    cmd.Parameters.AddWithValue("@StrategyName", Db(dto.StrategyName));
                    cmd.Parameters.AddWithValue("@QuoteRequestType", Db(dto.QuoteRequestType));
                    cmd.Parameters.AddWithValue("@QuoteType", Db(dto.QuoteType));
                    cmd.Parameters.AddWithValue("@Side", Db(dto.Side));
                    cmd.Parameters.AddWithValue("@TransactTime", Db(dto.TransactTimeUtc));
                    cmd.Parameters.AddWithValue("@ValidUntil", Db(dto.ValidUntilUtc));
                    cmd.Parameters.AddWithValue("@ExpireTime", Db(dto.ExpireTimeUtc));
                    cmd.Parameters.AddWithValue("@NetDelta", Db(dto.NetDelta));
                    cmd.Parameters.AddWithValue("@NetVega", Db(dto.NetVega));
                    cmd.Parameters.AddWithValue("@IsOwn", dto.IsOwnRequest);
                    cmd.Parameters.AddWithValue("@Text", Db(dto.Text));
                    cmd.Parameters.AddWithValue("@RawPayload", dto.RawPayload);
                    await cmd.ExecuteNonQueryAsync();
                }

                long rfqId;
                await using (var cmd = new MySqlCommand(idSql, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@SessionKey", sessionKey);
                    cmd.Parameters.AddWithValue("@QuoteReqId", dto.QuoteReqId);
                    rfqId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                }

                await using (var cmd = new MySqlCommand(deleteLegsSql, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@RfqId", rfqId);
                    await cmd.ExecuteNonQueryAsync();
                }

                var legNo = 0;
                foreach (var leg in dto.Legs)
                {
                    legNo++;
                    await using var cmd = new MySqlCommand(legSql, connection, transaction);
                    cmd.Parameters.AddWithValue("@RfqId", rfqId);
                    cmd.Parameters.AddWithValue("@LegNo", legNo);
                    cmd.Parameters.AddWithValue("@LegId", Db(leg.LegId));
                    cmd.Parameters.AddWithValue("@LegSymbol", Db(leg.LegSymbol));
                    cmd.Parameters.AddWithValue("@LegQty", Db(leg.LegQtyMillions));
                    cmd.Parameters.AddWithValue("@MaturityDate", Db(leg.MaturityDate));
                    cmd.Parameters.AddWithValue("@Strike", Db(leg.StrikePrice));
                    cmd.Parameters.AddWithValue("@LegSide", Db(leg.LegSide));
                    cmd.Parameters.AddWithValue("@TenorCode", Db(leg.TenorCode));
                    cmd.Parameters.AddWithValue("@Tenor", Db(leg.Tenor));
                    cmd.Parameters.AddWithValue("@SettlDate", Db(leg.SettlDate));
                    cmd.Parameters.AddWithValue("@LegType", Db(leg.LegType));
                    cmd.Parameters.AddWithValue("@OptionStyle", Db(leg.OptionStyle));
                    cmd.Parameters.AddWithValue("@Cutoff", Db(leg.Cutoff));
                    cmd.Parameters.AddWithValue("@PitType", Db(leg.PitType));
                    cmd.Parameters.AddWithValue("@DeliveryDate", Db(leg.DeliveryDate));
                    cmd.Parameters.AddWithValue("@LegDelta", Db(leg.LegDelta));
                    cmd.Parameters.AddWithValue("@AtmType", Db(leg.AtmType));
                    cmd.Parameters.AddWithValue("@LegCurrency", Db(leg.LegCurrency));
                    cmd.Parameters.AddWithValue("@Currency2", Db(leg.Currency2));
                    cmd.Parameters.AddWithValue("@CallPutCurrency", Db(leg.CallPutCurrency));
                    cmd.Parameters.AddWithValue("@SettlCurrency", Db(leg.SettlCurrency));
                    await cmd.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
                return rfqId;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<bool> MarkCancelledAsync(string sessionKey, string quoteReqId, string reason, DateTime receivedUtc)
        {
            const string sql = @"
                UPDATE fxvol.fxohub_rfq
                SET status = 'CANCELLED', status_reason = @Reason, status_utc = @Now, updated_utc = @Now
                WHERE session_key = @SessionKey AND quote_req_id = @QuoteReqId;";

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@SessionKey", sessionKey);
            cmd.Parameters.AddWithValue("@QuoteReqId", quoteReqId);
            cmd.Parameters.AddWithValue("@Reason", reason);
            cmd.Parameters.AddWithValue("@Now", receivedUtc);
            return await cmd.ExecuteNonQueryAsync() > 0;
        }

        public async Task ReplaceDepthAsync(string sessionKey, FxoRfqDepthDto dto, DateTime receivedUtc)
        {
            const string deleteSql = "DELETE FROM fxvol.fxohub_rfq_quote WHERE session_key = @SessionKey AND quote_req_id = @QuoteReqId;";
            const string quoteSql = @"
                INSERT INTO fxvol.fxohub_rfq_quote
                    (session_key, quote_req_id, position, quote_set_id, quote_id, bid_strategy_vol, offer_strategy_vol,
                     own_quote, own_quote_requestor, received_utc)
                VALUES
                    (@SessionKey, @QuoteReqId, @Position, @QuoteSetId, @QuoteId, @Bid, @Offer, @Own, @OwnRequestor, @Now);
                SELECT LAST_INSERT_ID();";
            const string entrySql = @"
                INSERT INTO fxvol.fxohub_rfq_quote_entry
                    (quote_row_id, md_entry_type, leg_id, price, size, position_no, px_related_to, leg_side, leg_type, text)
                VALUES
                    (@QuoteRowId, @MdEntryType, @LegId, @Price, @Size, @PositionNo, @PxRelatedTo, @LegSide, @LegType, @Text);";
            const string rfqSql = @"
                UPDATE fxvol.fxohub_rfq
                SET has_market = @HasMarket, last_traded_price = COALESCE(@LastTraded, last_traded_price),
                    depth_updated_utc = @Now, updated_utc = @Now
                WHERE session_key = @SessionKey AND quote_req_id = @QuoteReqId;";

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                await using (var cmd = new MySqlCommand(deleteSql, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@SessionKey", sessionKey);
                    cmd.Parameters.AddWithValue("@QuoteReqId", dto.QuoteReqId);
                    await cmd.ExecuteNonQueryAsync();
                }

                foreach (var set in dto.QuoteSets)
                {
                    long rowId;
                    await using (var cmd = new MySqlCommand(quoteSql, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("@SessionKey", sessionKey);
                        cmd.Parameters.AddWithValue("@QuoteReqId", dto.QuoteReqId);
                        cmd.Parameters.AddWithValue("@Position", set.Position);
                        cmd.Parameters.AddWithValue("@QuoteSetId", Db(set.QuoteSetId));
                        cmd.Parameters.AddWithValue("@QuoteId", Db(set.QuoteId));
                        cmd.Parameters.AddWithValue("@Bid", Db(set.BidStrategyVol));
                        cmd.Parameters.AddWithValue("@Offer", Db(set.OfferStrategyVol));
                        cmd.Parameters.AddWithValue("@Own", set.OwnQuote);
                        cmd.Parameters.AddWithValue("@OwnRequestor", set.OwnQuoteRequestor);
                        cmd.Parameters.AddWithValue("@Now", receivedUtc);
                        rowId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                    }

                    foreach (var e in set.Entries)
                    {
                        await using var cmd = new MySqlCommand(entrySql, connection, transaction);
                        cmd.Parameters.AddWithValue("@QuoteRowId", rowId);
                        cmd.Parameters.AddWithValue("@MdEntryType", Db(e.MdEntryType));
                        cmd.Parameters.AddWithValue("@LegId", Db(e.LegId));
                        cmd.Parameters.AddWithValue("@Price", Db(e.Price));
                        cmd.Parameters.AddWithValue("@Size", Db(e.Size));
                        cmd.Parameters.AddWithValue("@PositionNo", Db(e.PositionNo));
                        cmd.Parameters.AddWithValue("@PxRelatedTo", Db(e.PxRelatedTo));
                        cmd.Parameters.AddWithValue("@LegSide", Db(e.LegSide));
                        cmd.Parameters.AddWithValue("@LegType", Db(e.LegType));
                        cmd.Parameters.AddWithValue("@Text", Db(e.Text));
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                await using (var cmd = new MySqlCommand(rfqSql, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@SessionKey", sessionKey);
                    cmd.Parameters.AddWithValue("@QuoteReqId", dto.QuoteReqId);
                    cmd.Parameters.AddWithValue("@HasMarket", !dto.NoMarketData && dto.QuoteSets.Count > 0);
                    cmd.Parameters.AddWithValue("@LastTraded", Db(dto.LastTradedPrice));
                    cmd.Parameters.AddWithValue("@Now", receivedUtc);
                    await cmd.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task InsertEventAsync(string sessionKey, string? quoteReqId, string msgType, string eventType,
                                           string? detail, string rawPayload, DateTime receivedUtc)
        {
            const string sql = @"
                INSERT INTO fxvol.fxohub_rfq_event
                    (session_key, quote_req_id, msg_type, event_type, detail, raw_payload, received_utc)
                VALUES
                    (@SessionKey, @QuoteReqId, @MsgType, @EventType, @Detail, @RawPayload, @Now);";

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@SessionKey", sessionKey);
            cmd.Parameters.AddWithValue("@QuoteReqId", Db(quoteReqId));
            cmd.Parameters.AddWithValue("@MsgType", msgType);
            cmd.Parameters.AddWithValue("@EventType", eventType);
            cmd.Parameters.AddWithValue("@Detail", Db(detail));
            cmd.Parameters.AddWithValue("@RawPayload", rawPayload);
            cmd.Parameters.AddWithValue("@Now", receivedUtc);
            await cmd.ExecuteNonQueryAsync();
        }

        private static object Db(object? value) => value ?? DBNull.Value;
    }
}
