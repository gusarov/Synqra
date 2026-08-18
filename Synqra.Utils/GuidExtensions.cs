using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Synqra;

public static class GuidExtensions
{
	/// <summary>
	/// This is default state of counters. If you want to have a dedicated one - create your own instance of State class.
	/// </summary>
	public static Generator Default { get; } = new Generator();

	public class Generator
	{
		// For v1 & v6: Same as Mongo ObjectID, it is reset on client start, not saved
		private readonly ulong _v1v6nodeId; // actually only 6 bytes (48 bit) is in use
		private readonly byte _guidLen;
		private int _clockSeq; // actually only 1.5 bytes (12 bits) is in use but Interlocked works with int

		// For v7 monotonicy
		private long _prevOmniStamp; // custom layout of ms<<12 | sub_ms>>2 (see details in implementation)

		/// <summary>
		/// 
		/// </summary>
		/// <param name="nodeId">V1 and V6 has Node Id field. When zero - it Random for every generator. Default generator will have random on every application start.</param>
		/// <param name="guidLen">Guids are 16 byte by default. This setting allows you to make shorter v7 guids, zero ending, e.g. 12 is mongo guids length (but not same semantic!)</param>
		public unsafe Generator(ulong nodeId = 0, byte guidLen = 16)
		{
			if (nodeId == 0)
			{
				try
				{
					var rng = RandomNumberGenerator.Create();
#if NETSTANDARD2_0
					var buffer = new byte[8];
					rng.GetBytes(buffer);
					nodeId = BitConverter.ToUInt64(buffer, 0);
#else
					var span = new Span<byte>((byte*)&nodeId, 8);
					rng.GetBytes(span);
#endif
				}
				catch (Exception ex)
				{
					EmergencyLog.Default.LogError(ex.ToString());
					// fallback in case of crypto configuration problems
#if NETSTANDARD
					var buffer = new byte[8];
					new Random().NextBytes(buffer);
					nodeId = BitConverter.ToUInt64(buffer, 0);
#else
					nodeId = unchecked((ulong)Random.Shared.NextInt64());
#endif
				}
			}
			_v1v6nodeId = nodeId & 0x0000FFFFFFFFFFFF | 0x0000010000000000; // set multicast bit to avoid using real MAC address (See RFC spec)
			_guidLen = guidLen;
			if (guidLen < 10 || guidLen > 16)
			{
				throw new ArgumentException("Guid v7 length must be between 10 and 16 bytes. This feature truncates higher bytes to zero and requires adequate random space.", nameof(guidLen));
			}
		}

		public unsafe Generator()
			: this(0)
		{
		}

		[Obsolete("Use CreateVersion7 instead")]
		public unsafe Guid CreateVersion1()
		{
			return GuidExtensions.CreateVersion1(DateTime.UtcNow, (ushort)Interlocked.Increment(ref _clockSeq), _v1v6nodeId);
		}

		public unsafe Guid CreateVersion6()
		{
			return CreateVersion6(DateTime.UtcNow, (ushort)Interlocked.Increment(ref _clockSeq), _v1v6nodeId);
		}

		internal unsafe Guid CreateVersion6(DateTimeOffset dateTime, ushort clockSeq, ulong node)
		{
			var greg_100_ns = dateTime.ToUniversalTime().Ticks - GregEpochTicks;

			Guid g = default;
			byte* bytes = (byte*)&g;

			*(uint*)&g = (uint)(greg_100_ns >> 28); // write time_high
			*(ushort*)(bytes + 4) = (ushort)(greg_100_ns >> 12); // write time_mid
			*(ushort*)(bytes + 6) = (ushort)((ushort)(greg_100_ns & 0x0FFF) | (ushort)(6 << 12)); // write time_low & version 6

			clockSeq = (ushort)(clockSeq & 0x3FFF | 0x8000); // variant 1, 0x10: RFC 4122
			if (BitConverter.IsLittleEndian)
			{
				clockSeq = (ushort)((clockSeq << 8) | (clockSeq >> 8));
			}
			*(ushort*)(bytes + 8) = clockSeq;

			byte* nodeBytes = (byte*)&node;
			if (BitConverter.IsLittleEndian)
			{
				for (byte i = 0; i < 6; i++)
				{
					bytes[10 + i] = nodeBytes[5 - i];
				}
			}
			else
			{
				for (byte i = 0; i < 6; i++)
				{
					bytes[10 + i] = nodeBytes[i];
				}
			}
			return g;
		}

