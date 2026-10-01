using System;
using System.Windows;
using System.Windows.Controls;
using MusicBox;
using MusicBox.Services;
using MusicBox.Settings;

namespace MusicBox.Pages;

public partial class RecentPage : UserControl, IMusicPage
{
	// 列表是否过期，见 MainWindow.NavigateTo 的注释：导航回来时没变就别重建。
	private bool _needRefresh = true;

	public RecentPage()
	{
		InitializeComponent();
		UserLibrary.Changed += OnLibraryChanged;
		TrackRows.AccentChanged += OnAccentChanged;
	}

	/// <summary>换强调色：行里抄的旧颜色要重画才能变过来。</summary>
	private void OnAccentChanged()
	{
		_needRefresh = true;
		// 只有这一页真的在前台才画。强调色一改会广播给所有订阅者，
		// 隐藏的页面跟着重画一遍纯属浪费。
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
		// 隐藏时不重建。RecentlyPlayed 每次播放都会变，触发的是全量重画；
		// 用户在音乐库页听歌时，这页重建了几百行控件却一个都看不见。
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
		var items = UserLibrary.RecentlyPlayed;
		ClearRecentButton.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		RecentList.Items.Clear();
		foreach (SavedTrack t in items)
		{
			var copy = t;
			// 最近播放的行上补一个心形：点一下就进「我的收藏」。
			// 收藏状态按 UserLibrary.IsFavorite 现取，所以取消后行上的心形会自动变回空心。
			bool fav = UserLibrary.IsFavorite(copy);
			// 同样走 BuildSavedRow 带平台标签，和搜索页/收藏页保持一致
			RecentList.Items.Add(TrackRows.BuildSavedRow(copy, () => Play(copy), fav, () => UserLibrary.ToggleFavorite(copy), null));
		}
		// 条数改放进右上角的状态行，标题底下不再挂一句说明
		StatusText.Text = items.Count > 0 ? $"共 {items.Count} 首（仅保存在本机）" : "";
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

	private void ClearRecentButton_Click(object sender, RoutedEventArgs e)
	{
		UserLibrary.ClearRecent();
	}
}