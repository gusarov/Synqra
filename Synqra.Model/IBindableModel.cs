namespace Synqra;

public interface IBindableModel
{
	ISynqraStoreContext Store { get; set; }
	void Set(string propertyName, object? value);
}

