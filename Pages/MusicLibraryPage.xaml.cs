using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MusicBox.Services;
using MusicBox.Settings;

namespace MusicBox.Pages;

public partial class MusicLibraryPage : UserControl, IMusicPage, IMusicPageLeave
{
	private bool _ready;

	private string _searchFilter = "";

	// 程序自己改 SearchBox.Text 时置位，避免 TextChanged 又把筛选同步回来。
	private bool _suppressFilterSync;

	// 列表是不是已经过期了。之前每次切到本页都无条件重建全部行，
	// 而播放/收藏状态变化早就通过 StateChanged 重建过了 ——
	// 于是每次点导航栏回来，39 首歌的行控件都被拆了重造一遍，
	// 在快速切换时这段同步开销全砸在 UI 线程上，手感就是「卡一下」。
	// 真正改了东西才重建，没改就直接用现成的。
	private bool _needRefresh = true;

	// 上次重画列表时的数据指纹，见 OnStateChanged 的说明。
	private string _lastSignature = "";

	public MusicLibraryPage()
	{
		InitializeComponent();
		MusicService.StateChanged += OnStateChanged;
		TrackRows.AccentChanged += OnAccentChanged;
		try
		{
			KeepFilterCheckBox.IsChecked = SettingsManager.Instance.LibraryKeepFilter;
		}
		catch
		{
		}
	}

	/// <summary>换强调色：行里抄的旧颜色要重画才能变过来。</summary>
	private void OnAccentChanged()
	{
		_needRefresh = true;
		if (IsLoaded)
		{
			RefreshLibraryList();
		}
	}

	public void OnNavigatedTo()
	{
		// _ready 以前只有「选择音乐文件夹」按钮里会置 true，
		// 于是正常启动进来它一直是 false，SearchBox_TextChanged 直接 return，
		// 搜索框看着能打字、其实什么都没发生。页面真正被显示出来就算初始化完成。
		_ready = true;
		RefreshFolderText();
		// 只有列表真的过期了才重建。切走时清掉筛选词会置脏，所以这条路径照样会重画。
		if (_needRefresh)
		{
			RefreshLibraryList();
		}
	}

	/// <summary>切离本页时按「保留关键词」的勾选决定清不清筛选词。
	/// 不勾：每次跳去别的页面就清掉，下次回来是全量列表（免得「看着只有 3 首、
	/// 其实是筛过的」这种困惑）。勾上：原样保留。
	/// 这里只改状态，不重建列表 —— 重建要跑一遍全部歌曲，
	/// 而列表在切走时本来就会盖住，没必要白花这个开销。</summary>
	public void OnNavigatedFrom()
	{
		if (KeepFilterCheckBox.IsChecked == true)
		{
			return;
		}
		if (string.IsNullOrEmpty(_searchFilter) && string.IsNullOrEmpty(SearchBox.Text))
		{
			return;
		}
		_searchFilter = "";
		// 先把标志摘掉再改 Text，否则 TextChanged 会顺手把筛选又应用一次。
		_suppressFilterSync = true;
		try
		{
			SearchBox.Text = "";
		}
		finally
		{
			_suppressFilterSync = false;
		}
		// 筛选变了，列表得重画。等回到本页时 OnNavigatedTo 会补上。
		_needRefresh = true;
	}

	private void KeepFilterCheckBox_Changed(object sender, RoutedEventArgs e)
	{
		// 勾选状态本身存在设置里，重开程序要记住用户的习惯。
		try
		{
			var s = SettingsManager.Instance;
			s.LibraryKeepFilter = KeepFilterCheckBox.IsChecked == true;
			SettingsManager.Save(s);
		}
		catch
		{
		}
	}

