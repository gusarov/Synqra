using System.Diagnostics;

namespace Contoso.Playwright;

[SetUpFixture]
public class _OneTimeSetupFixture
{
	[OneTimeSetUp]
	public void OneTimeSetUp()
	{
		if (Debugger.IsAttached)
		{
			Environment.SetEnvironmentVariable("PWDEBUG", "1");
		}
	}
}