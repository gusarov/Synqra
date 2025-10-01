// This is to experiment with CommunityMVVM as well proven source generator around properties

using CommunityToolkit.Mvvm.ComponentModel;

namespace Synqra.Tests.DemoTodo;

public partial class CommunityMVVMTest : ObservableObject
{
	[ObservableProperty]
	string _roperty2;

#if CMVVM
	[ObservableProperty]
	public partial string Property3 { get; set; }
#endif
}