		public Guid CreateVersion7() => CreateVersion7(DateTime.UtcNow);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private unsafe Guid CreateVersion7(DateTimeOffset timestamp)
		{
			return CreateVersion7(timestamp.ToUniversalTime().Ticks);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal unsafe Guid CreateVersion7(DateTime timestamp)
		{
			if (timestamp.Kind != DateTimeKind.Utc)
			{
				throw new Exception("Only UTC time supported");
			}
			return CreateVersion7(timestamp.Ticks);
		}

		/// <summary>
		/// If this guid monotonically follows the previous guid, then it is returned as is and becomes previous guid.
		/// Otherwise a new guid is created with incremented timestamp part, preserving the rest of the guid.
		/// </summary>
		public unsafe Guid CreateVersion7OrApprove(Guid example)
		{
			if (example == default)
			{
				return CreateVersion7();
			}
			if (example.GetVersion() != 7)
			{
				throw new Exception("Only GUID v7 is supported");
			}
			// Extract omnistamp
			byte* bytes = (byte*)&example;
			long omniStamp = *(ushort*)(bytes + 6); // 12 bits of c
			omniStamp &= 0x0FFF; // clear version bits
			omniStamp |= (*(ushort*)(bytes + 4)) << 12; // b
			omniStamp |= (*(uint*)bytes) << 28; // a

			// Rotate omnistamp
			bool fix = false;
			while (true)
			{
				var previousOmniStamp = _prevOmniStamp;
				if (omniStamp <= previousOmniStamp)
				{
					omniStamp = previousOmniStamp + 1;
					fix = true;
				}
				if (Interlocked.CompareExchange(ref _prevOmniStamp, omniStamp, previousOmniStamp) == previousOmniStamp)
				{
					break; // success
				}
			}

			if (fix)
			{
				// this code writes to example!!
				uint a = (uint)(omniStamp >> 28);
				ushort b = (ushort)(omniStamp >> 12);
				ushort c = (ushort)((((ushort)(omniStamp)) & 0xfff | (0x7 << 12))); // version 7 in high nibble and 12 bits of sub-ms time

				*(uint*)bytes = a;
				*(ushort*)(bytes + 4) = b;
				*(ushort*)(bytes + 6) = c;

				// Also randomize random part!!
				var ng = Guid.NewGuid();
				byte* ngBytes = (byte*)&ng;
				for (int i = 8; i < _guidLen; i++)
				{
					bytes[i] = ngBytes[i];
				}
				for (int i = _guidLen; i < 16; i++)
				{
					bytes[i] = 0;
				}

			}
			return example; // all good, return as is
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private unsafe Guid CreateVersion7(long ticks)
		{
			// Monotonicy
			// Option 0: artificially bump ts to counter if input ts repeats (limited velocity, 1 GUID per 1 ms)
			// Option 1: intra-ms counter in rand_a short (empty bytes in normal case, but can be randomly seeded except 1 bit, guessable in 1 ms space)
			//       a) start from 0 and count if repeated ms
			//       b) start from random and reset MSB to get 50% space
			// Option 2: random_b is random first time in ms, but after that it increments like bigint or BigEndian Long (guessable in 1 ms space)
			// Option 3: use rand_a for high precision time (DateTime already has 100ns precision) This can be combined with other options. E.g. very good combo with option 0.

			// DECISION: Option 3+0. This also aligns well with anti-rollover requirement and leap seconds. Code name - "omnistamp"

			// The change from 3 ticks increment to 4 ticks increment:
			// 1) Allows to avoid floating arithmetics and just do x>>2
			// 2) Reduces rand_b space from full 0-4095 range to only 0-2500. Time remained in ticks is up to 10000 ticks. So it is 40% of the range, the rest is good to avoid spinning ms to compensate overflows.
			// 3) This considered a good compromise because allows to issue bulk of GUIDs in a single ms.

			var g = Guid.NewGuid(); // extremely optimized — supplies rand_b and the sub-ms seed below
			byte* bytes = (byte*)&g;

			// Sub-ms field seed. DateTime.UtcNow is millisecond-granular on some hosts (Linux/containers),
			// so `ticks % 10000` was 0 there and every id read "…-7000-…". Seed the 12-bit sub-ms field from
			// entropy instead (top bit cleared so the monotonic bump below keeps headroom to increment within
			// a ms). GetTimestamp is unaffected — v7 reads only the millisecond bits from groups 1-2, never
			// this field — and ordering stays monotonic via the counter loop, now by generation order rather
			// than the (frequently-unavailable) sub-ms clock. Source bytes 0-1 are overwritten below.
			ushort subMsSeed = (ushort)(*(ushort*)bytes & 0x07FF);

			ticks -= UnixEpochTicks;

			// Omni Stamp is a combo of ms<<12 & the sub-ms seed
			long omniStamp = ((ticks / 10000) << 12) | subMsSeed;

			while (true)
			{
				var previousOmniStamp = _prevOmniStamp;
				if (omniStamp <= previousOmniStamp)
				{
					omniStamp = previousOmniStamp + 1;
				}
				if (Interlocked.CompareExchange(ref _prevOmniStamp, omniStamp, previousOmniStamp) == previousOmniStamp)
				{
					break; // success
				}
			}

			uint a = (uint)(omniStamp >> 28);
			ushort b = (ushort)(omniStamp >> 12);
			ushort c = (ushort)((((ushort)(omniStamp)) & 0xfff | (0x7 << 12))); // version 7 in high nibble and 12 bits of sub-ms time

			*(uint*)bytes = a;
			*(ushort*)(bytes + 4) = b;
			*(ushort*)(bytes + 6) = c;

			bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant 1, 0x10: RFC 4122

			for (int i = _guidLen; i < 16; i++)
			{
				bytes[i] = 0;
			}

			return g;
		}

		public Guid CreateVersion8sq()
		{
			// Synqra Guid is v8 custom guid with a following rules:
			// first 4 bytes is unix seconds time



			return default;
		}

	}

	private static readonly Encoding _utf8 = new UTF8Encoding(false, false); // for name-based UUIDs
	public const long UnixEpochTicks = 0x089F7FF5F7B58000; // new DateTime(1970, 01, 01, 0, 0, 0, DateTimeKind.Utc).Ticks;
	public const long GregEpochTicks = 0x06ED6223E4344000; // new DateTime(1582, 10, 15, 0, 0, 0, DateTimeKind.Utc).Ticks;

	// https://www.rfc-editor.org/rfc/rfc9562.html?utm_source=chatgpt.com#name-namespace-id-usage-and-allo
	private static readonly Guid _namespaceDns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
	private static readonly Guid _namespaceUrl = new Guid("6ba7b811-9dad-11d1-80b4-00c04fd430c8");

	public static unsafe int GetVariant(this Guid guid)
	{
		byte variantByte = ((byte*)&guid)[8];

		// The variant is determined by the pattern of the most significant bits:
		// 0xxxxxxx - 0 (Apollo NCS Legacy)
		// 10xxxxxx - 1 (RFC 4122)
		// 110xxxxx - 2 (Microsoft Legacy)
		// 111xxxxx - 3 (Future/Reserved/very unlikely to be used)
		if ((variantByte & 0x80) == 0x00) // 0b0xx
		{
			return 0;
		}
		else if ((variantByte & 0xC0) == 0x80) // 0b10x
		{
			return 1;
		}
		else if ((variantByte & 0xE0) == 0xC0) // 0b110x
		{
			return 2;
		}
		else
		{
			return 3;
		}
	}

	public static unsafe int GetVersion(this Guid guid, bool zeroForDefault = true)
	{
		if (zeroForDefault && guid == Guid.Empty)
		{
			return 0;
		}
		var variant = guid.GetVariant();
		if (variant != 1)
		{
			throw new Exception($"Cannot get version of non-RFC UUIDs. Variant is {variant} but only 1 is supported");
		}
#if NET9_0_OR_GREATER
		return guid.Version;
#elif __NET8_0_OR_GREATER // not efficient, no point in unsafe context
		ref var guidMap = ref MemoryMarshal.AsRef<GuidMap>(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref guid, 1))); // no copy, just reinterpret
		return guidMap._c >> 12;
#else
		byte* bytes = (byte*)&guid;
		byte versionByte = bytes[BitConverter.IsLittleEndian ? 7 : 6]; // due to mixed Endian layout of GUIDs, version is actually in byte 7, not 6.
		return versionByte >> 4;
#endif
	}

