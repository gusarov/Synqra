namespace Synqra.BinarySerializer;

/// <summary>One <see cref="SbxSerializer.Map(int, double, Type, IModelBinder?)"/> entry: the id written for a type, and the schema version its binder reads and writes.</summary>
public sealed record SbxMapping(int MapId, Type Type, float SchemaVersion, IModelBinder? Binder);
