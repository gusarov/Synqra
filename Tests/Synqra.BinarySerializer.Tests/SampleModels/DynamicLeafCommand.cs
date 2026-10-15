// Namespace deliberately outside Synqra.Tests.* so the SBX DEBUG name allowlist does not apply.
namespace Synqra.BinarySerializer.Tests.Dynamic;

// Unmapped in SBX: persisted by type name + field names.
[SynqraModel("C0DE0000-0000-8000-9C45-000000000000")]
[Schema(2026.786, "1 CommandId Guid StreamId Guid TargetId Guid Note string?")]
public partial class DynamicLeafCommand : Command
{
	public partial Guid TargetId { get; set; }

	public partial string? Note { get; set; }

	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx)
	{
		return Task.CompletedTask;
	}
}
