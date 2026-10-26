namespace FxFixGateway.Domain.Interfaces
{
    public interface IGatewayHeartbeatRepository
    {
        Task UpdateBeatAsync(string sessionKey);
        Task SetOfflineAsync(string sessionKey);

        /// <summary>The session logged on at <paramref name="logonUtc"/> (this gateway's clock): fxvol.session_heartbeat.logon_utc.</summary>
        Task SetLogonAsync(string sessionKey, DateTime logonUtc);
    }
}