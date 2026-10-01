using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MusicBox;
using MusicBox.Services;
using MusicBox.Settings;

namespace MusicBox.Pages;

public partial class FavoritesPage : UserControl, IMusicPage
{
	// 列表是否过期，见 MainWindow.NavigateTo 的注释：导航回来时没变就别重建。
	private bool _needRefresh = true;

	public FavoritesPage()
	{
		InitializeComponent();
		UserLibrary.Changed += OnLibraryChanged;
		TrackRows.AccentChanged += OnAccentChanged;
	}

	/// <summary>换强调色：行里抄的旧颜色要重画才能变过来。</summary>
	private void OnAccentChanged()
	{
		_needRefresh = true;
		// 只有这一页真的在前台才画，强调色广播时隐藏页不用跟着重画。
		if (IsLoaded && IsOnScreen())
		{
			RefreshList();
		}
	}

	/// <summary>页面是不是当前显示的那一页。</summary>
	private bool IsOnScreen()
	{
		return MainWindow.Instance?.IsCurrentPage(this) ?? false;
	}

	public void OnNavigatedTo()
	{
		SyncButton.Content = Loc.Default["fav_sync_button"];
		// 数据没变就别重建整张列表 —— 每点一次导航栏回来就把所有行拆了重造，
		// 快速切换时这段同步开销全砸在 UI 线程上。Changed 事件已经把列表刷过了。
		if (_needRefresh)
		{
			RefreshList();
		}
	}

	private void OnLibraryChanged()
	{
		_needRefresh = true;
		// 隐藏时不重建。收藏变动在播放、切歌、导入时都会触发，
		// 用户不在这一页时重建整页控件纯属白干。
		// _needRefresh 已经置位，OnNavigatedTo 会补上。
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action(RefreshIfOnScreen));
			return;
		}
		RefreshIfOnScreen();
	}

	private void RefreshIfOnScreen()
	{
		if (IsOnScreen())
		{
			RefreshList();
		}
	}

	private void RefreshList()
	{
		_needRefresh = false;
		var items = UserLibrary.Favorites;
		EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		FavList.Items.Clear();
		foreach (SavedTrack t in items)
		{
			var copy = t;
			// 收藏列表里不再放 ×：已经是收藏了，卡片上再挂个叉既重复又占地方。
			// 想取消收藏就把行上的心形点一下（下面 onToggleFav），取消后这行自然就没了。
			// 走 BuildSavedRow 是为了带上平台标签：收藏里本地下载的和各平台在线的混在一起，
			// 没有标签根本分不出哪首是哪个平台的。
			FavList.Items.Add(TrackRows.BuildSavedRow(copy, () => Play(copy), true, () => UserLibrary.ToggleFavorite(copy), null));
		}
		// 计数挪到 SyncButton 旁边的提示位，标题底下不再挂说明
		SyncButton.Content = items.Count > 0 ? $"从云端同步 · {items.Count}" : "从云端同步";
	}

	private static void Play(SavedTrack t)
	{
		MusicService.PlayFavorite(t, err =>
		{
			if (!string.IsNullOrEmpty(err))
			{
				Application.Current.MainWindow?.Dispatcher.BeginInvoke(new Action(() =>
				{
					if (Application.Current.MainWindow is MainWindow mw)
					{
						mw.ShowToast(err, false);
					}
				}));
			}
		});
	}

	private bool _syncing;

	private void SyncButton_Click(object sender, RoutedEventArgs e)
	{
		if (_syncing) return;
		_ = SyncFromCloudAsync();
	}

	private async Task SyncFromCloudAsync()
	{
		var s = SettingsManager.Instance;
		bool hasNetease = !string.IsNullOrWhiteSpace(s.NeteaseCookie);
		bool hasBili = !string.IsNullOrWhiteSpace(s.BiliCookie);
		bool hasQq = QqClient.HasCookie;
		if (!hasNetease && !hasBili && !hasQq)
		{
			SetStatus(Loc.Default["fav_sync_none"]);
			return;
		}
		_syncing = true;
		SyncButton.IsEnabled = false;
		SetStatus(Loc.Default["fav_sync_running"]);
		int neteaseCount = 0;
		int biliCount = 0;
		int qqCount = 0;
		var tasks = new List<Task>();
		if (hasNetease)
		{
			tasks.Add(Task.Run(async () =>
			{
				try
				{
					var songs = await NeteaseApiClient.FetchMySongsAsync(s.NeteaseCookie!);
					if (songs == null)
					{
						_ = Dispatcher.BeginInvoke(new Action(() => AppendStatus(" · " + Loc.Default["fav_sync_netease_no_cookie"])));
						return;
					}
					var adds = songs.Select(t => new SavedTrack
					{
						Type = "netease",
						Id = t.Id.ToString(),
						Title = t.Name,
						Artist = t.ArtistNames,
						DurationMs = t.DurationMs,
						Cover = t.Album?.PicUrl ?? ""
					}).ToList();
					neteaseCount = UserLibrary.MergeFavorites(adds);
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "网易云同步收藏失败");
				}
			}));
		}
		if (hasBili)
		{
			tasks.Add(Task.Run(async () =>
			{
				try
				{
					var fav = await BiliClient.FetchUserFavoritesAsync(30);
					if (fav == null)
					{
						_ = Dispatcher.BeginInvoke(new Action(() => AppendStatus(" · " + Loc.Default["fav_sync_bili_no_cookie"])));
						return;
					}
					var adds = fav.Value.tracks.Select(b => new SavedTrack
					{
						Type = "bili",
						Id = b.Bvid,
						Title = b.Display,
						Artist = b.Author,
						DurationMs = (long)b.Duration.TotalMilliseconds,
						Cover = b.Pic
					}).ToList();
					biliCount = UserLibrary.MergeFavorites(adds);
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "B站同步收藏失败");
				}
			}));
		}
		if (hasQq)
		{
			tasks.Add(Task.Run(async () =>
			{
				try
				{
					var qq = await QqClient.FetchFavoritesAsync(30);
					if (qq == null)
					{
						_ = Dispatcher.BeginInvoke(new Action(() => AppendStatus(" · " + Loc.Default["fav_sync_qq_unsupported"])));
						return;
					}
					qqCount = UserLibrary.MergeFavorites(qq);
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "QQ同步收藏失败");
				}
			}));
		}
		try
		{
			await Task.WhenAll(tasks);
		}
		catch
		{
		}
		_syncing = false;
		SyncButton.IsEnabled = true;
		int total = neteaseCount + biliCount + qqCount;
		if (total == 0 && !hasNetease && !hasBili && !hasQq)
		{
			SetStatus(Loc.Default["fav_sync_none"]);
		}
		else if (total == 0)
		{
			SetStatus(Loc.Default["fav_sync_failed"]);
		}
		else
		{
			SetStatus(Loc.Default.Format("fav_sync_result", total, neteaseCount, biliCount, qqCount));
		}
	}

	private void SetStatus(string text)
	{
		StatusText.Text = text;
	}

	private void AppendStatus(string text)
	{
		StatusText.Text = (StatusText.Text ?? "") + text;
	}
}