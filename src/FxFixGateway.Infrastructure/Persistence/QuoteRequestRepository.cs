using FxFixGateway.Domain.Entities;
using FxFixGateway.Domain.Interfaces;
using MySql.Data.MySqlClient;

namespace FxFixGateway.Infrastructure.Persistence
{
    /// <summary>
    /// Persisterar QuoteRequest (35=R) i fxvol.quote_requests.
    /// </summary>
    public class QuoteRequestRepository : IQuoteRequestRepository
    {
        private readonly string _connectionString;

        public QuoteRequestRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));

            _connectionString = connectionString;
        }

        public async Task<long> InsertAsync(QuoteRequest request)
        {
            const string sql = @"
                INSERT INTO fxvol.quote_requests
                    (session_key, quote_req_id, security_id, symbol,
                     product, quote_style, delta_style, strategy_type,
                     currency_pair, put_or_call, tenor, expiry_date, cut,
                     strike_price, leg_order_qty, notional_currency, leg_side, premium_currency,
                     raw_payload, received_utc)
                VALUES
                    (@SessionKey, @QuoteReqId, @SecurityId, @Symbol,
                     @Product, @QuoteStyle, @DeltaStyle, @StrategyType,
                     @CurrencyPair, @PutOrCall, @Tenor, @ExpiryDate, @Cut,
                     @StrikePrice, @LegOrderQty, @NotionalCurrency, @LegSide, @PremiumCurrency,
                     @RawPayload, @ReceivedUtc);
                SELECT LAST_INSERT_ID();";

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@SessionKey",       request.SessionKey);
            command.Parameters.AddWithValue("@QuoteReqId",       (object?)request.QuoteReqId       ?? DBNull.Value);
            command.Parameters.AddWithValue("@SecurityId",       (object?)request.SecurityId       ?? DBNull.Value);
            command.Parameters.AddWithValue("@Symbol",           (object?)request.Symbol           ?? DBNull.Value);
            command.Parameters.AddWithValue("@Product",          (object?)request.Product          ?? DBNull.Value);
            command.Parameters.AddWithValue("@QuoteStyle",       (object?)request.QuoteStyle       ?? DBNull.Value);
            command.Parameters.AddWithValue("@DeltaStyle",       (object?)request.DeltaStyle       ?? DBNull.Value);
            command.Parameters.AddWithValue("@StrategyType",     (object?)request.StrategyType     ?? DBNull.Value);
            command.Parameters.AddWithValue("@CurrencyPair",     (object?)request.CurrencyPair     ?? DBNull.Value);
            command.Parameters.AddWithValue("@PutOrCall",        (object?)request.PutOrCall        ?? DBNull.Value);
            command.Parameters.AddWithValue("@Tenor",            (object?)request.Tenor            ?? DBNull.Value);
            command.Parameters.AddWithValue("@ExpiryDate",       (object?)request.ExpiryDate       ?? DBNull.Value);
            command.Parameters.AddWithValue("@Cut",              (object?)request.Cut              ?? DBNull.Value);
            command.Parameters.AddWithValue("@StrikePrice",      (object?)request.StrikePrice      ?? DBNull.Value);
            command.Parameters.AddWithValue("@LegOrderQty",      (object?)request.LegOrderQty      ?? DBNull.Value);
            command.Parameters.AddWithValue("@NotionalCurrency", (object?)request.NotionalCurrency ?? DBNull.Value);
            command.Parameters.AddWithValue("@LegSide",          (object?)request.LegSide          ?? DBNull.Value);
            command.Parameters.AddWithValue("@PremiumCurrency",  (object?)request.PremiumCurrency  ?? DBNull.Value);
            command.Parameters.AddWithValue("@RawPayload",       request.RawPayload);
            command.Parameters.AddWithValue("@ReceivedUtc",      request.ReceivedUtc);

            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task<IReadOnlyList<QuoteRequest>> GetSinceAsync(string sessionKey, DateTime sinceUtc)
        {
            const string sql = @"
                SELECT id, session_key, quote_req_id, security_id, symbol, product, quote_style, delta_style, strategy_type,
                       currency_pair, put_or_call, tenor, expiry_date, cut, strike_price, leg_order_qty, notional_currency,
                       leg_side, premium_currency, received_utc
                FROM fxvol.quote_requests
                WHERE session_key = @SessionKey AND received_utc >= @SinceUtc
                ORDER BY id;";

            var rows = new List<QuoteRequest>();
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@SessionKey", sessionKey);
            command.Parameters.AddWithValue("@SinceUtc", sinceUtc);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string? Str(string c) { var i = reader.GetOrdinal(c); return reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i)); }
                decimal? Dec(string c) { var i = reader.GetOrdinal(c); return reader.IsDBNull(i) ? null : reader.GetDecimal(i); }
                int? Int(string c) { var i = reader.GetOrdinal(c); return reader.IsDBNull(i) ? null : Convert.ToInt32(reader.GetValue(i)); }
                rows.Add(new QuoteRequest
                {
                    Id               = reader.GetInt64(reader.GetOrdinal("id")),
                    SessionKey       = reader.GetString(reader.GetOrdinal("session_key")),
                    QuoteReqId       = Str("quote_req_id"),
                    SecurityId       = Str("security_id"),
                    Symbol           = Str("symbol"),
                    Product          = Int("product"),
                    QuoteStyle       = Str("quote_style"),
                    DeltaStyle       = Str("delta_style"),
                    StrategyType     = Str("strategy_type"),
                    CurrencyPair     = Str("currency_pair"),
                    PutOrCall        = Str("put_or_call"),
                    Tenor            = Str("tenor"),
                    ExpiryDate       = Str("expiry_date"),
                    Cut              = Str("cut"),
                    StrikePrice      = Dec("strike_price"),
                    LegOrderQty      = Dec("leg_order_qty"),
                    NotionalCurrency = Str("notional_currency"),
                    LegSide          = Str("leg_side"),
                    PremiumCurrency  = Str("premium_currency"),
                    ReceivedUtc      = reader.GetDateTime(reader.GetOrdinal("received_utc")),
                });
            }
            return rows;
        }
    }
}