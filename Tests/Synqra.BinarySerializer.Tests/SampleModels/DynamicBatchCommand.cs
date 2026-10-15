// Namespace deliberately outside Synqra.Tests.* so the SBX DEBUG name allowlist does not apply.
namespace Synqra.BinarySerializer.Tests.Dynamic;

// BatchCommand look-alike: unmapped in SBX, carries mapped children and plain POCO expectations.
[SynqraModel("C0DE0000-0000-8000-9C46-000000000000")]
[Schema(2026.786, "1 CommandId Guid StreamId Guid Commands IList<Command> Expectations IList<DynamicExpectation>")]
public partial class DynamicBatchCommand : Command
{
	public DynamicBatchCommand()
	{
		Commands = new List<Command>();
		Expectations = new List<DynamicExpectation>();
	}

	public partial IList<Command> Commands { get; set; }

	public partial IList<DynamicExpectation> Expectations { get; set; }

	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx)
	{
		return Task.CompletedTask;
	}
}
