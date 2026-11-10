using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

using MusicBox.Services;

namespace MusicBox;

/// <summary>
/// 酷狗扫码登录弹窗：签发二维码 → 轮询状态 → 成功落盘。
/// 轮询在后台跑（1.5s 一次，约 90s 超时），窗口关闭即取消；
/// 「二维码过期（status=0）」时给出刷新按钮，不用重开弹窗。
/// </summary>
public partial class KugouQrLoginWindow : Window
{
	private CancellationTokenSource? _cts;

	public KugouQrLoginWindow()
	{
		InitializeComponent();
		Loaded += (_, _) => _ = RunAsync();
		Closed += (_, _) => _cts?.Cancel();
	}

	private async Task RunAsync()
	{
		_cts?.Cancel();
		_cts = new CancellationTokenSource();
		CancellationToken ct = _cts.Token;
		try
		{
			RetryButton.Visibility = Visibility.Collapsed;
			StateText.Text = "正在获取二维码…";
			string? key = await KugouClient.LoginQrStartAsync(ct);
			if (string.IsNullOrEmpty(key))
			{
				StateText.Text = "二维码获取失败，请检查网络后重试";
				RetryButton.Visibility = Visibility.Visible;
				return;
			}
			ShowQr(KugouClient.QrLoginContent(key));
			StateText.Text = "打开酷狗音乐 App，扫一扫登录";
			// 轮询：状态 1=等待 2=待确认 4=成功 0=过期。约 90s 没结果就停。
			for (int i = 0; i < 60 && !ct.IsCancellationRequested; i++)
			{
				await Task.Delay(1500, ct);
				var poll = await KugouClient.LoginQrPollAsync(key, ct);
				switch (poll.Status)
				{
					case 4:
						if (poll.Token != null && poll.Userid != null)
						{
							KugouClient.SaveQrSession(poll.Userid, poll.Token, poll.Nickname);
							StateText.Text = "登录成功，收藏夹已可用";
							await Task.Delay(600, ct);
							DialogResult = true;
							return;
						}
						StateText.Text = "登录响应异常，请刷新重试";
						RetryButton.Visibility = Visibility.Visible;
						return;
					case 0:
						StateText.Text = "二维码已过期";
						RetryButton.Visibility = Visibility.Visible;
						return;
					case 2:
						StateText.Text = "已扫码，请在手机上确认";
						break;
					case 1:
					default:
						if (poll.Status < 0)
						{
							StateText.Text = "网络异常，仍在重试…";
						}
						break;
				}
			}
			if (!ct.IsCancellationRequested)
			{
				StateText.Text = "等待超时，请刷新二维码";
				RetryButton.Visibility = Visibility.Visible;
			}
		}
		catch (OperationCanceledException)
		{
			// 窗口关闭导致的中断，正常路径
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "酷狗扫码登录流程失败");
			StateText.Text = "登录流程异常，请重试";
			RetryButton.Visibility = Visibility.Visible;
		}
	}

	private void ShowQr(string content)
	{
		using var gen = new QRCoder.QRCodeGenerator();
		var data = gen.CreateQrCode(content, QRCoder.QRCodeGenerator.ECCLevel.M);
		byte[] png = new QRCoder.PngByteQRCode(data).GetGraphic(6);
		var img = new BitmapImage();
		img.BeginInit();
		img.StreamSource = new MemoryStream(png);
		img.CacheOption = BitmapCacheOption.OnLoad;
		img.EndInit();
		img.Freeze();
		QrImage.Source = img;
	}

	private void RetryButton_Click(object sender, RoutedEventArgs e)
	{
		_ = RunAsync();
	}
}
