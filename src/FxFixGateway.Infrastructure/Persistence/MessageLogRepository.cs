using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using FxFixGateway.Domain.Enums;
using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Domain.ValueObjects;
using MySql.Data.MySqlClient;

namespace FxFixGateway.Infrastructure.Persistence
{
    public sealed class MessageLogRepository : IMessageLogger
    {
        private readonly string _connectionString;

        public MessageLogRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));

            _connectionString = connectionString;
        }

        public Task LogIncomingAsync(string sessionKey, string msgType, string rawMessage, DateTime receivedUtc)
            => LogMessageAsync(sessionKey, msgType, rawMessage, MessageDirection.Incoming, receivedUtc);

        public Task LogOutgoingAsync(string sessionKey, string msgType, string rawMessage, DateTime sentUtc)
            => LogMessageAsync(sessionKey, msgType, rawMessage, MessageDirection.Outgoing, sentUtc);

        public async Task<IEnumerable<MessageLogEntry>> GetRecentAsync(string sessionKey, int maxCount = 100)
        {
            var result = new List<MessageLogEntry>();

            var sql = @"
                SELECT
                    ReceivedUtc,
                    Direction,
                    MessageType,
                    RawMessage
                FROM MessageIn
                WHERE SessionKey = @SessionKey
                ORDER BY ReceivedUtc DESC
                LIMIT @MaxCount;";

            try
            {
                await using var connection = new MySqlConnection(_connectionString);
                await connection.OpenAsync();

                await using var command = new MySqlCommand(sql, connection);
                command.Parameters.AddWithValue("@SessionKey", sessionKey);
                command.Parameters.AddWithValue("@MaxCount", maxCount);

                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection);

                while (await reader.ReadAsync())
                {
                    try
                    {
                        var timestamp = reader.GetDateTime(0);
                        var directionStr = reader.IsDBNull(1) ? "Incoming" : reader.GetString(1);
                        var direction = Enum.TryParse<MessageDirection>(directionStr, out var dir) ? dir : MessageDirection.Incoming;
                        
                        var msgType = reader.IsDBNull(2) ? "?" : reader.GetString(2);
                        if (string.IsNullOrWhiteSpace(msgType)) msgType = "?";
                        
                        var rawMessage = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);

                        var summary = GetMessageSummary(msgType);
                        var entry = new MessageLogEntry(timestamp, direction, msgType, summary, rawMessage);
                        result.Add(entry);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MessageLogRepository] Row parse error: {ex.Message}");
                    }
                }
            }
            catch (MySqlException ex)
            {
                // Skriv ut ALLA detaljer till Debug output
                System.Diagnostics.Debug.WriteLine("========== MySQL ERROR in GetRecentAsync ==========");
                System.Diagnostics.Debug.WriteLine($"Error Number: {ex.Number}");
                System.Diagnostics.Debug.WriteLine($"SqlState: {ex.SqlState}");
                System.Diagnostics.Debug.WriteLine($"Message: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"SQL: {sql}");
                System.Diagnostics.Debug.WriteLine($"SessionKey: {sessionKey}");
                System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                System.Diagnostics.Debug.WriteLine("====================================================");
            }

            return result;
        }

        /// <summary>One row in MessageIn. A database error is thrown: the caller (MessageProcessingService) counts and logs it.</summary>
        private async Task LogMessageAsync(string sessionKey, string msgType, string rawMessage, MessageDirection direction, DateTime atUtc)
        {
            var sql = @"
                INSERT INTO MessageIn
                (SessionKey, MessageType, RawMessage, ReceivedUtc, Direction)
                VALUES
                (@SessionKey, @MessageType, @RawMessage, @ReceivedUtc, @Direction);";

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@SessionKey", sessionKey);
            command.Parameters.AddWithValue("@MessageType", msgType ?? "?");
            command.Parameters.AddWithValue("@RawMessage", rawMessage ?? string.Empty);
            command.Parameters.AddWithValue("@ReceivedUtc", atUtc);
            command.Parameters.AddWithValue("@Direction", direction.ToString());

            await command.ExecuteNonQueryAsync();
        }

        private string GetMessageSummary(string msgType)
        {
            return msgType switch
            {
                "0" => "Heartbeat",
                "1" => "TestRequest",
                "2" => "ResendRequest",
                "3" => "Reject",
                "4" => "SequenceReset",
                "5" => "Logout",
                "A" => "Logon",
                "8" => "ExecutionReport",
                "AE" => "TradeCaptureReport",
                "AR" => "TradeCaptureReportAck",
                "?" => "Unknown",
                "CREATE" => "Session Created",
                "LOGON" => "Logon Confirmed",
                "LOGOUT" => "Logout Received",
                _ => $"MsgType {msgType}"
            };
        }
    }
}
