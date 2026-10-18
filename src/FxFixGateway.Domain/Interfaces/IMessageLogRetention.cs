using System;
using System.Threading;
using System.Threading.Tasks;

namespace FxFixGateway.Domain.Interfaces
{
    /// <summary>
    /// Deletes old rows from the MessageIn log table (fix_config_dev.MessageIn, the gateway's log of every FIX message), by Id
    /// so a delete never locks the newest rows the log is writing. A database error is thrown.
    /// </summary>
    public interface IMessageLogRetention
    {
        /// <summary>
        /// The Id from which rows are kept: the lowest Id received at or after <paramref name="cutoffUtc"/>, so every row below it
        /// is older; one above the highest Id when every row is older; null when the table is empty.
        /// </summary>
        Task<long?> GetKeepFromIdAsync(DateTime cutoffUtc, CancellationToken ct);

        /// <summary>Deletes up to <paramref name="batchSize"/> rows with an Id below <paramref name="keepFromId"/>, oldest first; returns how many.</summary>
        Task<int> DeleteBelowAsync(long keepFromId, int batchSize, CancellationToken ct);
    }
}
