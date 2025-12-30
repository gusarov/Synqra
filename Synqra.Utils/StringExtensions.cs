using System.Buffers.Text;
using System.Text;

namespace Synqra;

public static partial class StringExtensions
{
	static Encoding _utf8 = new UTF8Encoding(false, false);

	public static string Utf8(this byte[] bytes)
	{
		return _utf8.GetString(bytes);
	}

	public static string Utf8(this byte[] bytes, int index, int count)
	{
		return _utf8.GetString(bytes, index, count);
	}

	public static byte[] Utf8(this string str)
	{
		return _utf8.GetBytes(str);
	}

	/*
	/// <summary>
	/// Base 64 Url Encoded
	/// [+/] => [-_] and no padding
	/// </summary>
	public static string Base64(this ReadOnlySpan<byte> bytes)
	{
		return Base64Url.EncodeToString(bytes);
		// return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	}

	// [Obsolete("Consider Span version or Base64Url class")]
	/// <summary>
	/// Base 64 Url Encoded
	/// [+/] => [-_] and no padding
	/// </summary>
	public static string Base64(this byte[] bytes)
	{
		return Base64Url.EncodeToString(bytes.AsSpan());
		// return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	}

	// [Obsolete("Consider Span version or Base64Url class")]
	/// <summary>
	/// Base 64 Url Encoded
	/// [+/] => [-_] and no padding
	/// </summary>
	public static string Base64(this byte[] bytes, int index, int count)
	{
		return Base64Url.EncodeToString(bytes.AsSpan(index, count));
		// return Convert.ToBase64String(bytes, index, count).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	}

	// [Obsolete("You might find it is usefult to do via Base64Url instead")]
	public static byte[] Base64(this string str)
	{
		return Base64Url.DecodeFromChars(str);
		/ *
		switch (str.Length % 4) // Pad with '=' to make the length a multiple of 4.
		{
			case 2: str += "=="; break;
			case 3: str += "="; break;
		}
		return Convert.FromBase64String(str.Replace('-', '+').Replace('_', '/'));
		* /
	}
	*/

	public static string Hex(this byte[] bytes, int index, int count)
	{
		char[] c = new char[count * 2];
		int b;
		for (int i = 0; i < count; i++)
		{
			b = bytes[index + i] >> 4;
			c[i * 2] = (char)(55 + b + (((b - 10) >> 31) & -7));
			b = bytes[index + i] & 0xF;
			c[i * 2 + 1] = (char)(55 + b + (((b - 10) >> 31) & -7));
		}
		return new string(c);
		// return BitConverter.ToString(bytes, index, count);
	}

	public static string Hex(this byte[] bytes)
	{
		return Hex(bytes, 0, bytes.Length);
	}

	public static byte[] Hex(this string hexString)
	{
		if (hexString.Length % 2 != 0)
		{
			throw new ArgumentException("The binary key cannot have an odd number of digits");
		}

		int GetHexVal(char hex)
		{
			int val = (int)hex;
			// Return the value by subtracting 48, then adjusting for A-F/a-f by subtracting an additional 7 or 32 respectively.
			return val - (val < 58 ? 48 : (val < 97 ? 55 : 87));
		}

		var len = hexString.Length >> 1;
		var arr = new byte[len];
		for (var i = 0; i < len; ++i)
		{
			arr[i] = (byte)((GetHexVal(hexString[i << 1]) << 4) + (GetHexVal(hexString[(i << 1) + 1])));
		}

		return arr;
	}

}