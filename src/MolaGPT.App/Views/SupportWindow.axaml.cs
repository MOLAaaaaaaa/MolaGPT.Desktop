using MolaGPT.Desktop.Services;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

public partial class SupportWindow : MolaContentWindow
{
    private readonly SponsorsService _sponsors;
    private readonly SettingsViewModel _settings;
    private int _fetchVersion;

    public SupportWindow(SponsorsService sponsors, SettingsViewModel settings)
    {
        _sponsors = sponsors;
        _settings = settings;
        InitializeComponent();

        _settings.AfdianSupportPromptShown = true;

        PART_Close.Click += (_, _) => Close();
        PART_Plan6.Click += (_, _) => CommunityLinks.TryOpenBrowser(CommunityLinks.AfdianPlan6Url);
        PART_Plan66.Click += (_, _) => CommunityLinks.TryOpenBrowser(CommunityLinks.AfdianPlan66Url);
        PART_AfdianHome.Click += (_, _) => CommunityLinks.TryOpenBrowser(CommunityLinks.AfdianHomeUrl);
        PART_GitHubStar.Click += (_, _) =>
        {
            _settings.GitHubStarPromptShown = true;
            CommunityLinks.TryOpenBrowser(CommunityLinks.GitHubUrl);
        };
        PART_RefreshSponsors.Click += (_, _) => _ = LoadSponsorsAsync();

        Opened += (_, _) => _ = LoadSponsorsAsync();
    }

    private async Task LoadSponsorsAsync()
    {
        var version = ++_fetchVersion;
        PART_RefreshSponsors.IsEnabled = false;
        if (PART_SponsorList.ItemsSource is null)
        {
            PART_SponsorStatus.Text = "正在加载感谢名单…";
            PART_SponsorStatus.IsVisible = true;
        }

        try
        {
            var snapshot = await _sponsors.FetchAsync();
            if (version != _fetchVersion) return;

            PART_SponsorStats.Text = snapshot.TotalCount > 0
                ? $"已有 {snapshot.TotalCount} 位朋友赞助支持"
                : string.Empty;

            if (snapshot.Items.Count > 0)
            {
                PART_SponsorList.ItemsSource = snapshot.Items;
                PART_SponsorListCard.IsVisible = true;
                PART_SponsorStatus.IsVisible = false;
            }
            else
            {
                PART_SponsorListCard.IsVisible = false;
                PART_SponsorStatus.Text = "暂无赞助记录";
                PART_SponsorStatus.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            if (version != _fetchVersion) return;
            PART_SponsorStatus.Text = $"无法加载感谢名单：{ex.Message}";
            PART_SponsorStatus.IsVisible = true;
        }
        finally
        {
            if (version == _fetchVersion)
                PART_RefreshSponsors.IsEnabled = true;
        }
    }
}
