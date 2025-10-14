using System.Text;

namespace Synqra;

public class HexDumpWriter
{
	// Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	public void HexDump(ReadOnlySpan<byte> span, Action<string> write, Action<char>? writeChar = null)
	{
		if (writeChar == null)
		{
			writeChar = (c) => write(c.ToString());
		}
		if (span.Length <= 20)
		{
			for (int i = 0, m = span.Length; i < m; i++)
			{
				write(span[i].ToString("X2"));
				if ((i + 1) % 4 == 0)
				{
					write("  ");
				}
				else
				{
					write(" ");
				}
			}
			write(Environment.NewLine);
			for (int i = 0, m = span.Length; i < m; i++)
			{
				var c = (char)span[i];
				if (c == 0)
				{
					c = '.';
				}
				else if (c < 32/* || c > 126*/)
				{
					c = '?';
				}
				write(string.Format("{0,2}", c));
				if ((i + 1) % 4 == 0)
				{
					write("  ");
				}
				else
				{
					write(" ");
				}
			}
			write(Environment.NewLine);
			write(Environment.NewLine);
		}
		else
		{
			int pos = 0;
			while (span.Length - pos >= 16)
			{
				for (int i = 0; i < 16; i++)
				{
					write(span[pos + i].ToString("X2"));
					if ((i + 1) % 4 == 0)
					{
						write("  ");
					}
					else
					{
						write(" ");
					}
				}
				for (int i = 0; i < 16; i++)
				{
					var c = (char)span[pos + i];
					if (c == 0)
					{
						c = '.';
					}
					else if (c < 32/* || c > 126*/)
					{
						c = '?';
					}
					writeChar(c);
					if ((i + 1) % 4 == 0)
					{
						write(" ");
					}
				}
				pos += 16;
				write(Environment.NewLine);
			}
			if (span.Length - pos > 0)
			{
				var rem = span.Length - pos;
				for (int i = 0; i < rem; i++)
				{
					write(span[pos + i].ToString("X2"));
					if ((i + 1) % 4 == 0)
					{
						write("  ");
					}
					else
					{
						write(" ");
					}
				}
				for (int i = rem; i < 16; i++)
				{
					write("  ");
					if ((i + 1) % 4 == 0)
					{
						write("  ");
					}
					else
					{
						write(" ");
					}
				}
				for (int i = 0; i < rem; i++)
				{
					var c = (char)span[pos + i];
					if (c == 0)
					{
						c = '.';
					}
					else if (c < 32/* || c > 126*/)
					{
						c = '?';
					}
					writeChar(c);
					if ((i + 1) % 4 == 0)
					{
						write(" ");
					}
				}
				pos += rem;
			}
		}
	}
}
