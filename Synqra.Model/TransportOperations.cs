using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Synqra;

[JsonPolymorphic(IgnoreUnrecognizedTypeDiscriminators = false, TypeDiscriminatorPropertyName = "_t", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(NewEvent1), "NewEvent1")]
public abstract class TransportOperation
{
}

[SynqraModel]
[Schema(2025.785, "1 Event Event")]
public partial class NewEvent1 : TransportOperation
{
	public required partial Event Event { get; set; }
}
