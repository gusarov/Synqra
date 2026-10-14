namespace Synqra;

public class CommandHandlerContext
{
	public List<Event> Events { get; set; } = new List<Event>();

	/// <summary>Whether a command emits its <see cref="CommandCreatedEvent"/>. An emitted one is applied, stored and pushed like any other event; one that is not emitted exists nowhere. A batch emits one for itself and runs its children with this off.</summary>
	public bool EmitCommandEvents { get; init; } = true;
}
