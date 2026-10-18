-- Removes the test requests of fxohub_rfq_blotter_testdata.sql (session key 'TEST'): their legs and prices go with them
-- (ON DELETE CASCADE); the depth and events are keyed by session.
DELETE FROM fxvol.fxohub_rfq_quote WHERE session_key = 'TEST';
DELETE FROM fxvol.fxohub_rfq_event WHERE session_key = 'TEST';
DELETE FROM fxvol.fxohub_rfq WHERE session_key = 'TEST';
