using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Synqra;


/// <summary>
/// Low-level storage interface for storing and retrieving events
/// </summary>
public interface IStorage<T, TKey> : IDisposable, IAsyncDisposable
	where T : IIdentifiable<TKey>
{
	Task AppendAsync(T item);

	IAsyncEnumerable<T> GetAll(TKey? from = default, CancellationToken? cancellationToken = default);

	[EditorBrowsable(EditorBrowsableState.Advanced)]
	Task FlushAsync()
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
		=> Task.CompletedTask
#endif
		;
}

public interface IIdentifiable<TKey>
{
	TKey Id { get; }
}

internal class AttachedData
{
	public string? TrackingSinceJsonSnapshot { get; set; }
}

public class CommandHandlerContext
{
	internal List<Event> Events { get; set; } = new List<Event>();
}

public class EventVisitorContext
{
}

class TypeMetadata
{
	public Type Type { get; set; }
	public Guid TypeId { get; set; }

	public override string ToString()
	{
		return $"{TypeId.ToString("N")[..4]} {Type.Name}";
	}
}
