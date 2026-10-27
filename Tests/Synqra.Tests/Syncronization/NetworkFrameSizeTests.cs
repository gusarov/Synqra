using Microsoft.Extensions.DependencyInjection;
using Synqra.BinarySerializer;
using Synqra.Tests.TestHelpers;
using System.Runtime.ExceptionServices;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests.Syncronization;

/// <summary>
/// Each overflow of the replication frame's buffer is a thrown exception, so the sender starts a frame in the size an
/// earlier one grew to: a large frame overflows once per size, not on every send.
/// </summary>
internal class NetworkFrameSizeTests : BaseTest
{
	[Test]
	public async Task A_second_large_frame_starts_in_the_size_the_first_grew_to()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSbxSerializer();
		var network = new SbxNetworkSerializationService(services.BuildServiceProvider().GetRequiredService<ISbxSerializerFactory>());
		var large = new SubscriptionState { Streams = Enumerable.Range(0, 1000).Select(_ => Guid.NewGuid()).ToList() };
		var thread = Environment.CurrentManagedThreadId;
		var overflows = 0;
		EventHandler<FirstChanceExceptionEventArgs> count = (_, e) =>
		{
			if (Environment.CurrentManagedThreadId == thread && e.Exception.IsBufferOverflow())
			{
				overflows++;
			}
		};

		var first = network.Serialize<TransportOperation>(large, new byte[EventReplicationService.DefaultFrameSize]);
		AppDomain.CurrentDomain.FirstChanceException += count;
		ArraySegment<byte> second;
		try
		{
			second = network.Serialize<TransportOperation>(large, new byte[EventReplicationService.DefaultFrameSize]);
		}
		finally
		{
			AppDomain.CurrentDomain.FirstChanceException -= count;
		}

		await Assert.That(first.Count).IsGreaterThan(EventReplicationService.DefaultFrameSize);
		await Assert.That(overflows).IsEqualTo(0);
		var back = (SubscriptionState)network.Deserialize<TransportOperation>(second.AsSpan());
		await Assert.That(back.Streams).IsEquivalentTo(large.Streams);
	}
}
