using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using piano_mailchimp_webhook.Config;
using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public sealed class PianoWebhookEventStore(IOptions<EventStoreOptions> options) : IPianoWebhookEventStore
{
    private const string DeduplicationKeyIndexName = "IX_PianoWebhookEvents_DeduplicationKey";
    private static readonly Regex SqlIdentifierPattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly SemaphoreSlim InitializationGate = new(1, 1);
    private readonly EventStoreOptions _options = options.Value;
    private readonly string _qualifiedTableName = $"{EscapeSqlIdentifier(options.Value.Schema)}.{EscapeSqlIdentifier(options.Value.TableName)}";
    private volatile bool _initialized;

    public async Task<PianoWebhookEventRecord> SaveReceivedAsync(
        PianoWebhookEvent? webhookEvent,
        string rawPayload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawPayload);

        await EnsureInitializedAsync(cancellationToken);

        var deduplicationKey = BuildDeduplicationKey(webhookEvent);
        var receivedAt = DateTimeOffset.UtcNow;

        await using var connection = new SqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

        var isDuplicate = !string.IsNullOrWhiteSpace(deduplicationKey) &&
            await HasExistingRecordAsync(connection, transaction, deduplicationKey, cancellationToken);

        var record = new PianoWebhookEventRecord
        {
            DeduplicationKey = deduplicationKey,
            Event = webhookEvent?.Event?.Trim(),
            Uid = webhookEvent?.Uid?.Trim(),
            Aid = webhookEvent?.Aid?.Trim(),
            Timestamp = webhookEvent?.Timestamp?.Trim(),
            RawPayload = rawPayload,
            ReceivedAt = receivedAt,
            ProcessedAt = isDuplicate ? receivedAt : null,
            Status = isDuplicate ? PianoWebhookEventStatuses.Duplicate : PianoWebhookEventStatuses.Received,
            ErrorMessage = isDuplicate ? "Duplicate webhook ignored." : null
        };

        await InsertRecordAsync(connection, transaction, record, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return record;
    }

    public async Task MarkProcessedAsync(string recordId, CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(recordId, PianoWebhookEventStatuses.Processed, null, cancellationToken);
    }

    public async Task MarkFailedAsync(
        string recordId,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(recordId, PianoWebhookEventStatuses.Failed, errorMessage, cancellationToken);
    }

    public async Task MarkInvalidPayloadAsync(
        string recordId,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(recordId, PianoWebhookEventStatuses.InvalidPayload, errorMessage, cancellationToken);
    }

    private async Task UpdateStatusAsync(
        string recordId,
        string status,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {_qualifiedTableName}
            SET ProcessedAt = @ProcessedAt,
                Status = @Status,
                ErrorMessage = @ErrorMessage
            WHERE Id = @Id;
            """;
        command.Parameters.Add(new SqlParameter("@ProcessedAt", SqlDbType.DateTimeOffset) { Value = DateTimeOffset.UtcNow });
        command.Parameters.Add(new SqlParameter("@Status", SqlDbType.NVarChar, 64) { Value = status });
        command.Parameters.Add(new SqlParameter("@ErrorMessage", SqlDbType.NVarChar, -1) { Value = (object?)errorMessage ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@Id", SqlDbType.NVarChar, 32) { Value = recordId });

        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            throw new InvalidOperationException($"Webhook event record {recordId} was not found.");
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await InitializationGate.WaitAsync(cancellationToken);

        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = new SqlConnection(GetConnectionString());
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = @SchemaName)
                BEGIN
                    EXEC(N'CREATE SCHEMA {EscapeSqlLiteral(EscapeSqlIdentifier(_options.Schema))}');
                END;

                IF OBJECT_ID(N'{EscapeSqlLiteral(_qualifiedTableName)}', N'U') IS NULL
                BEGIN
                    CREATE TABLE {_qualifiedTableName}
                    (
                        Id nvarchar(32) NOT NULL PRIMARY KEY,
                        DeduplicationKey nvarchar(64) NULL,
                        Event nvarchar(256) NULL,
                        Uid nvarchar(256) NULL,
                        Aid nvarchar(256) NULL,
                        Timestamp nvarchar(128) NULL,
                        RawPayload nvarchar(max) NOT NULL,
                        ReceivedAt datetimeoffset(7) NOT NULL,
                        ProcessedAt datetimeoffset(7) NULL,
                        Status nvarchar(64) NOT NULL,
                        ErrorMessage nvarchar(max) NULL
                    );
                END;

                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = N'{DeduplicationKeyIndexName}'
                      AND object_id = OBJECT_ID(N'{EscapeSqlLiteral(_qualifiedTableName)}', N'U'))
                BEGIN
                    CREATE INDEX {DeduplicationKeyIndexName}
                        ON {_qualifiedTableName} (DeduplicationKey)
                        WHERE DeduplicationKey IS NOT NULL;
                END;
                """;
            command.Parameters.Add(new SqlParameter("@SchemaName", SqlDbType.NVarChar, 128) { Value = _options.Schema });
            await command.ExecuteNonQueryAsync(cancellationToken);

            _initialized = true;
        }
        finally
        {
            InitializationGate.Release();
        }
    }

    private async Task<bool> HasExistingRecordAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string deduplicationKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT TOP (1) 1
            FROM {_qualifiedTableName} WITH (UPDLOCK, HOLDLOCK)
            WHERE DeduplicationKey = @DeduplicationKey;
            """;
        command.Parameters.Add(new SqlParameter("@DeduplicationKey", SqlDbType.NVarChar, 64) { Value = deduplicationKey });

        var existing = await command.ExecuteScalarAsync(cancellationToken);
        return existing is not null;
    }

    private async Task InsertRecordAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PianoWebhookEventRecord record,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {_qualifiedTableName}
            (
                Id,
                DeduplicationKey,
                Event,
                Uid,
                Aid,
                Timestamp,
                RawPayload,
                ReceivedAt,
                ProcessedAt,
                Status,
                ErrorMessage
            )
            VALUES
            (
                @Id,
                @DeduplicationKey,
                @Event,
                @Uid,
                @Aid,
                @Timestamp,
                @RawPayload,
                @ReceivedAt,
                @ProcessedAt,
                @Status,
                @ErrorMessage
            );
            """;
        command.Parameters.Add(new SqlParameter("@Id", SqlDbType.NVarChar, 32) { Value = record.Id });
        command.Parameters.Add(new SqlParameter("@DeduplicationKey", SqlDbType.NVarChar, 64) { Value = (object?)record.DeduplicationKey ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@Event", SqlDbType.NVarChar, 256) { Value = (object?)record.Event ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@Uid", SqlDbType.NVarChar, 256) { Value = (object?)record.Uid ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@Aid", SqlDbType.NVarChar, 256) { Value = (object?)record.Aid ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@Timestamp", SqlDbType.NVarChar, 128) { Value = (object?)record.Timestamp ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@RawPayload", SqlDbType.NVarChar, -1) { Value = record.RawPayload });
        command.Parameters.Add(new SqlParameter("@ReceivedAt", SqlDbType.DateTimeOffset) { Value = record.ReceivedAt });
        command.Parameters.Add(new SqlParameter("@ProcessedAt", SqlDbType.DateTimeOffset) { Value = (object?)record.ProcessedAt ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@Status", SqlDbType.NVarChar, 64) { Value = record.Status });
        command.Parameters.Add(new SqlParameter("@ErrorMessage", SqlDbType.NVarChar, -1) { Value = (object?)record.ErrorMessage ?? DBNull.Value });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string GetConnectionString()
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new InvalidOperationException("EventStore ConnectionString is not configured.");
        }

        return _options.ConnectionString;
    }

    private static string EscapeSqlIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !SqlIdentifierPattern.IsMatch(value))
        {
            throw new InvalidOperationException(
                $"SQL identifier '{value}' is invalid. Use letters, numbers, and underscores only.");
        }

        return $"[{value}]";
    }

    private static string EscapeSqlLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string? BuildDeduplicationKey(PianoWebhookEvent? webhookEvent)
    {
        var eventName = webhookEvent?.Event?.Trim();
        var uid = webhookEvent?.Uid?.Trim();
        var timestamp = webhookEvent?.Timestamp?.Trim();

        if (string.IsNullOrWhiteSpace(eventName) ||
            string.IsNullOrWhiteSpace(uid) ||
            string.IsNullOrWhiteSpace(timestamp))
        {
            return null;
        }

        var rawKey = $"{uid}\n{eventName}\n{timestamp}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));

        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
