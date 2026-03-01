using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Synqra;

public static class TypeMetadataProviderExtensions
{
	public static void AddTypeMetadataProvider(this IHostApplicationBuilder hostApplicationBuilder, params Type[] types)
	{
		hostApplicationBuilder.Services.AddSingleton<ITypeMetadataProvider, TypeMetadataProvider>();
		hostApplicationBuilder.Services.PostConfigure<TypeMetadataProviderConfig>(x =>
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
				Guid typeId = sma?.SynqraTypeId ?? GuidExtensions.CreateVersion5(SynqraGuids.SynqraTypeNamespaceId, type.FullName); // it is not a secret, so for type identification SHA1 is totally fine
				slot = new TypeMetadata
				{
					Type = type,
					TypeId = typeId,
				};
				_typeMetadataByType[type] = slot;
				_typeMetadataByTypeId[slot.TypeId] = slot;
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
