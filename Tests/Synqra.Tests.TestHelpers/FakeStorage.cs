
namespace Synqra.Tests.TestHelpers;

public class FakeStorage : IStorage
{
	public Task Add(Event theEvent)
	{
		throw new NotImplementedException();
	}

	public string GetTestName()
	{
		return "TestHelper";
	}
}
