namespace Synqra.Tests.BinarySerialization.Dynamic;

// BatchExpectation look-alike: a plain sealed POCO, not a [SynqraModel].
public sealed class DynamicExpectation
{
	public Guid TargetId { get; set; }

	public Guid TargetTypeId { get; set; }

	public Guid ExpectedLastEventId { get; set; }
}
