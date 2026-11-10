using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MusicBox.Services;

namespace MusicBox.Settings;

/// <summary>settings.json 里 Cookie 的落盘保护。
///
/// 之前 Cookie 是明文写在 %LocalAppData%\MusicBox\settings.json 里的：任何能读这个
/// 文件的程序/同步盘都能直接拿去登录。这里改成用 Windows DPAPI 按「当前用户」加密，
/// 不需要额外的 NuGet 包（直接 P/Invoke crypt32.dll）。
///
/// 兼容策略：密文带 <c>enc:v1:</c> 前缀；没有前缀的一律按旧明文原样返回，
/// 所以已有用户升级后第一次读取仍然保持登录态，下一次保存才变成密文。
/// 代价是密文绑定当前 Windows 账号：换账号/换机器解不开，表现为该平台未登录，
/// 需要用户重新粘贴 Cookie。</summary>
internal static class CookieProtection
{
	public const string Prefix = "enc:v1:";

	private const int CryptProtectUiForbidden = 0x1;

	[StructLayout(LayoutKind.Sequential)]
	private struct DataBlob
	{
		public int Size;

		public IntPtr Data;
	}

	[DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CryptProtectData(
		ref DataBlob dataIn,
		string? description,
		IntPtr entropy,
		IntPtr reserved,
		IntPtr prompt,
		int flags,
		out DataBlob dataOut);

	[DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CryptUnprotectData(
		ref DataBlob dataIn,
		IntPtr description,
		IntPtr entropy,
		IntPtr reserved,
		IntPtr prompt,
		int flags,
		out DataBlob dataOut);

	[DllImport("kernel32.dll")]
	private static extern IntPtr LocalFree(IntPtr handle);

	/// <summary>明文 → <c>enc:v1:base64</c>。空串、已加密的值原样返回；
	/// 加密失败也返回明文——宁可这次没加密，也不能因为加密失败把用户的登录态弄丢。</summary>
	public static string Protect(string? plain)
	{
		if (string.IsNullOrEmpty(plain) || plain.StartsWith(Prefix, StringComparison.Ordinal))
		{
			return plain ?? "";
		}
		try
		{
			byte[]? encrypted = Transform(Encoding.UTF8.GetBytes(plain), protect: true);
			return encrypted == null ? plain : Prefix + Convert.ToBase64String(encrypted);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "Cookie 加密失败，已按明文保存");
			return plain;
		}
	}

	/// <summary>落盘值 → 明文。无前缀视为旧版明文；解密失败返回空串（该平台退化为未登录）。</summary>
	public static string Unprotect(string? stored)
	{
		if (string.IsNullOrEmpty(stored))
		{
			return "";
		}
		if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
		{
			return stored;
		}
		try
		{
			byte[]? plain = Transform(Convert.FromBase64String(stored[Prefix.Length..]), protect: false);
			return plain == null ? "" : Encoding.UTF8.GetString(plain);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "Cookie 解密失败（可能换了 Windows 账号/机器），该平台将显示未登录");
			return "";
		}
	}

	private static byte[]? Transform(byte[] input, bool protect)
	{
		GCHandle handle = GCHandle.Alloc(input, GCHandleType.Pinned);
		try
		{
			DataBlob inBlob = new DataBlob { Size = input.Length, Data = handle.AddrOfPinnedObject() };
			DataBlob outBlob;
			bool ok = protect
				? CryptProtectData(ref inBlob, "MusicBox", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob)
				: CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob);
			if (!ok || outBlob.Data == IntPtr.Zero)
			{
				return null;
			}
			try
			{
				byte[] result = new byte[outBlob.Size];
				Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
				return result;
			}
			finally
			{
				LocalFree(outBlob.Data);
			}
		}
		finally
		{
			handle.Free();
		}
	}
}

/// <summary>把 Cookie 属性读写都过一遍 <see cref="CookieProtection"/>：
/// 读到旧明文照常可用，写出去自动变成密文，平台客户端拿到的始终是明文。</summary>
internal sealed class ProtectedCookieConverter : JsonConverter<string>
{
	public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		return CookieProtection.Unprotect(reader.GetString());
	}

	public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
	{
		writer.WriteStringValue(CookieProtection.Protect(value));
	}
}
