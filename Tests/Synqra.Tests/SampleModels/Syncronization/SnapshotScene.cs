namespace Synqra.Tests.SampleModels.Syncronization;

// A small scene for the snapshot tests: a root that hosts facets, a unique and a repeated facet, and a link with a payload, so a
// snapshot carries every kind of record (plans/snapshot.md §3).

/// <summary>A scene node: an entity with data of its own, facets, and links to other nodes.</summary>
[SynqraModel]
[Schema(2026.832, "1 Name string? Size long")]
public partial class SnapshotSceneNode : IComponentContainer
{
	public partial string? Name { get; set; }

	public partial long Size { get; set; }

	[To]
	public partial ICollection<SnapshotSceneEdge> Edges { get; }
}

/// <summary>A node's position: at most one per node.</summary>
[SynqraModel]
[Component(IsUnique = true)]
[Schema(2026.832, "1 X double Y double")]
public partial class SnapshotScenePosition : IComponent
{
	public partial double X { get; set; }

	public partial double Y { get; set; }
}

/// <summary>A label: a node carries any number.</summary>
[SynqraModel]
[Schema(2026.832, "1 Label string?")]
public partial class SnapshotSceneTag : IComponent
{
	public partial string? Label { get; set; }
}

/// <summary>A directed edge with a weight of its own.</summary>
[SynqraModel]
[Schema(2026.832, "1 SourceId Guid TargetId Guid Weight double?")]
public partial class SnapshotSceneEdge : DirectedLink<SnapshotSceneNode, SnapshotSceneNode>
{
	public partial double? Weight { get; set; }
}
