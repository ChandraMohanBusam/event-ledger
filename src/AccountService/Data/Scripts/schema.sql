-- Account Service schema (schema-of-record).
-- The in-memory demo builds this same shape via EF Core at startup. These
-- scripts are kept in sync with the EF model so a move to a real database
-- (file-based SQLite, SQL Server, PostgreSQL) is a connection-string change.

CREATE TABLE IF NOT EXISTS Transactions (
    TransactionId   TEXT        NOT NULL PRIMARY KEY,   -- idempotency key (== originating event id)
    AccountId       TEXT        NOT NULL,
    Type            TEXT        NOT NULL,                -- 'Credit' or 'Debit'
    Amount          DECIMAL(18,2) NOT NULL,
    Currency        TEXT        NOT NULL,
    EventTimestamp  TEXT        NOT NULL,                -- ISO 8601, producer time
    CreatedAt       TEXT        NOT NULL                 -- ISO 8601, server time
);

CREATE INDEX IF NOT EXISTS IX_Transactions_AccountId ON Transactions (AccountId);
