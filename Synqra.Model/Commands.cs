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
[JsonDerivedType(typeof(AddComponentCommand), "AddComponentCommand")]
[JsonDerivedType(typeof(ChangeComponentPropertyCommand), "ChangeComponentPropertyCommand")]
[JsonDerivedType(typeof(DeleteComponentCommand), "DeleteComponentCommand")]
[JsonDerivedType(typeof(AddLinkCommand), "AddLinkCommand")]
[JsonDerivedType(typeof(RemoveLinkCommand), "RemoveLinkCommand")]
[SynqraModel("C0DEADD0-1032-8000-8C00-000000000000")] // command family C, type 00 = the base kind (node 0 = type ref)
[Schema(2025.1, "")]
[Schema(2025.791, "1")]
[Schema(2025.792, "1 CommandId Guid ContainerId Guid")]
[Schema(2025.793, "1")]
[Schema(2025.794, "1 CommandId Guid ContainerId Guid")]
[Schema(2026.198, "1 CommandId Guid StreamId Guid")]
public abstract partial class Command : ISynqraCommand
{
	// CommandId is assigned by the store on submit (SubmitCommandAsync) from the injected
	// ISynqraIdProvider when left empty — a detached command has no ambient id factory.
	public partial Guid CommandId { get; set; }
	public partial Guid StreamId { get; set; }

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

[SynqraModel("C0DEADD0-1032-8000-8C0F-000000000000")] // abstract shared base for object+component single-target commands
[Schema(2025.1, "")]
[Schema(2025.791, "1 CommandId Guid ContainerId Guid-")]
[Schema(2025.792, "1 CommandId Guid ContainerId Guid")]
[Schema(2025.793, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid")]
[Schema(2025.794, "1 TargetTypeId Guid CollectionId Guid TargetId Guid Target object? CommandId Guid ContainerId Guid")]
[Schema(2025.795, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid")]
[Schema(2025.796, "1 TargetTypeId Guid CollectionId Guid TargetId Guid Target object? CommandId Guid ContainerId Guid")]
[Schema(2025.797, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid")]
[Schema(2026.156, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid Target object?")]
[Schema(2026.157, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid")]
[Schema(2026.167, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid TargetObject object?")]
[Schema(2026.168, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid")]
[Schema(2026.169, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid TargetObject object?")]
[Schema(2026.170, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid")]
[Schema(2026.198, "1 CommandId Guid StreamId Guid TargetTypeId Guid CollectionId Guid TargetId Guid")]
public abstract partial class SingleObjectCommand : Command
{
	public partial Guid TargetTypeId { get; set; }

	public partial Guid CollectionId { get; set; }

	public partial Guid TargetId { get; set; }

	[JsonIgnore]
	public object? TargetObject { get; set; }
}

[SynqraModel("C0DEADD0-1032-8000-9C02-000000000000")] // object vocabulary — dying (test/temp space 9)
[Schema(2025.1, "")]
public class DeleteObjectCommand : Command
{
	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}

[SynqraModel("C0DEADD0-1032-8000-9C01-000000000000")] // object vocabulary — dying (test/temp space 9), command family C, type 01
[Schema(2025.1, "")]
[Schema(2025.791, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid")]
[Schema(2025.792, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid PropertyName string OldValue object? NewValue object?")]
[Schema(2025.793, "1 PropertyName string OldValue object? NewValue object? TargetTypeId Guid CollectionId Guid TargetId Guid Target object? CommandId Guid ContainerId Guid")]
[Schema(2025.794, "1 CommandId Guid ContainerId Guid TargetTypeId Guid CollectionId Guid TargetId Guid PropertyName string OldValue object? NewValue object?")]
[Schema(2026.198, "1 CommandId Guid StreamId Guid TargetTypeId Guid CollectionId Guid TargetId Guid PropertyName string OldValue object? NewValue object?")]
public partial class ChangeObjectPropertyCommand : SingleObjectCommand
{
	public required partial string PropertyName { get; set; }

	public partial object? OldValue { get; set; }

	public partial object? NewValue { get; set; }

	public ChangeObjectPropertyCommand()
	{
		Console.WriteLine();
	}

	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}
