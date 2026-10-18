-- FXOhub RFQs: who picked a request up in the pricer (OptionSuite's Incoming RFQ blotter).
-- The pricer sets status PICKED_UP / INCOMING (released) / PASSED with conditional updates, so the first trader wins
-- and every pricer sees who has the request. The gateway's own writes leave these columns alone (a resent request
-- keeps its status; a QuoteRequestReject still sets CANCELLED).

ALTER TABLE fxvol.fxohub_rfq
    ADD COLUMN picked_up_by  VARCHAR(32)  NULL AFTER status_utc,
    ADD COLUMN picked_up_utc DATETIME(3)  NULL AFTER picked_up_by;
