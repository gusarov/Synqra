using System.Text.Json.Serialization;

namespace Synqra.Tests.DemoTodo;

public interface IExtendable
{
	[JsonExtensionData]
	IDictionary<string, object> Extra { get; }
}

