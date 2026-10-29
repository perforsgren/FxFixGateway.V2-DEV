-- 2026-10-29  Bloomberg FIX connectivity: TLS client certificate per session, the server's certificate checked, and the
-- BETA session for FX Options autopricing (deal code SEKO).
--
-- Bloomberg's FIX Connectivity Policy: every FIX session uses TLS 1.2 with mutual authentication, with a client
-- certificate Bloomberg gives per FIX session (Enterprise Console, valid two years); sequence numbers may only be reset at
-- the agreed time (daily 05:00 UTC). The gateway now writes each session's own StartTime/EndTime (before, every session ran
-- on the first session's in SessionKey order), so check them with query 0 before starting the new gateway.
--
-- 0. Each session's period, now used per session.
-- 1. fix_connection_config: SslCertificate, SslCertificatePassword, SslValidateCertificates, SslCaCertificate.
-- 2. The BETA session BLOOMBERG_OPT_BETA, disabled until the certificate is installed and our IP is whitelisted.
-- 3. When the certificate is installed: set it and enable the session.
--
-- Run 1 before starting a gateway built with these columns (the session list is read with them). 2 is idempotent.

-- ── 0. Each session's period (UTC) ──────────────────────────────────────────
SELECT ConnectionId, SessionKey, VenueCode, IsEnabled, StartTime, EndTime, UseSsl, SslServerName
FROM fix_config_dev.fix_connection_config
ORDER BY SessionKey;

-- ── 1. Columns ──────────────────────────────────────────────────────────────
ALTER TABLE fix_config_dev.fix_connection_config
    ADD COLUMN SslCertificate VARCHAR(500) NULL
        COMMENT 'TLS client certificate: .pfx/.p12 path, or the subject in the Windows store My (LocalMachine, then CurrentUser); CN=... is the distinguished name',
    ADD COLUMN SslCertificatePassword VARCHAR(200) NULL
        COMMENT '.pfx password; NULL for a certificate in the store',
    ADD COLUMN SslValidateCertificates TINYINT(1) NOT NULL DEFAULT 0
        COMMENT '1 = check the server certificate (name = SslServerName, chain = SslCaCertificate or the Windows trust store); 0 = accept any, as before',
    ADD COLUMN SslCaCertificate VARCHAR(500) NULL
        COMMENT 'CA the server certificate chain must end in (path or store subject); NULL = the Windows trust store';

-- ── 2. The BETA session ─────────────────────────────────────────────────────
-- Internet, the BETA NY address only. Period 05:01-04:59 UTC: out of session 04:59-05:01, so the sequence numbers are reset
-- at Bloomberg's daily 05:00 UTC reset with a logout before it and a new logon after it. FIX44_Bloomberg.xml as dictionary.
INSERT INTO fix_config_dev.fix_connection_config (
    SessionKey, VenueCode, ConnectionType, Description, FixVersion,
    Host, Port, SenderCompId, TargetCompId, HeartBtIntSec,
    UseSsl, SslServerName, LogonUsername, Password,
    AckSupported, AckMode, ReconnectIntervalSeconds,
    StartTime, EndTime, UseDataDictionary, DataDictionaryFile,
    IsEnabled, CreatedUtc, UpdatedUtc, UpdatedBy, Notes,
    ResetSeqNumOnLogon, SslCertificate, SslCertificatePassword, SslValidateCertificates, SslCaCertificate)
SELECT
    'BLOOMBERG_OPT_BETA', 'BLOOMBERG', 'initiator', 'Bloomberg FXGO Options autopricing, BETA (deal code SEKO)', 'FIX.4.4',
    '69.191.198.32', 8228, 'SEKOUAT_OPT', 'BLPUAT_OPT', 30,
    1, 'fixbeta-ficc.bloomberg.com', '', '',
    0, '', 30,
    '05:01:00', '04:59:00', 1, 'FIX44_Bloomberg.xml',
    0, UTC_TIMESTAMP(), UTC_TIMESTAMP(), 'P901PEF', 'Sequence numbers reset daily 05:00 UTC (agreed with Bloomberg)',
    0, NULL, NULL, 1, NULL
FROM DUAL
WHERE NOT EXISTS (SELECT 1 FROM fix_config_dev.fix_connection_config WHERE SessionKey = 'BLOOMBERG_OPT_BETA');

SELECT ConnectionId, SessionKey, VenueCode, Host, Port, SenderCompId, TargetCompId, UseSsl, SslServerName,
       SslValidateCertificates, SslCertificate, StartTime, EndTime, ResetSeqNumOnLogon, DataDictionaryFile, IsEnabled
FROM fix_config_dev.fix_connection_config
WHERE SessionKey = 'BLOOMBERG_OPT_BETA';

-- ── 3. When the certificate is installed (run then, with its real subject or path) ──
-- In the Windows store (LocalMachine\My or CurrentUser\My, with its private key): its subject, no password.
-- As a file: the .pfx path and its password.
-- UPDATE fix_config_dev.fix_connection_config
-- SET SslCertificate = 'CN=<the certificate''s subject>', SslCertificatePassword = NULL, IsEnabled = 1, UpdatedUtc = UTC_TIMESTAMP()
-- WHERE SessionKey = 'BLOOMBERG_OPT_BETA';
