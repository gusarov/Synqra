using Synqra.BinarySerializer;

namespace Synqra.Tests.SampleModels.TwinB;

/// <summary>
/// The other <c>Twin</c>; see <see cref="TwinA.Twin"/>. Hand-written, not generated: the generator emits one
/// source per simple name, so two generated twins cannot share an assembly (Quotaly's two <c>Node</c>s live in
/// different ones). Only its registration as an <see cref="IBindableModel"/> named <c>Twin</c> matters here.
/// </summary>
public sealed class Twin : IBindableModel
{
	public string? Title { get; set; }

	IObjectStore? IBindableModel.Store => null;

	Guid? IBindableModel.CollectionId => null;

	public void Attach(IObjectStore store, Guid collectionId)
	{
	}

	void IBindableModel.Get(ISbxSerializer serializer, float schemaVersion, in Span<byte> buffer, ref int pos) => throw new NotSupportedException("a registry twin only");

	void IBindableModel.Set(string propertyName, object? value) => throw new NotSupportedException("a registry twin only");

	void IBindableModel.Set(ISbxSerializer serializer, float schemaVersion, in ReadOnlySpan<byte> buffer, ref int pos) => throw new NotSupportedException("a registry twin only");
}
