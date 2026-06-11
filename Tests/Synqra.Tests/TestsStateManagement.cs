using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synqra.AppendStorage;
using Synqra.Projection.InMemory;
#if Sqlite && NET10_0_OR_GREATER
using Microsoft.EntityFrameworkCore;
using Synqra.Projection.Sqlite;
#endif
using Synqra.Tests.SampleModels;
using Synqra.Tests.TestHelpers;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Synqra.BinarySerializer;
using Synqra.Projection.File;
using Synqra.AppendStorage.BlobStorage.File;
using Synqra.BlobStorage.File;
using Synqra.Projection;
using Synqra.AppendStorage.JsonLines;
using Synqra.Tests.SampleModels.Syncronization;

namespace Synqra.Tests;

using IAppendStorage = IAppendStorage<Event, Guid>;

[InheritsTests]
public class TestsStateManageementInMemory : TestsStateManagement
{
	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);
		hostApplicationBuilder.Services.AddInMemorySynqraStore();
	}

	// ---- Optimistic concurrency tests (InMemoryProjection only) ----
	//
	// These verify the projection-side precondition check, which only the
	// in-memory projection enforces today (file/sqlite accept options for
	// interface conformance but don't reject — see _DI.cs notes).

	[Test]
	public async Task Should_30_track_LastEventId_after_setter_writes()
	{
		var model = new DemoModel();
		_sut.GetCollection<DemoModel>().Add(model);
		var targetId = _sut.GetId(model);

		var afterCreate = _sut.GetLastEventId(targetId);
		await Assert.That(afterCreate).IsNotEqualTo(Guid.Empty);
		// "After ObjectCreatedEvent the projection should have stamped a creation-event id."

		model.Name = "TestName"; // generated setter -> command -> property-changed event

		var afterChange = _sut.GetLastEventId(targetId);
		await Assert.That(afterChange).IsNotEqualTo(Guid.Empty);
		await Assert.That(afterChange).IsNotEqualTo(afterCreate);
		// "Each applied event advances LastEventId to its own EventId."
	}

	[Test]
	public async Task Should_31_accept_command_with_current_ExpectedLastEventId()
	{
		var model = new DemoModel();
		_sut.GetCollection<DemoModel>().Add(model);
		var targetId = _sut.GetId(model);

		var current = _sut.GetLastEventId(targetId);

		await _sut.SubmitCommandAsync(
			new ChangeObjectPropertyCommand
			{
				CommandId = GuidExtensions.CreateVersion7(),
				TargetObject = model,
				PropertyName = nameof(model.Name),
				OldValue = null,
				NewValue = "Accepted",
			},
			new CommandSubmissionOptions { ExpectedLastEventId = current });

		await Assert.That(model.Name).IsEqualTo("Accepted");
	}

	[Test]
	public async Task Should_32_reject_command_with_stale_ExpectedLastEventId()
	{
		var model = new DemoModel();
		_sut.GetCollection<DemoModel>().Add(model);
		var targetId = _sut.GetId(model);

		// A nonexistent event id — guaranteed not to match.
		var stale = GuidExtensions.CreateVersion7();

		ConcurrencyException? caught = null;
		try
		{
			await _sut.SubmitCommandAsync(
				new ChangeObjectPropertyCommand
				{
					CommandId = GuidExtensions.CreateVersion7(),
					TargetObject = model,
					PropertyName = nameof(model.Name),
					OldValue = null,
					NewValue = "Should not stick",
				},
				new CommandSubmissionOptions { ExpectedLastEventId = stale });
		}
		catch (ConcurrencyException ex)
		{
			caught = ex;
		}

		await Assert.That(caught).IsNotNull();
		await Assert.That(caught!.TargetId).IsEqualTo(targetId);
		await Assert.That(caught.ExpectedLastEventId).IsEqualTo(stale);
		// The rejected command must NOT have mutated the model — projection state untouched.
		await Assert.That(model.Name).IsNotEqualTo("Should not stick");
	}

	[Test]
	public async Task Should_33_empty_ExpectedLastEventId_bypasses_check()
	{
		var model = new DemoModel();
		_sut.GetCollection<DemoModel>().Add(model);

		// Guid.Empty is the "don't care" sentinel — equivalent to passing no options.
		// This is the path manually-constructed commands take when callers omit options.
		await _sut.SubmitCommandAsync(
			new ChangeObjectPropertyCommand
			{
				CommandId = GuidExtensions.CreateVersion7(),
				TargetObject = model,
				PropertyName = nameof(model.Name),
				OldValue = null,
				NewValue = "Bypassed",
			},
			new CommandSubmissionOptions { ExpectedLastEventId = Guid.Empty });

		await Assert.That(model.Name).IsEqualTo("Bypassed");
	}

	[Test]
	public async Task Should_34_null_options_bypasses_check()
	{
		var model = new DemoModel();
		_sut.GetCollection<DemoModel>().Add(model);

		// No options at all — the default-null path. Should behave the same
		// as Guid.Empty: no check, last-writer-wins semantics.
		await _sut.SubmitCommandAsync(
			new ChangeObjectPropertyCommand
			{
				CommandId = GuidExtensions.CreateVersion7(),
				TargetObject = model,
				PropertyName = nameof(model.Name),
				OldValue = null,
				NewValue = "NoOptions",
			});

		await Assert.That(model.Name).IsEqualTo("NoOptions");
	}

	// ---- Phase C: component substrate runtime coverage ----
	//
	// These use manually-constructed commands (Phase A substrate is complete; the
	// generator integration that lets you write `node.Components.Add(c)` and have
	// it emit the command for you is Phase B and will live in its own commit).
	// Until then, the substrate is reachable by passing AddComponentCommand /
	// ChangeComponentPropertyCommand / DeleteComponentCommand directly.

	Guid TypeIdOf<T>() => _sut.TypeMetadataProvider.GetTypeMetadata(typeof(T)).TypeId;

	[Test]
	public async Task Should_40_AddComponent_attaches_to_container()
	{
		var node = new TestComponentNode { Name = "n1" };
		_sut.GetCollection<TestComponentNode>().Add(node);

		var component = new TestUniqueComponent { Subject = "hello" };

		await _sut.SubmitCommandAsync(new AddComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetObject = node,
			ComponentTypeId = TypeIdOf<TestUniqueComponent>(),
			Data = component,
		});

		var attached = node.Components.GetUniqueComponent(typeof(TestUniqueComponent));
		await Assert.That(attached).IsNotNull();
		await Assert.That(attached).IsSameReferenceAs(component);
		await Assert.That(node.Components.Count).IsEqualTo(1);
	}

	[Test]
	public async Task Should_41_AddComponent_unique_constraint_rejects_second()
	{
		var node = new TestComponentNode { Name = "n2" };
		_sut.GetCollection<TestComponentNode>().Add(node);

		await _sut.SubmitCommandAsync(new AddComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetObject = node,
			ComponentTypeId = TypeIdOf<TestUniqueComponent>(),
			Data = new TestUniqueComponent { Subject = "first" },
		});

		// Adding a second [Component(IsUnique = true)] of the same type must fail
		// during event apply (the projection refuses to attach when the unique slot
		// is filled).
		InvalidOperationException? caught = null;
		try
		{
			await _sut.SubmitCommandAsync(new AddComponentCommand
			{
				CommandId = GuidExtensions.CreateVersion7(),
				TargetObject = node,
				ComponentTypeId = TypeIdOf<TestUniqueComponent>(),
				Data = new TestUniqueComponent { Subject = "second" },
			});
		}
		catch (InvalidOperationException ex)
		{
			caught = ex;
		}

		await Assert.That(caught).IsNotNull();
		await Assert.That(node.Components.Count).IsEqualTo(1);
		await Assert.That(((TestUniqueComponent)node.Components.GetUniqueComponent(typeof(TestUniqueComponent))!).Subject)
			.IsEqualTo("first");
	}

	[Test]
	public async Task Should_42_ChangeComponentProperty_updates_unique_component_by_type()
	{
		var node = new TestComponentNode { Name = "n3" };
		_sut.GetCollection<TestComponentNode>().Add(node);

		var c = new TestUniqueComponent { Subject = "before" };
		await _sut.SubmitCommandAsync(new AddComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetObject = node,
			ComponentTypeId = TypeIdOf<TestUniqueComponent>(),
			Data = c,
		});

		// Unique components are addressed by type alone; ComponentId stays empty.
		await _sut.SubmitCommandAsync(new ChangeComponentPropertyCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetObject = node,
			ComponentTypeId = TypeIdOf<TestUniqueComponent>(),
			PropertyName = nameof(c.Subject),
			OldValue = "before",
			NewValue = "after",
		});

		await Assert.That(c.Subject).IsEqualTo("after");
	}

	[Test]
	public async Task Should_43_NonUnique_components_addressed_by_id()
	{
		var node = new TestComponentNode { Name = "n4" };
		_sut.GetCollection<TestComponentNode>().Add(node);

		var aId = GuidExtensions.CreateVersion7();
		var bId = GuidExtensions.CreateVersion7();

		await _sut.SubmitCommandAsync(new AddComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetObject = node,
			ComponentTypeId = TypeIdOf<TestTaggingComponent>(),
			ComponentId = aId,
			Data = new TestTaggingComponent { Id = aId, Tag = "alpha" },
		});

		await _sut.SubmitCommandAsync(new AddComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetObject = node,
			ComponentTypeId = TypeIdOf<TestTaggingComponent>(),
			ComponentId = bId,
			Data = new TestTaggingComponent { Id = bId, Tag = "beta" },
		});

		await Assert.That(node.Components.Count).IsEqualTo(2);

		// Change just the second one. ComponentId disambiguates among instances of the same type.
		await _sut.SubmitCommandAsync(new ChangeComponentPropertyCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetObject = node,
			ComponentTypeId = TypeIdOf<TestTaggingComponent>(),
			ComponentId = bId,
			PropertyName = nameof(TestTaggingComponent.Tag),
			OldValue = "beta",
			NewValue = "beta-updated",
		});

		var aComp = node.Components.OfType<TestTaggingComponent>().Single(x => x.Id == aId);
		var bComp = node.Components.OfType<TestTaggingComponent>().Single(x => x.Id == bId);
		await Assert.That(aComp.Tag).IsEqualTo("alpha");
		await Assert.That(bComp.Tag).IsEqualTo("beta-updated");
	}

	[Test]
	public async Task Should_44_DeleteComponent_removes_from_collection()
	{
		var node = new TestComponentNode { Name = "n5" };
		_sut.GetCollection<TestComponentNode>().Add(node);

		await _sut.SubmitCommandAsync(new AddComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetObject = node,
			ComponentTypeId = TypeIdOf<TestUniqueComponent>(),
			Data = new TestUniqueComponent { Subject = "doomed" },
		});
		await Assert.That(node.Components.Count).IsEqualTo(1);

		await _sut.SubmitCommandAsync(new DeleteComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetObject = node,
			ComponentTypeId = TypeIdOf<TestUniqueComponent>(),
		});

		await Assert.That(node.Components.Count).IsEqualTo(0);
		await Assert.That(node.Components.GetUniqueComponent(typeof(TestUniqueComponent))).IsNull();
	}

	[Test]
	public async Task Should_45_Component_edit_advances_container_LastEventId()
	{
		var node = new TestComponentNode { Name = "n6" };
		_sut.GetCollection<TestComponentNode>().Add(node);
		var nodeId = _sut.GetId(node);

		var beforeAdd = _sut.GetLastEventId(nodeId);
		await _sut.SubmitCommandAsync(new AddComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetObject = node,
			ComponentTypeId = TypeIdOf<TestUniqueComponent>(),
			Data = new TestUniqueComponent { Subject = "x" },
		});
		var afterAdd = _sut.GetLastEventId(nodeId);
		await Assert.That(afterAdd).IsNotEqualTo(beforeAdd);
		// "Container's LastEventId advances on ComponentAddedEvent so concurrent edits conflict at container granularity."

		await _sut.SubmitCommandAsync(new ChangeComponentPropertyCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetObject = node,
			ComponentTypeId = TypeIdOf<TestUniqueComponent>(),
			PropertyName = nameof(TestUniqueComponent.Subject),
			OldValue = "x",
			NewValue = "y",
		});
		var afterChange = _sut.GetLastEventId(nodeId);
		await Assert.That(afterChange).IsNotEqualTo(afterAdd);

		await _sut.SubmitCommandAsync(new DeleteComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetObject = node,
			ComponentTypeId = TypeIdOf<TestUniqueComponent>(),
		});
		var afterDelete = _sut.GetLastEventId(nodeId);
		await Assert.That(afterDelete).IsNotEqualTo(afterChange);
	}

	[Test]
	public async Task Should_46_Stale_LastEventId_rejects_AddComponent()
	{
		var node = new TestComponentNode { Name = "n7" };
		_sut.GetCollection<TestComponentNode>().Add(node);

		// Forge a "stale" precondition. The component attach must be rejected
		// with no events produced; the container's state stays unchanged.
		var stale = GuidExtensions.CreateVersion7();

		ConcurrencyException? caught = null;
		try
		{
			await _sut.SubmitCommandAsync(
				new AddComponentCommand
				{
					CommandId = GuidExtensions.CreateVersion7(),
					TargetObject = node,
					ComponentTypeId = TypeIdOf<TestUniqueComponent>(),
					Data = new TestUniqueComponent { Subject = "ghost" },
				},
				new CommandSubmissionOptions { ExpectedLastEventId = stale });
		}
		catch (ConcurrencyException ex)
		{
			caught = ex;
		}

		await Assert.That(caught).IsNotNull();
		await Assert.That(node.Components.Count).IsEqualTo(0);
	}

	// ---- Phase B: natural-surface coverage (generator-emitted component setters) ----
	//
	// These don't construct ChangeComponentPropertyCommand by hand. They write
	// `c.PropertyName = value` and rely on the generator to emit the command
	// targeting the container.

	[Test]
	public async Task Should_50_Component_property_setter_emits_ChangeComponentPropertyCommand()
	{
		var node = new TestComponentNode { Name = "host" };
		_sut.GetCollection<TestComponentNode>().Add(node);

		var c = new TestUniqueComponent { Subject = "v1" };
		await _sut.SubmitCommandAsync(new AddComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetObject = node,
			ComponentTypeId = TypeIdOf<TestUniqueComponent>(),
			Data = c,
		});
		await Assert.That(c.Subject).IsEqualTo("v1");

		var commandsBefore = _sut.GetCollection<Command>().OfType<ChangeComponentPropertyCommand>().Count();

		c.Subject = "v2"; // generator-emitted path

		var ccps = _sut.GetCollection<Command>().OfType<ChangeComponentPropertyCommand>().ToArray();
		await Assert.That(ccps.Length).IsEqualTo(commandsBefore + 1);

		var emitted = ccps.Last();
		await Assert.That(emitted.PropertyName).IsEqualTo(nameof(TestUniqueComponent.Subject));
		await Assert.That(emitted.NewValue).IsEqualTo("v2");
		await Assert.That(emitted.OldValue).IsEqualTo("v1");
		await Assert.That(emitted.TargetId).IsEqualTo(_sut.GetId(node));
		await Assert.That(emitted.ComponentTypeId).IsEqualTo(TypeIdOf<TestUniqueComponent>());
		// Unique component: ComponentId stays Guid.Empty; resolver uses ComponentTypeId only.
		await Assert.That(emitted.ComponentId).IsEqualTo(Guid.Empty);
		await Assert.That(c.Subject).IsEqualTo("v2");
	}

	[Test]
	public async Task Should_51_NonUnique_component_setter_fills_ComponentId_from_Identifiable()
	{
		var node = new TestComponentNode { Name = "host2" };
		_sut.GetCollection<TestComponentNode>().Add(node);

		var tagId = GuidExtensions.CreateVersion7();
		var c = new TestTaggingComponent { Id = tagId, Tag = "alpha" };
		await _sut.SubmitCommandAsync(new AddComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetObject = node,
			ComponentTypeId = TypeIdOf<TestTaggingComponent>(),
			ComponentId = tagId,
			Data = c,
		});

		c.Tag = "beta"; // generator-emitted path

		var ccps = _sut.GetCollection<Command>().OfType<ChangeComponentPropertyCommand>().ToArray();
		var emitted = ccps.Last();
		await Assert.That(emitted.ComponentTypeId).IsEqualTo(TypeIdOf<TestTaggingComponent>());
		// Non-unique component: generator fills ComponentId from IIdentifiable<Guid>.Id.
		await Assert.That(emitted.ComponentId).IsEqualTo(tagId);
		await Assert.That(emitted.TargetId).IsEqualTo(_sut.GetId(node));
		await Assert.That(c.Tag).IsEqualTo("beta");
	}

	[Test]
	public async Task Should_52_Component_setter_uses_container_LastEventId_for_concurrency()
	{
		// The optimistic-concurrency precondition emitted by a component setter
		// must probe the *container* (the conflict-boundary aggregate), not the
		// component itself. Verify by mutating the container between adding the
		// component and writing to the component — the write should still succeed
		// because the generator reads the freshest LastEventId at write time.
		var node = new TestComponentNode { Name = "concurrency-host" };
		_sut.GetCollection<TestComponentNode>().Add(node);
		var c = new TestUniqueComponent { Subject = "v1" };
		await _sut.SubmitCommandAsync(new AddComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetObject = node,
			ComponentTypeId = TypeIdOf<TestUniqueComponent>(),
			Data = c,
		});

		node.Name = "concurrency-host-edited"; // unrelated container write
		var afterUnrelatedWrite = _sut.GetLastEventId(_sut.GetId(node));

		c.Subject = "v2"; // component setter — should succeed against the new LastEventId
		await Assert.That(c.Subject).IsEqualTo("v2");

		var afterComponentWrite = _sut.GetLastEventId(_sut.GetId(node));
		await Assert.That(afterComponentWrite).IsNotEqualTo(afterUnrelatedWrite);
	}

	// ---- Phase B-2: generator-emitted Components collection (StoreBoundComponentsCollection) ----
	//
	// These exercise the symmetric mirror of Phase B's setter-side generator —
	// `container.Components.Add(c)` emits AddComponentCommand and
	// `container.Components.Remove(c)` emits DeleteComponentCommand when the
	// container is store-attached. The projection's event-apply path goes
	// through TryAdd / BypassRemove so it never produces a recursive command.

	[Test]
	public async Task Should_60_Components_Add_emits_AddComponentCommand_when_attached()
	{
		var node = new TestGeneratedContainerNode { Name = "g1" };
		_sut.GetCollection<TestGeneratedContainerNode>().Add(node);

		var commandsBefore = _sut.GetCollection<Command>().OfType<AddComponentCommand>().Count();

		var c = new TestUniqueComponent { Subject = "from generator" };
		node.Components.Add(c); // generator-emitted path

		var acs = _sut.GetCollection<Command>().OfType<AddComponentCommand>().ToArray();
		await Assert.That(acs.Length).IsEqualTo(commandsBefore + 1);

		var emitted = acs.Last();
		await Assert.That(emitted.TargetId).IsEqualTo(_sut.GetId(node));
		await Assert.That(emitted.ComponentTypeId).IsEqualTo(TypeIdOf<TestUniqueComponent>());
		await Assert.That(emitted.ComponentId).IsEqualTo(Guid.Empty);

		var attached = node.Components.GetUniqueComponent(typeof(TestUniqueComponent));
		await Assert.That(attached).IsSameReferenceAs(c);
	}

	[Test]
	public async Task Should_61_Components_Remove_emits_DeleteComponentCommand_when_attached()
	{
		var node = new TestGeneratedContainerNode { Name = "g2" };
		_sut.GetCollection<TestGeneratedContainerNode>().Add(node);

		var c = new TestUniqueComponent { Subject = "doomed" };
		node.Components.Add(c);
		await Assert.That(node.Components.Count).IsEqualTo(1);

		var commandsBefore = _sut.GetCollection<Command>().OfType<DeleteComponentCommand>().Count();
		node.Components.Remove(c);

		var dcs = _sut.GetCollection<Command>().OfType<DeleteComponentCommand>().ToArray();
		await Assert.That(dcs.Length).IsEqualTo(commandsBefore + 1);

		var emitted = dcs.Last();
		await Assert.That(emitted.TargetId).IsEqualTo(_sut.GetId(node));
		await Assert.That(emitted.ComponentTypeId).IsEqualTo(TypeIdOf<TestUniqueComponent>());

		await Assert.That(node.Components.Count).IsEqualTo(0);
	}

	[Test]
	public async Task Should_62_Components_Add_pre_attach_falls_back_to_direct_mutation()
	{
		// Before the node is added to its store-collection, the wrapper has
		// no store linkage — Add should mutate the inner data directly, just
		// like the property setter does for early initializer-style code.
		var node = new TestGeneratedContainerNode { Name = "g3" };
		var c = new TestUniqueComponent { Subject = "early" };
		node.Components.Add(c);
		await Assert.That(node.Components.Count).IsEqualTo(1);

		// No commands should have been issued — there was no store yet.
		// (We can't observe "no AddComponentCommand related to this node"
		// because the command collection is global; just verify the node's
		// state ended up correct.)
		_sut.GetCollection<TestGeneratedContainerNode>().Add(node);
		await Assert.That(node.Components.Count).IsEqualTo(1);
		await Assert.That(node.Components.GetUniqueComponent(typeof(TestUniqueComponent)))
			.IsSameReferenceAs(c);
	}

	// ---- Wires (Phase Wires) ----
	//
	// First substrate-level slice of the graph: typed connections between
	// component ports. v0 just persists wires + offers projection-side
	// from/to lookups; runtime routing comes in a later slice.

	[Test]
	public async Task Should_70_AddWireCommand_creates_a_wire_with_assigned_id()
	{
		// Two nodes with components participate as wire endpoints.
		var n1 = new TestComponentNode { Name = "src" };
		var n2 = new TestComponentNode { Name = "dst" };
		_sut.GetCollection<TestComponentNode>().Add(n1);
		_sut.GetCollection<TestComponentNode>().Add(n2);

		var srcId = _sut.GetId(n1);
		var dstId = _sut.GetId(n2);
		var srcTypeId = TypeIdOf<TestUniqueComponent>();
		var dstTypeId = TypeIdOf<TestUniqueComponent>();

		await _sut.SubmitCommandAsync(new AddWireCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			SourceContainerId = srcId, SourceComponentTypeId = srcTypeId, SourcePortName = "out",
			TargetContainerId = dstId, TargetComponentTypeId = dstTypeId, TargetPortName = "in",
			Type = (int)PortType.Event,
		});

		var projection = (InMemoryProjection)_sut;
		await Assert.That(projection.Wires.Count).IsEqualTo(1);

		var wire = projection.Wires.Single();
		await Assert.That(wire.Id).IsNotEqualTo(Guid.Empty);
		await Assert.That(wire.SourceContainerId).IsEqualTo(srcId);
		await Assert.That(wire.TargetContainerId).IsEqualTo(dstId);
		await Assert.That(wire.PortType).IsEqualTo(PortType.Event);
	}

	[Test]
	public async Task Should_71_GetWiresFrom_returns_outgoing_wires_only()
	{
		var n1 = new TestComponentNode { Name = "src" };
		var n2 = new TestComponentNode { Name = "dst1" };
		var n3 = new TestComponentNode { Name = "dst2" };
		_sut.GetCollection<TestComponentNode>().Add(n1);
		_sut.GetCollection<TestComponentNode>().Add(n2);
		_sut.GetCollection<TestComponentNode>().Add(n3);

		var src = _sut.GetId(n1);
		var dst1 = _sut.GetId(n2);
		var dst2 = _sut.GetId(n3);
		var ct = TypeIdOf<TestUniqueComponent>();

		await _sut.SubmitCommandAsync(new AddWireCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			SourceContainerId = src, SourceComponentTypeId = ct, SourcePortName = "out",
			TargetContainerId = dst1, TargetComponentTypeId = ct, TargetPortName = "in",
			Type = (int)PortType.Event,
		});
		await _sut.SubmitCommandAsync(new AddWireCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			SourceContainerId = src, SourceComponentTypeId = ct, SourcePortName = "out",
			TargetContainerId = dst2, TargetComponentTypeId = ct, TargetPortName = "in",
			Type = (int)PortType.Event,
		});

		var projection = (InMemoryProjection)_sut;
		var fromSrc = projection.GetWiresFrom(new PortRef(src, ct, Guid.Empty, "out"));
		await Assert.That(fromSrc.Count).IsEqualTo(2);

		var fromUnknown = projection.GetWiresFrom(new PortRef(dst1, ct, Guid.Empty, "out"));
		await Assert.That(fromUnknown.Count).IsEqualTo(0);
	}

	[Test]
	public async Task Should_72b_GetWiresTo_returns_incoming_wires_only()
	{
		var n1 = new TestComponentNode { Name = "src1" };
		var n2 = new TestComponentNode { Name = "src2" };
		var n3 = new TestComponentNode { Name = "dst" };
		_sut.GetCollection<TestComponentNode>().Add(n1);
		_sut.GetCollection<TestComponentNode>().Add(n2);
		_sut.GetCollection<TestComponentNode>().Add(n3);

		var src1 = _sut.GetId(n1);
		var src2 = _sut.GetId(n2);
		var dst = _sut.GetId(n3);
		var ct = TypeIdOf<TestUniqueComponent>();

		await _sut.SubmitCommandAsync(new AddWireCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			SourceContainerId = src1, SourceComponentTypeId = ct, SourcePortName = "out",
			TargetContainerId = dst, TargetComponentTypeId = ct, TargetPortName = "in",
			Type = (int)PortType.Event,
		});
		await _sut.SubmitCommandAsync(new AddWireCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			SourceContainerId = src2, SourceComponentTypeId = ct, SourcePortName = "out",
			TargetContainerId = dst, TargetComponentTypeId = ct, TargetPortName = "in",
			Type = (int)PortType.Event,
		});

		var projection = (InMemoryProjection)_sut;
		var toDst = projection.GetWiresTo(new PortRef(dst, ct, Guid.Empty, "in"));
		await Assert.That(toDst.Count).IsEqualTo(2);

		// Directionality check: src1's "out" IS a source endpoint of one wire.
		// If GetWiresTo accidentally read the from-index, this would return 1
		// instead of 0 — pinning the from/to split.
		var fromIndexLeak = projection.GetWiresTo(new PortRef(src1, ct, Guid.Empty, "out"));
		await Assert.That(fromIndexLeak.Count).IsEqualTo(0);

		// And a fully-unrelated port returns 0 (catches over-broad indexing).
		var toUnknown = projection.GetWiresTo(new PortRef(src1, ct, Guid.Empty, "in"));
		await Assert.That(toUnknown.Count).IsEqualTo(0);
	}

	[Test]
	public async Task Should_72c_AddWireCommand_with_explicit_wireId_round_trips_through_event()
	{
		var n1 = new TestComponentNode { Name = "src" };
		var n2 = new TestComponentNode { Name = "dst" };
		_sut.GetCollection<TestComponentNode>().Add(n1);
		_sut.GetCollection<TestComponentNode>().Add(n2);

		var src = _sut.GetId(n1);
		var dst = _sut.GetId(n2);
		var ct = TypeIdOf<TestUniqueComponent>();
		var explicitId = GuidExtensions.CreateVersion7();

		await _sut.SubmitCommandAsync(new AddWireCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			WireId = explicitId,
			SourceContainerId = src, SourceComponentTypeId = ct, SourcePortName = "out",
			TargetContainerId = dst, TargetComponentTypeId = ct, TargetPortName = "in",
			Type = (int)PortType.Event,
		});

		var projection = (InMemoryProjection)_sut;
		var wire = projection.Wires.Single();
		// Caller's explicit WireId must be honoured — the projection only assigns one
		// when the command came in with Guid.Empty.
		await Assert.That(wire.Id).IsEqualTo(explicitId);
	}

	[Test]
	public async Task Should_72d_DeleteWireCommand_for_unknown_wire_is_a_noop()
	{
		// Delete-without-create should not throw; it just leaves the index untouched.
		await _sut.SubmitCommandAsync(new DeleteWireCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			WireId = GuidExtensions.CreateVersion7(), // never added
		});

		var projection = (InMemoryProjection)_sut;
		await Assert.That(projection.Wires.Count).IsEqualTo(0);
	}

	[Test]
	public async Task Should_72_DeleteWireCommand_removes_wire_from_index()
	{
		var n1 = new TestComponentNode { Name = "src" };
		var n2 = new TestComponentNode { Name = "dst" };
		_sut.GetCollection<TestComponentNode>().Add(n1);
		_sut.GetCollection<TestComponentNode>().Add(n2);

		var src = _sut.GetId(n1);
		var dst = _sut.GetId(n2);
		var ct = TypeIdOf<TestUniqueComponent>();
		var wireId = GuidExtensions.CreateVersion7();

		await _sut.SubmitCommandAsync(new AddWireCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			WireId = wireId,
			SourceContainerId = src, SourceComponentTypeId = ct, SourcePortName = "out",
			TargetContainerId = dst, TargetComponentTypeId = ct, TargetPortName = "in",
			Type = (int)PortType.Event,
		});

		var projection = (InMemoryProjection)_sut;
		await Assert.That(projection.Wires.Count).IsEqualTo(1);

		await _sut.SubmitCommandAsync(new DeleteWireCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			WireId = wireId,
		});

		await Assert.That(projection.Wires.Count).IsEqualTo(0);
		await Assert.That(projection.GetWiresFrom(new PortRef(src, ct, Guid.Empty, "out")).Count).IsEqualTo(0);
	}

	[Test]
	public async Task Should_47_Activator_fires_with_IsReplay_false_on_originating_event()
	{
		var node = new TestComponentNode { Name = "n8" };
		_sut.GetCollection<TestComponentNode>().Add(node);

		var c = new TestActivatableComponent { Marker = "ready" };
		await _sut.SubmitCommandAsync(new AddComponentCommand
		{
			CommandId = GuidExtensions.CreateVersion7(),
			TargetObject = node,
			ComponentTypeId = TypeIdOf<TestActivatableComponent>(),
			Data = c,
		});

		await Assert.That(c.ActivationCount).IsEqualTo(1);
		await Assert.That(c.LastActivationWasReplay).IsEqualTo(false);
		// (Replay path — IsReplay = true — is covered indirectly: LoadStateCoreAsync
		// constructs an EventVisitorContext with IsReplay=true, and the same handler
		// branches on it. A dedicated replay-storage test requires storing real Data
		// payloads, which is Phase D — JSON polymorphism for components.)
	}
}

