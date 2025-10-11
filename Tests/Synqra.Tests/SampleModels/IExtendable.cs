using System.Text.Json.Serialization;

namespace Synqra.Tests.SampleModels;

public interface IExtendable
{
	[JsonExtensionData]
	IDictionary<string, object> Extra { get; }
}

