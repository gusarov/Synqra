using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Synqra.Tests.Helpers;

class TestConfigurationSection(string theKey, string theValue) : IConfiguration, IConfigurationSection
{
	public string Key => theKey;
	public string Path => theKey;

	string? IConfigurationSection.Value
	{
		get => theValue;
		set => throw new NotImplementedException();
	}

	public string? this[string key]
	{
		get => throw new NotImplementedException();
		set => throw new NotImplementedException();
	}

	public IEnumerable<IConfigurationSection> GetChildren()
	{
		return [];
	}

	public IChangeToken GetReloadToken()
	{
		throw new NotImplementedException();
	}

	public IConfigurationSection GetSection(string key)
	{
		throw new NotImplementedException();
	}
}