[InheritsTests]
public class TestsStateManageementFile : TestsStateManagement
{
	string _folder;

	[Before(Test)]
	public void Setup()
	{
		_folder = CreateTestFolder();
	}

	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);
		hostApplicationBuilder.AddFileSynqraStore();
		// IAppendStorage<Event, Guid> stays FakeAppendStorage from base.Register (Reopen carries it across restarts)
		hostApplicationBuilder.AddAppendStorageBlobFile<Event>(e => e.EventId);
		hostApplicationBuilder.AddAppendStorageBlobFile<Command>(e => e.CommandId);
		hostApplicationBuilder.AddAppendStorageBlobFile<Item>(e =>
		{
			if (e.CollectionId == default)
			{
				throw new Exception("Unknown CollectionId");
			}
			return (e.CollectionId, e.ObjectId);
		});

		hostApplicationBuilder.Configuration["Storage:BlobStorage:File:Folder"] = Path.Combine(_folder, "[Store]") + Path.DirectorySeparatorChar;
	}
}

/*
[InheritsTests]
public class JsonLinesStateManageementTests : StateManagementTests
{
	string _fileName;

	[Before(Test)]
	public void Setup()
	{
		_fileName = CreateTestFileName("[Type].jsonl");
	}

	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);


		hostApplicationBuilder.AddFileSynqraStore();
		hostApplicationBuilder.AddAppendStorageJsonLines<Event>("", e => e.EventId);
		hostApplicationBuilder.AddAppendStorageJsonLines<Command>("", e => e.CommandId);
		hostApplicationBuilder.AddAppendStorageJsonLines<Item>("", e =>
		{
			if (e.CollectionId == default)
			{
				throw new Exception("Unknown collection id");
			}
			return (e.CollectionId, e.ObjectId);
		});
		hostApplicationBuilder.Configuration["Storage:JsonLinesStorage:FileName"] = _fileName;
	}
}
*/

