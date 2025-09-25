using CommunityToolkit.Mvvm.ComponentModel;

namespace Synqra.Tests.DemoTodo;

public partial class CommunityMVVMTest : ObservableObject
{
	[ObservableProperty]
	string _roperty2;

	[ObservableProperty]
	public partial string Property3 { get; set; }
}

