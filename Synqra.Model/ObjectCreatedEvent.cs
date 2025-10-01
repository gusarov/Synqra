using System.Text.Json.Serialization;

namespace Synqra;

public class ObjectCreatedEvent : SingleObjectEvent
{
	public IDictionary<string, object?>? Data { get; set; }

	[JsonIgnore]
	public string? DataString { get; set; }

	[JsonIgnore]
	public object? DataObject { get; set; }

	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx) => visitor.VisitAsync(this, ctx);
}
