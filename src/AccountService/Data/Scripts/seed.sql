-- Optional demo data for a real database. Not loaded by the in-memory demo.

INSERT INTO Transactions (TransactionId, AccountId, Type, Amount, Currency, EventTimestamp, CreatedAt)
VALUES
  ('seed-evt-001', 'acct-demo', 'Credit', 500.00, 'USD', '2026-05-15T09:00:00Z', '2026-05-15T09:00:01Z'),
  ('seed-evt-002', 'acct-demo', 'Debit',  120.50, 'USD', '2026-05-15T10:30:00Z', '2026-05-15T10:30:01Z');
