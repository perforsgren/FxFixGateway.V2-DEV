-- FXOhub sends LegID (7940) as a GUID (UAT 9 Oct: 7940=cdfc7701-db48-42bd-a7f9-a48806c3853d, 36 characters), not as
-- Leg1, Leg2 as the specification's example. The columns were VARCHAR(16): with MySQL's strict mode the request (and
-- the depth's leg entries) could not be stored. Widened to 64, as QuoteReqID.

ALTER TABLE fxvol.fxohub_rfq_leg
    MODIFY COLUMN leg_id VARCHAR(64) NULL;                  -- 7940

ALTER TABLE fxvol.fxohub_rfq_quote_entry
    MODIFY COLUMN leg_id VARCHAR(64) NULL;                  -- 7940
