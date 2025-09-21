using Synqra;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TUnit.Assertions.AssertConditions.Operators;

namespace Synqra.Tests.BindingPerformance;

partial class SamplePublicModel_ : INotifyPropertyChanging, INotifyPropertyChanged, IBindableModel
{
	public partial string TestProperty { get; set; }

	/* THIS IS A SANDBOX AND DEMO OF WHAT NEEDS TO BE GENERATED */

	public event PropertyChangedEventHandler? PropertyChanged;
	public event PropertyChangingEventHandler? PropertyChanging;

	partial void OnTestPropertyChanging(string newValue);
	partial void OnTestPropertyChanging(string oldValue, string newValue);
	partial void OnTestPropertyChanged(string newValue);
	partial void OnTestPropertyChanged(string oldValue, string newValue);

	ISynqraStoreContext IBindableModel.Store
	{
		get => field;
		set => field = value;
	}

	void IBindableModel.Set(string propertyName, object? value)
	{
		switch (propertyName)
		{
			case nameof(TestProperty):
				TestProperty = value as string;
				break;
		}
	}

	public partial string TestProperty
	{
		get => field;
		set
		{
			if (!global::System.Collections.Generic.EqualityComparer<string>.Default.Equals(field, value))
			{
				OnTestPropertyChanging(value);
				OnTestPropertyChanging(field, value);
				PropertyChanging?.Invoke(this, new PropertyChangingEventArgs(nameof(TestProperty)));
				field = value;
				OnTestPropertyChanged(value);
				OnTestPropertyChanged(field, value);
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TestProperty)));
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
		this.TestProperty = "Value 0";
	}
}