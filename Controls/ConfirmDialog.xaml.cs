using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using MusicBox.Services;

namespace MusicBox.Controls;

/// <summary>确认框的三种结局。用枚举而不是 bool，是为了把「确认并且以后别再问」
/// 和「只是这一次确认」区分开 —— 调用方要按这个区别决定要不要落那个标志。</summary>
public enum ConfirmResult
{
	Cancel,
	Confirm,
	ConfirmAndDontAsk,
}

/// <summary>确认框。为什么不用 MessageBox：
/// MessageBox 的按钮文字、图标全是系统语言，混在一堆中文界面里很突兀；
/// 更要紧的是它加不了「不再提醒」这个勾——而删文件这种事，
/// 用户第一次确认之后就不该每首再点一次了。</summary>
public partial class ConfirmDialog : Window
{
	private ConfirmResult _result = ConfirmResult.Cancel;

	public ConfirmDialog()
	{
		InitializeComponent();
		// 无边框窗口没有标题栏，用户会本能地去拖它。整块面板都当标题栏用。
		RootBorder.MouseLeftButtonDown += (_, args) =>
		{
			if (args.ChangedButton == MouseButton.Left && args.ClickCount == 1)
			{
				try
				{
					DragMove();
				}
				catch (InvalidOperationException)
				{
					// 鼠标已释放时会抛，忽略就行
				}
			}
		};
	}

	/// <summary>文案和勾选框都在这里传，不走 XAML 绑定：这些字符串是运行期
	/// 算出来的（文件名、要删几句收藏），绑不出来。</summary>
	public static ConfirmResult Show(
		Window? owner,
		string message,
		string confirmText,
		string cancelText,
		string dontAskText,
		bool showDontAsk)
	{
		var dialog = new ConfirmDialog { MessageText = { Text = message } };
		dialog.ConfirmButton.Content = confirmText;
		dialog.CancelButton.Content = cancelText;
		dialog.DontAskCheckBox.Content = dontAskText;
		dialog.DontAskCheckBox.Visibility = showDontAsk ? Visibility.Visible : Visibility.Collapsed;
		dialog.ConfirmButton.Focus();
		dialog.Owner = owner;
		if (owner != null && owner.IsVisible)
		{
			dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
		}
		else
		{
			// 没有 owner（或主窗最小化中）时 CenterOwner 会算出一个屏幕外的位置，
			// 弹窗看着像没出现。退回屏幕中心。
			dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
		}
		dialog.ShowDialog();
		return dialog._result;
	}

	private void ConfirmButton_Click(object sender, RoutedEventArgs e)
	{
		_result = DontAskCheckBox.IsChecked == true ? ConfirmResult.ConfirmAndDontAsk : ConfirmResult.Confirm;
		Close();
	}

	private void CancelButton_Click(object sender, RoutedEventArgs e)
	{
		_result = ConfirmResult.Cancel;
		Close();
	}
}