	public static unsafe DateTime GetTimestamp(this Guid guid, bool zeroForDefault = true)
	{
		if (zeroForDefault && guid == Guid.Empty)
		{
			return default;
		}
		var ver = guid.GetVersion(zeroForDefault: zeroForDefault);

		var bytes = (byte*)&guid;
		var ints = (uint*)&guid; // there is only one valid int at 0 index
		var shorts = (ushort*)(bytes + 4); // there are 2 shorts at 0 & 1 index

		switch (ver)
		{
			case 1:
			{
				var tsLow = ints[0];
				long tsMid = shorts[0];
				long tsHigh = shorts[1] & 0x0FFF;

				var greg_100_ns = (tsHigh << 48) | (tsMid << 32) | tsLow;
				return new DateTime(GregEpochTicks + greg_100_ns, DateTimeKind.Utc);
			}
			case 2:
				throw new NotImplementedException();
			case 6:
			{
				long tsHigh = ints[0];
				long tsMid = shorts[0];
				var tsLow = shorts[1] & 0x0FFF;

				var greg_100_ns = (tsHigh << 28) | (tsMid << 12) | tsLow;
				return new DateTime(GregEpochTicks + greg_100_ns, DateTimeKind.Utc);
			}
			case 7:
			{
				long tsHigh = ints[0];
				var tsLow = shorts[0];
				long unix_64_bit_ms = (tsHigh << 16) | tsLow;

				return new DateTime(UnixEpochTicks, DateTimeKind.Utc).AddMilliseconds(unix_64_bit_ms);
			}
			default:
				throw new NotSupportedException($"Cannot get timestamp of UUID v{ver}");
		}
	}

