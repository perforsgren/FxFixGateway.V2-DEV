using FxFixGateway.Domain.Entities;
using FxFixGateway.Domain.Interfaces;
using MySql.Data.MySqlClient;
using System.Data;
using System.Text;

namespace FxFixGateway.Infrastructure.Persistence
{
    public class CanonicalMarketBookRepository : ICanonicalMarketBookRepository
    {
        private readonly string _connectionString;

        public CanonicalMarketBookRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be null.", nameof(connectionString));
            _connectionString = connectionString;
        }

        public async Task UpsertEntriesAsync(IReadOnlyList<CanonicalBookEntry> entries)
        {
            if (entries.Count == 0) return;

            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // Soft-delete allt per (venue, session, security); upserten återaktiverar
                // de rader som faktiskt finns i snapshoten.
                const string deactivate = @"
                    UPDATE fxvol.canonical_market_book
                    SET    is_active = 0
                    WHERE  venue       = @Venue
                      AND  session_key = @SessionKey
                      AND  security_id = @SecurityId;";

                foreach (var g in entries.GroupBy(e => (e.Venue, e.SessionKey, e.SecurityId)))
                {
                    await using var cmd = new MySqlCommand(deactivate, conn, tx);
                    cmd.Parameters.AddWithValue("@Venue", g.Key.Venue);
                    cmd.Parameters.AddWithValue("@SessionKey", g.Key.SessionKey);
                    cmd.Parameters.AddWithValue("@SecurityId", g.Key.SecurityId);
                    await cmd.ExecuteNonQueryAsync();
                }

                const string upsert = @"
                    INSERT INTO fxvol.canonical_market_book
                        (venue, session_key, security_id, currency_pair, tenor, cut, strategy, delta, product,
                         md_entry_type, position_no, price, size, originator, trader_id, is_active, updated_utc)
                    VALUES
                        (@Venue, @SessionKey, @SecurityId, @CurrencyPair, @Tenor, @Cut, @Strategy, @Delta, @Product,
                         @MdEntryType, @PositionNo, @Price, @Size, @Originator, @TraderId, 1, @UpdatedUtc)
                    ON DUPLICATE KEY UPDATE
                        is_active     = 1,
                        currency_pair = VALUES(currency_pair),
                        tenor         = VALUES(tenor),
                        cut           = VALUES(cut),
                        strategy      = VALUES(strategy),
                        delta         = VALUES(delta),
                        product       = VALUES(product),
                        originator    = VALUES(originator),
                        trader_id     = VALUES(trader_id),
                        updated_utc   = IF(price <> VALUES(price) OR size <> VALUES(size),
                                          VALUES(updated_utc), updated_utc),
                        price         = VALUES(price),
                        size          = VALUES(size);";

                var now = DateTime.UtcNow;
                foreach (var e in entries)
                {
                    await using var cmd = new MySqlCommand(upsert, conn, tx);
                    cmd.Parameters.AddWithValue("@Venue", e.Venue);
                    cmd.Parameters.AddWithValue("@SessionKey", e.SessionKey);
                    cmd.Parameters.AddWithValue("@SecurityId", e.SecurityId);
                    cmd.Parameters.AddWithValue("@CurrencyPair", e.CurrencyPair);
                    cmd.Parameters.AddWithValue("@Tenor", e.Tenor);
                    cmd.Parameters.AddWithValue("@Cut", e.Cut);
                    cmd.Parameters.AddWithValue("@Strategy", e.Strategy);
                    cmd.Parameters.AddWithValue("@Delta", (object?)e.Delta ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Product", (object?)e.Product ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@MdEntryType", e.MdEntryType);
                    cmd.Parameters.AddWithValue("@PositionNo", e.PositionNo);
                    cmd.Parameters.AddWithValue("@Price", (object?)e.Price ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Size", (object?)e.Size ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Originator", (object?)e.Originator ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@TraderId", (object?)e.TraderId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@UpdatedUtc", now);
                    await cmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        public async Task DeactivateEntriesAsync(string venue, string sessionKey, string securityId)
        {
            const string sql = @"
                UPDATE fxvol.canonical_market_book
                SET    is_active = 0, updated_utc = @Now
                WHERE  venue       = @Venue
                  AND  session_key = @SessionKey
                  AND  security_id = @SecurityId;";

            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Venue", venue);
            cmd.Parameters.AddWithValue("@SessionKey", sessionKey);
            cmd.Parameters.AddWithValue("@SecurityId", securityId);
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<IReadOnlyList<CanonicalBookEntry>> GetBookAsync(
            string currencyPair, string? tenor = null, string? strategy = null,
            string? cut = null, bool activeOnly = true)
        {
            var sb = new StringBuilder(@"
                SELECT id, venue, session_key, security_id, currency_pair, tenor, cut, strategy, delta, product,
                       md_entry_type, position_no, price, size, originator, trader_id, is_active, updated_utc
                FROM fxvol.canonical_market_book
                WHERE currency_pair = @CurrencyPair");

            if (activeOnly) sb.Append(" AND is_active = 1");
            if (tenor != null) sb.Append(" AND tenor    = @Tenor");
            if (strategy != null) sb.Append(" AND strategy = @Strategy");
            if (cut != null) sb.Append(" AND cut      = @Cut");
            sb.Append(" ORDER BY venue, tenor, strategy, md_entry_type, position_no;");

            var result = new List<CanonicalBookEntry>();
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand(sb.ToString(), conn);
            cmd.Parameters.AddWithValue("@CurrencyPair", currencyPair);
            if (tenor != null) cmd.Parameters.AddWithValue("@Tenor", tenor);
            if (strategy != null) cmd.Parameters.AddWithValue("@Strategy", strategy);
            if (cut != null) cmd.Parameters.AddWithValue("@Cut", cut);

            await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection);
            while (await r.ReadAsync())
                result.Add(MapRow(r));
            return result;
        }

        private static CanonicalBookEntry MapRow(MySqlDataReader r) => new()
        {
            Id = r.GetInt64("id"),
            Venue = r.GetString("venue"),
            SessionKey = r.GetString("session_key"),
            SecurityId = r.GetString("security_id"),
            CurrencyPair = r.GetString("currency_pair"),
            Tenor = r.GetString("tenor"),
            Cut = r.GetString("cut"),
            Strategy = r.GetString("strategy"),
            Delta = r.IsDBNull(r.GetOrdinal("delta")) ? null : r.GetString("delta"),
            Product = r.IsDBNull(r.GetOrdinal("product")) ? null : r.GetInt32("product"),
            MdEntryType = r.GetString("md_entry_type"),
            PositionNo = r.GetInt32("position_no"),
            Price = r.IsDBNull(r.GetOrdinal("price")) ? null : r.GetDecimal("price"),
            Size = r.IsDBNull(r.GetOrdinal("size")) ? null : r.GetDecimal("size"),
            Originator = r.IsDBNull(r.GetOrdinal("originator")) ? null : r.GetString("originator"),
            TraderId = r.IsDBNull(r.GetOrdinal("trader_id")) ? null : r.GetString("trader_id"),
            IsActive = r.GetBoolean("is_active"),
            UpdatedUtc = r.GetDateTime("updated_utc"),
        };

        public async Task UpsertIncrementalEntriesAsync(IReadOnlyList<CanonicalBookEntry> entries)
        {
            if (entries.Count == 0) return;

            // Ren upsert — ingen deactivate-all. Nyckel: (venue, session_key, security_id,
            // md_entry_type, position_no) där position_no = MDEntryID för TPICAP.
            const string upsert = @"
                INSERT INTO fxvol.canonical_market_book
                    (venue, session_key, security_id, currency_pair, tenor, cut, strategy, delta, product,
                     md_entry_type, position_no, price, size, originator, trader_id, is_active, updated_utc)
                VALUES
                    (@Venue, @SessionKey, @SecurityId, @CurrencyPair, @Tenor, @Cut, @Strategy, @Delta, @Product,
                     @MdEntryType, @PositionNo, @Price, @Size, @Originator, @TraderId, 1, @UpdatedUtc)
                ON DUPLICATE KEY UPDATE
                    is_active     = 1,
                    currency_pair = VALUES(currency_pair),
                    tenor         = VALUES(tenor),
                    cut           = VALUES(cut),
                    strategy      = VALUES(strategy),
                    delta         = VALUES(delta),
                    product       = VALUES(product),
                    originator    = VALUES(originator),
                    trader_id     = VALUES(trader_id),
                    updated_utc   = IF(price <> VALUES(price) OR size <> VALUES(size),
                                      VALUES(updated_utc), updated_utc),
                    price         = VALUES(price),
                    size          = VALUES(size);";

            var now = DateTime.UtcNow;
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();

            foreach (var e in entries)
            {
                await using var cmd = new MySqlCommand(upsert, conn);
                cmd.Parameters.AddWithValue("@Venue", e.Venue);
                cmd.Parameters.AddWithValue("@SessionKey", e.SessionKey);
                cmd.Parameters.AddWithValue("@SecurityId", e.SecurityId);
                cmd.Parameters.AddWithValue("@CurrencyPair", e.CurrencyPair);
                cmd.Parameters.AddWithValue("@Tenor", e.Tenor);
                cmd.Parameters.AddWithValue("@Cut", e.Cut);
                cmd.Parameters.AddWithValue("@Strategy", e.Strategy);
                cmd.Parameters.AddWithValue("@Delta", (object?)e.Delta ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Product", (object?)e.Product ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@MdEntryType", e.MdEntryType);
                cmd.Parameters.AddWithValue("@PositionNo", e.PositionNo);
                cmd.Parameters.AddWithValue("@Price", (object?)e.Price ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Size", (object?)e.Size ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Originator", (object?)e.Originator ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@TraderId", (object?)e.TraderId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@UpdatedUtc", now);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async Task DeactivateEntryAsync(string venue, string sessionKey, string securityId,
            string mdEntryType, int positionNo)
        {
            const string sql = @"
                UPDATE fxvol.canonical_market_book
                SET    is_active = 0, updated_utc = @Now
                WHERE  venue         = @Venue
                  AND  session_key   = @SessionKey
                  AND  security_id   = @SecurityId
                  AND  md_entry_type = @MdEntryType
                  AND  position_no   = @PositionNo;";

            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Venue", venue);
            cmd.Parameters.AddWithValue("@SessionKey", sessionKey);
            cmd.Parameters.AddWithValue("@SecurityId", securityId);
            cmd.Parameters.AddWithValue("@MdEntryType", mdEntryType);
            cmd.Parameters.AddWithValue("@PositionNo", positionNo);
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task DeactivateStaleOwnEntriesAsync(
    string venue, string sessionKey, string securityId, string mdEntryType,
    string originator, string? traderId, int keepPositionNo)
        {
            const string sql = @"
                UPDATE fxvol.canonical_market_book
                SET    is_active = 0, updated_utc = @Now
                WHERE  venue         = @Venue
                  AND  session_key   = @SessionKey
                  AND  security_id   = @SecurityId
                  AND  md_entry_type = @MdEntryType
                  AND  originator    = @Originator
                  AND  (trader_id = @TraderId OR (trader_id IS NULL AND @TraderId IS NULL))
                  AND  position_no  <> @KeepPositionNo
                  AND  is_active    = 1;";

            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Venue", venue);
            cmd.Parameters.AddWithValue("@SessionKey", sessionKey);
            cmd.Parameters.AddWithValue("@SecurityId", securityId);
            cmd.Parameters.AddWithValue("@MdEntryType", mdEntryType);
            cmd.Parameters.AddWithValue("@Originator", originator);
            cmd.Parameters.AddWithValue("@TraderId", (object?)traderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@KeepPositionNo", keepPositionNo);
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}