namespace MusicBox;

/// <summary>内容页面通用契约：切到该页时由主窗调用，用于刷新页面数据。</summary>
public interface IMusicPage
{
	void OnNavigatedTo();
}

/// <summary>切离该页时调用。
/// 单独开一个接口而不是加进 IMusicPage，是为了不用改所有页面的实现：
/// 需要这个时机的只有音乐库（要按「保留关键词」的勾选决定清不清筛选词）。</summary>
public interface IMusicPageLeave
{
	void OnNavigatedFrom();
}