	[Obsolete("Use CreateVersion7 instead")]
	public static unsafe Guid CreateVersion1() => Default.CreateVersion1();

	[Obsolete("Use CreateVersion7 instead")]
	internal static unsafe Guid CreateVersion1(DateTimeOffset dateTime, ushort clockSeq, ulong node)
	{
		var greg_100_ns = dateTime.ToUniversalTime().Ticks - GregEpochTicks;

		Guid g = default;
		byte* bytes = (byte*)&g;

		*(uint*)&g = (uint)greg_100_ns; // write time_low
		*(ushort*)(bytes + 4) = (ushort)(greg_100_ns >> 32); // write time_mid
		*(ushort*)(bytes + 6) = (ushort)((ushort)(greg_100_ns >> 48) | (ushort)(1 << 12)); // write time_high & version 1

		clockSeq = (ushort)(clockSeq & 0x3FFF | 0x8000); // variant 1, 0x10: RFC 4122
		if (BitConverter.IsLittleEndian)
		{
			clockSeq = (ushort)((clockSeq << 8) | (clockSeq >> 8));
		}
		*(ushort*)(bytes + 8) = clockSeq;

		byte* nodeBytes = (byte*)&node;
		if (BitConverter.IsLittleEndian)
		{
			for (byte i = 0; i < 6; i++)
			{
				bytes[10 + i] = nodeBytes[5 - i];
			}
		}
		else
		{
			for (byte i = 0; i < 6; i++)
			{
				bytes[10 + i] = nodeBytes[i];
			}
		}
		return g;
	}

	[Obsolete("Use v5 with SHA1 or v8 with current best hash function")]
	public static unsafe Guid CreateVersion3Dns(string name)
	{
		return CreateVersion3(_namespaceDns, name);
	}

	[Obsolete("Use v5 with SHA1 or v8 with current best hash function")]
	public static unsafe Guid CreateVersion3Url(string url)
	{
		return CreateVersion3(_namespaceUrl, url);
	}

	[Obsolete("Use v5 with SHA1 or v8 with current best hash function")]
	public static unsafe Guid CreateVersion3(Guid namespaceId, string name)
	{
		// var max = _utf8.GetMaxByteCount(name.Length);
		return CreateVersion3(namespaceId, _utf8.GetBytes(name));
	}

	[Obsolete("Use v5 with SHA1 or v8 with current best hash function")]
	public static unsafe Guid CreateVersion3(Guid namespaceId, byte[] input)
	{
		using var md5 = MD5.Create();
		return CreateHashBased(md5, 3, namespaceId, input);
	}

	public static Guid CreateVersion4()
	{
		return Guid.NewGuid();
	}

	public static unsafe Guid CreateVersion5Dns(string name)
	{
		return CreateVersion5(_namespaceDns, name);
	}

	public static unsafe Guid CreateVersion5Url(string url)
	{
		return CreateVersion5(_namespaceUrl, url);
	}

	public static unsafe Guid CreateVersion5(Guid namespaceId, string name)
	{
		return CreateVersion5(namespaceId, _utf8.GetBytes(name));
	}

	public static unsafe Guid CreateVersion5(Guid namespaceId, byte[] input)
	{
		using var sha1 = SHA1.Create();
		return CreateHashBased(sha1, 5, namespaceId, input);
	}

