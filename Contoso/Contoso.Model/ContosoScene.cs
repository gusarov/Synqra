using System.Text;
using Synqra;

namespace Contoso.Model;

// A small scene, as Quotaly's 3D scene is: nodes that carry facets and are joined by weighted edges. A stream snapshot carries
// every kind of record it has (plans/snapshot.md §3): a root, a unique and a repeated facet, and a link with a payload.

/// <summary>A scene node: an entity with data of its own, facets, and edges to other nodes.</summary>
[SynqraModel]
[Schema(2026.833, "1 Name string? Size long")]
public partial class ContosoSceneNode : IComponentContainer
{
	public partial string? Name { get; set; }

	public partial long Size { get; set; }

	[To]
	public partial ICollection<ContosoSceneEdge> Edges { get; }
}

/// <summary>Where a node sits: at most one per node.</summary>
[SynqraModel]
[Component(IsUnique = true)]
[Schema(2026.833, "1 X double Y double")]
public partial class ContosoScenePosition : IComponent
{
	public partial double X { get; set; }

	public partial double Y { get; set; }
}

/// <summary>A label: a node carries any number.</summary>
[SynqraModel]
[Schema(2026.833, "1 Label string?")]
public partial class ContosoSceneTag : IComponent
{
	public partial string? Label { get; set; }
}

/// <summary>A directed edge with a weight of its own.</summary>
[SynqraModel]
[Schema(2026.833, "1 SourceId Guid TargetId Guid Weight double?")]
public partial class ContosoSceneEdge : DirectedLink<ContosoSceneNode, ContosoSceneNode>
{
	public partial double? Weight { get; set; }
}

/// <summary>
/// Builds, edits and describes a scene through any <see cref="IObjectStore"/>: the browser's projection or the master's store. The
/// description is the same text wherever it is taken, so the browser tests compare a browser's scene with the master's line by line.
/// </summary>
public sealed class ContosoScene
{
	/// <summary>The scene's types, to register with a store's type metadata and its serializer.</summary>
	public IReadOnlyList<Type> Types { get; } = [typeof(ContosoSceneNode), typeof(ContosoScenePosition), typeof(ContosoSceneTag), typeof(ContosoSceneEdge),];

	/// <summary>
	/// Adds <paramref name="nodes"/> nodes, each with a position and two tags, joined in a chain of weighted edges, as one batch;
	/// then edits them one command at a time: a rename, a relabelled tag, a removed tag, a retargeted edge and a deleted node.
	/// </summary>
	public async Task BuildAsync(IObjectStore store, string prefix, int nodes)
	{
		var batch = new BatchCommand();
		var ids = new List<Guid>();
		var tags = new List<Guid>();
		for (var i = 0; i < nodes; i++)
		{
			var id = store.IdProvider.CreateComponentId();
			ids.Add(id);
			batch.Commands.Add(ComposeAdd(store, id, id, new ContosoSceneNode { Name = $"{prefix} {i}", Size = i * 10 }));
			batch.Commands.Add(ComposeAdd(store, id, store.IdProvider.CreateComponentId(), new ContosoScenePosition { X = i, Y = -i }));
			batch.Commands.Add(ComposeAdd(store, id, store.IdProvider.CreateComponentId(), new ContosoSceneTag { Label = i % 2 == 0 ? "even" : "odd" }));
			var tag = store.IdProvider.CreateComponentId();
			tags.Add(tag);
			batch.Commands.Add(ComposeAdd(store, id, tag, new ContosoSceneTag { Label = $"{prefix} tag {i}" }));
		}
		var edges = new List<Guid>();
		for (var i = 0; i + 1 < nodes; i++)
		{
			var edge = store.IdProvider.CreateComponentId();
			edges.Add(edge);
			batch.Commands.Add(ComposeAdd(store, ids[i], edge, new ContosoSceneEdge { SourceId = ids[i], TargetId = ids[i + 1], Weight = i / 2.0 }));
		}
		await store.SubmitCommandAsync(batch);
		if (nodes >= 5)
		{
			await store.SubmitCommandAsync(ComposeChange<ContosoSceneNode>(store, ids[0], ids[0], nameof(ContosoSceneNode.Name), $"{prefix} first"));
			await store.SubmitCommandAsync(ComposeChange<ContosoSceneTag>(store, ids[1], tags[1], nameof(ContosoSceneTag.Label), "relabelled"));
			await store.SubmitCommandAsync(ComposeDelete<ContosoSceneTag>(store, ids[2], tags[2]));
			await store.SubmitCommandAsync(ComposeChange<ContosoSceneEdge>(store, ids[3], edges[3], nameof(Link.TargetId), ids[0]));
			await store.SubmitCommandAsync(ComposeDelete<ContosoSceneNode>(store, ids[^1], ids[^1]));
		}
	}

