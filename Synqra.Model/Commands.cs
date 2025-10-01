using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Synqra;

public abstract class Command : ISynqraCommand
{
	public Guid CommandId { get; set; } = GuidExtensions.CreateVersion7();
	public Guid ContainerId { get; set; }

	protected abstract Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx);

	public Task AcceptAsync(ICommandVisitor<object?> visitor)
	{
		return AcceptAsync(visitor, null);
	}

	public async Task AcceptAsync<T>(ICommandVisitor<T> visitor, T ctx)
	{
		await visitor.BeforeVisitAsync(this, ctx);
		await AcceptCoreAsync(visitor, ctx);
		await visitor.AfterVisitAsync(this, ctx);
	}
}

public abstract class SingleObjectCommand : Command
{
	public Guid TargetTypeId { get; set; }

	public Guid CollectionId { get; set; }

	public Guid TargetId { get; set; }

	[JsonIgnore]
	public object? Target { get; set; }
}

public class CreateObjectCommand : SingleObjectCommand
{
	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);

	public IDictionary<string, object?>? Data { get; set; }

	[JsonIgnore]
	public string? DataJson { get; set; }
}

public class DeleteObjectCommand : Command
{
	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}

public class ChangeObjectPropertyCommand : SingleObjectCommand
{
	public required string PropertyName { get; init; }

	public object? OldValue { get; set; }

	public object? NewValue { get; set; }

	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}