#if Sqlite && NET10_0_OR_GREATER
[InheritsTests]
public class SqliteStateManageementTests : StateManagementTests
{
	protected override void Registration(IHostApplicationBuilder hostApplicationBuilder)
	{
		hostApplicationBuilder.Configuration["ConnectionStrings:SynqraProjectionSqlite"] = ":memory:"; // DataStore:sqlite_test.db
		hostApplicationBuilder.AddSqliteSynqraStore();
		hostApplicationBuilder.Services.AddSingleton<SqliteDatabaseContext, TestExtendedSqliteDatabaseContext>();
	}
}

public class TestExtendedSqliteDatabaseContext : SqliteDatabaseContext
{
	ILogger _logger;

	public TestExtendedSqliteDatabaseContext()
	{
		
	}

	
	public TestExtendedSqliteDatabaseContext(
		  DbContextOptions<TestExtendedSqliteDatabaseContext> options
		, IConfiguration configuration
		, ILogger<TestExtendedSqliteDatabaseContext> logger
		) : base(
		  true
		, options
		, configuration
		, logger
		)
	{
		_logger = logger;
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		modelBuilder.Entity<MyPocoTask>(b =>
		{
			b.HasKey(t => t.Id);
			b.Property("Subject");
		});

		modelBuilder.Entity<DemoModel>(b =>
		{
			b.HasKey(t => t.Id);
			b.Property("Name");
			b.Property("Prprpr");
		});
	}
}

