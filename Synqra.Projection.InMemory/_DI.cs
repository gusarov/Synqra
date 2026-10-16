using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synqra.AppendStorage;

namespace Synqra.Projection.InMemory;

public static class InMemorySynqraExtensions
{
	static InMemorySynqraExtensions()
	{
		// AOT ROOTS:
		_ = typeof(IAppendStorage<Event, (Guid, Guid)>);
	}

	/// <summary>
	/// Register the stream-agnostic in-memory projection-area contracts: a factory that creates a
	/// single-stream <see cref="InMemoryProjection"/> on demand (<see cref="IProjectionFactory"/>), a
	/// provider that hands out the cached latest projection per stream
	/// (<see cref="IProjectionProvider"/>), and the keeper that drives delta catch-up from the registered
	/// <see cref="IEventLog"/> (<see cref="IProjectionKeeper"/>).
	/// <para>
	/// No stream is pinned here — there is deliberately no <c>AddInMemorySynqraStore(streamId)</c> that
	/// registers a projection singleton bound to a fixed stream (a stream id is a security boundary,
	/// not a DI key). Callers obtain a projection per stream at runtime via the provider/factory (a
	/// fresh random stream in tests, the session stream on a client); the provider brings it up to date
	/// with <see cref="IProjectionKeeper.MaintainAsync"/> before hand-out.
	/// </para>
	/// The in-memory <b>projection</b> is strictly non-multitenant: one instance == one stream, so it
	/// is never a DI singleton. The <b>event log</b> (<see cref="IEventLog"/>, registered separately: a storage keyed by
	/// <c>(StreamId, SequenceId)</c> plus <c>AddClientEventLog</c>) IS multitenant — a stream is a key prefix — so it stays
	/// a singleton, exactly like the durable Mongo/File event stores.
	/// </summary>
	public static void AddInMemorySynqraStore(this IServiceCollection services)
	{
		services.TryAddSingleton<IProjectionFactory, InMemoryProjectionFactory>();
		services.TryAddSingleton<IProjectionProvider, InMemoryProjectionProvider>();
		services.TryAddSingleton<IProjectionKeeper, ProjectionKeeper>();
	}

	/// <summary>
	/// As <see cref="AddInMemorySynqraStore(IServiceCollection)"/>, but the factory/provider produce a
	/// domain subclass of <see cref="InMemoryProjection"/> (e.g. Contoso's
	/// <c>ContosoInMemoryProjection</c>) instead of the base projection.
	/// </summary>
	public static void AddInMemorySynqraStore<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] TProjection>(this IServiceCollection services)
		where TProjection : InMemoryProjection
	{
		services.TryAddSingleton<IProjectionFactory, InMemoryProjectionFactory<TProjection>>();
		services.TryAddSingleton<IProjectionProvider, InMemoryProjectionProvider>();
		services.TryAddSingleton<IProjectionKeeper, ProjectionKeeper>();
	}
}
