using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

using MusicBox.Services;

namespace MusicBox;

/// <summary>
/// 网易云原生扫码登录窗：申请 unikey → 渲染二维码 → 1.8 秒轮询。
/// 状态机（见抓包分析报告 4.2）：
/// 800 过期（遮罩 + 刷新） / 801 未扫 / 802 已扫待确认 / 803 成功（回调 cookie）。
/// 窗口关闭即停轮询；成功后由调用方保存 Cookie 并刷新 UI。
/// </summary>
public partial class QrLoginWindow : Window
{
	private readonly Action<string> _onSuccess;
	private CancellationTokenSource? _cts;
	private string _unikey = "";
	private bool _polling;
	private bool _succeeded;
	private bool _closed;

	public QrLoginWindow(Action<string> onSuccess)
	{
		InitializeComponent();
		_onSuccess = onSuccess;
		TitleText.Text = "登录网易云音乐";
		Loaded += (_, _) => _ = RestartAsync();
		Closed += (_, _) =>
		{
			_closed = true;
			_cts?.Cancel();
			_cts?.Dispose();
		};
	}

	private async Task RestartAsync()
	{
		_cts?.Cancel();
		_cts = new CancellationTokenSource();
		var ct = _cts.Token;
		_succeeded = false;
		_unikey = "";
		LoadingText.Visibility = Visibility.Visible;
		QrImage.Source = null;
		QrMask.Visibility = Visibility.Collapsed;
		StatusText.Text = "正在获取二维码…";

		var (ok, unikey, qrurl, error) = await NeteaseApiClient.LoginQrStartAsync(ct);
		if (_closed || ct.IsCancellationRequested)
		{
			return;
		}
		if (!ok || string.IsNullOrEmpty(unikey))
		{
			LoadingText.Text = "获取失败，请重试";
			StatusText.Text = string.IsNullOrEmpty(error) ? "网络异常，请检查网络后重试" : error;
			return;
		}
		LoadingText.Visibility = Visibility.Collapsed;
		_unikey = unikey;
		QrImage.Source = RenderQr(qrurl);
		StatusText.Text = "请使用网易云音乐 App 扫码登录";
		if (!_polling)
		{
			_polling = true;
			_ = PollLoopAsync(ct);
		}
	}

	/// <summary>轮询扫码状态：800 过期 / 801 等待 / 802 已扫 / 803 成功。</summary>
	private async Task PollLoopAsync(CancellationToken ct)
	{
		try
		{
			while (!_closed && !ct.IsCancellationRequested)
			{
				await Task.Delay(1800, ct);
				if (_closed || string.IsNullOrEmpty(_unikey) || _succeeded)
				{
					continue;
				}
				var (code, cookie, _) = await NeteaseApiClient.LoginQrPollAsync(_unikey, ct);
				if (_closed || ct.IsCancellationRequested)
				{
					return;
				}
				switch (code)
				{
					case 800:
						_succeeded = false;
						_unikey = "";
						QrMask.Visibility = Visibility.Visible;
						MaskText.Text = "二维码已过期";
						StatusText.Text = "二维码已过期，请刷新后重新扫码";
						break;
					case 802:
						StatusText.Text = "已扫码，请在手机上确认";
						break;
					case 803 when !string.IsNullOrEmpty(cookie):
						_succeeded = true;
						StatusText.Text = "登录成功";
						_onSuccess(cookie);
						_ = Dispatcher.BeginInvoke(new Action(Close));
						return;
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("扫码轮询异常: " + ex.Message);
		}
	}

	private static BitmapImage? RenderQr(string content)
	{
		try
		{
			using var generator = new QRCoder.QRCodeGenerator();
			using var data = generator.CreateQrCode(content, QRCoder.QRCodeGenerator.ECCLevel.M);
			var png = new QRCoder.PngByteQRCode(data).GetGraphic(4);
			var img = new BitmapImage();
			img.BeginInit();
			img.CacheOption = BitmapCacheOption.OnLoad;
			img.StreamSource = new MemoryStream(png);
			img.EndInit();
			img.Freeze();
			return img;
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("二维码渲染失败: " + ex.Message);
			return null;
		}
	}

	private void RefreshButton_Click(object sender, RoutedEventArgs e)
	{
		_ = RestartAsync();
	}
}