#endif

public abstract class TestsStateManagement : BaseTest<IObjectStore>
{
	JsonSerializerOptions _jsonSerializerOptions => ServiceProvider.GetRequiredService<JsonSerializerOptions>();
	// ISynqraStoreContext _sut => ServiceProvider.GetRequiredService<ISynqraStoreContext>();
	FakeAppendStorage _fakeStorage => (FakeAppendStorage)ServiceProvider.GetService<IAppendStorage>(); // ServiceProvider.GetService<FakeAppendStorage>() ?? (FakeAppendStorage)ServiceProvider.GetService<IAppendStorage<Event, Guid>>() ?? (FakeAppendStorage)ServiceProvider.GetService<IAppendStorage>();
	ISynqraCollection<MyPocoTask> _tasks => _sut.GetCollection<MyPocoTask>();

	protected override void Register(IHostApplicationBuilder hostApplicationBuilder)
	{
		base.Register(hostApplicationBuilder);
		HostBuilder.Services.AddSingleton<JsonSerializerContext>(SampleJsonSerializerContext.Default); // im not sure yet, context or options
		HostBuilder.Services.AddSingleton(SampleJsonSerializerContext.DefaultOptions); // im not sure yet, context or options

		HostBuilder.Services.AddTypeMetadataProvider([
			typeof(DemoModel),
			typeof(SampleTaskModel),
			typeof(MyPocoTask),
			typeof(Command),
			typeof(CreateObjectCommand),
			typeof(ChangeObjectPropertyCommand),
			typeof(AddComponentCommand),
			typeof(ChangeComponentPropertyCommand),
			typeof(DeleteComponentCommand),
			typeof(AddWireCommand),
			typeof(DeleteWireCommand),
			typeof(Wire),
			typeof(TestComponentNode),
			typeof(TestGeneratedContainerNode),
			typeof(TestUniqueComponent),
			typeof(TestTaggingComponent),
			typeof(TestActivatableComponent),
			typeof(Item),
		]);

		var q0 = new DemoModel(); // must register polimorfic before serializaiton
		var q1 = new Item(); // must register polimorfic before serializaiton
		var q2 = new CreateObjectCommand(); // must register polimorfic before serializaiton
		var q3 = new ChangeObjectPropertyCommand() { PropertyName = "q" }; // must register polimorfic before serializaiton

		HostBuilder.Services.AddSingleton<FakeAppendStorage>();
		HostBuilder.Services.AddSingleton<IAppendStorage<Event, Guid>>(sp => sp.GetRequiredService<FakeAppendStorage>());
		// HostBuilder.Services.AddSingleton<IAppendStorage>(sp => sp.GetRequiredService<FakeAppendStorage>());

		HostBuilder.Services.AddSbxSerializer(ser =>
		{
			ser.Map(100, -1, typeof(MyPocoTask));
			ser.Map(101, 3000.0, typeof(Item));
			ser.Map(102, 3000.0, typeof(DemoModel));
			ser.Snapshot();
		});

		// HostBuilder.AddJsonLinesStorage();

		// var _fileName = string.Empty;
		// Configuration["Storage:JsonLinesStorage:FileName"] = _fileName = $"TestData/data_{Guid.NewGuid():N}_[TypeName].jsonl";
		// Directory.CreateDirectory(Path.GetDirectoryName(_fileName));
	}