	public static unsafe Guid CreateVersion6() => Default.CreateVersion6();

	public static Guid CreateVersion7() => Default.CreateVersion7();

	public static unsafe Guid CreateVersion8_Sha256_Dns(string name)
	{
		return CreateVersion8_Sha256(_namespaceDns, name);
	}

	public static unsafe Guid CreateVersion8_Sha256_Url(string url)
	{
		return CreateVersion8_Sha256(_namespaceUrl, url);
	}

	public static unsafe Guid CreateVersion8_Sha256(Guid namespaceId, string name)
	{
		return CreateVersion8_Sha256(namespaceId, _utf8.GetBytes(name));
	}

	const string SwitchValidateNamespaceIdKey = "Synqra.GuidExtensions.ValidateNamespaceId";
	const string SwitchValidateNamespaceIdHashChainKey = "Synqra.GuidExtensions.ValidateNamespaceIdHashChain";

#if NET9_0_OR_GREATER
	[FeatureSwitchDefinition(SwitchValidateNamespaceIdKey)] // hint for AOT trimmer
#endif
	internal static bool SwitchValidateNamespaceId => AppContext.TryGetSwitch(SwitchValidateNamespaceIdKey, out var v) ? v : true;

#if NET9_0_OR_GREATER
	[FeatureSwitchDefinition(SwitchValidateNamespaceIdHashChainKey)] // hint for AOT trimmer
#endif
	internal static bool SwitchValidateNamespaceIdHashChain => AppContext.TryGetSwitch(SwitchValidateNamespaceIdHashChainKey, out var v) ? v : true;

	static unsafe void ValidateNamespaceId(Guid namespaceId)
	{
		if (!AppContext.TryGetSwitch(SwitchValidateNamespaceIdKey, out var isValidationEnabled))
		{
			isValidationEnabled = true;
		}
		if (!isValidationEnabled)
		{
			return;
		}

		const string disclaimer = $". If you believe that this is not a mistake, you know what you are doing and you have legitimate case, you can disabled this validation with AppContext.SetSwitch(\"{SwitchValidateNamespaceIdKey}\", false).";
		// generating a UUIDv4 or UUIDv7 Namespace ID value is RECOMMENDED according to the spec.
		// I also discourage using default or empty namespace IDs, as they can lead to collisions. no point to use v1-v6 except allocated values

		if (namespaceId == default)
		{
			throw new ArgumentException("Empty namespace ID" + disclaimer, nameof(namespaceId));
		}
		/*
		if (namespaceId == Guid.AllBitsSet) // all bits set is not portable, but there is variant check below, so fine...
		{
			throw new ArgumentException("Max Guid namespace ID", nameof(namespaceId));
		}
		*/
		if (namespaceId.GetVariant() != 1)
		{
			// technically I should allow 3+ (other variants), but practically they will never be used and spec evolved to get proper versioning.
			throw new ArgumentException("Do not use variant 0x0 or 0x110 as namespace ID" + disclaimer, nameof(namespaceId));
		}
		switch (namespaceId.GetVersion())
		{
			case 0: // not recommended
			case 2: // not recommended
				throw new ArgumentException($"Do not use version {namespaceId.GetVersion()} (as well as 0, 2, 3, 5) as namespace ID" + disclaimer, nameof(namespaceId));
			case 3 when (SwitchValidateNamespaceIdHashChain): // hashbased for ns? no...
			case 5 when (SwitchValidateNamespaceIdHashChain): // hashbased for ns? no...
				throw new ArgumentException($"Do not use version {namespaceId.GetVersion()} (as well as 3, 5) as namespace ID because this creates a chain of hash-based IDs" + disclaimer.Replace(SwitchValidateNamespaceIdKey, SwitchValidateNamespaceIdHashChainKey), nameof(namespaceId));
			case 1: // only legal list, others are not recommended (use v4 or v7 instead)
			{
				// RFC pattern: xxxxxxxx-9dad-11d1-80b4-00c04fd430c8
				uint* uints = (uint*)&namespaceId;
				ulong* ulongs = (ulong*)&namespaceId;
				if (uints[1] != (BitConverter.IsLittleEndian ? 0x11d19dad : 0xad9dd111) || ulongs[1] != (BitConverter.IsLittleEndian ? 0xc830d44fc000b480 : 0x80b400c04fd430c8))
				{
					throw new ArgumentException("Do not use custom v1 namespace IDs, only RFC allocated ones" + disclaimer, nameof(namespaceId));
				}
				break;
			}
			case 4: // recommended
			case 7: // recommended
				break;
			default:
				// could be v8 or new future version, can't forbid
				break;
		}
	}

