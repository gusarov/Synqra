using System.Diagnostics.CodeAnalysis;

namespace Synqra.Tests.SampleModels;

#if NET8_0_OR_GREATER
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
#endif
public class DemoObject
{
#if NET8_0_OR_GREATER
	[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
#endif
	public string Property1 { get; set; }
}