	public void Reopen()
	{
		var fakeAppendStorage = ServiceProvider.GetRequiredService<FakeAppendStorage>();
		Restart();
		ServiceCollection.AddSingleton(fakeAppendStorage);
	}

	static object _lock = new object();

	[Test]
	public async Task Should_00_have_proper_container()
	{
		lock (_lock)
		{
			Console.WriteLine("========================= " + GetType().Name);
			foreach (var item in typeof(InMemoryProjection).GetConstructors().Last().GetParameters())
			{
				Console.WriteLine(item.ParameterType.FullName);
				Console.Write("   ");
				Console.WriteLine(ServiceCollection.FirstOrDefault(x => x.ServiceType == item.ParameterType)?.ToString() ?? "NONE!!");
			}
			Console.WriteLine("----");
		}

		Console.WriteLine(_sut);
	}

	[Test]
	public async Task Should_20_emit_command_by_setting_property()
	{
		Console.WriteLine("v2");
		// HostBuilder.AddJsonLinesStorage();
		// HostBuilder.AddSynqraStoreContext();

		var model = new DemoModel();
		_sut.GetCollection<DemoModel>().Add(model);
		await Assert.That(model.Name).IsNotEqualTo("TestName");

		model.Name = "TestName"; // this should emit a command and broadcast it

		// But it also needs to be applied
		await Assert.That(model.Name).IsEqualTo("TestName");
		await Assert.That(_sut.GetCollection<DemoModel>().Count).IsEqualTo(1);
		await Assert.That(_sut.GetCollection<DemoModel>().First().Name).IsEqualTo("TestName");

		var commands = _sut.GetCollection<Command>().ToArray();

		var jso = ServiceProvider.GetRequiredService<JsonSerializerOptions>();
		Console.WriteLine("Commands:");
		foreach (var item in commands)
		{
			Console.WriteLine(JsonSerializer.Serialize(item, jso) + " // " + item.GetType().Name + " // " + item);
		}
		Console.WriteLine("Commands Done");


		await Assert.That(commands.Count()).IsEqualTo(2);
		var co = (CreateObjectCommand)commands[0];

		var cop = (ChangeObjectPropertyCommand)commands[1];
		await Assert.That(cop.PropertyName).IsEqualTo(nameof(model.Name));
		await Assert.That(cop.OldValue).IsEqualTo(null);
		await Assert.That(cop.NewValue).IsEqualTo("TestName");
	}

