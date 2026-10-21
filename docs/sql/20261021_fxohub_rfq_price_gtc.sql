-- A price can be good till cancelled (GTC): no time stipulation, so no ValidUntilTime (62) is sent and the quote stays
-- until the trader withdraws it (or the spot stipulation pulls it, or FXOhub ends its day). valid_until_utc NULL = GTC.
-- The pricer's time stipulations are now 2, 10 and 60 min (valid_seconds 120, 600, 3600) and GTC (valid_seconds NULL and
-- valid_until_utc NULL); a clock time keeps valid_seconds NULL with valid_until_utc set. Run once.

ALTER TABLE fxvol.fxohub_rfq_price
    MODIFY valid_seconds   INT         NULL COMMENT 'the time stipulation chosen (120, 600, 3600); null for a clock time or GTC',
    MODIFY valid_until_utc DATETIME(3) NULL COMMENT 'ValidUntilTime (62); null = good till cancelled (GTC), 62 not sent';
