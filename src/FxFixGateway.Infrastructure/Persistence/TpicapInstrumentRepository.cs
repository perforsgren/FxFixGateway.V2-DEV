using FxFixGateway.Domain.Entities;
using FxFixGateway.Domain.Interfaces;
using MySql.Data.MySqlClient;
using System.Data;

namespace FxFixGateway.Infrastructure.Persistence
{
    public class TpicapInstrumentRepository : ITpicapInstrumentRepository
    {
        private readonly string _connectionString;

        public TpicapInstrumentRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));
            _connectionString = connectionString;
        }

        public async Task UpsertAsync(TpicapInstrument instrument)
        {
            const string sql = @"
                INSERT INTO fxvol.tpicap_instruments
                    (session_key, security_id, symbol, currency_pair, security_type,
                     security_exchange, tenor_value, option_strategy, maturity_month_year,
                     premium_type, delivery_type, delta_basis, premium_ccy,
                     tenor, cut, strategy, delta, discovered_utc)
                VALUES
                    (@SessionKey, @SecurityId, @Symbol, @CurrencyPair, @SecurityType,
                     @SecurityExchange, @TenorValue, @OptionStrategy, @MaturityMonthYear,
                     @PremiumType, @DeliveryType, @DeltaBasis, @PremiumCcy,
                     @Tenor, @Cut, @Strategy, @Delta, @DiscoveredUtc)
                ON DUPLICATE KEY UPDATE
                    symbol              = VALUES(symbol),
                    currency_pair       = VALUES(currency_pair),
                    security_type       = VALUES(security_type),
                    security_exchange   = VALUES(security_exchange),
                    tenor_value         = VALUES(tenor_value),
                    option_strategy     = VALUES(option_strategy),
                    maturity_month_year = VALUES(maturity_month_year),
                    premium_type        = VALUES(premium_type),
                    delivery_type       = VALUES(delivery_type),
                    delta_basis         = VALUES(delta_basis),
                    premium_ccy         = VALUES(premium_ccy),
                    tenor               = VALUES(tenor),
                    cut                 = VALUES(cut),
                    strategy            = VALUES(strategy),
                    delta               = VALUES(delta),
                    updated_utc         = CURRENT_TIMESTAMP(3);";

            try
            {
                await using var connection = new MySqlConnection(_connectionString);
                await connection.OpenAsync();
                await using var cmd = new MySqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@SessionKey", instrument.SessionKey);
                cmd.Parameters.AddWithValue("@SecurityId", instrument.SecurityId);
                cmd.Parameters.AddWithValue("@Symbol", (object?)instrument.Symbol ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CurrencyPair", (object?)instrument.CurrencyPair ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SecurityType", (object?)instrument.SecurityType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SecurityExchange", (object?)instrument.SecurityExchange ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@TenorValue", (object?)instrument.TenorValue ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@OptionStrategy", (object?)instrument.OptionStrategy ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@MaturityMonthYear", (object?)instrument.MaturityMonthYear ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@PremiumType", (object?)instrument.PremiumType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@DeliveryType", (object?)instrument.DeliveryType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@DeltaBasis", (object?)instrument.DeltaBasis ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@PremiumCcy", (object?)instrument.PremiumCcy ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Tenor", (object?)instrument.Tenor ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Cut", (object?)instrument.Cut ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Strategy", (object?)instrument.Strategy ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Delta", (object?)instrument.Delta ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@DiscoveredUtc", instrument.DiscoveredUtc);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TpicapInstrumentRepository] UpsertAsync error: {ex.Message}");
            }
        }

        public async Task<TpicapInstrument?> GetBySecurityIdAsync(string sessionKey, string securityId)
        {
            const string sql = @"
                SELECT id, session_key, security_id, symbol, currency_pair, security_type,
                       security_exchange, tenor_value, option_strategy, maturity_month_year,
                       premium_type, delivery_type, delta_basis, premium_ccy,
                       tenor, cut, strategy, delta, discovered_utc, updated_utc
                FROM fxvol.tpicap_instruments
                WHERE session_key = @SessionKey AND security_id = @SecurityId
                LIMIT 1;";

            try
            {
                await using var connection = new MySqlConnection(_connectionString);
                await connection.OpenAsync();
                await using var cmd = new MySqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@SessionKey", sessionKey);
                cmd.Parameters.AddWithValue("@SecurityId", securityId);
                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    return new TpicapInstrument
                    {
                        Id = reader.GetInt64("id"),
                        SessionKey = reader.GetString("session_key"),
                        SecurityId = reader.GetString("security_id"),
                        Symbol = reader.IsDBNull(reader.GetOrdinal("symbol")) ? null : reader.GetString("symbol"),
                        CurrencyPair = reader.IsDBNull(reader.GetOrdinal("currency_pair")) ? null : reader.GetString("currency_pair"),
                        SecurityType = reader.IsDBNull(reader.GetOrdinal("security_type")) ? null : reader.GetString("security_type"),
                        SecurityExchange = reader.IsDBNull(reader.GetOrdinal("security_exchange")) ? null : reader.GetString("security_exchange"),
                        TenorValue = reader.IsDBNull(reader.GetOrdinal("tenor_value")) ? null : reader.GetString("tenor_value"),
                        OptionStrategy = reader.IsDBNull(reader.GetOrdinal("option_strategy")) ? null : reader.GetString("option_strategy"),
                        MaturityMonthYear = reader.IsDBNull(reader.GetOrdinal("maturity_month_year")) ? null : reader.GetString("maturity_month_year"),
                        PremiumType = reader.IsDBNull(reader.GetOrdinal("premium_type")) ? null : reader.GetString("premium_type"),
                        DeliveryType = reader.IsDBNull(reader.GetOrdinal("delivery_type")) ? null : reader.GetString("delivery_type"),
                        DeltaBasis = reader.IsDBNull(reader.GetOrdinal("delta_basis")) ? null : reader.GetString("delta_basis"),
                        PremiumCcy = reader.IsDBNull(reader.GetOrdinal("premium_ccy")) ? null : reader.GetString("premium_ccy"),
                        Tenor = reader.IsDBNull(reader.GetOrdinal("tenor")) ? null : reader.GetString("tenor"),
                        Cut = reader.IsDBNull(reader.GetOrdinal("cut")) ? null : reader.GetString("cut"),
                        Strategy = reader.IsDBNull(reader.GetOrdinal("strategy")) ? null : reader.GetString("strategy"),
                        Delta = reader.IsDBNull(reader.GetOrdinal("delta")) ? null : reader.GetString("delta"),
                        DiscoveredUtc = reader.GetDateTime("discovered_utc"),
                        UpdatedUtc = reader.GetDateTime("updated_utc"),
                    };
                }
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TpicapInstrumentRepository] GetBySecurityIdAsync error: {ex.Message}");
                return null;
            }
        }
    }
}