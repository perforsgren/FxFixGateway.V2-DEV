-- FXOhub quotes: our size per leg, sent as LegQty (687) in millions of the leg's currency. Any size can be quoted, not only
-- the request's (Graham and Chris, TP ICAP, 22 Oct: "The size is populated in FIX tag LegQty (687) for each individual leg").
-- The pricer writes it with each price version (its notionals unlocked); null = the request's quantity (fxohub_rfq_leg).

ALTER TABLE fxvol.fxohub_rfq_price_leg
    ADD COLUMN qty_millions DECIMAL(18,6) NULL COMMENT 'LegQty (687) we quote, millions of the leg currency; null = the request''s' AFTER delta_pct;
