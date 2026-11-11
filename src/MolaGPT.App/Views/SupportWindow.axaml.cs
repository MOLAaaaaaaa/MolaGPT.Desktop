using Avalonia.Controls;
using MolaGPT.Desktop.Services;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

public partial class SupportWindow : MolaContentWindow
{
    private readonly SponsorsService _sponsors;
    private readonly SettingsViewModel _settings;
    private readonly Func<Window, string?, Task> _openMembership;
    private readonly MembershipService _membership;
    private int _fetchVersion;

    public SupportWindow(SponsorsService sponsors, MembershipService membership, SettingsViewModel settings, Func<Window, string?, Task> openMembership)
    {
        _sponsors = sponsors;
        _settings = settings;
        _membership = membership;
        _openMembership = openMembership;
        InitializeComponent();

        _settings.AfdianSupportPromptShown = true;

        PART_Close.Click += (_, _) => Close();
        PART_AfdianHome.Click += (_, _) => CommunityLinks.TryOpenBrowser(CommunityLinks.AfdianHomeUrl);
        PART_GitHubStar.Click += (_, _) =>
        {
            _settings.GitHubStarPromptShown = true;
            CommunityLinks.TryOpenBrowser(CommunityLinks.GitHubUrl);
        };
        PART_RefreshSponsors.Click += (_, _) => _ = LoadSponsorsAsync();
        PART_RetryProducts.Click += (_, _) => _ = LoadProductsAsync();
        PART_OpenMembership.Click += async (_, _) => await _openMembership(this, null);

        Opened += (_, _) =>
        {
            _ = LoadSponsorsAsync();
            _ = LoadProductsAsync();
        };
    }

    private async Task LoadProductsAsync()
    {
        PART_ProductsActions.IsVisible = false;
        PART_ProductsStatus.IsVisible = true;
        PART_ProductsStatus.Text = "正在加载支持方案…";
        try
        {
            var products = await _membership.GetProductsAsync();
            PART_Products.ItemsSource = products;
            PART_ProductsStatus.Text = "暂无支持方案";
            PART_ProductsStatus.IsVisible = products.Count == 0;
        }
        catch
        {
            PART_ProductsStatus.Text = "无法加载支持方案";
            PART_ProductsActions.IsVisible = true;
        }
    }

    private async void OnProductClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MembershipProduct product } button) return;
        button.IsEnabled = false;
        try { await _openMembership(this, product.Id); }
        finally { button.IsEnabled = true; }
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
                ? $"已有 {snapshot.TotalCount} 位朋友支持项目"
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
                PART_SponsorStatus.Text = "暂无支持记录";
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
