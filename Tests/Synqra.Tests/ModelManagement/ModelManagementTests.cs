using Microsoft.Extensions.DependencyInjection;
using Synqra.Storage;
using Synqra.Tests.BindingPerformance;
using Synqra.Tests.DemoTodo;
using Synqra.Tests.TestHelpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Synqra.Tests.ModelManagement;

internal class ModelManagementTests : BaseTest<ISynqraStoreContext>
{
	[Test]
	public async Task Should_emit_command_by_setting_property()
	{
		HostBuilder.Services.AddSingleton<JsonSerializerContext>(DemoTodo.TestJsonSerializerContext.Default);
		HostBuilder.Services.AddSingleton(DemoTodo.TestJsonSerializerContext.Default.Options);
		HostBuilder.AddJsonLinesStorage();
		HostBuilder.AddSynqraStoreContext();

		var model = new DemoMdl();
		_sut.Get<DemoMdl>().Add(model);
		model.Name = "TestName"; // this should emit a command and broadcast it

		var commands = _sut.Get<Synqra.ISynqraCommand>().ToArray();

		Trace.WriteLine("Commands:");
		foreach (var item in commands)
		{
			Trace.WriteLine(item.ToString());
		}
		Trace.WriteLine("Commands Done");


		await Assert.That(commands.Count()).IsEqualTo(2);
		var co = (CreateObjectCommand)commands[0];

		var cop = (ChangeObjectPropertyCommand)commands[1];
		await Assert.That(cop.PropertyName).IsEqualTo(nameof(model.Name));
		await Assert.That(cop.OldValue).IsEqualTo(null);
		await Assert.That(cop.NewValue).IsEqualTo("TestName");
	}
}


/*
public partial class DemoStoreContext : StoreContext
{
	public DemoStoreContext(IStorage storage) : base(storage)
	{
	}
}
*/

public class DemoMdl
{
	public string Name { get; set; }
}
