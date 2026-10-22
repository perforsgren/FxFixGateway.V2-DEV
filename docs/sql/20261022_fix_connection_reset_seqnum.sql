-- Per-session sequence number reset. Until now every session reset its sequence numbers on logon, logout and disconnect
-- (SessionSettingsBuilder: ResetOnLogon / ResetOnLogout / ResetOnDisconnect = Y, and 141=Y in our Logon). A session with
-- ResetSeqNumOnLogon = 0 keeps them across logons, so a gap is resent from the message store; QuickFIX still resets them
-- at the start of each session period (StartTime). Bloomberg's certification tests sequence number handling.
-- Run this before starting a gateway built with this column: the session list is read with it.

ALTER TABLE fix_config_dev.fix_connection_config
    ADD COLUMN ResetSeqNumOnLogon TINYINT(1) NOT NULL DEFAULT 1
        COMMENT '1 = reset sequence numbers on logon, logout and disconnect (141=Y); 0 = keep them (Bloomberg)';

-- Check: every existing session keeps resetting (1).
SELECT ConnectionId, SessionKey, VenueCode, ResetSeqNumOnLogon
FROM fix_config_dev.fix_connection_config
ORDER BY SessionKey;