	// static readonly Guid _hashspaceSha256 = new Guid("3fb32780-953c-4464-9cfd-e85dbbe9843d"); // Hashspaces did not survived from the draft
	public static unsafe Guid CreateVersion8_Sha256(Guid namespaceId, byte[] input)
	{
		using var sha256 = SHA256.Create();

		/* Hashspaces did not survived from the draft
		var hashSpaceBuf = _hashspaceSha256.ToByteArray();
		if (BitConverter.IsLittleEndian)
		{
			Array.Reverse(hashSpaceBuf, 0, 4);
			Array.Reverse(hashSpaceBuf, 4, 2);
			Array.Reverse(hashSpaceBuf, 6, 2);
		}
		sha256.TransformBlock(hashSpaceBuf, 0, 16, null, 0);
		*/

		return CreateHashBased(sha256, 8, namespaceId, input);
	}

	public static unsafe Guid CreateHashBased(HashAlgorithm hashAlgorithm, byte version, Guid namespaceId, byte[] input)
	{
		ValidateNamespaceId(namespaceId);
#if TRUE || NETSTANDARD2_0
		var namespaceBuf = namespaceId.ToByteArray();
		if (BitConverter.IsLittleEndian)
		{
			Array.Reverse(namespaceBuf, 0, 4);
			Array.Reverse(namespaceBuf, 4, 2);
			Array.Reverse(namespaceBuf, 6, 2);
		}

		hashAlgorithm.TransformBlock(namespaceBuf, 0, 16, null, 0);
		hashAlgorithm.TransformFinalBlock(input, 0, input.Length);
		var hash = hashAlgorithm.Hash;

		hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // Set Variant to 0b10xx
		hash[6] = (byte)((hash[6] & 0x0F) | (version << 4)); // Set version

		if (BitConverter.IsLittleEndian)
		{
			Array.Reverse(hash, 0, 4);
			Array.Reverse(hash, 4, 2);
			Array.Reverse(hash, 6, 2);
		}

		return new Guid(hash[..16]);
#else
		var len = 16 + input.Length;
		const int StackLimit = 10240; // 10 KB
		byte[] rented = null;
		Span<byte> hash = stackalloc byte[hashAlgorithm.HashSize / 8];
		// TODO: Do not rent for large buffers! Do blocks instead!
		Span<byte> buf = len <= StackLimit ? stackalloc byte[len] : (rented = ArrayPool<byte>.Shared.Rent(len));
		try
		{
			if (BitConverter.IsLittleEndian)
			{
				//    int         - short - short - by-by - by-by-by-by-by-by
				//    int         - int           - int          -int
				//    short-short - short - short - short - short-short-short
				// 0x 00 00 00 00 - 00 00 - 00 00 - 00 00 - 00 00 00 00 00 00
				//     0  1  2  3    4  5    6  7    8  9   10 11 12 13 14 15

				byte* bytes = (byte*)&namespaceId;
				(bytes[0], bytes[1], bytes[2], bytes[3]) = (bytes[3], bytes[2], bytes[1], bytes[0]);
				(bytes[4], bytes[5]) = (bytes[5], bytes[4]);
				(bytes[6], bytes[7]) = (bytes[7], bytes[6]);

				/*
				int* ints = (int*)&namespaceId;
				short* shorts = (short*)&namespaceId;
				ints[0] = BinaryPrimitives.ReverseEndianness(ints[0]);
				shorts[2] = BinaryPrimitives.ReverseEndianness(shorts[2]);
				shorts[3] = BinaryPrimitives.ReverseEndianness(shorts[3]);
				*/
			}

			MemoryMarshal.Write(buf, ref namespaceId);
			for (int i = 0, m = input.Length; i < m; i++)
			{
				buf[i + 16] = input[i];
			}
			if (!hashAlgorithm.TryComputeHash(buf, hash, out int bytesWritten))
			{
				throw new Exception($"Failed to hash");
			}
			if (bytesWritten != hash.Length)
			{
				throw new Exception($"Unexpected hash length {bytesWritten} but expected {hash.Length}");
			}

			hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // Set Variant to 0b10xx
			hash[6] = (byte)((hash[6] & 0x0F) | (version << 4)); // Set version

			if (BitConverter.IsLittleEndian)
			{
				byte* bytes = (byte*)&hash;
				(hash[0], hash[1], hash[2], hash[3]) = (hash[3], hash[2], hash[1], hash[0]);
				(hash[4], hash[5]) = (hash[5], hash[4]);
				(hash[6], hash[7]) = (hash[7], hash[6]);
			}

			return new Guid(hash[..16]);
		}
		finally
		{
			if (rented != null)
			{
				ArrayPool<byte>.Shared.Return(rented);
			}
		}
#endif
	}

