using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MusicBox.Services;
using static MusicBox.Pages.OnlineMusicPage;

namespace MusicBox.Views;

/// <summary>第三界面：评论抽屉。
///
/// 原来评论是搜索页右半边常驻的一栏（OnlineMusicPage 里的 CommentPanel），
/// 用户反馈那条分栏把音乐列表挤窄了、还疑似引入了滚轮失灵；按新需求整块
/// 搬到这里，变成贴窗口右缘、带阴影的覆盖面板：程序内点行内评论键打开，
/// 既盖得住搜索页右半边，也盖得住主内容区。
///
/// 搬迁原则：评论的加载 / 渲染 / 展开回复 / 分页 / 发表逻辑原样照搬，
/// 只把宿主从「页面的一栏」换成「这个抽屉」，行为以旧版为准。</summary>
public partial class CommentsPanel : UserControl
{
	// 评论区请求令牌：防止连点两首歌时旧请求后到、覆盖新歌的评论
	private int _commentToken;
	// 当前正在看评论的曲目，发表评论时要用
	private object? _commentTarget;
	// 下一页游标，空串表示第一页
	private string _commentCursor = "";
	// 本轮是否已经到底 / 正在取，避免触底反复发请求
	private bool _commentMoreDone;

	/// <summary>开合动画的代次。滑出动画播到一半又点开时，旧动画的
	/// Completed 还会来一发，不挡住它会把刚打开的面板重新收回去。</summary>
	private int _animGen;

	public CommentsPanel()
	{
		InitializeComponent();
		// 切界面语言后重刷代码赋值的字（标题、加载更多提示）。
		// XAML 里那些 Binding 会自己变，`text.Text = Loc.Default[...]` 不会。
		Loc.Default.LanguageChanged += OnLanguageChanged;
		RefreshCommentHeader();
	}

	/// <summary>面板是不是开着（含滑出动画进行中）。</summary>
	internal bool IsOpen => Visibility == Visibility.Visible;

	/// <summary>行内评论键的入口。同一首歌已经开着列表就收起（回到空态，
	/// 旧版常驻栏的语义）；面板整个被关着时同一首歌也要重新打开，
	/// 不能命中收起分支 —— Esc 刚把它收走，再点评论键应是重开。</summary>
	internal void Toggle(object item)
	{
		if (Visibility != Visibility.Visible)
		{
			OpenCommentsCore(item);
			ShowAnimated();
			return;
		}
		if (CommentBodyHost.Visibility == Visibility.Visible && SameTrack(_commentTarget, item))
		{
			// 收起列表本体、标题回默认，面板本身留着（显示空态提示）。
			// 关掉整块面板走关闭键 / Esc / 再点同一行不算。
			CollapseCommentList();
			CommentHeaderText.Text = Loc.Default["cm_title"];
			CommentLoginHint.Visibility = Visibility.Collapsed;
			SetCommentStatus("");
			CommentPlaceholderText.Visibility = Visibility.Visible;
			return;
		}
		OpenCommentsCore(item);
	}

	/// <summary>关掉整块面板（关闭键、Esc、切页）。</summary>
	internal void Close()
	{
		CloseAnimated();
	}

	private void CommentCloseButton_Click(object sender, RoutedEventArgs e)
	{
		CloseAnimated();
	}

