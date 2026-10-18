using FxFixGateway.Domain.ValueObjects;

namespace FxFixGateway.Domain.Interfaces
{
    public interface ISecurityListService
    {
        /// <summary>
        /// Called on QuickFIX's session thread for each 35=y (a list: one message can hold several instruments, tag
        /// 146=NoRelatedSym). Only queues it: the messages are handled one at a time in arrival order by a background task,
        /// so the list's last fragment is handled after all the others.
        /// </summary>
        void Enqueue(string sessionKey, IReadOnlyList<SecurityInstrumentDto> instruments);
    }
}