using Microsoft.Extensions.DependencyInjection;
using Synqra.Tests.TestHelpers;

namespace Synqra.Tests.Miscellaneous;

public class BaseTestTests : BaseTest
{
	[Test]
	public async Task Should_allow_configure_di_and_use_it()
	{
		ServiceCollection.AddSingleton<IDemoService, DemoService>();

		var ds = ServiceProvider.GetRequiredService<IDemoService>();

		await Assert.That(ds).IsNotNull();
	}
}

public interface IDemoService
{
}

public class DemoService : IDemoService
{

}