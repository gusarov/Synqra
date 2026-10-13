using System.Collections.Generic;
using System.Threading.Tasks;

namespace Synqra;

/// <summary>
/// Several commands applied as one: either every command succeeds and all their events are applied
/// and stored together, or one is refused and nothing is applied or stored. Later commands see the
/// effects of earlier ones (create an object, then attach to it). Modelled on Todo's BatchCommand.
/// <para>
/// Server-side only: a batch never crosses the wire and is never itself persisted; its effect is its
/// children's events. Children address targets by id (no <see cref="SingleObjectCommand.TargetObject"/>)
/// and cannot be batches themselves. A projection that cannot run batches refuses them explicitly.
/// </para>
/// </summary>
[SynqraModel("C0DEADD0-1032-8000-8C04-000000000000")]
[Schema(2026.780, "1 CommandId Guid StreamId Guid Commands IList<Command> Expectations IList<BatchExpectation>")]
public partial class BatchCommand : Command
{
	public BatchCommand()
	{
		Commands = new List<Command>();
		Expectations = new List<BatchExpectation>();
	}

	public partial IList<Command> Commands { get; set; }

	/// <summary>Versions the targets must still have when the batch runs: optimistic concurrency for the batch as a whole. Checked before anything runs.</summary>
	public partial IList<BatchExpectation> Expectations { get; set; }

	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx)
		=> visitor.VisitAsync(this, ctx);
}

/// <summary>One precondition of a <see cref="BatchCommand"/>: the target must still be at this event.</summary>
public sealed class BatchExpectation
{
	public Guid TargetId { get; set; }

	/// <summary>The target's root type, so a store that loads objects lazily can find it.</summary>
	public Guid TargetTypeId { get; set; }

	public Guid ExpectedLastEventId { get; set; }
}
