using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace Synqra.Tests;

public class TypeMetadataProviderConcurrencyTests
{
	const int Rounds = 20;
	const int Writers = 4;
	const int Readers = 4;

	Type[] Arguments { get; } = [typeof(int), typeof(long), typeof(string), typeof(Guid), typeof(byte), typeof(short), typeof(double), typeof(decimal), typeof(char), typeof(uint)];

	/// <summary>1000 distinct closed generics, none registered up front.</summary>
	List<Type> LateTypes => Arguments.SelectMany(a => Arguments.SelectMany(b => Arguments.Select(c => typeof(Tuple<,,>).MakeGenericType(a, b, c)))).ToList();

	/// <summary>Registered before the race starts; every lookup of them must succeed throughout.</summary>
	List<Type> EarlyTypes => Arguments.Select(a => typeof(KeyValuePair<,>).MakeGenericType(a, a)).ToList();

	[Test]
	public async Task Should_register_look_up_and_enumerate_types_from_many_threads_at_once()
	{
		var failures = new ConcurrentQueue<Exception>();
		var lateTypes = LateTypes;
		var earlyTypes = EarlyTypes;
		var registeredCounts = new List<int>();
		for (var round = 0; round < Rounds; round++)
		{
			var provider = CreateProvider(earlyTypes);
			RunRace(provider, earlyTypes, lateTypes, failures);
			VerifyAllRegistered(provider, earlyTypes.Concat(lateTypes), failures);
			registeredCounts.Add(provider.RegisteredTypes.Count);
		}

		await Assert.That(string.Join(" | ", failures.Select(x => $"{x.GetType().Name}: {x.Message}").Distinct().Take(5))).IsEmpty();
		await Assert.That(registeredCounts.Distinct()).IsEquivalentTo([earlyTypes.Count + lateTypes.Count + 1]); // + Command
	}

	ITypeMetadataProvider CreateProvider(IEnumerable<Type> types)
	{
		var services = new ServiceCollection();
		services.AddTypeMetadataProvider(types.ToArray());
		return services.BuildServiceProvider().GetRequiredService<ITypeMetadataProvider>();
	}

	void RunRace(ITypeMetadataProvider provider, List<Type> earlyTypes, List<Type> lateTypes, ConcurrentQueue<Exception> failures)
	{
		using var start = new Barrier(Writers + Readers);
		var writing = Enumerable.Range(0, Writers)
			.Select(w => Task.Run(() => Collect(failures, () =>
			{
				start.SignalAndWait();
				// each writer walks the list from its own offset: new keys and already-known keys race together
				for (var i = 0; i < lateTypes.Count; i++)
				{
					provider.RegisterType(lateTypes[(i + w * lateTypes.Count / Writers) % lateTypes.Count]);
				}
			})))
			.ToArray();
		var done = Task.WhenAll(writing);
		var reading = Enumerable.Range(0, Readers)
			.Select(r => Task.Run(() => Collect(failures, () =>
			{
				start.SignalAndWait();
				while (!done.IsCompleted)
				{
					ReadEverything(provider, earlyTypes);
				}
				ReadEverything(provider, earlyTypes);
			})))
			.ToArray();
		Task.WaitAll([.. writing, .. reading]);
	}

	void ReadEverything(ITypeMetadataProvider provider, List<Type> earlyTypes)
	{
		foreach (var type in earlyTypes)
		{
			var metadata = provider.GetTypeMetadata(type);
			if (provider.GetTypeMetadata(metadata.TypeId) != metadata)
			{
				throw new InvalidOperationException($"{type} resolves to a different metadata by id");
			}
		}
		// whatever the listing shows must already be resolvable both ways
		foreach (var metadata in provider.RegisteredTypes)
		{
			if (provider.GetTypeMetadata(metadata.Type) != metadata || provider.GetTypeMetadata(metadata.TypeId) != metadata)
			{
				throw new InvalidOperationException($"{metadata.Type} is listed but resolves to a different metadata");
			}
		}
	}

	void VerifyAllRegistered(ITypeMetadataProvider provider, IEnumerable<Type> types, ConcurrentQueue<Exception> failures)
	{
		Collect(failures, () =>
		{
			var listed = provider.RegisteredTypes.ToList();
			if (listed.Count != listed.Select(x => x.Type).Distinct().Count())
			{
				throw new InvalidOperationException("A type is listed twice");
			}
			foreach (var type in types)
			{
				var metadata = provider.GetTypeMetadata(type);
				if (provider.GetTypeMetadata(metadata.TypeId) != metadata || !listed.Contains(metadata))
				{
					throw new InvalidOperationException($"{type} was not registered consistently");
				}
			}
		});
	}

	void Collect(ConcurrentQueue<Exception> failures, Action action)
	{
		try
		{
			action();
		}
		catch (Exception ex)
		{
			failures.Enqueue(ex);
		}
	}
}
