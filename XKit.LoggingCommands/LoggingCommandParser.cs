
namespace XKit.LoggingCommands;

public interface ILoggingCommandParser
{
	// Command 
}

public abstract class Command
{

}

public class LoggingCommandParser
{
	public bool TryParse(string inputLine, out string command)
	{
		if (inputLine.StartsWith("/log "))
		{
			command = inputLine.Substring(5).Trim();
			return true;
		}
		command = null;
		return false;
	}
}
