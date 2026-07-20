using Microsoft.Extensions.DependencyInjection;
using Synqra.BinarySerializer;
using Synqra.BlobStorage.Sqlite;
using Synqra.Tests.TestHelpers;

namespace Synqra.Tests;

[NotInParallel]
public class ClientEventStoreTests : BaseTest
{
	private ServiceProvider _services = null!;
	private string _databasePath = null!;
	private BlobClientEventStore _store = null!;

	[Before(Test)]
	public void Setup()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSbxSerializer();
		_services = services.BuildServiceProvider();
		_databasePath = CreateTestFileName("client_replica.db");
		_store = CreateStore();
	}

	[After(Test)]
	public async Task Cleanup()
	{
		await _store.DisposeAsync();
		await _services.DisposeAsync();
	}

	[Test]
	public async Task Pending_batch_survives_reopen_with_command_options_and_events()
	{
		var streamId = GuidExtensions.CreateVersion7();
		var command = CreateCommand(streamId);
		var optimisticEvent = CreateEvent(streamId, command.CommandId);
		var expectedLastEventId = GuidExtensions.CreateVersion7();
		await _store.StageAsync(
			command,
			[optimisticEvent],
			new CommandSubmissionOptions { ExpectedLastEventId = expectedLastEventId }
		);

		_store = CreateStore();
		var pending = await ToListAsync(_store.GetPendingAsync(streamId));
		var visibleEvents = await ToListAsync(_store.GetAllAsync());

		await Assert.That(pending).HasCount(1);
		await Assert.That(pending[0].Command.CommandId).IsEqualTo(command.CommandId);
		await Assert.That(pending[0].Options?.ExpectedLastEventId).IsEqualTo(expectedLastEventId);
		await Assert.That(pending[0].Events).HasCount(1);
		await Assert.That(visibleEvents.Select(x => x.EventId)).IsEquivalentTo([optimisticEvent.EventId]);
	}

	[Test]
	public async Task Confirmed_events_for_pending_command_stay_hidden_until_acknowledgement()
	{
		var streamId = GuidExtensions.CreateVersion7();
		var command = CreateCommand(streamId);
		var optimisticEvent = CreateEvent(streamId, command.CommandId, "optimistic");
		var authoritativeEvent = CreateEvent(streamId, command.CommandId, "authoritative");
		await _store.StageAsync(command, [optimisticEvent]);

		var changedBeforeAcknowledgement = await _store.UpsertConfirmedAsync(authoritativeEvent);
		var beforeAcknowledgement = await ToListAsync(_store.GetAllAsync());
		var acknowledged = await _store.AcknowledgeAsync(command.CommandId);
		var afterAcknowledgement = await ToListAsync(_store.GetAllAsync());

		await Assert.That(changedBeforeAcknowledgement).IsEqualTo(ClientEventStoreChange.None);
		await Assert.That(beforeAcknowledgement.Select(x => x.EventId)).IsEquivalentTo([optimisticEvent.EventId]);
		await Assert.That(acknowledged).IsEqualTo(ClientEventStoreChange.Rebuild);
		await Assert.That(afterAcknowledgement.Select(x => x.EventId)).IsEquivalentTo([authoritativeEvent.EventId]);
	}

	[Test]
	public async Task Matching_authoritative_events_replace_pending_batch_without_rebuild()
	{
		var streamId = GuidExtensions.CreateVersion7();
		var command = CreateCommand(streamId);
		var optimisticEvent = CreateEvent(streamId, command.CommandId);
		await _store.StageAsync(command, [optimisticEvent]);

		var upsert = await _store.UpsertConfirmedAsync(optimisticEvent);
		var acknowledgement = await _store.AcknowledgeAsync(command.CommandId);

		await Assert.That(upsert).IsEqualTo(ClientEventStoreChange.None);
		await Assert.That(acknowledgement).IsEqualTo(ClientEventStoreChange.None);
	}

	[Test]
	public async Task Remote_confirmed_event_requires_rebuild_when_stream_has_pending_tail()
	{
		var streamId = GuidExtensions.CreateVersion7();
		var pendingCommand = CreateCommand(streamId);
		await _store.StageAsync(
			pendingCommand,
			[CreateEvent(streamId, pendingCommand.CommandId)]
		);
		var remoteEvent = CreateEvent(streamId, GuidExtensions.CreateVersion7());

		var change = await _store.UpsertConfirmedAsync(remoteEvent);

		await Assert.That(change).IsEqualTo(ClientEventStoreChange.Rebuild);
	}

	[Test]
	public async Task Repairing_confirmed_records_does_not_remove_pending_batches()
	{
		var streamId = GuidExtensions.CreateVersion7();
		var confirmedCommand = CreateCommand(streamId);
		var confirmedEvent = CreateEvent(streamId, confirmedCommand.CommandId);
		await _store.UpsertConfirmedAsync(confirmedEvent);
		var pendingCommand = CreateCommand(streamId);
		var pendingEvent = CreateEvent(streamId, pendingCommand.CommandId);
		await _store.StageAsync(pendingCommand, [pendingEvent]);

		await _store.DeleteConfirmedAsync(confirmedEvent.EventId);
		var pending = await ToListAsync(_store.GetPendingAsync(streamId));
		var visibleEvents = await ToListAsync(_store.GetAllAsync());

		await Assert.That(pending.Select(x => x.Command.CommandId)).IsEquivalentTo([pendingCommand.CommandId]);
		await Assert.That(visibleEvents.Select(x => x.EventId)).IsEquivalentTo([pendingEvent.EventId]);
	}

	[Test]
	public async Task Confirmed_digest_changes_when_record_content_is_repaired()
	{
		var streamId = GuidExtensions.CreateVersion7();
		var eventId = GuidExtensions.CreateVersion7();
		var first = CreateEvent(streamId, GuidExtensions.CreateVersion7(), "first", eventId);
		await _store.UpsertConfirmedAsync(first);
		var firstDigest = (await ToListAsync(_store.GetConfirmedDigestsAsync(streamId))).Single();

		var repaired = CreateEvent(streamId, first.CommandId, "repaired", eventId);
		await _store.UpsertConfirmedAsync(repaired);
		var repairedDigest = (await ToListAsync(_store.GetConfirmedDigestsAsync(streamId))).Single();

		await Assert.That(repairedDigest.EventId).IsEqualTo(eventId);
		await Assert.That(repairedDigest.Hash).IsNotEqualTo(firstDigest.Hash);
	}

	[Test]
	public async Task Protocol_round_trips_hash_inventory_and_command_submission()
	{
		var codec = new ReplicationRecordCodec(_services.GetRequiredService<ISbxSerializerFactory>());
		var protocol = new ReplicationProtocol(codec);
		var streamId = GuidExtensions.CreateVersion7();
		var command = CreateCommand(streamId);
		var optimisticEvent = CreateEvent(streamId, command.CommandId);
		var batch = new PendingCommandBatch
		{
			Command = command,
			Events = [optimisticEvent],
			Options = new CommandSubmissionOptions { ExpectedLastEventId = GuidExtensions.CreateVersion7() },
		};
		var digest = new ConfirmedEventDigest(GuidExtensions.CreateVersion7(), 123u);

		var hello = protocol.ReadHello(protocol.CreateHello(456ul, [digest]));
		var submitted = protocol.ReadSubmittedCommand(protocol.CreateSubmitCommand(batch));

		await Assert.That(hello.Magic).IsEqualTo(456ul);
		await Assert.That(hello.ConfirmedEvents[digest.EventId]).IsEqualTo(digest.Hash);
		await Assert.That(submitted.Command.CommandId).IsEqualTo(command.CommandId);
		await Assert.That(submitted.Command.StreamId).IsEqualTo(streamId);
		await Assert.That(submitted.Options?.ExpectedLastEventId).IsEqualTo(batch.Options.ExpectedLastEventId);
		await Assert.That(submitted.Options?.AllocatedEventIds).IsEquivalentTo([optimisticEvent.EventId]);
		await Assert.That(protocol.HashEvent(optimisticEvent))
			.IsEqualTo(codec.HashConfirmedRecord(codec.EncodeConfirmedRecord(optimisticEvent)));
	}

	private BlobClientEventStore CreateStore()
	{
		var options = new SqliteBlobStorageOptions
		{
			ConnectionString = $"Data Source={_databasePath}",
		};
		return new BlobClientEventStore(
			  new SqliteBlobStorage<Guid>(options, "confirmed-events")
			, new SqliteBlobStorage<Guid>(options, "pending-command-batches")
			, new ReplicationRecordCodec(_services.GetRequiredService<ISbxSerializerFactory>())
		);
	}

	private ChangeObjectPropertyCommand CreateCommand(Guid streamId)
	{
		return new ChangeObjectPropertyCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			StreamId = streamId,
			TargetId = GuidExtensions.CreateVersion7(),
			TargetTypeId = GuidExtensions.CreateVersion7(),
			CollectionId = GuidExtensions.CreateVersion7(),
			PropertyName = "Subject",
			OldValue = "old",
			NewValue = "new",
		};
	}

	private ObjectPropertyChangedEvent CreateEvent(
		  Guid streamId
		, Guid commandId
		, string value = "value"
		, Guid? eventId = null
	)
	{
		return new ObjectPropertyChangedEvent
		{
			EventId = eventId ?? GuidExtensions.CreateVersion7(),
			CommandId = commandId,
			StreamId = streamId,
			TargetId = GuidExtensions.CreateVersion7(),
			TargetTypeId = GuidExtensions.CreateVersion7(),
			CollectionId = GuidExtensions.CreateVersion7(),
			PropertyName = "Subject",
			OldValue = "old",
			NewValue = value,
		};
	}

	private async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
	{
		var result = new List<T>();
		await foreach (var item in source)
		{
			result.Add(item);
		}
		return result;
	}
}