	[Test]
	public async Task Should_10_create_object_by_adding_to_list()
	{
		foreach (var item in ServiceCollection.Skip(_origServiceCount))
		{
			Console.WriteLine($"{item.ServiceType.Name} '{item.ServiceKey}' = {(item.IsKeyedService ? item.KeyedImplementationInstance : item.ImplementationInstance)}");
		}
		Console.WriteLine("/////////");
		foreach (var item in _tasks)
		{
			Console.WriteLine(item);
		}
		Console.WriteLine("=======================");
		var t = new MyPocoTask { Subject = "Test Task" };
		_tasks.Add(t);

		var events = _fakeStorage.Items.OfType<Event>().ToArray();
		foreach (var item in events)
		{
			Console.WriteLine(JsonSerializer.Serialize(item, _jsonSerializerOptions));
		}

		Console.WriteLine("=======================");
		foreach (var item in _tasks)
		{
			Console.WriteLine(item .Subject+ " " + item.Id + " " + _sut.GetId(item));
		}
		Console.WriteLine("=======================");

		// objects
		await Assert.That(_tasks).HasCount(1);
		await Assert.That(_tasks.First().Subject).IsEqualTo("Test Task");
		await Assert.That(_tasks.First()).IsEquivalentTo(t);
		await Assert.That(ReferenceEquals(_tasks.First(), t)).IsTrue();

		// events
		await Assert.That(events).HasCount(3);
		var commandCreated = events[0];
		await Assert.That(commandCreated).IsTypeOf<CommandCreatedEvent>();
		var objectCreated = events[1];
		await Assert.That(objectCreated).IsTypeOf<ObjectCreatedEvent>();
		var propertyChanged = events[2];
		await Assert.That(propertyChanged).IsTypeOf<ObjectPropertyChangedEvent>();

		var tasks = _sut.GetCollection<MyPocoTask>();
		await Assert.That(tasks).HasCount(1);
		await Assert.That(tasks.First().Subject).IsEqualTo("Test Task");

		// reopen
		Reopen();
		if (_sut is InMemoryProjection imp)
		{
			await imp.LoadStateAsync();
		}

		//var bt = (StateManagementTests)Activator.CreateInstance(GetType());
		//bt.ServiceCollection.AddSingleton(_fakeStorage);
		//bt.ServiceCollection.AddSingleton<IAppendStorage>(_fakeStorage);
		//var reopened = bt.ServiceProvider.GetRequiredService<IProjection>();

		tasks = _sut.GetCollection<MyPocoTask>();
		await Assert.That(tasks).HasCount(1);
		await Assert.That(tasks.First().Subject).IsEqualTo("Test Task");
	}

