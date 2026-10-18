using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Domain.ValueObjects;
using MySql.Data.MySqlClient;
using System.Data;

namespace FxFixGateway.Infrastructure.Persistence
{
    /// <summary>
    /// Läser prenumerationskonfiguration från fix_config_prod.market_subscriptions.
    /// </summary>
    public class MarketSubscriptionRepository : IMarketSubscriptionRepository
    {
        private readonly string _connectionString;

        public MarketSubscriptionRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));

            _connectionString = connectionString;
        }

        public async Task<IReadOnlyList<MarketSubscriptionFilter>> GetEnabledSubscriptionsAsync(string sessionKey)
        {
            var result = new List<MarketSubscriptionFilter>();

            const string sql = @"
                SELECT session_key, currency_pair, product
                FROM fix_config_prod.market_subscriptions
                WHERE session_key = @SessionKey
                  AND is_enabled  = TRUE
                ORDER BY currency_pair, product;";

            // No catch: a database error is thrown. Read as "no subscriptions" before, it skipped the security list with a
            // misleading warning and the error went only to the Debug output.
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@SessionKey", sessionKey);

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection);

            while (await reader.ReadAsync())
            {
                result.Add(new MarketSubscriptionFilter(
                    sessionKey:   reader.GetString("session_key"),
                    currencyPair: reader.GetString("currency_pair"),
                    product:      reader.GetInt32("product")));
            }

            return result;
        }
    }
}