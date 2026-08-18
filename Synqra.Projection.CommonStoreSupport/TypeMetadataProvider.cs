using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Synqra;

public static class TypeMetadataProviderExtensions
{
	public static void AddTypeMetadataProvider(this IServiceCollection services, params Type[] types)
	{
		services.AddSingleton<ITypeMetadataProvider, TypeMetadataProvider>();
		// Production id factory. A test host registers DeterministicSynqraIdProvider before/after to
		// override it (last explicit registration wins); TryAdd keeps this as the default otherwise.
		services.TryAddSingleton<ISynqraIdProvider, SynqraIdProvider>();
		services.PostConfigure<TypeMetadataProviderConfig>(x =>
		{
			x.Types ??= new List<Type>();
			foreach (var type in types)
			{
				x.Types.Add(type);
			}
			x.Types.Add(typeof(Command));
			// x.Types.Add(typeof(Event));
		});
	}

	public static TypeMetadata GetTypeMetadata(this ITypeMetadataProvider provider, Type type)
	{
		if (provider is TypeMetadataProvider typeMetadataProvider)
		{
			return typeMetadataProvider.GetTypeMetadata(type);
		}
		throw new Exception("Invalid type metadata provider");
	}

	private class TypeMetadataProviderConfig
	{
		public List<Type> Types { get; set; } = new();
	}

	private class TypeMetadataProvider : ITypeMetadataProvider
	{
		private readonly Dictionary<Type, TypeMetadata> _typeMetadataByType = new();
		private readonly Dictionary<Guid, TypeMetadata> _typeMetadataByTypeId = new();

		public TypeMetadataProvider(IOptions<TypeMetadataProviderConfig> options)
		{
			foreach (var type in options.Value.Types ?? [])
			{
				RegisterType(type);
			}
		}

		public void RegisterType(Type type)
		{
			ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_typeMetadataByType, type, out var exists);
			if (!exists)
			{
				var sma = type.GetCustomAttribute<SynqraModelAttribute>();
				var legacyTypeIds = type.GetCustomAttributes<SynqraLegacyTypeIdAttribute>()
					.Select(x => x.SynqraTypeId)
					.Distinct()
					.ToArray();
				Guid typeId = sma?.SynqraTypeId ?? GuidExtensions.CreateVersion5(SynqraGuids.SynqraTypeNamespaceId, type.FullName); // it is not a secret, so for type identification SHA1 is totally fine
				// Namespace "Synqra" itself, not just "Synqra.*": ObjectDeletedEvent lives in the bare
				// namespace and escaped this guard for exactly that reason. Interfaces are exempt — an
				// interface is never the runtime type of an instance, so its id never reaches a `_t`.
				var ns = type.Namespace;
				var isBuiltIn = !type.IsInterface
					&& (ns == "Synqra" || true == ns?.StartsWith("Synqra."))
					&& ns.ToLowerInvariant().Contains("test") == false
				;
				if (typeId.GetVersion() == 5 && isBuiltIn)
				{
					throw new Exception($"Built-in type {type.FullName} must declare an explicit [SynqraModel(id)] — see docs/model.md §8; a derived v5 id is opaque and would leak into every persisted event of that type");
				}
				slot = new TypeMetadata
				{
					Type = type,
					TypeId = typeId,
				};
				_typeMetadataByType[type] = slot;
				_typeMetadataByTypeId[slot.TypeId] = slot;
				// Old ids resolve to the same type after the current id changes — lets a type's id be
				// migrated without orphaning already-persisted data. See SynqraLegacyTypeIdAttribute.
				foreach (var legacyTypeId in legacyTypeIds)
				{
					_typeMetadataByTypeId[legacyTypeId] = slot;
				}
			}
		}

		public TypeMetadata GetTypeMetadata(Type type)
		{
			if (_typeMetadataByType.TryGetValue(type ?? throw new ArgumentNullException(nameof(type)), out var metadata))
			{
				return metadata;
			}
			throw new ArgumentException($"Type {type.FullName} is not registered");
		}

		public TypeMetadata GetTypeMetadata(Guid typeId)
		{
			if (typeId == default)
			{
				throw new ArgumentException("typeId is empty", nameof(typeId));
			}
			if (_typeMetadataByTypeId.TryGetValue(typeId, out var metadata))
			{
				return metadata;
			}
			throw new ArgumentException($"TypeId {typeId} is not registered");
		}
	}
}
