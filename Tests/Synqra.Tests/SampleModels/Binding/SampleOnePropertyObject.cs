using System.Diagnostics.CodeAnalysis;

namespace Synqra.Tests.SampleModels.Binding;

#if NET8_0_OR_GREATER
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
#endif
public class SampleOnePropertyObject
{
#if NET8_0_OR_GREATER
	[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
#endif
	public string Property1 { get; set; }
}

