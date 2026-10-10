using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Servers;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Repository;

/// <summary>
/// AB#6435: communication-state writes repeat on a transient MongoDB write conflict (max 2 retries)
/// and never on other errors.
/// </summary>
internal class StateWriteRetryTests
{
    private static MongoCommandException WriteConflict() =>
        new(new ConnectionId(new ServerId(new ClusterId(1), new System.Net.DnsEndPoint("localhost", 27017))),
            "Write conflict during plan execution", new BsonDocument(),
            new BsonDocument { { "code", 112 }, { "codeName", "WriteConflict" } });

    private static Task NoDelay(TimeSpan _) => Task.CompletedTask;

    [Test]
    public async Task Execute_WriteConflictThenSuccess_RetriesAndSucceeds()
    {
        var calls = 0;

        await StateWriteRetry.ExecuteAsync(() =>
        {
            calls++;
            // The repository wraps the Mongo error, so the cause sits in InnerException.
            return calls == 1
                ? throw new InvalidOperationException("wrapped", WriteConflict())
                : Task.CompletedTask;
        }, NullLogger.Instance, "test", NoDelay);

        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task Execute_TransientLabel_IsRetried()
    {
        var calls = 0;
        var ex = new MongoException("aborted");
        ex.AddErrorLabel("TransientTransactionError");

        await StateWriteRetry.ExecuteAsync(() => ++calls == 1 ? throw ex : Task.CompletedTask,
            NullLogger.Instance, "test", NoDelay);

        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task Execute_PersistentConflict_StopsAfterTwoRetriesAndRethrowsOriginal()
    {
        var calls = 0;
        var original = WriteConflict();

        var thrown = await Assert.ThrowsAsync<MongoCommandException>(() =>
            StateWriteRetry.ExecuteAsync(() =>
            {
                calls++;
                throw original;
            }, NullLogger.Instance, "test", NoDelay));

        await Assert.That(calls).IsEqualTo(1 + StateWriteRetry.MaxRetries);
        await Assert.That(thrown).IsSameReferenceAs(original);
    }

    [Test]
    public async Task Execute_NonTransientError_IsNotRetried()
    {
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StateWriteRetry.ExecuteAsync(() =>
            {
                calls++;
                throw new InvalidOperationException("validation failed", new MongoException("duplicate key"));
            }, NullLogger.Instance, "test", NoDelay));

        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task IsTransient_UnrelatedCommandError_IsFalse()
    {
        var ex = new MongoCommandException(
            new ConnectionId(new ServerId(new ClusterId(1), new System.Net.DnsEndPoint("localhost", 27017))),
            "Unauthorized", new BsonDocument(), new BsonDocument { { "code", 13 } });

        await Assert.That(StateWriteRetry.IsTransient(ex)).IsFalse();
        await Assert.That(StateWriteRetry.IsTransient(null)).IsFalse();
    }
}
