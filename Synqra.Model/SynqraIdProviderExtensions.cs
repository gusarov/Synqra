using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace Synqra;

/// <summary>
/// Type-aware conveniences over <see cref="ISynqraIdProvider"/>. They live in the model layer rather
/// than beside the interface because reading a type's <see cref="SynqraModelAttribute"/> needs it.
/// </summary>
public static class SynqraIdProviderExtensions
{
	static readonly ConcurrentDictionary<Type, Guid> _declaredTypeIds = new ConcurrentDictionary<Type, Guid>();

	/// <summary>
	/// A command instance id labelled with <typeparamref name="TCommand"/>'s own class — the statically
	/// typed spelling of <see cref="ISynqraIdProvider.CreateCommandId"/> for call sites that know the
	/// command type at compile time.
	/// </summary>
	public static Guid CreateCommandId<TCommand>(this ISynqraIdProvider ids) where TCommand : Command
		=> (ids ?? throw new ArgumentNullException(nameof(ids))).CreateCommandId(DeclaredTypeId(typeof(TCommand)));

	/// <summary>
	/// The id of the <paramref name="ordinal"/>-th event of type <typeparamref name="TEvent"/> that
	/// <paramref name="commandId"/> expands to. Naming the event type here is what lets a structured
	/// command id yield an event id carrying the <i>event's</i> class rather than the command's — and
	/// what keeps two different events of one command distinguishable.
	/// <para>
	/// The derivation itself is provider-independent, so this goes straight to the static
	/// <see cref="SynqraIdDerivation.DeriveEventId"/>; <paramref name="ids"/> is kept only to preserve the
	/// extension-method call shape at the hundreds of existing call sites.
	/// </para>
	/// </summary>
	public static Guid CreateEventId<TEvent>(this ISynqraIdProvider ids, Guid commandId, int ordinal) where TEvent : Event
	{
		_ = ids ?? throw new ArgumentNullException(nameof(ids));
		return SynqraIdDerivation.DeriveEventId(commandId, DeclaredTypeId(typeof(TEvent)), ordinal);
	}

	/// <summary>
	/// The id a type declares through <see cref="SynqraModelAttribute"/>, or <see cref="Guid.Empty"/> when
	/// it declares none. Deliberately does <i>not</i> fall back to the derived v5 id: callers want the
	/// structured semantic class, and a derived id carries none.
	/// </summary>
	public static Guid DeclaredTypeId(Type type)
		=> _declaredTypeIds.GetOrAdd(type ?? throw new ArgumentNullException(nameof(type)),
			t => t.GetCustomAttribute<SynqraModelAttribute>()?.SynqraTypeId ?? default);
}