	/// <summary>滑入 + 淡入。位移只有 44px：整块从屏外滑进来会拖着阴影
	/// 扫过全屏页，闪得厉害；短行程 + 淡入既有「从右侧过来」的方向感，
	/// 又不会喧宾夺主。</summary>
	private void ShowAnimated()
	{
		int gen = ++_animGen;
		try
		{
			Root.BeginAnimation(UIElement.OpacityProperty, null);
			SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
			Visibility = Visibility.Visible;
			SlideTransform.X = 44;
			Root.Opacity = 0;
			var slide = new DoubleAnimation(44, 0, new Duration(TimeSpan.FromMilliseconds(190)))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
			};
			var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(190)));
			slide.Completed += (_, _) =>
			{
				if (gen != _animGen) return;
				SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
				Root.BeginAnimation(UIElement.OpacityProperty, null);
				SlideTransform.X = 0;
				Root.Opacity = 1;
			};
			SlideTransform.BeginAnimation(TranslateTransform.XProperty, slide);
			Root.BeginAnimation(UIElement.OpacityProperty, fade);
		}
		catch
		{
			if (gen == _animGen)
			{
				Visibility = Visibility.Visible;
				SlideTransform.X = 0;
				Root.Opacity = 1;
			}
		}
	}

	/// <summary>滑出 + 淡出，收完再 Collapsed。</summary>
	private void CloseAnimated()
	{
		if (Visibility != Visibility.Visible)
		{
			return;
		}
		int gen = ++_animGen;
		try
		{
			Root.BeginAnimation(UIElement.OpacityProperty, null);
			SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
			var slide = new DoubleAnimation(0, 44, new Duration(TimeSpan.FromMilliseconds(160)))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
			};
			var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(160)));
			slide.Completed += (_, _) =>
			{
				if (gen != _animGen) return;
				Root.BeginAnimation(UIElement.OpacityProperty, null);
				SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
				Root.Opacity = 1;
				SlideTransform.X = 0;
				Visibility = Visibility.Collapsed;
			};
			SlideTransform.BeginAnimation(TranslateTransform.XProperty, slide);
			Root.BeginAnimation(UIElement.OpacityProperty, fade);
		}
		catch
		{
			if (gen == _animGen)
			{
				Visibility = Visibility.Collapsed;
			}
		}
	}

	private void OnLanguageChanged(object? sender, EventArgs e)
	{
		try
		{
			RefreshCommentHeader();
		}
		catch { }
	}

	private void RefreshCommentHeader()
	{
		if (CommentHeaderText == null)
		{
			return;
		}
		CommentHeaderText.Text = _commentTarget == null
			? Loc.Default["cm_title"]
			: Loc.Default["cm_title"] + "  ·  " + Title(_commentTarget);
		if (CommentMoreHint.Visibility == Visibility.Visible && !_commentMoreDone)
		{
			CommentMoreHint.Text = Loc.Default["cm_more_loading"];
		}
	}

	/// <summary>是不是同一首。列表里的行是 copy 过的对象实例，
	/// 直接 ReferenceEquals 永远为 false，所以按 key 比。</summary>
	private static bool SameTrack(object? a, object? b)
	{
		if (a == null || b == null) return false;
		if (a.GetType() != b.GetType()) return false;
		return string.Equals(Title(a), Title(b), StringComparison.Ordinal)
			&& string.Equals(ArtistOf(a), ArtistOf(b), StringComparison.Ordinal);
	}

	/// <summary>打开（或换歌重开）评论列表。</summary>
	private void OpenCommentsCore(object item)
	{
		_commentTarget = item;
		CommentHeaderText.Text = Loc.Default["cm_title"] + "  ·  " + Title(item);
		CommentPlaceholderText.Visibility = Visibility.Collapsed;
		SetCommentStatus(Loc.Default["cm_load"]);
		CommentMoreHint.Visibility = Visibility.Collapsed;
		CommentList.Children.Clear();
		_commentToken++;
		_commentCursor = "";
		_commentMoreDone = false;
		// 不播整栏入场动画：面板整体的滑入由 ShowAnimated 负责，
		// 列表本体直接摆出来，两套动画叠着抢会像页面在闪。
		ShowCommentList();
		RefreshComposeState();
		_ = LoadCommentsAsync(item, _commentToken);
	}

	/// <summary>下滑接近底部才取下一页。游标为空说明已经翻到底，不再发请求。
	/// 原来的阈值只有 24px，而且只看 VerticalChange&gt;0：手指一滑就把整页内容
	/// 顶上来，用户还没看就又追加一页，看起来像评论自己涌出来。
	/// 现在要求「确实已经在底部附近」才触发，并且加载期间只认一次。</summary>
	private async void CommentScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		if (e.VerticalChange <= 0 || _commentMoreDone || _commentTarget == null)
		{
			return;
		}
		// 内容不足一屏时 ScrollableHeight 为 0，滚不到底，也就没有「更多」
		if (CommentScroll.ScrollableHeight <= 0.0)
		{
			return;
		}
		if (e.VerticalOffset < e.ViewportHeight - 160)
		{
			return;
		}
		_commentMoreDone = true;
		CommentMoreHint.Text = Loc.Default["cm_more_loading"];
		CommentMoreHint.Visibility = Visibility.Visible;
		object target = _commentTarget;
		int token = _commentToken;
		try
		{
			OnlineComments.Page? page = await OnlineComments.FetchPageAsync(CommentSource(target), target, _commentCursor);
			if (page == null || token != _commentToken || !ReferenceEquals(_commentTarget, target))
			{
				return;
			}
			if (page.Comments.Count > 0)
			{
				AppendComments(page.Comments, stagger: true);
			}
			_commentCursor = page.Next;
			_commentMoreDone = string.IsNullOrEmpty(page.Next);
			CommentMoreHint.Text = _commentMoreDone ? Loc.Default["cm_no_more"] : Loc.Default["cm_more"];
			if (_commentMoreDone)
			{
				// 到底了就别一直挂着提示
				await Task.Delay(1800);
				if (token == _commentToken)
				{
					CommentMoreHint.Visibility = Visibility.Collapsed;
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "加载更多评论失败");
			_commentMoreDone = false;
			CommentMoreHint.Visibility = Visibility.Collapsed;
		}
	}

	/// <summary>状态文字和「加载更多」提示共用底部那一行，
	/// 这里统一收口，保证两者互斥，不会叠成两行糊字。</summary>
	private void SetCommentStatus(string text)
	{
		CommentStatusText.Text = text;
		if (!string.IsNullOrEmpty(text))
		{
			CommentMoreHint.Visibility = Visibility.Collapsed;
		}
	}

	/// <summary>把评论列表直接摆出来，不做整体入场动画（旧版曾有 PopInCommentList，
	/// 常驻栏时代被用户认定为「多余的整体动画」去掉；抽屉的滑入由外层负责）。</summary>
	private void ShowCommentList()
	{
		try
		{
			// 先摘动画再改状态，顺序反了会让新内容继承旧动画的起始值。
			CommentBodyHost.BeginAnimation(UIElement.OpacityProperty, null);
			if (CommentBodyHost.RenderTransform is TranslateTransform hostTrans)
			{
				hostTrans.BeginAnimation(TranslateTransform.XProperty, null);
				hostTrans.BeginAnimation(TranslateTransform.YProperty, null);
				hostTrans.X = 0;
				hostTrans.Y = 0;
			}
			CommentBodyHost.Opacity = 1;
			CommentBodyHost.Visibility = Visibility.Visible;
			CommentPlaceholderText.Visibility = Visibility.Collapsed;
		}
		catch
		{
			// 极端情况下直接硬显示，别让评论区卡在看不见的状态。
			CommentBodyHost.Visibility = Visibility.Visible;
			CommentBodyHost.Opacity = 1;
		}
	}

	/// <summary>收起评论列表本体，面板留着（空态）。</summary>
	private void CollapseCommentList()
	{
		try
		{
			// 把挂在 Opacity/Transform 上的动画摘干净，
			// 否则下一次打开时内容会从半透明/偏移状态接着播。
			CommentComposeBar.BeginAnimation(UIElement.OpacityProperty, null);
			CommentComposeBar.Opacity = 1;
			CommentBodyHost.BeginAnimation(UIElement.OpacityProperty, null);
			if (CommentBodyHost.RenderTransform is TranslateTransform trans)
			{
				trans.BeginAnimation(TranslateTransform.XProperty, null);
				trans.BeginAnimation(TranslateTransform.YProperty, null);
				trans.X = 0;
				trans.Y = 0;
			}
			CommentBodyHost.Opacity = 1;
			CommentBodyHost.Visibility = Visibility.Collapsed;
		}
		catch
		{
			CommentBodyHost.Visibility = Visibility.Collapsed;
		}
	}

	private async Task LoadCommentsAsync(object item, int token)
	{
		// 必须走 FetchPageAsync：这里以前用的是 FetchAsync，
		// 返回的只是第一页、没有把游标存进 _commentCursor。
		// 于是用户往下滑一下，ScrollChanged 就拿空游标又去拉「第一页」，
		// 一页页地往上叠 —— 表现就是评论还没滑到底就全涌出来了。
		OnlineComments.Page? page = await OnlineComments.FetchPageAsync(CommentSource(item), item, "");
		// 连续点两首歌时，先发的请求可能后回来，会把后点的歌的评论盖掉。
		// 令牌对不上就丢弃这份结果。
		if (token != _commentToken)
		{
			return;
		}
		CommentList.Children.Clear();
		if (page == null)
		{
			SetCommentStatus(Loc.Default["cm_fail"]);
			_commentMoreDone = true;
			return;
		}
		// 游标在这一步就存好，触底时才能接着往下翻而不是重新拉第一页
		_commentCursor = page.Next;
		_commentMoreDone = string.IsNullOrEmpty(page.Next);
		// 首屏就已经够长、根本滑不到底时，标记成到底，避免用户空滚一次
		if (page.Comments.Count == 0)
		{
			SetCommentStatus(Loc.Default["cm_none"]);
			CommentMoreHint.Visibility = Visibility.Collapsed;
			return;
		}
		// 不再显示评论条数
		SetCommentStatus("");
		CommentMoreHint.Text = _commentMoreDone ? Loc.Default["cm_no_more"] : Loc.Default["cm_more"];
		CommentMoreHint.Visibility = _commentMoreDone ? Visibility.Collapsed : Visibility.Visible;
		AppendComments(page.Comments, stagger: true);
		await Task.Yield();
		// 显式回到顶部。
		// Children.Clear() 理论上会把 Extent 清零、偏移跟着夹回 0，
		// 但 ScrollViewer 的偏移是异步生效的：上一首滑到一半的位置
		// 有时会残留到新一批评论渲染完，于是打开评论第一眼看到的是中间某条。
		// 这里在渲染完再压一次 0，结果是确定的。
		CommentScroll.ScrollToVerticalOffset(0);
		// 首屏渲染完再判断：内容一屏装得下就直接算到底，
		// 否则用户会看到「加载更多」提示但怎么滚都触发不了。
		if (!_commentMoreDone && CommentScroll.ScrollableHeight <= 0.0)
		{
			_commentMoreDone = true;
			CommentMoreHint.Visibility = Visibility.Collapsed;
		}
	}

	/// <summary>把评论渲染成条目追加到列表。首屏和「加载更多」共用。
	/// 每条自己淡入上浮，错峰步长很小（8ms，最多 240ms 封顶），
	/// 开头几条看着像依次滚进来，后面的基本同时到位，不至于拖累滚动。</summary>
	private void AppendComments(List<OnlineComment> comments, bool stagger = false)
	{
		int index = 0;
		foreach (OnlineComment c in comments)
		{
			FrameworkElement row = BuildCommentRow(c, 0);
			CommentList.Children.Add(row);
			if (!stagger)
			{
				continue;
			}
			// 步长 8ms、封顶 240ms：第 30 条之后就全部同时淡入，
			// 再往后堆延迟只会让末尾迟迟不出现。
			int delay = Math.Min(index * 8, 240);
			FadeInCommentRow(row, TimeSpan.FromMilliseconds(delay));
			index++;
		}
	}

	/// <summary>单条评论的淡入 + 轻微上浮。RenderTransformOrigin 设成底边中心，
	/// 让「上浮」看起来是从这条评论自己的底部升起，而不是整块内容往上飘。</summary>
	private static void FadeInCommentRow(FrameworkElement row, TimeSpan delay)
	{
		try
		{
			row.RenderTransformOrigin = new Point(0.5, 1.0);
			var t = new TranslateTransform();
			row.RenderTransform = t;
			row.Opacity = 0;
			t.BeginAnimation(TranslateTransform.YProperty, null);
			row.BeginAnimation(UIElement.OpacityProperty, null);

			t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, new Duration(TimeSpan.FromMilliseconds(240)))
			{
				BeginTime = delay,
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
			});
			row.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(190)))
			{
				BeginTime = delay
			});
		}
		catch
		{
			// 动画起不来时至少保证内容是可见的，不能留在 Opacity=0。
			row.Opacity = 1;
		}
	}

	/// <summary>构建一条评论。depth&gt;0 表示这是别人下面的子回复，
	/// 会缩进并加一条竖线，这样用户能一眼看出层级 ——
	/// 之前所有评论都是同一层平铺，看起来就像「一半评论都在回复某条评论」。</summary>
	private FrameworkElement BuildCommentRow(OnlineComment c, int depth)
	{
		bool nested = depth > 0;
		var stack = new StackPanel
		{
			Margin = new Thickness(nested ? 14 : 0, 0, 0, nested ? 8 : 12)
		};

		// 头部：昵称 · 时间 · 回复对象 · 点赞
		var head = new StackPanel { Orientation = Orientation.Horizontal };
		head.Children.Add(new TextBlock
		{
			Text = string.IsNullOrEmpty(c.Time) ? c.User : c.User + "  ·  " + c.Time,
			FontSize = nested ? 11.5 : 12.5,
			FontWeight = FontWeights.SemiBold,
			Foreground = ThemeManager.Accent(),
			TextTrimming = TextTrimming.CharacterEllipsis,
			MaxWidth = 150
		});
		if (c.IsReply && !string.IsNullOrEmpty(c.ReplyTo))
		{
			// 标出「回复 @某某」，原来完全没这个信息
			head.Children.Add(new TextBlock
			{
				Text = "  回复 " + c.ReplyTo,
				FontSize = 11,
				Foreground = ThemeManager.Secondary(),
				TextTrimming = TextTrimming.CharacterEllipsis,
				MaxWidth = 110
			});
		}
		if (c.Likes > 0)
		{
			// 点赞数。原来完全没有这个字段，评论看不出热度。
			head.Children.Add(new TextBlock
			{
				Text = "  ♥ " + FormatCount(c.Likes),
				FontSize = 11,
				Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),
				VerticalAlignment = VerticalAlignment.Center
			});
		}
		stack.Children.Add(head);

		var body = new TextBlock
		{
			Text = c.Content,
			FontSize = 13,
			// 原来写死 0xE0E0E0 的浅灰，是照深色主题配的；
			// 浅色主题下就是白底浅灰字，看着就「糊」。
			Foreground = ThemeManager.Primary(),
			TextWrapping = TextWrapping.Wrap,
			LineHeight = 19,
			Margin = new Thickness(0, 4, 0, 0)
		};
		// 小字号正文走默认的 Ideal 平滑会发虚，Display 关掉字距微调后笔画更实
		TextOptions.SetTextFormattingMode(body, TextFormattingMode.Display);
		stack.Children.Add(body);

		// 接口内嵌带的子回复。缩进 + 左侧一条竖线标出层级。
		if (c.Replies.Count > 0)
		{
			var subBox = new StackPanel();
			foreach (OnlineComment sub in c.Replies)
			{
				subBox.Children.Add(BuildCommentRow(sub, depth + 1));
			}
			stack.Children.Add(new Border
			{
				Margin = new Thickness(8, 6, 0, 0),
				BorderBrush = ThemeManager.Secondary(),
				BorderThickness = new Thickness(2, 0, 0, 0),
				Padding = new Thickness(8, 0, 0, 0),
				Opacity = 0.92,
				Child = subBox
			});
		}
		else if (c.ReplyCount > 0 && !nested)
		{
			// 只知道有 N 条回复但接口这次没带回来 —— 做成可点的，
			// 点一下就地展开子回复，不另开面板、不重载整页。
			AddInlineReplies(c, stack, depth);
		}
		return stack;
	}

	/// <summary>「展开 N 条回复」那一行 + 展开后的容器。
	/// 展开状态存在这里，所以重复点不会重复发请求。</summary>
	private void AddInlineReplies(OnlineComment c, StackPanel parent, int depth)
	{
		// 曲目对象在建行那一刻先抓住。用户可能在请求飞着的时候点了另一首，
		// 那时 _commentTarget 已经换成别的了 —— 拿它去查回复就是查错歌的。
		object? target = _commentTarget;
		string source = target != null ? CommentSource(target) : "";
		var host = new StackPanel { Margin = new Thickness(8, 4, 0, 0) };
		var toggle = MakeReplyToggle(c.ReplyCount, collapsed: true);
		var subBox = new StackPanel { Visibility = Visibility.Collapsed };
		bool loaded = false;
		// 请求飞着的时候用户还能再点一下「展开」。
		// 只用 loaded 挡不住：第一次点把 loaded 置 true 是在回调里，
		// 两次快速点击都能走到发请求那一步，于是同一首评论发两遍请求、
		// 往同一个 subBox 里塞两份子回复。
		bool loading = false;

		toggle.Click += (_, _) =>
		{
			bool expand = subBox.Visibility != Visibility.Visible;
			if (!expand)
			{
				// 再点一次收起。已经加载过的内容留着，不重新请求。
				subBox.Visibility = Visibility.Collapsed;
				toggle.Content = MakeReplyText(c.ReplyCount, collapsed: true);
				return;
			}
			subBox.Visibility = Visibility.Visible;
			toggle.Content = MakeReplyText(c.ReplyCount, collapsed: false);
			if (loaded || loading || target == null || source.Length == 0)
			{
				return;
			}
			loading = true;
			toggle.Foreground = ThemeManager.Tertiary();
			toggle.Content = Loc.Default["cm_reply_loading"];
			_ = LoadInlineRepliesAsync(
				target, source, c, subBox, toggle, host,
				done: () => loading = false,
				markLoaded: () => loaded = true);
		};

		host.Children.Add(toggle);
		host.Children.Add(subBox);
		parent.Children.Add(host);
	}

	private async Task LoadInlineRepliesAsync(object target, string source, OnlineComment root,
		StackPanel box, Button btn, StackPanel host, Action done, Action markLoaded)
	{
		try
		{
			var replies = await OnlineComments.FetchSubRepliesAsync(source, target, root.Id);
			// 请求在飞的时候用户可能已经切到别的歌/收起面板了。
			// 这时把这一段从视觉树上摘掉，别往已经不显示的容器里塞控件。
			if (host.Parent == null)
			{
				return;
			}
			AppendInlineReplies(replies, root, box, btn);
			markLoaded();
		}
		catch
		{
			if (host.Parent != null)
			{
				btn.Foreground = ThemeManager.Tertiary();
				btn.Content = MakeReplyText(root.ReplyCount, collapsed: false);
			}
			// 这里不置 loaded：网络抖了一下之后还应该能再点一次重试。
		}
		finally
		{
			// 无论成功失败都要放闸，否则一次网络异常之后这个开关就永远卡在 true，
			// 再点「展开」就再也发不出请求了。
			done();
		}
	}

	private void AppendInlineReplies(List<OnlineComment> replies, OnlineComment root, StackPanel box, Button btn)
	{
		btn.Foreground = ThemeManager.Secondary();
		btn.Content = MakeReplyText(root.ReplyCount, collapsed: false);
		if (replies.Count == 0)
		{
			box.Children.Add(new TextBlock
			{
				Text = Loc.Default["cm_reply_none"],
				FontSize = 11.5,
				Foreground = ThemeManager.Tertiary(),
				Margin = new Thickness(8, 4, 0, 0)
			});
			return;
		}
		var line = new StackPanel();
		foreach (OnlineComment sub in replies)
		{
			line.Children.Add(BuildCommentRow(sub, 1));
		}
		box.Children.Add(new Border
		{
			Margin = new Thickness(0, 4, 0, 0),
			BorderBrush = ThemeManager.Secondary(),
			BorderThickness = new Thickness(2, 0, 0, 0),
			Padding = new Thickness(8, 0, 0, 0),
			Opacity = 0.92,
			Child = line
		});
	}

	private Button MakeReplyToggle(int count, bool collapsed)
	{
		var btn = new Button
		{
			Content = MakeReplyText(count, collapsed),
			FontSize = 11,
			Foreground = ThemeManager.Secondary(),
			Background = Brushes.Transparent,
			BorderThickness = new Thickness(0),
			Padding = new Thickness(0),
			HorizontalAlignment = HorizontalAlignment.Left,
			Cursor = System.Windows.Input.Cursors.Hand
		};
		return btn;
	}

	private static string MakeReplyText(int count, bool collapsed)
	{
		return collapsed
			? Loc.Default.Format("cm_reply_more", count)
			: Loc.Default["cm_reply_hide"];
	}

	/// <summary>1234 -&gt; 1234，12345 -&gt; 1.2万，和平台习惯一致。</summary>
	private static string FormatCount(long n)
	{
		if (n < 10000)
		{
			return n.ToString();
		}
		return (n / 10000.0).ToString("0.#") + "万";
	}

	// ---------- 发表评论 ----------

	/// <summary>只有「Cookie 有效 + 平台支持发表 + 接口可达 + 已拿到 csrf」时才放开输入框。
	/// 条件不满足一律 disabled，避免用户写完一堆字点发送才发现发不了。</summary>
	private void RefreshComposeState()
	{
		try
		{
			object? target = _commentTarget;
			if (target == null)
			{
				CommentComposeBar.Visibility = Visibility.Collapsed;
				return;
			}
			string source = CommentSource(target);
			bool supported = source == "bili" || source == "netease";
			CommentComposeBar.Visibility = Visibility.Visible;
			if (!supported)
			{
				CommentInputBox.IsEnabled = false;
				CommentSendButton.IsEnabled = false;
				CommentLoginHint.Text = GetCommentLoginHint(source);
				CommentLoginHint.Visibility = Visibility.Visible;
				CommentInputBox.ToolTip = GetCommentLoginHint(source);
				return;
			}
			CommentLoginHint.Text = OnlineComments.CanPost(source) ? Loc.Default["cm_login_ok"] : GetCommentLoginHint(source);
			CommentInputBox.IsEnabled = OnlineComments.CanPost(source);
			CommentInputBox.ToolTip = CommentInputBox.IsEnabled ? null : GetCommentLoginHint(source);
			UpdateSendEnabled();
		}
		catch
		{
			CommentInputBox.IsEnabled = false;
			CommentSendButton.IsEnabled = false;
		}
	}

	private void UpdateSendEnabled()
	{
		CommentSendButton.IsEnabled = CommentInputBox.IsEnabled
			&& !string.IsNullOrWhiteSpace(CommentInputBox.Text);
	}

	/// <summary>登录提示按平台分流。原来所有平台都用同一句
	/// 「评论需要登录」，用户看到 B 站歌下面提示「Cookie 已失效」根本对不上；
	/// 酷狗/QQ 根本不支持发表，也不该复用「登录即可发」的措辞。</summary>
	private static string GetCommentLoginHint(string source)
	{
		return source switch
		{
			"netease" => Loc.Default["cm_netease_login"],
			"bili" => Loc.Default["cm_bili_login"],
			"kugou" => Loc.Default["cm_kugou_note"],
			_ => Loc.Default["cm_hint_login"]
		};
	}

	private void CommentInputBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		UpdateSendEnabled();
	}

	private void CommentInputBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
		{
			e.Handled = true;
			CommentSendButton_Click(sender, new RoutedEventArgs());
		}
	}

	private bool _sending;

	private async void CommentSendButton_Click(object sender, RoutedEventArgs e)
	{
		// IsEnabled 的置位要等布局走完才真正挡住点击；Enter 连发可能在
		// 同一次输入循环里再次进来，所以再加一个纯逻辑的重入标记。
		if (_sending)
		{
			return;
		}
		object? target = _commentTarget;
		string text = CommentInputBox.Text.Trim();
		if (target == null || text.Length == 0 || !CommentInputBox.IsEnabled)
		{
			return;
		}
		_sending = true;
		CommentInputBox.IsEnabled = false;
		CommentSendButton.IsEnabled = false;
		SetCommentStatus(Loc.Default["cm_posting"]);
		try
		{
			string err = await OnlineComments.PostAsync(CommentSource(target), target, text);
			if (err.Length > 0)
			{
				SetCommentStatus(Loc.Default["cm_post_fail"] + "  " + err);
			}
			else
			{
				SetCommentStatus(Loc.Default["cm_posted"]);
				CommentInputBox.Text = "";
				// 重新拉一遍，让新评论出现在列表里
				int token = ++_commentToken;
				await LoadCommentsAsync(target, token);
			}
		}
		catch (Exception ex)
		{
			SetCommentStatus(Loc.Default["cm_post_fail"]);
			LogHelper.LogError(ex, "发表评论失败");
		}
		finally
		{
			_sending = false;
			RefreshComposeState();
		}
	}
}
