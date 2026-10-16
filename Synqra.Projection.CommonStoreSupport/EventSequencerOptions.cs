namespace Synqra;

/// <summary>Tuning for <see cref="EventSequencer"/>; a host changes it with <c>services.Configure&lt;EventSequencerOptions&gt;(...)</c>.</summary>
public sealed class EventSequencerOptions
{
	/// <summary>How far ahead of the server clock a proposal may run and still be kept (the Kerberos five minutes).</summary>
	public TimeSpan MaxAhead { get; set; } = TimeSpan.FromMinutes(5);
}
