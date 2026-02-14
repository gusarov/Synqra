using Synqra;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Contoso.Model;

[SynqraModel]
[Schema(2026.122, "1 ItemName string")]
public partial class ContosoItem
{
	public partial string ItemName { get; set; }
}

public class FooContosoCommand : Command
{
	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx)
	{
		return ((IContosoCommandVisitor<T>)visitor).VisitAsync(this, ctx);
	}
}

public class FooContosoEvent : Event
{
	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx)
	{
		return ((IContosoEventVisitor<T>)visitor).VisitAsync(this, ctx);
	}
}

public interface IContosoCommandVisitor<T> : ICommandVisitor<T>
{
	Task VisitAsync(FooContosoCommand command, T ctx);
}

public interface IContosoEventVisitor<T> : IEventVisitor<T>
{
	Task VisitAsync(FooContosoEvent ev, T ctx);
}