	[Test]
	public async Task Should_25_change_object()
	{
		var t = new MyPocoTask { Subject = "Test Task" };
		_tasks.Add(t);

		using (_tasks.PocoTracker(t))
		{
			t.Subject = "123"; // There should be event driven mode that helps to track the changes, but POCO also must work, so need snapshotting
		}

		var events = _fakeStorage.Items.OfType<Event>().ToArray();
		foreach (var item in events)
		{
			Console.WriteLine($"{item.GetType().Name} {item}");
		}
		await Assert.That(events).HasCount(5);
		await Assert.That(events[4]).IsTypeOf<ObjectPropertyChangedEvent>();

		await Assert.That(_tasks).HasCount(1);
		await Assert.That(_tasks.First().Subject).IsEqualTo("123");

		// reopen
		Reopen();
		if (_sut is InMemoryProjection imp)
		{
			await imp.LoadStateAsync();
		}

		var tasks = _sut.GetCollection<MyPocoTask>();
		await Assert.That(tasks).HasCount(1);
		await Assert.That(tasks.First().Subject).IsEqualTo("123");
	}

	[Test]
	public async Task Should_30_instantiate_collection_by_ctor()
	{
		var tasks = _sut.GetCollection<MyPocoTask>();
	}

	[Test]
	public async Task Should_30_instantiate_collection_by_type()
	{
		var tasks = _sut.GetCollection(typeof(MyPocoTask));
	}
}

