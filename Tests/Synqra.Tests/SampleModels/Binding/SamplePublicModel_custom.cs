// NOTE! this is a different class name - for experiments with manual code instead of generator
/* THIS IS A SANDBOX AND DEMO OF WHAT NEEDS TO BE GENERATED */

using Synqra.BinarySerializer;
using Synqra.Tests.SampleModels.Serialization;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TUnit.Assertions.AssertConditions.Operators;

namespace Synqra.Tests.SampleModels.Binding;


public class SampleFieldListBaseModel_ : IBindableModel
{
	public IList<SampleBaseModel> Data { get; set; }

	public IObjectStore? Store { get; set; }

	public void Set(string propertyName, object? value)
	{
		throw new NotImplementedException();
	}

	public void Get(ISBXSerializer serializer, float schemaVersion, in Span<byte> buffer, ref int pos)
	{
		serializer.Serialize(in buffer, Data, ref pos); // TODO need to pass weather this list requires typeId or not
	}

	public void Set(ISBXSerializer serializer, float schemaVersion, in ReadOnlySpan<byte> buffer, ref int pos)
	{
		// DeserializeList - only executed when it is known from static data (field or request type) all information about the list.
		// Deserialize<IList<SampleBaseModel>> - executed when there is not way to guarantee the list type or element types
		Data = serializer.Deserialize<IList<SampleBaseModel>>(in buffer, ref pos); // TODO need to pass weather this list requires typeId or not
	}
}

public class SampleFieldEnumerableBaseModel_ : IBindableModel
{
	public IEnumerable<SampleBaseModel> Data { get; set; }

	public IObjectStore? Store { get; set; }

	public void Set(string propertyName, object? value)
	{
		throw new NotImplementedException();
	}

	public void Get(ISBXSerializer serializer, float schemaVersion, in Span<byte> buffer, ref int pos)
	{
		serializer.Serialize(in buffer, Data, ref pos); // TODO need to pass weather this list requires typeId or not
	}

	public void Set(ISBXSerializer serializer, float schemaVersion, in ReadOnlySpan<byte> buffer, ref int pos)
	{
		// DeserializeList - only executed when it is known from static data (field or request type) all information about the list.
		// Deserialize<IList<SampleBaseModel>> - executed when there is not way to guarantee the list type or element types
		Data = serializer.Deserialize<IEnumerable<SampleBaseModel>>(in buffer, ref pos); // TODO need to pass weather this list requires typeId or not
	}
}

/// <summary>
/// Note, this is different class name - for experiments with manual code instead of generator
/// The _ at the end stops code generation
/// </summary>
partial class SamplePublicModel_ : INotifyPropertyChanging, INotifyPropertyChanged, IBindableModel
{
	public partial string? Name { get; set; }

	public event PropertyChangedEventHandler? PropertyChanged;
	public event PropertyChangingEventHandler? PropertyChanging;

	partial void OnNameChanging(string? newValue);
	partial void OnNameChanging(string? oldValue, string? newValue);
	partial void OnNameChanged(string? newValue);
	partial void OnNameChanged(string? oldValue, string? newValue);

	IObjectStore __store;

	IObjectStore IBindableModel.Store
	{
		get => __store;
		set => __store = value;
	}

	void IBindableModel.Set(string propertyName, object? value)
	{
		//var previous = _assigning;
		_assigning = true;
		try
		{
			switch (propertyName)
			{
				case nameof(Name):
					Name = (string?)value;
					break;
			}
		}
		finally
		{
			//_assigning = previous;
			_assigning = false;
		}
	}

	[ThreadStatic]
	static bool _assigning; // when true, the source of the change is model binding due to new events reaching the context, so it is external change. This way, when setter see false here - it means the source is a client code, direct property change by consumer.

	string? __name;

	public partial string? Name
	{
		get => __name;
		set
		{
			var oldValue = __name;
			if (_assigning || __store is null)
			{
				var pci = PropertyChanging;
				var pce = PropertyChanged;
				if (pci is null && pce is null)
				{
					OnNameChanging(value);
					OnNameChanging(oldValue, value);
					__name = value;
					OnNameChanged(value);
					OnNameChanged(oldValue, value);
				}
				else if (!Equals(oldValue, value))
				{
					// throw null;
					OnNameChanging(value);
					OnNameChanging(oldValue, value);
					pci?.Invoke(this, new PropertyChangingEventArgs(nameof(Name)));
					__name = value;
					OnNameChanged(value);
					OnNameChanged(oldValue, value);
					pce?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
				}
			}
			else
			{
				OnNameChanging(value);
				OnNameChanging(oldValue, value);
				__store.SubmitCommandAsync(new ChangeObjectPropertyCommand
				{
					CommandId = GuidExtensions.CreateVersion7(),
					ContainerId = default,
					CollectionId = default,

					Target = this,
					TargetId = __store.GetId(this, null, GetMode.RequiredId),
					TargetTypeId = default,
					// TargetTypeId = __store.GetId(this, null, GetMode.RequiredId),

					PropertyName = nameof(Name),
					OldValue = oldValue,
					NewValue = value
				}).GetAwaiter().GetResult(); // properties are never async
			}
		}
	}

	/// <summary>
	/// This class represents both a command and an event.
	/// </summary>
	public class CustomCommandEvent
	{

	}

	public void CustomCommand1_Reset()
	{
		Name = "Value 0";
	}

	public void Set(ISBXSerializer serializer, float version, in ReadOnlySpan<byte> buffer, ref int pos)
	{
		// Positional Fields: Name
		__name = serializer.DeserializeString(in buffer, ref pos);
	}

	public void Get(ISBXSerializer serializer, float version, in Span<byte> buffer, ref int pos)
	{
		// Positional Fields: Name
		serializer.Serialize(in buffer, __name, ref pos);

		// Optional Presence Mask Fields: (??)
		// Keyed Fields: (??)
		// Named Fields: (auto processed by serializer later, it is never here)
	}
}