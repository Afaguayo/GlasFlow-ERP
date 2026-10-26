using System.Collections.ObjectModel;
using System.Windows.Input;
using GlasFlow.Desktop.Infrastructure;
using GlasFlow.Desktop.Services;

namespace GlasFlow.Desktop.ViewModels;

public sealed class InventoryViewModel : PagedScreen
{
    private const string AllCategories = "Todas las categorías";
    private readonly ApiClient _api;
    private string _search = "";
    private string _category = AllCategories;
    private WarehouseDto _warehouse = new(0, "", "Todos los almacenes", "", "", true);
    private bool _lowStockOnly;
    private StockDto? _selected;
    private bool _filtersLoaded, _suspend;

    public InventoryViewModel(ApiClient api)
    {
        _api = api;
        Categories = [AllCategories];
        Warehouses = [_warehouse];
        AdjustCommand = new AsyncRelayCommand(AdjustAsync, () => NotBusy && Selected is not null);
    }

    public override string Title => "Inventario";
    public ObservableCollection<StockDto> Items { get; } = [];
    public ObservableCollection<string> Categories { get; }
    public ObservableCollection<WarehouseDto> Warehouses { get; }
    public ICommand AdjustCommand { get; }

    public string Search { get => _search; set => SetProperty(ref _search, value); }
    public StockDto? Selected { get => _selected; set => SetProperty(ref _selected, value); }

    public string Category
    {
        get => _category;
        set { if (SetProperty(ref _category, value)) FilterChanged(); }
    }

    public WarehouseDto Warehouse
    {
        get => _warehouse;
        set { if (value is not null && SetProperty(ref _warehouse, value)) FilterChanged(); }
    }

    public bool LowStockOnly
    {
        get => _lowStockOnly;
        set { if (SetProperty(ref _lowStockOnly, value)) FilterChanged(); }
    }

    private void FilterChanged()
    {
        if (_suspend) return;
        Page = 1;
        _ = LoadAsync();
    }

    protected override async Task FetchAsync()
    {
        if (!_filtersLoaded)
        {
            var cats = await _api.GetCategoriesAsync();
            var whs = await _api.GetWarehousesAsync();
            _suspend = true;
            try
            {
                foreach (var c in cats) Categories.Add(c);
                foreach (var w in whs) Warehouses.Add(w);
            }
            finally { _suspend = false; }
            _filtersLoaded = true;
        }

        var result = await _api.GetInventoryAsync(Search,
            Category == AllCategories ? null : Category,
            Warehouse.Id == 0 ? null : Warehouse.Id,
            LowStockOnly, Page, 50);

        var selectedId = Selected?.InventoryId;
        Items.Clear();
        foreach (var s in result.Items) Items.Add(s);
        Selected = Items.FirstOrDefault(i => i.InventoryId == selectedId);
        ApplyPaging(result);
    }

    private async Task AdjustAsync()
    {
        if (Selected is null) return;
        var vm = new StockAdjustDialogViewModel(_api, Selected);
        if (Dialogs.Show(vm)) await LoadAsync();
    }
}

public sealed class StockAdjustDialogViewModel : DialogViewModel
{
    private readonly ApiClient _api;
    private readonly StockDto _stock;
    private decimal _quantity;
    private string _reason = "";

    public StockAdjustDialogViewModel(ApiClient api, StockDto stock)
    {
        _api = api;
        _stock = stock;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => NotBusy);
        CancelCommand = new RelayCommand(() => Close(false));
    }

    public override string DialogTitle => "Ajuste de inventario";
    public string ProductText => $"{_stock.ProductCode} - {_stock.ProductName}";
    public string WarehouseText => $"{_stock.WarehouseCode} - {_stock.WarehouseName}";
    public string CurrentText => $"{_stock.QuantityAvailable:N2} {_stock.Unit} disponibles, {_stock.QuantityReserved:N2} reservadas";
    public string Unit => _stock.Unit;

    /// <summary>Signed delta: positive adds stock, negative removes it. The server validates the result.</summary>
    public decimal Quantity { get => _quantity; set => SetProperty(ref _quantity, value); }
    public string Reason { get => _reason; set => SetProperty(ref _reason, value); }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private async Task SaveAsync()
    {
        if (await RunAsync(() => _api.AdjustStockAsync(new StockAdjustmentRequest(_stock.ProductId, _stock.WarehouseId, Quantity, Reason.Trim()))))
            Close(true);
    }
}