/// <summary>
/// THIS IS POCO, NOT GENERATED, do not make it partial
/// </summary>
public class MyPocoTask
{
	/// <summary>
	/// THIS IS POCO, NOT GENERATED, do not make it partial
	/// </summary>
	public MyPocoTask()
	{
		
	}
	public Guid Id { get; set; }
	public string Subject { get; set; }
}

public class FakeAppendStorage : FakeAppendStorage<Event, Guid>, IAppendStorage
{
}

public class FakeAppendStorage<T, TKey> : IAppendStorage<T, TKey>
	where T : class
	// where T : IIdentifiable<TKey>
{
	public List<T> Items { get; } = new List<T>();

	public Task AppendAsync(T item, CancellationToken cancellationToken = default)
	{
		Items.Add(item);
		return Task.CompletedTask;
	}

	public Task AppendBatchAsync(IEnumerable<T> items, CancellationToken cancellationToken = default)
	{
		Items.AddRange(items);
		return Task.CompletedTask;
	}

	public void Dispose()
	{
	}

	public ValueTask DisposeAsync()
	{
		return default;
	}

	public Task FlushAsync(CancellationToken cancellationToken = default)
	{
		return Task.CompletedTask;
	}

	public async IAsyncEnumerable<T> GetAllAsync(TKey? from = default, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		foreach (T item in Items)
		{
			// if (from == null || item is IEvent e && e.Id > from)
			{
				yield return item;
			}
		}
	}

	public Task<T> GetAsync(TKey key, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException();
	}
}

[SynqraModel]
[Schema(3000.0, "1 Name string Prprpr string")]

public partial class DemoModel
{
	public Guid Id => ((IBindableModel)this).Store.GetId(this);

	public partial string Name { get; set; }
	public partial string Prprpr { get; set; }
}

[SynqraModel]
[Schema(2026.164, "1 Key string Title string")]
public partial class StorableModel
{
	public partial string Key { get; set; }

	public partial string Title { get; set; }
}

// ---- Component substrate test fixtures (used by InMemoryStateManageementTests) ----

/// <summary>Container exposing a manually-backed components collection. Kept on the manual path so
/// Phase A/B tests continue to exercise that code path (user code can still bring its own collection).</summary>
[SynqraModel]
[Schema(2026.405, "1 Name string?")]
public partial class TestComponentNode : IComponentContainer
{
	readonly ComponentsCollection _components = new();

	public partial string? Name { get; set; }

	[JsonIgnore]
	public IComponentsCollection Components => _components;
}

/// <summary>Container that does NOT declare Components — generator emits the wrapper.
/// Exercises Phase B-2 (StoreBoundComponentsCollection routing).</summary>
[SynqraModel]
[Schema(2026.405, "1 Name string?")]
public partial class TestGeneratedContainerNode : IComponentContainer
{
	public partial string? Name { get; set; }
}

/// <summary>Unique-by-class component: at most one instance per container.</summary>
[SynqraModel]
[Component(IsUnique = true)]
[Schema(2026.405, "1 Subject string?")]
public partial class TestUniqueComponent : IComponent
{
	public partial string? Subject { get; set; }
}

/// <summary>Non-unique component: requires its own Id so multiple instances are addressable.</summary>
[SynqraModel]
[Schema(2026.405, "1 Id Guid Tag string?")]
public partial class TestTaggingComponent : IComponent, IIdentifiable<Guid>
{
	public partial Guid Id { get; set; }
	public partial string? Tag { get; set; }

	Guid IIdentifiable<Guid>.Id => Id;
}

/// <summary>
/// Activatable component used to verify replay-skip semantics. Sets <see cref="LastActivationWasReplay"/>
/// on activation so tests can check whether activation fired at all and in which mode.
/// </summary>
[SynqraModel]
[Component(IsUnique = true)]
[Schema(2026.405, "1 Marker string?")]
public partial class TestActivatableComponent : IComponent, IActivatableComponent
{
	public partial string? Marker { get; set; }

	[JsonIgnore]
	public int ActivationCount { get; private set; }

	[JsonIgnore]
	public bool? LastActivationWasReplay { get; private set; }

	void IActivatableComponent.Activate(ComponentActivationContext context)
	{
		ActivationCount++;
		LastActivationWasReplay = context.IsReplay;
	}
}

[SynqraModel]
[Schema(2026.164, "1 Title string")]
public partial class CollectionElementModel
{
	public Guid ObjectId => ((IBindableModel)this).Store.GetId(this);
	[JsonIgnore]
	public Guid CollectionId { get; set; }

	public partial string Title { get; set; }
}
