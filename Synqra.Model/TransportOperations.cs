using System.Text.Json.Serialization;

namespace Synqra;

[JsonPolymorphic(IgnoreUnrecognizedTypeDiscriminators = false, TypeDiscriminatorPropertyName = "_t", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(NewEvent1), "NewEvent1")]
[SynqraModel]
[Schema(2025.791, "1")]
public abstract partial class TransportOperation
{
}

[SynqraModel]
[Schema(2025.785, "1 Event Event")]
public partial class NewEvent1 : TransportOperation
{
	public required partial Event Event { get; set; }

	public override string ToString()
	{
		return Event.ToString();
	}
}
