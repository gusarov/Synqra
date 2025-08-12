namespace Synqra;

public interface IBindableModel
{
	ISynqraStoreContext? Store { get; set; }
	void Set(string propertyName, object? value);
}

public static class BinderModes
{
	[ThreadStatic]
	public static BinderFlags Current; // default is always 000, it is always like that except when event is replayed locally
}

/// <summary>
/// Bit 0 - must be 1 for any custom value. If 0, it is default runtime state and other flags are ignored.
/// Bit 1 - should setter raise INotifyPropertyChanged events?
/// Bit 2 - should setter produce a command in the store?
/// </summary>
public enum BinderFlags
{
	None = 0b_000, // default is always 000, so this supposed to be normal runtime state
	RaisePropertyChanged = 0b_011,
	SuppressPropertyChanged = 0b_001,
	EmitCommand = 0b_101,
	SuppressCommand = 0b_001,
}