	private void OnStateChanged()
	{
		// 播放状态变了，但列表数据（曲库/收藏/跳过/筛选）未必变了。
		// 切一首歌 MusicService 会连着抛好几次 StateChanged（起播、缓冲、状态位），
		// 以前每一次都把 39 行拆了重造一遍 —— 全砸在 UI 线程上，
		// 这就是「换歌时程序未响应」的主因。
		// 行内的播放高亮由 TrackRows.SetPlayingTrack 单独更新，跟重建列表无关，
		// 所以这里只在列表数据真的变了时才重建。
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action(OnStateChanged));
			return;
		}
		if (ListDataSignature() == _lastSignature)
		{
			SyncPlayingSelection();
			return;
		}
		_needRefresh = true;
		// 页面不在前台就别重画：反正看不见，等切回来时 OnNavigatedTo 会补上。
		if (IsOnScreen())
		{
			RefreshLibraryList();
		}
	}

	/// <summary>决定列表要不要重画的那几个输入的指纹。
	/// 故意不含「当前播放哪首」—— 那一项只影响选中项和高亮，
	/// 用 SyncPlayingSelection 单独同步就够了，不需要重造所有行。</summary>
	private static string ListDataSignature()
	{
		var s = SettingsManager.Instance;
		int skipped = s.SkippedMusic?.Count ?? 0;
		// 这里只放「列表内容真的取决于它」的量。
		// 原来还带着 UserLibrary.RecentlyPlayed.Count，可这一页从头到尾
		// 没用过 RecentlyPlayed（列表只由 KnownMusic + 跳过 + 收藏决定），
		// 于是每播一首歌 RecentlyPlayed 就会变，签名跟着变，
		// 触发一次全量重建——为了一个页面上根本不显示的数据，把 39 行全造一遍。
		return string.Concat(
			MusicService.MusicFolder, "|",
			MusicService.KnownMusic.Count.ToString(), "|",
			UserLibrary.Favorites.Count.ToString(), "|",
			skipped.ToString());
	}

	/// <summary>页面是否正在前台显示。用主窗的当前页判断比 IsVisible 可靠：
	/// 窗口最小化时 IsVisible 是 false，但那一页其实就是当前页。</summary>
	private bool IsOnScreen()
	{
		return MainWindow.Instance?.IsCurrentPage(this) ?? false;
	}

	/// <summary>把选中项挪到正在播放的那首。只动选中，不重建任何行。</summary>
	private void SyncPlayingSelection()
	{
		string? current = MusicService.CurrentPath;
		foreach (object o in LibraryListBox.Items)
		{
			if (o is ListBoxItem item && item.Tag is string path
				&& string.Equals(path, current, StringComparison.OrdinalIgnoreCase))
			{
				if (!item.IsSelected)
				{
					LibraryListBox.SelectedItem = item;
					LibraryListBox.ScrollIntoView(item);
				}
				return;
			}
		}
	}

	private void RefreshFolderText()
	{
		FolderText.Text = MusicService.MusicFolder;
	}

	private void RefreshLibraryList()
	{
		if (!_ready && !IsLoaded)
		{
			return;
		}
		_needRefresh = false;
		_lastSignature = ListDataSignature();
		UiPerf.Measure("音乐库列表重建", () =>
		{
		LibraryListBox.Items.Clear();
		string filter = _searchFilter;
		// 搜 "CU" 要能命中 "C U Again"：歌名里的空格、下划线、连字符、
		// 各种括号和点都是「装饰」，不该挡在匹配中间。
		// 所以两边都先压成一个只由字母数字和汉字组成的串再比。
		string filterKey = NormalizeKey(filter);
		int shown = 0;
		foreach (string file in MusicService.KnownMusic)
		{
			string full = Path.GetFileName(file);
			string name = Path.GetFileNameWithoutExtension(file);
			// 自动过滤：输入即筛，大小写不敏感。
			// 拿带扩展名的全名去比，这样搜 "flac" 也能命中 "8D Sorry.flac"；
			// 只比无扩展名的话，扩展名就永远搜不到了。
			if (filterKey.Length > 0 && !Matches(full, filter, filterKey))
			{
				continue;
			}
			shown++;
			bool skipped = MusicService.IsSkipped(file);
			var saved = new SavedTrack { Type = "local", Title = name, Path = file };
			bool fav = UserLibrary.IsFavorite(saved);

			// 复用「最近播放 / 我的收藏」那套行控件和存法：
			// 都是 SavedTrack + TrackRows.BuildRow，Key 都是 "local:" + 路径，
			// 收藏判定、去重、心形状态自然就对得上了，不用再单独画一遍。
			string sub = skipped ? Loc.Default["lib_skipped"] : string.Empty;
			// 这里原来在行的「评论」位上挂了逐首歌词按钮（图标还专门改成歌词字形）。
			// 歌词现在是全局的桌面悬浮窗，跟着底部播放栏走就够了：
			// 每一行都挂一个按钮，39 首就是 39 个入口，用户根本不知道该点哪个，
			// 而真正想看歌词时人是坐在播放栏那儿的。
			Grid row = TrackRows.BuildRow(
				saved, sub,
				() => MusicService.Play(file),
				fav,
				() =>
				{
					UserLibrary.ToggleFavorite(saved);
					RefreshLibraryList();
				},
				highlight: filter.Length > 0 ? filter : null);

			// 外面还是套 ListBoxItem：选中高亮、右键菜单、双击都靠它拿 Tag
			var item = new ListBoxItem
			{
				Tag = file,
				Content = row,
				Padding = new Thickness(0)
			};
			if (string.Equals(MusicService.CurrentPath, file, StringComparison.OrdinalIgnoreCase))
			{
				item.IsSelected = true;
			}
			LibraryListBox.Items.Add(item);
		}
		CountText.Text = filter.Length > 0
			? $"筛选出 {shown} 首 · 共 {MusicService.KnownMusic.Count} 首"
			: $"音乐库 ({MusicService.KnownMusic.Count} 首)";
		if (shown == 0 && filter.Length > 0)
		{
			LibraryListBox.Items.Add(new TextBlock
			{
				Text = Loc.Default["lib_none"],
				FontSize = 14,
				Foreground = ThemeManager.Tertiary(),
				HorizontalAlignment = HorizontalAlignment.Center,
				Margin = new Thickness(0, 24, 0, 0)
			});
		}
		});
	}

	/// <summary>把字符串压成「只由字母数字和汉字组成」的匹配键。
	/// "C U Again (feat. X)" -&gt; "CUAGAINFEATX"，
	/// 于是搜 "cuag" 能命中、搜 "cu" 也能命中。</summary>
	private static string NormalizeKey(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return "";
		}
		var sb = new System.Text.StringBuilder(text.Length);
		foreach (char c in text)
		{
			if (char.IsLetterOrDigit(c))
			{
				sb.Append(char.ToLowerInvariant(c));
			}
		}
		return sb.ToString();
	}

	/// <summary>关键词是否命中这个文件名。
	/// 两条路都算命中：
	/// 1) 原样包含 —— 保留原来「搜 flac 能搜到 .flac」的行为；
	/// 2) 压掉分隔符后包含 —— 让 "cu" 能命中 "C U Again"。
	/// 第 2 条在原样匹配失败时才试，所以不会把行为弄反。</summary>
	private static bool Matches(string fileName, string filter, string filterKey)
	{
		if (filter.Length > 0 && fileName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return true;
		}
		if (filterKey.Length == 0)
		{
			return false;
		}
		return NormalizeKey(fileName).Contains(filterKey, StringComparison.Ordinal);
	}

	private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (!_ready || _suppressFilterSync)
		{
			return;
		}
		_searchFilter = SearchBox.Text?.Trim() ?? "";
		_needRefresh = true;
		RefreshLibraryList();
	}

	private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
	{
		var dialog = new Microsoft.Win32.OpenFolderDialog
		{
			Title = "选择音乐文件夹",
			InitialDirectory = Directory.Exists(MusicService.MusicFolder)
				? MusicService.MusicFolder
				: Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
		};
		if (dialog.ShowDialog() == true)
		{
			var s = SettingsManager.Instance;
			s.MusicFolder = dialog.FolderName;
			SettingsManager.Save(s);
			_ready = true;
			RefreshFolderText();
			MusicService.ReloadLibrary();
		}
	}

	private void RefreshButton_Click(object sender, RoutedEventArgs e)
	{
		MusicService.ReloadLibrary();
	}

	private void LibraryListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (LibraryListBox.SelectedItem is ListBoxItem { Tag: string path })
		{
			MusicService.Play(path);
		}
	}

	private void LibraryListBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
	{
		if (LibraryListBox.SelectedItem == null)
		{
			e.Handled = true;
		}
	}

	private void ContextPlay_Click(object sender, RoutedEventArgs e)
	{
		if (LibraryListBox.SelectedItem is ListBoxItem { Tag: string path })
		{
			MusicService.Play(path);
		}
	}

	private void ContextToggleSkip_Click(object sender, RoutedEventArgs e)
	{
		if (LibraryListBox.SelectedItem is ListBoxItem { Tag: string path })
		{
			MusicService.ToggleSkip(path);
		}
	}

}
