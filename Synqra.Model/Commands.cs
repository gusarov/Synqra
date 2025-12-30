using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Synqra;

[JsonPolymorphic(IgnoreUnrecognizedTypeDiscriminators = false, TypeDiscriminatorPropertyName = "_t", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ChangeObjectPropertyCommand), "ChangeObjectPropertyCommand")]
[JsonDerivedType(typeof(DeleteObjectCommand), "DeleteObjectCommand")]
[JsonDerivedType(typeof(CreateObjectCommand), "CreateObjectCommand")]
[SynqraModel]
[Schema(2025.1, "")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 CommandId Guid ContainerId Guid")]
[Schema(2025.793, "1")]
[Schema(2025.794, "1 CommandId Guid ContainerId Guid")]
public abstract partial class Command : ISynqraCommand
{
	protected Command()
	{
		CommandId = GuidExtensions.CreateVersion7();
	}

	public partial Guid CommandId { get; set; }
	public partial Guid ContainerId { get; set; }

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

[SynqraModel]
[Schema(2025.1, "")]
[Schema(2025.791, "1 CommandId Guid ContainerId Guid-")]
[Schema(2025.792, "1 CommandId Guid ContainerId Guid")]
[Schema(2025.793, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid")]
[Schema(2025.794, "1 TargetTypeId Guid CollectionId Guid TargetId Guid Target object? CommandId Guid ContainerId Guid")]
[Schema(2025.795, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid")]
[Schema(2025.796, "1 TargetTypeId Guid CollectionId Guid TargetId Guid Target object? CommandId Guid ContainerId Guid")]
[Schema(2025.797, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid")]
public abstract partial class SingleObjectCommand : Command
{
	public partial Guid TargetTypeId { get; set; }

	public partial Guid CollectionId { get; set; }

	public partial Guid TargetId { get; set; }

	[JsonIgnore]
	public object? Target { get; set; }
}

[SynqraModel]
[Schema(2025.791, "1 Data IDictionary<string, object?>? DataJson string? TargetId Guid TargetTypeId Guid CollectionId Guid EventId Guid CommandId Guid ContainerId Guid")]
[Schema(2025.792, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid")]
[Schema(2025.793, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid Data IDictionary<string, object?>?")]
[Schema(2025.794, "1 Data IDictionary<string, object?>? DataJson string? TargetTypeId Guid CollectionId Guid TargetId Guid Target object? CommandId Guid ContainerId Guid")]
[Schema(2025.795, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid Data IDictionary<string, object?>?")]
[Schema(2025.796, "1 Data IDictionary<string, object?>? DataJson string? TargetTypeId Guid CollectionId Guid TargetId Guid Target object? CommandId Guid ContainerId Guid")]
[Schema(2025.797, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid Data IDictionary<string, object?>?")]
[Schema(2025.798, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid Data object")]
[Schema(2025.799, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid Data IBindableModel")]
[Schema(2025.800, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid Data object")]
public partial class CreateObjectCommand : SingleObjectCommand
{
	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);

	public partial object Data { get; set; }
}

[SynqraModel]
[Schema(2025.1, "")]
public class DeleteObjectCommand : Command
{
	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}

[SynqraModel]
[Schema(2025.1, "")]
[Schema(2025.791, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid")]
[Schema(2025.792, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid PropertyName string OldValue object? NewValue object?")]
[Schema(2025.793, "1 PropertyName string OldValue object? NewValue object? TargetTypeId Guid CollectionId Guid TargetId Guid Target object? CommandId Guid ContainerId Guid")]
[Schema(2025.794, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid PropertyName string OldValue object? NewValue object?")]
public partial class ChangeObjectPropertyCommand : SingleObjectCommand
{
	public required partial string PropertyName { get; set; }

	public partial object? OldValue { get; set; }

	public partial object? NewValue { get; set; }

	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}
