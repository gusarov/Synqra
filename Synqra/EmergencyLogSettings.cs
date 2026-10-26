using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Synqra;

/// <summary><c>Synqra:EmergencyLog</c> in app settings.</summary>
public sealed class EmergencyLogOptions
{
	public const string SectionName = "Synqra:EmergencyLog";

	/// <summary>On or off; unset keeps what the runtimeconfig switch "Synqra.EmergencyLog.Enabled" said (off by default).</summary>
	public bool? Enabled { get; set; }
}

/// <summary>Applies <see cref="EmergencyLogOptions.Enabled"/> to the log when the host starts, and again whenever it changes.</summary>
internal sealed class EmergencyLogSettingsApplier(EmergencyLog log, IOptionsMonitor<EmergencyLogOptions> options) : IHostedService, IDisposable
{
	IDisposable? _subscription;

	public Task StartAsync(CancellationToken cancellationToken)
	{
		Apply(options.CurrentValue);
		_subscription = options.OnChange(Apply);
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		Dispose();
		return Task.CompletedTask;
	}

	public void Dispose()
	{
		_subscription?.Dispose();
		_subscription = null;
	}

	void Apply(EmergencyLogOptions settings)
	{
		if (settings.Enabled is bool on)
		{
			log.IsOn = on;
		}
	}
}

public static class EmergencyLogSettingsExtensions
{
	/// <summary>Lets app settings turn <see cref="EmergencyLog.Default"/> on or off (<c>Synqra:EmergencyLog:Enabled</c>).</summary>
	public static IServiceCollection AddEmergencyLogSettings(this IServiceCollection services, IConfiguration configuration)
	{
		services.Configure<EmergencyLogOptions>(configuration.GetSection(EmergencyLogOptions.SectionName));
		services.TryAddSingleton(EmergencyLog.Default);
		services.AddHostedService<EmergencyLogSettingsApplier>();
		return services;
	}
}
