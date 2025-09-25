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
	public partial string Name { get; set; }

	/* THIS IS A SANDBOX AND DEMO OF WHAT NEEDS TO BE GENERATED */

	public event PropertyChangedEventHandler? PropertyChanged;
	public event PropertyChangingEventHandler? PropertyChanging;

	partial void OnNameChanging(string newValue);
	partial void OnNameChanging(string oldValue, string newValue);
	partial void OnNameChanged(string newValue);
	partial void OnNameChanged(string oldValue, string newValue);

	ISynqraStoreContext IBindableModel.Store
	{
		get => field;
		set => field = value;
	}

	void IBindableModel.Set(string propertyName, object? value)
	{
		switch (propertyName)
		{
			case nameof(Name):
				Name = value as string;
				break;
		}
	}


	[ThreadStatic]
	static bool _assigning; // when true, the source of the change is model binding due to new events reaching the context, so it is external change. This way, when setter see false here - it means the source is a client code, direct property change by consumer.

	public partial string Name
	{
		get => field;
		set
		{
			var bm = (IBindableModel)this;
			if (_assigning || bm.Store is null)
			{
				var oldValue = field;
				if (!global::System.Collections.Generic.EqualityComparer<string>.Default.Equals(oldValue, value))
				{
					OnNameChanging(value);
					OnNameChanging(oldValue, value);
					PropertyChanging?.Invoke(this, new PropertyChangingEventArgs(nameof(Name)));
					field = value;
					OnNameChanged(value);
					OnNameChanged(oldValue, value);
					PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
				}
			}
			else
			{
				bm.Store.SubmitCommandAsync(new ChangeObjectPropertyCommand
				{
					CommandId = GuidExtensions.CreateVersion7(),
					ContainerId = default,
					CollectionId = default,
					TargetTypeId = bm.Store.GetId(this, null, GetMode.RequiredId),
					TargetId = bm.Store.GetId(this, null, GetMode.RequiredId),
					PropertyName = nameof(Name),
					OldValue = field,
					NewValue = value
				}).GetAwaiter().GetResult();
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
}