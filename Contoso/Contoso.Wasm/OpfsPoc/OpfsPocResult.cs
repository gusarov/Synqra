namespace Contoso.Wasm.OpfsPoc;

public sealed class OpfsPocResult
{
	public bool IsSuccess { get; set; }

	public string Summary { get; set; } = string.Empty;

	public string CompletedAtUtc { get; set; } = string.Empty;

	public List<OpfsSuiteResult> Suites { get; set; } = [];
}

public sealed class OpfsSuiteResult
{
	public string Key { get; set; } = string.Empty;

	public string Name { get; set; } = string.Empty;

	public bool Supported { get; set; }

	public bool Passed { get; set; }

	public string Message { get; set; } = string.Empty;

	public List<OpfsStepResult> Steps { get; set; } = [];
}

public sealed class OpfsStepResult
{
	public string Key { get; set; } = string.Empty;

	public string Name { get; set; } = string.Empty;

	public bool Passed { get; set; }

	public string Message { get; set; } = string.Empty;
}
