using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions.Extensions;

namespace Synqra.Tests;

/// <summary>App settings turn the emergency log on or off through IOptions; unset leaves it as the runtimeconfig switch set it.</summary>
public class EmergencyLogSettingsTests
{
	async Task<EmergencyLog> StartWithAsync(Dictionary<string, string?> settings, bool startsOn)
	{
		var log = new EmergencyLog(new EmergencyLoggerProvider(new StringWriter(), startsOn));
		var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
		var services = new ServiceCollection();
		services.AddSingleton(log);
		services.AddEmergencyLogSettings(configuration);
		await using var provider = services.BuildServiceProvider();
		foreach (var hosted in provider.GetServices<IHostedService>())
		{
			await hosted.StartAsync(CancellationToken.None);
		}
		return log;
	}

	[Test]
	public async Task Enabled_true_turns_it_on()
	{
		var log = await StartWithAsync(new Dictionary<string, string?> { ["Synqra:EmergencyLog:Enabled"] = "true", }, startsOn: false);

		await Assert.That(log.IsOn).IsTrue();
	}

	[Test]
	public async Task Enabled_false_turns_it_off()
	{
		var log = await StartWithAsync(new Dictionary<string, string?> { ["Synqra:EmergencyLog:Enabled"] = "false", }, startsOn: true);

		await Assert.That(log.IsOn).IsFalse();
	}

	[Test]
	public async Task Unset_leaves_it_as_it_was()
	{
		var log = await StartWithAsync(new Dictionary<string, string?>(), startsOn: false);

		await Assert.That(log.IsOn).IsFalse();
	}
}
