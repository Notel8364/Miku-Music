using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MusicBox.Services;

/// <summary>
/// 搜索结果封面加载：按图片来源设置 Referer（B站/网易云/Kugou/QQ 图片 CDN 大多有防盗链），
/// 异步下载到内存、解码 128px 并缓存；同一 URL 只拉一次。
/// </summary>
public static class CoverLoader
{
	private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

	private static readonly ConcurrentDictionary<string, ImageSource> Cache = new();

	static CoverLoader()
	{
		Http.DefaultRequestHeaders.UserAgent.ParseAdd(
			"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
	}

	public static void LoadAsync(Image img, string url)
	{
		if (img == null)
		{
			return;
		}
		string? fixedUrl = NormalizeUrl(url);
		if (string.IsNullOrWhiteSpace(fixedUrl))
		{
			return;
		}
		url = fixedUrl;
		if (Cache.TryGetValue(url, out ImageSource? cached))
		{
			img.Source = cached;
			return;
		}
		_ = FetchAsync(img, url);
	}

	/// <summary>归一化图片 URL：补全 // 协议、修复缺失/错误的 scheme；非法地址返回 null。</summary>
	private static string? NormalizeUrl(string url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return null;
		}
		string u = url.Trim();
		if (u.StartsWith("//", StringComparison.Ordinal))
		{
			u = "https:" + u;
		}
		else if (u.StartsWith("/", StringComparison.Ordinal))
		{
			return null;
		}
		else if (!Uri.TryCreate(u, UriKind.Absolute, out _))
		{
			return null;
		}
		if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
		{
			u = "https://" + u.Substring("http://".Length);
		}
		return u;
	}

	private static async Task FetchAsync(Image img, string url)
	{
		try
		{
			using var req = new HttpRequestMessage(HttpMethod.Get, url);
			req.Headers.Referrer = new Uri(RefererFor(url));
			using var resp = await Http.SendAsync(req);
			resp.EnsureSuccessStatusCode();
			byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
			ImageSource? src = TryDecode(bytes);
			if (src == null)
			{
				return;
			}
			Cache.TryAdd(url, src);
			var d = Application.Current?.Dispatcher;
			if (d != null && !d.HasShutdownStarted)
			{
				_ = d.BeginInvoke(new Action(() =>
				{
					if (img != null)
					{
						img.Source = src;
					}
				}));
			}
			else if (img != null)
			{
				img.Source = src;
			}
		}
		catch
		{
		}
	}

	private static string RefererFor(string url)
	{
		try
		{
			string host = new Uri(url).Host;
			if (host.Contains("bilibili") || host.Contains("hdslb")) return "https://www.bilibili.com/";
			if (host.Contains("126.net")) return "https://music.163.com/";
			if (host.Contains("kugou")) return "https://www.kugou.com/";
			if (host.Contains("gtimg")) return "https://y.qq.com/";
		}
		catch
		{
		}
		return "";
	}

	private static ImageSource? TryDecode(byte[] bytes)
	{
		try
		{
			var bi = new BitmapImage();
			bi.BeginInit();
			bi.CacheOption = BitmapCacheOption.OnLoad;
			bi.DecodePixelWidth = 128;
			using var ms = new MemoryStream(bytes);
			bi.StreamSource = ms;
			bi.EndInit();
			bi.Freeze();
			return bi;
		}
		catch
		{
			return null;
		}
	}
}