namespace Synqra.BinarySerializer;

public static class SchemasBundleExtensions
{
	/// <summary>This process's schemas bundle: the mapping every serializer the factory creates writes and reads with.</summary>
	public static SchemasBundle CreateSchemasBundle(this ISbxSerializerFactory factory)
		=> factory.CreateSerializer() is ISbxSchemaVersions versions
			? new SchemasBundle(versions)
			: throw new NotSupportedException($"{factory.GetType().Name} creates serializers that do not report their schema versions.");
}
