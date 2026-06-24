using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synqra.AppendStorage.MongoDb;
using Synqra.BinarySerializer;
using Synqra.Projection.MongoDb;
using Synqra.Tests.TestHelpers;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>
/// Component and wire materialization for <see cref="MongoProjection"/>, against a real Mongo
/// (<see cref="EphemeralMongo"/>). Reuses the shared component substrate models
/// (<see cref="TestComponentNode"/> + <see cref="TestUniqueComponent"/> / <see cref="TestTaggingComponent"/>).
/// Mirrors the in-memory component contract (add / unique-constraint / addressed change / delete) and adds
/// the Mongo-specific guarantees: components survive a restart by rehydrating from the container's stored
/// <c>_c</c> array, and wires are persisted.
/// </summary>
[NotInParallel]
public class MongoProjectionComponentTests : BaseTest
{
	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);

		// Reflection-based options so the substrate models materialize to/from JSON without a
		// source-generated context (the projection works with either; a real app supplies its context).
		hostApplicationBuilder.Services.AddSingleton(new JsonSerializerOptions
		{
			TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
		});
		hostApplicationBuilder.Services.AddTypeMetadataProvider(
		[
			typeof(TestComponentNode),
			typeof(TestUniqueComponent),
			typeof(TestTaggingComponent),
			typeof(Command),
			typeof(CreateObjectCommand),
			typeof(ChangeObjectPropertyCommand),
			typeof(AddComponentCommand),
			typeof(ChangeComponentPropertyCommand),
			typeof(DeleteComponentCommand),
			typeof(AddWireCommand),
			typeof(DeleteWireCommand),
			typeof(Wire),
		]);
		hostApplicationBuilder.Services.AddSbxSerializer(ser =>
		{
			ser.Map(210, typeof(TestComponentNode));
			ser.Map(211, typeof(TestUniqueComponent));
			ser.Map(212, typeof(TestTaggingComponent));
			ser.Snapshot();
		});

		hostApplicationBuilder.Services.AddAppendStorageMongoDb<Event>(_connectionString!);
		hostApplicationBuilder.Services.AddMongoDbSynqraStore(_connectionString!);
	}

	IObjectStore Store => ServiceProvider.GetRequiredService<IObjectStore>();
	MongoProjection Projection => (MongoProjection)Store;
	Guid TypeId<T>() => Store.TypeMetadataProvider.GetTypeMetadata(typeof(T)).TypeId;

	TestComponentNode NewNode(string name = "n")
	{
		var node = new TestComponentNode { Name = name };
		Store.GetCollection<TestComponentNode>().Add(node);
		return node;
	}

	[Test]
	public async Task Should_10_add_unique_component()
	{
		var node = NewNode();
		await Store.SubmitCommandAsync(new AddComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetId = Store.GetId(node),
			ComponentTypeId = TypeId<TestUniqueComponent>(),
			Data = new TestUniqueComponent { Subject = "hello" },
		});

		var attached = node.Components.GetUniqueComponent(typeof(TestUniqueComponent));
		await Assert.That(attached).IsNotNull();
		await Assert.That(((TestUniqueComponent)attached!).Subject).IsEqualTo("hello");
		await Assert.That(node.Components.Count).IsEqualTo(1);
	}

	[Test]
	public async Task Should_20_unique_constraint_rejects_second()
	{
		var node = NewNode();
		await Store.SubmitCommandAsync(new AddComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetId = Store.GetId(node),
			ComponentTypeId = TypeId<TestUniqueComponent>(),
			Data = new TestUniqueComponent { Subject = "first" },
		});

		var rejected = false;
		try
		{
			await Store.SubmitCommandAsync(new AddComponentCommand
			{
				CommandId = GuidExtensions.CreateVersion7(),
				TargetId = Store.GetId(node),
				ComponentTypeId = TypeId<TestUniqueComponent>(),
				Data = new TestUniqueComponent { Subject = "second" },
			});
		}
		catch (InvalidOperationException)
		{
			rejected = true;
		}

		await Assert.That(rejected).IsTrue();
		await Assert.That(node.Components.Count).IsEqualTo(1);
	}

	[Test]
	public async Task Should_30_change_property_on_addressed_instance()
	{
		var node = NewNode();
		var aId = GuidExtensions.CreateVersion7();
		var bId = GuidExtensions.CreateVersion7();
		await Store.SubmitCommandAsync(new AddComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetId = Store.GetId(node),
			ComponentTypeId = TypeId<TestTaggingComponent>(),
			Data = new TestTaggingComponent { Id = aId, Tag = "alpha" },
		});
		await Store.SubmitCommandAsync(new AddComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetId = Store.GetId(node),
			ComponentTypeId = TypeId<TestTaggingComponent>(),
			Data = new TestTaggingComponent { Id = bId, Tag = "beta" },
		});
		await Assert.That(node.Components.Count).IsEqualTo(2);

		await Store.SubmitCommandAsync(new ChangeComponentPropertyCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetId = Store.GetId(node),
			ComponentTypeId = TypeId<TestTaggingComponent>(),
			ComponentId = aId,
			PropertyName = nameof(TestTaggingComponent.Tag),
			NewValue = "alpha-edited",
		});

		var aComp = node.Components.OfType<TestTaggingComponent>().Single(x => x.Id == aId);
		var bComp = node.Components.OfType<TestTaggingComponent>().Single(x => x.Id == bId);
		await Assert.That(aComp.Tag).IsEqualTo("alpha-edited");
		await Assert.That(bComp.Tag).IsEqualTo("beta");
	}

	[Test]
	public async Task Should_40_delete_component()
	{
		var node = NewNode();
		await Store.SubmitCommandAsync(new AddComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetId = Store.GetId(node),
			ComponentTypeId = TypeId<TestUniqueComponent>(),
			Data = new TestUniqueComponent { Subject = "doomed" },
		});
		await Assert.That(node.Components.Count).IsEqualTo(1);

		await Store.SubmitCommandAsync(new DeleteComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetId = Store.GetId(node),
			ComponentTypeId = TypeId<TestUniqueComponent>(),
		});
		await Assert.That(node.Components.Count).IsEqualTo(0);
	}

	[Test]
	public async Task Should_50_components_survive_restart()
	{
		var node = NewNode("survivor");
		var id = Store.GetId(node);
		await Store.SubmitCommandAsync(new AddComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetId = id,
			ComponentTypeId = TypeId<TestUniqueComponent>(),
			Data = new TestUniqueComponent { Subject = "persisted" },
		});

		// Brand-new projection over the same Mongo read-model: the container's components must come back
		// by rehydrating the stored _c array, not from RAM.
		Restart();

		var reloaded = Store.GetCollection<TestComponentNode>().FirstOrDefault(n => Store.GetId(n) == id);
		await Assert.That(reloaded).IsNotNull();
		var comp = reloaded!.Components.GetUniqueComponent(typeof(TestUniqueComponent)) as TestUniqueComponent;
		await Assert.That(comp).IsNotNull();
		await Assert.That(comp!.Subject).IsEqualTo("persisted");
	}

	[Test]
	public async Task Should_60_wire_is_persisted()
	{
		var node = NewNode();
		var id = Store.GetId(node);
		await Store.SubmitCommandAsync(new AddWireCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			SourceContainerId = id,
			SourcePortName = "out",
			TargetContainerId = id,
			TargetPortName = "in",
			Type = 0,
		});

		await Assert.That(Projection.Wires.Count).IsEqualTo(1);
		var wire = Projection.Wires.Single();
		await Assert.That(Projection.GetWiresFrom(wire.Source).Count).IsEqualTo(1);
	}

}