	/// <summary>
	/// Deterministically derives the id of the <paramref name="ordinal"/>-th event a command expands
	/// to, from the command's own client-generated v7 id. This makes the whole command→event
	/// expansion reproducible across nodes and replays (core.md §8: same command ⇒ same events) with
	/// no clock or shared counter. Modelled on the Todo predecessor's id layout (a reserved low-bytes
	/// counter region + increment): here the low 56 random bits are incremented by <paramref name="ordinal"/>
	/// while the timestamp, version and variant bytes are preserved, so the result stays a valid,
	/// time-ordered v7 that sorts adjacent to its command (model.md §8: v7 monotonic for all data).
	/// </summary>
	public static unsafe Guid Derive(Guid commandId, int ordinal)
	{
		if (ordinal < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(ordinal));
		}
		byte* b = (byte*)&commandId;
		// Increment the trailing 56 random bits (bytes 9..15, big-endian). Bytes 0..8 (timestamp +
		// version nibble + variant) are untouched, so the derived id is still a valid v7 sharing the
		// command's time position; ordinals stay far below the 56-bit space for any real command.
		ulong low = 0;
		for (int i = 9; i < 16; i++)
		{
			low = (low << 8) | b[i];
		}
		low += (ulong)ordinal;
		for (int i = 15; i >= 9; i--)
		{
			b[i] = (byte)low;
			low >>= 8;
		}
		return commandId;
	}

	/// <summary>
	/// True when <paramref name="guid"/> is a <b>structured</b> Synqra id: the <c>C0DE</c> magic prefix
	/// plus RFC 9562 version 8, i.e. an application-defined layout whose group-4 carries an allocation
	/// mode nibble
	/// and a 12-bit semantic class (model.md §8). Opaque ids — v7 data, v5 derived type ids, v4 tokens,
	/// and unrelated v8 hashes such as <see cref="CreateVersion5"/>'s siblings — are not structured, so
	/// callers must not read a class out of them.
	/// </summary>
	public static unsafe bool IsStructuredId(this Guid guid)
	{
		if (guid == Guid.Empty)
		{
			return false;
		}
		if (guid.GetVariant() != 1 || guid.GetVersion() != 8)
		{
			return false;
		}
		byte* b = (byte*)&guid;
		// group 1 is stored in native order, so the C0DE magic lands in bytes 3,2 on little-endian
		return BitConverter.IsLittleEndian
			? b[3] == 0xC0 && b[2] == 0xDE
			: b[0] == 0xC0 && b[1] == 0xDE
			;
	}

	/// <summary>
	/// The allocation mode of a structured id — the RFC 9562 variant nibble, whose two free low bits are
	/// <b>orthogonal</b>: <see cref="AllocationMode.Staging"/> selects the semantic registry and
	/// <see cref="AllocationMode.Generated"/> the provenance. It is never a flat four-value enum; see
	/// <see cref="AllocationMode"/> and model.md §8.
	/// </summary>
	public static unsafe AllocationMode GetAllocationMode(this Guid guid)
	{
		byte* b = (byte*)&guid;
		return (AllocationMode)(b[8] >> 4);
	}

	/// <summary>True when the id belongs to the committed (normative, firm) semantic registry.</summary>
	public static bool IsCommitted(this Guid guid) => (guid.GetAllocationMode() & AllocationMode.Staging) == 0;

	/// <summary>True when the id belongs to the staging (working, still-mutable) semantic registry.</summary>
	public static bool IsStaging(this Guid guid) => (guid.GetAllocationMode() & AllocationMode.Staging) != 0;

	/// <summary>True when the id was minted by a generator rather than hand-allocated in a registry.</summary>
	public static bool IsGenerated(this Guid guid) => (guid.GetAllocationMode() & AllocationMode.Generated) != 0;

	/// <summary>True when the id was pinned by hand in a registry rather than generated.</summary>
	public static bool IsPinned(this Guid guid) => (guid.GetAllocationMode() & AllocationMode.Generated) == 0;

	/// <summary>
	/// The 12-bit semantic class <c>Fnn</c> of a structured id: the semantic family <c>F</c> (high 4 bits)
	/// plus the code <c>nn</c> allocated inside that family <i>and</i> registry (low 8 bits). For a type id
	/// this is the type's own class; for an instance id it is the class of the type it instantiates.
	/// <para>
	/// The class alone does not identify a type — semantic identity is <c>registry + Fnn</c>, because the
	/// committed and staging registries allocate codes independently.
	/// </para>
	/// </summary>
	public static unsafe ushort GetSemanticClass(this Guid guid)
	{
		byte* b = (byte*)&guid;
		return (ushort)(((b[8] & 0x0F) << 8) | b[9]);
	}

	/// <summary>The semantic family nibble <c>F</c> of <see cref="GetSemanticClass"/>.</summary>
	public static unsafe byte GetSemanticFamily(this Guid guid)
	{
		byte* b = (byte*)&guid;
		return (byte)(b[8] & 0x0F);
	}

	/// <summary>The family-local semantic code <c>nn</c> of <see cref="GetSemanticClass"/>.</summary>
	public static unsafe byte GetSemanticCode(this Guid guid)
	{
		byte* b = (byte*)&guid;
		return b[9];
	}

	/// <summary>
	/// Deterministically derives the id of the <paramref name="ordinal"/>-th event a command expands to,
	/// carrying the <i>event's</i> semantic allocation rather than the command's.
	/// <para>
	/// The byte-level derivation legitimately differs by UUID version. When the command id is opaque
	/// (production: a v7 client id) there is nowhere to put a semantic class, so this is exactly
	/// <see cref="Derive(Guid, int)"/> — production event ids are bit-for-bit unchanged. When the command
	/// id is a <b>structured</b> <c>C0DE</c> v8 id the result is composed from three sources (model.md §8):
	/// the command instance supplies the company/scope prefix and the base node, the event <i>type</i>
	/// supplies the registry bit and the semantic class <c>Enn</c>, and this derivation supplies the
	/// <see cref="AllocationMode.Generated"/> bit. Only the 48-bit node advances by
	/// <paramref name="ordinal"/>, so a carry can never reach the mode or class.
	/// </para>
	/// <para>
	/// A structured command whose event type is <i>not</i> structured throws: emitting the command's own
	/// <c>Cnn</c> as if it were the event's class would be a false semantic claim.
	/// </para>
	/// </summary>
	public static unsafe Guid DeriveEventId(Guid commandId, Guid eventTypeId, int ordinal)
	{
		if (ordinal < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(ordinal));
		}
		if (!commandId.IsStructuredId())
		{
			return Derive(commandId, ordinal);
		}
		if (!eventTypeId.IsStructuredId())
		{
			throw new ArgumentException(
				$"A structured command id ({commandId}) cannot derive a structured event id from an unstructured event type id ({eventTypeId}): the result would carry the command's own semantic class."
				, nameof(eventTypeId)
			);
		}
		// registry bit from the event type, generated bit from this derivation; the command instance's
		// own mode is deliberately not consulted (model.md §8: generated projection preserves the
		// semantic allocation of the type, not of the thing it was derived from).
		var mode = AllocationMode.Variant | AllocationMode.Generated | (eventTypeId.GetAllocationMode() & AllocationMode.Staging);
		var eventClass = eventTypeId.GetSemanticClass();
		byte* b = (byte*)&commandId;
		b[8] = (byte)(((byte)mode << 4) | ((eventClass >> 8) & 0x0F));
		b[9] = (byte)eventClass;
		ulong node = 0;
		for (int i = 10; i < 16; i++)
		{
			node = (node << 8) | b[i];
		}
		node += (ulong)ordinal;
		if (node > 0xFFFFFFFFFFFFUL)
		{
			throw new ArgumentOutOfRangeException(nameof(ordinal), "Derived event node overflows the 48-bit instance space");
		}
		for (int i = 15; i >= 10; i--)
		{
			b[i] = (byte)node;
			node >>= 8;
		}
		return commandId;
	}

	public static Guid Create(int version)
	{
		switch (version)
		{
			case 1:
				return CreateVersion1();
			case 3:
				throw new NotSupportedException($"Use {nameof(CreateVersion3)}(...)");
			case 4:
				return CreateVersion4();
			case 5:
				throw new NotSupportedException($"Use {nameof(CreateVersion5)}(...)");
			case 6:
				return CreateVersion6();
			case 7:
				return CreateVersion7();
			default:
				throw new NotSupportedException();
		}
	}
}
