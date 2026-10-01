using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using MusicBox.Services;

namespace MusicBox;

/// <summary>通用内置浏览器登录：用 WebView2（Chromium）打开平台登录页，登录后提取 Cookie 回写。
/// 若本机未装 WebView2 Runtime，可一键下载安装；也支持把 Fixed Version Runtime 放进
/// 程序目录 <see cref="RuntimeWebView2FolderName"/> 以自包含方式调用。</summary>
public partial class WebLoginWindow : Window
{
	private readonly string _startUrl;
	private readonly string _cookieRoot;
	private readonly string _caption;
	private readonly Action<string> _onCookie;

	/// <summary>Evergreen 在线安装器下载地址（微软官方 fwlink）。</summary>
	private const string RuntimeBootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

	/// <summary>可选自包含 Runtime 目录名：{程序目录}\Runtime\WebView2\msedgewebview2.exe</summary>
	private const string RuntimeWebView2FolderName = "Runtime\\WebView2";

	public WebLoginWindow(string startUrl, string cookieRoot, string caption, Action<string> onCookie)
	{
		InitializeComponent();
		_startUrl = startUrl;
		_cookieRoot = cookieRoot;
		_caption = caption;
		_onCookie = onCookie;
		Title = caption + " 登录";
		HintText.Text = "在下方" + caption + "网页登录，成功后点「提取登录Cookie」即可保存回本程序：";
	}

	private async void Window_Loaded(object sender, RoutedEventArgs e)
	{
		try
		{
			await InitAsync();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "WebView2 初始化失败: " + _caption);
			bool missingRuntime = ex is WebView2RuntimeNotFoundException;
			SetStatus(
				(missingRuntime ? "本机未安装 WebView2 Runtime。" : "内置浏览器初始化失败。")
				+ " 可点上方「下载并安装 WebView2」自动安装后重试；"
				+ " 或点「改用系统浏览器登录」在外置浏览器登录后回填 Cookie。"
				+ "\n错误：" + ex.Message, true);
			MessageBox.Show(
				(missingRuntime ? "本机未安装或异常了 WebView2 Runtime。\n\n"
					: "内置浏览器初始化失败。\n\n")
				+ "两个办法：\n"
				+ "1) 点本窗口上方绿色「下载并安装 WebView2」自动下载安装后自动重试；\n"
				+ "2) 点「改用系统浏览器登录」在默认浏览器完成登录，再把 Cookie 粘贴进设置保存。\n\n"
				+ "（也可到微软官方页下载并安装 “Microsoft Edge WebView2 Runtime”：\nhttps://developer.microsoft.com/microsoft-edge/webview2/ ）\n\n"
				+ "错误详情：\n" + ex.Message,
				_caption + " 登录", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	/// <summary>探测可选的自包含 Runtime 目录（{程序目录}\Runtime\WebView2）。存在则优先用它。</summary>
	private static string? FindBundledRuntime()
	{
		string[] bases =
		{
			AppContext.BaseDirectory,
			string.IsNullOrEmpty(Environment.ProcessPath) ? "" : (Path.GetDirectoryName(Environment.ProcessPath) ?? "")
		};
		foreach (string dir in bases)
		{
			if (string.IsNullOrWhiteSpace(dir))
			{
				continue;
			}
			string candidate = Path.Combine(dir, RuntimeWebView2FolderName, "msedgewebview2.exe");
			if (File.Exists(candidate))
			{
				string? exeDir = Path.GetDirectoryName(candidate);
				if (!string.IsNullOrWhiteSpace(exeDir))
				{
					return exeDir;
				}
			}
		}
		return null;
	}

	private async Task InitAsync()
	{
		string? bundle = FindBundledRuntime();
		if (bundle != null)
		{
			// 自包含 Fixed Version：直接指向本地 Runtime，不依赖系统安装。
			CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: bundle);
			await Web.EnsureCoreWebView2Async(env);
		}
		else
		{
			await Web.EnsureCoreWebView2Async();
		}
		if (Uri.TryCreate(_startUrl, UriKind.Absolute, out _))
		{
			Web.Source = new Uri(_startUrl);
		}
	}

	private void SetStatus(string text, bool isWarning)
	{
		StatusText.Text = text;
		StatusText.Visibility = Visibility.Visible;
		StatusText.Background = isWarning
			? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xF4, 0xD6))
			: new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xF6, 0xEC));
		StatusText.Foreground = isWarning
			? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7a, 0x5b, 0x00))
			: new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2e, 0x7d, 0x4f));
	}

	private async void DownloadRuntimeBtn_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			DownloadRuntimeBtn.IsEnabled = false;
			SetStatus("正在下载 WebView2 Runtime 安装器…", false);
			string exe = Path.Combine(Path.GetTempPath(), "WebView2RuntimeBootstrapper_" + Guid.NewGuid().ToString("N") + ".exe");
			// HttpClient 下载并写出到临时文件
			using (HttpClient http = new HttpClient())
			using (HttpResponseMessage resp = await http.GetAsync(RuntimeBootstrapperUrl, HttpCompletionOption.ResponseHeadersRead))
			{
				resp.EnsureSuccessStatusCode();
				using (FileStream fs = new FileStream(exe, FileMode.Create, FileAccess.Write, FileShare.None))
				{
					await resp.Content.CopyToAsync(fs);
				}
			}
			SetStatus("安装器已下载，正在静默安装 WebView2 Runtime…（若弹出授权框请点“是”）", false);
			// 提权静默安装，等它退出后再重试内嵌浏览器
			await Task.Run(() =>
			{
				try
				{
					System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(exe)
					{
						UseShellExecute = true,
						Verb = "runas",
						Arguments = "/silent /install"
					};
					using (System.Diagnostics.Process? p = System.Diagnostics.Process.Start(psi))
					{
						p?.WaitForExit();
					}
				}
				catch
				{
					// 用户取消提权 / 启动失败：交给下方 InitAsync 的 catch 提示
				}
			});

			SetStatus("正在重试内嵌浏览器…", false);
			await InitAsync();
			DownloadRuntimeBtn.IsEnabled = true;
			SetStatus("WebView2 已就绪，请继续在窗口内登录。", false);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "下载/安装 WebView2 Runtime 失败");
			DownloadRuntimeBtn.IsEnabled = true;
			SetStatus("下载/安装 WebView2 失败：" + ex.Message, true);
		}
	}

	private void ExternalBtn_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_startUrl) { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			MessageBox.Show("打开外部浏览器失败：" + ex.Message, _caption + " 登录", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private void ExtractBtn_Click(object sender, RoutedEventArgs e)
	{
		string cookie = QqClient.ReadSiteCookies(_cookieRoot);
		if (string.IsNullOrWhiteSpace(cookie))
		{
			MessageBox.Show("未获取到登录 Cookie。请确认已在窗口内完成登录后重试，或改用「手动粘贴 Cookie」。", _caption + " 登录", MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}
		try
		{
			_onCookie(cookie);
			MessageBox.Show("已提取并保存 " + _caption + " 登录 Cookie。", _caption + " 登录", MessageBoxButton.OK, MessageBoxImage.Information);
			DialogResult = true;
			Close();
		}
		catch (Exception ex)
		{
			MessageBox.Show("保存失败：" + ex.Message, _caption + " 登录", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}
}