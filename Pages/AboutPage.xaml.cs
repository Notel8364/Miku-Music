using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using MusicBox.Services;

namespace MusicBox.Pages;

public partial class AboutPage : UserControl, IMusicPage
{
	private static readonly (string Name, string License, string Desc, string Url)[] Credits =
	{
		("BBDown", "MIT", "B 站视频/音频下载核心工具", "https://github.com/nilaoda/BBDown"),
		("NAudio", "MIT", "音频播放与解码", "https://github.com/naudio/NAudio"),
		("NeteaseCloudMusicApi", "MIT", "网易云音乐接口参考（Binaryify）", "https://github.com/Binaryify/NeteaseCloudMusicApi"),
		("KuGouMusicApi", "MIT", "酷狗音乐接口参考（MakcRe）", "https://github.com/MakcRe/KuGouMusicApi"),
		("lx-music 洛雪音乐", "Apache-2.0", "多音源在线音乐的界面与交互参考", "https://github.com/lxmusics/lx-music-desktop"),
		(".NET / WPF", "MIT", "应用程序运行框架", "https://github.com/dotnet/wpf"),
	};

	public AboutPage()
	{
		InitializeComponent();
		TryLoadIcon();
		BuildCredits();
	}

	public void OnNavigatedTo()
	{
		// 版本号从程序集读，XAML 里那个写死的值只是给设计器看的兜底。
		// 真正的版本号只有 csproj 的 <Version> 一个来源，
		// 关于页不自己存一份，免得两处对不上。
		try
		{
			var asm = System.Reflection.Assembly.GetExecutingAssembly();
			var attrs = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
			string? v = (attrs.Length > 0 ? attrs[0] as System.Reflection.AssemblyInformationalVersionAttribute : null)?.InformationalVersion
				?? asm.GetName().Version?.ToString(3);
			if (!string.IsNullOrWhiteSpace(v))
			{
				// InformationalVersion 常带 "+<commit>"，只留前三段
				int plus = v.IndexOf('+');
				VersionText.Text = "v" + (plus > 0 ? v[..plus] : v);
			}
		}
		catch
		{
		}
	}

	private void BuildCredits()
	{
		CreditsPanel.Children.Clear();
		foreach (var c in Credits)
		{
			var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
			var header = new StackPanel { Orientation = Orientation.Horizontal };
			header.Children.Add(new TextBlock
			{
				Text = c.Name,
				FontSize = 13.5,
				FontWeight = FontWeights.SemiBold,
				Foreground = ThemeManager.Primary()
			});
			header.Children.Add(new Border
			{
				Background = new SolidColorBrush(Color.FromArgb(0x1F, 0x3C, 0x8C, 0xFF)),
				CornerRadius = new CornerRadius(4),
				Padding = new Thickness(6, 1, 6, 1),
				Margin = new Thickness(8, 0, 0, 0),
				VerticalAlignment = VerticalAlignment.Center,
				Child = new TextBlock
				{
					Text = c.License,
					FontSize = 11,
					Foreground = ThemeManager.Accent()
				}
			});
			panel.Children.Add(header);
			panel.Children.Add(new TextBlock
			{
				Text = c.Desc,
				FontSize = 12.5,
				Foreground = ThemeManager.Secondary(),
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 3, 0, 0)
			});
			var link = new Hyperlink(new Run(c.Url))
			{
				FontSize = 12,
				Foreground = ThemeManager.Accent(),
				ToolTip = c.Url
			};
			link.RequestNavigate += OnLinkNavigate;
			panel.Children.Add(new TextBlock { Inlines = { link }, Margin = new Thickness(0, 2, 0, 0) });
			CreditsPanel.Children.Add(panel);
		}
	}

	private void OnLinkNavigate(object sender, RequestNavigateEventArgs e)
	{
		try
		{
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
			{
				FileName = e.Uri.AbsoluteUri,
				UseShellExecute = true
			});
			e.Handled = true;
		}
		catch
		{
		}
	}

	private void TryLoadIcon()
	{
		try
		{
			string path = Path.Combine(AppContext.BaseDirectory, "Assets", "app_icon.png");
			if (File.Exists(path))
			{
				var bi = new BitmapImage();
				bi.BeginInit();
				bi.UriSource = new Uri(path);
				// 高 DPI 缩放大图时用高质量缩放，只限宽不限高，保持原始宽高比不被压缩
				bi.DecodePixelWidth = 152;
				bi.CacheOption = BitmapCacheOption.OnLoad;
				bi.EndInit();
				bi.Freeze();
				AppIcon.Source = bi;
			}
		}
		catch
		{
		}
	}
}
