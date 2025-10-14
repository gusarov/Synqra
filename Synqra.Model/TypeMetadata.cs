namespace Synqra;

class TypeMetadata
{
	public Type Type { get; set; }
	public Guid TypeId { get; set; }

	public override string ToString()
	{
		return $"{TypeId.ToString("N")[..4]} {Type.Name}";
	}
}
