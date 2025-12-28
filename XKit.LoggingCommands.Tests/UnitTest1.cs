namespace XKit.LoggingCommands.Tests;

public class LoggingCommandsTests
{
	[SetUp]
	public void Setup()
	{
	}

	[Test]
	[Explicit("It is better to delay this feature and implement it when scripting is needed")]
	[TestCase("Log this message")]
	[TestCase("##vso[test]")] // mninimal VSO example
	[TestCase("::warning::hohoho")]
	[TestCase("##warning## hohoho")]
	[TestCase("##upsert[id=1 def=2]")]
	[TestCase("::command par1=val1 par2=val2:: optional body")]
	[TestCase("::vso.command par1=val1 par2=val2:: optional body")]
	[TestCase("::q.upsert par1=val1 par2=val2:: optional body")]
	[TestCase("::type=q.upsert par1=val1 par2=val2 value=optionalbody::")]
	[TestCase("<System.Upsert par1=<System.Decimal value=12> par2=val2 value=optionalbody::")]
	[TestCase("[upsert par1=val1 par2=val2]")]
	[TestCase("[upsert par1=val1 par2=[upser]]")]
	[TestCase("[a=1 b=2]")] // {'a'=1, 'b'=2}
	[TestCase("[a=1 type=upsert b=2]")] // {'a'=1, 'b'=2, 'type'='upsert'} type is descriminator
	[TestCase("[upsert a=1 b=2]")]
	[TestCase("[upsert a=1 b=[decimal 12]]")]
	public void Should_10_detect_and_execute_logging_command(string line)
	{
		Assert.Inconclusive();
	}

	[Test]
	public void Should_15_create_new_requark()
	{
		Assert.Inconclusive();
	}
}
