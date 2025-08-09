using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Intrinsics.Arm;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Synqra;

internal class GuidExtensions
{
	static Encoding _encoding = new UnicodeEncoding(true, false, false);
	// static SHA1 sha1 = SHA1.Create();

	public static Guid CreateVersion7()
	{
#if NET9_0_OR_GREATER
		return Guid.CreateVersion7();
#else
		var g = Guid.NewGuid();
		/*
		if ((g.Variant | 0b0111) == 0) // 0 .. 7
		{
			// Apollo NCS variant 1980
		}
		else if ((g.Variant | 0b1011) == 0b1011) // 8..11 (8,9,a,b)
		{
			// OSF DCE RFC 4122 "Leach–Salz" UUIDs
		}
		else if ((g.Variant & 0b1101) == 0b1101) // 12..13
		{
			// Microsoft Legacy COM
		}
		else
		{
			throw new Exception($"Unknown variant: {g.Variant}");
		}
		g = g.WithVersion(7);
		*/
		return g;
#endif
	}

	public static unsafe Guid CreateVersion3(string input)
	{
		return CreateVersion3(_encoding.GetBytes(input));
	}

	public static unsafe Guid CreateVersion3(byte[] input)
	{
		using var md5 = MD5.Create();
		var hash = md5.ComputeHash(input);
		hash[8] = (byte)((hash[8] & 0x3F) | 0xA0); // Set Variant to 0b10xx
		hash[7] = (byte)((hash[7] & 0x0F) | 0x30); // Set version to 3
#if NET9_0_OR_GREATER
		var g = new Guid(hash[..16]);
		if (g.Variant < 8 || g.Variant > 11)
		{
			throw new Exception($"vAriant is not set: {g.Variant}");
		}

		if (g.Version != 3)
		{
			throw new Exception($"vErsion is not set: {g.Version}");
		}
		return g;
#else
		throw new Exception($"Standard Target Not Supported");
#endif
	}

	public static unsafe Guid CreateVersion5(string input)
	{
		return CreateVersion5(_encoding.GetBytes(input));
	}

	public static unsafe Guid CreateVersion5(byte[] input)
	{
		using var sha1 = SHA1.Create();
		/*
		var g = Guid.NewGuid();

		if ((g.Variant | 0b0111) == 0) // 0 .. 7
		{
			// Apollo NCS variant 1980
		}
		else if ((g.Variant | 0b1011) == 0b1011) // 8..11 (8,9,a,b)
		{
			// OSF DCE RFC 4122 "Leach–Salz" UUIDs
		}
		else if ((g.Variant & 0b1101) == 0b1101) // 12..13
		{
			// Microsoft Legacy COM
		}
		else
		{
			// Reserved
		}
		*/

		// Console.WriteLine("Var={0} {0:B}", g.Variant);
		// Console.WriteLine("Ver={0} {0:B}", g.Version);

		// using var sha1 = SHA1.Create();
		byte[] hash;
		//lock (_sha1)
		{
			hash = sha1.ComputeHash(input);
		}

		/*
		for (int i = 0; i < 16; i++)
		{
			var h = hash[..16].ToArray();
			h[i] = 0;
			var gg = new Guid(h);
			Console.WriteLine("{3}\tNVar={0} {0:B} NVer={1} {1:B} {2}", gg.Variant, gg.Version, gg, i);
		}
		*/

		/*
		var g2 = new Guid(hash[..16]);
		Console.WriteLine("NVar={0} {0:B}", g2.Variant);
		Console.WriteLine("NVer={0} {0:B}", g2.Version);
		Console.WriteLine(g2);
		*/

		hash[8] = (byte)((hash[8] & 0x3F) | 0xA0); // Set Variant to 0b10xx
		hash[7] = (byte)((hash[7] & 0x0F) | 0x50); // Set version to 5

		var g3 = new Guid(hash[..16]);

		/*
		Console.WriteLine("NVar={0} {0:B}", g3.Variant);
		Console.WriteLine("NVer={0} {0:B}", g3.Version);
		Console.WriteLine(g3);
		*/

		if (g3.Variant < 8 || g3.Variant > 11)
		{
			throw new Exception($"vAriant is not set: {g3.Variant}");
		}

		if (g3.Version != 5)
		{
			throw new Exception($"vErsion is not set: {g3.Version}");
		}

		/*
		var maxBytes = _encoding.GetMaxByteCount(input.Length);
		/ *
		Span<byte> buffer = maxBytes <= 1024 * 16
			? stackalloc byte[maxBytes]
			: ArrayPool<byte>.Shared.Rent(maxBytes);
		;
		* /
		var buffer = ArrayPool<byte>.Shared.Rent(maxBytes);
		var bytesCount = _encoding.GetBytes(input, buffer);

		Span<byte> hash = stackalloc byte[20];
		sha1.TransformBlock(buffer, 0, , hash);

		var byteCount = _encoding.GetBytes(input, buffer);
		// var hashBytes = sha1b.ComputeHash(hashBytes, 0, byteCount);

		Console.WriteLine(sizeof(Guid));
		Console.WriteLine(buffer.Length);
		*/


		return g3;
	}
}