	/// <summary>Renames the node first in id order: an edit a test makes on one replica and expects on the others.</summary>
	public async Task RenameFirstAsync(IObjectStore store, string name)
	{
		var id = ReadNodes(store).Select(x => x.Id).FirstOrDefault();
		if (id == default)
		{
			throw new InvalidOperationException("The scene has no node to rename.");
		}
		await store.SubmitCommandAsync(ComposeChange<ContosoSceneNode>(store, id, id, nameof(ContosoSceneNode.Name), name));
	}

	/// <summary>Adds an edge from the first node to the last, in id order, with this weight.</summary>
	public async Task LinkFirstToLastAsync(IObjectStore store, double weight)
	{
		var ordered = ReadNodes(store).Select(x => x.Id).ToList();
		if (ordered.Count < 2)
		{
			throw new InvalidOperationException("The scene has fewer than two nodes to link.");
		}
		await store.SubmitCommandAsync(ComposeAdd(store, ordered[0], store.IdProvider.CreateComponentId(), new ContosoSceneEdge { SourceId = ordered[0], TargetId = ordered[^1], Weight = weight }));
	}

	/// <summary>The scene as text, one line per node in id order: its data, its position, its tags and its edges.</summary>
	public string Describe(IObjectStore store)
	{
		var text = new StringBuilder();
		foreach (var (id, node) in ReadNodes(store))
		{
			text.Append(DescribeNode(id, node));
		}
		return text.ToString();
	}

	/// <summary>
	/// One node's line, or nothing when a peer deletes the node while it is read: its edges ask the store for its id, which throws
	/// once it has left the store's tracking. It is gone, and left out, as <see cref="ReadNodes"/> leaves it out.
	/// </summary>
	string DescribeNode(Guid id, ContosoSceneNode node)
	{
		var line = "";
		try
		{
			var components = ((IComponentContainer)node).Components.ToList();
			var position = components.OfType<ContosoScenePosition>().SingleOrDefault();
			var tags = components.OfType<ContosoSceneTag>().Select(x => x.Label).Order();
			var edges = node.Edges.Select(x => $"{x.TargetId}:{x.Weight}").Order();
			line = $"{id} {node.Name} {node.Size} @{position?.X},{position?.Y} [{string.Join(", ", tags)}] -> [{string.Join(", ", edges)}]\n";
		}
		catch (InvalidOperationException)
		{
			// deleted while it was read
		}
		return line;
	}

	/// <summary>
	/// The nodes with their ids, in id order, read in one pass: a Mongo collection's Count is a query of its own, so a copy sized by
	/// it misses or overruns a root a peer adds in between. A node a peer deletes while it is read leaves the store's tracking, and
	/// asking its id then throws: it is gone, and left out.
	/// </summary>
	List<(Guid Id, ContosoSceneNode Node)> ReadNodes(IObjectStore store)
	{
		var nodes = new List<(Guid Id, ContosoSceneNode Node)>();
		foreach (var node in store.GetCollection<ContosoSceneNode>())
		{
			if (FindId(store, node) is Guid id)
			{
				nodes.Add((id, node));
			}
		}
		return nodes.OrderBy(x => x.Id).ToList();
	}

	Guid? FindId(IObjectStore store, object node)
	{
		Guid? id = null;
		try
		{
			id = store.GetId(node);
		}
		catch (InvalidOperationException)
		{
			// deleted by a peer while the scene was read
		}
		return id;
	}

	AddComponentCommand ComposeAdd(IObjectStore store, Guid owner, Guid componentId, object data)
	{
		var node = store.TypeMetadataProvider.GetTypeMetadata(typeof(ContosoSceneNode));
		return new AddComponentCommand
		{
			TargetTypeId = node.TypeId,
			CollectionId = node.CollectionId,
			TargetId = owner,
			ComponentTypeId = store.TypeMetadataProvider.GetTypeMetadata(data.GetType()).TypeId,
			ComponentId = componentId,
			Data = data,
		};
	}

	ChangeComponentPropertyCommand ComposeChange<TComponent>(IObjectStore store, Guid owner, Guid componentId, string property, object? value)
	{
		var node = store.TypeMetadataProvider.GetTypeMetadata(typeof(ContosoSceneNode));
		return new ChangeComponentPropertyCommand
		{
			TargetTypeId = node.TypeId,
			CollectionId = node.CollectionId,
			TargetId = owner,
			ComponentTypeId = store.TypeMetadataProvider.GetTypeMetadata(typeof(TComponent)).TypeId,
			ComponentId = componentId,
			PropertyName = property,
			NewValue = value,
		};
	}

	DeleteComponentCommand ComposeDelete<TComponent>(IObjectStore store, Guid owner, Guid componentId)
	{
		var node = store.TypeMetadataProvider.GetTypeMetadata(typeof(ContosoSceneNode));
		return new DeleteComponentCommand
		{
			TargetTypeId = node.TypeId,
			CollectionId = node.CollectionId,
			TargetId = owner,
			ComponentTypeId = store.TypeMetadataProvider.GetTypeMetadata(typeof(TComponent)).TypeId,
			ComponentId = componentId,
		};
	}
}
