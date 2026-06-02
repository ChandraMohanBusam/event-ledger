-- Event Gateway schema (schema-of-record).
-- The in-memory demo builds this same shape via EF Core at startup.

CREATE TABLE IF NOT EXISTS Events (
    EventId         TEXT          NOT NULL PRIMARY KEY,  -- idempotency key
    AccountId       TEXT          NOT NULL,
    Type            TEXT          NOT NULL,              -- 'Credit' or 'Debit'
    Amount          DECIMAL(18,2) NOT NULL,
    Currency        TEXT          NOT NULL,
    EventTimestamp  TEXT          NOT NULL,              -- ISO 8601, producer time (ordering key)
    Metadata        TEXT          NULL,                  -- raw JSON, optional
    ReceivedAt      TEXT          NOT NULL               -- ISO 8601, server time
);

CREATE INDEX IF NOT EXISTS IX_Events_AccountId ON Events (AccountId);
