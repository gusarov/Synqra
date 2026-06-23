using System;
using System.Collections.Generic;

namespace Synqra;

/// <summary>
/// Context handed to <see cref="IActivatableComponent.Activate"/>.
/// </summary>
public sealed class ComponentActivationContext
{
	public required IComponentContainer Container { get; init; }
	public required Guid ContainerId { get; init; }
	public required IComponent Component { get; init; }

	/// <summary>
	/// True when the activation is happening during projection replay (rebuild
	/// from event store). Activators should skip side-effectful work in this
	/// case to avoid duplicate subscriptions, double-firing webhooks, etc.
	/// </summary>
	public required bool IsReplay { get; init; }

	/// <summary>
	/// Optional bag for component-specific extra data that should be returned
	/// to the originating caller (e.g. a webhook activation can deposit a freshly
	/// issued token here so the HTTP response can carry it). Ignored on replay.
	/// </summary>
	public IDictionary<string, object>? ExtraResponseData { get; set; }
}
