using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Synqra;

[SynqraModel]
[Schema(2025.789, "1 TargetId Guid TargetTypeId Guid CollectionId Guid EventId Guid CommandId Guid ContainerId Guid")]
[Schema(2025.790, "1 TargetId Guid TargetTypeId Guid CollectionId Guid EventId Guid CommandId Guid")]
[Schema(2025.791, "1 EventId Guid CommandId Guid")]
[Schema(2025.792, "1 TargetId Guid TargetTypeId Guid CollectionId Guid EventId Guid CommandId Guid")]
[Schema(2025.793, "1 EventId Guid CommandId Guid")]
[Schema(2025.794, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid")]
[Schema(2025.795, "1 EventId Guid CommandId Guid")]
[Schema(2025.796, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid")]
[Schema(2025.797, "1 TargetId Guid TargetTypeId Guid CollectionId Guid EventId Guid CommandId Guid ContainerId Guid")]
[Schema(2025.798, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid")]
[Schema(2025.799, "1 TargetId Guid TargetTypeId Guid CollectionId Guid EventId Guid CommandId Guid ContainerId Guid")]
[Schema(2025.800, "1 EventId Guid CommandId Guid TargetId Guid TargetTypeId Guid CollectionId Guid")]
public abstract partial class SingleObjectEvent : Event
{
	public required partial Guid TargetId { get; set; } // like row id
	public required partial Guid TargetTypeId { get; set; } // like descriminator
	public required partial Guid CollectionId { get; set; } // like table name (can be derrived from root type id)
}
