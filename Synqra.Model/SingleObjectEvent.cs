using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Synqra;

public abstract class SingleObjectEvent : Event
{
	public required Guid TargetId { get; set; } // like row id
	public required Guid TargetTypeId { get; set; } // like descriminator
	public required Guid CollectionId { get; set; } // like table name (can be derrived from root type id)
}
