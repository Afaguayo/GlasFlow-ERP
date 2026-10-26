using System.Windows.Input;
using GlasFlow.Desktop.Infrastructure;
using GlasFlow.Desktop.Services;

namespace GlasFlow.Desktop.ViewModels;

/// <summary>One bar of the sales chart. Height is only a drawing scale; the amounts come from the server.</summary>
public sealed record BarItem(string Label, decimal Total, double BarHeight, string Tooltip);

public sealed class DashboardViewModel(ApiClient api) : ViewModelBase, IScreen
{
    private const double MaxBarHeight = 150;
    private KpiDto? _kpis;
    private DateTime? _generatedAt;

    public string Title => "Tablero";
    public KpiDto? Kpis { get => _kpis; private set => SetProperty(ref _kpis, value); }
    public DateTime? GeneratedAt { get => _generatedAt; private set => SetProperty(ref _generatedAt, value); }
    public List<BarItem> Bars { get; private set => SetProperty(ref field, value); } = [];
    public IReadOnlyList<StockDto> LowStock { get; private set => SetProperty(ref field, value); } = [];
    public IReadOnlyList<SalesOrderSummaryDto> RecentOrders { get; private set => SetProperty(ref field, value); } = [];

    public ICommand RefreshCommand => field ??= new AsyncRelayCommand(LoadAsync, () => NotBusy);

    public Task LoadAsync() => RunAsync(async () =>
    {
        var d = await api.GetDashboardAsync();
        Kpis = d.Kpis;
        GeneratedAt = d.GeneratedAt;
        var max = d.SalesByMonth.Count == 0 ? 0 : d.SalesByMonth.Max(m => m.Total);
        Bars = d.SalesByMonth.Select(m => new BarItem(m.Label, m.Total,
            max > 0 ? Math.Max(2, (double)(m.Total / max) * MaxBarHeight) : 2,
            $"{m.Label}: {m.Total:C2} ({m.Orders} pedidos)")).ToList();
        LowStock = d.LowStock;
        RecentOrders = d.RecentOrders;
    });
}
