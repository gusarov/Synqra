using Synqra;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Contoso.Model;

[SynqraModel]
[Schema(2026.134, "1 ItemName string ItemName2 string")]
public partial class ContosoItem
{
	public partial string ItemName { get; set; }
	public partial string ItemName2 { get; set; }
}

[SynqraModel]
[Schema(2026.134, "1 CommandId Guid ContainerId Guid")]
public partial class FooContosoCommand : Command
{
	protected override Task AcceptCoreAsync<T>(ICommandVisitor<T> visitor, T ctx)
	{
		return ((IContosoCommandVisitor<T>)visitor).VisitAsync(this, ctx);
	}
}

[SynqraModel]
[Schema(2026.134, "1 EventId Guid CommandId Guid")]
public partial class FooContosoEvent : Event
{
	protected override Task AcceptCoreAsync<T>(IEventVisitor<T> visitor, T ctx)
	{
		return ((IContosoEventVisitor<T>)visitor).VisitAsync(this, ctx);
	}
}

public interface IContosoCommandVisitor<T> : ICommandVisitor<T>
{
	Task VisitAsync(FooContosoCommand command, T ctx);
}

public interface IContosoEventVisitor<T> : IEventVisitor<T>
{
	Task VisitAsync(FooContosoEvent ev, T ctx);
}


[JsonSourceGenerationOptions(
	  JsonSerializerDefaults.Web
	, AllowTrailingCommas = true
	, DefaultBufferSize = 16 * 1024
	, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	, DictionaryKeyPolicy = JsonKnownNamingPolicy.CamelCase
	, GenerationMode = JsonSourceGenerationMode.Default
	, IgnoreReadOnlyFields = true
	, IgnoreReadOnlyProperties = true
	, IncludeFields = false
	, PropertyNameCaseInsensitive = true
	, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase
	, ReadCommentHandling = JsonCommentHandling.Skip
	// , TypeInfoResolver = new TodoPolymorphicTypeResolver()
#if DEBUG
	, WriteIndented = true
#endif
	, Converters = [
		typeof(ObjectConverter),
		// typeof(BindableModelConverter),
	]
)]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(IDictionary<string, object?>))]
[JsonSerializable(typeof(ISynqraCommand))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(Synqra.Event))]
[JsonSerializable(typeof(Synqra.CommandCreatedEvent))]
[JsonSerializable(typeof(Synqra.ObjectCreatedEvent))]
[JsonSerializable(typeof(Synqra.ObjectPropertyChangedEvent))]
[JsonSerializable(typeof(Synqra.Command))]
[JsonSerializable(typeof(Synqra.CreateObjectCommand))]
[JsonSerializable(typeof(Synqra.ChangeObjectPropertyCommand))]
[JsonSerializable(typeof(FooContosoCommand))]
[JsonSerializable(typeof(FooContosoEvent))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(Int64))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(TransportOperation))]

[JsonConverter(typeof(ObjectConverter))] // re-supplied with extras below
public partial class ContosoJsonSerializerContext : JsonSerializerContext
{
	public static JsonSerializerOptions DefaultOptions { get; }

	static ContosoJsonSerializerContext()
	{
		DefaultOptions = new JsonSerializerOptions(Default.Options)
		{
			TypeInfoResolver = JsonTypeInfoResolver.Combine(new SynqraJsonTypeInfoResolver([
				  typeof(FooContosoCommand)
				, typeof(FooContosoEvent)
				]), Default),
		};
	}
}
