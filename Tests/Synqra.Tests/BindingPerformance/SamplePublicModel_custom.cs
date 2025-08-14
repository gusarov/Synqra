using Synqra.Model;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using TUnit.Assertions.AssertConditions.Operators;

namespace Synqra.Tests.BindingPerformance;

partial class SamplePublicModel_ : INotifyPropertyChanging, INotifyPropertyChanged, IBindableModel
{
	public partial string Name { get; set; }

	/* THIS IS A SANDBOX AND DEMO OF WHAT NEEDS TO BE GENERATED */

	public event global::System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
	public event global::System.ComponentModel.PropertyChangingEventHandler? PropertyChanging;

	global::Synqra.ISynqraStoreContext? IBindableModel.Store { get; set; }

	void IBindableModel.Set(string name, object? value)
	{
		switch (name)
		{
			case "Name":
				Name = (string)value;
				break;
		}
	}
	partial void OnNameChanging(string value);
	partial void OnNameChanging(string oldValue, string value);
	partial void OnNameChanged(string value);
	partial void OnNameChanged(string oldValue, string value);

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
		this.Name = "Value 0";
	}
}