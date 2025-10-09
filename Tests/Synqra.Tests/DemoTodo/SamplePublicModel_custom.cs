// NOTE! this is a different class name - for experiments with manual code instead of generator
/* THIS IS A SANDBOX AND DEMO OF WHAT NEEDS TO BE GENERATED */

using Synqra.BinarySerializer;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TUnit.Assertions.AssertConditions.Operators;

namespace Synqra.Tests.DemoTodo;

partial class SamplePublicModel_ : INotifyPropertyChanging, INotifyPropertyChanged, IBindableModel
{
	public partial string? Name { get; set; }

	public event PropertyChangedEventHandler? PropertyChanged;
	public event PropertyChangingEventHandler? PropertyChanging;

	partial void OnNameChanging(string? newValue);
	partial void OnNameChanging(string? oldValue, string? newValue);
	partial void OnNameChanged(string? newValue);
	partial void OnNameChanged(string? oldValue, string? newValue);

	ISynqraStoreContext __store;

	ISynqraStoreContext IBindableModel.Store
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

	public void Set(ISBXSerializer serializer, float version, ReadOnlySpan<byte> buffer, ref int pos)
	{
		// Positional Fields: Name
		__name = serializer.DeserializeString(buffer, ref pos);
	}

	public void Get(ISBXSerializer serializer, float version, Span<byte> buffer, ref int pos)
	{
		// Positional Fields: Name
		serializer.Serialize(buffer, __name, ref pos);

		// Optional Presence Mask Fields: (??)
		// Keyed Fields: (??)
		// Named Fields: (auto processed by serializer later, it is never here)
	}
}