using System.Text.Json.Serialization;

namespace Synqra;

public interface IExtendable
{
	[JsonExtensionData]
	IDictionary<string, object> Extra { get; }
}

