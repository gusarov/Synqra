namespace Synqra.Tests.SampleModels.Serialization;

// The same model after A was dropped: schema 1.0 still names it, no property does.
[SynqraModel("C488A741-F890-4E7A-B915-06169A11B385")]
[Schema(1.0, "1 A Guid B string?")]
[Schema(2.0, "1 B string?")]
public partial class SampleDroppedFieldReaderModel
{
	public partial string? B { get; set; }
}
